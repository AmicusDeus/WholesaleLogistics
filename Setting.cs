using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace WholesaleLogistics
{
    [FileLocation(nameof(WholesaleLogistics))]
    public class Setting : ModSetting
    {
        public const string Section = "Main";
        public const string Group = "Wholesale";
        public const string GroupGeneral = "General";

        public Setting(IMod mod) : base(mod) { }

        // NOTE: initializers double as the settings-migration failsafe (missing keys in an old .coc keep these
        // values instead of defaulting to 0/false).

        // Master switch. ON = commercial shops are supplied from city warehouses (push); OFF = pure vanilla pull.
        [SettingsUISection(Section, Group)]
        public bool Enabled { get; set; } = true;

        // OFF (default): when no city warehouse stocks a needed resource, the shop falls back to vanilla buying
        // (drives to industry / imports). ON: no warehouse = no goods — shops run shortages until you build
        // warehouse capacity ("no building, no service").
        [SettingsUISection(Section, Group)]
        public bool StrictMode { get; set; } = false;

        // Reserved for the dispatch stage: shops never import directly; only warehouses import (in bulk).
        [SettingsUISection(Section, Group)]
        public bool WarehouseImportsOnly { get; set; } = true;

        // Lets warehouses/cargo yards stock ALL physical tradable goods (incl. commercial goods like food,
        // furniture, electronics) — the player still picks per building. Revert takes effect after game restart.
        [SettingsUISection(Section, Group)]
        public bool WidenWarehouseStock { get; set; } = true;

        // Keep platform achievements enabled while this mod is active (the game otherwise disables them for any mod).
        [SettingsUISection(Section, GroupGeneral)]
        public bool EnableAchievements { get; set; } = true;

        public override void SetDefaults()
        {
            Enabled = true;
            StrictMode = false;
            WarehouseImportsOnly = true;
            WidenWarehouseStock = true;
            EnableAchievements = true;
        }
    }
}
