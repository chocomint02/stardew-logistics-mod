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

        /// <summary>Machines wired to the network, or <c>null</c> to plan with any machine in the game.</summary>
        private HashSet<string> Available;


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
        /// <param name="availableMachines">The qualified IDs of machines wired to the network. When given, only
        /// those are planned with; a plan built around a machine the player doesn't own can't be carried out.</param>
        /// <remarks>
        /// An order for five means produce five: stock already held never counts towards the requested item, or
        /// ordering five with five on the shelf would plan nothing and look broken. Intermediates always draw on
        /// stock, which is what the ledger is for. This is deliberately not a parameter -- it was one, and the
        /// two call sites that forgot to pass it produced a plan preview that disagreed with the queued job.
        /// </remarks>
        public CraftPlan Plan(string targetId, int count, IReadOnlyList<IFilterableEntry> stock, IReadOnlyDictionary<string, string> preferredMachines = null, IReadOnlyCollection<string> availableMachines = null)
        {
            Ledger ledger = new(stock);
            CraftPlan plan = new() { RequestedCount = count };
            this.Available = availableMachines == null ? null : new HashSet<string>(availableMachines, StringComparer.OrdinalIgnoreCase);

            PlanNode root = this.Resolve(targetId, count, ledger, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, preferredMachines, plan);
            return new CraftPlan { Root = root, RequestedCount = count, HitDepthLimit = plan.HitDepthLimit };
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Resolves how to supply a number of one item.</summary>
        private PlanNode Resolve(string itemId, int needed, Ledger ledger, HashSet<string> inProgress, int depth, IReadOnlyDictionary<string, string> preferred, CraftPlan plan)
        {
            PlanNode node = new()
            {
                ItemId = itemId,
                DisplayName = GetDisplayName(itemId),
                Requested = needed,
                Depth = depth
            };

            // Spend what's already in storage first. This is what stops two branches both planning around the
            // same hundred stone. The item being ordered can be exempt, so that an order for five produces five
            // rather than pointing at the five already on the shelf.
            int taken = depth == 0 ? 0 : ledger.Take(itemId, needed);
            node.FromStock = taken;

            int remaining = needed - taken;
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
                    node.Kind = PlanStepKind.Missing;
                    node.Missing = remaining;
                    node.Reason = reason;
                }
            }
            finally
            {
                inProgress.Remove(itemId);
            }

            return node;
        }

        /// <summary>Fills in a node with whichever recipe can make the item, crafting preferred over processing.</summary>
        /// <returns>Whether a producer was found.</returns>
        private bool TryPlanProduction(PlanNode node, string itemId, int remaining, Ledger ledger, HashSet<string> inProgress, int depth, IReadOnlyDictionary<string, string> preferred, CraftPlan plan, out MissingReason reason)
        {
            reason = MissingReason.NoRecipe;

            // Crafting is instant and needs no machine, so it wins when both are possible.
            RecipeEntry craft = this.Crafting?.FindByOutput(itemId);
            if (craft != null)
            {
                int perBatch = Math.Max(1, craft.Recipe.numberProducedPerCraft);
                int batches = (int)Math.Ceiling(remaining / (double)perBatch);

                node.Kind = PlanStepKind.Craft;
                node.CraftRecipe = craft.Recipe;
                node.Batches = batches;
                node.ToProduce = remaining;

                foreach (KeyValuePair<string, int> ingredient in craft.Recipe.recipeList)
                    node.Children.Add(this.Resolve(NormaliseIngredient(ingredient.Key), ingredient.Value * batches, ledger, inProgress, depth + 1, preferred, plan));

                ledger.Give(itemId, (batches * perBatch) - remaining);
                return true;
            }

            IReadOnlyList<MachineRecipe> known = this.Machines?.GetRecipesFor(itemId) ?? Array.Empty<MachineRecipe>();
            if (known.Count == 0)
            {
                // Nothing produces it, and the player has no recipe either, so it can only come from stock.
                reason = MissingReason.NotEnoughStock;
                return false;
            }

            // Only plan around machines the network actually has. Picking the Heavy Furnace on throughput when
            // the player owns none produces a plan that looks fine and can never run.
            List<MachineRecipe> options = this.Available == null
                ? known.ToList()
                : known.Where(option => this.Available.Contains(option.MachineId)).ToList();

            if (options.Count == 0)
            {
                // Record what could have done it, so the caller can name the machine the player is missing.
                node.Alternatives = known;
                reason = MissingReason.NoMachineAvailable;
                return false;
            }

            List<MachineAssignment> assignments = DistributeRuns(options, remaining, itemId, preferred);
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

            // Ingredients are summed across the shares, since the two machines want different amounts per run.
            Dictionary<string, int> totals = new(StringComparer.OrdinalIgnoreCase);
            foreach (MachineAssignment assignment in assignments)
            {
                foreach (ItemCost input in assignment.Recipe.GetAllInputs())
                {
                    totals[input.ItemId] = totals.TryGetValue(input.ItemId, out int running)
                        ? running + (input.Count * assignment.Runs)
                        : input.Count * assignment.Runs;
                }
            }

            foreach (KeyValuePair<string, int> total in totals)
                node.Children.Add(this.Resolve(total.Key, total.Value, ledger, inProgress, depth + 1, preferred, plan));

            ledger.Give(itemId, assignments.Sum(assignment => assignment.Output) - remaining);
            return true;
        }

        /// <summary>Shares a step's runs between the machine types that can do it.</summary>
        /// <remarks>
        /// Optimises for the fewest machine-hours without overproducing. Each machine is offered as many whole
        /// runs as fit in what is still needed, cheapest per item first, so a Heavy Furnace takes the bulk and a
        /// plain one mops up the remainder. Only if something is still outstanding after that -- an order too
        /// small for any bulk machine's batch -- does a single run that overshoots get added, and then it is the
        /// one that wastes least.
        ///
        /// The player's chosen machine sorts first regardless of efficiency, which is what makes "heavy first,
        /// remainder to regular" a decision they can make rather than one the numbers make for them.
        /// </remarks>
        private static List<MachineAssignment> DistributeRuns(IReadOnlyList<MachineRecipe> options, int needed, string itemId, IReadOnlyDictionary<string, string> preferred)
        {
            string preferredMachine = preferred != null && preferred.TryGetValue(itemId, out string chosen) ? chosen : null;

            // One recipe per machine: two rules on the same furnace are the same furnace's time.
            List<MachineRecipe> candidates = options
                .GroupBy(option => option.MachineId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(option => option.OutputCount).First())
                .OrderByDescending(option => string.Equals(option.MachineId, preferredMachine, StringComparison.OrdinalIgnoreCase))
                .ThenBy(MinutesPerItem)
                .ThenByDescending(option => option.OutputCount)
                .ToList();

            List<MachineAssignment> assignments = new();
            int remaining = needed;

            foreach (MachineRecipe recipe in candidates)
            {
                if (remaining <= 0)
                    break;

                int perRun = Math.Max(1, recipe.OutputCount);
                int runs = remaining / perRun; // whole runs only, so nothing is overproduced here
                if (runs <= 0)
                    continue;

                assignments.Add(new MachineAssignment { Recipe = recipe, Runs = runs });
                remaining -= runs * perRun;
            }

            if (remaining > 0 && candidates.Count > 0)
            {
                MachineRecipe filler = candidates
                    .OrderBy(option => Math.Max(0, Math.Max(1, option.OutputCount) - remaining))
                    .ThenBy(MinutesPerItem)
                    .First();

                MachineAssignment existing = assignments.FirstOrDefault(assignment => assignment.Recipe == filler);
                if (existing != null)
                    existing.Runs++;
                else
                    assignments.Add(new MachineAssignment { Recipe = filler, Runs = 1 });
            }

            return assignments;
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
            // Category ingredients come through as bare negative numbers, which aren't items.
            return !string.IsNullOrWhiteSpace(itemId) && !itemId.StartsWith("-");
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

        /// <summary>The display name for an item ID, falling back to the ID itself.</summary>
        private static string GetDisplayName(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                return "?";

            try
            {
                // A category ingredient has no item to name, so the raw ID stands in until the UI can label it.
                if (itemId.StartsWith("-"))
                    return itemId;

                ParsedItemData data = ItemRegistry.GetData(itemId);
                return data?.DisplayName ?? itemId;
            }
            catch
            {
                return itemId;
            }
        }


        /*********
        ** Nested types
        *********/
        /// <summary>Tracks what's left of the network's stock as a plan spends it.</summary>
        /// <remarks>
        /// A plan is hypothetical, so it can't consume the real chests. This is a running tally that starts from
        /// the aggregated stock and is drawn down as branches claim things, which is what keeps two branches from
        /// both planning to use the same materials.
        /// </remarks>
        private class Ledger
        {
            private readonly Dictionary<string, long> Available = new(StringComparer.OrdinalIgnoreCase);

            public Ledger(IReadOnlyList<IFilterableEntry> stock)
            {
                if (stock == null)
                    return;

                foreach (IFilterableEntry entry in stock)
                {
                    string id = entry.Sample?.QualifiedItemId;
                    if (string.IsNullOrEmpty(id))
                        continue;

                    this.Available[id] = this.Available.TryGetValue(id, out long existing)
                        ? existing + entry.Count
                        : entry.Count;
                }
            }

            /// <summary>Claims up to a number of an item, returning how many were actually available.</summary>
            public int Take(string itemId, int wanted)
            {
                if (wanted <= 0 || itemId == null || !this.Available.TryGetValue(itemId, out long have) || have <= 0)
                    return 0;

                int taken = (int)Math.Min(wanted, have);
                this.Available[itemId] = have - taken;
                return taken;
            }

            /// <summary>Returns surplus to the tally, so a later branch can use what this one overproduced.</summary>
            public void Give(string itemId, int count)
            {
                if (itemId == null || count <= 0)
                    return;

                this.Available[itemId] = this.Available.TryGetValue(itemId, out long have) ? have + count : count;
            }
        }
    }
}
