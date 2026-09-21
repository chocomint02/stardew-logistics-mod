# Stardew Logistics

A Stardew Valley mod that brings **Applied Energistics 2**-style digital storage to the farm. Link chests
together with cables, then browse, search, withdraw and auto-route tens of thousands of items from a single
terminal — instead of walking a wall of 40 chests looking for the one with the iron ore in it.

Requires Stardew Valley 1.6+ and SMAPI 4.0+.

---

## What it does

Lay **Logistics Cable** down. Anything on or beside a cable joins the network: chests become shared storage,
machines become available for processing, and a **Storage Terminal** gives you one searchable window over the lot.

Cable is a *floor*, not an object, so a chest or a furnace can sit **on** the same tile as the cable feeding it.
A wall of machines needs no gaps for wiring.

```
   [Chest][Chest][Chest]       chests sit on the cable
   ======================      <- cable floor, walkable
   [Keg] [Keg] [Terminal]      machines sit on it too
```

### Devices

| Device | AE2 analogue | What it does |
|---|---|---|
| **Logistics Cable** | ME Cable | A floor tile that carries the network. Anything on it or beside it attaches. |
| **Storage Terminal** | ME Terminal | Browse, search, sort, withdraw and deposit. |
| **Crafting Terminal** | Crafting Terminal | A terminal that also crafts using materials held anywhere on the network. |

There are no bus items and no storage-bus item. **Any player chest on or beside a cable is network storage**, and
**any vanilla machine on or beside a cable is wired to the network** — the network empties finished machines into
storage on its own. That keeps the common case to "lay cable, put things on it, done".

Chests take the role AE2 gives to storage cells, including priority and partitioning, configured from the
terminal's **Storage** tab rather than by holding the chest.

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

### No channels, no power

Both of AE2's infrastructure limits are deliberately absent. There is no power system, because Stardew has no
electricity to model and "feed your network coal" would be a chore rather than a puzzle. There are no channels
either: they were implemented and then removed, because in a game where the storage *is* a wall of chests, a
device budget mostly punishes the player for building the thing the mod exists to build.

A network is as large as the cable you lay. The only limit is `MaxNetworkSize`, a safety valve against a runaway
flood fill, not a gameplay rule.

---

## Configuration

Edit `config.json`, or use [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) if
installed.

| Setting | Default | Meaning |
|---|---|---|
| `MaxNetworkSize` | `20000` | Safety cap on cable tiles per network. |
| `BusIntervalTicks` | `30` | How often the network services machines (60 ticks = 1 second). |
| `BusItemsPerRun` | `64` | Most items moved per machine per run. |
| `EnableMachineAutomation` | `true` | Let the network collect finished machine output. |
| `UnlockAllRecipes` | `true` | Teach all recipes now, rather than gating behind Mining levels. |
| `OpenTerminalKey` | *(none)* | Optional key to open the terminal under the cursor. |

Recipes unlock at Mining 2 / 4 / 8 (cable, storage terminal, crafting terminal) when `UnlockAllRecipes` is off.

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
    NetworkScanner.cs      flood-fills the cable floor into networks
    NetworkManager.cs      per-location cache, invalidated on world changes
    StorageNetwork.cs      aggregation, insertion, extraction
    StorageEntry.cs        one attached chest: priority + partition
  Devices/
    NetworkTicker.cs       empties finished machines into storage
    MachineIO.cs           machine collection and loading
  Menus/
    TerminalMenu.cs        menu frame, item grid, withdraw/deposit
    TerminalMenu.Tabs.cs   Storage and Network tabs
  Integrations/            GMCM API, and the Data/* edits that register the content
  assets/craftables.png    16x32 spritesheet for the terminals
  assets/cable-floor.png   64x64 floor tilesheet, 16 connection variants
  i18n/default.json        all user-facing strings
```

`tools/` holds the generators for both spritesheets. `make_cable_floor.py` documents the neighbour-bitmask table
it encodes, which was read out of the game by reflection rather than guessed.

---

## Status and known limitations

Builds and runs against **Stardew Valley 1.6.15 / SMAPI 4.5.2**. The storage network, terminal, filtering and
deposit/withdraw paths have been exercised in-game.

Not yet verified in-game: the cable floor itself. Placement, connection rendering and attaching objects that sit
*on* a cable are new and have only been checked at compile time.

Functional limitations, by design or not built yet:

- **No autocrafting yet.** The Crafting Terminal crafts on demand from network stock; it does not queue
  multi-step jobs across wired machines. That is the next major piece of work.
- **Networks don't span locations.** There is no wireless link yet, so a cable run is confined to one map.
- **Machines are collected but not loaded.** The network empties finished machines into storage. Feeding them
  belongs to the autocrafting scheduler, so it deliberately doesn't happen on its own.
- **Machine loading can't reach fuel in storage.** The game checks extra inputs (a furnace's coal) against the
  *player's* inventory, which will constrain the scheduler when it lands.
- **Self-restarting machines are skipped.** Tappers and crystalariums restart as part of the player collecting
  them, so the network leaves them alone rather than silently switching them off.
- **Excluded chests:** loot chests, mini-shipping bins and Junimo chests never join a network.
