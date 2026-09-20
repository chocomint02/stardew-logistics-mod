# Stardew Logistics

A Stardew Valley mod that brings **Applied Energistics 2**-style digital storage to the farm. Link chests
together with cables, then browse, search, withdraw and auto-route tens of thousands of items from a single
terminal — instead of walking a wall of 40 chests looking for the one with the iron ore in it.

Requires Stardew Valley 1.6+ and SMAPI 4.0+.

---

## What it does

Put a **Logistics Cable** down. Every chest that touches a cable joins the network. Put a **Storage Terminal**
next to the same cable run and you get one searchable window showing everything in every attached chest, with
combined counts.

```
        [Chest]  [Chest]  [Chest]
           |        |        |
        ===+========+========+===        <- Logistics Cable
           |                 |
     [Terminal]        [Import Bus]--[Keg]
```

### Devices

| Device | AE2 analogue | What it does |
|---|---|---|
| **Logistics Cable** | ME Cable | Carries the network between tiles. Chests and devices attach by touching it orthogonally. |
| **Logistics Controller** | ME Controller | Raises the network's channel budget so it can grow past a handful of devices. |
| **Storage Terminal** | ME Terminal | Browse, search, sort, withdraw and deposit. |
| **Crafting Terminal** | Crafting Terminal | A terminal that also crafts using materials held anywhere on the network. |
| **Import Bus** | ME Import Bus | Pulls items out of the adjacent chest or finished machine and onto the network. |
| **Export Bus** | ME Export Bus | Pushes filtered items from the network into the adjacent chest or machine. |

There is no storage-bus item: **any player chest touching a cable is network storage**, which keeps the common
case to "place cable, place chest, done". Chests take the role AE2 gives to storage cells, including priority
and partitioning — configured from the terminal's **Storage** tab rather than by holding the chest.

### The terminal

- **Items** — every item on the network as one grid, with combined counts abbreviated (`12.3K`, `4.5M`) so big
  numbers still fit in a slot. Search by name, by context tag (`#wine`), or by category (`@Fish`). Sort by
  name, quantity or category.
  - *Left-click* takes a stack · *right-click* takes one · *shift-click* fills your bag.
  - Clicking an item in your own inventory stores it; shift-click stores every stack of that item.
- **Craft** (Crafting Terminal only) — the game's own crafting menu, backed by every chest on the network.
- **Storage** — each attached chest and bus, with its insertion priority and a 9-slot filter.
- **Network** — channel usage, device counts, slots used, and what's wrong if the network is down.

### Priorities and filters

Each chest has a **priority** (higher fills first and drains last) and a **filter** of up to nine items in
either *Allow* or *Deny* mode. A chest with an Allow filter is treated as dedicated storage and is offered
matching items before any general-purpose chest, whatever the priority numbers say — so "all my ore goes in
this one chest" works without tuning anything.

Import and export buses use the same filter widget, but an empty filter means different things:

- **Import Bus** with an empty filter imports *everything*.
- **Export Bus** with an empty filter exports *nothing* (as in AE2 — otherwise one bus would drain the network
  into the first chest it touched).

### Channels

Like AE2, devices consume channels. A network with no controller supports **8** devices; each **Logistics
Controller** adds **32**. Terminals, buses and each attached chest each spend one channel; cables and
controllers don't. Exceed the budget and the network goes offline until you add a controller or remove
devices — the Network tab tells you exactly where you stand.

If you'd rather not think about it, set `EnableChannelLimits` to `false` for unlimited networks.

**Deliberate divergence from AE2:** there's no power system. Stardew has no electricity to model, and a
"feed your network coal" chore would be busywork rather than a puzzle. Channels alone carry the
build-out-your-infrastructure pressure.

---

## Configuration

Edit `config.json`, or use [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) if
installed.

| Setting | Default | Meaning |
|---|---|---|
| `EnableChannelLimits` | `true` | Whether devices consume channels at all. |
| `AdHocDeviceLimit` | `8` | Devices supported without a controller. |
| `ChannelsPerController` | `32` | Devices each controller adds. |
| `MaxNetworkSize` | `20000` | Safety cap on cable tiles per network. |
| `BusIntervalTicks` | `30` | How often buses run (60 ticks = 1 second). |
| `BusItemsPerRun` | `64` | Most items one bus moves per run. |
| `EnableMachineAutomation` | `true` | Let buses collect from and load into machines. |
| `UnlockAllRecipes` | `true` | Teach all recipes now, rather than gating behind Mining levels. |
| `OpenTerminalKey` | *(none)* | Optional key to open the terminal under the cursor. |

Recipes unlock at Mining 2 / 4 / 5 / 5 / 7 / 8 (cable, terminal, buses, controller, crafting terminal) when
`UnlockAllRecipes` is off.

---

## Multiplayer

Per-device settings (priority, filters) are stored in each object's `modData`, so the game saves and
synchronises them for free — no host-only save data, no custom network messages. Buses run on the host only,
so items move once rather than once per player. The network skips any chest another player currently has open.

---

## Building

There is no prebuilt release; you build against your own copy of the game.

```bash
cd src/StardewLogistics
dotnet build -c Release
```

[`Pathoschild.Stardew.ModBuildConfig`](https://github.com/Pathoschild/SMAPI/blob/develop/docs/technical/mod-build-config.md)
finds your game folder automatically and copies the built mod into `Mods/StardewLogistics`. If your game is
installed somewhere unusual, set `GamePath` in the `.csproj` or create a `stardewvalley.targets` file.

---

## Project layout

```
src/StardewLogistics/
  ModEntry.cs              entry point, events, terminal activation
  Framework/               config, item identity, filters, number formatting
    ItemKey.cs             the hash key that makes aggregating 50k stacks cheap
    ItemFilter.cs          allow/deny partitions, serialised into modData
  Network/
    NetworkScanner.cs      flood-fills cables into networks
    NetworkManager.cs      per-location cache, invalidated on world changes
    StorageNetwork.cs      aggregation, insertion, extraction
    StorageEntry.cs        one attached chest: priority + partition
  Devices/
    BusRunner.cs           import/export bus tick
    MachineIO.cs           machine collection and loading
  Menus/
    TerminalMenu.cs        menu frame, item grid, withdraw/deposit
    TerminalMenu.Tabs.cs   Storage and Network tabs
  Integrations/            GMCM API, Data/BigCraftables + Data/CraftingRecipes edits
  assets/craftables.png    16x32 spritesheet, 6 devices
  i18n/default.json        all user-facing strings
```

---

## Status and known limitations

**This has not been compiled or run.** It was written in an environment with no .NET SDK and no copy of
Stardew Valley to reference, so it has had a careful reading but no compiler pass. Expect to fix a few API
mismatches on your first build. The places most likely to need adjustment, in order:

1. **`MachineIO.cs`** — driving `PlaceInMachine` and reading `MachineOutputRule.Triggers` from outside the
   normal player-interaction path is the least certain API use in the mod. Setting `EnableMachineAutomation`
   to `false` disables everything in this file.
2. **`CraftingPage` material containers** — 1.6 takes `List<IInventory>`; if your game build wants
   `List<Chest>`, change `StorageNetwork.GetMaterialInventories()` to return the chests themselves.
3. **Menu drawing details** — sprite source rectangles and font metrics are cosmetic; wrong ones look off
   rather than crash.

Functional limitations that are by design or simply not built yet:

- **No autocrafting.** The Crafting Terminal crafts on demand from network stock; it does not queue
  multi-step crafts the way an AE2 pattern provider does.
- **Networks don't span locations.** There's no quantum bridge; a cable run is confined to one map.
- **Machine loading can't reach fuel in storage.** The game checks extra inputs (a furnace's coal) against the
  *player's* inventory, so a furnace fed by an export bus still wants coal on you.
- **Self-restarting machines are skipped.** Tappers and crystalariums restart as part of the player collecting
  them, so import buses leave them alone rather than silently switching them off.
- **Excluded chests:** loot chests, mini-shipping bins and Junimo chests never join a network.
