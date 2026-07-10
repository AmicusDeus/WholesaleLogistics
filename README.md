# Wholesale Logistics

Turns your warehouses into a real distribution layer: they stock goods and dispatch delivery trucks to the shops that need them, instead of every shop importing on its own. For **Cities: Skylines II**.

**Paradox Mods:** https://mods.paradoxplaza.com/mods/150547

## What it does
- Unlocks every physical, tradable good for storage in your warehouses (you still choose per building what each stocks).
- Supplies commercial shops from that local stock — warehouse-owned delivery trucks physically drive the goods to the shops, using the game's own delivery system.
- No warehouse capacity for a needed good means shops run short until you build it ("no building, no service").

Opt-in per building; off = vanilla pull (shops buy from industry / imports).

## Under the hood (for the curious / security-minded)
- **Pure ECS — no Harmony patches.** It intercepts shop resource-buying, queues delivery orders, and spawns warehouse-owned trucks via the game's own `DeliveryTruckSelectData.CreateVehicle` factory (the same call the base game uses).
- **No network access at all** — nothing leaves your machine.
- **Filesystem:** writes only its own settings file and a log (`WholesaleLogistics.Mod.log`). Nothing else.
- **Dependencies:** none beyond the base game.

Full source is here; the compiled DLL decompiles cleanly if you'd like to verify it matches.

## Build from source
Requires the official CS2 modding toolchain. `dotnet build -c Release` compiles and deploys to your local Mods folder.

## License
[MIT](LICENSE).

---

*Made with [Claude Code](https://claude.com/claude-code), Anthropic's agentic coding tool.*
