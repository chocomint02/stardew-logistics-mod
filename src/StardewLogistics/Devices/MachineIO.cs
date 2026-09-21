using System.Linq;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewValley;
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

        /// <summary>Takes a finished machine's output onto the network.</summary>
        /// <param name="machine">The machine to collect from.</param>
        /// <param name="network">The network to store the output in.</param>
        /// <returns>The number of items collected.</returns>
        public static int TryCollect(SObject machine, StorageNetwork network)
        {
            if (machine?.heldObject.Value == null || !machine.readyForHarvest.Value)
                return 0;

            // Some machines (tappers, crystalariums) restart themselves when the player collects their output, and
            // the game drives that from the "OutputCollected" trigger during a real collection. Emptying them from
            // here would silently switch them off, so leave those to the player.
            if (RestartsOnCollection(machine))
                return 0;

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


        /*********
        ** Private methods
        *********/
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
