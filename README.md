# Stardew Logistics

An advanced logistics mod for Stardew Valley. 

Requires **Stardew Valley 1.6** and **SMAPI 4.0** or later. Built and tested on 1.6.15 / SMAPI 4.5.2.

---

## Contents

- [Installation](#installation)
- [Getting started](#getting-started)
- [Items](#items)
- [Terminal](#the-terminal)
- [Storage](#storage)
- [Autocrafting](#autocrafting)
- [Minimum stock](#minimum-stock)
- [Auto-Harvester](#auto-harvester)
- [Wireless](#wireless)
- [Shipping](#shipping)
- [Income](#income)
- [Multiplayer](#multiplayer)
- [Configuration](#configuration)
- [Console Commands](#console-commands)
- [Compatibility](#compatibility)
- [Known Limitations](#known-limitations)
- [Building](#building)
- [Project Layout](#project-layout)

---

## Installation

1. Install [SMAPI](https://smapi.io/) 4.0 or later.
2. Download the latest [Release](https://github.com/chocomint02/stardew-logistics-mod/releases/tag/Release)
3. Unzip it into your game's `Mods` folder.
4. Launch the game through SMAPI.

---

## Getting started

1. Craft **Logistics Cable** and lay it on the ground. Cables are a floor tile, so it can be walked on and objects can
   be placed on top of it.
2. Place chests and machines **on** or **directly beside** the cable. They join the network automatically.
3. Place a **Storage Terminal** or **Crafting Terminal** on or beside the cable and interact with it.

```
   [Chest][Chest][Chest]        chests on the cable
   =====================        cable (floor tile)
   [Keg] [Keg] [Terminal]       machines and terminals on it too
```

Every connected cable tile, chest and machine forms one network. A network has no power or device limits, except
as governed by `MaxNetworkSize`.

---

## Items

| Item | Recipe | Unlocks at | Purpose |
|---|---|---|---|
| Logistics Cable (x8) | 1 Copper Bar, 5 Stone | Mining 2 | Carries the network. |
| Storage Terminal | 2 Iron Bar, 5 Refined Quartz, 10 Hardwood | Mining 4 | Storage access. |
| Crafting Terminal | 3 Gold Bar, 10 Refined Quartz, 1 Battery Pack | Mining 8 | Storage access, crafting, autocrafting, stock rules. |
| Wireless Transmitter | 2 Gold Bar, 5 Refined Quartz, 1 Battery Pack | Mining 7 | Broadcasts its network on a channel. |
| Wireless Receiver | 1 Gold Bar, 2 Refined Quartz, 1 Battery Pack | Mining 7 | Joins its network to a transmitter's channel. |
| Wireless Terminal | 2 Iridium Bar, 2 Battery Pack, 5 Refined Quartz | Mining 9 | Handheld Crafting Terminal, opened anywhere. |
| Auto-Harvester | 5 Iron Bar, 2 Gold Bar, 1 Quality Sprinkler, 1 Battery Pack | Farming 6 | Farms a planned area. |

All recipes are learned immediately while `UnlockAllRecipes` is on (default).

---

## The terminal

The Storage Terminal shows the **Items**, **Farm**, **Storage**, **Network**, **Shipping**, **Income** and
**Settings** tabs.
The Crafting Terminal and Wireless Terminal add **Craft**, **Jobs** and **Stock**.

| Tab | Purpose |
|---|---|
| Items | Browse, withdraw and deposit network storage. |
| Craft | Plan and queue autocrafting jobs for anything the network can make. |
| Jobs | Monitor, speed up and cancel running jobs. |
| Stock | Manage minimum-stock rules. |
| Farm | View Auto-Harvesters on the network and a live view of their fields. |
| Storage | Configure priorities and filters, and view connected machines / storage. |
| Network | Network statistics; channel control for the Wireless Terminal. |
| Shipping | Sell stored items through a connected shipping bin. |
| Income | Income forecast, history, expense planner and ledger. |
| Settings | Color scheme and animation speed. |

### Appearance

- **Color schemes:** 17 schemes, each with a preview swatch. Light: Vanilla, Cream, Light, Forest, Sakura,
  Coral, Ocean, Glacier, Lavender, Citrus. Dark: Dark, Midnight, Eclipse (OLED black), Pine, Aurora, Amethyst,
  Ember. Applies to every window the mod opens, including its tooltips, text boxes and the inventory shown inside
  them. Other game menus are unaffected.
- **Animations:** switching tabs or Income views slides and fades the new content in, and the active highlight
  glides across. Grid icons grow slightly when hovered, as in the inventory. Buttons, tabs and legend entries
  in every window the mod opens (the terminal, the autocrafting planner, the harvester and its plan, the wireless
  devices, and the sell, bulk-craft and expense windows) light up under the cursor with a soft wash and an
  underline, and a click sends a ripple across the control. The
  terminal fades in and out, ledger days fold open, letters typed into the search box pop into place (and
  deleted ones float away) with a gliding caret, tooltips grow out from the cursor as they appear, shrink back as they go, and resize smoothly
  when the cursor moves from one thing to another, their frames
  carrying a soft streak of light travelling round the border, a new color scheme fades in over the old one, and planned expenses slide in and fade away as they're added
  and removed. Speed is adjustable (0–300%), or can be disabled completely.
- **Devices** play a boot sequence when placed, then run: terminals scroll stock or fill a crafting grid, the
  transmitter sends rings out and the receiver takes them in, and the Auto-Harvester's sprout sways under its grow
  light. Data pulses flow along cables. These follow the same speed setting; at 0% devices show their still sprite.
- Both can be changed on the terminal's **Settings** tab, where changes apply immediately, or in Generic Mod
  Config Menu.

### Search

The search box and the **Type** and **Mod** filters apply to the Items, Craft, Auto and Shipping tabs. Terms
combine with AND.

```
blueberry        name contains "blueberry"
"iron bar"       exact phrase
#wine            context tag
@fish            category
~ridgeside       source mod
>500  <=2000     quantity held
!stone           negate any term
```

Source-mod detection matches item IDs against loaded mods using the 1.6 ID namespacing convention. Items with
unnamespaced IDs report as "Unknown".

---

## Storage

- Every **player chest** on or beside a cable is network storage, including Big Chests. Loot chests, Junimo
  Chests etc... are never used for storage.
- Chests another player has open are skipped until they're closed.
- **Items tab:** click to take a stack, right-click to take one, shift-click to fill your bag. Click an item in
  your bag to store it; shift-click to store every stack of it. **Deposit All** stores everything except tools.
- Counts are abbreviated in the grid (`12.3K`, `4.5M`).

### Priorities and filters

Configured per chest on the **Storage** tab.

- **Priority:** higher-priority chests input first and extract last.
- **Filter:** up to nine items in **Allow** or **Deny** mode. A chest with an `Allow` filter is dedicated
  storage - matching items go there before any general chest, regardless of priority.

Settings are stored on the chest, so they persist when moving network cables or rebuilding the network.

### Machines

Any machine on or beside a cable is part of the network. Finished output is collected into storage
automatically (`EnableMachineAutomation`), and anything that starts again on its own is restarted through expected
normal behavior:

- Machines whose rules restart on collection (Crystalariums, Worm Bins, Bee Houses) start their next batch.
- Tappers set their tree producing again.
- Crab Pots are rebaited from storage (cheapest bait first), unless their owner needs no bait.

Machines are only emptied when storage has room for the whole output. When a machine holding items is
removed, its inputs or finished output returns to storage instead of being lost.

---

## Autocrafting

The **Craft** tab lists everything the network can make: known crafting recipes, plus the output of every
machine connected to the network. A blue corner marks machine-made items; a green corner marks crops grown on
automation tiles. Dimmed items have missing ingredients; the hammer button shows only what the network can
make right now.

Hover an item for its description and what it's made from, each ingredient with a have/need count against
storage: a crafting recipe's ingredients, the machine and inputs for a machine-made item (the ways storage can
supply first), or the seeds for a crop.

Selecting an item opens the planner, which shows the complete production tree before anything starts.

### Planning

- **Multi-step chains.** Ingredients are drawn from storage first, then crafted or processed as needed, down to
  `MaxCraftDepth` steps.
- **Machine choice.** When several machines can make a step (e.g. Furnace and Heavy Furnace), the work is
  split between them to minimize time. Clicking on a step allows you to manually configure what machine(s) are used.
- **Crafting or a machine.** Where an item can be both crafted and made in a machine (an Iron Bar transmuted or
  smelted), both are planned. The one that can be supplied is used; if both can, the one with less machine time
  across its whole chain, then the one using less valuable stock. Steps with a choice carry a swap icon: click
  one to pick another machine, or crafting, yourself.
- **No self-feeding recipes.** A recipe or machine that takes in what it makes (a Crystalarium copying a gem), or
  something further up the same chain, is never planned. The item is made another way if there is one, and
  otherwise taken from storage, noted as missing: "only made from one of itself".
- **Max machines.** Limits how many machines each step may occupy. Defaults to every available machine.
- **Ingredient alternatives.** Recipes that accept more than one input (eg. Duck Mayonnaise from a Duck Egg or a
  Golden Duck Egg) consider every option.
- **Multiple inputs per step.** Machines that consume extra items, whether machine-wide (a Furnace's coal) or
  per recipe through Extra Machine Config, plan, reserve and load every
  ingredient. Extra ingredients given as an item, a category ("any gem") or context tags are drawn from
  storage. Where the product takes its flavor, color or price from the extra ingredient, each ingredient in
  storage is its own recipe. A recipe shows as soon as its main ingredient is stored, even if an extra ingredient
  is short; the plan names what's missing. The exception is a product that takes its identity from the extra
  ingredient, which appears once a matching ingredient has been stored.
- **Category ingredients.** Crafting recipes that ask for a category ("any egg") draw matching items from
  storage, cheapest first.
- **Quality.** Lowest-quality ingredients are used first, unless a higher quality needs fewer inputs *and*
  fewer machine-hours. Ingredients drawn from storage are listed per quality.
- **Flavored goods.** Wine, juice, jelly, pickles, roe, honey and dried or smoked goods are planned by
  ingredient: Starfruit Wine and Parsnip Juice are distinct items.
- **Aging.** Items a Cask can age offer a target quality. The planner ages existing stock (best first) and
  produces the rest from scratch. Casks where aging isn't allowed are ignored.
- **Fairy Dust.** Optionally applied to machines that accept it, using dust from storage.
- **Crops.** Crops growing under Auto-Harvesters count as incoming stock and are reserved by the job. If a crop
  isn't stored or growing, the planner reserves free **automation tiles** and plants it, optionally with a chosen
  Speed-Gro.
- **Diagram.** The **Diagram** tab draws the plan as a flowchart, raw materials on the left and the finished item
  on the right, with links showing what goes into what. Drag to move it, scroll to zoom around the cursor, and
  click a step to change how it's made, as in the list. **Fit** frames the whole plan. Cards pop in column by column,
  links draw themselves in and packets flow along them; when the plan changes, cards glide to their new places.
- **Summary.** Total time (including crop growth), value of the finished items, and gold per day.
- **Missing ingredients** are named with the reason: not in storage, no machine on the network, no free automation
  tiles, or no time left in the season to grow it.

### Jobs

Queued jobs appear on the **Jobs** tab with progress, time remaining, value and current status.

- **Ingredients are reserved on queue.** Ingredients leave storage immediately and can't be taken by anything else.
- **Machines are claimed** while in use, and held in advance when an earlier step is still producing their
  input.
- **Waiting reasons** are specific: waiting for a free Keg, for an earlier step, or for crops to grow.
- **Fairy Dust** can be switched on for a running job. The button is hidden when storage has none.
- **Cancel** stops the job and returns its inputs from machines and its reserved items to storage.
- **Casks.** Striking a Cask a job is using returns the item to the job; aging continues in another Cask with
  progress kept. Casks placed after queuing are used as they appear on network.
- **Broken machines.** The job recovers the machine's inputs and reruns the batch elsewhere. If no suitable
  machine remains, the job cancels and refunds the input ingredient(s).
- **Saved with the game.** Jobs, running batches, machine claims, crop reservations and pending plantings are
  written to the save and resume on load. Batches that finished overnight are collected on the first pass.
  A job that can't be restored (e.g. its recipe's mod was removed) returns its reserved items to storage.

---

## Minimum stock

Keeps at least a set amount of an item in storage. Open an item on the **Craft** tab, set the quantity and
options, and select **Keep Stocked**.

- Checked every 10 in-game minutes. When stock plus pending production falls below the minimum, a job is
  queued for the difference.
- If the full amount can't be made, as much as possible is queued. If none can be made, the rule shows the
  reason and retries hourly, with one notification per day.
- Rules keep their quality, Fairy Dust, Speed-Gro and machine settings.
- The **Stock** tab lists each rule with current stock and status. Adjust the minimum with **−/+** (Shift: 10),
  click a rule to edit it, or **Remove** it.
- Jobs created by rules are labelled `(stock rule)` and clear themselves when finished.
- Rules are stored on the network's terminals and apply wherever that network reaches.

---

## Auto-Harvester

A machine that farms a rectangular area. Connect it to a network: seeds and fertilizer come from storage and
the harvest goes into it.

### Area

- 1×1 to 50×50 tiles, offset up to 50 tiles in any direction from the machine.
- **Area Shown** outlines the area in the world while the menu is closed.
- **Preview** shows what is currently growing; **Plan** opens the planning grid.

### Planning grid

The grid shows each tile as it looks in the world: tilled soil, fertilizer and crop sprites.

- **Seeds** and **Fertilizer** tabs paint the selected item onto tiles. Right-click erases. **Fill Area**
  and **Clear All** apply to the whole area. Palette counts show what storage holds.
- **Automation** tab allocates tiles for autocrafting. Jobs and stock rules plant crops on free automation
  tiles when they need them. Allocated tiles are shaded gold; tiles in use by a job are outlined.
- **Growth stage** arrows preview planned crops at any stage.
- **Replant After Harvest** is set per seed for single-harvest crops. Regrowing crops stay planted.
- **Issues panel** flags problems and highlights the affected tiles in red: untillable tiles, out-of-season
  seeds, crops that won't mature before their season ends, and seed or fertilizer shortfalls.
- **Force Change** clears crops that conflict with the new plan when you confirm, after a confirmation
  prompt. Without it, the harvester waits for those crops to finish. Crops that regrow indefinitely
  (greenhouse, Ginger Island) require Force Change.

### Behavior

- Runs every 10 in-game minutes and each morning.
- Tills, fertilizes, plants and waters planned tiles; harvests anything ripe in the area.
- Only plants when the crop will mature before the season ends, accounting for fertilizer and multi-season
  crops. Greenhouse and Ginger Island crops ignore seasons.
- A planned fertilizer replaces a different fertilizer already in the soil.
- When storage is full, ripe crops stay in the field and a notification is shown.
- Tilled soil inside the area doesn't revert overnight, even when empty.
- Harvests from crops reserved by a job go directly to that job.

---

## Wireless

### Transmitters and receivers

Place a **Wireless Transmitter** on one network and a **Wireless Receiver** on another, tuned to the same
channel (1–999). The two networks, and every receiver on that channel, combine into one network across any
number of locations. A channel needs at least one transmitter. Channel numbers are shown above nearby devices.

### Wireless Terminal

A handheld Crafting Terminal.

- **Equip:** place it in the terminal slot below Boots in your inventory.
- **Open:** press **B** (configurable) anywhere.
- **Link:** set its channel on the **Network** tab to reach the network with a transmitter on that channel.

---

## Shipping

Connect a network to a **Shipping Bin** (cable beside the building) or to a **Mini-Shipping Bin**.

- The **Shipping** tab lists stored items the shipping bin accepts. Items it doesn't accept are never exported.
- Click an item to open the sale window:
  - Set a quantity with the input box, **±1 / 10 / 50 / 100 / 1000**, or **±Max**.
  - Or enter a **Gold Target**: the quantity is calculated to reach it, rounded up. Targets above the stored
    value are flagged.
  - **Max Value** shows what the entire stock would sell for.
- Switch to **Shipping Bin** to see items reserved for sale; click an item to return it to storage.
- Prices account for quality and profession modifiers.
- Mini-Shipping Bins have limited slots; anything that doesn't fit stays in storage.

---

## Income

The **Income** tab shows **Net worth** (sale value of everything in storage), **Income** per day, and
**Profit** per day once input expenses are set. Figures are color-coded by amount.

### Forecast

Projected income from everything on the network that's producing:

- **Machines:** output value ÷ processing time. 
- **Autocrafting machines:** count the batches their job has left.
- **Recurring machines:** Any machine whose rules restart it each morning or on collection repeats
  indefinitely, including while empty between batches, and including modded machines. Cycle times use the
  game's per-item timing; machines that restart each morning count at most one batch a day; machines the game times itself, such as Solar Panels, use their observed countdown.
  Tappers repeat at their tree's rate. Crab Pots repeat daily while baited, valued at their current or last catch.
- **Casks:** count only the value aging adds.
- **Animals:** every adult animal living in a coop or barn with network cable inside (linked by a Wireless
  Receiver, or a network of its own) counts its produce at its current produce quality, every *days to
  produce*. Golden Animal Crackers are accounted for; deluxe produce is not counted; Pigs don't
  count in winter. Baby animals are listed as *(young)* and counted from the day they grow up: they add
  to the projection, but not to income per day until they're producing.
- **Crops under Auto-Harvesters:** guaranteed yield ÷ growth time. Regrowing and replanted crops repeat until
  their season ends; crops reserved by autocrafting jobs are excluded.

Graph options: **Daily** bars or **Running Total** line; **7 / 28 / 112 Days**; **Linear / Log** scale;
**By Source** stacks each producer in its own color, with a legend to show or hide individual sources. The graph
is shaded by season. Hover for daily figures: the hovered day lights up, a guide line follows the cursor, and on
the running total a marker rides the line. Hovering a legend entry picks out that source or tier in the graph.

**Colors:** click a legend swatch (a tier's row, or a source's color square) to choose its color from a palette,
or reset it with **Default**. Choices are saved to the config: tier colors as `IncomeTierColours` (also editable
in Generic Mod Config Menu) and source colors by name as `IncomeSourceColours`.

### History

The same graph built from recorded earnings, split into shipping and other income.

### Expenses

- **Planned Expenses:** purchases you're saving for. The tab shows how many days until they're covered at
  current profit, optionally counting gold on hand.
- **Input Expenses:** the unit cost of inputs such as seeds. Deducted from each producer's income to show
  profit and margin. A crop's cost is its seed cost divided by guaranteed yield. Shop prices (Pierre, JojaMart
  and others) can be applied with one click.

### Ledger

Each day's earnings are recorded overnight: shipping (itemized by item, quantity and gold) and other income
(quests, mail, shop sales). Shows 7-day and 28-day totals and the best day. Stored in the save.

---

## Multiplayer

The host runs every network. Other players' actions are sent to the host and carried out there, so items are
never duplicated.

- Withdraw, deposit, craft, queue and cancel jobs, set stock rules, ship and return items all work for every
  player. Items move through per-player transfer inventories that the game syncs and saves.
- Jobs, stock rule status and the ledger are sent from the host to all players.
- Any change to a network refreshes every open terminal immediately.
- Device settings (priorities, filters, channels, harvester plans) are stored on the objects themselves and
  sync automatically.
- Shipping uses each player's own bin when wallets are separate.

---

## Configuration

Edit `config.json`, or use [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098).

| Setting | Default | Description |
|---|---|---|
| `MaxCraftDepth` | `6` | Maximum steps in an autocrafting chain. |
| `MaxNetworkSize` | `20000` | Maximum cable tiles per network (safety cap). |
| `BusIntervalTicks` | `30` | How often machines are serviced (60 ticks = 1 second). |
| `BusItemsPerRun` | `64` | Maximum items moved per machine per run. |
| `EnableMachineAutomation` | `true` | Collect finished machine output into storage. |
| `UnlockAllRecipes` | `true` | Learn all recipes immediately instead of by skill level. |
| `OpenTerminalKey` | *(none)* | Optional key to open the terminal under the cursor. |
| `OpenWirelessTerminalKey` | `B` | Opens the equipped Wireless Terminal. |
| `Theme` | `Vanilla` | Window colour scheme; see [Appearance](#appearance) for the names. |
| `AnimationSpeed` | `100` | Menu, device and cable animation speed in percent (0–300); `0` turns animations off. |
| `IncomeTierColours` | brown, green, blue, purple, gold | The Income graph's five tier colors, lowest first, as `#RRGGBB`. |
| `IncomeSourceColours` | *(none)* | Colors chosen for particular income sources, by name, as `#RRGGBB`. |
| `AdaptiveCalibration` | `true` | Learn how long machines and crops really take and what shipping pays, and plan by that. |

---

## Console commands

For troubleshooting, in the SMAPI console:

| Command | Description |
|---|---|
| `logistics_stock` | List what the network at your location holds. |
| `logistics_machines [filter]` | List indexed processing recipes. |
| `logistics_rawmachine <filter>` | Dump a machine's raw `Data/Machines` rules. |
| `logistics_plan <item id> [count]` | Print an autocrafting plan. |
| `logistics_craft <item id> <count> [max machines]` | Queue an autocrafting job. |
| `logistics_jobs` | List jobs and their progress. |
| `logistics_cancel <job id>` | Cancel a job. |
| `logistics_calibration [reset]` | Show, or clear, what's been learned about timings and prices. |

---

## Compatibility

- **Automate is not supported.** 

### Mods that change machines, crops or fertilizer

- **Machines added or changed in data** (tiered machines, new rules, different times, amounts or quality, including
  through Extra Machine Config) are indexed like any other: each is its own machine with its own recipes.
- **Autocrafting jobs load machines through the game's own loading**, the way a Hopper does: the machine is offered
  the job's reserved items and takes what a run needs. Anything a mod changes about loading (how many items a run
  takes, fuel, the product, its quality, how long it takes) applies as if the player had loaded it. A machine that
  takes a bigger batch counts as several runs. If the game won't load a machine, or starts it making something the
  plan didn't ask for, the items go back and the job loads the machine directly, with the product the game's
  machine code makes from the actual input.
- **Machines that differ from others of their kind** (upgraded one by one, or several combined into one) are
  planned by how each actually works: before planning, a stand-in copy of each machine (same kind, same data) is
  loaded with sample inputs through the game's own loading, off in an empty location, and its timer and batch size
  are read off. Step times are then worked out on the network's actual machines, fastest first, and jobs use the
  fastest machines first. Measured once per machine and recipe, and again if the machine's data changes.
- **Fertilizer** is treated as a set. Where a mod lets fertilizers stack in one tile, the harvester checks whether the
  planned one is among them, and lays fertilizer through the game's own rules, so the mod adds it its own way; it's
  only replaced where the soil takes one, as in the base game. Growth times for stacked fertilizer come from the
  game's own speed-up code.

### Mods that change timing or prices

Plans, jobs, the harvester and forecasts are worked out from the game's data, so anything another mod changes in
that data (machine times and ready-time modifiers, crop phases, prices) is picked up as it is. Prices come from the
game's own `sellToStorePrice`, and crop times from the game's own speed-up code, so mods patching those are included
too.

For mods that change behaviour in code instead, the mod measures what actually happens in the save (**Adaptive
timing and prices**, on by default) and plans by that. No mod is special-cased.

| Watched | Compared with | Adjusts |
|---|---|---|
| What the game sets a machine's timer to when it starts | The machine's data | Planned run times, and the timer jobs set |
| How fast a machine's timer runs down | The clock | Planned run times and forecasts |
| A watered crop's growth overnight | The game's one day | Crop days for plans, the harvester and forecasts |
| What the shipping bin paid overnight | Its contents at the game's prices | Sale values, the sell window and forecasts |

- Every networked machine is timed from start to finish; each crop is compared between evening and morning.
- Each figure keeps its last 9 observations and uses their median, so a one-off (Fairy Dust, a crop that missed its
  water) doesn't skew it. Within 4% of the game's figure counts as no change.
- Specific figures (this machine making this item, this crop) are used first; with none yet, the machine's, then
  all machines' or all crops', stand in, since a speed-up mod usually applies to everything.
- Machines are also learned one by one, so machines upgraded or combined individually by other mods each keep
  their own figures; a machine's own figures come before its kind's.
- Learned figures are saved with the game, shared with farmhands, and shown on the **Network** tab.
  `logistics_calibration` prints them; `logistics_calibration reset` clears them.

TL;DR: This mod should work with most, if not all, mods. 
---

## Known limitations

- **Multiplayer compatibility is not tested**. Use at your own risk. 

---

## Building

To build it yourself instead of using the release zip, build against your own copy of the game:

```bash
cd src/StardewLogistics
dotnet build -c Release
```

[`Pathoschild.Stardew.ModBuildConfig`](https://github.com/Pathoschild/SMAPI/blob/develop/docs/technical/mod-build-config.md)
locates the game folder and deploys the mod to `Mods/StardewLogistics`. For a non-standard install location,
set `GamePath` in the `.csproj` or a `stardewvalley.targets` file. A Release build also writes the release zip,
`StardewLogistics <version>.zip`, to `bin/Release/net6.0`.

Spritesheets are generated by the scripts in `tools/` (Python 3, standard library only); run them from the
repository root. `make_sprites.py` draws the devices and their animation frames, `make_cable_floor.py` the cable
floor and its pulses, and `make_ui_icons.py` the terminal's icons; all share the palette in `pixelkit.py`. Each
takes `--preview` to write enlarged previews to `tools/`.

---

## Project layout

```
src/StardewLogistics/
  ModEntry.cs                  entry point and event wiring
  Network/                     cable scanning, network cache, storage aggregation, device nodes
  Devices/
    NetworkTicker.cs           collects finished machine output
    MachineIO.cs               machine collection, loading and refunds
    JobRunner.cs               autocrafting scheduler
    JobBuffer.cs               reserved job ingredients
    JobStore.cs                job save and restore
    StockKeeper.cs             minimum-stock rules
    HarvesterRunner.cs         Auto-Harvester farming loop
    ShippingService.cs         shipping bin transfers
    ShippingLedger.cs          daily earnings history
    IncomeForecast.cs          income projection
    NetworkCrafting.cs         crafting from network storage only
  Framework/
    CraftPlanner.cs            production planning
    MachineRecipeIndex.cs      machine recipes resolved through the game's own machine rules
    MachineAllocator.cs        machine budget per step
    CropMath.cs                growth time, season windows, yields
    Calibration.cs             learns real timings and prices from the save
    HarvesterSettings.cs       Auto-Harvester area and plan
    HarvesterPlanCheck.cs      plan validation
    StockId.cs                 item identity including flavour
    ItemKey.cs, ItemFilter.cs, StockFilter.cs, ...
  Multiplayer/                 host requests, transfer inventories, state sync
  Integrations/                content registration, GMCM, Harmony patches (cask, soil, equipment slot,
                               device and cable animation)
  Menus/                       terminal tabs, planner, harvester, sale and expense windows,
                               colour schemes and animation
  assets/                      spritesheets
  i18n/default.json            all user-facing text
tools/                         spritesheet generators
```
