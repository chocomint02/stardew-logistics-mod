using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewValley;
using StardewValley.Inventories;

namespace StardewLogistics.Devices
{
    /// <summary>The items an autocrafting job has set aside for itself.</summary>
    /// <remarks>
    /// When a job is queued it withdraws every ingredient it planned to take from storage and holds them here,
    /// as a dedicated job store. Nothing else can spend them -- not the player through the terminal, not
    /// a second job planned against the same Starfruit. Intermediate products land here too, so the copper bars
    /// a job made for itself can't be taken out halfway through; only the finished item goes back to storage.
    ///
    /// The items live in one of the game's global inventories rather than in memory, so they're written into the
    /// save with everything else. <see cref="JobStore"/> saves the jobs too, but a buffer can still outlive its
    /// job -- one that couldn't be restored, or a cancel with storage full -- so the key records which network it
    /// came from, and <see cref="JobRunner"/> returns such orphans to it.
    /// </remarks>
    internal class JobBuffer
    {
        /*********
        ** Fields
        *********/
        /// <summary>The prefix marking a global inventory as one of these.</summary>
        public const string KeyPrefix = ModIds.ModId + "/reserve/";


        /*********
        ** Accessors
        *********/
        /// <summary>The global inventory key, which also records where the items came from.</summary>
        public string Key { get; }

        /// <summary>The items held.</summary>
        public Inventory Items => Game1.player.team.GetOrCreateGlobalInventory(this.Key);

        /// <summary>Whether anything is held.</summary>
        public bool IsEmpty => !Game1.player.team.globalInventories.ContainsKey(this.Key) || this.Items.All(item => item == null);


        /*********
        ** Public methods
        *********/
        private JobBuffer(string key)
        {
            this.Key = key;
        }

        /// <summary>Creates an empty buffer for a job running on a network.</summary>
        /// <param name="token">A value unique to the job, so a buffer left from a previous session can't be mistaken for it.</param>
        public static JobBuffer Create(string token, string locationName, Vector2 tile)
        {
            string key = string.Join("|",
                KeyPrefix + token,
                locationName ?? "",
                ((int)tile.X).ToString(CultureInfo.InvariantCulture) + "," + ((int)tile.Y).ToString(CultureInfo.InvariantCulture));

            return new JobBuffer(key);
        }

        /// <summary>Wraps an existing buffer found in the save.</summary>
        public static JobBuffer FromKey(string key) => new(key);

        /// <summary>Whether a global inventory key belongs to a job buffer.</summary>
        public static bool IsBufferKey(string key) => key != null && key.StartsWith(KeyPrefix, StringComparison.Ordinal);

        /// <summary>Reads back where a buffer's items came from.</summary>
        public bool TryGetOrigin(out string locationName, out Vector2 tile)
        {
            locationName = null;
            tile = Vector2.Zero;

            string[] parts = this.Key.Split('|');
            if (parts.Length < 3)
                return false;

            locationName = parts[1];
            string[] xy = parts[2].Split(',');
            if (xy.Length != 2
                || !int.TryParse(xy[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(xy[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
                return false;

            tile = new Vector2(x, y);
            return locationName.Length > 0;
        }

        /// <summary>How many held items satisfy a stock ID, optionally at one quality.</summary>
        public int Count(string stockId, int quality = Quality.Any)
        {
            if (!Game1.player.team.globalInventories.ContainsKey(this.Key))
                return 0;

            return this.Items.Where(item => StockId.Matches(item, stockId) && (quality < 0 || item.Quality == quality)).Sum(item => item.Stack);
        }

        /// <summary>Puts an item in the buffer, merging it into existing stacks.</summary>
        public void Add(Item item)
        {
            if (item == null || item.Stack <= 0)
                return;

            Inventory items = this.Items;
            foreach (Item held in items)
            {
                if (held == null || !held.canStackWith(item))
                    continue;

                item.Stack = held.addToStack(item);
                if (item.Stack <= 0)
                    return;
            }

            items.Add(item);
        }

        /// <summary>Removes up to a number of items matching a stock ID, returning what was taken.</summary>
        /// <param name="stockId">The stock ID.</param>
        /// <param name="count">How many to take.</param>
        /// <param name="quality">The quality to take, or <see cref="Quality.Any"/> to take the lowest first.</param>
        public List<Item> Take(string stockId, int count, int quality = Quality.Any)
        {
            List<Item> taken = new();
            if (count <= 0 || !Game1.player.team.globalInventories.ContainsKey(this.Key))
                return taken;

            Inventory items = this.Items;
            int remaining = count;

            foreach (int i in LowestQualityFirst(items))
            {
                if (remaining <= 0)
                    break;

                Item item = items[i];
                if (!StockId.Matches(item, stockId) || (quality >= 0 && item.Quality != quality))
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
            }

            items.RemoveEmptySlots();
            return taken;
        }

        /// <summary>Takes one item of the best quality below a mark, for putting in a cask.</summary>
        /// <remarks>The best one is nearest to done: a silver wine reaches iridium two weeks before a normal one.</remarks>
        public Item TakeBestBelow(string stockId, int belowQuality)
        {
            if (!Game1.player.team.globalInventories.ContainsKey(this.Key))
                return null;

            Inventory items = this.Items;
            int best = -1;
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (StockId.Matches(item, stockId) && item.Quality < belowQuality && (best < 0 || item.Quality > items[best].Quality))
                    best = i;
            }

            if (best < 0)
                return null;

            Item chosen = items[best];
            Item one = chosen.getOne();
            one.Stack = 1;
            chosen.Stack--;
            if (chosen.Stack <= 0)
                items[best] = null;

            items.RemoveEmptySlots();
            return one;
        }

        /// <summary>Makes sure the buffer holds a number of an item, drawing any shortfall from storage.</summary>
        /// <remarks>
        /// A job's ingredients are all reserved when it's queued and its intermediates are made into the buffer,
        /// so normally there is nothing to draw. This covers the odd case where something left anyway -- a run
        /// emptied by another mod -- so the job can still finish from what the network has.
        /// </remarks>
        /// <returns>Whether the buffer now holds enough.</returns>
        public bool EnsureHas(string stockId, int count, StorageNetwork network, int quality = Quality.Any)
        {
            int shortfall = count - this.Count(stockId, quality);
            if (shortfall <= 0)
                return true;

            // A category or tag spec ("any egg") is withdrawn like an ID: storage matches items to it the same way.
            if (network == null)
                return false;

            if (network.CountById(stockId, quality) < shortfall)
                return false;

            foreach (Item item in network.ExtractById(stockId, shortfall, quality))
                this.Add(item);

            return this.Count(stockId, quality) >= count;
        }

        /// <summary>Whether the buffer holds everything one craft of a recipe needs.</summary>
        /// <remarks>
        /// Done here rather than with <see cref="CraftingRecipe.doesFarmerHaveIngredientsInInventory"/>, which
        /// always counts the player's backpack as well: an autocraft job must only ever spend its own materials.
        /// </remarks>
        public bool HasIngredientsFor(CraftingRecipe recipe)
        {
            if (!Game1.player.team.globalInventories.ContainsKey(this.Key))
                return recipe.recipeList.Count == 0;

            Inventory items = this.Items;
            foreach ((string ingredient, int required) in recipe.recipeList)
            {
                int held = items.Where(item => item != null && CraftingRecipe.ItemMatchesForCrafting(item, ingredient)).Sum(item => item.Stack);
                if (held < required)
                    return false;
            }

            return true;
        }

        /// <summary>Removes one craft's worth of a recipe's ingredients.</summary>
        /// <remarks>Call only after <see cref="HasIngredientsFor"/> has confirmed they're all here.</remarks>
        public void ConsumeIngredientsFor(CraftingRecipe recipe)
        {
            Inventory items = this.Items;

            foreach ((string ingredient, int required) in recipe.recipeList)
            {
                int remaining = required;
                foreach (int i in LowestQualityFirst(items))
                {
                    if (remaining <= 0)
                        break;

                    Item item = items[i];
                    if (item == null || !CraftingRecipe.ItemMatchesForCrafting(item, ingredient))
                        continue;

                    int take = Math.Min(remaining, item.Stack);
                    item.Stack -= take;
                    remaining -= take;
                    if (item.Stack <= 0)
                        items[i] = null;
                }
            }

            items.RemoveEmptySlots();
        }

        /// <summary>The indexes of held items, lowest quality first, so better items are spent last.</summary>
        private static List<int> LowestQualityFirst(Inventory items)
        {
            return Enumerable.Range(0, items.Count)
                .Where(i => items[i] != null)
                .OrderBy(i => items[i].Quality)
                .ThenBy(i => i)
                .ToList();
        }

        /// <summary>Moves everything held back into storage.</summary>
        /// <returns>Whether the buffer is now empty. Anything storage had no room for stays here for a later try.</returns>
        public bool ReturnTo(StorageNetwork network)
        {
            if (!Game1.player.team.globalInventories.ContainsKey(this.Key))
                return true;

            Inventory items = this.Items;
            if (network != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    Item item = items[i];
                    if (item == null)
                        continue;

                    network.Insert(item);
                    if (item.Stack <= 0)
                        items[i] = null;
                }
            }

            items.RemoveEmptySlots();
            if (items.Count > 0)
                return false;

            Game1.player.team.globalInventories.Remove(this.Key);
            return true;
        }
    }
}
