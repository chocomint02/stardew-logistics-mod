using System;
using System.Collections.Generic;
using System.Linq;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Objects;

namespace StardewLogistics.Devices
{
    /// <summary>A shipping bin a network is wired to, and how much it can hold.</summary>
    internal class ShippingBinSink
    {
        /// <summary>What's in the bin, sold overnight.</summary>
        public IInventory Items { get; init; }

        /// <summary>How many stacks it holds; the farm's own bin has no limit.</summary>
        public int Capacity { get; init; }

        /// <summary>Whether this is the farm's Shipping Bin building, rather than a Mini-Shipping Bin.</summary>
        public bool IsBuilding { get; init; }
    }

    /// <summary>Moves items between a network's storage and the shipping bins it's wired to.</summary>
    /// <remarks>
    /// A bin is the network's to fill and to empty: whatever is in it is sold overnight, and can be taken back into
    /// storage until then. The farm's Shipping Bin building takes any amount; a Mini-Shipping Bin only as much as it
    /// has slots for. Only what the shipping bin itself accepts is ever moved.
    /// </remarks>
    internal static class ShippingService
    {
        /*********
        ** Public methods
        *********/
        /// <summary>The shipping bins a network reaches, the farm's building first.</summary>
        public static List<ShippingBinSink> GetBins(StorageNetwork network)
        {
            List<ShippingBinSink> bins = new();
            if (network == null)
                return bins;

            foreach (NetworkNode node in network.GetNodes(NodeKind.ShippingBin))
            {
                ShippingBinSink sink = node.Object is Chest mini
                    ? new ShippingBinSink { Items = mini.GetItemsForPlayer(), Capacity = mini.GetActualCapacity(), IsBuilding = false }
                    : Game1.getFarm() is Farm farm
                        ? new ShippingBinSink { Items = farm.getShippingBin(Game1.player), Capacity = int.MaxValue, IsBuilding = true }
                        : null;

                if (sink?.Items != null && !bins.Any(existing => ReferenceEquals(existing.Items, sink.Items)))
                    bins.Add(sink);
            }

            return bins.OrderByDescending(bin => bin.IsBuilding).ToList();
        }

        /// <summary>Ships a number of one stored item: takes it from storage and puts it in the bins.</summary>
        /// <returns>How many went into a bin. Anything the bins had no room for stays in storage.</returns>
        public static int Ship(StorageNetwork network, NetworkItemStack entry, int count)
        {
            if (network == null || entry == null || count <= 0 || !Selling.CanSell(entry.Sample))
                return 0;

            List<ShippingBinSink> bins = GetBins(network);
            if (bins.Count == 0)
                return 0;

            int shipped = 0;
            Item last = null;
            foreach (Item item in network.Extract(entry.Key, entry.Sample, count))
            {
                int before = item.Stack;
                foreach (ShippingBinSink bin in bins)
                {
                    if (item.Stack <= 0)
                        break;
                    AddToBin(bin, item);
                }

                shipped += before - Math.Max(0, item.Stack);
                last = item;

                // Full bins: what's left goes back where it came from.
                if (item.Stack > 0)
                    network.Insert(item);
            }

            if (shipped > 0 && last != null && Game1.getFarm() is Farm farm)
                farm.lastItemShipped = last.getOne();

            return shipped;
        }

        /// <summary>Takes an item back out of a bin into storage.</summary>
        /// <returns>How many went back; what storage has no room for stays in the bin.</returns>
        public static int Return(StorageNetwork network, Item item)
        {
            if (network == null || item == null)
                return 0;

            foreach (ShippingBinSink bin in GetBins(network))
            {
                int index = bin.Items.IndexOf(item);
                if (index < 0)
                    continue;

                int before = item.Stack;
                network.Insert(item);
                int moved = before - Math.Max(0, item.Stack);

                if (item.Stack <= 0)
                    bin.Items.RemoveAt(index);
                return moved;
            }

            return 0;
        }

        /// <summary>Everything waiting in a network's bins.</summary>
        public static List<Item> GetContents(StorageNetwork network)
        {
            return GetBins(network).SelectMany(bin => bin.Items).Where(item => item != null).ToList();
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Puts as much of an item into a bin as it will take, topping up stacks already there first.</summary>
        private static void AddToBin(ShippingBinSink bin, Item item)
        {
            foreach (Item existing in bin.Items)
            {
                if (item.Stack <= 0)
                    return;
                if (existing != null && existing.canStackWith(item))
                    item.Stack = existing.addToStack(item);
            }

            if (item.Stack <= 0)
                return;

            // A Mini-Shipping Bin has a fixed number of slots, some of which may be empty gaps.
            int empty = bin.Items.IndexOf(null);
            if (empty >= 0)
            {
                bin.Items[empty] = item.getOne();
                bin.Items[empty].Stack = item.Stack;
                item.Stack = 0;
                return;
            }

            if (bin.Items.Count(existing => existing != null) >= bin.Capacity)
                return;

            Item copy = item.getOne();
            copy.Stack = item.Stack;
            bin.Items.Add(copy);
            item.Stack = 0;
        }
    }
}
