using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewValley;
using StardewValley.Network;
using StardewValley.Objects;

namespace StardewLogistics.Network
{
    /// <summary>A chest attached to a storage network, with the priority and partition that govern what lands in it.</summary>
    /// <remarks>
    /// This is the mod's answer to an Applied Energistics storage cell: the chest supplies the capacity, and the
    /// priority and filter stored in its <c>modData</c> supply the routing rules. Settings live on the chest itself so
    /// that breaking and replacing a cable never loses them.
    /// </remarks>
    internal class StorageEntry
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The chest holding the items.</summary>
        public Chest Chest { get; }

        /// <summary>The location the chest is in.</summary>
        public GameLocation Location { get; }

        /// <summary>The tile the chest occupies.</summary>
        public Vector2 Tile { get; }

        /// <summary>The partition restricting what may be stored here.</summary>
        public ItemFilter Filter { get; private set; }

        /// <summary>How eagerly the network fills this chest. Higher priorities are filled first and drained last.</summary>
        public int Priority
        {
            get => this.PriorityField;
            set
            {
                this.PriorityField = value;
                if (value == 0)
                    this.Chest.modData.Remove(ModIds.PriorityKey);
                else
                    this.Chest.modData[ModIds.PriorityKey] = value.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Whether another player has this chest open, in which case the network leaves it alone.</summary>
        public bool IsBusy
        {
            get
            {
                NetMutex mutex = this.Chest.GetMutex();
                return mutex != null && mutex.IsLocked() && !mutex.IsLockHeld();
            }
        }

        /// <summary>The number of stacks the chest can hold.</summary>
        public int Capacity => this.Chest.GetActualCapacity();

        /// <summary>The number of stacks currently in use.</summary>
        public int UsedSlots => this.Chest.Items.Count(item => item != null);

        /// <summary>The number of stacks still free.</summary>
        public int FreeSlots => System.Math.Max(0, this.Capacity - this.UsedSlots);


        /*********
        ** Fields
        *********/
        private int PriorityField;


        /*********
        ** Public methods
        *********/
        public StorageEntry(Chest chest, GameLocation location, Vector2 tile)
        {
            this.Chest = chest;
            this.Location = location;
            this.Tile = tile;

            this.PriorityField = chest.modData.TryGetValue(ModIds.PriorityKey, out string rawPriority)
                && int.TryParse(rawPriority, NumberStyles.Integer, CultureInfo.InvariantCulture, out int priority)
                ? priority
                : 0;

            this.Filter = chest.modData.TryGetValue(ModIds.FilterKey, out string rawFilter)
                ? ItemFilter.Parse(rawFilter)
                : new ItemFilter();
        }

        /// <summary>Whether this chest is dedicated to specific items, and so should be filled before general storage.</summary>
        /// <remarks>A deny list doesn't count: it restricts what may land here, but doesn't claim anything in particular.</remarks>
        public bool IsDedicated => this.Filter.Mode == FilterMode.Allow && !this.Filter.IsEmpty;

        /// <summary>Whether this chest will accept an item, ignoring how much room it has.</summary>
        /// <remarks>A chest with no partition accepts anything, which is what makes plain chests work out of the box.</remarks>
        public bool Accepts(Item item) => this.Filter.Accepts(item, acceptAllWhenEmpty: true);

        /// <summary>Writes the current filter back to the chest's <c>modData</c>.</summary>
        public void SaveFilter()
        {
            string raw = this.Filter.Serialise();
            if (raw.Length == 0)
                this.Chest.modData.Remove(ModIds.FilterKey);
            else
                this.Chest.modData[ModIds.FilterKey] = raw;
        }

        /// <summary>Replaces the filter and saves it.</summary>
        public void SetFilter(ItemFilter filter)
        {
            this.Filter = filter ?? new ItemFilter();
            this.SaveFilter();
        }
    }
}
