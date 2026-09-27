using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace StardewLogistics.Framework
{
    /// <summary>Learns, from what actually happens in this save, how far the game's data is from the truth.</summary>
    /// <remarks>
    /// The mod plans from the game's data: how long a machine takes, how many days a crop needs, what an item
    /// fetches. Whatever other mods change in that data is picked up as it is. What they change in code isn't: a
    /// patch that runs machines faster, grows crops more than a day a night, or pays extra at shipping. There are
    /// too many such mods, in too many combinations, to know about each; instead the results are measured.
    ///
    /// Three things are watched, on the host:
    /// <list type="bullet">
    ///   <item>
    ///     Every networked machine, from starting to finishing. Two figures come from that: what the game set its
    ///     timer to, against what the data says (a mod changing how long a run takes), and how fast the timer ran
    ///     down, against the clock (a mod changing how fast time passes for machines). They're kept apart because
    ///     an autocrafting job sets its own timer: it has to match the first, but the second already acts on it.
    ///   </item>
    ///   <item>
    ///     Every watered crop, overnight: how many days' growth it made, against the one the game gives.
    ///   </item>
    ///   <item>
    ///     The shipping bin: what the night paid, against what its contents were worth by the game's own prices.
    ///   </item>
    /// </list>
    ///
    /// Each keeps its last few observations, and the median of them is the answer, so a one-off -- Fairy Dust on a
    /// keg, a crop that missed its water -- can't skew it. With no mod changing anything every figure settles at
    /// exactly 1, and nothing changes. Specific figures (this machine making this) are preferred; where there are
    /// none yet, the machine's, then everything's, stand in, since a mod that speeds up machines usually speeds up
    /// all of them.
    /// </remarks>
    internal static class Calibration
    {
        /*********
        ** Fields
        *********/
        /// <summary>The save data key.</summary>
        private const string SaveKey = "calibration";

        /// <summary>How many observations are kept per figure.</summary>
        private const int Keep = 9;

        /// <summary>How far a figure can stray from 1 before an observation is treated as nonsense.</summary>
        private const double MinFactor = 0.05;
        private const double MaxFactor = 20;

        /// <summary>A factor this close to 1 is 1: the rounding in the game's clock shouldn't nudge plans.</summary>
        private const double Tolerance = 0.04;

        /// <summary>The key for figures that stand for every machine or every crop.</summary>
        private const string Everything = "*";

        private static IDataHelper Data;

        /// <summary>What's been learned.</summary>
        private static CalibrationData State = new();

        /// <summary>Machines being timed, by location and tile.</summary>
        private static readonly Dictionary<(string Location, Vector2 Tile), MachineRun> Runs = new();

        /// <summary>Crops noted as the day ended, to compare with the morning.</summary>
        private static readonly Dictionary<(string Location, Vector2 Tile), (string Harvest, int Days)> Evening = new();

        /// <summary>Estimates already worked out, until the next observation; planning asks for them constantly.</summary>
        private static readonly Dictionary<string, double> Cached = new();

        /// <summary>Finds a machine's recipe, for what its data says a run takes.</summary>
        private static Func<string, string, MachineRecipe> FindRecipe;


        /*********
        ** Accessors
        *********/
        /// <summary>Whether learned figures are used. Off, everything goes by the game's data alone.</summary>
        public static bool Enabled { get; set; } = true;


        /*********
        ** Public methods: setup
        *********/
        /// <summary>Connects the save data and the recipe lookup.</summary>
        public static void Initialise(IDataHelper data, Func<string, string, MachineRecipe> findRecipe)
        {
            Data = data;
            FindRecipe = findRecipe;
        }

        /// <summary>Loads what the save has learned.</summary>
        public static void Load()
        {
            Reset();
            if (!Context.IsMainPlayer || Data == null)
                return;

            try
            {
                State = Data.ReadSaveData<CalibrationData>(SaveKey) ?? new CalibrationData();
                Cached.Clear();
            }
            catch (Exception ex)
            {
                Log.Debug($"The learned timings couldn't be read, so they start fresh: {ex.Message}");
                State = new CalibrationData();
            }
        }

        /// <summary>Writes what's been learned into the save.</summary>
        public static void Save()
        {
            if (!Context.IsMainPlayer || Data == null)
                return;

            try
            {
                Data.WriteSaveData(SaveKey, State);
            }
            catch (Exception ex)
            {
                Log.Debug($"The learned timings couldn't be saved: {ex.Message}");
            }
        }

        /// <summary>What's been learned, to send to farmhands.</summary>
        public static CalibrationData Snapshot() => State;

        /// <summary>Takes on what the host has learned, as a farmhand: only the host watches, but everyone plans.</summary>
        public static void Adopt(CalibrationData learned)
        {
            if (Context.IsMainPlayer || learned == null)
                return;

            State = learned;
            Cached.Clear();
        }

        /// <summary>Forgets everything, for leaving the save or starting over.</summary>
        public static void Reset()
        {
            State = new CalibrationData();
            Runs.Clear();
            Evening.Clear();
            Cached.Clear();
        }


        /*********
        ** Public methods: machines
        *********/
        /// <summary>How much longer (above 1) or shorter a machine's runs take than its data says, all told.</summary>
        /// <param name="machineId">The machine's qualified item ID.</param>
        /// <param name="outputId">What it's making, if known.</param>
        /// <param name="location">Where one particular machine is, to use what's been learned about it in particular.</param>
        /// <param name="tile">That machine's tile.</param>
        public static double MachineFactor(string machineId, string outputId, GameLocation location = null, Vector2? tile = null)
        {
            return Clamp(SetupFactor(machineId, outputId, location, tile) * SpeedFactor(machineId, location, tile));
        }

        /// <summary>How the timer the game sets compares with the data's, for a machine making something.</summary>
        /// <remarks>
        /// One particular machine first: mods that upgrade machines one by one, or combine several into one, make
        /// two kegs run differently. Then that kind of machine, then every machine.
        /// </remarks>
        public static double SetupFactor(string machineId, string outputId, GameLocation location = null, Vector2? tile = null)
        {
            string instance = Instance(location, tile);
            return instance == null
                ? Estimate(State.Setup, (machineId + "|" + outputId, 1), (machineId, 1), (Everything, 3))
                : Estimate(State.Setup, (instance + "|" + outputId, 1), (instance, 1), (machineId + "|" + outputId, 1), (machineId, 1), (Everything, 3));
        }

        /// <summary>How long a machine's timer takes to run down, against the clock: one particular machine, its kind, or every machine.</summary>
        public static double SpeedFactor(string machineId, GameLocation location = null, Vector2? tile = null)
        {
            string instance = Instance(location, tile);
            return instance == null
                ? Estimate(State.Speed, (machineId, 2), (Everything, 4))
                : Estimate(State.Speed, (instance, 2), (machineId, 2), (Everything, 4));
        }

        /// <summary>Records what a stand-in load showed about one particular machine's timer (see <see cref="MachinePaces"/>).</summary>
        /// <remarks>For that machine alone: it may be upgraded or combined, and say nothing about others of its kind.</remarks>
        public static void NoteMeasured(string machineId, string outputId, GameLocation location, Vector2 tile, double setup)
        {
            string instance = Instance(location, tile);
            if (!Enabled || instance == null)
                return;

            Add(State.Setup, instance + "|" + outputId, setup);
            Add(State.Setup, instance, setup);
        }

        /// <summary>The key for one particular machine, or <c>null</c> if none is given.</summary>
        private static string Instance(GameLocation location, Vector2? tile)
        {
            return location == null || tile == null ? null : $"@{location.NameOrUniqueName}:{(int)tile.Value.X},{(int)tile.Value.Y}";
        }

        /// <summary>Scales a run's minutes by a factor, leaving it be if the factor is 1.</summary>
        public static int Scale(int minutes, double factor) => factor == 1 || minutes <= 0 ? minutes : Math.Max(1, (int)Math.Round(minutes * factor));

        /// <summary>Scales a run's days by a factor: at least one, since a day-long run finishes on a morning.</summary>
        public static int ScaleDays(int days, double factor) => factor == 1 || days <= 0 ? days : Math.Max(1, (int)Math.Round(days * factor));

        /// <summary>Times a networked machine: call each time the network is serviced.</summary>
        public static void ObserveMachine(SObject machine, GameLocation location, Vector2 tile)
        {
            if (!Enabled || machine == null || location == null || machine is Cask || !Context.IsMainPlayer)
                return;

            (string, Vector2) key = (location.NameOrUniqueName, tile);
            Runs.TryGetValue(key, out MachineRun run);
            SObject held = machine.heldObject.Value;

            if (machine.readyForHarvest.Value && held != null)
            {
                if (run is { State: RunState.Working, SawStart: true } && run.OutputId == StockId.Of(held))
                    FinishRun(machine, run);
                Runs[key] = new MachineRun { State = RunState.Ready };
                return;
            }

            if (held != null && machine.MinutesUntilReady > 0)
            {
                string outputId = StockId.Of(held);
                if (run is { State: RunState.Working } && run.OutputId == outputId)
                    return;

                // A run starting. Only one seen from its very start can be timed.
                bool sawStart = run != null && run.State != RunState.Working;
                MachineRun started = new()
                {
                    Instance = Instance(location, tile),
                    State = RunState.Working,
                    SawStart = sawStart,
                    MachineId = machine.QualifiedItemId,
                    OutputId = outputId,
                    StartClock = Clock(),
                    StartDay = Game1.Date.TotalDays,
                    StartTime = Game1.timeOfDay,
                    StartMinutes = machine.MinutesUntilReady,
                    ByJob = machine.modData.ContainsKey(ModIds.DirectLoadKey)
                };
                Runs[key] = started;

                if (sawStart && !started.ByJob)
                    NoteSetup(started);
                return;
            }

            Runs[key] = new MachineRun { State = RunState.Idle };
        }


        /*********
        ** Public methods: crops
        *********/
        /// <summary>How many days' growth a crop makes a night, against the game's one.</summary>
        public static double CropRate(string harvestId) => Estimate(State.Growth, (harvestId ?? Everything, harvestId == null ? 5 : 3), (Everything, 5));

        /// <summary>Days until a crop is ready, allowing for how fast crops really grow here.</summary>
        public static int CropDays(string harvestId, int days)
        {
            double rate = CropRate(harvestId);
            return rate == 1 || days <= 0 ? days : Math.Max(1, (int)Math.Ceiling(days / rate));
        }

        /// <summary>Notes every watered crop's days left as the day ends. Call as the day ends, before the night.</summary>
        public static void BeforeNight()
        {
            Evening.Clear();
            if (!Enabled || !Context.IsMainPlayer)
                return;

            Utility.ForEachLocation(location =>
            {
                foreach ((Vector2 tile, TerrainFeature feature) in location.terrainFeatures.Pairs)
                {
                    if (feature is not HoeDirt { crop: not null } soil || soil.state.Value != HoeDirt.watered)
                        continue;

                    int? days = CropMath.RawDaysUntilHarvest(soil);
                    string harvest = soil.crop.indexOfHarvest.Value;
                    if (days is > 0 && !string.IsNullOrEmpty(harvest))
                        Evening[(location.NameOrUniqueName, tile)] = (ItemRegistry.QualifyItemId(harvest) ?? harvest, days.Value);
                }
                return true;
            });
        }

        /// <summary>Compares the crops noted last night with this morning. Call first thing in the day.</summary>
        public static void AfterNight()
        {
            if (!Enabled || !Context.IsMainPlayer || Evening.Count == 0)
                return;

            int noted = 0;
            foreach (((string locationName, Vector2 tile), (string harvest, int before)) in Evening)
            {
                GameLocation location = Game1.getLocationFromName(locationName);
                if (location == null || !location.terrainFeatures.TryGetValue(tile, out TerrainFeature feature) || feature is not HoeDirt { crop: not null } soil || soil.crop.dead.Value)
                    continue;

                string now = soil.crop.indexOfHarvest.Value;
                if (string.IsNullOrEmpty(now) || (ItemRegistry.QualifyItemId(now) ?? now) != harvest)
                    continue;

                int? after = CropMath.RawDaysUntilHarvest(soil);
                if (after == null || after > before)
                    continue; // replanted or reset: not growth

                // A crop that finished overnight made at least what it had left; the rest can't be seen.
                double step = before - after.Value;
                Add(State.Growth, harvest, step);
                Add(State.Growth, Everything, step);
                noted++;
            }

            Evening.Clear();
            if (noted > 0)
                Log.Trace($"Calibration: noted overnight growth of {noted} crops; crops grow {CropRate(null):0.##}x the game's rate.");
        }


        /*********
        ** Public methods: prices
        *********/
        /// <summary>What shipping really pays, against the game's own prices.</summary>
        public static double PriceFactor => Estimate(State.Shipping, (Everything, 3));

        /// <summary>Compares a night's shipping payout with what the bin held. Call once the night has paid out.</summary>
        /// <param name="expected">What the bin's contents were worth at the game's own prices.</param>
        /// <param name="paid">What the night actually paid.</param>
        public static void ObserveShipping(long expected, long paid)
        {
            // A small bin says little: one egg's rounding would read as a big change.
            if (!Enabled || !Context.IsMainPlayer || expected < 250 || paid <= 0)
                return;

            Add(State.Shipping, Everything, paid / (double)expected);
        }


        /*********
        ** Public methods: reporting
        *********/
        /// <summary>What's been learned that differs from the game's data, for the terminal and the console.</summary>
        public static List<string> Describe(Func<string, string> getName)
        {
            List<string> lines = new();

            foreach (string machineId in State.Speed.Keys.Concat(State.Setup.Keys.Select(key => key.Split('|')[0])).Where(id => id != Everything && !id.StartsWith("@")).Distinct())
            {
                double factor = MachineFactor(machineId, null);
                if (factor != 1)
                    lines.Add($"{getName(machineId)}: runs take {factor:0.##}x as long ({Samples(State.Speed, machineId) + Samples(State.Setup, machineId)} observations)");
            }

            // Machines that run differently from others of their kind: upgraded or combined ones.
            int individual = State.Setup.Keys.Concat(State.Speed.Keys)
                .Where(key => key.StartsWith("@") && !key.Contains('|'))
                .Distinct()
                .Count(key => Estimate(State.Setup, (key, 1)) * Estimate(State.Speed, (key, 2)) != 1);
            if (individual > 0)
                lines.Add($"{individual} machines run at their own speed (upgraded or combined)");

            double growth = CropRate(null);
            if (growth != 1)
                lines.Add($"Crops: grow {growth:0.##}x as fast ({Samples(State.Growth, Everything)} observations)");

            double price = PriceFactor;
            if (price != 1)
                lines.Add($"Shipping: pays {price:0.##}x the listed price ({Samples(State.Shipping, Everything)} days)");

            return lines;
        }

        /// <summary>How many observations there are in total.</summary>
        public static int ObservationCount => State.Setup.Values.Sum(list => list.Count) + State.Speed.Values.Sum(list => list.Count) + State.Growth.Values.Sum(list => list.Count) + State.Shipping.Values.Sum(list => list.Count);


        /*********
        ** Private methods
        *********/
        /// <summary>Records what the game set a machine's timer to, against its data.</summary>
        private static void NoteSetup(MachineRun run)
        {
            MachineRecipe recipe = FindRecipe?.Invoke(run.MachineId, run.OutputId);
            if (recipe == null || recipe.IsAging)
                return;

            int expected = recipe.BaseDays > 0
                ? Utility.CalculateMinutesUntilMorning(run.StartTime, recipe.BaseDays)
                : recipe.BaseMinutes;
            if (expected < 60)
                return;

            // The game counts timers down ten minutes at a time, and one step may pass before the run is seen: a
            // timer within a step of the data's figure is exactly on it.
            int short_ = expected - run.StartMinutes;
            double ratio = short_ >= 0 && short_ <= 10 ? 1 : run.StartMinutes / (double)expected;
            Add(State.Setup, run.Instance + "|" + run.OutputId, ratio);
            Add(State.Setup, run.Instance, ratio);
            Add(State.Setup, run.MachineId + "|" + run.OutputId, ratio);
            Add(State.Setup, run.MachineId, ratio);
            Add(State.Setup, Everything, ratio);
        }

        /// <summary>Records how fast a finished run's timer ran down.</summary>
        private static void FinishRun(SObject machine, MachineRun run)
        {
            double elapsed = Clock() - run.StartClock;
            int days = Game1.Date.TotalDays - run.StartDay;
            double ratio;

            if (days == 0)
            {
                // Finished the same day: minute against minute.
                if (run.StartMinutes < 30)
                    return;
                ratio = elapsed / run.StartMinutes;
            }
            else if (Game1.timeOfDay > 620)
            {
                // Finished during a later day. A machine's timer counts 1,440 minutes from one morning to the next
                // whenever the player sleeps, as the clock here does, so minute against minute still holds.
                ratio = elapsed / run.StartMinutes;
            }
            else
            {
                // Finished overnight: when in the night can't be seen, so count mornings instead -- how many the
                // timer needed, against how many it took. A run due by its first morning is on time whenever the
                // player went to bed, so that one tells nothing.
                int needed = 1;
                while (needed < 60 && Utility.CalculateMinutesUntilMorning(run.StartTime, needed) < run.StartMinutes)
                    needed++;
                if (needed == 1 && days == 1)
                    return;

                ratio = days / (double)needed;
            }

            Add(State.Speed, run.Instance, ratio);
            Add(State.Speed, run.MachineId, ratio);
            Add(State.Speed, Everything, ratio);
        }

        /// <summary>Adds an observation to a figure, keeping only the latest few.</summary>
        private static void Add(Dictionary<string, List<double>> figures, string key, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < MinFactor || value > MaxFactor)
                return;

            if (!figures.TryGetValue(key, out List<double> list))
                figures[key] = list = new List<double>();

            list.Add(value);
            if (list.Count > Keep)
                list.RemoveRange(0, list.Count - Keep);
            Cached.Clear();
        }

        /// <summary>The best estimate of a figure: the first key with enough observations behind it, as a median.</summary>
        /// <remarks>
        /// What counts as enough depends on how noisy the figure is: a timer the game set is an exact reading, a
        /// crop's growth varies more, and a stand-in for everything should rest on several.
        /// </remarks>
        private static double Estimate(Dictionary<string, List<double>> figures, params (string Key, int Needed)[] keys)
        {
            if (!Enabled)
                return 1;

            string cacheKey = FigureName(figures) + ":" + string.Join(",", keys.Select(key => key.Key + "/" + key.Needed));
            if (Cached.TryGetValue(cacheKey, out double cached))
                return cached;

            double estimate = 1;
            foreach ((string key, int needed) in keys)
            {
                if (key == null || !figures.TryGetValue(key, out List<double> list) || list.Count < needed)
                    continue;

                estimate = Clamp(Median(list));
                break;
            }

            Cached[cacheKey] = estimate;
            return estimate;
        }

        /// <summary>Tells the figures apart in the cache.</summary>
        private static string FigureName(Dictionary<string, List<double>> figures)
        {
            return figures == State.Setup ? "setup" : figures == State.Speed ? "speed" : figures == State.Growth ? "growth" : "shipping";
        }

        /// <summary>The middle of a list.</summary>
        private static double Median(List<double> values)
        {
            List<double> sorted = values.OrderBy(value => value).ToList();
            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }

        /// <summary>Keeps a factor sane, and treats one within rounding of 1 as 1.</summary>
        private static double Clamp(double factor)
        {
            if (Math.Abs(factor - 1) <= Tolerance)
                return 1;
            return Math.Clamp(factor, MinFactor, MaxFactor);
        }

        /// <summary>How many observations back a figure, specific keys included.</summary>
        private static int Samples(Dictionary<string, List<double>> figures, string prefix)
        {
            return figures.Where(pair => pair.Key == prefix || pair.Key.StartsWith(prefix + "|")).Sum(pair => pair.Value.Count);
        }

        /// <summary>The game's clock in minutes since the save began, counting each day as the 1,440 minutes machines see.</summary>
        private static double Clock()
        {
            int hours = Game1.timeOfDay / 100;
            int minutes = Game1.timeOfDay % 100;
            return (Game1.Date.TotalDays * 1440.0) + (((hours - 6) * 60) + minutes);
        }


        /*********
        ** Nested types
        *********/
        private enum RunState { Idle, Working, Ready }

        /// <summary>A machine being timed.</summary>
        private class MachineRun
        {
            public string Instance;
            public RunState State;
            public bool SawStart;
            public bool ByJob;
            public string MachineId;
            public string OutputId;
            public double StartClock;
            public int StartDay;
            public int StartTime;
            public int StartMinutes;
        }

        /// <summary>What's been learned, as saved.</summary>
        internal class CalibrationData
        {
            /// <summary>What the game set timers to against the data, by "machine|output", machine, and overall.</summary>
            public Dictionary<string, List<double>> Setup { get; set; } = new();

            /// <summary>How fast timers ran down against the clock, by machine and overall.</summary>
            public Dictionary<string, List<double>> Speed { get; set; } = new();

            /// <summary>Days' growth a night, by harvest and overall.</summary>
            public Dictionary<string, List<double>> Growth { get; set; } = new();

            /// <summary>Shipping paid against the game's prices.</summary>
            public Dictionary<string, List<double>> Shipping { get; set; } = new();
        }
    }
}
