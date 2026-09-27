using System;
using System.Collections.Generic;
using System.Linq;
using StardewLogistics.Network;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.Inventories;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewLogistics.Framework
{
    /// <summary>How fast each particular machine on a network works through a recipe.</summary>
    /// <remarks>
    /// Machines of one kind needn't work alike. Mods upgrade machines one at a time, or combine several into one
    /// that works faster or takes bigger batches, and nothing in the game's data says so. Each machine is asked
    /// instead, the way the game would find out: a stand-in copy of it -- same kind, same <c>modData</c> -- is
    /// loaded with sample inputs through the game's own loading, and the timer it gets and how much it takes are
    /// read off. That's done once per machine and recipe, and again if the machine's data changes, as it does when
    /// another machine is combined into it.
    ///
    /// The stand-in isn't in the world: it stands in an empty location of its own, so its loading sound and
    /// effects go nowhere, and any stats the load counts are put back. What's been seen of the machine really
    /// running (see <see cref="Calibration"/>) is added on top: how fast its timer really runs down.
    /// </remarks>
    internal static class MachinePaces
    {
        /*********
        ** Fields
        *********/
        /// <summary>How many runs' worth of inputs a stand-in is offered, so a machine that takes bigger batches can.</summary>
        private const int OfferedRuns = 10;

        /// <summary>What each stand-in load showed, by machine, recipe and the machine's data.</summary>
        private static readonly Dictionary<string, (double Setup, int Runs)?> Measured = new();

        /// <summary>An empty place for stand-ins, away from the world.</summary>
        private static GameLocation Nowhere;


        /*********
        ** Public methods
        *********/
        /// <summary>How long a load takes, in the minutes a plan counts, and how many runs it covers, for each machine on a network that can make a recipe, fastest first.</summary>
        public static List<MachinePace> For(StorageNetwork network, MachineRecipe recipe)
        {
            List<MachinePace> paces = new();
            if (network == null || recipe == null || recipe.IsAging)
                return paces;

            int baseMinutes = recipe.BaseMinutes + (recipe.BaseDays * CraftPlan.MinutesPerDay);
            if (baseMinutes <= 0)
                return paces;

            foreach (NetworkNode node in network.Machines)
            {
                SObject machine = node.Object;
                if (machine == null || !string.Equals(machine.QualifiedItemId, recipe.MachineId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!node.AcceptsInput(StockId.BaseId(recipe.InputId)) || !Devices.MachineIO.IsOperable(machine))
                    continue;

                GameLocation location = node.Location ?? network.Location;
                (double setup, int runs) = Measure(machine, location, node.Tile, recipe) ?? (Calibration.SetupFactor(recipe.MachineId, recipe.OutputId, location, node.Tile), 1);
                double speed = Calibration.SpeedFactor(recipe.MachineId, location, node.Tile);

                paces.Add(new MachinePace(node, Math.Max(10, (int)Math.Round(baseMinutes * setup * speed)), Math.Max(1, runs)));
            }

            return paces.OrderByDescending(pace => pace.Runs / (double)pace.Minutes).ToList();
        }

        /// <summary>Forgets every measurement, for leaving the save.</summary>
        public static void Reset()
        {
            Measured.Clear();
            Nowhere = null;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Loads a stand-in for a machine to see what timer it gets, against the data's, and how many runs it takes.</summary>
        /// <returns>The timer against the data's and the runs taken, or <c>null</c> if the stand-in wouldn't load.</returns>
        private static (double Setup, int Runs)? Measure(SObject machine, GameLocation location, Microsoft.Xna.Framework.Vector2 tile, MachineRecipe recipe)
        {
            string key = $"{location?.NameOrUniqueName}:{tile}|{recipe.Key}|{Signature(machine)}";
            if (Measured.TryGetValue(key, out (double, int)? known))
                return known;

            (double Setup, int Runs)? result = null;
            try
            {
                result = LoadStandIn(machine, tile, recipe);
            }
            catch (Exception ex)
            {
                Log.Trace($"Couldn't try a stand-in {machine.DisplayName} for {recipe.OutputName}: {ex.Message}");
            }

            Measured[key] = result;
            if (result is (double setup, int runs))
            {
                Calibration.NoteMeasured(recipe.MachineId, recipe.OutputId, location, tile, setup);
                if (setup != 1 || runs != 1)
                    Log.Trace($"The {machine.DisplayName} at {location?.NameOrUniqueName} {tile} takes {runs} runs of {recipe.OutputName} at a time, {setup:0.##}x as long as its data says.");
            }
            return result;
        }

        /// <summary>Loads a copy of a machine, off in an empty location, and reads off what it did.</summary>
        private static (double Setup, int Runs)? LoadStandIn(SObject machine, Microsoft.Xna.Framework.Vector2 tile, MachineRecipe recipe)
        {
            MachineData data = machine.GetMachineData();
            if (data == null || machine is Cask || !Context.IsWorldReady)
                return null;

            // A copy with everything the machine knows about itself.
            if (machine.getOne() is not SObject standIn)
                return null;
            foreach (KeyValuePair<string, string> pair in machine.modData.Pairs)
                standIn.modData[pair.Key] = pair.Value;
            standIn.heldObject.Value = null;
            standIn.readyForHarvest.Value = false;
            standIn.minutesUntilReady.Value = 0;
            standIn.TileLocation = tile;
            standIn.Location = Nowhere ??= new GameLocation();

            // Sample inputs, several runs' worth.
            Inventory feed = new();
            List<(Item Item, int Stack)> fed = new();
            if (!Offer(feed, fed, recipe.InputId, recipe.InputCount * OfferedRuns, recipe.InputQuality))
                return null;
            foreach (ItemCost extra in recipe.ExtraInputs)
            {
                if (!Offer(feed, fed, extra.ItemId, extra.Count * OfferedRuns, extra.RequiredQuality))
                    return null;
            }

            // The load counts towards the player's stats; this one didn't happen.
            Dictionary<string, uint> stats = new(Game1.player.stats.Values);
            bool loaded;
            try
            {
                loaded = standIn.AttemptAutoLoad(feed, Game1.player);
            }
            finally
            {
                Game1.player.stats.Values.Clear();
                foreach ((string name, uint value) in stats)
                    Game1.player.stats.Values[name] = value;
            }

            SObject held = standIn.heldObject.Value;
            if (!loaded || held == null || !string.Equals(StockId.Of(held), recipe.OutputId, StringComparison.OrdinalIgnoreCase))
                return null;

            // The timer, against what the data would give at this time of day.
            int expected = recipe.BaseDays > 0 ? Utility.CalculateMinutesUntilMorning(Game1.timeOfDay, recipe.BaseDays) : recipe.BaseMinutes;
            if (expected <= 0)
                return null;
            int timer = standIn.MinutesUntilReady;
            double setup = Math.Abs(expected - timer) <= 10 ? 1 : timer / (double)expected;

            // How many runs it took: the main input used, in runs.
            int perRun = Math.Max(1, recipe.InputCount);
            int used = fed.Where(entry => StockId.Matches(entry.Item, recipe.InputId)).Sum(entry => entry.Stack - Math.Max(0, entry.Item.Stack));
            return (setup, Math.Max(1, used / perRun));
        }

        /// <summary>Adds sample items to a stand-in's inputs.</summary>
        /// <returns>Whether a sample could be made.</returns>
        private static bool Offer(Inventory feed, List<(Item Item, int Stack)> fed, string itemId, int count, int quality)
        {
            Item sample = StockId.Create(itemId);
            if (sample == null)
                return false;

            sample.Stack = Math.Max(1, Math.Min(count, sample.maximumStackSize()));
            if (quality > 0)
                sample.Quality = quality;
            feed.Add(sample);
            fed.Add((sample, sample.Stack));
            return true;
        }

        /// <summary>A short summary of a machine's own data, so a change to it -- another machine combined in -- is noticed.</summary>
        private static string Signature(SObject machine)
        {
            return string.Join(";", machine.modData.Pairs.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)) + "#" + machine.Stack;
        }
    }

    /// <summary>How fast one machine works through a recipe.</summary>
    /// <param name="Node">The machine.</param>
    /// <param name="Minutes">How long one load takes, in the minutes a plan counts.</param>
    /// <param name="Runs">How many runs one load covers.</param>
    internal sealed record MachinePace(NetworkNode Node, int Minutes, int Runs);
}
