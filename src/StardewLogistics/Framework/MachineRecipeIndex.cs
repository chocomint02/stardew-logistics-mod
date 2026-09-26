using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.ItemTypeDefinitions;
using SObject = StardewValley.Object;

namespace StardewLogistics.Framework
{
    /// <summary>Works out what each machine produces from a given input.</summary>
    /// <remarks>
    /// The game has no list of "what can a furnace make"; it has machine definitions evaluated when an item is
    /// dropped in. Rather than reimplement that evaluation -- rule order, trigger and output conditions, the
    /// <c>FLAVORED_ITEM</c> item query a keg's wine comes from -- this hands the game a real input item and asks
    /// what it would do, using the same code a player's click runs, with <c>probe</c> set so nothing changes.
    ///
    /// That leaves the question of which inputs to ask about. Rules naming a specific item are resolved up front,
    /// on load. Rules taking a category -- a keg takes any fruit -- can't be listed for every item in the game,
    /// and the player asked for only what they could actually make, so those are resolved against what storage
    /// holds, and remembered. Starfruit Wine becomes orderable once there is Starfruit to make it from.
    ///
    /// Outputs computed by a C# <c>OutputMethod</c> and outputs picked at random are skipped, since neither can
    /// be promised ahead of time.
    /// </remarks>
    internal class MachineRecipeIndex
    {
        /*********
        ** Fields
        *********/
        /// <summary>Recipes by the stock ID they produce, both those found on load and those found from stock.</summary>
        private readonly Dictionary<string, List<MachineRecipe>> ByOutput = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Recipes whose input is a specific named item, found on load.</summary>
        private readonly List<MachineRecipe> FixedRecipes = new();

        /// <summary>Recipes found by trying stock in machines, by the stock ID of the input.</summary>
        private readonly Dictionary<string, List<MachineRecipe>> FromStockByInput = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Stock IDs already tried in every machine, so each is only resolved once per session.</summary>
        private readonly HashSet<string> TriedInputs = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Recipe keys already indexed, so a stock item that a fixed rule covers isn't listed twice.</summary>
        private readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every machine the index can resolve against.</summary>
        private readonly List<MachineContext> MachineList = new();

        /// <summary>Each machine's any-quality recipe for an input, to compare quality-specific ones against.</summary>
        private readonly Dictionary<string, MachineRecipe> General = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A random source that never rolls lucky: only a 100% chance passes.</summary>
        /// <remarks>
        /// Output modifiers like "double for iridium wool" are conditions with a <c>RANDOM</c> chance. Evaluated
        /// against this, a chance below 100% always fails and a certain one always passes, which leaves exactly
        /// the yield that can be promised.
        /// </remarks>
        private static readonly Random WorstCase = new FixedRandom(1 - 1e-9);

        /// <summary>A random source that always rolls lucky, for the most a run could yield.</summary>
        private static readonly Random BestCase = new FixedRandom(0);


        /*********
        ** Accessors
        *********/
        /// <summary>Every processing recipe known so far: the fixed ones plus everything found from stock.</summary>
        public IEnumerable<MachineRecipe> All => this.FixedRecipes.Concat(this.FromStockByInput.Values.SelectMany(list => list));

        /// <summary>How many recipes are known, for the console.</summary>
        public int Count => this.FixedRecipes.Count + this.FromStockByInput.Values.Sum(list => list.Count);

        /// <summary>How many inputs were skipped because the machine's output can't be predicted.</summary>
        public int SkippedRules { get; private set; }

        /// <summary>How many outputs were set aside because another mod adds ingredients this mod can't supply.</summary>
        public int ExternalRequirementSkips { get; private set; }


        /*********
        ** Public methods
        *********/
        /// <summary>Rebuilds the index from the current machine data.</summary>
        public void Rebuild()
        {
            this.ByOutput.Clear();
            this.FixedRecipes.Clear();
            this.FromStockByInput.Clear();
            this.TriedInputs.Clear();
            this.KnownKeys.Clear();
            this.General.Clear();
            this.MachineList.Clear();
            this.SkippedRules = 0;
            this.ExternalRequirementSkips = 0;
            StockId.Reset();

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
                if (data?.OutputRules == null || data.OutputRules.Count == 0)
                    continue;

                SObject machine;
                try
                {
                    machine = ItemRegistry.Create(machineId, allowNull: true) as SObject;
                }
                catch
                {
                    machine = null;
                }

                if (machine == null)
                    continue;

                this.MachineList.Add(new MachineContext(machineId, GetDisplayName(machineId), data, machine, ReadExtraInputs(data)));
            }

            // Resolve every input a rule names outright. Flavoured outputs are left for the stock pass: a named
            // input can still make one (oats into oat milk), and it should appear the same way a keg's fruit does,
            // when there is some to use.
            Stopwatch timer = Stopwatch.StartNew();
            foreach (MachineContext context in this.MachineList)
            {
                foreach (string inputId in context.NamedInputs())
                {
                    Item input = CreateInput(inputId);
                    if (input == null)
                        continue;

                    MachineRecipe recipe = this.Resolve(context, input, fromStock: false);
                    if (recipe != null && this.KnownKeys.Add(recipe.Key))
                    {
                        this.FixedRecipes.Add(recipe);
                        this.AddByOutput(recipe);
                        this.General[GeneralKey(recipe.MachineId, recipe.InputId)] = recipe;
                    }
                }
            }

            Log.Trace($"Indexed {this.FixedRecipes.Count} processing recipes across {this.MachineList.Count} machines in {timer.ElapsedMilliseconds}ms "
                + $"({this.SkippedRules} inputs skipped as unpredictable, {this.ExternalRequirementSkips} outputs needing another mod's extra ingredients). "
                + "Recipes taking a category are found from stock as it appears.");
        }

        /// <summary>Tries every new item in storage in every machine, recording what each would make.</summary>
        /// <remarks>
        /// Each stock item is tried once per session; after that it costs a set lookup. Recipes found this way are
        /// kept even after the stock runs out, so a job already planned can still be run and a plan can say what's
        /// missing rather than claiming nothing makes it. Only <see cref="GetOrderable"/> filters by stock.
        /// </remarks>
        public void ExpandFor(IEnumerable<Item> stock)
        {
            if (stock == null || this.MachineList.Count == 0)
                return;

            Stopwatch timer = null;
            int tried = 0;
            int found = 0;

            foreach (Item sample in stock)
            {
                if (sample is not SObject)
                    continue;

                string inputId = StockId.Of(sample);
                if (inputId == null)
                    continue;

                // Each item is tried once as "any quality": the recipe is for Starfruit, not gold Starfruit.
                if (this.TriedInputs.Add(inputId))
                {
                    timer ??= Stopwatch.StartNew();
                    tried++;

                    // So a flavoured input (a fish's roe, say) can be named and recreated later.
                    StockId.Remember(sample);

                    Item input = sample.getOne();
                    input.Quality = SObject.lowQuality;
                    input.Stack = input.maximumStackSize();

                    foreach (MachineContext context in this.MachineList)
                    {
                        MachineRecipe recipe = this.Resolve(context, input, fromStock: true);
                        if (recipe == null || !this.KnownKeys.Add(recipe.Key))
                            continue;

                        this.AddFromStock(inputId, recipe);
                        this.General[GeneralKey(recipe.MachineId, recipe.InputId)] = recipe;
                        found++;
                    }
                }

                // And once more at each better quality actually held, in case a machine does more with it. Only a
                // difference in what a run takes, makes or how long it runs is worth a recipe of its own; a better
                // quality that just makes a better-quality product is the same recipe as far as planning goes.
                if (sample.Quality > SObject.lowQuality && this.TriedInputs.Add(inputId + "#" + sample.Quality))
                {
                    timer ??= Stopwatch.StartNew();
                    tried++;

                    Item input = sample.getOne();
                    input.Stack = input.maximumStackSize();

                    foreach (MachineContext context in this.MachineList)
                    {
                        MachineRecipe recipe = this.Resolve(context, input, fromStock: true, quality: sample.Quality);
                        if (recipe == null)
                            continue;

                        if (this.General.TryGetValue(GeneralKey(context.MachineId, inputId), out MachineRecipe general) && !DiffersInWork(recipe, general))
                            continue;

                        if (!this.KnownKeys.Add(recipe.Key))
                            continue;

                        this.AddFromStock(inputId, recipe);
                        found++;
                        Log.Trace($"{recipe.MachineName} does more with {Quality.Name(sample.Quality)} {StockId.GetDisplayName(inputId)}: "
                            + $"{recipe.InputCount} in, {recipe.OutputCount} out, {recipe.Minutes}m.");
                    }
                }
            }

            if (tried > 0)
                Log.Trace($"Tried {tried} new stock items in {this.MachineList.Count} machines in {timer.ElapsedMilliseconds}ms; found {found} recipes.");
        }

        /// <summary>The recipes worth offering the player to order, given what storage currently holds.</summary>
        /// <remarks>
        /// The fixed recipes are always offered, as they always have been: a copper bar is worth listing with no
        /// ore on hand, because the plan will say what's missing. Recipes found from stock are offered only while
        /// their input is there, which is what keeps the list to Starfruit Wine rather than every wine in the game.
        /// </remarks>
        public List<MachineRecipe> GetOrderable(IReadOnlyCollection<Item> stock)
        {
            this.ExpandFor(stock);

            List<MachineRecipe> orderable = new(this.FixedRecipes);

            HashSet<string> held = new(stock.Select(StockId.Of).Where(id => id != null), StringComparer.OrdinalIgnoreCase);
            foreach (string inputId in held)
            {
                if (this.FromStockByInput.TryGetValue(inputId, out List<MachineRecipe> list))
                    orderable.AddRange(list);
            }

            return orderable;
        }

        /// <summary>Returns the ways an item can be produced by a machine.</summary>
        public IReadOnlyList<MachineRecipe> GetRecipesFor(string stockId)
        {
            return this.ByOutput.TryGetValue(stockId ?? "", out List<MachineRecipe> found)
                ? found
                : Array.Empty<MachineRecipe>();
        }

        /// <summary>Whether any machine can produce an item.</summary>
        public bool CanProduce(string stockId) => this.GetRecipesFor(stockId).Count > 0;


        /*********
        ** Private methods
        *********/
        /// <summary>Asks the game what a machine would make from an input, and turns the answer into a recipe.</summary>
        /// <returns>The recipe, or <c>null</c> if the machine won't take the input or its output can't be predicted.</returns>
        private MachineRecipe Resolve(MachineContext context, Item input, bool fromStock, int quality = Quality.Any)
        {
            Farmer who = Game1.player;
            GameLocation location = who?.currentLocation ?? Game1.getFarm();

            try
            {
                // The rule the machine would pick, honouring rule order and every condition. This is what makes a
                // keg turn barley into stout and not into barley juice, when both rules would accept it.
                if (!MachineDataUtility.TryGetMachineOutputRule(context.Machine, context.Data, MachineOutputTrigger.ItemPlacedInMachine, input, who, location,
                        out MachineOutputRule rule, out MachineOutputTriggerRule trigger, out _, out _)
                    || rule == null || trigger == null)
                    return null;

                // Outputs that need a second ingredient added by Extra Machine Config (fruit plus Plain Yogurt)
                // are set aside before the game sees them. EMC checks those against the player's own inventory
                // and shows "Requires..." when it's missing, so leaving them in made the index both noisy and
                // dependent on what the player happened to be carrying -- and a recipe indexed while they held
                // the yogurt would then make yogurt without ever taking any from storage.
                List<MachineItemOutput> candidates = rule.OutputItem?.Where(item => !HasExternalRequirement(item)).ToList();
                if (candidates == null || candidates.Count == 0)
                {
                    if (rule.OutputItem is { Count: > 0 })
                        this.ExternalRequirementSkips++;
                    return null;
                }

                // Which of the rule's outputs applies, honouring per-output conditions. A rule can list a special
                // case before its fallback, like red cabbage making two sauerkraut where other cabbage makes one.
                MachineItemOutput output = MachineDataUtility.GetOutputData(candidates, rule.UseFirstValidOutput, input, who, location);
                if (output == null)
                    return null;

                if (!IsPredictable(output))
                {
                    this.SkippedRules++;
                    return null;
                }

                // A flavoured output only means something for a real input, so the load pass leaves them to stock.
                bool flavoured = output.ItemId.StartsWith("FLAVORED_ITEM", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(output.PreserveId);
                if (flavoured && !fromStock)
                    return null;

                Item product = MachineDataUtility.GetOutputItem(context.Machine, output, input, who, probe: true, out int? overrideMinutes);
                if (product == null || ItemRegistry.GetData(product.QualifiedItemId) == null)
                    return null;

                StockId.Remember(product);

                // The yield that can be promised, after any output modifiers: evaluated against a random source
                // that never rolls lucky, so "50% chance of double" adds nothing and "always double" counts in full.
                int minimum = output.MinStack > 0 ? output.MinStack : 1;
                int guaranteed = ApplyStackModifiers(minimum, output, location, who, product, input, WorstCase);
                int best = ApplyStackModifiers(Math.Max(minimum, output.MaxStack), output, location, who, product, input, BestCase);
                int minutes = overrideMinutes ?? rule.MinutesUntilReady;

                return new MachineRecipe
                {
                    MachineId = context.MachineId,
                    MachineName = context.MachineName,
                    InputId = StockId.Of(input),
                    InputTags = trigger.RequiredTags?.Where(tag => !string.IsNullOrWhiteSpace(tag)).ToList() ?? new List<string>(),
                    InputCount = Math.Max(1, trigger.RequiredCount),
                    InputQuality = quality,
                    ExtraInputs = context.Extras,
                    OutputId = StockId.Of(product),
                    OutputSample = product.getOne(),
                    FromStock = fromStock,
                    OutputCount = guaranteed,
                    MaxOutputCount = Math.Max(guaranteed, best),
                    Minutes = Math.Max(0, minutes),
                    Days = Math.Max(0, rule.DaysUntilReady)
                };
            }
            catch (Exception ex)
            {
                Log.Trace($"Couldn't resolve {context.MachineName} with {input.QualifiedItemId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Records a recipe found from stock.</summary>
        private void AddFromStock(string inputId, MachineRecipe recipe)
        {
            if (!this.FromStockByInput.TryGetValue(inputId, out List<MachineRecipe> list))
                this.FromStockByInput[inputId] = list = new List<MachineRecipe>();

            list.Add(recipe);
            this.AddByOutput(recipe);
        }

        /// <summary>The key for a machine's any-quality recipe for an input.</summary>
        private static string GeneralKey(string machineId, string inputId) => machineId + "|" + inputId;

        /// <summary>Whether a quality-specific recipe changes the work compared with the any-quality one.</summary>
        private static bool DiffersInWork(MachineRecipe specific, MachineRecipe general)
        {
            return !string.Equals(specific.OutputId, general.OutputId, StringComparison.OrdinalIgnoreCase)
                || specific.OutputCount != general.OutputCount
                || specific.InputCount != general.InputCount
                || specific.Minutes != general.Minutes
                || specific.Days != general.Days;
        }

        /// <summary>Adds a recipe to the by-output lookup the planner uses.</summary>
        private void AddByOutput(MachineRecipe recipe)
        {
            if (!this.ByOutput.TryGetValue(recipe.OutputId, out List<MachineRecipe> list))
                this.ByOutput[recipe.OutputId] = list = new List<MachineRecipe>();
            list.Add(recipe);
        }

        /// <summary>Whether an output needs an extra ingredient declared through Extra Machine Config.</summary>
        /// <remarks>
        /// EMC reads these from the output's custom data and enforces them in its own patches. The scheduler
        /// loads machines directly, so it would never take the extra ingredient; until that's supported, such
        /// outputs are left out rather than produced for free.
        /// </remarks>
        private static bool HasExternalRequirement(MachineItemOutput output)
        {
            return output?.CustomData != null
                && output.CustomData.Keys.Any(key => key.StartsWith("selph.ExtraMachineConfig.Requirement", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Applies an output's stack modifiers to a yield, rolling with a given random source.</summary>
        private static int ApplyStackModifiers(int stack, MachineItemOutput output, GameLocation location, Farmer who, Item product, Item input, Random random)
        {
            if (output.StackModifiers is not { Count: > 0 })
                return stack;

            try
            {
                float modified = Utility.ApplyQuantityModifiers(stack, output.StackModifiers, output.StackModifierMode, location, who, product, input, random);
                return Math.Max(1, (int)Math.Floor(modified));
            }
            catch
            {
                return stack;
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

            return true;
        }

        /// <summary>Creates a full stack of an input, so the count a rule requires is never what rejects it.</summary>
        private static Item CreateInput(string qualifiedId)
        {
            try
            {
                Item item = ItemRegistry.Create(qualifiedId, 1, 0, allowNull: true);
                if (item != null)
                    item.Stack = item.maximumStackSize();
                return item;
            }
            catch
            {
                return null;
            }
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


        /*********
        ** Nested types
        *********/
        /// <summary>A random source that always produces the same value.</summary>
        private sealed class FixedRandom : Random
        {
            private readonly double Value;

            public FixedRandom(double value) : base(0)
            {
                this.Value = value;
            }

            protected override double Sample() => this.Value;
            public override double NextDouble() => this.Value;
            public override int Next() => (int)(this.Value * int.MaxValue);
            public override int Next(int maxValue) => (int)(this.Value * maxValue);
            public override int Next(int minValue, int maxValue) => minValue + (int)(this.Value * (maxValue - minValue));
        }

        /// <summary>A machine as the index resolves against it.</summary>
        private class MachineContext
        {
            public string MachineId { get; }
            public string MachineName { get; }
            public MachineData Data { get; }

            /// <summary>An unplaced instance, which is what the game's resolution code needs to be handed.</summary>
            public SObject Machine { get; }

            public List<ItemCost> Extras { get; }

            public MachineContext(string machineId, string machineName, MachineData data, SObject machine, List<ItemCost> extras)
            {
                this.MachineId = machineId;
                this.MachineName = machineName;
                this.Data = data;
                this.Machine = machine;
                this.Extras = extras;
            }

            /// <summary>Every input a rule names outright, rather than by tag.</summary>
            public IEnumerable<string> NamedInputs()
            {
                return this.Data.OutputRules
                    .Where(rule => rule?.Triggers != null)
                    .SelectMany(rule => rule.Triggers)
                    .Where(trigger => trigger != null
                        && trigger.Trigger.HasFlag(MachineOutputTrigger.ItemPlacedInMachine)
                        && !string.IsNullOrWhiteSpace(trigger.RequiredItemId))
                    .Select(trigger => Qualify(trigger.RequiredItemId))
                    .Where(id => id != null)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
