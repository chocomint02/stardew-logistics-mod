using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.ItemTypeDefinitions;

namespace StardewLogistics.Framework
{
    /// <summary>Derives the processing recipes available from <c>Data/Machines</c>.</summary>
    /// <remarks>
    /// The game has no list of "what can a furnace make"; it has machine definitions that are evaluated when an
    /// item is dropped in. This walks those definitions and inverts them into recipes the planner can reason
    /// about ahead of time.
    ///
    /// Rules are only indexed where the outcome is predictable from the data alone. Three kinds are skipped:
    /// outputs computed by a C# <c>OutputMethod</c>, outputs chosen at random from a list, and flavoured outputs
    /// whose identity comes from the input (a keg's wine, a preserves jar's jam). Including those would let the
    /// planner promise a specific item it has no way to guarantee.
    /// </remarks>
    internal class MachineRecipeIndex
    {
        /*********
        ** Fields
        *********/
        /// <summary>Recipes by the qualified item ID they produce.</summary>
        private readonly Dictionary<string, List<MachineRecipe>> ByOutput = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every indexed recipe.</summary>
        private readonly List<MachineRecipe> AllRecipes = new();


        /*********
        ** Accessors
        *********/
        /// <summary>Every processing recipe known to the mod.</summary>
        public IReadOnlyList<MachineRecipe> All => this.AllRecipes;

        /// <summary>How many machine definitions were skipped because their output isn't predictable.</summary>
        public int SkippedRules { get; private set; }

        /// <summary>Counters recorded during a rebuild, so an empty index says where it lost everything.</summary>
        public int MachinesWithRules { get; private set; }
        public int RulesSeen { get; private set; }
        public int RulesWithPlacedTrigger { get; private set; }


        /*********
        ** Public methods
        *********/
        /// <summary>Rebuilds the index from the current machine data.</summary>
        public void Rebuild()
        {
            this.ByOutput.Clear();
            this.AllRecipes.Clear();
            this.SkippedRules = 0;
            this.MachinesWithRules = 0;
            this.RulesSeen = 0;
            this.RulesWithPlacedTrigger = 0;

            Dictionary<string, MachineData> machines;
            try
            {
                machines = DataLoader.Machines(Game1.content);
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't read Data/Machines, so processing recipes are unavailable.", ex);
                return;
            }

            foreach ((string machineId, MachineData data) in machines)
            {
                // Deliberately not filtering on HasInput/HasOutput. Both default to false and Data/Machines
                // leaves them unset for most entries -- they are display hints, not a statement about whether
                // the machine processes anything. Filtering on them rejected every machine.
                if (data?.OutputRules == null)
                    continue;

                this.MachinesWithRules++;
                string machineName = GetDisplayName(machineId);
                List<ItemCost> extras = ReadExtraInputs(data);

                foreach (MachineOutputRule rule in data.OutputRules)
                    this.IndexRule(machineId, machineName, extras, rule);
            }

            this.LogSummary(machines.Count);
        }

        /// <summary>Logs what the index found, with a sample big enough to tell whether it read the data correctly.</summary>
        /// <remarks>
        /// The recipes with the largest input counts are the interesting ones: they are the bulk machines like the
        /// Heavy Furnace, and they are where a misread of the trigger's required count or the output's stack range
        /// would show up first.
        /// </remarks>
        private void LogSummary(int machineCount)
        {
            int variable = this.AllRecipes.Count(recipe => recipe.HasVariableYield);
            Log.Trace($"Indexed {this.AllRecipes.Count} processing recipes across {machineCount} machines "
                + $"({this.SkippedRules} rules skipped as unpredictable, {variable} with a variable yield).");
            Log.Trace($"  breakdown: {this.MachinesWithRules} machines had rules, {this.RulesSeen} rules seen, "
                + $"{this.RulesWithPlacedTrigger} had an item-placed trigger.");

            foreach (MachineRecipe recipe in this.AllRecipes.OrderByDescending(recipe => recipe.InputCount).Take(6))
            {
                string inputs = string.Join(" + ", recipe.GetAllInputs().Select(input => $"{input.Count}x {input.ItemId}"));
                string yield = recipe.HasVariableYield
                    ? $"{recipe.OutputCount}-{recipe.MaxOutputCount}"
                    : recipe.OutputCount.ToString();

                Log.Trace($"  sample: {recipe.MachineName} | {inputs} -> {yield}x {recipe.OutputId} | {recipe.Minutes}m");
            }
        }

        /// <summary>Returns the ways an item can be produced by a machine, best throughput first.</summary>
        public IReadOnlyList<MachineRecipe> GetRecipesFor(string qualifiedItemId)
        {
            return this.ByOutput.TryGetValue(qualifiedItemId ?? "", out List<MachineRecipe> found)
                ? found
                : Array.Empty<MachineRecipe>();
        }

        /// <summary>Whether any machine can produce an item.</summary>
        public bool CanProduce(string qualifiedItemId) => this.GetRecipesFor(qualifiedItemId).Count > 0;


        /*********
        ** Private methods
        *********/
        /// <summary>Indexes every usable input/output pairing in one rule.</summary>
        private void IndexRule(string machineId, string machineName, List<ItemCost> extras, MachineOutputRule rule)
        {
            if (rule?.Triggers == null || rule.OutputItem == null)
                return;

            this.RulesSeen++;

            // Only the "player put an item in" trigger describes something the network can cause to happen.
            // Day-update and put-down triggers fire on their own and can't be scheduled.
            List<MachineOutputTriggerRule> triggers = rule.Triggers
                .Where(trigger => trigger != null
                    && trigger.Trigger.HasFlag(MachineOutputTrigger.ItemPlacedInMachine)
                    && !string.IsNullOrWhiteSpace(trigger.RequiredItemId))
                .ToList();

            if (triggers.Count == 0)
                return;

            this.RulesWithPlacedTrigger++;

            foreach (MachineItemOutput output in rule.OutputItem)
            {
                if (!IsPredictable(output))
                {
                    this.SkippedRules++;
                    continue;
                }

                string outputId = Qualify(output.ItemId);
                if (outputId == null)
                    continue;

                foreach (MachineOutputTriggerRule trigger in triggers)
                {
                    string inputId = Qualify(trigger.RequiredItemId);
                    if (inputId == null)
                        continue;

                    MachineRecipe recipe = new()
                    {
                        MachineId = machineId,
                        MachineName = machineName,
                        InputId = inputId,
                        InputCount = Math.Max(1, trigger.RequiredCount),
                        ExtraInputs = extras,
                        OutputId = outputId,
                        OutputCount = GuaranteedOutput(output),
                        MaxOutputCount = Math.Max(GuaranteedOutput(output), output.MaxStack > 0 ? output.MaxStack : GuaranteedOutput(output)),
                        Minutes = Math.Max(0, rule.MinutesUntilReady),
                        Days = Math.Max(0, rule.DaysUntilReady)
                    };

                    this.AllRecipes.Add(recipe);

                    if (!this.ByOutput.TryGetValue(outputId, out List<MachineRecipe> list))
                        this.ByOutput[outputId] = list = new List<MachineRecipe>();
                    list.Add(recipe);
                }
            }
        }

        /// <summary>Whether an output's identity and size can be known without running the machine.</summary>
        private static bool IsPredictable(MachineItemOutput output)
        {
            if (output == null || string.IsNullOrWhiteSpace(output.ItemId))
                return false;

            // Computed in C#, so there is nothing to read here.
            if (!string.IsNullOrWhiteSpace(output.OutputMethod))
                return false;

            // One of several possible items, chosen when the machine runs.
            if (output.RandomItemId is { Count: > 0 })
                return false;

            // A flavoured output — a keg's wine, a preserves jar's jam — is really "the input, transformed", so
            // the item ID alone doesn't identify what comes out.
            if (!string.IsNullOrWhiteSpace(output.PreserveId))
                return false;

            return true;
        }

        /// <summary>The smallest stack a run can produce.</summary>
        private static int GuaranteedOutput(MachineItemOutput output)
        {
            if (output.MinStack > 0)
                return output.MinStack;

            // An unset range means a single item; an unset minimum with a set maximum still only promises one.
            return 1;
        }

        /// <summary>Reads the items a machine consumes on top of its main input.</summary>
        private static List<ItemCost> ReadExtraInputs(MachineData data)
        {
            List<ItemCost> extras = new();
            if (data.AdditionalConsumedItems == null)
                return extras;

            foreach (MachineItemAdditionalConsumedItems extra in data.AdditionalConsumedItems)
            {
                string id = Qualify(extra?.ItemId);
                if (id != null)
                    extras.Add(new ItemCost(id, Math.Max(1, extra.RequiredCount)));
            }

            return extras;
        }

        /// <summary>Normalises an item ID to its qualified form, or <c>null</c> if it isn't a plain item.</summary>
        /// <remarks>Category and tag requirements come through here as unqualifiable, and are left out: the planner
        /// needs to name a specific item to go and make.</remarks>
        private static string Qualify(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                return null;

            try
            {
                return ItemRegistry.QualifyItemId(itemId);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The display name for an item ID, falling back to the ID itself.</summary>
        private static string GetDisplayName(string qualifiedId)
        {
            try
            {
                ParsedItemData data = ItemRegistry.GetData(qualifiedId);
                return data?.DisplayName ?? qualifiedId;
            }
            catch
            {
                return qualifiedId;
            }
        }
    }
}
