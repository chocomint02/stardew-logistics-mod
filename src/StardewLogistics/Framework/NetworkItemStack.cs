using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>One row in the terminal: every item on the network sharing an <see cref="ItemKey"/>, counted together.</summary>
    internal class NetworkItemStack : IFilterableEntry
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The identity shared by every item in this group.</summary>
        public ItemKey Key { get; }

        /// <summary>A sample item used for drawing and tooltips. Never mutate this; it belongs to a chest.</summary>
        public Item Sample { get; }

        /// <summary>A single copy of the item for drawing its icon.</summary>
        /// <remarks>
        /// The grid draws its own count, so the icon must never show one. Drawing a stack of one guarantees that
        /// even when another mod takes over an item's drawing -- Even Better Artisan Good Icons does for wine and
        /// draws the stack number regardless -- and it leaves the chest's own item untouched.
        /// </remarks>
        public Item Icon => this.IconField ??= this.CreateIcon();

        /// <summary>The total number of items across the whole network.</summary>
        public long Count { get; set; }

        /// <summary>The display name shown in the terminal and matched against the search box.</summary>
        public string DisplayName { get; }

        /// <summary>The item's category, used for sorting and grouping.</summary>
        public int Category { get; }

        /// <summary>The display name of the mod that added this item, for the terminal's mod filter.</summary>
        public string SourceMod { get; }


        /*********
        ** Public methods
        *********/
        private Item IconField;

        private Item CreateIcon()
        {
            if (this.Sample == null)
                return null;

            Item icon = this.Sample.getOne();
            icon.Stack = 1;
            return icon;
        }

        public NetworkItemStack(ItemKey key, Item sample, long count)
        {
            this.Key = key;
            this.Sample = sample;
            this.Count = count;
            this.DisplayName = sample?.DisplayName ?? sample?.Name ?? "???";
            this.Category = sample?.Category ?? 0;
            this.SourceMod = ItemSource.GetSourceName(sample);
        }
    }
}
