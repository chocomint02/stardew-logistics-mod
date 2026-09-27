using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewValley;
using StardewValley.GameData.FarmAnimals;
using StardewValley.GameData.Machines;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace StardewLogistics.Devices
{
    /// <summary>Something on a network making sellable goods: a machine, a tapper, a cask, or a crop.</summary>
    internal class IncomeSource
    {
        /// <summary>What it is and what it makes, for the breakdown: "Keg: Starfruit Wine".</summary>
        public string Name { get; init; }

        /// <summary>What it makes, for the icon.</summary>
        public Item Icon { get; init; }

        /// <summary>Gold one completion is worth.</summary>
        /// <remarks>For a cask, only what aging adds: the wine going in was already counted by the keg.</remarks>
        public double Value { get; init; }

        /// <summary>Days one completion takes.</summary>
        public double CycleDays { get; init; }

        /// <summary>Days until the next completion.</summary>
        public double FirstDays { get; init; }

        /// <summary>How many completions are coming; <c>-1</c> for as many as there's time for.</summary>
        public int Completions { get; init; } = 1;

        /// <summary>The last day it can complete: a crop's season end. Past it, it makes nothing.</summary>
        public double UntilDay { get; init; } = double.MaxValue;

        /// <summary>What one completion uses up, for working out its cost: a keg's fruit, a crop's seed.</summary>
        public List<(string ItemId, int Count)> Inputs { get; init; } = new();

        /// <summary>Whether it's earning already, rather than only from some day ahead: a baby animal isn't yet.</summary>
        /// <remarks>Only what's earning now counts toward gold a day; the projection counts the rest from when it starts.</remarks>
        public bool EarningNow { get; init; } = true;

        /// <summary>Gold a day: what one completion is worth, over the days it takes.</summary>
        public double PerDay => this.EarningNow && this.CycleDays > 0 ? this.Value / this.CycleDays : 0;

        /// <summary>What one completion's inputs cost, by the player's item costs.</summary>
        public double Cost(ExpensePlan plan) => plan == null ? 0 : this.Inputs.Sum(input => plan.CostOf(input.ItemId) * input.Count);

        /// <summary>What its inputs cost a day.</summary>
        public double CostPerDay(ExpensePlan plan) => this.CycleDays > 0 ? this.Cost(plan) / this.CycleDays : 0;
    }

    /// <summary>Works out what a network's machines, tappers and fields are earning.</summary>
    /// <remarks>
    /// Each producer is valued at what its output sells for, over the time one batch takes: a keg of Starfruit Wine
    /// worth 3,150g that takes seven days earns 450g a day. Machines that keep producing on their own -- bee houses,
    /// crystalariums, tappers -- and regrowing crops repeat; a keg loaded by hand makes its one batch; a keg an
    /// autocrafting job is running makes the batches the job still has for it. Only what the shipping bin would
    /// take counts.
    /// </remarks>
    internal static class IncomeForecast
    {
        /*********
        ** Fields
        *********/
        /// <summary>The time a machine was seen to have left when its output first appeared, as a last resort for how long a batch takes.</summary>
        private static readonly ConditionalWeakTable<Item, StrongBox<int>> FirstSeen = new();

        /// <summary>What each crab pot's last catch sold for, to value it while it waits for the next.</summary>
        private static readonly ConditionalWeakTable<CrabPot, StrongBox<double>> LastCatch = new();

        private const double MinutesPerDay = CraftPlan.MinutesPerDay;


        /*********
        ** Public methods
        *********/
        /// <summary>Everything on a network that's earning.</summary>
        public static List<IncomeSource> Build(StorageNetwork network, JobRunner jobs)
        {
            List<IncomeSource> sources = new();
            if (network == null)
                return sources;

            foreach (NetworkNode node in network.Machines.Concat(network.GetNodes(NodeKind.Producer)))
            {
                try
                {
                    IncomeSource source = node.Object switch
                    {
                        Cask cask => FromCask(cask, jobs),
                        CrabPot pot => FromCrabPot(pot),
                        _ when node.Kind == NodeKind.Producer => FromTapper(node),
                        _ => FromMachine(node, network, jobs)
                    };
                    if (source != null)
                        sources.Add(source);
                }
                catch (Exception ex)
                {
                    Log.Trace($"Income forecast skipped the {node.Object?.DisplayName} at {node.Tile}: {ex.Message}");
                }
            }

            try
            {
                sources.AddRange(FromCrops(network, jobs));
            }
            catch (Exception ex)
            {
                Log.Trace($"Income forecast skipped the fields: {ex.Message}");
            }

            try
            {
                sources.AddRange(FromAnimals(network));
            }
            catch (Exception ex)
            {
                Log.Trace($"Income forecast skipped the animals: {ex.Message}");
            }

            return sources;
        }

        /// <summary>Records what a crab pot's catch sold for, when the network collects it.</summary>
        public static void RememberCatch(CrabPot pot, double value)
        {
            if (pot != null && value > 0)
                LastCatch.AddOrUpdate(pot, new StrongBox<double>(value));
        }

        /// <summary>The network's income a day: every producer's value over its batch time.</summary>
        public static double DailyIncome(IEnumerable<IncomeSource> sources) => sources.Sum(source => source.PerDay);

        /// <summary>Gold expected on each of the coming days, from each producer's completions.</summary>
        /// <param name="sources">The producers.</param>
        /// <param name="days">How many days to project, today being the first.</param>
        /// <param name="worth">What one completion counts for; its value unless given, or its profit after costs.</param>
        public static double[] Project(IEnumerable<IncomeSource> sources, int days, Func<IncomeSource, double> worth = null)
        {
            double[] income = new double[Math.Max(1, days)];

            foreach (IncomeSource source in sources)
            {
                double value = worth?.Invoke(source) ?? source.Value;
                if (value == 0 || source.CycleDays <= 0)
                    continue;

                int limit = source.Completions < 0 ? int.MaxValue : source.Completions;
                double when = Math.Max(0, source.FirstDays);
                for (int i = 0; i < limit; i++)
                {
                    if (when >= income.Length || when > source.UntilDay)
                        break;

                    income[(int)Math.Floor(when)] += value;
                    when += source.CycleDays;
                }
            }

            return income;
        }


        /*********
        ** Private methods: producers
        *********/
        /// <summary>A machine with data: a keg, a bee house, a crystalarium.</summary>
        private static IncomeSource FromMachine(NetworkNode node, StorageNetwork network, JobRunner jobs)
        {
            SObject machine = node.Object;
            SObject held = machine?.heldObject.Value;

            // An incubator's "output" is the egg it's hatching: an animal comes out, not something to sell.
            if (IsHatcher(machine))
                return null;

            // Empty between batches -- a bee house just collected, a worm bin waiting for morning -- but it starts
            // again by itself: it's earning all the same.
            if (held == null)
                return FromIdleRepeater(node, network);
            if (held is Chest)
                return null;

            int? price = Selling.UnitPrice(held);
            if (price is not > 0)
                return null;

            GameLocation location = node.Location ?? network.Location;
            double cycle = CycleDays(machine, location, out bool repeats);

            // What went in: the last item the player loaded, unless a job loaded it, which kept the exact items.
            List<(string, int)> inputs = new();
            if (machine.lastInputItem.Value is Item lastInput && !repeats)
                inputs.Add((StockId.Of(lastInput), 1));

            // A batch an autocrafting job started: the job knows exactly how long it takes, and how many more it
            // has for this machine.
            int completions = repeats ? -1 : 1;
            if (TryGetJobBatch(jobs, machine, location, node.Tile, out JobStep step, out RunningBatch batch))
            {
                inputs = batch.Inputs.Where(item => item != null).Select(item => (StockId.Of(item), item.Stack)).ToList();
                if (batch.ExpectedMinutes > 0)
                    cycle = ToDays(batch.ExpectedMinutes);
                int machines = Math.Max(1, step.InFlight.Count);
                completions = 1 + (int)Math.Ceiling(step.RemainingBatches / (double)machines);
                repeats = false;
            }

            if (cycle <= 0)
                return null;

            // Finished and waiting to be collected: a one-off batch has earned what it will.
            double first = machine.readyForHarvest.Value ? 0 : ToDays(Math.Max(0, machine.MinutesUntilReady));
            if (machine.readyForHarvest.Value && !repeats && completions <= 1)
                return null;

            return new IncomeSource
            {
                Name = $"{machine.DisplayName}: {held.DisplayName}",
                Icon = held,
                Value = price.Value * (double)Math.Max(1, held.Stack),
                CycleDays = cycle,
                FirstDays = first,
                Completions = completions,
                Inputs = inputs
            };
        }

        /// <summary>A cask: what aging adds to its item, over the days left to reach the quality it's aging to.</summary>
        private static IncomeSource FromCask(Cask cask, JobRunner jobs)
        {
            SObject held = cask.heldObject.Value;
            if (held == null)
                return null;

            // An autocrafting job ages to the quality it was asked for; otherwise a cask goes all the way.
            int target = SObject.bestQuality;
            if (cask.modData.TryGetValue(ModIds.JobKey, out string token))
            {
                CraftJob job = jobs?.Jobs.FirstOrDefault(candidate => candidate.Token == token);
                if (job?.TargetQuality > 0)
                    target = job.TargetQuality;
            }

            if (held.Quality >= target)
                return null;

            int days = MachineRecipeIndex.AgingDaysLeft(cask, target);
            int? now = Selling.UnitPrice(held);
            int? then = Selling.UnitPrice(held, target);
            if (days <= 0 || now == null || then == null || then <= now)
                return null;

            return new IncomeSource
            {
                Name = $"{cask.DisplayName}: {held.DisplayName}",
                Icon = held,
                Value = then.Value - now.Value,
                CycleDays = days,
                FirstDays = days,
                Completions = 1
            };
        }

        /// <summary>A tapper: the tree's product, every few days for as long as it's tapped.</summary>
        private static IncomeSource FromTapper(NetworkNode node)
        {
            SObject tapper = node.Object;
            SObject held = tapper?.heldObject.Value;
            if (held == null)
                return null;

            int? price = Selling.UnitPrice(held);
            if (price is not > 0)
                return null;

            double cycle = 0;
            if (node.Location?.terrainFeatures.TryGetValue(node.Tile, out TerrainFeature feature) == true && feature is Tree tree)
            {
                int days = tree.GetData()?.TapItems?.FirstOrDefault()?.DaysUntilReady ?? 0;
                if (days > 0)
                    cycle = tapper.QualifiedItemId == "(BC)264" ? Math.Max(1, Math.Ceiling(days / 2.0)) : days; // a Heavy Tapper is twice as fast
            }
            if (cycle <= 0)
                cycle = ObservedDays(held, tapper.MinutesUntilReady);
            if (cycle <= 0)
                return null;

            return new IncomeSource
            {
                Name = $"{tapper.DisplayName}: {held.DisplayName}",
                Icon = held,
                Value = price.Value * (double)Math.Max(1, held.Stack),
                CycleDays = cycle,
                FirstDays = tapper.readyForHarvest.Value ? 0 : ToDays(Math.Max(0, tapper.MinutesUntilReady)),
                Completions = -1
            };
        }

        /// <summary>Crops under the network's auto-harvesters, at their guaranteed yield.</summary>
        /// <remarks>
        /// A crop an autocrafting job has reserved is left out: it's an ingredient, and its worth arrives with the
        /// job's product. A regrowing crop repeats until its season ends, as does a one-harvest crop the harvester
        /// replants; otherwise a crop is harvested once.
        /// </remarks>
        private static IEnumerable<IncomeSource> FromCrops(StorageNetwork network, JobRunner jobs)
        {
            foreach (IncomingCrop crop in jobs?.Forecast?.Invoke(network) ?? new List<IncomingCrop>())
            {
                if (jobs.GetReservation(crop.Location, crop.Tile) != null)
                    continue;
                if (!crop.Location.terrainFeatures.TryGetValue(crop.Tile, out TerrainFeature feature) || feature is not HoeDirt { crop: not null } soil)
                    continue;

                Item harvest = ItemRegistry.Create(crop.ItemId, allowNull: true);
                int? price = Selling.UnitPrice(harvest);
                if (price is not > 0)
                    continue;

                string seed = soil.crop.netSeedIndex.Value;
                int regrow = soil.crop.GetData()?.RegrowDays ?? 0;
                double cycle;
                int completions;
                if (regrow > 0)
                {
                    cycle = regrow;
                    completions = -1;
                }
                else
                {
                    cycle = CropMath.DaysToGrow(seed, CropMath.FertilizerOf(soil), crop.Location, crop.Tile) ?? crop.Days;
                    completions = WillReplant(crop, seed) ? -1 : 1;
                }

                if (cycle <= 0)
                    cycle = Math.Max(1, crop.Days);

                // A one-harvest crop uses up its seed each time; a regrowing one only the once, long since paid.
                List<(string, int)> inputs = regrow > 0 ? new() : new() { (ItemRegistry.QualifyItemId(seed), 1) };

                int window = CropMath.DaysLeftToGrow(seed, crop.Location);
                yield return new IncomeSource
                {
                    Inputs = inputs,
                    Name = harvest.DisplayName,
                    Icon = harvest,
                    Value = price.Value * (double)crop.Count,
                    CycleDays = cycle,
                    FirstDays = crop.Days,
                    Completions = completions,
                    UntilDay = window == int.MaxValue || window < 0 ? double.MaxValue : window
                };
            }
        }


        /// <summary>Animals living in coops and barns the network reaches: what each produces, and how often.</summary>
        /// <remarks>
        /// A building counts when the network has cable inside it -- linked to the rest by a wireless receiver, or a
        /// network of its own. Each adult animal is valued at its next produce, at the quality it's producing at,
        /// doubled if it has eaten a Golden Animal Cracker, every <c>DaysToProduce</c> days. Deluxe produce is a
        /// chance, not a promise, so it isn't counted. Pigs dig their truffles outdoors, so they make none in winter.
        ///
        /// A baby animal is counted from the day it grows up: its first produce comes one production cycle after
        /// that, at normal quality. It adds to the projection, but not to gold a day, which is what's earning now.
        /// </remarks>
        private static IEnumerable<IncomeSource> FromAnimals(StorageNetwork network)
        {
            foreach (AnimalHouse house in network.Locations.OfType<AnimalHouse>())
            {
                foreach (long id in house.animalsThatLiveHere)
                {
                    FarmAnimal animal = Utility.getAnimal(id);
                    if (animal == null)
                        continue;

                    FarmAnimalData data = animal.GetAnimalData();
                    if (data == null || data.DaysToProduce <= 0)
                        continue;

                    bool adult = animal.isAdult();
                    int daysToGrow = adult ? 0 : Math.Max(1, data.DaysToMature - animal.age.Value);
                    if (data.HarvestType == FarmAnimalHarvestType.DigUp && Game1.IsWinter)
                        continue;

                    // What it's producing now, or the first thing it produces at its friendship level.
                    string produceId = adult ? animal.currentProduce.Value : null;
                    if (string.IsNullOrEmpty(produceId) || produceId == "-1")
                        produceId = data.ProduceItemIds?.FirstOrDefault(entry => entry != null && animal.friendshipTowardFarmer.Value >= entry.MinimumFriendship)?.ItemId;

                    Item produce = string.IsNullOrEmpty(produceId) ? null : ItemRegistry.Create(produceId, allowNull: true);
                    if (produce == null)
                        continue;
                    produce.Quality = adult ? Math.Max(0, animal.produceQuality.Value) : StardewValley.Object.lowQuality;

                    int? price = Selling.UnitPrice(produce);
                    if (price is not > 0)
                        continue;

                    int perHarvest = animal.hasEatenAnimalCracker.Value ? 2 : 1;
                    yield return new IncomeSource
                    {
                        Name = adult ? $"{animal.displayType}: {produce.DisplayName}" : $"{animal.displayType} (young): {produce.DisplayName}",
                        Icon = produce,
                        Value = price.Value * (double)perHarvest,
                        CycleDays = data.DaysToProduce,
                        FirstDays = adult
                            ? Math.Max(1, data.DaysToProduce - animal.daysSinceLastLay.Value)
                            : daysToGrow + data.DaysToProduce,
                        Completions = -1,
                        EarningNow = adult
                    };
                }
            }
        }


        /*********
        ** Private methods: timing
        *********/
        /// <summary>Days one batch takes on a machine, from the rule that made what's in it.</summary>
        /// <param name="repeats">Whether the machine starts again by itself: a bee house, a crystalarium.</param>
        private static double CycleDays(SObject machine, GameLocation location, out bool repeats)
        {
            repeats = false;
            MachineData data = machine.GetMachineData();
            MachineOutputRule rule = null;

            if (data?.OutputRules != null)
            {
                // The rule that made what's in it, if the machine says; otherwise the one its last input matches.
                string lastRule = machine.lastOutputRuleId.Value;
                if (!string.IsNullOrEmpty(lastRule))
                    rule = data.OutputRules.FirstOrDefault(candidate => candidate?.Id == lastRule);

                Item input = machine.lastInputItem.Value;
                if (rule == null && input != null)
                {
                    try
                    {
                        MachineDataUtility.TryGetMachineOutputRule(machine, data, MachineOutputTrigger.ItemPlacedInMachine, input, Game1.player, location, out rule, out _, out _, out _);
                    }
                    catch
                    {
                        rule = null;
                    }
                }

                // Nothing goes in: the rule is one the machine runs on its own.
                rule ??= data.OutputRules.FirstOrDefault(RunsByItself);
                repeats = RunsByItself(rule);
            }

            double cycle = rule != null ? RuleCycleDays(machine, data, rule, location) : 0;
            return cycle > 0 ? cycle : ObservedDays(machine.heldObject.Value, machine.MinutesUntilReady);
        }

        /// <summary>Days one run of a rule takes, with the machine's ready-time modifiers.</summary>
        /// <returns>The days, or zero where the game times the machine itself -- a solar panel counts down only on
        /// sunny days -- and its countdown has to be watched instead.</returns>
        /// <remarks>
        /// A rule that runs each morning can't finish more than once a day, however short its time: a soda machine's
        /// is zero minutes, which would otherwise read as a batch every minute.
        /// </remarks>
        private static double RuleCycleDays(SObject machine, MachineData data, MachineOutputRule rule, GameLocation location)
        {
            if (rule.DaysUntilReady <= 0 && rule.MinutesUntilReady < 0)
                return 0;

            double days = rule.DaysUntilReady > 0 || rule.MinutesUntilReady > 0 ? ToDays(ReadyMinutes(machine, data, rule, location)) : 0;
            return RunsEachMorning(rule) ? Math.Max(1, days) : days;
        }

        /// <summary>Whether a rule runs at the start of each day.</summary>
        private static bool RunsEachMorning(MachineOutputRule rule) => rule?.Triggers?.Any(trigger => trigger.Trigger.HasFlag(MachineOutputTrigger.DayUpdate)) == true;

        /// <summary>Whether a machine hatches animals rather than making things to sell.</summary>
        private static bool IsHatcher(SObject machine)
        {
            MachineData data = machine.GetMachineData();
            return data?.IsIncubator == true || machine.QualifiedItemId == "(BC)156";
        }

        /// <summary>A crab pot: its catch each morning, for as long as it's baited or needs no bait.</summary>
        /// <remarks>
        /// A catch is random, so a pot holding one is valued at that catch; an empty pot at its last one, remembered
        /// when the network collected it. The network rebaits pots it collects, so a pot with bait keeps going.
        /// </remarks>
        private static IncomeSource FromCrabPot(CrabPot pot)
        {
            SObject held = pot.heldObject.Value;
            double value = held != null
                ? Selling.Value(held, held.Stack)
                : LastCatch.TryGetValue(pot, out StrongBox<double> last) ? last.Value : 0;
            if (value <= 0)
                return null;

            bool keepsGoing = pot.bait.Value != null || !pot.NeedsBait(Game1.MasterPlayer) || held != null;
            return new IncomeSource
            {
                Name = $"{pot.DisplayName}: {held?.DisplayName ?? pot.DisplayName}",
                Icon = held ?? (Item)pot,
                Value = value,
                CycleDays = 1,
                FirstDays = held != null && pot.readyForHarvest.Value ? 0 : 1,
                Completions = keepsGoing ? -1 : 1
            };
        }

        /// <summary>Whether a rule starts the machine again by itself: each morning, or when its output is collected.</summary>
        private static bool RunsByItself(MachineOutputRule rule)
        {
            const MachineOutputTrigger byItself = MachineOutputTrigger.DayUpdate | MachineOutputTrigger.OutputCollected;
            return rule?.Triggers?.Any(trigger => (trigger.Trigger & byItself) != 0) == true;
        }

        /// <summary>The minutes a rule takes, with the machine's ready-time modifiers applied, as the game applies them.</summary>
        /// <remarks>A Crystalarium's time depends on the gem in it, and that's a modifier, not the rule's base time.</remarks>
        private static int ReadyMinutes(SObject machine, MachineData data, MachineOutputRule rule, GameLocation location)
        {
            int minutes = rule.DaysUntilReady > 0 ? rule.DaysUntilReady * (int)MinutesPerDay : rule.MinutesUntilReady;
            if (data?.ReadyTimeModifiers?.Count > 0)
            {
                try
                {
                    minutes = (int)Utility.ApplyQuantityModifiers(minutes, data.ReadyTimeModifiers, data.ReadyTimeModifierMode, location, Game1.player, machine.heldObject.Value, machine.lastInputItem.Value);
                }
                catch
                {
                    // Keep the base time.
                }
            }

            return Math.Max(1, minutes);
        }

        /// <summary>A machine that's empty between batches but starts again by itself each morning.</summary>
        /// <remarks>
        /// Valued at what its morning rule would make, worked out with the game's own output code without changing
        /// the machine. A machine that only restarts when its output is collected has nothing to restart from once
        /// empty, so it isn't counted.
        /// </remarks>
        private static IncomeSource FromIdleRepeater(NetworkNode node, StorageNetwork network)
        {
            SObject machine = node.Object;
            if (machine == null || machine.MinutesUntilReady > 0)
                return null;

            MachineData data = machine.GetMachineData();
            GameLocation location = node.Location ?? network.Location;

            // Only a morning rule whose condition holds now: a bee house makes nothing in winter.
            MachineOutputRule rule = data?.OutputRules?.FirstOrDefault(candidate => candidate?.Triggers?.Any(trigger =>
                trigger.Trigger.HasFlag(MachineOutputTrigger.DayUpdate)
                && (string.IsNullOrEmpty(trigger.Condition) || GameStateQuery.CheckConditions(trigger.Condition, location, Game1.player))) == true);
            if (rule == null)
                return null;

            Item product;
            try
            {
                MachineItemOutput output = MachineDataUtility.GetOutputData(machine, data, rule, null, Game1.player, location);
                product = output == null ? null : MachineDataUtility.GetOutputItem(machine, output, null, Game1.player, probe: true, out _);
            }
            catch
            {
                product = null;
            }

            int? price = Selling.UnitPrice(product);
            if (price is not > 0)
                return null;

            // Timed by the game itself, and nothing in it yet to watch: nothing to say how often it'll finish.
            double cycle = RuleCycleDays(machine, data, rule, location);
            if (cycle <= 0)
                return null;

            return new IncomeSource
            {
                Name = $"{machine.DisplayName}: {product.DisplayName}",
                Icon = product,
                Value = price.Value * (double)Math.Max(1, product.Stack),
                CycleDays = cycle,
                FirstDays = 1 + cycle,
                Completions = -1
            };
        }

        /// <summary>Converts a machine's minutes to days.</summary>
        /// <remarks>
        /// A batch a day or longer finishes on a morning, so it's rounded up to whole days: a keg's 10,000 minutes
        /// is collected on the seventh morning. Shorter batches keep their fraction.
        /// </remarks>
        private static double ToDays(int minutes)
        {
            return minutes >= MinutesPerDay ? Math.Ceiling(minutes / MinutesPerDay) : minutes / MinutesPerDay;
        }

        /// <summary>The longest a machine was seen to have left on an output, when nothing better is known.</summary>
        private static double ObservedDays(Item held, int minutesLeft)
        {
            if (held == null)
                return 0;

            StrongBox<int> seen = FirstSeen.GetValue(held, _ => new StrongBox<int>(minutesLeft));
            seen.Value = Math.Max(seen.Value, minutesLeft);
            return seen.Value > 0 ? ToDays(seen.Value) : 0;
        }

        /// <summary>The job batch running on a machine, if an autocrafting job loaded it.</summary>
        private static bool TryGetJobBatch(JobRunner jobs, SObject machine, GameLocation location, Vector2 tile, out JobStep step, out RunningBatch batch)
        {
            step = null;
            batch = null;
            if (jobs == null || !machine.modData.TryGetValue(ModIds.JobKey, out string token))
                return false;

            CraftJob job = jobs.Jobs.FirstOrDefault(candidate => candidate.Token == token);
            if (job == null)
                return false;

            string where = location?.NameOrUniqueName;
            foreach (JobStep candidate in job.Steps)
            {
                batch = candidate.InFlight.FirstOrDefault(running => running.LocationName == where && running.Tile == tile);
                if (batch != null)
                {
                    step = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether the harvester will plant a one-harvest crop again after it's picked.</summary>
        private static bool WillReplant(IncomingCrop crop, string seed)
        {
            if (!crop.Location.Objects.TryGetValue(crop.HarvesterTile, out SObject harvester) || harvester.ItemId != ModIds.AutoHarvester)
                return false;

            HarvesterSettings settings = HarvesterSettings.ReadCached(harvester);
            Rectangle area = settings.GetArea(crop.HarvesterTile);
            Point point = new((int)crop.Tile.X - area.X, (int)crop.Tile.Y - area.Y);
            return settings.Tiles.TryGetValue(point, out TilePlan plan)
                && string.Equals(CropMath.Unqualify(plan.SeedId), seed, StringComparison.OrdinalIgnoreCase)
                && settings.ShouldReplant(plan.SeedId, regrows: false);
        }
    }
}
