using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>One row in the terminal: every item on the network sharing an <see cref="ItemKey"/>, counted together.</summary>
    internal class NetworkItemStack
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The identity shared by every item in this group.</summary>
        public ItemKey Key { get; }

        /// <summary>A sample item used for drawing and tooltips. Never mutate this; it belongs to a chest.</summary>
        public Item Sample { get; }

        /// <summary>The total number of items across the whole network.</summary>
        public long Count { get; set; }

        /// <summary>The display name shown in the terminal and matched against the search box.</summary>
        public string DisplayName { get; }

        /// <summary>The item's category, used for sorting and grouping.</summary>
        public int Category { get; }


        /*********
        ** Public methods
        *********/
        public NetworkItemStack(ItemKey key, Item sample, long count)
        {
            this.Key = key;
            this.Sample = sample;
            this.Count = count;
            this.DisplayName = sample?.DisplayName ?? sample?.Name ?? "???";
            this.Category = sample?.Category ?? 0;
        }
    }
}
