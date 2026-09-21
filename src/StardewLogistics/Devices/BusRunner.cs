using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewValley;
using StardewValley.Network;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewLogistics.Devices
{
    /// <summary>Runs the import and export buses on every network in the world.</summary>
    /// <remarks>
    /// Buses run on the host only. Farmhands see the results through the game's own object synchronisation, which
    /// avoids every player moving the same items and duplicating them.
    /// </remarks>
    internal class BusRunner
    {
        /*********
        ** Fields
        *********/
        private static readonly Vector2[] Directions =
        {
            new(0, -1),
            new(0, 1),
            new(-1, 0),
            new(1, 0)
        };

        private readonly NetworkManager Networks;
        private readonly ModConfig Config;


        /*********
        ** Public methods
        *********/
        public BusRunner(NetworkManager networks, ModConfig config)
        {
            this.Networks = networks;
            this.Config = config;
        }

        /// <summary>Runs every bus in the world once.</summary>
        public void Run()
        {
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
        /// <summary>Runs the buses attached to one network.</summary>
        private void RunNetwork(StorageNetwork network)
        {
            foreach (NetworkNode bus in network.GetNodes(NodeKind.ImportBus))
                this.RunImportBus(network, bus);

            List<NetworkNode> exporters = network.GetNodes(NodeKind.ExportBus).ToList();
            if (exporters.Count == 0)
                return;

            // Aggregating is the expensive part, so do it once and let every export bus share the snapshot,
            // decrementing it as stock is withdrawn.
            List<NetworkItemStack> stock = network.Aggregate();
            foreach (NetworkNode bus in exporters)
                this.RunExportBus(network, bus, stock);
        }

        /// <summary>Pulls items from the chests and machines around an import bus onto the network.</summary>
        private void RunImportBus(StorageNetwork network, NetworkNode bus)
        {
            ItemFilter filter = bus.GetFilter();
            int budget = this.Config.BusItemsPerRun;

            foreach (SObject neighbour in this.GetTargets(network, bus))
            {
                if (budget <= 0)
                    break;

                if (neighbour is Chest chest)
                {
                    budget -= this.ImportFromChest(chest, network, filter, budget);
                }
                else if (this.Config.EnableMachineAutomation && MachineIO.IsMachine(neighbour))
                {
                    SObject held = neighbour.heldObject.Value;
                    if (held != null && neighbour.readyForHarvest.Value && filter.Accepts(held, acceptAllWhenEmpty: true))
                        budget -= MachineIO.TryCollect(neighbour, network);
                }
            }
        }

        /// <summary>Pushes filtered items from the network into the chests and machines around an export bus.</summary>
        private void RunExportBus(StorageNetwork network, NetworkNode bus, List<NetworkItemStack> stock)
        {
            ItemFilter filter = bus.GetFilter();

            // An export bus with nothing configured exports nothing, the same as in Applied Energistics. Without
            // that rule a single bus would try to drain the entire network into the first chest it touches.
            // A deny list still counts as configured: it exports everything except what it names.
            if (filter.IsEmpty)
                return;

            int budget = this.Config.BusItemsPerRun;

            foreach (SObject neighbour in this.GetTargets(network, bus))
            {
                if (budget <= 0)
                    break;

                List<NetworkItemStack> candidates = stock
                    .Where(entry => entry.Count > 0 && filter.Accepts(entry.Sample, acceptAllWhenEmpty: false))
                    .ToList();
                if (candidates.Count == 0)
                    break;

                if (neighbour is Chest chest)
                {
                    if (IsBusy(chest))
                        continue;

                    foreach (NetworkItemStack entry in candidates)
                    {
                        if (budget <= 0)
                            break;
                        budget -= this.ExportToChest(chest, network, entry, budget);
                    }
                }
                else if (this.Config.EnableMachineAutomation && MachineIO.IsMachine(neighbour))
                {
                    foreach (NetworkItemStack entry in candidates)
                    {
                        int consumed = MachineIO.TryLoad(neighbour, network, entry);
                        if (consumed > 0)
                        {
                            entry.Count -= consumed;
                            budget -= consumed;
                            break; // a machine only takes one input at a time
                        }
                    }
                }
            }
        }

        /// <summary>Moves matching items out of a chest and onto the network.</summary>
        /// <returns>The number of items moved.</returns>
        private int ImportFromChest(Chest chest, StorageNetwork network, ItemFilter filter, int budget)
        {
            if (IsBusy(chest))
                return 0;

            int moved = 0;
            IList<Item> items = chest.Items;
            bool changed = false;

            for (int i = 0; i < items.Count && moved < budget; i++)
            {
                Item item = items[i];
                if (item == null || !filter.Accepts(item, acceptAllWhenEmpty: true))
                    continue;

                int want = System.Math.Min(budget - moved, item.Stack);
                Item carrier = item.getOne();
                carrier.Stack = want;

                int inserted = network.Insert(carrier);
                if (inserted <= 0)
                    break; // the network is full, so there's no point trying the remaining slots

                item.Stack -= inserted;
                if (item.Stack <= 0)
                    items[i] = null;

                moved += inserted;
                changed = true;
            }

            if (changed)
                chest.clearNulls();

            return moved;
        }

        /// <summary>Moves one kind of item from the network into a chest.</summary>
        /// <returns>The number of items moved.</returns>
        private int ExportToChest(Chest chest, StorageNetwork network, NetworkItemStack entry, int budget)
        {
            int want = (int)System.Math.Min(System.Math.Min(entry.Count, budget), entry.Sample.maximumStackSize());
            if (want <= 0)
                return 0;

            int moved = 0;
            foreach (Item withdrawn in network.ExtractMerged(entry.Key, entry.Sample, want))
            {
                int before = withdrawn.Stack;
                Item leftover = chest.addItem(withdrawn);
                int accepted = before - (leftover?.Stack ?? 0);
                moved += accepted;

                // Whatever the chest wouldn't take goes straight back, so nothing is ever lost in transit.
                if (leftover != null && leftover.Stack > 0)
                {
                    network.Insert(leftover);
                    break;
                }
            }

            entry.Count -= moved;
            return moved;
        }

        /// <summary>Returns the objects a bus may interact with: its orthogonal neighbours, minus the network itself.</summary>
        private IEnumerable<SObject> GetTargets(StorageNetwork network, NetworkNode bus)
        {
            GameLocation location = network.Location;

            foreach (Vector2 direction in Directions)
            {
                Vector2 tile = bus.Tile + direction;

                if (!location.Objects.TryGetValue(tile, out SObject obj) || obj == null)
                    continue;

                // Never route through the mod's own devices, and never treat a chest that's already part of the
                // network as an external target: that would be a bus shuffling items in a circle.
                if (NetworkNode.GetKind(obj.ItemId) != null)
                    continue;
                if (network.Storages.Any(storage => storage.Tile == tile))
                    continue;

                yield return obj;
            }
        }

        /// <summary>Whether another player has a chest open, in which case it shouldn't be edited underneath them.</summary>
        private static bool IsBusy(Chest chest)
        {
            NetMutex mutex = chest.GetMutex();
            return mutex != null && mutex.IsLocked() && !mutex.IsLockHeld();
        }
    }
}
