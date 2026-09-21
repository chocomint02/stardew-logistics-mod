using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewValley;
using SObject = StardewValley.Object;

namespace StardewLogistics.Devices
{
    /// <summary>Keeps wired machines flowing: collects finished output into storage on a timer.</summary>
    /// <remarks>
    /// This replaces the import and export buses. Wiring a machine to a cable is now the whole configuration, so
    /// anything the network can see and that has finished its work gets emptied into storage automatically. Loading
    /// machines is deliberately not done here: that belongs to the autocrafting scheduler, which decides what should
    /// be made rather than shovelling in whatever is to hand.
    ///
    /// Runs on the host only. Farmhands see the results through the game's own object synchronisation, so running it
    /// everywhere would move each item once per player.
    /// </remarks>
    internal class NetworkTicker
    {
        /*********
        ** Fields
        *********/
        private readonly NetworkManager Networks;
        private readonly ModConfig Config;


        /*********
        ** Public methods
        *********/
        public NetworkTicker(NetworkManager networks, ModConfig config)
        {
            this.Networks = networks;
            this.Config = config;
        }

        /// <summary>Services every network in the world once.</summary>
        public void Run()
        {
            if (!this.Config.EnableMachineAutomation)
                return;

            Utility.ForEachLocation(location =>
            {
                foreach (StorageNetwork network in this.Networks.GetNetworks(location))
                    this.RunNetwork(network);
                return true;
            });
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Collects finished output from one network's machines.</summary>
        private void RunNetwork(StorageNetwork network)
        {
            // With nowhere to put the output there's no point disturbing the machines.
            if (network.Storages.Count == 0)
                return;

            int collected = 0;

            foreach (NetworkNode node in network.Machines)
            {
                SObject machine = node.Object;
                if (machine?.heldObject.Value == null || !machine.readyForHarvest.Value)
                    continue;

                int moved = MachineIO.TryCollect(machine, network);
                if (moved > 0)
                    collected += moved;
            }

            if (collected > 0)
                Log.Trace($"Collected {collected} items from machines on the {network.Location?.NameOrUniqueName} network.");
        }
    }
}
