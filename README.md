# Stardew Logistics

An advanced logistics mod for Stardew Valley. Connect chests and machines with cable, then store, search,
craft, automate production, farm, and sell from one terminal.

- **Storage network** — every connected chest acts as one searchable inventory.
- **Autocrafting** — multi-step crafting and machine production, planned and run automatically.
- **Minimum stock** — keep a set amount of any item in storage; production is queued when it drops below.
- **Auto-Harvester** — tills, plants, fertilizes, waters and harvests a planned area into storage.
- **Wireless** — link networks across locations, and carry a handheld terminal that works anywhere.
- **Shipping and income** — sell from storage, forecast income, plan expenses, and track daily earnings.
- **Multiplayer** — every player works on the same networks, kept in sync by the host.

Requires **Stardew Valley 1.6** and **SMAPI 4.0** or later. Built and tested on 1.6.15 / SMAPI 4.5.2.
No other mods are required.

---

## Contents

- [Getting started](#getting-started)
- [Items](#items)
- [The terminal](#the-terminal)
- [Storage](#storage)
- [Crafting](#crafting)
- [Autocrafting](#autocrafting)
- [Minimum stock](#minimum-stock)
- [Auto-Harvester](#auto-harvester)
- [Wireless](#wireless)
- [Shipping](#shipping)
- [Income](#income)
- [Multiplayer](#multiplayer)
- [Configuration](#configuration)
- [Console commands](#console-commands)
- [Compatibility](#compatibility)
- [Known limitations](#known-limitations)
- [Building](#building)
- [Project layout](#project-layout)

---

## Getting started

1. Craft **Logistics Cable** and lay it on the ground. Cable is a floor, so it can be walked on and objects can
   be placed on top of it.
2. Place chests and machines **on** or **directly beside** the cable. They join the network automatically.
3. Place a **Storage Terminal** or **Crafting Terminal** on or beside the cable and interact with it.

```
   [Chest][Chest][Chest]        chests on the cable
   =====================        cable (a floor tile)
   [Keg] [Keg] [Terminal]       machines and terminals on it too
```

Every connected cable tile, chest and machine forms one network. A network has no power or device limits;
`MaxNetworkSize` exists only as a safety cap.

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

All recipes are learned immediately while `UnlockAllRecipes` is on (the default).

---

## The terminal

The Storage Terminal shows the **Items**, **Farm**, **Storage**, **Network**, **Shipping** and **Income** tabs.
The Crafting Terminal and Wireless Terminal add **Craft**, **Auto**, **Jobs** and **Stock**.

| Tab | Purpose |
|---|---|
| Items | Browse, withdraw and deposit network storage. |
| Craft | Craft recipes from network storage. |
| Auto | Plan and queue autocrafting jobs. |
| Jobs | Monitor, speed up and cancel running jobs. |
| Stock | Manage minimum-stock rules. |
| Farm | Auto-Harvesters on the network and a live view of their fields. |
| Storage | Chest priorities and filters; connected machines and their state. |
| Network | Network statistics; channel control for the Wireless Terminal. |
| Shipping | Sell stored items through a connected shipping bin. |
| Income | Income forecast, history, expense planner and ledger. |

### Search

The search box and the **Type** and **Mod** filters apply to the Items, Craft, Auto and Shipping tabs. Terms
combine with AND.

```
blueberry        name contains "blueberry"
"iron bar"       exact phrase
#wine            context tag
@fish            category
~ridgeside       source mod
>500  <=2000     quantity (on the Craft tab: craftable batches)
!stone           negate any term
```

Source-mod detection matches item IDs against loaded mods using the 1.6 ID namespacing convention. Items with
unnamespaced IDs report as "Unknown".

---

## Storage

- Every **player chest** on or beside a cable is network storage, including Big Chests. Loot chests, Junimo
  Chests and Mini-Shipping Bins are never used for storage.
- Chests another player has open are skipped until they're closed.
- **Items tab:** click to take a stack, right-click to take one, shift-click to fill your bag. Click an item in
  your bag to store it; shift-click to store every stack of it. **Deposit All** stores everything except tools.
- Counts are abbreviated in the grid (`12.3K`, `4.5M`); hover for the exact number.

### Priorities and filters

Configured per chest on the **Storage** tab.

- **Priority:** higher-priority chests fill first and empty last.
- **Filter:** up to nine items in **Allow** or **Deny** mode. A chest with an Allow filter is dedicated
  storage: matching items go there before any general chest, regardless of priority.

Settings are stored on the chest, so they survive moving cable or rebuilding the network.

### Machines

Any machine on or beside a cable is part of the network. Finished output is collected into storage
automatically (`EnableMachineAutomation`). When a machine holding items is removed, its inputs or finished
output return to storage instead of being lost.

---

## Crafting

The **Craft** tab lists every recipe you know, crafted from network storage.

- Recipes you can't afford are dimmed. Hover a recipe to see each ingredient's have/need count.
- Click crafts one, shift-click crafts five, right-click opens a quantity dialog.
- The quantity box accepts arithmetic: `10*2`, `(3+4)*6`.
- Crafted items go into storage; anything that doesn't fit goes to your bag.

---

## Autocrafting

The **Auto** tab lists everything the network can make: known crafting recipes, plus the output of every
machine connected to the network. A blue corner marks machine-made items; a green corner marks crops grown on
automation tiles. Dimmed items are short of materials.

Selecting an item opens the planner, which shows the complete production tree before anything starts.

### Planning

- **Multi-step chains.** Ingredients are drawn from storage first, then crafted or processed as needed, down to
  `MaxCraftDepth` steps.
- **Machine choice.** When several machines can make a step (e.g. Furnace and Heavy Furnace), the work is
  split between them to minimize time. Click a step to choose a machine.
- **Max machines.** Limits how many machines each step may occupy. Defaults to every available machine.
- **Ingredient alternatives.** Recipes that accept more than one input (Duck Mayonnaise from a Duck Egg or a
  Golden Duck Egg) consider every option.
- **Quality.** Lowest-quality ingredients are used first, unless a higher quality needs fewer inputs *and*
  fewer machine-hours. Ingredients drawn from storage are listed per quality.
- **Flavoured goods.** Wine, juice, jelly, pickles, roe, honey and dried or smoked goods are planned by
  ingredient: Starfruit Wine and Parsnip Juice are distinct items.
- **Aging.** Items a Cask can age offer a target quality. The planner ages existing stock (best first) and
  produces the rest from scratch. Casks where aging isn't allowed are ignored.
- **Fairy Dust.** Optionally applied to machines that accept it, using dust from storage.
- **Crops.** Crops growing under Auto-Harvesters count as incoming stock and are reserved by the job. If a crop
  isn't stored or growing, the planner books free **automation tiles** and plants it, optionally with a chosen
  Speed-Gro.
- **Summary.** Total time (including crop growth), value of the finished items, and gold per day.
- **Shortfalls** name the missing item and why: not in storage, no machine on the network, no free automation
  tiles, or no time left in the season to grow it.

### Jobs

Queued jobs appear on the **Jobs** tab with progress, time remaining, value and current status.

- **Ingredients are reserved on queue.** They leave storage immediately and can't be taken by anything else.
- **Machines are claimed** while in use, and held in advance when an earlier step is still producing their
  input.
- **Waiting reasons** are specific: waiting for a free Keg, for an earlier step, or for crops to grow.
- **Fairy Dust** can be switched on for a running job. The button is hidden when storage has none.
- **Cancel** stops the job and returns its inputs from machines and its reserved items to storage.
- **Casks.** Striking a Cask a job is using returns the item to the job; aging continues in another Cask with
  progress kept. Casks placed after queuing are used as they appear.
- **Broken machines.** The job recovers the machine's inputs and reruns the batch elsewhere. If no suitable
  machine remains, the job cancels and refunds everything.

---

## Minimum stock

Keep at least a set amount of an item in storage. Open an item on the **Auto** tab, set the quantity and
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
- **Preview** shows what's currently growing; **Plan** opens the planning grid.

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

Connect a network to the farm's **Shipping Bin** (cable beside the building) or to a **Mini-Shipping Bin**.

- The **Shipping** tab lists stored items the shipping bin accepts. Items it doesn't accept are never exported.
- Click an item to open the sale window:
  - Set a quantity with the input box, **±1 / 10 / 50 / 100 / 1000**, or **±Max**.
  - Or enter a **Gold Target**: the quantity is calculated to reach it, rounded up. Targets above the stored
    value are flagged.
  - **Max Value** shows what the entire stock would sell for.
- Switch to **Shipping Bin** to see what's waiting to sell; click an item to return it to storage.
- Prices match the shipping bin exactly, including quality and professions such as Artisan.
- Mini-Shipping Bins have limited slots; anything that doesn't fit stays in storage.

---

## Income

The **Income** tab shows **Net worth** (sale value of everything in storage), **Income** per day, and
**Profit** per day once input expenses are set. Figures are color-coded by amount.

### Forecast

Projected income from everything on the network that's producing:

- **Machines:** output value ÷ processing time. A Keg making 3,150g Starfruit Wine every 7 days earns 450g/day.
- **Autocrafting machines:** count the batches their job has left.
- **Bee Houses, Crystalariums, Tappers:** repeat indefinitely.
- **Casks:** count only the value aging adds.
- **Crops under Auto-Harvesters:** guaranteed yield ÷ growth time. Regrowing and replanted crops repeat until
  their season ends; crops reserved by jobs are excluded.

Graph options: **Daily** bars or **Running Total** line; **7 / 28 / 112 Days**; **Linear / Log** scale;
**By Source** stacks each producer in its own color, with a legend to show or hide individual sources. The graph
is shaded by season. Hover for daily figures.

### History

The same graph built from recorded earnings, split into shipping and other income.

### Expenses

- **Planned Expenses:** purchases you're saving for. The tab shows how many days until they're covered at
  current profit, optionally counting gold on hand.
- **Input Expenses:** the unit cost of inputs such as seeds. Deducted from each producer's income to show
  profit and margin. A crop's cost is its seed cost divided by guaranteed yield. Shop prices (Pierre, JojaMart
  and others) can be applied with one click.

### Ledger

Each day's earnings, recorded overnight: shipping (itemized by item, quantity and gold) and other income
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

---

## Compatibility

- **Standalone.** No other mods are required.
- **Generic Mod Config Menu** (optional) adds an in-game settings page.
- **Even Better Artisan Good Icons** (optional) icons are used where installed.
- **Automate** is not supported. Both mods drive the same machines, which produces unexpected behavior; a
  warning is logged if both are installed.
- Modded machines and crops defined through the game's 1.6 data formats are supported.

---

## Known limitations

- **Jobs don't survive quitting.** Reserved items are returned to storage on the next load. Crops planted for
  a job keep growing and are harvested into storage; stock rules queue new jobs.
- **Machine products come out at normal quality.** Input quality isn't carried through to machine output.
- **Recipes needing extra inputs** beyond their main ingredient (other than fuel) aren't offered for
  autocrafting.
- **Tappers** are counted in the income forecast but not collected by the network. Crystalariums and other
  machines that restart when collected are also left alone.
- **Harvesting** grants Farming experience to the host.
- **Farmhands** see stored items from their own copy of the world, which updates less often for locations
  outside the farm and its buildings. All item changes still go through the host. Crafts made by farmhands
  don't count toward their crafting stats.

---

## Building

There is no prebuilt release. Build against your own copy of the game:

```bash
cd src/StardewLogistics
dotnet build -c Release
```

[`Pathoschild.Stardew.ModBuildConfig`](https://github.com/Pathoschild/SMAPI/blob/develop/docs/technical/mod-build-config.md)
locates the game folder and deploys the mod to `Mods/StardewLogistics`. For a non-standard install location,
set `GamePath` in the `.csproj` or a `stardewvalley.targets` file.

Spritesheets are generated by the scripts in `tools/`; run them from the repository root.

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
    HarvesterSettings.cs       Auto-Harvester area and plan
    HarvesterPlanCheck.cs      plan validation
    StockId.cs                 item identity including flavour
    ItemKey.cs, ItemFilter.cs, StockFilter.cs, ...
  Multiplayer/                 host requests, transfer inventories, state sync
  Integrations/                content registration, GMCM, Harmony patches (cask, soil, equipment slot)
  Menus/                       terminal tabs, planner, harvester, sale and expense windows
  assets/                      spritesheets
  i18n/default.json            all user-facing text
tools/                         spritesheet generators
```
