using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewValley;
using StardewValley.Inventories;

namespace StardewLogistics.Network
{
    /// <summary>One connected storage network: a set of cables plus every device and chest touching them.</summary>
    /// <remarks>
    /// A network is rebuilt from the world by <see cref="NetworkScanner"/> whenever the tiles around it change, so it
    /// is a short-lived view rather than saved state. Item totals are never cached: the terminal re-aggregates on
    /// demand, which keeps it correct even when chests are edited by the player, by another mod, or by a farmhand.
    /// </remarks>
    internal class StorageNetwork
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The location this network lives in. Networks don't span locations.</summary>
        public GameLocation Location { get; }

        /// <summary>The cable tiles this network is laid across.</summary>
        /// <remarks>Cables are floor, not objects, so the network's shape is a set of tiles rather than a node list.</remarks>
        public IReadOnlyCollection<Vector2> CableTiles { get; }

        /// <summary>Every device attached to the network.</summary>
        public IReadOnlyList<NetworkNode> Nodes { get; }

        /// <summary>The vanilla machines wired to the network.</summary>
        public IEnumerable<NetworkNode> Machines => this.GetNodes(NodeKind.Machine);

        /// <summary>The terminals wired to the network.</summary>
        public IEnumerable<NetworkNode> Terminals => this.Nodes.Where(node => node.IsTerminal);

        /// <summary>Every chest attached to the network, ordered by descending priority.</summary>
        public IReadOnlyList<StorageEntry> Storages { get; }

        /// <summary>The total number of stacks the attached chests can hold.</summary>
        public int TotalSlots => this.Storages.Sum(entry => entry.Capacity);

        /// <summary>The number of stacks currently in use.</summary>
        public int UsedSlots => this.Storages.Sum(entry => entry.UsedSlots);

        /// <summary>The number of stacks still free.</summary>
        public int FreeSlots => Math.Max(0, this.TotalSlots - this.UsedSlots);


        /*********
        ** Public methods
        *********/
        public StorageNetwork(GameLocation location, IReadOnlyCollection<Vector2> cableTiles, List<NetworkNode> nodes, List<StorageEntry> storages)
        {
            this.Location = location;
            this.CableTiles = cableTiles;
            this.Nodes = nodes;
            this.Storages = storages.OrderByDescending(entry => entry.Priority).ToList();
        }

        /// <summary>Counts the machines that could run a recipe, respecting the filters set on them.</summary>
        /// <remarks>
        /// Used for planning, for the machine budget, and by the scheduler, so a filtered-out machine is absent
        /// from the plan rather than being planned for and then skipped at run time.
        /// </remarks>
        public int CountUsableMachines(MachineRecipe recipe)
        {
            if (recipe == null)
                return 0;

            return this.Machines.Count(node =>
                string.Equals(node.Object?.QualifiedItemId, recipe.MachineId, StringComparison.OrdinalIgnoreCase)
                && node.AcceptsInput(StockId.BaseId(recipe.InputId)));
        }

        /// <summary>Returns every node of a given role.</summary>
        public IEnumerable<NetworkNode> GetNodes(NodeKind kind) => this.Nodes.Where(node => node.Kind == kind);


        /*********
        ** Reading stock
        *********/
        /// <summary>Collapses every item on the network into one entry per distinct item.</summary>
        /// <remarks>This is O(total stacks) with a dictionary lookup per stack, so it stays cheap even for networks holding thousands of chests.</remarks>
        public List<NetworkItemStack> Aggregate()
        {
            Dictionary<ItemKey, NetworkItemStack> totals = new();
            int uniqueCounter = 0;

            foreach (StorageEntry entry in this.Storages)
            {
                IList<Item> items = entry.Chest.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    Item item = items[i];
                    if (item == null)
                        continue;

                    ItemKey key = ItemKey.From(item, ++uniqueCounter);
                    if (totals.TryGetValue(key, out NetworkItemStack existing))
                        existing.Count += item.Stack;
                    else
                        totals[key] = new NetworkItemStack(key, item, item.Stack);
                }
            }

            return totals.Values.ToList();
        }

        /// <summary>Counts how many of an item the network holds, across every quality and variant.</summary>
        public long CountById(string qualifiedItemId)
        {
            if (string.IsNullOrEmpty(qualifiedItemId))
                return 0;

            long total = 0;
            foreach (StorageEntry entry in this.Storages)
            {
                IList<Item> items = entry.Chest.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    if (StockId.Matches(items[i], qualifiedItemId))
                        total += items[i].Stack;
                }
            }

            return total;
        }

        /// <summary>Removes a number of an item by ID, taking the lowest quality first.</summary>
        /// <remarks>Autocrafting works in item IDs rather than the terminal's quality-aware keys, and spending
        /// the worst stock first leaves the player's good produce alone.</remarks>
        public List<Item> ExtractById(string qualifiedItemId, int count)
        {
            List<Item> taken = new();
            if (string.IsNullOrEmpty(qualifiedItemId) || count <= 0)
                return taken;

            int remaining = count;

            foreach (StorageEntry entry in this.Storages.OrderBy(e => e.Priority))
            {
                if (remaining <= 0)
                    break;
                if (entry.IsBusy)
                    continue;

                IList<Item> items = entry.Chest.Items;
                bool changed = false;

                for (int i = 0; i < items.Count && remaining > 0; i++)
                {
                    Item item = items[i];
                    if (!StockId.Matches(item, qualifiedItemId))
                        continue;

                    int take = Math.Min(remaining, item.Stack);
                    if (take >= item.Stack)
                    {
                        items[i] = null;
                        taken.Add(item);
                    }
                    else
                    {
                        Item split = item.getOne();
                        split.Stack = take;
                        item.Stack -= take;
                        taken.Add(split);
                    }

                    remaining -= take;
                    changed = true;
                }

                if (changed)
                    entry.Chest.clearNulls();
            }

            return taken;
        }

        /// <summary>Counts how many of an item the network holds.</summary>
        public long CountOf(ItemKey key)
        {
            long total = 0;
            foreach (StorageEntry entry in this.Storages)
            {
                IList<Item> items = entry.Chest.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    Item item = items[i];
                    if (item != null && ItemKey.From(item).Equals(key.WithoutUnique()))
                        total += item.Stack;
                }
            }
            return total;
        }

        /// <summary>Returns the attached chests' inventories, for APIs such as the vanilla crafting page that take material containers.</summary>
        /// <remarks>
        /// The crafting page takes <c>List&lt;IInventory&gt;</c>, while <see cref="StardewValley.Objects.Chest.Items"/>
        /// is the concrete <c>Inventory</c>, so the projection needs an explicit cast to land on the right list type.
        /// </remarks>
        public List<IInventory> GetMaterialInventories()
        {
            return this.Storages
                .Where(entry => !entry.IsBusy)
                .Select(entry => (IInventory)entry.Chest.Items)
                .ToList();
        }


        /*********
        ** Moving stock
        *********/
        /// <summary>Stores as much of an item as will fit, filling high-priority chests first.</summary>
        /// <param name="item">The stack to store. Its <see cref="Item.Stack"/> is reduced by the amount accepted.</param>
        /// <returns>The number of items stored.</returns>
        public int Insert(Item item)
        {
            if (item == null || item.Stack <= 0)
                return 0;

            int remaining = item.Stack;
            int original = remaining;

            // Pass one: chests that explicitly ask for this item, so partitioned storage wins over general storage
            // even when a catch-all chest has a higher priority number.
            remaining = this.InsertPass(item, remaining, partitionedOnly: true);

            // Pass two: anything else that will take it.
            if (remaining > 0)
                remaining = this.InsertPass(item, remaining, partitionedOnly: false);

            item.Stack = remaining;
            return original - remaining;
        }

        /// <summary>Removes items from the network.</summary>
        /// <param name="key">The item to withdraw.</param>
        /// <param name="sample">The sample item from the matching <see cref="NetworkItemStack"/>, used to find one-of-a-kind items such as tools.</param>
        /// <param name="count">The most items to withdraw.</param>
        /// <returns>The withdrawn stacks, which are detached from their chests and safe to hand to the player.</returns>
        public List<Item> Extract(ItemKey key, Item sample, int count)
        {
            List<Item> results = new();
            if (count <= 0 || !key.IsValid)
                return results;

            int remaining = count;

            // Drain the lowest-priority chests first, so the chests the player marked as important stay stocked.
            foreach (StorageEntry entry in this.Storages.OrderBy(e => e.Priority))
            {
                if (remaining <= 0)
                    break;
                if (entry.IsBusy)
                    continue;

                IList<Item> items = entry.Chest.Items;
                bool changed = false;

                for (int i = 0; i < items.Count && remaining > 0; i++)
                {
                    Item item = items[i];
                    if (item == null || !Matches(item, key, sample))
                        continue;

                    int take = Math.Min(remaining, item.Stack);
                    if (take >= item.Stack)
                    {
                        items[i] = null;
                        results.Add(item);
                    }
                    else
                    {
                        Item split = item.getOne();
                        split.Stack = take;
                        item.Stack -= take;
                        results.Add(split);
                    }

                    remaining -= take;
                    changed = true;
                }

                if (changed)
                    entry.Chest.clearNulls();
            }

            return results;
        }

        /// <summary>Withdraws items and merges them into as few stacks as possible.</summary>
        public List<Item> ExtractMerged(ItemKey key, Item sample, int count)
        {
            List<Item> raw = this.Extract(key, sample, count);
            if (raw.Count <= 1)
                return raw;

            List<Item> merged = new();
            foreach (Item item in raw)
            {
                Item target = merged.LastOrDefault();
                if (target != null && target.Stack < target.maximumStackSize() && target.canStackWith(item))
                {
                    int space = target.maximumStackSize() - target.Stack;
                    int move = Math.Min(space, item.Stack);
                    target.Stack += move;
                    item.Stack -= move;
                    if (item.Stack <= 0)
                        continue;
                }
                merged.Add(item);
            }

            return merged;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Runs one insertion pass over the chests.</summary>
        /// <param name="item">The item being stored, used as a template for the stacks handed to each chest.</param>
        /// <param name="remaining">How many are still to be stored.</param>
        /// <param name="partitionedOnly">Whether to consider only chests whose partition names this item.</param>
        /// <returns>How many are still to be stored after this pass.</returns>
        private int InsertPass(Item item, int remaining, bool partitionedOnly)
        {
            foreach (StorageEntry entry in this.Storages)
            {
                if (remaining <= 0)
                    break;
                if (entry.IsBusy || !entry.Accepts(item))
                    continue;
                if (partitionedOnly != entry.IsDedicated)
                    continue; // a dedicated chest is offered in pass one and skipped in pass two

                // Offer the chest one full stack at a time. Handing it more than a stack's worth in one call
                // would let an oversized stack land in a slot, which the rest of the game doesn't expect.
                int maxStack = Math.Max(1, item.maximumStackSize());
                while (remaining > 0)
                {
                    int chunk = Math.Min(remaining, maxStack);

                    // Hand the chest its own instance: Chest.addItem takes ownership of whatever it fully absorbs.
                    Item carrier = item.getOne();
                    carrier.Stack = chunk;

                    Item leftover = entry.Chest.addItem(carrier);
                    int moved = chunk - (leftover?.Stack ?? 0);
                    if (moved <= 0)
                        break; // this chest has no more room for this item

                    remaining -= moved;
                }
            }

            return remaining;
        }

        /// <summary>Whether a stored item is the one a withdrawal is asking for.</summary>
        private static bool Matches(Item item, ItemKey key, Item sample)
        {
            // Items that can't stack are tracked by identity, since two otherwise identical tools are still two tools.
            if (key.Unique != 0)
                return ReferenceEquals(item, sample);

            return ItemKey.From(item).Equals(key);
        }
    }
}
