using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.GameData.Machines;
using SObject = StardewValley.Object;

namespace StardewLogistics.Devices
{
    /// <summary>Moves items between a storage network and a vanilla machine such as a keg or furnace.</summary>
    /// <remarks>
    /// This is the most fragile part of the mod, because it drives the game's machine pipeline from outside the
    /// normal "player walks up and clicks" path. Everything here is therefore conservative: it refuses to touch a
    /// machine it doesn't fully understand, and it hands items back to the network if the machine declines them.
    /// The whole feature can be switched off with the <c>EnableMachineAutomation</c> setting.
    /// </remarks>
    internal static class MachineIO
    {
        /*********
        ** Public methods
        *********/
        /// <summary>Whether an object is a machine rather than a chest or one of the mod's own devices.</summary>
        public static bool IsMachine(SObject obj)
        {
            return obj is not null
                && obj is not StardewValley.Objects.Chest
                && obj.GetMachineData() != null;
        }

        /// <summary>Returns what was in a machine that has just been broken, into the network it was wired to.</summary>
        /// <remarks>
        /// Breaking a working machine normally destroys what's inside. On the network, what went in comes back
        /// instead: the item last put in, plus anything the machine always consumes on top (a furnace's coal).
        /// Finished output, and a cask's item, come back as they are. Whatever storage has no room for drops at
        /// the machine's tile rather than vanishing.
        /// </remarks>
        public static void RefundRemoved(SObject machine, StorageNetwork network, GameLocation location, Vector2 tile)
        {
            if (!IsMachine(machine) || network == null)
                return;

            // An Auto-Grabber's "held object" is the chest it fills, and the game won't let one be picked up
            // unless that chest is empty; there is nothing to return.
            SObject held = machine.heldObject.Value;
            if (held == null || held is StardewValley.Objects.Chest)
                return;

            List<Item> refund = new();
            if (machine.readyForHarvest.Value || machine is StardewValley.Objects.Cask)
                refund.Add(held);
            else
            {
                Item last = machine.lastInputItem.Value;
                if (last != null)
                {
                    Item copy = last.getOne();
                    copy.Stack = Math.Max(1, last.Stack);
                    refund.Add(copy);
                }

                foreach (var extra in machine.GetMachineData()?.AdditionalConsumedItems ?? new List<StardewValley.GameData.Machines.MachineItemAdditionalConsumedItems>())
                {
                    Item item = ItemRegistry.Create(extra.ItemId, Math.Max(1, extra.RequiredCount), allowNull: true);
                    if (item != null)
                        refund.Add(item);
                }
            }

            foreach (Item item in refund)
            {
                string name = item.DisplayName;
                int count = item.Stack;
                network.Insert(item);
                if (item.Stack > 0)
                    Game1.createItemDebris(item, (tile * Game1.tileSize) + new Vector2(Game1.tileSize / 2f), -1, location);

                Log.Debug($"A {machine.DisplayName} was broken while working; returned {count}x {name} to the network.");
            }
        }

        /// <summary>Whether a wired machine can actually run where it's placed.</summary>
        /// <remarks>
        /// A cask ages only where the location allows it -- the cellar, or a modded location that opts in. One
        /// placed anywhere else still attaches to the network but is never planned around or loaded.
        /// </remarks>
        public static bool IsOperable(SObject obj)
        {
            return obj is not StardewValley.Objects.Cask cask || cask.IsValidCaskLocation();
        }

        /// <summary>Takes a finished machine's output onto the network.</summary>
        /// <param name="machine">The machine to collect from.</param>
        /// <param name="network">The network to store the output in.</param>
        /// <returns>The number of items collected.</returns>
        public static int TryCollect(SObject machine, StorageNetwork network)
        {
            if (machine?.heldObject.Value == null || !machine.readyForHarvest.Value)
                return 0;

            // Some machines (crystalariums) restart themselves when their output is collected. The game drives that
            // from the "OutputCollected" trigger, so it's run here too, just as when the player collects.
            if (RestartsOnCollection(machine))
                return CollectAndRestart(machine, network);

            SObject held = machine.heldObject.Value;
            int collected = network.Insert(held);
            if (collected <= 0)
                return 0;

            if (held.Stack <= 0)
            {
                machine.heldObject.Value = null;
                machine.readyForHarvest.Value = false;
                machine.showNextIndex.Value = false;
                machine.minutesUntilReady.Value = 0;
                machine.ResetParentSheetIndex();
            }

            return collected;
        }

        /// <summary>Loads an item from the network into an idle machine.</summary>
        /// <param name="machine">The machine to load.</param>
        /// <param name="network">The network to draw from.</param>
        /// <param name="stack">The network stock entry to feed it.</param>
        /// <returns>The number of items consumed by the machine.</returns>
        public static int TryLoad(SObject machine, StorageNetwork network, NetworkItemStack stack)
        {
            MachineData data = machine?.GetMachineData();
            if (data == null || stack == null || stack.Count <= 0)
                return 0;

            // Only feed a machine that's genuinely idle, so we never overwrite work in progress.
            if (machine.heldObject.Value != null || machine.MinutesUntilReady > 0 || machine.readyForHarvest.Value)
                return 0;

            // Withdraw first and refund the remainder: the machine decides how much it wants (a furnace takes five
            // ore, a keg takes one fruit) and reports it only by reducing the stack we hand it.
            int offered = (int)System.Math.Min(stack.Count, stack.Sample.maximumStackSize());
            Item carrier = network.ExtractMerged(stack.Key, stack.Sample, offered).FirstOrDefault();
            if (carrier == null)
                return 0;

            int before = carrier.Stack;
            bool accepted;
            try
            {
                accepted = machine.PlaceInMachine(data, carrier, probe: false, Game1.player, showMessages: false, playSounds: false);
            }
            catch
            {
                accepted = false;
            }

            int consumed = accepted ? System.Math.Max(0, before - carrier.Stack) : 0;

            // A machine that accepted the input but left the stack untouched still ate one item; assume the minimum
            // rather than duplicating it back into storage.
            if (accepted && consumed == 0)
                consumed = 1;

            carrier.Stack = before - consumed;
            if (carrier.Stack > 0)
                network.Insert(carrier);

            return consumed;
        }


        /// <summary>Collects a producer the game runs with its own code, and sets it going again as the game does.</summary>
        /// <returns>The number of items collected.</returns>
        public static int TryCollectProducer(SObject producer, StorageNetwork network)
        {
            return producer switch
            {
                CrabPot pot => TryCollectCrabPot(pot, network),
                _ => TryCollectTapper(producer, network)
            };
        }

        /// <summary>Collects a crab pot's catch and baits it again from storage, as the player would.</summary>
        /// <remarks>
        /// Collecting uses up the bait, as it does by hand; the pot catches again overnight only with fresh bait,
        /// unless its owner doesn't need any. The bait is whatever bait storage holds, cheapest first.
        /// </remarks>
        private static int TryCollectCrabPot(CrabPot pot, StorageNetwork network)
        {
            SObject held = pot.heldObject.Value;
            if (held == null || !pot.readyForHarvest.Value || !network.HasRoomFor(held, held.Stack))
                return 0;

            IncomeForecast.RememberCatch(pot, Framework.Selling.Value(held, held.Stack));

            int collected = network.Insert(held);
            if (held.Stack > 0)
                return collected;

            pot.heldObject.Value = null;
            pot.readyForHarvest.Value = false;
            pot.bait.Value = null;
            pot.tileIndexToShow = 710;

            if (pot.NeedsBait(Game1.MasterPlayer))
            {
                NetworkItemStack bait = network.Aggregate()
                    .Where(entry => entry.Sample?.Category == SObject.baitCategory)
                    .OrderBy(entry => entry.Sample.salePrice())
                    .FirstOrDefault();
                if (bait != null && network.ExtractMerged(bait.Key, bait.Sample, 1).FirstOrDefault() is SObject taken)
                    pot.bait.Value = taken;
            }

            return collected;
        }

        /// <summary>Collects a tapper's product and sets the tree producing the next, as the game does for the player.</summary>
        /// <returns>The number of items collected.</returns>
        private static int TryCollectTapper(SObject tapper, StorageNetwork network)
        {
            if (tapper?.heldObject.Value == null || !tapper.readyForHarvest.Value || !tapper.IsTapper())
                return 0;

            SObject held = tapper.heldObject.Value;
            if (!network.HasRoomFor(held, held.Stack))
                return 0;

            SObject previous = (SObject)held.getOne();
            int collected = network.Insert(held);
            if (held.Stack > 0)
                return collected;

            tapper.heldObject.Value = null;
            tapper.readyForHarvest.Value = false;
            if (tapper.Location?.terrainFeatures.TryGetValue(tapper.TileLocation, out TerrainFeature feature) == true && feature is Tree tree)
                tree.UpdateTapperProduct(tapper, previous);

            return collected;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Collects a machine that restarts when collected, and restarts it.</summary>
        /// <remarks>
        /// Only when storage has room for the whole output: a partial collection would leave the machine holding the
        /// rest, and restarting it then would overwrite what's left.
        /// </remarks>
        private static int CollectAndRestart(SObject machine, StorageNetwork network)
        {
            SObject held = machine.heldObject.Value;
            if (!network.HasRoomFor(held, held.Stack))
                return 0;

            Item collectedSample = held.getOne();
            int collected = network.Insert(held);
            if (held.Stack > 0)
                return collected;

            machine.heldObject.Value = null;
            machine.readyForHarvest.Value = false;
            machine.showNextIndex.Value = false;
            machine.ResetParentSheetIndex();

            try
            {
                MachineData data = machine.GetMachineData();
                GameLocation location = machine.Location;
                if (data != null && location != null
                    && MachineDataUtility.TryGetMachineOutputRule(machine, data, MachineOutputTrigger.OutputCollected, collectedSample, Game1.player, location, out MachineOutputRule rule, out _, out _, out _))
                    machine.OutputMachine(data, rule, machine.lastInputItem.Value, Game1.player, location, probe: false);
            }
            catch (System.Exception ex)
            {
                Log.Trace($"Couldn't restart the {machine.DisplayName} at {machine.TileLocation} after collecting it: {ex.Message}");
            }

            return collected;
        }

        /// <summary>Whether the game restarts this machine as part of collecting its output.</summary>
        private static bool RestartsOnCollection(SObject machine)
        {
            MachineData data = machine.GetMachineData();
            if (data?.OutputRules == null)
                return false;

            foreach (MachineOutputRule rule in data.OutputRules)
            {
                if (rule != null && rule.Triggers != null && rule.Triggers.Any(trigger => trigger.Trigger.HasFlag(MachineOutputTrigger.OutputCollected)))
                    return true;
            }

            return false;
        }
    }
}
