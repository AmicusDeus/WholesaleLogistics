using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace WholesaleLogistics
{
    // Stage A1 — intercepts commercial shops' vanilla purchase requests (demand-driven "orders") and queues them
    // on the nearest city warehouse that stocks the resource, instead of letting the shop send its own truck
    // across the map. Runs BEFORE the vanilla ResourceBuyerSystem so fresh requests are consumed first.
    //
    // Rules (user-approved design):
    //   * Only COMMERCIAL companies (ServiceAvailable) are intercepted; industrial input buying stays vanilla.
    //   * Only requests vanilla hasn't started processing yet (no PathInformation) are taken — anything already
    //     in flight completes vanilla-style. Failsafe: disabling the mod instantly restores pure vanilla.
    //   * Warehouse candidates: in-city storage companies (not outside connections) whose prefab can store the
    //     resource AND that currently have stock. Nearest one wins.
    //   * No candidate: vanilla fallback (shop buys the old way) — or, in StrictMode, the request is suppressed
    //     and the shop runs a shortage until warehouse capacity exists ("no building, no service").
    public partial class WholesaleBuyerSystem : GameSystemBase
    {
        private EntityQuery m_ShopQuery;
        private EntityQuery m_WarehouseQuery;
        private ResourceSystem m_ResourceSystem;
        private ComponentLookup<ResourceData> m_ResourceDatas;

        // Daily counters for the [SelfTest] log (reset by WholesaleDispatchSystem when it logs).
        public int InterceptedCount;
        public int FallbackCount;
        public int StrictSuppressedCount;
        // Diagnostics: how many requests we SAW, and how many were skipped as non-commercial / immaterial.
        public int SeenCount;
        public int NonCommercialCount;
        public int ImmaterialCount;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ResourceSystem = World.GetOrCreateSystemManaged<ResourceSystem>();
            m_ResourceDatas = GetComponentLookup<ResourceData>(isReadOnly: true);
            // Commercial-ness is decided per entity below (ServiceAvailable on the company OR ServiceCompanyData
            // on its prefab) — kept OUT of the query so the diagnostics can see every company request.
            m_ShopQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ResourceBuyer>(),
                    ComponentType.ReadOnly<BuyingCompany>(),
                    ComponentType.ReadOnly<PropertyRenter>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<PathInformation>(),    // don't touch requests vanilla already started
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                },
            });
            // NOTE: no PropertyRenter requirement — zoned warehouse companies rent a building, but cargo
            // terminals/yards ARE their own storage company (verified: CargoTransportStation prefab adds
            // StorageCompany directly to the station entity). Position resolution handles both shapes.
            m_WarehouseQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Companies.StorageCompany>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Economy.Resources>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Game.Objects.OutsideConnection>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                },
            });
            RequireForUpdate(m_ShopQuery); // only ticks while a fresh request exists
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1;

        protected override void OnUpdate()
        {
            Setting s = Mod.ActiveSetting;
            if (s == null || !s.Enabled)
                return;

            m_ResourceDatas.Update(this);
            ResourcePrefabs resourcePrefabs = m_ResourceSystem.GetPrefabs();
            NativeArray<Entity> shops = m_ShopQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Entity> warehouses = m_WarehouseQuery.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < shops.Length; i++)
            {
                Entity shop = shops[i];
                SeenCount++;
                ResourceBuyer buyer = EntityManager.GetComponentData<ResourceBuyer>(shop);
                if (buyer.m_ResourceNeeded == Resource.NoResource || buyer.m_AmountNeeded <= 0)
                    continue;
                // Commercial shops only: ServiceAvailable on the company, or ServiceCompanyData on its prefab.
                bool commercial = EntityManager.HasComponent<ServiceAvailable>(shop);
                if (!commercial && EntityManager.HasComponent<PrefabRef>(shop))
                {
                    Entity companyPrefab = EntityManager.GetComponentData<PrefabRef>(shop).m_Prefab;
                    commercial = EntityManager.HasComponent<ServiceCompanyData>(companyPrefab);
                }
                if (!commercial)
                {
                    NonCommercialCount++;
                    continue;
                }
                // Virtual goods never generate trucks in vanilla (its own rule: weight == 0) — leave to vanilla.
                // NOTE: m_IsMaterial means RAW material (grain, ore...), NOT "physical" — finished consumer goods
                // have it false, so it must not be part of this test (that bug blanked all interceptions).
                Entity resPrefab = resourcePrefabs[buyer.m_ResourceNeeded];
                if (resPrefab == Entity.Null || !m_ResourceDatas.HasComponent(resPrefab))
                    continue;
                ResourceData rd = m_ResourceDatas[resPrefab];
                if (rd.m_Weight <= 0f)
                {
                    ImmaterialCount++;
                    continue;
                }

                Entity warehouse = FindNearestStockedWarehouse(warehouses, shop, buyer.m_ResourceNeeded);
                if (warehouse != Entity.Null)
                {
                    // Consume the vanilla request; queue a demand-driven order on the warehouse.
                    EntityManager.RemoveComponent<ResourceBuyer>(shop);
                    if (!EntityManager.HasBuffer<WholesaleOrder>(warehouse))
                        EntityManager.AddBuffer<WholesaleOrder>(warehouse);
                    DynamicBuffer<WholesaleOrder> orders = EntityManager.GetBuffer<WholesaleOrder>(warehouse);
                    // Merge with an existing order from the same shop for the same resource.
                    bool merged = false;
                    for (int j = 0; j < orders.Length; j++)
                    {
                        if (orders[j].m_Shop == shop && orders[j].m_Resource == buyer.m_ResourceNeeded)
                        {
                            WholesaleOrder o = orders[j];
                            o.m_Amount = math.max(o.m_Amount, buyer.m_AmountNeeded);
                            orders[j] = o;
                            merged = true;
                            break;
                        }
                    }
                    if (!merged)
                        orders.Add(new WholesaleOrder { m_Shop = shop, m_Resource = buyer.m_ResourceNeeded, m_Amount = buyer.m_AmountNeeded });
                    InterceptedCount++;
                }
                else if (s.StrictMode)
                {
                    EntityManager.RemoveComponent<ResourceBuyer>(shop); // no warehouse => no goods
                    StrictSuppressedCount++;
                }
                else
                {
                    FallbackCount++; // leave to vanilla (counted once per sighting; indicative, not exact)
                }
            }

            shops.Dispose();
            warehouses.Dispose();
        }

        // Nearest in-city storage company whose prefab allows the resource and that has stock on hand.
        private Entity FindNearestStockedWarehouse(NativeArray<Entity> warehouses, Entity shop, Resource resource)
        {
            float3 shopPos = BuildingPosition(shop);
            Entity best = Entity.Null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < warehouses.Length; i++)
            {
                Entity wh = warehouses[i];
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(wh).m_Prefab;
                if (!EntityManager.HasComponent<StorageCompanyData>(prefab))
                    continue;
                if ((EntityManager.GetComponentData<StorageCompanyData>(prefab).m_StoredResources & resource) == Resource.NoResource)
                    continue;
                DynamicBuffer<Game.Economy.Resources> res = EntityManager.GetBuffer<Game.Economy.Resources>(wh, isReadOnly: true);
                if (EconomyUtils.GetResources(resource, res) <= 0)
                    continue;
                float dist = math.distance(shopPos, BuildingPosition(wh));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = wh;
                }
            }
            return best;
        }

        private float3 BuildingPosition(Entity company)
        {
            // Zoned company: position of the rented building. Cargo station: the entity is the building itself.
            if (EntityManager.HasComponent<PropertyRenter>(company))
            {
                Entity property = EntityManager.GetComponentData<PropertyRenter>(company).m_Property;
                if (property != Entity.Null && EntityManager.HasComponent<Transform>(property))
                    return EntityManager.GetComponentData<Transform>(property).m_Position;
            }
            if (EntityManager.HasComponent<Transform>(company))
                return EntityManager.GetComponentData<Transform>(company).m_Position;
            return default;
        }
    }
}
