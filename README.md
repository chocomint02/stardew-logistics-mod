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
  numbers still fit in a slot.
  - *Left-click* takes a stack · *right-click* takes one · *shift-click* fills your bag.
  - Clicking an item in your own inventory stores it; shift-click stores every stack of that item.
- **Craft** (Crafting Terminal only) — a searchable recipe browser that crafts from network stock. Recipes you
  can't afford are dimmed rather than hidden, and hovering one shows its ingredients with have/need counted
  against the network.
  - *Left-click* crafts one · *shift-click* five · *right-click* opens a bulk dialog.
  - The bulk dialog has `Min / -100 / -50 / -25 / -10 / -1` and the matching increments either side of a
    quantity box that accepts arithmetic, so `10*2` and `(3+4)*6` work.
- **Storage** — each attached chest with its insertion priority and 9-slot filter, plus every wired machine and
  whether it is idle, working or ready.
- **Network** — cable tiles, terminals, machines, chests, slots used and total stock.

Search and the **Type** and **Mod** dropdowns work on both the Items and Craft tabs. Terms are ANDed:

```
blueberry        name contains "blueberry"
"iron bar"       quoted phrase
#wine            context tag
@fish            category
~ridgeside       source mod
>500  <=2000     quantity, or on the Craft tab, craftable batches
!stone           negate any of the above
#wine >100       combined
```

Mod attribution is a heuristic: Stardew keeps no item-to-mod index, so item IDs are matched against loaded mod
IDs using the 1.6 namespacing convention. Older content with unnamespaced IDs reports as "Unknown" rather than
being guessed at.

### Priorities and filters

Each chest has a **priority** (higher fills first and drains last) and a **filter** of up to nine items in
either *Allow* or *Deny* mode. A chest with an Allow filter is treated as dedicated storage and is offered
matching items before any general-purpose chest, whatever the priority numbers say — so "all my ore goes in
this one chest" works without tuning anything.

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
  Framework/               config, item identity, filtering, recipes
    ItemKey.cs             the hash key that makes aggregating 50k stacks cheap
    ItemFilter.cs          allow/deny partitions, serialised into modData
    StockFilter.cs         the search query language, shared by both grids
    ItemSource.cs          maps an item to the mod that added it
    RecipeIndex.cs         the recipes the player knows
    RecipeEntry.cs         one recipe + how many batches the network affords
    MathExpression.cs      arithmetic for the bulk quantity box
    Log.cs                 diagnostic logging
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
    TerminalMenu.Craft.cs  the recipe browser
    BulkCraftMenu.cs       the quantity dialog
    RecipeTooltip.cs       hover panel, counted against the network
    DropdownPopup.cs       the Type and Mod filter menus
  Integrations/            GMCM API, and the Data/* edits that register the content
  assets/craftables.png    16x32 spritesheet for the terminals
  assets/cable-floor.png   64x64 floor tilesheet, 16 connection variants
  assets/ui-icons.png      16x16 icons for the terminal's own buttons
  i18n/default.json        all user-facing strings
```

`tools/` holds the generator for each spritesheet. `make_cable_floor.py` documents the neighbour-bitmask table
it encodes, which was read out of the game by reflection rather than guessed. The UI icons are drawn rather than
cropped from the game's shared cursor sheet, because picking rectangles out of that texture blind produced
meaningless crops more than once.

---

## Status and known limitations

Builds and runs against **Stardew Valley 1.6.15 / SMAPI 4.5.2**, and has been exercised in-game: cable places as
a floor and renders connected, chests and machines sitting on or beside it join the network, wired machines empty
themselves into storage, and the terminal's browsing, filtering, crafting and deposit/withdraw paths all work.

Functional limitations, by design or not built yet:

- **No autocrafting yet.** The Crafting Terminal crafts on demand from network stock; it does not queue
  multi-step jobs across wired machines. That is the next major piece of work, and the recipe browser was built
  to host its queue.
- **Networks don't span locations.** There is no wireless link yet, so a cable run is confined to one map.
- **Machines are collected but not loaded.** The network empties finished machines into storage. Feeding them
  belongs to the autocrafting scheduler, so it deliberately doesn't happen on its own.
- **Machine loading can't reach fuel in storage.** The game checks extra inputs (a furnace's coal) against the
  *player's* inventory, which will constrain the scheduler when it lands.
- **Self-restarting machines are skipped.** Tappers and crystalariums restart as part of the player collecting
  them, so the network leaves them alone rather than silently switching them off.
- **Excluded chests:** loot chests, mini-shipping bins and Junimo chests never join a network.
