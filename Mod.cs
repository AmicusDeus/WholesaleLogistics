using System.Collections.Generic;
using Colossal;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Simulation;

namespace WholesaleLogistics
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(WholesaleLogistics)}.{nameof(Mod)}").SetShowsErrorsInUI(false);
        public static Setting ActiveSetting;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            ActiveSetting = new Setting(this);
            ActiveSetting.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEn(ActiveSetting));
            AssetDatabase.global.LoadSettings(nameof(WholesaleLogistics), ActiveSetting, new Setting(this));
            // Persist every settings change to disk the moment it is applied (survives a crash / non-clean exit).
            ActiveSetting.onSettingsApplied += OnSettingsApplied;

            // Intercept fresh shop purchase requests BEFORE the vanilla buyer system consumes them.
            updateSystem.UpdateBefore<WholesaleBuyerSystem, ResourceBuyerSystem>(SystemUpdatePhase.GameSimulation);
            // Fulfil queued warehouse orders (v0: direct; next stage: warehouse-owned trucks).
            updateSystem.UpdateAt<WholesaleDispatchSystem>(SystemUpdatePhase.GameSimulation);
            // Let warehouses stock commercial goods (widens storage prefab masks; player still picks per building).
            updateSystem.UpdateAt<WarehouseStockSystem>(SystemUpdatePhase.GameSimulation);
            // Keep platform achievements enabled while the mod is active.
            updateSystem.UpdateAt<AchievementEnablerSystem>(SystemUpdatePhase.GameSimulation);

            log.Info("[SelfTest] WholesaleLogistics loaded (demand-driven distribution).");
        }

        // Persist a settings change to disk as soon as it is applied (guard: ApplyAndSave re-raises onSettingsApplied).
        private static bool s_savingReentrant;
        private static void OnSettingsApplied(Game.Settings.Setting setting)
        {
            if (s_savingReentrant)
                return;
            s_savingReentrant = true;
            try { ActiveSetting?.ApplyAndSave(); }
            finally { s_savingReentrant = false; }
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            if (ActiveSetting != null)
            {
                ActiveSetting.onSettingsApplied -= OnSettingsApplied;
                ActiveSetting.UnregisterInOptionsUI();
                ActiveSetting = null;
            }
        }
    }

    // Minimal English locale (full localization comes after mechanics stabilize, same pipeline as EconomyTweaks).
    public class LocaleEn : IDictionarySource
    {
        private readonly Setting m_S;
        public LocaleEn(Setting setting) { m_S = setting; }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_S.GetSettingsLocaleID(), "Wholesale Logistics" },
                { m_S.GetOptionTabLocaleID(Setting.Section), "Main" },
                { m_S.GetOptionGroupLocaleID(Setting.Group), "Demand-driven distribution" },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.Enabled)), "Enable wholesale distribution" },
                { m_S.GetOptionDescLocaleID(nameof(Setting.Enabled)), "Commercial shops stop sending their own trucks: their restock orders are queued at the nearest city warehouse that stocks the goods, and the warehouse fulfils them. Off = vanilla behaviour." },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.StrictMode)), "Strict mode (no warehouse = no goods)" },
                { m_S.GetOptionDescLocaleID(nameof(Setting.StrictMode)), "When no city warehouse stocks a needed resource: off = the shop falls back to vanilla buying; on = the shop runs a shortage until you provide warehouse capacity." },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.WarehouseImportsOnly)), "Warehouse-only imports" },
                { m_S.GetOptionDescLocaleID(nameof(Setting.WarehouseImportsOnly)), "Reserved for the truck-dispatch stage: shops never import directly; only warehouses import, in bulk." },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.WidenWarehouseStock)), "Warehouses can stock commercial goods" },
                { m_S.GetOptionDescLocaleID(nameof(Setting.WidenWarehouseStock)), "Adds every physical tradable good (food, furniture, electronics, ...) to the resources warehouses and cargo yards may store. You still choose per building what each one stores. Turning this off takes effect after restarting the game." },

                { m_S.GetOptionGroupLocaleID(Setting.GroupGeneral), "General" },
                { m_S.GetOptionLabelLocaleID(nameof(Setting.EnableAchievements)), "Keep achievements enabled" },
                { m_S.GetOptionDescLocaleID(nameof(Setting.EnableAchievements)), "Cities: Skylines II disables achievements whenever any mod is active. This re-enables them. Safe to leave on." },
            };
        }

        public void Unload() { }
    }
}
