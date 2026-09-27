using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>Something the terminal can search, sort and filter.</summary>
    /// <remarks>
    /// The Items tab lists stock and the Craft tab lists recipes, but the player expects the same search box and the
    /// same Type and Mod dropdowns to work on both. This is the small surface <see cref="StockFilter"/> needs, so
    /// one filter implementation serves both lists rather than each tab growing its own.
    /// </remarks>
    internal interface IFilterableEntry
    {
        /// <summary>The name shown to the player, and matched by a plain search term.</summary>
        string DisplayName { get; }

        /// <summary>The item category, for the Type filter.</summary>
        int Category { get; }

        /// <summary>The mod that added this, for the Mod filter.</summary>
        string SourceMod { get; }

        /// <summary>The number this entry represents: items in storage, or craftable batches.</summary>
        long Count { get; }

        /// <summary>A representative item, used for context tags and category names.</summary>
        Item Sample { get; }
    }
}
