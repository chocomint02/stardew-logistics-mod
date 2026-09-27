using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.ItemTypeDefinitions;

namespace StardewLogistics.Framework
{
    /// <summary>Works out how to get from what's in storage to a requested item, making intermediates as needed.</summary>
    /// <remarks>
    /// Depth-first over the recipe graph, spending stock as it goes so the same iron bar can't be counted twice by
    /// two different branches. Depth is capped and the recursion guards against cycles, because recipe graphs in a
    /// modded game are not guaranteed to be acyclic — refining a bar back into ore is a real recipe pair.
    ///
    /// Planning deliberately ignores the player's own inventory. A job runs over minutes or days while the player
    /// is elsewhere, so a plan that counted on something in their bag would be wrong as soon as they walked off.
    /// </remarks>
    internal class CraftPlanner
    {
        /*********
        ** Fields
        *********/
        private readonly RecipeIndex Crafting;
        private readonly MachineRecipeIndex Machines;
        private readonly int MaxDepth;

        /// <summary>How many machines could run a given recipe, or <c>null</c> to assume every machine exists.</summary>
        /// <remarks>
        /// A count rather than a set of machine IDs, because whether a machine is usable depends on the recipe:
        /// a furnace told to refuse copper ore is available for iridium and not for copper.
        /// </remarks>
        private Func<MachineRecipe, int> CountUsable;

        /// <summary>Growing crops not yet planned on, soonest first.</summary>
        private List<IncomingCrop> Incoming = new();

        /// <summary>Automation tiles not yet planned on.</summary>
        private List<FreeTile> FreeTiles = new();

        /// <summary>The fertilizer to lay under crops the plan plants, or <c>null</c> for none.</summary>
        private string Fertilizer;

        /// <summary>Days to grow per seed, fertilizer and location, since working it out builds a throwaway crop.</summary>
        private readonly Dictionary<string, int?> GrowTimes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Item prices by stock ID, for ordering ingredients cheapest first.</summary>
        private static readonly Dictionary<string, int> PriceCache = new(StringComparer.OrdinalIgnoreCase);


        /*********
        ** Public methods
        *********/
        public CraftPlanner(RecipeIndex crafting, MachineRecipeIndex machines, int maxDepth)
        {
            this.Crafting = crafting;
            this.Machines = machines;
            this.MaxDepth = Math.Max(1, maxDepth);
        }

        /// <summary>Builds a plan for making a number of an item.</summary>
        /// <param name="targetId">The qualified item ID to make.</param>
        /// <param name="count">How many are wanted.</param>
        /// <param name="stock">The network's aggregated stock.</param>
        /// <param name="preferredMachines">The player's chosen machine per output item, keyed by qualified item ID.</param>
        /// <param name="countUsableMachines">How many machines could run a given recipe, taking into account both
        /// what the network has and any input filters set on them. When given, only recipes with at least one
        /// usable machine are planned with; a plan built around a machine the player doesn't own, or has told to
        /// refuse that input, can't be carried out.</param>
        /// <remarks>
        /// An order for five means produce five: stock already held never counts towards the requested item, or
        /// ordering five with five on the shelf would plan nothing and look broken. Intermediates always draw on
        /// stock, which is what the ledger is for. This is deliberately not a parameter -- it was one, and the
        /// two call sites that forgot to pass it produced a plan preview that disagreed with the queued job.
        /// </remarks>
        /// <param name="targetQuality">The quality wanted, which adds a cask step; <see cref="Quality.Any"/> for none.</param>
        /// <param name="incoming">Crops growing under auto-harvesters, which the plan can wait for once storage runs short.</param>
        /// <param name="freeTiles">Automation tiles with nothing growing, which the plan can plant crops on.</param>
        /// <param name="fertilizerId">The fertilizer to lay under what the plan plants, or <c>null</c> for none.</param>
        public CraftPlan Plan(string targetId, int count, IReadOnlyList<IFilterableEntry> stock, IReadOnlyDictionary<string, string> preferredMachines = null, Func<MachineRecipe, int> countUsableMachines = null, int targetQuality = Quality.Any, IReadOnlyList<IncomingCrop> incoming = null, IReadOnlyList<FreeTile> freeTiles = null, string fertilizerId = null)
        {
            Ledger ledger = new(stock);
            this.Incoming = incoming?.OrderBy(crop => crop.Days).ToList() ?? new List<IncomingCrop>();
            this.FreeTiles = freeTiles?.ToList() ?? new List<FreeTile>();
            this.Fertilizer = string.IsNullOrWhiteSpace(fertilizerId) ? null : fertilizerId;
            CraftPlan plan = new() { RequestedCount = count };
            this.CountUsable = countUsableMachines;

            // Make sure whatever is in storage has been tried in the machines, so a keg recipe for a fruit that
            // only just arrived is there to plan with.
            if (stock != null)
                this.Machines?.ExpandFor(stock.Select(entry => entry.Sample).Where(sample => sample != null));

            // The ordered item's own ingredient, when it's a flavour: Parsnip Juice has no recipe until a Parsnip
            // has been tried in a keg, and without one the plan couldn't say it's the Parsnip that's missing.
            string flavour = StockId.FlavourOf(targetId);
            if (flavour != null && ItemRegistry.Create(flavour, allowNull: true) is Item ingredient)
                this.Machines?.ExpandFor(new[] { ingredient });

            // Crops still growing count too: Starfruit ready in five days makes Starfruit Wine something to plan.
            this.Machines?.ExpandFor(this.Incoming.Select(crop => crop.ItemId).Distinct().Select(id => ItemRegistry.Create(id, allowNull: true)).Where(item => item != null));

            // And so do crops that could be planted: Starfruit Seeds and a free automation tile make Starfruit.
            if (this.FreeTiles.Count > 0 && stock != null)
            {
                this.Machines?.ExpandFor(stock
                    .Select(entry => CropMath.HarvestItemId(entry.Sample?.QualifiedItemId))
                    .Where(id => id != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(id => ItemRegistry.Create(id, allowNull: true))
                    .Where(item => item != null));
            }

            PlanNode root = targetQuality > 0
                ? this.PlanAging(targetId, count, targetQuality, ledger, preferredMachines, plan)
                : this.Resolve(targetId, count, ledger, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, preferredMachines, plan);
            return new CraftPlan { Root = root, RequestedCount = count, HitDepthLimit = plan.HitDepthLimit };
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Plans an item aged in casks to a quality: a cask step, fed from stock and from whatever makes it.</summary>
        /// <remarks>
        /// The cask step's inputs are this same item below the target quality. Stock is used best first -- a silver
        /// wine is fourteen days nearer iridium than a normal one, which is the opposite of how ingredients are
        /// spent elsewhere, and deliberately so. Stock already at or above the target isn't used: an order for
        /// five iridium wines makes five, the same rule as every other order. Whatever stock can't cover is made
        /// by the usual route -- Starfruit into a keg -- and aged from normal.
        /// </remarks>
        private PlanNode PlanAging(string itemId, int count, int targetQuality, Ledger ledger, IReadOnlyDictionary<string, string> preferred, CraftPlan plan)
        {
            PlanNode node = new()
            {
                ItemId = itemId,
                RequiredQuality = targetQuality,
                DisplayName = GetDisplayName(itemId),
                Requested = count,
                Depth = 0
            };

            MachineRecipe aging = this.Machines?.GetAgingRecipe(itemId, targetQuality);
            if (aging == null)
            {
                node.Kind = PlanStepKind.Missing;
                node.Missing = count;
                node.Reason = MissingReason.NoRecipe;
                return node;
            }

            if (this.CountUsable != null && this.CountUsable(aging) <= 0)
            {
                node.Kind = PlanStepKind.Missing;
                node.Missing = count;
                node.Reason = MissingReason.NoMachineAvailable;
                node.Alternatives = new[] { aging };
                return node;
            }

            node.Kind = PlanStepKind.Process;
            node.ToProduce = count;
            node.Alternatives = new[] { aging };
            node.Assignments.Add(new MachineAssignment { Recipe = aging, Runs = count });
            node.Batches = count;
            node.MinutesPerBatch = aging.Minutes;
            node.DaysPerBatch = aging.Days;

            // Stock below the target, best first.
            List<(int Quality, int Count)> parts = ledger.TakeParts(itemId, count, belowQuality: targetQuality, highestFirst: true);
            int taken = parts.Sum(part => part.Count);
            if (taken > 0)
            {
                AddChild(node, new PlanNode
                {
                    Kind = PlanStepKind.FromStock,
                    ItemId = itemId,
                    DisplayName = GetDisplayName(itemId),
                    Requested = taken,
                    FromStock = taken,
                    StockParts = parts,
                    Depth = 1
                });
            }

            // The rest is made. It mustn't be drawn from the stock just set aside as too good to age.
            if (count - taken > 0)
                AddChild(node, this.Resolve(itemId, count - taken, ledger, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 1, preferred, plan, useStock: false));

            return node;
        }

        /// <summary>Resolves how to supply a number of one item.</summary>
        /// <param name="useStock">Whether storage may supply this item, or it must be made.</param>
        private PlanNode Resolve(string itemId, int needed, Ledger ledger, HashSet<string> inProgress, int depth, IReadOnlyDictionary<string, string> preferred, CraftPlan plan, int quality = Quality.Any, bool useStock = true)
        {
            PlanNode node = new()
            {
                ItemId = itemId,
                RequiredQuality = quality,
                DisplayName = GetDisplayName(itemId),
                Requested = needed,
                Depth = depth
            };

            // Spend what's already in storage first. This is what stops two branches both planning around the
            // same hundred stone. The item being ordered can be exempt, so that an order for five produces five
            // rather than pointing at the five already on the shelf.
            List<(int Quality, int Count)> parts = depth == 0 || !useStock ? new() : ledger.TakeParts(itemId, needed, quality);
            int taken = parts.Sum(part => part.Count);
            node.FromStock = taken;
            node.StockParts = parts;

            int remaining = needed - taken;

            // A particular quality can only come from storage; nothing the network makes is better than normal.
            if (remaining > 0 && quality > 0)
            {
                node.Kind = PlanStepKind.Missing;
                node.Missing = remaining;
                node.Reason = MissingReason.NotEnoughStock;
                return node;
            }

            // Then crops still growing under a harvester, soonest first. A harvest can't be split, so whole tiles
            // are taken; anything a tile gives beyond what's needed goes back to storage when the job ends.
            if (remaining > 0 && depth > 0 && useStock && quality < 0)
            {
                foreach (IncomingCrop crop in this.Incoming.Where(crop => string.Equals(crop.ItemId, itemId, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    if (remaining <= 0)
                        break;

                    this.Incoming.Remove(crop);
                    node.Harvests.Add(crop);
                    node.FromHarvest += crop.Count;
                    node.HarvestDays = Math.Max(node.HarvestDays, crop.Days);
                    remaining -= crop.Count;
                }
                remaining = Math.Max(0, remaining);
            }
            if (remaining <= 0)
            {
                node.Kind = PlanStepKind.FromStock;
                return node;
            }

            // A recipe that asks for a category ("any egg") names a group, not something to go and make.
            if (!IsMakeable(itemId))
            {
                node.Kind = PlanStepKind.Missing;
                node.Missing = remaining;
                node.Reason = MissingReason.NotAnItem;
                return node;
            }

            if (depth >= this.MaxDepth)
            {
                plan.HitDepthLimit = true;
                node.Kind = PlanStepKind.Missing;
                node.Missing = remaining;
                node.Reason = MissingReason.DepthLimit;
                return node;
            }

            // Guard against recipe loops: if we're already partway through making this item further up the
            // chain, going round again would never terminate.
            if (!inProgress.Add(itemId))
            {
                node.Kind = PlanStepKind.Missing;
                node.Missing = remaining;
                node.Reason = MissingReason.RecipeLoop;
                return node;
            }

            try
            {
                if (!this.TryPlanProduction(node, itemId, remaining, ledger, inProgress, depth, preferred, plan, out MissingReason reason))
                {
                    // Nothing makes it, but it's a crop: plant it on free automation tiles. A harvest can't be
                    // split, so whole tiles are planted, and any extra goes back to storage when the job ends.
                    int grown = quality < 0 ? this.PlanGrowing(node, itemId, remaining, ledger, inProgress, depth, preferred, plan) : 0;
                    remaining -= grown;

                    if (remaining <= 0)
                        node.Kind = PlanStepKind.FromStock;
                    else
                    {
                        node.Kind = PlanStepKind.Missing;
                        node.Missing = remaining;

                        // A crop is short for want of somewhere to grow it, not just for want of stock.
                        bool growable = quality < 0 && CropMath.SeedsFor(itemId).Count > 0;
                        node.Reason = grown > 0 || (growable && this.FreeTiles.Count == 0)
                            ? MissingReason.NoFreeTiles
                            : growable
                                ? MissingReason.CantGrowInTime
                                : reason;
                    }
                }
            }
            finally
            {
                inProgress.Remove(itemId);
            }

            return node;
        }

        /// <summary>Plans a crop grown on free automation tiles, with its seed and any fertilizer as the node's children.</summary>
        /// <returns>How many the planted tiles are guaranteed to yield, which may be more than needed; zero if none can be grown.</returns>
        /// <remarks>
        /// One seed for the whole row: one in storage if there is one, otherwise the quickest. Tiles are taken
        /// quickest first -- a tile already fertilized with Speed-Gro beats a bare one -- and only where the crop will
        /// be ready before its season ends. The row waits as long as the slowest tile it uses.
        /// </remarks>
        private int PlanGrowing(PlanNode node, string itemId, int needed, Ledger ledger, HashSet<string> inProgress, int depth, IReadOnlyDictionary<string, string> preferred, CraftPlan plan)
        {
            if (this.FreeTiles.Count == 0 || needed <= 0)
                return 0;

            IEnumerable<string> seeds = CropMath.SeedsFor(itemId)
                .OrderByDescending(seed => ledger.Peek(seed) > 0)
                .ThenBy(seed => CropMath.GetData(seed)?.DaysInPhase?.Sum() ?? int.MaxValue);

            foreach (string seed in seeds)
            {
                int yield = CropMath.GuaranteedYield(seed);
                int wanted = (int)Math.Ceiling(needed / (double)yield);

                // With Speed-Gro chosen it's laid where a tile doesn't have it: added to what's there where a mod
                // lets fertilizers stack, and otherwise replacing whatever else a tile has -- Basic Fertilizer does
                // nothing for growth time -- unless it already has a Speed-Gro, which it keeps.
                var tiles = this.FreeTiles
                    .Select(tile => (Tile: tile, After: CropMath.FertilizerAfterLaying(tile.Fertilizer, this.Fertilizer, tile.Location, tile.Tile, keepSpeedGro: true)))
                    .Select(entry => (entry.Tile, AddsFertilizer: this.Fertilizer != null && !CropMath.HasFertilizer(entry.Tile.Fertilizer, this.Fertilizer) && CropMath.HasFertilizer(entry.After, this.Fertilizer), entry.After))
                    .Select(entry => (entry.Tile, entry.AddsFertilizer, Days: this.GrowDays(seed, entry.After, entry.Tile)))
                    .Where(entry => entry.Days != null)
                    .OrderBy(entry => entry.Days)
                    .Take(wanted)
                    .ToList();
                if (tiles.Count == 0)
                    continue;

                foreach ((FreeTile tile, bool addsFertilizer, int? days) in tiles)
                {
                    this.FreeTiles.Remove(tile);
                    node.Plantings.Add(new PlannedPlanting
                    {
                        Location = tile.Location,
                        Tile = tile.Tile,
                        HarvesterTile = tile.HarvesterTile,
                        SeedId = seed,
                        FertilizerId = addsFertilizer ? this.Fertilizer : null,
                        ItemId = itemId,
                        Count = yield,
                        Days = days.Value
                    });
                }

                int grown = tiles.Count * yield;
                node.FromHarvest += grown;
                node.HarvestDays = Math.Max(node.HarvestDays, tiles.Max(entry => entry.Days.Value));

                AddChild(node, this.Resolve(seed, tiles.Count, ledger, inProgress, depth + 1, preferred, plan));
                int fertilized = tiles.Count(entry => entry.AddsFertilizer);
                if (fertilized > 0)
                    AddChild(node, this.Resolve(this.Fertilizer, fertilized, ledger, inProgress, depth + 1, preferred, plan));

                return grown;
            }

            return 0;
        }

        /// <summary>Days a seed would take to grow on a free tile, or <c>null</c> if it can't grow there in time.</summary>
        private int? GrowDays(string seedId, string fertilizerId, FreeTile tile)
        {
            int window = CropMath.DaysLeftToGrow(seedId, tile.Location);
            if (window < 0)
                return null;

            string key = $"{seedId}|{fertilizerId}|{tile.Location?.NameOrUniqueName}";
            if (!this.GrowTimes.TryGetValue(key, out int? days))
                this.GrowTimes[key] = days = CropMath.DaysToGrow(seedId, fertilizerId, tile.Location, tile.Tile);

            if (days == null || (window != int.MaxValue && days > window))
                return null;

            return tile.Location.CanPlantSeedsHere(CropMath.Unqualify(seedId), (int)tile.Tile.X, (int)tile.Tile.Y, isGardenPot: false, out _)
                ? days
                : null;
        }

        /// <summary>Fills in a node with the best way to make the item: a crafting recipe, a machine, or either.</summary>
        /// <returns>Whether a producer was found.</returns>
        /// <remarks>
        /// Some things can be both crafted and made in a machine: an Iron Bar transmuted from copper bars at the
        /// workbench, or smelted from ore in a furnace. Where both are possible each is planned in turn, from the
        /// same starting point, and the better kept: the player's own choice first (a machine, or
        /// <see cref="CraftChoice"/>); then whichever can actually be supplied; and failing that, whichever falls
        /// shorter. Where both can be supplied, the whole branch counts, not just its last step -- transmuting an
        /// Iron Bar is instant, but the Copper Bars it takes need smelting first: the one with less machine time
        /// wins, then the one taking less value from storage, then crafting. The other's claims on storage, crops
        /// and tiles are undone.
        /// </remarks>
        private bool TryPlanProduction(PlanNode node, string itemId, int remaining, Ledger ledger, HashSet<string> inProgress, int depth, IReadOnlyDictionary<string, string> preferred, CraftPlan plan, out MissingReason reason)
        {
            // A recipe that takes in what it makes, or something further up the chain still being made, can't add
            // to the supply: it's left out, and the item planned another way or taken from storage.
            RecipeEntry craft = this.Crafting?.FindByOutput(itemId);
            MissingReason craftLoop = craft != null ? LoopKind(CraftInputs(craft), itemId, inProgress) : MissingReason.None;
            if (craftLoop != MissingReason.None)
                craft = null;
            bool hasMachines = this.Machines?.GetRecipesFor(itemId).Any(recipe => LoopKind(MachineInputs(recipe), itemId, inProgress) == MissingReason.None) == true;

            if (craft == null)
            {
                bool made = this.TryPlanProcessing(node, itemId, remaining, ledger, inProgress, depth, preferred, plan, out reason);
                if (!made && craftLoop != MissingReason.None && reason is MissingReason.NotEnoughStock or MissingReason.NoRecipe)
                    reason = craftLoop;
                return made;
            }
            if (!hasMachines)
            {
                reason = MissingReason.NoRecipe;
                return this.PlanCrafting(node, craft, itemId, remaining, ledger, inProgress, depth, preferred, plan);
            }

            // Both ways. Plan crafting first, and note where that leaves everything.
            PlannerState before = this.Capture(ledger, plan);
            PlanNode crafted = Blank(node);
            this.PlanCrafting(crafted, craft, itemId, remaining, ledger, inProgress, depth, preferred, plan);
            PlannerState afterCrafting = this.Capture(ledger, plan);

            // Then processing, from the same start.
            this.Restore(before, ledger, plan);
            PlanNode processed = Blank(node);
            bool canProcess = this.TryPlanProcessing(processed, itemId, remaining, ledger, inProgress, depth, preferred, plan, out MissingReason processReason);

            string choice = preferred != null && preferred.TryGetValue(itemId, out string chosen) ? chosen : null;
            bool craftChosen = string.Equals(choice, CraftChoice, StringComparison.OrdinalIgnoreCase);
            bool useProcessing = canProcess && !craftChosen && (
                choice != null
                || (!crafted.IsSatisfied && processed.IsSatisfied)
                || (crafted.IsSatisfied && processed.IsSatisfied && Cheaper(processed, crafted))
                || (!crafted.IsSatisfied && !processed.IsSatisfied && TotalMissing(processed) < TotalMissing(crafted)));

            reason = processReason;
            if (useProcessing)
            {
                CopyProduction(processed, node);
                node.CanCraftInstead = true;
                return true;
            }

            // Crafting it is: put back where crafting left things, and offer the machines on the network that could
            // do it instead.
            IReadOnlyList<MachineRecipe> machines = canProcess ? processed.Alternatives : new List<MachineRecipe>();
            this.Restore(afterCrafting, ledger, plan);
            CopyProduction(crafted, node);
            node.Alternatives = machines;
            return true;
        }

        /// <summary>The choice a player makes to have something crafted rather than made in a machine.</summary>
        public const string CraftChoice = "craft";

        /// <summary>Plans an item made at the workbench.</summary>
        private bool PlanCrafting(PlanNode node, RecipeEntry craft, string itemId, int remaining, Ledger ledger, HashSet<string> inProgress, int depth, IReadOnlyDictionary<string, string> preferred, CraftPlan plan)
        {
            int perBatch = Math.Max(1, craft.Recipe.numberProducedPerCraft);
            int batches = (int)Math.Ceiling(remaining / (double)perBatch);

            node.Kind = PlanStepKind.Craft;
            node.CraftRecipe = craft.Recipe;
            node.Batches = batches;
            node.ToProduce = remaining;

            foreach (KeyValuePair<string, int> ingredient in craft.Recipe.recipeList)
                AddChild(node, this.Resolve(NormaliseIngredient(ingredient.Key), ingredient.Value * batches, ledger, inProgress, depth + 1, preferred, plan));

            ledger.Give(itemId, (batches * perBatch) - remaining);
            return true;
        }

        /// <summary>Plans an item made in a machine.</summary>
        private bool TryPlanProcessing(PlanNode node, string itemId, int remaining, Ledger ledger, HashSet<string> inProgress, int depth, IReadOnlyDictionary<string, string> preferred, CraftPlan plan, out MissingReason reason)
        {
            reason = MissingReason.NoRecipe;

            IReadOnlyList<MachineRecipe> all = this.Machines?.GetRecipesFor(itemId) ?? Array.Empty<MachineRecipe>();
            if (all.Count == 0)
            {
                // Nothing produces it, and the player has no recipe either, so it can only come from stock.
                reason = MissingReason.NotEnoughStock;
                return false;
            }

            // Leave out machines that would need one of what they're making -- a Crystalarium copying a gem -- or
            // something further up the chain. If that's all of them, storage has to supply it.
            List<MachineRecipe> known = all.Where(recipe => LoopKind(MachineInputs(recipe), itemId, inProgress) == MissingReason.None).ToList();
            if (known.Count == 0)
            {
                reason = all.Any(recipe => LoopKind(MachineInputs(recipe), itemId, inProgress) == MissingReason.OnlyFromItself)
                    ? MissingReason.OnlyFromItself
                    : MissingReason.RecipeLoop;
                return false;
            }

            // Only plan around machines the network can actually run this on. Picking the Heavy Furnace on
            // throughput when the player owns none, or has told them all to refuse this input, produces a plan
            // that looks fine and can never run.
            List<MachineRecipe> options = this.CountUsable == null
                ? known.ToList()
                : known.Where(option => this.CountUsable(option) > 0).ToList();

            if (options.Count == 0)
            {
                // Record what could have done it, so the caller can name the machine the player is missing.
                node.Alternatives = known;
                reason = MissingReason.NoMachineAvailable;
                return false;
            }

            List<MachineAssignment> assignments = this.DistributeRuns(options, remaining, itemId, preferred, ledger);
            if (assignments.Count == 0)
            {
                reason = MissingReason.NoMachineAvailable;
                return false;
            }

            node.Kind = PlanStepKind.Process;
            node.Alternatives = options;
            node.ToProduce = remaining;
            node.Assignments.AddRange(assignments);
            node.Batches = assignments.Sum(assignment => assignment.Runs);
            node.MinutesPerBatch = assignments[0].Recipe.Minutes;
            node.DaysPerBatch = assignments[0].Recipe.Days;

            // Ingredients are summed across the shares, since the two machines want different amounts per run. A
            // share tied to a quality keeps it, so iridium wool isn't planned from the normal wool pile.
            List<ItemCost> totals = new();
            foreach (MachineAssignment assignment in assignments)
            {
                foreach (ItemCost input in assignment.Recipe.GetAllInputs())
                {
                    int index = totals.FindIndex(total => total.RequiredQuality == input.RequiredQuality && string.Equals(total.ItemId, input.ItemId, StringComparison.OrdinalIgnoreCase));
                    int add = input.Count * assignment.Runs;

                    if (index < 0)
                        totals.Add(new ItemCost(input.ItemId, add, input.RequiredQuality));
                    else
                        totals[index] = new ItemCost(totals[index].ItemId, totals[index].Count + add, input.RequiredQuality);
                }
            }

            // The main inputs the step could have used instead, for a short one to name: Plain Yogurt short of
            // Goat Milk would do just as well with cow's milk.
            HashSet<string> chosenInputs = new(assignments.Select(assignment => assignment.Recipe.InputId), StringComparer.OrdinalIgnoreCase);
            List<string> otherInputs = options
                .Select(option => option.InputId)
                .Where(id => id != null && !chosenInputs.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(GetPrice)
                .ToList();

            foreach (ItemCost total in totals)
            {
                PlanNode child = this.Resolve(total.ItemId, total.Count, ledger, inProgress, depth + 1, preferred, plan, total.RequiredQuality);
                if (child.Missing > 0 && chosenInputs.Contains(total.ItemId))
                    child.Substitutes = otherInputs;
                AddChild(node, child);
            }

            ledger.Give(itemId, assignments.Sum(assignment => assignment.Output) - remaining);
            return true;
        }

        /// <summary>A node for trying one way of making something: the same item, need and stock, with nothing under it yet.</summary>
        private static PlanNode Blank(PlanNode node)
        {
            PlanNode blank = new()
            {
                ItemId = node.ItemId,
                RequiredQuality = node.RequiredQuality,
                DisplayName = node.DisplayName,
                Requested = node.Requested,
                Depth = node.Depth,
                FromStock = node.FromStock,
                StockParts = node.StockParts,
                FromHarvest = node.FromHarvest,
                HarvestDays = node.HarvestDays
            };
            blank.Harvests.AddRange(node.Harvests);
            return blank;
        }

        /// <summary>Copies the way of making something that was chosen onto the node that asked.</summary>
        private static void CopyProduction(PlanNode from, PlanNode to)
        {
            to.Kind = from.Kind;
            to.CraftRecipe = from.CraftRecipe;
            to.Batches = from.Batches;
            to.ToProduce = from.ToProduce;
            to.MinutesPerBatch = from.MinutesPerBatch;
            to.DaysPerBatch = from.DaysPerBatch;
            to.Alternatives = from.Alternatives;
            to.Assignments.AddRange(from.Assignments);
            to.Children.AddRange(from.Children);
        }

        /// <summary>Whether one way of making something costs less than another: less machine time, then less value taken from storage.</summary>
        private static bool Cheaper(PlanNode candidate, PlanNode other)
        {
            int candidateTime = BranchMinutes(candidate);
            int otherTime = BranchMinutes(other);
            if (candidateTime != otherTime)
                return candidateTime < otherTime;

            return BranchValue(candidate) < BranchValue(other);
        }

        /// <summary>The machine time a branch takes, every step counted as if on one machine.</summary>
        private static int BranchMinutes(PlanNode node) => node.Walk().Where(each => each.Kind == PlanStepKind.Process).Sum(each => each.SequentialMinutes);

        /// <summary>The value of what a branch takes from storage.</summary>
        private static long BranchValue(PlanNode node) => node.Walk().Sum(each => (long)each.FromStock * GetPrice(each.ItemId));

        /// <summary>How many things a branch of a plan can't supply, all told.</summary>
        private static int TotalMissing(PlanNode node) => node.Walk().Sum(each => each.Missing);

        /// <summary>Everything planning a branch changes: what storage has left, the crops and tiles still free.</summary>
        private PlannerState Capture(Ledger ledger, CraftPlan plan)
        {
            return new PlannerState(ledger.Copy(), this.Incoming.ToList(), this.FreeTiles.ToList(), plan.HitDepthLimit);
        }

        /// <summary>Puts back what planning a branch changed.</summary>
        private void Restore(PlannerState state, Ledger ledger, CraftPlan plan)
        {
            ledger.CopyFrom(state.Ledger);
            this.Incoming = state.Incoming.ToList();
            this.FreeTiles = state.FreeTiles.ToList();
            plan.HitDepthLimit = state.HitDepthLimit;
        }

        /// <summary>A snapshot of what planning changes.</summary>
        private sealed record PlannerState(Ledger Ledger, List<IncomingCrop> Incoming, List<FreeTile> FreeTiles, bool HitDepthLimit);

        /// <summary>Shares a step's runs between the recipes that can do it: different machines, and different inputs.</summary>
        /// <remarks>
        /// A step can often be made more than one way. A Heavy Furnace and a plain one both smelt ore; a Mayonnaise
        /// Machine makes Duck Mayonnaise from a Duck Egg or a Golden Duck Egg. Every (machine, input) pairing is a
        /// candidate, and runs are handed out in three passes:
        ///
        ///  1. Whole runs from what storage already holds. Cheaper inputs go first, so a plain Duck Egg is used
        ///     before a rare one; among candidates sharing an input, the fewest machine-hours wins, which is what
        ///     puts the bulk on a Heavy Furnace and the remainder on a plain one.
        ///  2. If storage can't cover the order in whole runs, one run that overshoots, wasting least.
        ///  3. Whatever is still outstanding is planned on a single input -- one the network can make if there is
        ///     one, otherwise the cheapest -- so a shortfall names the ingredient a player would expect.
        ///
        /// The player's chosen machine sorts first regardless, which is what makes "heavy first, remainder to
        /// regular" a decision they can make rather than one the numbers make for them.
        /// </remarks>
        private List<MachineAssignment> DistributeRuns(IReadOnlyList<MachineRecipe> options, int needed, string itemId, IReadOnlyDictionary<string, string> preferred, Ledger ledger)
        {
            string preferredMachine = preferred != null && preferred.TryGetValue(itemId, out string chosen) ? chosen : null;
            bool IsPreferred(MachineRecipe recipe) => string.Equals(recipe.MachineId, preferredMachine, StringComparison.OrdinalIgnoreCase);

            // One recipe per machine, input and quality: two rules turning the same input into this on one machine
            // are the same choice, so keep the one that yields most.
            List<MachineRecipe> distinct = options
                .GroupBy(option => (Machine: option.MachineId.ToLowerInvariant(), Input: option.InputId?.ToLowerInvariant(), option.InputQuality))
                .Select(group => group.OrderByDescending(option => option.OutputCount).First())
                .ToList();

            // A recipe tied to a better quality is only worth using if it needs fewer ingredients for the same
            // output and fewer machine-hours; otherwise lower-quality stock does the job and the better stock is
            // kept. Iridium wool weaving two cloth a run passes; a quality that only improves the product doesn't.
            List<MachineRecipe> candidates = distinct
                .Where(option => option.InputQuality < 0 || IsWorthUsing(option, distinct))
                .OrderByDescending(IsPreferred)
                .ThenBy(InputCostPerItem)
                .ThenBy(MinutesPerItem)
                .ThenByDescending(option => option.OutputCount)
                .ToList();

            List<MachineAssignment> assignments = new();
            int remaining = needed;

            void Assign(MachineRecipe recipe, int runs)
            {
                MachineAssignment existing = assignments.FirstOrDefault(assignment => assignment.Recipe == recipe);
                if (existing != null)
                    existing.Runs += runs;
                else
                    assignments.Add(new MachineAssignment { Recipe = recipe, Runs = runs });
            }

            // What storage holds of each input, by quality, drawn down as candidates claim it. Two machines fed the
            // same input share one pool, so the furnace and the Heavy Furnace can't both count the same ore, and an
            // iridium-only recipe and an any-quality one can't both count the same iridium wool.
            Dictionary<string, SortedDictionary<int, long>> held = new(StringComparer.OrdinalIgnoreCase);
            foreach (MachineRecipe recipe in candidates)
            {
                if (recipe.InputId != null && !held.ContainsKey(recipe.InputId))
                    held[recipe.InputId] = ledger.PeekByQuality(recipe.InputId);
            }

            long Affordable(MachineRecipe recipe)
            {
                if (recipe.InputId == null || !held.TryGetValue(recipe.InputId, out SortedDictionary<int, long> pool))
                    return 0;

                long have = recipe.InputQuality >= 0
                    ? (pool.TryGetValue(recipe.InputQuality, out long exact) ? exact : 0)
                    : pool.Values.Sum();
                return have / Math.Max(1, recipe.InputCount);
            }

            // Takes a candidate's input out of the pool: its own quality, or the lowest first.
            void Spend(MachineRecipe recipe, long units)
            {
                SortedDictionary<int, long> pool = held[recipe.InputId];
                if (recipe.InputQuality >= 0)
                {
                    pool[recipe.InputQuality] -= units;
                    return;
                }

                foreach (int quality in pool.Keys.ToList())
                {
                    long take = Math.Min(units, pool[quality]);
                    pool[quality] -= take;
                    units -= take;
                    if (units <= 0)
                        break;
                }
            }

            // Pass 1: whole runs from stock.
            foreach (MachineRecipe recipe in candidates)
            {
                if (remaining <= 0)
                    break;

                int perRun = Math.Max(1, recipe.OutputCount);
                int runs = (int)Math.Min(remaining / perRun, Affordable(recipe));
                if (runs <= 0)
                    continue;

                Assign(recipe, runs);
                remaining -= runs * perRun;
                Spend(recipe, (long)runs * recipe.InputCount);
            }

            // Pass 2: an order too small for any stocked candidate's batch takes one run that overshoots.
            if (remaining > 0)
            {
                MachineRecipe filler = candidates
                    .Where(recipe => Affordable(recipe) > 0)
                    .OrderBy(recipe => Math.Max(0, Math.Max(1, recipe.OutputCount) - remaining))
                    .ThenBy(InputCostPerItem)
                    .ThenBy(MinutesPerItem)
                    .FirstOrDefault();

                if (filler != null)
                {
                    Assign(filler, 1);
                    remaining -= Math.Min(remaining, Math.Max(1, filler.OutputCount));
                    Spend(filler, filler.InputCount);
                }
            }

            // Pass 3: plan the rest on one input, which the step's children will then make or report missing. Only
            // any-quality recipes qualify: nothing the network makes comes out at a particular quality.
            List<MachineRecipe> open = candidates.Where(recipe => recipe.InputQuality < 0).ToList();
            if (open.Count == 0)
                open = candidates;

            if (remaining > 0 && open.Count > 0)
            {
                string input = open
                    .OrderByDescending(IsPreferred)
                    .ThenByDescending(recipe => this.CanMake(recipe.InputId))
                    .ThenBy(InputCostPerItem)
                    .First()
                    .InputId;

                List<MachineRecipe> group = open
                    .Where(recipe => string.Equals(recipe.InputId, input, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(IsPreferred)
                    .ThenBy(MinutesPerItem)
                    .ThenByDescending(recipe => recipe.OutputCount)
                    .ToList();

                foreach (MachineRecipe recipe in group)
                {
                    if (remaining <= 0)
                        break;

                    int perRun = Math.Max(1, recipe.OutputCount);
                    int runs = remaining / perRun; // whole runs only, so nothing is overproduced here
                    if (runs <= 0)
                        continue;

                    Assign(recipe, runs);
                    remaining -= runs * perRun;
                }

                if (remaining > 0)
                {
                    MachineRecipe filler = group
                        .OrderBy(recipe => Math.Max(0, Math.Max(1, recipe.OutputCount) - remaining))
                        .ThenBy(MinutesPerItem)
                        .First();

                    Assign(filler, 1);
                }
            }

            return assignments;
        }

        /// <summary>Adds an ingredient to a step, as one row per quality when it comes from storage in several.</summary>
        /// <remarks>
        /// Two Starfruit wanted, one normal and one iridium on the shelf: the plan shows both, each with its own
        /// star, rather than "2x Starfruit" that hides which ones go in. Each row is tied to its quality, so the
        /// job reserves exactly the Starfruit the plan showed. Only an ingredient drawn entirely from storage is
        /// split this way; one that's partly made keeps its single row.
        /// </remarks>
        private static void AddChild(PlanNode parent, PlanNode child)
        {
            // Part from storage and part from a harvest: storage's share splits by quality as usual, and the
            // harvest gets a row of its own, so it's clear what's on the shelf and what's still growing.
            if (child.Kind == PlanStepKind.FromStock && child.FromHarvest > 0)
            {
                PlanNode harvest = new()
                {
                    Kind = PlanStepKind.FromStock,
                    ItemId = child.ItemId,
                    DisplayName = child.DisplayName,
                    Requested = Math.Max(0, child.Requested - child.FromStock),
                    FromHarvest = child.FromHarvest,
                    HarvestDays = child.HarvestDays,
                    Harvests = child.Harvests,
                    Plantings = child.Plantings,
                    Depth = child.Depth
                };

                // The seeds and fertilizer for what's planted belong under the harvest row.
                harvest.Children.AddRange(child.Children);

                if (child.StockParts.Count > 0)
                {
                    child.Requested = child.FromStock;
                    child.FromHarvest = 0;
                    child.HarvestDays = 0;
                    child.Harvests = new List<IncomingCrop>();
                    child.Plantings = new List<PlannedPlanting>();
                    child.Children.Clear();
                    AddChild(parent, child);
                }

                parent.Children.Add(harvest);
                return;
            }

            bool fromStockOnly = child.Kind == PlanStepKind.FromStock && child.StockParts.Count > 0;
            if (!fromStockOnly)
            {
                parent.Children.Add(child);
                return;
            }

            if (child.StockParts.Count == 1)
            {
                child.RequiredQuality = child.StockParts[0].Quality;
                parent.Children.Add(child);
                return;
            }

            foreach ((int quality, int count) in child.StockParts)
            {
                parent.Children.Add(new PlanNode
                {
                    Kind = PlanStepKind.FromStock,
                    ItemId = child.ItemId,
                    RequiredQuality = quality,
                    DisplayName = child.DisplayName,
                    Requested = count,
                    FromStock = count,
                    StockParts = new List<(int, int)> { (quality, count) },
                    Depth = child.Depth
                });
            }
        }

        /// <summary>Whether a recipe tied to a better quality beats the any-quality recipe it would replace.</summary>
        /// <remarks>It must need fewer ingredients per item made and fewer machine-hours per item, both.</remarks>
        private static bool IsWorthUsing(MachineRecipe specific, IReadOnlyList<MachineRecipe> all)
        {
            MachineRecipe general = all.FirstOrDefault(other =>
                other.InputQuality < 0
                && string.Equals(other.MachineId, specific.MachineId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(other.InputId, specific.InputId, StringComparison.OrdinalIgnoreCase));

            if (general == null)
                return true;

            double specificInputs = specific.InputCount / (double)Math.Max(1, specific.OutputCount);
            double generalInputs = general.InputCount / (double)Math.Max(1, general.OutputCount);

            return specificInputs < generalInputs && MinutesPerItem(specific) < MinutesPerItem(general);
        }

        /// <summary>Whether the network could make an item rather than only take it from storage.</summary>
        private bool CanMake(string itemId)
        {
            if (!IsMakeable(itemId))
                return false;

            RecipeEntry craft = this.Crafting?.FindByOutput(itemId);
            return (craft != null && LoopKind(CraftInputs(craft), itemId, null) == MissingReason.None)
                || (this.Machines?.GetRecipesFor(itemId).Any(recipe => (this.CountUsable == null || this.CountUsable(recipe) > 0) && LoopKind(MachineInputs(recipe), itemId, null) == MissingReason.None) ?? false);
        }

        /// <summary>Whether a way of making something would loop: taking in the item itself, or something further up the chain that's still being made.</summary>
        /// <param name="inputs">What the way of making it takes in.</param>
        /// <param name="itemId">What it makes.</param>
        /// <param name="inProgress">What's being made further up the chain, if planning.</param>
        /// <returns><see cref="MissingReason.OnlyFromItself"/> if it takes in the item itself, <see cref="MissingReason.RecipeLoop"/> if something further up, otherwise <see cref="MissingReason.None"/>.</returns>
        private static MissingReason LoopKind(IEnumerable<string> inputs, string itemId, HashSet<string> inProgress)
        {
            MissingReason kind = MissingReason.None;
            foreach (string input in inputs)
            {
                if (string.IsNullOrEmpty(input))
                    continue;
                if (string.Equals(input, itemId, StringComparison.OrdinalIgnoreCase) || string.Equals(StockId.BaseId(input), StockId.BaseId(itemId), StringComparison.OrdinalIgnoreCase))
                    return MissingReason.OnlyFromItself;
                if (inProgress != null && inProgress.Contains(input))
                    kind = MissingReason.RecipeLoop;
            }
            return kind;
        }

        /// <summary>What a crafting recipe takes in, as item IDs.</summary>
        private static IEnumerable<string> CraftInputs(RecipeEntry craft) => craft.Recipe.recipeList.Keys.Select(NormaliseIngredient);

        /// <summary>What a machine recipe takes in, as item IDs.</summary>
        private static IEnumerable<string> MachineInputs(MachineRecipe recipe) => recipe.GetAllInputs().Select(input => input.ItemId);

        /// <summary>What a recipe's main input costs per item it makes, used to spend cheap ingredients before rare ones.</summary>
        /// <remarks>
        /// Only the main input counts. A furnace and a Heavy Furnace take the same ore at the same ratio, so this
        /// ties and leaves the choice between them to machine-hours, exactly as before inputs were compared.
        /// </remarks>
        private static double InputCostPerItem(MachineRecipe recipe)
        {
            return GetPrice(recipe.InputId) * Quality.PriceMultiplier(recipe.InputQuality) * Math.Max(1, recipe.InputCount) / Math.Max(1, recipe.OutputCount);
        }

        /// <summary>An item's base sell price, cached since sorting asks for it repeatedly.</summary>
        private static int GetPrice(string stockId)
        {
            if (string.IsNullOrEmpty(stockId))
                return 0;

            if (!PriceCache.TryGetValue(stockId, out int price))
            {
                try
                {
                    price = (StockId.Create(stockId) as StardewValley.Object)?.Price ?? 0;
                }
                catch
                {
                    price = 0;
                }

                PriceCache[stockId] = price;
            }

            return price;
        }

        /// <summary>Machine-minutes one item costs on a given recipe, which is what the split minimises.</summary>
        private static double MinutesPerItem(MachineRecipe recipe)
        {
            int minutes = recipe.Minutes + (recipe.Days * CraftPlan.MinutesPerDay);
            return minutes / (double)Math.Max(1, recipe.OutputCount);
        }

        /// <summary>Whether an ID names a specific item the planner could go and make.</summary>
        private static bool IsMakeable(string itemId)
        {
            // Category and tag specs name groups of items; they come from storage or not at all.
            return !string.IsNullOrWhiteSpace(itemId) && !StockId.IsSpec(itemId);
        }

        /// <summary>Normalises a crafting ingredient ID to its qualified form where it names a real item.</summary>
        private static string NormaliseIngredient(string ingredientId)
        {
            if (string.IsNullOrWhiteSpace(ingredientId) || ingredientId.StartsWith("-"))
                return ingredientId;

            try
            {
                return ItemRegistry.QualifyItemId(ingredientId) ?? ingredientId;
            }
            catch
            {
                return ingredientId;
            }
        }

        /// <summary>The display name for a stock ID, falling back to the ID itself.</summary>
        private static string GetDisplayName(string itemId) => StockId.GetDisplayName(itemId);


        /*********
        ** Nested types
        *********/
        /// <summary>Tracks what's left of the network's stock as a plan spends it.</summary>
        /// <remarks>
        /// A plan is hypothetical, so it can't consume the real chests. This is a running tally that starts from
        /// the aggregated stock and is drawn down as branches claim things, which is what keeps two branches from
        /// both planning to use the same materials.
        ///
        /// Keyed by <see cref="StockId"/>, so Starfruit Wine and Blueberry Wine are counted apart, and by quality
        /// within that. A request for a plain ID draws on every flavour; a request for any quality draws the
        /// lowest first, so a gold Starfruit is only spent once the normal ones are gone.
        /// </remarks>
        private class Ledger
        {
            private readonly Dictionary<string, SortedDictionary<int, long>> Available = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>An item for each stock ID, to match category and tag specs against.</summary>
            private readonly Dictionary<string, Item> Samples = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>A copy, for trying one way of making something and undoing it.</summary>
            public Ledger Copy()
            {
                Ledger copy = new(null);
                copy.CopyFrom(this);
                return copy;
            }

            /// <summary>Makes this ledger match another.</summary>
            public void CopyFrom(Ledger other)
            {
                this.Available.Clear();
                foreach ((string id, SortedDictionary<int, long> counts) in other.Available)
                    this.Available[id] = new SortedDictionary<int, long>(counts);

                this.Samples.Clear();
                foreach ((string id, Item sample) in other.Samples)
                    this.Samples[id] = sample;
            }

            public Ledger(IReadOnlyList<IFilterableEntry> stock)
            {
                if (stock == null)
                    return;

                foreach (IFilterableEntry entry in stock)
                {
                    string id = StockId.Of(entry.Sample);
                    if (string.IsNullOrEmpty(id))
                        continue;

                    this.Samples.TryAdd(id, entry.Sample);

                    SortedDictionary<int, long> bucket = this.Bucket(id);
                    int quality = entry.Sample.Quality;
                    bucket[quality] = bucket.TryGetValue(quality, out long existing) ? existing + entry.Count : entry.Count;
                }
            }

            /// <summary>How many of an item there are at each quality, without claiming any.</summary>
            /// <remarks>Counts the same way <see cref="Take"/> draws: a plain ID includes every flavour.</remarks>
            public SortedDictionary<int, long> PeekByQuality(string itemId)
            {
                SortedDictionary<int, long> merged = new();
                foreach (string key in this.KeysFor(itemId))
                {
                    foreach ((int quality, long count) in this.Available[key])
                    {
                        if (count > 0)
                            merged[quality] = merged.TryGetValue(quality, out long have) ? have + count : count;
                    }
                }

                return merged;
            }

            /// <summary>How many of an item could be claimed right now, at a quality or at any.</summary>
            public long Peek(string itemId, int quality = Quality.Any)
            {
                SortedDictionary<int, long> byQuality = this.PeekByQuality(itemId);
                return quality >= 0
                    ? (byQuality.TryGetValue(quality, out long exact) ? exact : 0)
                    : byQuality.Values.Sum();
            }

            /// <summary>Claims up to a number of an item, returning how many were actually available.</summary>
            /// <param name="itemId">The stock ID. A plain ID accepts any flavour.</param>
            /// <param name="wanted">How many to claim.</param>
            /// <param name="quality">The quality required, or <see cref="Quality.Any"/> to take the lowest first.</param>
            public int Take(string itemId, int wanted, int quality = Quality.Any)
            {
                return this.TakeParts(itemId, wanted, quality).Sum(part => part.Count);
            }

            /// <summary>Claims up to a number of an item, returning how many were taken at each quality.</summary>
            /// <param name="itemId">The stock ID. A plain ID accepts any flavour.</param>
            /// <param name="wanted">How many to claim.</param>
            /// <param name="quality">The quality required, or <see cref="Quality.Any"/>.</param>
            /// <param name="belowQuality">Only claim items below this quality.</param>
            /// <param name="highestFirst">Claim the best first, rather than the lowest.</param>
            public List<(int Quality, int Count)> TakeParts(string itemId, int wanted, int quality = Quality.Any, int belowQuality = int.MaxValue, bool highestFirst = false)
            {
                List<(int Quality, int Count)> parts = new();
                if (wanted <= 0 || itemId == null)
                    return parts;

                // Every (flavour, quality) the request could draw on, lowest quality first. The sort is stable,
                // so within a quality the exact ID still comes before its flavoured variants.
                var candidates = this.KeysFor(itemId)
                    .SelectMany(key => this.Available[key]
                        .Where(pair => pair.Value > 0 && (quality < 0 || pair.Key == quality) && pair.Key < belowQuality)
                        .Select(pair => (Key: key, Quality: pair.Key)));
                var slots = (highestFirst ? candidates.OrderByDescending(slot => slot.Quality) : candidates.OrderBy(slot => slot.Quality)).ToList();

                int taken = 0;
                foreach ((string key, int slotQuality) in slots)
                {
                    long have = this.Available[key][slotQuality];
                    int take = (int)Math.Min(wanted - taken, have);
                    if (take <= 0)
                        continue;

                    this.Available[key][slotQuality] = have - take;
                    taken += take;

                    // Flavours of one quality merge into one part: the row is per quality, not per flavour.
                    int index = parts.FindIndex(part => part.Quality == slotQuality);
                    if (index < 0)
                        parts.Add((slotQuality, take));
                    else
                        parts[index] = (slotQuality, parts[index].Count + take);

                    if (taken >= wanted)
                        break;
                }

                return parts;
            }

            /// <summary>Returns surplus to the tally, so a later branch can use what this one overproduced.</summary>
            /// <remarks>What a plan makes is counted as normal quality, which is all it can promise.</remarks>
            public void Give(string itemId, int count)
            {
                if (itemId == null || count <= 0)
                    return;

                SortedDictionary<int, long> bucket = this.Bucket(itemId);
                bucket[0] = bucket.TryGetValue(0, out long have) ? have + count : count;
            }

            /// <summary>The per-quality tally for exactly one stock ID, created if missing.</summary>
            private SortedDictionary<int, long> Bucket(string id)
            {
                if (!this.Available.TryGetValue(id, out SortedDictionary<int, long> bucket))
                    this.Available[id] = bucket = new SortedDictionary<int, long>();
                return bucket;
            }

            /// <summary>The stock IDs a request can draw on: the exact one, plus every flavour of a plain one.</summary>
            private IEnumerable<string> KeysFor(string itemId)
            {
                if (itemId == null)
                    yield break;

                // A spec draws on every item it matches, cheapest first so the valuable ones are kept.
                if (StockId.IsSpec(itemId))
                {
                    foreach (string key in this.Samples.Where(pair => StockId.Matches(pair.Value, itemId)).OrderBy(pair => GetPrice(pair.Key)).Select(pair => pair.Key))
                        yield return key;
                    yield break;
                }

                if (this.Available.ContainsKey(itemId))
                    yield return itemId;

                if (StockId.IsFlavoured(itemId))
                    yield break;

                foreach (string key in this.Available.Keys)
                {
                    if (StockId.IsFlavoured(key) && string.Equals(StockId.BaseId(key), itemId, StringComparison.OrdinalIgnoreCase))
                        yield return key;
                }
            }
        }
    }
}
