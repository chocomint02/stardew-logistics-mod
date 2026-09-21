using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>How a filter treats the items listed in it.</summary>
    internal enum FilterMode
    {
        /// <summary>Only the listed items pass.</summary>
        Allow,

        /// <summary>Everything except the listed items passes.</summary>
        Deny
    }

    /// <summary>A partition list attached to a chest or a bus, mirroring Applied Energistics' partitioned cells.</summary>
    /// <remarks>
    /// Filters are matched on qualified item ID only, so a partition for "Wine" accepts every quality and flavour of
    /// wine. The meaning of an <em>empty</em> filter depends on the device: an unpartitioned chest accepts anything,
    /// while an export bus with nothing configured exports nothing. Callers say which they want via
    /// <c>acceptAllWhenEmpty</c>.
    /// </remarks>
    internal class ItemFilter
    {
        /*********
        ** Fields
        *********/
        /// <summary>The qualified item IDs listed in the filter, in display order.</summary>
        private readonly List<string> Entries = new();


        /*********
        ** Accessors
        *********/
        /// <summary>The most entries a filter may hold, matching the slots drawn in the UI.</summary>
        public const int MaxEntries = 9;

        /// <summary>Whether listed items are the ones that pass, or the ones that don't.</summary>
        public FilterMode Mode { get; set; } = FilterMode.Allow;

        /// <summary>Whether no items are listed.</summary>
        public bool IsEmpty => this.Entries.Count == 0;

        /// <summary>The number of listed items.</summary>
        public int Count => this.Entries.Count;

        /// <summary>The listed qualified item IDs.</summary>
        public IReadOnlyList<string> Ids => this.Entries;


        /*********
        ** Public methods
        *********/
        /// <summary>Whether an item passes the filter.</summary>
        /// <param name="item">The item to test.</param>
        /// <param name="acceptAllWhenEmpty">What an empty filter means: <c>true</c> to pass everything (a chest), <c>false</c> to pass nothing (an export bus).</param>
        public bool Accepts(Item item, bool acceptAllWhenEmpty)
        {
            if (item == null)
                return false;
            if (this.Entries.Count == 0)
                return this.Mode == FilterMode.Deny || acceptAllWhenEmpty;

            bool listed = this.Entries.Contains(item.QualifiedItemId);
            return this.Mode == FilterMode.Allow ? listed : !listed;
        }

        /// <summary>Adds an item to the filter, ignoring duplicates and respecting <see cref="MaxEntries"/>.</summary>
        /// <returns>Whether the filter changed.</returns>
        public bool Add(Item item)
        {
            if (item == null || this.Entries.Count >= MaxEntries || this.Entries.Contains(item.QualifiedItemId))
                return false;

            this.Entries.Add(item.QualifiedItemId);
            return true;
        }

        /// <summary>Removes the entry at an index.</summary>
        /// <returns>Whether the filter changed.</returns>
        public bool RemoveAt(int index)
        {
            if (index < 0 || index >= this.Entries.Count)
                return false;

            this.Entries.RemoveAt(index);
            return true;
        }

        /// <summary>Removes every entry.</summary>
        public void Clear() => this.Entries.Clear();

        /// <summary>Builds a sample item for each entry, so the UI can draw the filter slots.</summary>
        /// <remarks>Entries whose item no longer exists (a removed mod, say) yield <c>null</c> and are drawn blank.</remarks>
        public IEnumerable<Item> GetSampleItems()
        {
            foreach (string id in this.Entries)
            {
                Item sample = null;
                try
                {
                    sample = ItemRegistry.Create(id, 1, 0, allowNull: true);
                }
                catch
                {
                    // A malformed ID shouldn't take the menu down with it.
                }
                yield return sample;
            }
        }

        /// <summary>Serialises the filter to a string suitable for <c>modData</c>.</summary>
        public string Serialise()
        {
            return this.Entries.Count == 0 && this.Mode == FilterMode.Allow
                ? string.Empty
                : $"{(this.Mode == FilterMode.Allow ? "allow" : "deny")}|{string.Join(",", this.Entries)}";
        }

        /// <summary>Reads a filter back from <c>modData</c>, returning an empty filter for missing or malformed input.</summary>
        public static ItemFilter Parse(string raw)
        {
            ItemFilter filter = new();
            if (string.IsNullOrWhiteSpace(raw))
                return filter;

            string[] parts = raw.Split('|');
            if (parts.Length > 0 && parts[0].Equals("deny", StringComparison.OrdinalIgnoreCase))
                filter.Mode = FilterMode.Deny;

            if (parts.Length > 1)
            {
                foreach (string id in parts[1].Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
                {
                    if (filter.Entries.Count >= MaxEntries)
                        break;
                    if (!filter.Entries.Contains(id))
                        filter.Entries.Add(id);
                }
            }

            return filter;
        }
    }
}
