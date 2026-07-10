using Game;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;

namespace WholesaleLogistics
{
    // Widens what warehouses / cargo yards CAN stock: every storage-company prefab's m_StoredResources mask gets
    // all PHYSICAL, TRADABLE goods OR-ed in (m_IsMaterial && m_IsTradable && m_Weight > 0 — so commercial goods
    // like food, furniture or electronics become storable, while virtual goods like lodging/entertainment are
    // excluded). The player still chooses per building what each warehouse stores; this only unlocks the options.
    //
    // Idempotent, applied continuously while enabled (prefab data is session-only — reverting the setting takes
    // effect after reloading the game, since masks are rebuilt from assets on load).
    public partial class WarehouseStockSystem : GameSystemBase
    {
        private ResourceSystem m_ResourceSystem;
        private EntityQuery m_StoragePrefabQuery;
        private EntityQuery m_TruckPrefabQuery;
        private ComponentLookup<ResourceData> m_ResourceDatas;
        private bool m_Logged;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ResourceSystem = World.GetOrCreateSystemManaged<ResourceSystem>();
            m_StoragePrefabQuery = GetEntityQuery(ComponentType.ReadWrite<StorageCompanyData>());
            m_TruckPrefabQuery = GetEntityQuery(ComponentType.ReadWrite<DeliveryTruckData>());
            m_ResourceDatas = GetComponentLookup<ResourceData>(isReadOnly: true);
            RequireForUpdate(m_StoragePrefabQuery);
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 64;

        protected override void OnUpdate()
        {
            Setting s = Mod.ActiveSetting;
            if (s == null || !s.WidenWarehouseStock)
                return;

            m_ResourceDatas.Update(this);
            ResourcePrefabs prefabs = m_ResourceSystem.GetPrefabs();

            // Union of all physical, tradable goods.
            Resource goods = Resource.NoResource;
            ResourceIterator it = ResourceIterator.GetIterator();
            while (it.Next())
            {
                Entity resPrefab = prefabs[it.resource];
                if (resPrefab == Entity.Null || !m_ResourceDatas.HasComponent(resPrefab))
                    continue;
                ResourceData rd = m_ResourceDatas[resPrefab];
                // Physical tradable goods: weight > 0 is the game's own physical/virtual distinction.
                // (m_IsMaterial = RAW material only — using it here excluded all finished consumer goods.)
                if (rd.m_IsTradable && rd.m_Weight > 0f)
                    goods |= it.resource;
            }
            if (goods == Resource.NoResource)
                return;

            int widened = 0;
            NativeArray<Entity> storagePrefabs = m_StoragePrefabQuery.ToEntityArray(Allocator.Temp);
            int prefabCount = storagePrefabs.Length;
            for (int i = 0; i < prefabCount; i++)
            {
                StorageCompanyData d = EntityManager.GetComponentData<StorageCompanyData>(storagePrefabs[i]);
                if ((d.m_StoredResources & goods) != goods)
                {
                    d.m_StoredResources |= goods;
                    EntityManager.SetComponentData(storagePrefabs[i], d);
                    widened++;
                }
            }
            storagePrefabs.Dispose();

            // Make every warehouse-stockable good PHYSICALLY DELIVERABLE. Some goods have no delivery-truck prefab
            // whose m_TransportedResources covers them, so a wholesale order for them can't spawn a truck (that's the
            // noTruck count). Add those missing goods to the delivery-truck prefabs, then tag each modified prefab
            // Updated so VehicleCapacitySystem rebuilds its truck-select cache — it ONLY rebuilds on Updated/Deleted
            // or a load, so a bare data write would be silently ignored. Idempotent: once a truck carries `missing`
            // we skip it (so we stop re-tagging), matching the storage-widen behaviour above.
            Resource truckUnion = Resource.NoResource;
            NativeArray<Entity> truckPrefabs = m_TruckPrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < truckPrefabs.Length; i++)
                truckUnion |= EntityManager.GetComponentData<DeliveryTruckData>(truckPrefabs[i]).m_TransportedResources;

            Resource missing = Resource.NoResource;
            ResourceIterator mit = ResourceIterator.GetIterator();
            while (mit.Next())
                if ((goods & mit.resource) != Resource.NoResource && (truckUnion & mit.resource) == Resource.NoResource)
                    missing |= mit.resource;

            int trucksWidened = 0;
            if (missing != Resource.NoResource)
            {
                for (int i = 0; i < truckPrefabs.Length; i++)
                {
                    DeliveryTruckData td = EntityManager.GetComponentData<DeliveryTruckData>(truckPrefabs[i]);
                    if ((td.m_TransportedResources & missing) != missing)
                    {
                        td.m_TransportedResources |= missing;
                        EntityManager.SetComponentData(truckPrefabs[i], td);
                        if (!EntityManager.HasComponent<Updated>(truckPrefabs[i]))
                            EntityManager.AddComponent<Updated>(truckPrefabs[i]);
                        trucksWidened++;
                    }
                }
            }
            int truckCount = truckPrefabs.Length;
            truckPrefabs.Dispose();

            if (!m_Logged)
            {
                m_Logged = true;
                Mod.log.Info($"[SelfTest] warehouseStock: storagePrefabs={prefabCount} widenedThisPass={widened} goodsMask={(ulong)goods:X} | " +
                             $"truckPrefabs={truckCount} trucksWidenedThisPass={trucksWidened} missingMask={(ulong)missing:X}");
            }
        }
    }
}
