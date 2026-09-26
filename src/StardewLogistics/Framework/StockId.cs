using System;
using System.Collections.Generic;
using StardewValley;
using SObject = StardewValley.Object;

namespace StardewLogistics.Framework
{
    /// <summary>Names an item the way autocrafting needs to: precisely enough to tell Starfruit Wine from Blueberry Wine.</summary>
    /// <remarks>
    /// Every wine in the game is <c>(O)348</c>; what makes one Starfruit Wine is the ingredient recorded on it. The
    /// planner and scheduler pass item identities around as strings, so a flavoured item is written as its item ID
    /// and its ingredient joined by a bar: <c>(O)348|268</c>. A plain ID with no bar means "any of these", which is
    /// what every recipe that existed before flavoured items still asks for.
    ///
    /// Quality is deliberately not part of the ID. Planning counts a gold and a normal Starfruit as two Starfruit,
    /// the same way the terminal's counts always have.
    /// </remarks>
    internal static class StockId
    {
        /*********
        ** Fields
        *********/
        private const char Separator = '|';

        /// <summary>A real instance of each flavoured item seen, so one can be recreated with its proper name and sprite.</summary>
        /// <remarks>
        /// A flavoured item can't be rebuilt from its ID alone -- a wine's name, colour and price are set when the
        /// keg makes it. Keeping one made by the game's own machine code is the only way to get all of that right.
        /// </remarks>
        private static readonly Dictionary<string, Item> Samples = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Display names already worked out, since the UI asks for them every frame.</summary>
        private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);


        /*********
        ** Public methods
        *********/
        /// <summary>The stock ID for an item: its qualified ID, plus its ingredient when it has one.</summary>
        public static string Of(Item item)
        {
            if (item == null)
                return null;

            string flavour = (item as SObject)?.preservedParentSheetIndex?.Value;
            return string.IsNullOrEmpty(flavour)
                ? item.QualifiedItemId
                : item.QualifiedItemId + Separator + flavour;
        }

        /// <summary>Whether an ID names one particular flavour rather than any item with that ID.</summary>
        public static bool IsFlavoured(string id) => id != null && id.IndexOf(Separator) >= 0;

        /// <summary>The qualified item ID with any flavour removed.</summary>
        public static string BaseId(string id)
        {
            int index = id?.IndexOf(Separator) ?? -1;
            return index < 0 ? id : id.Substring(0, index);
        }

        /// <summary>Whether an item satisfies a request for a stock ID.</summary>
        /// <remarks>A plain ID accepts any flavour; a flavoured one accepts only that flavour.</remarks>
        public static bool Matches(Item item, string id)
        {
            if (item == null || string.IsNullOrEmpty(id))
                return false;

            return IsFlavoured(id)
                ? string.Equals(Of(item), id, StringComparison.OrdinalIgnoreCase)
                : string.Equals(item.QualifiedItemId, id, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Keeps a copy of a flavoured item so it can be recreated later.</summary>
        public static void Remember(Item sample)
        {
            string id = Of(sample);
            if (!IsFlavoured(id) || Samples.ContainsKey(id))
                return;

            Item copy = sample.getOne();
            Samples[id] = copy;
            Names[id] = copy.DisplayName;
        }

        /// <summary>Drops everything remembered, for when a different save is loaded.</summary>
        public static void Reset()
        {
            Samples.Clear();
            Names.Clear();
        }

        /// <summary>Creates an item from a stock ID, or <c>null</c> if it doesn't resolve.</summary>
        public static Item Create(string id, int stack = 1)
        {
            if (string.IsNullOrWhiteSpace(id) || id.StartsWith("-"))
                return null;

            try
            {
                if (Samples.TryGetValue(id, out Item sample))
                {
                    Item copy = sample.getOne();
                    copy.Stack = Math.Max(1, stack);
                    return copy;
                }

                Item item = ItemRegistry.Create(BaseId(id), Math.Max(1, stack), 0, allowNull: true);

                // Not seen from a machine yet. The ingredient can still be recorded, which is enough to stack and
                // match correctly even if the name is the generic one.
                if (IsFlavoured(id) && item is SObject obj)
                    obj.preservedParentSheetIndex.Value = id.Substring(id.IndexOf(Separator) + 1);

                return item;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The display name for a stock ID, falling back to the ID itself.</summary>
        public static string GetDisplayName(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return "?";

            // A category ingredient has no item to name, so the raw ID stands in until the UI can label it.
            if (id.StartsWith("-"))
                return id;

            if (Names.TryGetValue(id, out string cached))
                return cached;

            string name;
            try
            {
                name = IsFlavoured(id)
                    ? Create(id)?.DisplayName
                    : ItemRegistry.GetData(id)?.DisplayName;
            }
            catch
            {
                name = null;
            }

            name ??= id;
            Names[id] = name;
            return name;
        }
    }
}
