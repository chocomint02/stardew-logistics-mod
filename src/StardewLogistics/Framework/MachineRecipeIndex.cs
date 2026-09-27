using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.GameData.Machines;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Objects;
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

        /// <summary>Machines that age items rather than transform them: those whose rules hand off to the cask's code.</summary>
        private readonly List<MachineContext> AgingMachines = new();

        /// <summary>What ages each item, and how fast, or <c>null</c> for items nothing ages.</summary>
        private readonly Dictionary<string, (MachineContext Context, float Rate)?> AgingCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Each machine's any-quality recipe for an input, to compare quality-specific ones against.</summary>
        private readonly Dictionary<string, MachineRecipe> General = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>One of every item seen in storage, by stock ID: the candidates for a recipe's extra ingredient.</summary>
        private readonly Dictionary<string, Item> IngredientPool = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Inputs whose recipe needs an extra ingredient not seen yet, tried again when new stock appears.</summary>
        private readonly Dictionary<string, (MachineContext Context, Item Input, bool FromStock, int Quality)> AwaitingIngredients = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Extra Machine Config's custom data keys.</summary>
        private const string EmcPrefix = "selph.ExtraMachineConfig.";

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
            this.AgingMachines.Clear();
            this.AgingCache.Clear();
            this.SkippedRules = 0;
            this.ExternalRequirementSkips = 0;
            this.IngredientPool.Clear();
            this.AwaitingIngredients.Clear();
            IdTagCache.Clear();
            SampleCache.Clear();
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

            // Casks, and anything a mod makes that ages the same way. Their rules are skipped as unpredictable
            // below, since the output is computed in code; aging is handled on its own terms instead.
            this.AgingMachines.AddRange(this.MachineList.Where(context => context.Data.OutputRules.Any(rule =>
                rule?.OutputItem?.Any(output => output?.OutputMethod?.Contains("OutputCask", StringComparison.OrdinalIgnoreCase) == true) == true)));

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

                    foreach (MachineRecipe recipe in this.Resolve(context, input, fromStock: false))
                        this.AddFixed(recipe);
                }

                // Inputs a rule accepts by tag: known to the planner from the start, so a short ingredient can say
                // what else would do, but offered on the Auto tab only once one is stored -- a Crystalarium would
                // otherwise list every gem in the game.
                foreach (string inputId in context.TaggedInputs().Except(context.NamedInputs(), StringComparer.OrdinalIgnoreCase))
                {
                    Item input = CreateInput(inputId);
                    if (input == null)
                        continue;

                    foreach (MachineRecipe recipe in this.Resolve(context, input, fromStock: false).Where(recipe => this.KnownKeys.Add(recipe.Key)))
                        this.AddFromStock(recipe.InputId, recipe);
                }
            }

            Log.Trace($"Indexed {this.FixedRecipes.Count} processing recipes across {this.MachineList.Count} machines in {timer.ElapsedMilliseconds}ms "
                + $"({this.SkippedRules} inputs skipped as unpredictable, {this.ExternalRequirementSkips} waiting for an extra ingredient to appear in storage). "
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

            // New candidates for extra ingredients first, so an input tried just now can use them -- and recipes
            // that were waiting on one get another try.
            List<Item> items = stock.Where(item => item is SObject).ToList();
            bool newIngredients = false;
            foreach (Item sample in items)
            {
                string id = StockId.Of(sample);
                if (id != null && !this.IngredientPool.ContainsKey(id))
                {
                    Item copy = sample.getOne();
                    copy.Quality = SObject.lowQuality;
                    this.IngredientPool[id] = copy;
                    newIngredients = true;
                }
            }
            if (newIngredients && this.AwaitingIngredients.Count > 0)
                found += this.RetryAwaiting();

            foreach (Item sample in items)
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
                        foreach (MachineRecipe recipe in this.Resolve(context, input, fromStock: true))
                        {
                            if (!this.KnownKeys.Add(recipe.Key))
                                continue;

                            this.AddFromStock(inputId, recipe);
                            if (recipe.RecipeExtras == 0)
                                this.General[GeneralKey(recipe.MachineId, recipe.InputId)] = recipe;
                            found++;
                        }
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
                        foreach (MachineRecipe recipe in this.Resolve(context, input, fromStock: true, quality: sample.Quality))
                        {
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

        /// <summary>Whether Fairy Dust can speed up a machine.</summary>
        public bool AllowsFairyDust(string machineId)
        {
            return this.MachineList.Any(context => string.Equals(context.MachineId, machineId, StringComparison.OrdinalIgnoreCase) && context.Data.AllowFairyDust);
        }

        /// <summary>Whether a machine ages items the way a cask does.</summary>
        public bool IsAgingMachine(string machineId) => this.AgingMachines.Any(context => string.Equals(context.MachineId, machineId, StringComparison.OrdinalIgnoreCase));

        /// <summary>Whether a cask can age an item.</summary>
        public bool CanAge(string stockId) => this.GetAging(stockId) != null;

        /// <summary>A recipe for aging an item to a quality in a cask, or <c>null</c> if nothing ages it.</summary>
        /// <remarks>Its time is from normal quality, the longest it can take; a better starting item finishes sooner.</remarks>
        public MachineRecipe GetAgingRecipe(string stockId, int targetQuality)
        {
            if (targetQuality <= SObject.lowQuality || this.GetAging(stockId) is not { } aging)
                return null;

            Item sample = StockId.Create(stockId);
            if (sample == null)
                return null;
            sample.Quality = targetQuality;

            return new MachineRecipe
            {
                MachineId = aging.Context.MachineId,
                MachineName = aging.Context.MachineName,
                InputId = stockId,
                InputCount = 1,
                OutputId = stockId,
                OutputSample = sample,
                OutputCount = 1,
                MaxOutputCount = 1,
                Minutes = 0,
                Days = AgingDays(SObject.lowQuality, targetQuality, aging.Rate),
                IsAging = true,
                TargetQuality = targetQuality,
                AgingRate = aging.Rate,
                FromStock = true
            };
        }

        /// <summary>Puts an item in a cask to age, the way the game would.</summary>
        /// <returns>Whether the cask took it. A cask somewhere aging isn't allowed, or one that won't take this item, doesn't.</returns>
        /// <remarks>
        /// The game's own <c>Cask.OutputCask</c> does the work, so the aging rate, starting maturity and any mod
        /// that changes casks all behave exactly as for a player's hand.
        /// </remarks>
        public bool StartAging(SObject cask, Item input)
        {
            if (cask is not Cask real || !real.IsValidCaskLocation())
            {
                Log.Trace($"Didn't put {input?.DisplayName} in the cask at {cask?.TileLocation}: it isn't somewhere aging is allowed.");
                return false;
            }

            MachineData data = cask.GetMachineData();
            if (data == null || TryGetAgingRate(cask, data, input, out MachineOutputRule rule, out MachineItemOutput output) == null)
            {
                Log.Trace($"Didn't put {input?.DisplayName} (quality {input?.Quality}) in the cask at {cask.TileLocation}: the cask's rules don't accept it.");
                return false;
            }

            try
            {
                Item aging = Cask.OutputCask(cask, input, probe: false, output, Game1.player, out int? overrideMinutes);
                if (aging is not SObject held)
                {
                    Log.Trace($"Didn't put {input.DisplayName} (quality {input.Quality}) in the cask at {cask.TileLocation}: the cask turned it down.");
                    return false;
                }

                cask.heldObject.Value = held;
                cask.minutesUntilReady.Value = Math.Max(0, overrideMinutes ?? rule.MinutesUntilReady);
                cask.readyForHarvest.Value = false;
                return true;
            }
            catch (Exception ex)
            {
                Log.Trace($"Couldn't start {input.DisplayName} aging in a cask: {ex.Message}");
                return false;
            }
        }

        /// <summary>Whole days to age from one quality to another at a rate, matching how a cask counts.</summary>
        /// <remarks>
        /// A cask starts an item at the days its quality stands for -- normal 56, silver 42, gold 28, iridium 0 --
        /// and takes off its aging rate each night, moving up a quality as it passes each mark.
        /// </remarks>
        public static int AgingDays(int fromQuality, int toQuality, float rate)
        {
            float days = DaysForQuality(fromQuality) - DaysForQuality(toQuality);
            return days <= 0 ? 0 : (int)Math.Ceiling(days / Math.Max(0.01f, rate));
        }

        /// <summary>Days a cask has left before its item reaches a quality.</summary>
        public static int AgingDaysLeft(Cask cask, int toQuality)
        {
            float days = cask.daysToMature.Value - DaysForQuality(toQuality);
            return days <= 0 ? 0 : (int)Math.Ceiling(days / Math.Max(0.01f, cask.agingRate.Value));
        }

        /// <summary>The cask maturity mark for a quality, as <c>Cask.GetDaysForQuality</c> sets it.</summary>
        private static float DaysForQuality(int quality)
        {
            return quality switch
            {
                SObject.bestQuality => 0,
                SObject.highQuality => 28,
                SObject.medQuality => 42,
                _ => 56
            };
        }

        /// <summary>Finds what ages an item, remembering the answer.</summary>
        private (MachineContext Context, float Rate)? GetAging(string stockId)
        {
            if (string.IsNullOrEmpty(stockId) || this.AgingMachines.Count == 0)
                return null;

            if (this.AgingCache.TryGetValue(stockId, out var cached))
                return cached;

            (MachineContext, float)? found = null;
            Item input = StockId.Create(stockId);
            if (input is SObject)
            {
                foreach (MachineContext context in this.AgingMachines)
                {
                    float? rate = TryGetAgingRate(context.Machine, context.Data, input, out _, out _);
                    if (rate != null)
                    {
                        found = (context, rate.Value);
                        break;
                    }
                }
            }

            return this.AgingCache[stockId] = found;
        }

        /// <summary>The aging rate a cask would give an item, or <c>null</c> if it wouldn't take it.</summary>
        private static float? TryGetAgingRate(SObject cask, MachineData data, Item input, out MachineOutputRule rule, out MachineItemOutput output)
        {
            rule = null;
            output = null;
            Farmer who = Game1.player;
            GameLocation location = cask.Location ?? who?.currentLocation ?? Game1.getFarm();

            try
            {
                if (!MachineDataUtility.TryGetMachineOutputRule(cask, data, MachineOutputTrigger.ItemPlacedInMachine, input, who, location,
                        out rule, out MachineOutputTriggerRule trigger, out _, out _)
                    || rule == null || trigger == null)
                    return null;

                List<MachineItemOutput> candidates = rule.OutputItem?.Where(item => !HasExternalRequirement(item)).ToList();
                if (candidates == null || candidates.Count == 0)
                    return null;

                output = MachineDataUtility.GetOutputData(candidates, rule.UseFirstValidOutput, input, who, location);
                if (output?.OutputMethod?.Contains("OutputCask", StringComparison.OrdinalIgnoreCase) != true)
                    return null;

                return output.CustomData != null
                    && output.CustomData.TryGetValue("AgingMultiplier", out string raw)
                    && float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float multiplier)
                    && multiplier > 0
                        ? multiplier
                        : 1f;
            }
            catch
            {
                return null;
            }
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
        /// <summary>Asks the game what a machine would make from an input, and turns the answers into recipes.</summary>
        /// <returns>
        /// Every recipe the input makes on this machine: its plain one, and one for each output that takes an extra
        /// ingredient through Extra Machine Config. Empty if the machine won't take the input.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Extra Machine Config (EMC) lets an output ask for more ingredients -- fruit plus Plain Yogurt, a fish plus
        /// rice -- and checks for them in the player's bag, or in the "auto-load" inventory the game sets while a
        /// hopper fills a machine. The index sets that inventory itself, to a throwaway one holding the ingredients
        /// it wants EMC to see. So EMC resolves the real output -- including any flavour or price the extra
        /// ingredient gives it -- without showing "Requires..." or depending on what the player is carrying. It's
        /// only ever a probe: nothing is consumed.
        /// </para>
        /// <para>
        /// The rule is looked for twice: with nothing extra, which finds the plain rule, and with every candidate
        /// ingredient, which finds a rule that only applies when its extra ingredients are there.
        /// </para>
        /// </remarks>
        private List<MachineRecipe> Resolve(MachineContext context, Item input, bool fromStock, int quality = Quality.Any)
        {
            List<MachineRecipe> recipes = new();
            Farmer who = Game1.player;
            GameLocation location = who?.currentLocation ?? Game1.getFarm();

            try
            {
                List<(MachineOutputRule Rule, MachineOutputTriggerRule Trigger)> rules = new();
                foreach (Inventory offered in new[] { new Inventory(), this.EveryIngredient(context) })
                {
                    using (OfferIngredients(offered))
                    {
                        // The rule the machine would pick, honouring rule order and every condition. This is what
                        // makes a keg turn barley into stout and not into barley juice, when both rules would accept it.
                        if (MachineDataUtility.TryGetMachineOutputRule(context.Machine, context.Data, MachineOutputTrigger.ItemPlacedInMachine, input, who, location,
                                out MachineOutputRule rule, out MachineOutputTriggerRule trigger, out _, out _)
                            && rule != null && trigger != null && !rules.Any(existing => existing.Rule == rule))
                            rules.Add((rule, trigger));
                    }
                }

                foreach ((MachineOutputRule rule, MachineOutputTriggerRule trigger) in rules)
                {
                    List<MachineItemOutput> outputs = rule.OutputItem?.Where(item => item != null).ToList() ?? new List<MachineItemOutput>();

                    // The plain outputs, as before: whichever one the game would pick.
                    List<MachineItemOutput> plain = outputs.Where(item => !HasExternalRequirement(item)).ToList();
                    if (plain.Count > 0)
                    {
                        MachineRecipe recipe = this.BuildRecipe(context, rule, trigger, plain, input, fromStock, quality, new List<Item>(), new List<ItemCost>());
                        if (recipe != null)
                            recipes.Add(recipe);
                    }

                    // Each output with extra ingredients, with the ingredients storage could supply.
                    foreach (MachineItemOutput output in outputs.Where(HasExternalRequirement))
                        recipes.AddRange(this.ResolveWithIngredients(context, rule, trigger, output, input, fromStock, quality));
                }
            }
            catch (Exception ex)
            {
                Log.Trace($"Couldn't resolve {context.MachineName} with {input.QualifiedItemId}: {ex.Message}");
            }

            return recipes;
        }

        /// <summary>The recipes for an output that takes extra ingredients.</summary>
        /// <remarks>
        /// An output whose product depends on the extra ingredient -- its flavour, colour or price comes from it --
        /// gets a recipe per ingredient storage holds, each asking for exactly that one. Any other output gets one
        /// recipe, asking for any item that meets the requirement: that ID, that category, or those tags.
        /// </remarks>
        private IEnumerable<MachineRecipe> ResolveWithIngredients(MachineContext context, MachineOutputRule rule, MachineOutputTriggerRule trigger, MachineItemOutput output, Item input, bool fromStock, int quality)
        {
            List<Requirement> requirements = ReadRequirements(output);
            if (requirements.Count == 0)
                yield break;

            // What storage (or the item registry, for a named ingredient) could supply for each requirement.
            string inputId = StockId.Of(input);
            bool dependsOnIngredient = DependsOnIngredient(output);
            List<List<Item>> candidates = requirements
                .Select(requirement => this.CandidatesFor(requirement, inputId))
                .ToList();

            // A requirement nothing in storage meets yet. If the product doesn't depend on which item meets it, any
            // item from the game's data will do to work the recipe out: the recipe then shows as soon as its main
            // ingredient is stored, and the plan says what's short. If the product does depend on it -- takes its
            // flavour -- there's no knowing what it makes until a real one turns up, so try again when one does.
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Count == 0 && !dependsOnIngredient && SampleFor(requirements[i].Spec) is Item sample)
                    candidates[i].Add(sample);
            }

            if (candidates.Any(list => list.Count == 0))
            {
                string key = $"{context.MachineId}|{inputId}|{quality}";
                if (this.AwaitingIngredients.TryAdd(key, (context, input.getOne(), fromStock, quality)))
                    this.ExternalRequirementSkips++;
                yield break;
            }

            IEnumerable<Item> firstChoices = dependsOnIngredient ? candidates[0].Take(24) : candidates[0].Take(1);

            foreach (Item first in firstChoices)
            {
                // One item per requirement: the one being tried for the first, the first unused one for the rest.
                List<Item> chosen = new() { first };
                for (int i = 1; i < requirements.Count; i++)
                {
                    Item next = candidates[i].FirstOrDefault(item => !requirements[i].NoDuplicate || chosen.All(other => StockId.Of(other) != StockId.Of(item)));
                    if (next == null)
                        yield break;
                    chosen.Add(next);
                }

                // Where the product depends on the ingredient, the recipe asks for exactly that one; otherwise for
                // anything meeting the requirement.
                List<ItemCost> costs = requirements
                    .Select((requirement, index) => new ItemCost(dependsOnIngredient ? StockId.Of(chosen[index]) : requirement.Spec, requirement.Count))
                    .ToList();

                MachineRecipe recipe = this.BuildRecipe(context, rule, trigger, new List<MachineItemOutput> { output }, input, fromStock, quality,
                    chosen.Select((item, index) => WithStack(item, requirements[index].Count)).ToList(), costs);
                if (recipe != null)
                    yield return recipe;
            }
        }

        /// <summary>Turns a rule's chosen output into a recipe, with any extra ingredients offered to Extra Machine Config.</summary>
        private MachineRecipe BuildRecipe(MachineContext context, MachineOutputRule rule, MachineOutputTriggerRule trigger, List<MachineItemOutput> candidates, Item input, bool fromStock, int quality, List<Item> ingredients, List<ItemCost> ingredientCosts)
        {
            Farmer who = Game1.player;
            GameLocation location = who?.currentLocation ?? Game1.getFarm();

            Inventory offered = new();
            foreach (Item ingredient in ingredients)
                offered.Add(ingredient);

            using (OfferIngredients(offered))
            {
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
                int days = rule.DaysUntilReady;

                // The machine's ready-time modifiers, applied as the game applies them when it starts a run: to the
                // run's minutes, a day-long run's included. Content packs use them to speed machines up.
                if (context.Data?.ReadyTimeModifiers?.Count > 0)
                {
                    try
                    {
                        int before = days > 0 ? days * CraftPlan.MinutesPerDay : minutes;
                        int after = (int)Utility.ApplyQuantityModifiers(before, context.Data.ReadyTimeModifiers, context.Data.ReadyTimeModifierMode, location, who, product, input);
                        if (after != before)
                        {
                            minutes = Math.Max(10, after);
                            days = 0;
                        }
                    }
                    catch
                    {
                        // Keep the data's own time.
                    }
                }

                return new MachineRecipe
                {
                    MachineId = context.MachineId,
                    MachineName = context.MachineName,
                    InputId = StockId.Of(input),
                    InputTags = trigger.RequiredTags?.Where(tag => !string.IsNullOrWhiteSpace(tag)).ToList() ?? new List<string>(),
                    InputCount = Math.Max(1, trigger.RequiredCount),
                    InputQuality = quality,
                    ExtraInputs = context.Extras.Concat(ingredientCosts).ToList(),
                    RecipeExtras = ingredientCosts.Count,
                    OutputId = StockId.Of(product),
                    OutputSample = product.getOne(),
                    FromStock = fromStock,
                    OutputCount = guaranteed,
                    MaxOutputCount = Math.Max(guaranteed, best),
                    Minutes = Math.Max(0, minutes),
                    Days = Math.Max(0, days)
                };
            }
        }

        /// <summary>One extra ingredient an Extra Machine Config output asks for.</summary>
        private record Requirement(string Spec, int Count, bool NoDuplicate);

        /// <summary>Reads an output's extra ingredients: <c>RequirementId.N</c> or <c>RequirementTags.N</c>, with <c>RequirementCount.N</c>.</summary>
        private static List<Requirement> ReadRequirements(MachineItemOutput output)
        {
            List<Requirement> requirements = new();
            if (output?.CustomData == null)
                return requirements;

            for (int n = 1; n <= 20; n++)
            {
                string spec = null;
                if (output.CustomData.TryGetValue($"{EmcPrefix}RequirementId.{n}", out string id) && !string.IsNullOrWhiteSpace(id))
                    spec = id.Trim().StartsWith("-") ? id.Trim() : Qualify(id.Trim());
                else if (output.CustomData.TryGetValue($"{EmcPrefix}RequirementTags.{n}", out string tags) && !string.IsNullOrWhiteSpace(tags))
                {
                    // A single "id_o_..." tag names one item; asking for that item says what it is and shows its icon.
                    List<string> list = tags.Split(',').Select(tag => tag.Trim()).Where(tag => tag.Length > 0).ToList();
                    spec = list.Count == 1 && list[0].StartsWith("id_", StringComparison.OrdinalIgnoreCase)
                        ? ItemForIdTag(list[0]) ?? StockId.ForTags(list)
                        : StockId.ForTags(list);
                }

                if (spec == null)
                {
                    if (n > 1)
                        break;
                    continue;
                }

                int count = output.CustomData.TryGetValue($"{EmcPrefix}RequirementCount.{n}", out string rawCount)
                    && int.TryParse(rawCount, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
                        ? Math.Max(1, parsed)
                        : 1;
                bool noDuplicate = output.CustomData.ContainsKey($"{EmcPrefix}RequirementNoDuplicate.{n}");
                requirements.Add(new Requirement(spec, count, noDuplicate));
            }

            return requirements;
        }

        /// <summary>The items that could meet a requirement: a named one always; a category or tags, from what storage has held.</summary>
        private List<Item> CandidatesFor(Requirement requirement, string inputId)
        {
            if (!StockId.IsSpec(requirement.Spec))
            {
                Item named = CreateInput(requirement.Spec);
                return named == null ? new List<Item>() : new List<Item> { named };
            }

            return this.IngredientPool
                .Where(pair => StockId.Matches(pair.Value, requirement.Spec) && (!requirement.NoDuplicate || !string.Equals(pair.Key, inputId, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(pair => pair.Value.salePrice())
                .Select(pair => pair.Value)
                .ToList();
        }

        /// <summary>The item an "id_" context tag names, if there is one.</summary>
        /// <remarks>Every item has a tag of its qualified ID -- "id_o_cornucopia_plainyogurt" -- which content packs use to ask for one item.</remarks>
        private static string ItemForIdTag(string tag)
        {
            if (IdTagCache.TryGetValue(tag, out string cached))
                return cached;

            string found = null;
            try
            {
                found = (Game1.objectData?.Keys.Select(key => "(O)" + key) ?? Enumerable.Empty<string>())
                    .Concat(Game1.bigCraftableData?.Keys.Select(key => "(BC)" + key) ?? Enumerable.Empty<string>())
                    .FirstOrDefault(id => StardewValley.ItemContextTagManager.HasBaseTag(id, tag));
            }
            catch
            {
                found = null;
            }

            return IdTagCache[tag] = found;
        }

        /// <summary>An item from the game's data that meets a category or tag spec, for working out a recipe before storage has one.</summary>
        /// <remarks>Checked against each object's base tags, without creating it, and remembered per spec.</remarks>
        private static Item SampleFor(string spec)
        {
            if (!StockId.IsSpec(spec))
                return CreateInput(spec);

            if (!SampleCache.TryGetValue(spec, out string id))
            {
                id = null;
                try
                {
                    if (spec.StartsWith("-") && int.TryParse(spec, out int category))
                        id = Game1.objectData?.FirstOrDefault(pair => pair.Value?.Category == category).Key is string key ? "(O)" + key : null;
                    else if (spec.StartsWith("#"))
                    {
                        string[] tags = spec.Substring(1).Split(',').Select(tag => tag.Trim()).Where(tag => tag.Length > 0).ToArray();
                        id = Game1.objectData?.Keys.Select(key => "(O)" + key).FirstOrDefault(candidate => tags.All(tag => StardewValley.ItemContextTagManager.HasBaseTag(candidate, tag)));
                    }
                }
                catch
                {
                    id = null;
                }

                SampleCache[spec] = id;
            }

            return id == null ? null : CreateInput(id);
        }

        private static readonly Dictionary<string, string> IdTagCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> SampleCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every ingredient a machine's outputs could ask for, for finding a rule that needs them.</summary>
        private Inventory EveryIngredient(MachineContext context)
        {
            Inventory inventory = new();
            foreach (Item item in this.IngredientPool.Values)
                inventory.Add(WithStack(item, item.maximumStackSize()));

            foreach (MachineItemOutput output in context.Data.OutputRules.Where(rule => rule?.OutputItem != null).SelectMany(rule => rule.OutputItem).Where(HasExternalRequirement))
            {
                foreach (Requirement requirement in ReadRequirements(output))
                {
                    Item sample = SampleFor(requirement.Spec);
                    if (sample != null)
                        inventory.Add(sample);
                }
            }

            return inventory;
        }

        /// <summary>Whether an output's product takes anything from its extra ingredient: its ID, flavour, colour or price.</summary>
        private static bool DependsOnIngredient(MachineItemOutput output)
        {
            static bool Mentions(string text) => text != null && (text.Contains("DROP_IN_ID_", StringComparison.OrdinalIgnoreCase) || text.Contains("DROP_IN_PRESERVE_", StringComparison.OrdinalIgnoreCase));

            return Mentions(output.ItemId)
                || Mentions(output.PreserveId)
                || Mentions(output.ObjectInternalName)
                || Mentions(output.ObjectDisplayName)
                || output.CustomData?.Keys.Any(key => key.StartsWith(EmcPrefix + "RequirementAddPriceMultiplier", StringComparison.OrdinalIgnoreCase)) == true;
        }

        /// <summary>A copy of an item with a given stack.</summary>
        private static Item WithStack(Item item, int stack)
        {
            Item copy = item.getOne();
            copy.Stack = Math.Max(1, stack);
            return copy;
        }

        /// <summary>What a machine makes from the actual items going into it, by the game's own machine code.</summary>
        /// <param name="machine">The machine.</param>
        /// <param name="recipe">The recipe being run.</param>
        /// <param name="input">The actual main input: a gold Starfruit rather than any Starfruit.</param>
        /// <param name="ingredients">The actual extra ingredients, for rules that take them.</param>
        /// <returns>The product, or <c>null</c> if the game makes something other than the recipe's product, or nothing.</returns>
        /// <remarks>
        /// A recipe's product was worked out once, from a plain sample of its input. The real input can change it:
        /// a rule that copies the input's quality, a mod's per-machine bonuses, anything the machine's rules say.
        /// Asking again with the real items gets the product the machine would make if the player loaded it. Where
        /// that's something else entirely -- a rule that picks at random -- the recipe's own product stands, so the
        /// job still gets what it planned for.
        /// </remarks>
        public Item MakeOutput(SObject machine, MachineRecipe recipe, Item input, IEnumerable<Item> ingredients)
        {
            if (machine == null || recipe == null || input == null || recipe.IsAging)
                return null;

            try
            {
                MachineData data = machine.GetMachineData();
                if (data == null)
                    return null;

                Farmer who = Game1.player;
                GameLocation location = machine.Location ?? who?.currentLocation ?? Game1.getFarm();

                StardewValley.Inventories.Inventory offered = new();
                foreach (Item ingredient in ingredients ?? Enumerable.Empty<Item>())
                    offered.Add(ingredient);

                using (OfferIngredients(offered))
                {
                    if (!MachineDataUtility.TryGetMachineOutputRule(machine, data, MachineOutputTrigger.ItemPlacedInMachine, input, who, location, out MachineOutputRule rule, out _, out _, out _) || rule == null)
                        return null;

                    MachineItemOutput output = MachineDataUtility.GetOutputData(machine, data, rule, input, who, location);
                    if (output == null)
                        return null;

                    Item product = MachineDataUtility.GetOutputItem(machine, output, input, who, probe: true, out _);
                    return product != null && string.Equals(StockId.Of(product), recipe.OutputId, StringComparison.OrdinalIgnoreCase)
                        ? product
                        : null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Sets the game's auto-load inventory while the index asks about a machine, and restores it after.</summary>
        /// <remarks>Extra Machine Config looks for extra ingredients there before the player's bag, and stays quiet when they're missing.</remarks>
        private static IDisposable OfferIngredients(StardewValley.Inventories.IInventory inventory)
        {
            StardewValley.Inventories.IInventory previous = SObject.autoLoadFrom;
            SObject.autoLoadFrom = inventory;
            return new Restore(() => SObject.autoLoadFrom = previous);
        }

        /// <summary>Runs an action when disposed.</summary>
        private sealed class Restore : IDisposable
        {
            private Action Action;
            public Restore(Action action) => this.Action = action;
            public void Dispose()
            {
                this.Action?.Invoke();
                this.Action = null;
            }
        }

        /// <summary>Tries again the inputs whose recipes were waiting for an extra ingredient to appear.</summary>
        /// <returns>How many recipes were found.</returns>
        private int RetryAwaiting()
        {
            int found = 0;
            foreach ((string key, var entry) in this.AwaitingIngredients.ToList())
            {
                List<MachineRecipe> recipes = this.Resolve(entry.Context, WithStack(entry.Input, entry.Input.maximumStackSize()), entry.FromStock, entry.Quality)
                    .Where(recipe => recipe.RecipeExtras > 0)
                    .ToList();
                if (recipes.Count == 0)
                    continue;

                this.AwaitingIngredients.Remove(key);
                foreach (MachineRecipe recipe in recipes.Where(recipe => this.KnownKeys.Add(recipe.Key)))
                {
                    if (entry.FromStock)
                        this.AddFromStock(recipe.InputId, recipe);
                    else
                    {
                        this.FixedRecipes.Add(recipe);
                        this.AddByOutput(recipe);
                    }
                    found++;
                }
            }

            return found;
        }

        /// <summary>Records a recipe found from what a rule names outright.</summary>
        private void AddFixed(MachineRecipe recipe)
        {
            if (!this.KnownKeys.Add(recipe.Key))
                return;

            this.FixedRecipes.Add(recipe);
            this.AddByOutput(recipe);
            if (recipe.RecipeExtras == 0)
                this.General[GeneralKey(recipe.MachineId, recipe.InputId)] = recipe;
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
                || specific.BaseMinutes != general.BaseMinutes
                || specific.BaseDays != general.BaseDays;
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
        /// EMC enforces these in its own patches, but the scheduler loads machines directly, so the index reads them
        /// and makes each one an ingredient of the recipe: reserved, and taken, like any other.
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

            /// <summary>Every item a rule accepts by tag rather than by name, found from the game's own item data.</summary>
            /// <remarks>
            /// A Yogurt Jar takes "any milk": naming only the milk a rule mentions outright would leave cow's milk
            /// unknown until some was stored, and a plan short of milk could only ever ask for goat's. Checked against
            /// each object's base tags, without creating it. Capped per rule, so a rule taking "any object" doesn't
            /// try every item in the game.
            /// </remarks>
            public IEnumerable<string> TaggedInputs()
            {
                const int maxPerTrigger = 300;
                HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
                IEnumerable<MachineOutputTriggerRule> triggers = this.Data.OutputRules
                    .Where(rule => rule?.Triggers != null)
                    .SelectMany(rule => rule.Triggers)
                    .Where(trigger => trigger != null
                        && trigger.Trigger.HasFlag(MachineOutputTrigger.ItemPlacedInMachine)
                        && string.IsNullOrWhiteSpace(trigger.RequiredItemId)
                        && trigger.RequiredTags is { Count: > 0 });

                foreach (MachineOutputTriggerRule trigger in triggers)
                {
                    int found = 0;
                    foreach (string key in Game1.objectData?.Keys ?? Enumerable.Empty<string>())
                    {
                        string id = "(O)" + key;
                        bool matches;
                        try
                        {
                            matches = ItemContextTagManager.DoAllTagsMatch(trigger.RequiredTags, ItemContextTagManager.GetBaseContextTags(id));
                        }
                        catch
                        {
                            matches = false;
                        }

                        if (!matches)
                            continue;
                        if (seen.Add(id))
                            yield return id;
                        if (++found >= maxPerTrigger)
                            break;
                    }
                }
            }
        }
    }
}
