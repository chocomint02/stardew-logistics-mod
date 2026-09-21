using System;
using StardewValley;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewLogistics.Framework
{
    /// <summary>Identifies a group of items that should be shown as a single entry in the terminal.</summary>
    /// <remarks>
    /// The terminal has to collapse potentially tens of thousands of individual stacks into one list every time it
    /// redraws, so grouping has to be a cheap hash lookup rather than an O(n^2) sweep of <see cref="Item.canStackWith"/>.
    /// This key captures everything the game uses to decide whether two items stack: the item ID, its quality, and the
    /// "flavour" fields that distinguish e.g. Blueberry Wine from Starfruit Wine. Items that can't stack at all (tools,
    /// weapons, furniture) get a unique discriminator so each one keeps its own slot.
    /// </remarks>
    internal readonly struct ItemKey : IEquatable<ItemKey>
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The qualified item ID, e.g. <c>(O)634</c>.</summary>
        public string QualifiedId { get; }

        /// <summary>The item quality (0 = normal, 1 = silver, 2 = gold, 4 = iridium).</summary>
        public int Quality { get; }

        /// <summary>Extra fields that keep otherwise identical items apart, such as the preserve flavour or dye colour.</summary>
        public string Variant { get; }

        /// <summary>A per-instance discriminator for items that never stack; zero for ordinary stackable items.</summary>
        public int Unique { get; }

        /// <summary>Whether this key refers to a real item.</summary>
        public bool IsValid => this.QualifiedId != null;


        /*********
        ** Public methods
        *********/
        public ItemKey(string qualifiedId, int quality, string variant, int unique)
        {
            this.QualifiedId = qualifiedId;
            this.Quality = quality;
            this.Variant = variant ?? string.Empty;
            this.Unique = unique;
        }

        /// <summary>Builds the key for an item.</summary>
        /// <param name="item">The item to identify.</param>
        /// <param name="unique">A discriminator used only when the item can't stack. Callers should pass an increasing counter.</param>
        public static ItemKey From(Item item, int unique = 0)
        {
            if (item == null)
                return default;

            bool stackable = item.maximumStackSize() > 1;
            return new ItemKey(
                item.QualifiedItemId,
                item.Quality,
                BuildVariant(item),
                stackable ? 0 : unique
            );
        }

        /// <summary>Builds a key that ignores quality, for matching against filters.</summary>
        public ItemKey WithoutQuality() => new ItemKey(this.QualifiedId, 0, this.Variant, this.Unique);

        /// <summary>Builds a key without the non-stackable discriminator, for counting rather than identifying.</summary>
        public ItemKey WithoutUnique() => this.Unique == 0 ? this : new ItemKey(this.QualifiedId, this.Quality, this.Variant, 0);

        public bool Equals(ItemKey other)
        {
            return this.Quality == other.Quality
                && this.Unique == other.Unique
                && string.Equals(this.QualifiedId, other.QualifiedId, StringComparison.Ordinal)
                && string.Equals(this.Variant, other.Variant, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => obj is ItemKey other && this.Equals(other);

        public override int GetHashCode()
        {
            int hash = this.QualifiedId?.GetHashCode() ?? 0;
            hash = (hash * 397) ^ this.Quality;
            hash = (hash * 397) ^ (this.Variant?.GetHashCode() ?? 0);
            return (hash * 397) ^ this.Unique;
        }

        public override string ToString() => $"{this.QualifiedId}#{this.Quality}{(this.Variant.Length > 0 ? ":" + this.Variant : "")}";


        /*********
        ** Private methods
        *********/
        /// <summary>Collects the secondary fields that stop two same-ID items from stacking.</summary>
        private static string BuildVariant(Item item)
        {
            if (item is not SObject obj)
                return string.Empty;

            string flavour = obj.preservedParentSheetIndex?.Value;
            string preserve = obj.preserve?.Value?.ToString();
            string colour = item is ColoredObject coloured
                ? coloured.color.Value.PackedValue.ToString()
                : null;

            // The common case is an item with none of these set, so avoid allocating a builder for it.
            if (flavour == null && preserve == null && colour == null)
                return string.Empty;

            return string.Concat(preserve ?? "", "|", flavour ?? "", "|", colour ?? "");
        }
    }
}
