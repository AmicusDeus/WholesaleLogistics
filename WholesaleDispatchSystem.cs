using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using System.Collections.Generic;

namespace WholesaleLogistics
{
    // Stage A2 (v1) — fulfils queued WholesaleOrders by dispatching a REAL warehouse-owned delivery truck per
    // order slice. The truck is built with vanilla's OWN factory (DeliveryTruckSelectData.CreateVehicle — the exact
    // call TripNeededSystem uses), owned by the warehouse, Loaded|Delivering, targeting the shop; vanilla's
    // DeliveryTruckAISystem then emerges it from the warehouse, drives it, unloads at the shop, and SETTLES: the
    // shop pays the industrial price and the warehouse (a StorageCompany) is uncredited — vanilla storage-seller
    // economics, byte-identical to the old instant transfer's numbers, now with a visible truck.
    //
    // We own only the spawn. Warehouse stock is decremented HERE at load (that decrement IS the reservation, so
    // two orders can't oversell the same units) rather than via vanilla's UpdateOwnerQuantity flag — a
    // StorageCompany-owned delivery truck is a path vanilla never spawns, so its owner-quantity reconcile is
    // untested for warehouses; we make the stock leave explicitly and let the truck carry it. Money moves once, on
    // delivery, inside vanilla (we charge nothing here) → no double-charge. If the truck never arrives (path fail /
    // shop or warehouse deleted / stuck) vanilla runs CancelTransaction and no money moves; the order was already
    // decremented at spawn so it isn't re-dispatched (small stock loss on cancel is acceptable — see OQ-3).
    public partial class WholesaleDispatchSystem : GameSystemBase
    {
        private SimulationSystem m_Sim;
        private VehicleCapacitySystem m_VehicleCapacity;
        private WholesaleBuyerSystem m_Buyer;
        private EntityQuery m_OrderQuery;
        private ComponentLookup<DeliveryTruckData> m_DeliveryTruckDatas;
        private ComponentLookup<ObjectData> m_ObjectDatas;
        private int m_LastDay = int.MinValue;

        // Daily counters for the [SelfTest] log.
        public int DispatchedTrucks { get; private set; }
        public int UnitsLoaded { get; private set; }
        public int NoTruckCount { get; private set; }

        // Per-resource noTruck breakdown (diagnostic), split by cause, logged + cleared daily.
        private readonly Dictionary<Resource, int> m_NoTruckPrefab = new Dictionary<Resource, int>();   // no truck prefab carries it
        private readonly Dictionary<Resource, int> m_NoTruckLoadZero = new Dictionary<Resource, int>();  // truck found, loaded 0

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Sim = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_VehicleCapacity = World.GetOrCreateSystemManaged<VehicleCapacitySystem>();
            m_Buyer = World.GetOrCreateSystemManaged<WholesaleBuyerSystem>();
            m_DeliveryTruckDatas = GetComponentLookup<DeliveryTruckData>(isReadOnly: true);
            m_ObjectDatas = GetComponentLookup<ObjectData>(isReadOnly: true);
            m_OrderQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<WholesaleOrder>(), ComponentType.ReadOnly<Game.Economy.Resources>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 64;

        protected override void OnUpdate()
        {
            Setting s = Mod.ActiveSetting;
            if (s == null)
                return;

            // Dispatch a few times per day so shops don't starve between order and delivery. 262144 frames/day.
            int slot = (int)(m_Sim.frameIndex / 32768); // 8 slots per in-game day
            if (slot == m_LastDay)
                return;
            bool logDay = m_LastDay != int.MinValue; // log every slot (8x/day) while the pipeline is validated
            m_LastDay = slot;

            if (!s.Enabled || m_OrderQuery.IsEmptyIgnoreFilter)
            {
                if (logDay) LogDaily();
                return;
            }

            m_DeliveryTruckDatas.Update(this);
            m_ObjectDatas.Update(this);
            DeliveryTruckSelectData select = m_VehicleCapacity.GetDeliveryTruckSelectData();
            Random random = new Random(1u + (m_Sim.frameIndex | 1u));

            EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.Temp);
            EntityCommandBuffer.ParallelWriter pw = ecb.AsParallelWriter();

            NativeArray<Entity> warehouses = m_OrderQuery.ToEntityArray(Allocator.Temp);
            for (int w = 0; w < warehouses.Length; w++)
            {
                Entity wh = warehouses[w];

                // Where the truck emerges: the warehouse's rented building (zoned company) or the entity itself
                // (a cargo terminal IS its own building). Mirrors TripNeededSystem's from/transform resolution.
                Entity source = Entity.Null;
                Transform xform = default;
                if (EntityManager.HasComponent<PropertyRenter>(wh))
                {
                    Entity prop = EntityManager.GetComponentData<PropertyRenter>(wh).m_Property;
                    if (prop != Entity.Null && EntityManager.HasComponent<Transform>(prop))
                    {
                        source = prop;
                        xform = EntityManager.GetComponentData<Transform>(prop);
                    }
                }
                if (source == Entity.Null && EntityManager.HasComponent<Transform>(wh))
                {
                    source = wh;
                    xform = EntityManager.GetComponentData<Transform>(wh);
                }
                if (source == Entity.Null)
                    continue; // no position to emerge from — skip this warehouse this slot

                DynamicBuffer<WholesaleOrder> orders = EntityManager.GetBuffer<WholesaleOrder>(wh);
                DynamicBuffer<Game.Economy.Resources> whRes = EntityManager.GetBuffer<Game.Economy.Resources>(wh);

                for (int i = orders.Length - 1; i >= 0; i--)
                {
                    WholesaleOrder order = orders[i];
                    if (order.m_Shop == Entity.Null || !EntityManager.Exists(order.m_Shop) ||
                        !EntityManager.HasBuffer<Game.Economy.Resources>(order.m_Shop))
                    {
                        orders.RemoveAt(i);
                        continue;
                    }

                    int stock = EconomyUtils.GetResources(order.m_Resource, whRes);
                    int move = math.min(stock, order.m_Amount);
                    if (move <= 0)
                        continue; // wait for the warehouse to restock

                    // Spawn a warehouse-owned, loaded, delivering truck via vanilla's own factory. CreateVehicle
                    // picks a truck prefab that can carry the resource and loads up to its cargo capacity. IMPORTANT:
                    // the PUBLIC item/auto-select overload SETS `amount` to the quantity it LOADED (not the residual):
                    // the private per-prefab worker does amount-=loaded, but the item overload's `amount -= amount2`
                    // flips it back to the loaded delta (DeliveryTruckSelectData line ~126). So post-call `amount` ==
                    // units placed on the truck; `move - amount` would be the RESIDUAL, which is the opposite.
                    int amount = move, returnAmount = 0;
                    Entity truck = select.CreateVehicle(
                        pw, 0, ref random,
                        ref m_DeliveryTruckDatas, ref m_ObjectDatas,
                        order.m_Resource, Resource.NoResource,
                        ref amount, ref returnAmount,
                        xform, source,
                        DeliveryTruckFlags.Loaded | DeliveryTruckFlags.Delivering, 0u);
                    if (truck == Entity.Null)
                    {
                        NoTruckCount++; // no truck prefab carries this resource — leave the order, don't touch stock
                        Bump(m_NoTruckPrefab, order.m_Resource);
                        continue;
                    }
                    int loaded = amount; // vanilla set `amount` = units actually loaded on the truck (see note above)
                    if (loaded <= 0)
                    {
                        // Genuine zero-load: the selected truck's DeliveryTruckData can't carry this resource, or has 0
                        // capacity. Rare now — the old `move - amount` miscounted a FULL load (amount==move) as zero,
                        // which threw away every delivery that fit in a single truck (the bulk of orders).
                        NoTruckCount++;
                        Bump(m_NoTruckLoadZero, order.m_Resource);
                        continue;
                    }

                    // Stock leaves the warehouse now (it's on the truck). Settlement (shop pays) happens on delivery
                    // inside DeliveryTruckAISystem — we charge nothing here.
                    EconomyUtils.AddResources(order.m_Resource, -loaded, whRes);

                    // Direct the truck: deliver to the shop, owned by the warehouse (Owner => cost payer at settle).
                    pw.SetComponent(0, truck, new Target(order.m_Shop));
                    pw.AddComponent(0, truck, new Owner(wh));

                    // Cosmetic: record the trade partner for any flow visualization.
                    if (EntityManager.HasComponent<BuyingCompany>(order.m_Shop))
                    {
                        BuyingCompany bc = EntityManager.GetComponentData<BuyingCompany>(order.m_Shop);
                        bc.m_LastTradePartner = wh;
                        EntityManager.SetComponentData(order.m_Shop, bc);
                    }

                    DispatchedTrucks++;
                    UnitsLoaded += loaded;
                    order.m_Amount -= loaded;
                    if (order.m_Amount <= 0)
                        orders.RemoveAt(i);
                    else
                        orders[i] = order;
                }
            }
            warehouses.Dispose();

            ecb.Playback(EntityManager);
            ecb.Dispose();

            if (logDay) LogDaily();
        }

        private void LogDaily()
        {
            int pendingOrders = 0;
            NativeArray<Entity> warehouses = m_OrderQuery.ToEntityArray(Allocator.Temp);
            for (int w = 0; w < warehouses.Length; w++)
                pendingOrders += EntityManager.GetBuffer<WholesaleOrder>(warehouses[w], isReadOnly: true).Length;
            int warehousesWithOrders = warehouses.Length;
            warehouses.Dispose();

            Mod.log.Info($"[SelfTest] wholesale: seen={m_Buyer.SeenCount} nonCommercial={m_Buyer.NonCommercialCount} immaterial={m_Buyer.ImmaterialCount} " +
                         $"intercepted={m_Buyer.InterceptedCount} fallbackSightings={m_Buyer.FallbackCount} " +
                         $"strictSuppressed={m_Buyer.StrictSuppressedCount} dispatchedTrucks={DispatchedTrucks} unitsLoaded={UnitsLoaded} " +
                         $"noTruck={NoTruckCount} pendingOrders={pendingOrders} warehousesWithOrders={warehousesWithOrders}");
            m_Buyer.InterceptedCount = 0;
            m_Buyer.FallbackCount = 0;
            m_Buyer.StrictSuppressedCount = 0;
            m_Buyer.SeenCount = 0;
            m_Buyer.NonCommercialCount = 0;
            m_Buyer.ImmaterialCount = 0;
            DispatchedTrucks = 0;
            UnitsLoaded = 0;
            NoTruckCount = 0;

            // Per-resource diagnostic: which goods couldn't get a truck, and why (no carrier prefab vs loaded nothing).
            if (m_NoTruckPrefab.Count > 0 || m_NoTruckLoadZero.Count > 0)
                Mod.log.Info("[SelfTest] wholesale noTruck by resource — noPrefab: " + FormatByResource(m_NoTruckPrefab)
                             + " | loadedZero: " + FormatByResource(m_NoTruckLoadZero));
            m_NoTruckPrefab.Clear();
            m_NoTruckLoadZero.Clear();
        }

        private static void Bump(Dictionary<Resource, int> d, Resource r)
        {
            d.TryGetValue(r, out int c);
            d[r] = c + 1;
        }

        private static string FormatByResource(Dictionary<Resource, int> d)
        {
            if (d.Count == 0) return "none";
            string result = "";
            foreach (var kv in d)
                result += (result.Length > 0 ? ", " : "") + kv.Key + "=" + kv.Value;
            return result;
        }
    }
}
