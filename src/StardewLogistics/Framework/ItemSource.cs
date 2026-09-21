using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>Works out which mod an item came from, so the terminal can filter by it.</summary>
    /// <remarks>
    /// Stardew has no item-to-mod index, so this leans on the 1.6 convention that a modded item ID is namespaced
    /// with its author's mod ID — <c>Author.ModName_ItemName</c>. Every loaded mod's unique ID becomes a prefix to
    /// match against. It's a heuristic: mods that predate the convention (most Json Assets content) hand out IDs
    /// with no namespace at all and are reported as <see cref="UnknownSource"/> rather than guessed at.
    ///
    /// This is static because <see cref="NetworkItemStack"/> is built deep inside the aggregation loop, and
    /// threading a registry reference through five layers to reach it would be worse than one explicit init call.
    /// </remarks>
    internal static class ItemSource
    {
        /*********
        ** Fields
        *********/
        /// <summary>Mod ID prefixes mapped to display names, longest first so the most specific prefix wins.</summary>
        private static (string Prefix, string Name)[] Prefixes = Array.Empty<(string, string)>();

        /// <summary>Cached lookups, since the terminal asks the same question about the same items constantly.</summary>
        private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);


        /*********
        ** Accessors
        *********/
        /// <summary>The name used for base-game content.</summary>
        public const string VanillaSource = "Stardew Valley";

        /// <summary>The name used when an item's origin can't be determined.</summary>
        public const string UnknownSource = "Unknown";


        /*********
        ** Public methods
        *********/
        /// <summary>Builds the prefix table from the loaded mod list. Call once at startup.</summary>
        public static void Initialise(IModRegistry registry)
        {
            Prefixes = registry
                .GetAll()
                .Select(mod => (Prefix: mod.Manifest.UniqueID, Name: mod.Manifest.Name))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Prefix))
                .OrderByDescending(entry => entry.Prefix.Length)
                .ToArray();

            Cache.Clear();
        }

        /// <summary>Returns the display name of the mod that added an item.</summary>
        public static string GetSourceName(Item item)
        {
            string id = item?.ItemId;
            if (string.IsNullOrEmpty(id))
                return UnknownSource;

            if (Cache.TryGetValue(id, out string cached))
                return cached;

            string result = Resolve(id);
            Cache[id] = result;
            return result;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Works out an item's origin from its ID.</summary>
        private static string Resolve(string id)
        {
            // Vanilla item IDs are plain numbers ("634"), with a handful of unprefixed string IDs for 1.6 content.
            if (id.All(char.IsDigit))
                return VanillaSource;

            foreach ((string prefix, string name) in Prefixes)
            {
                if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Require a separator so "Foo.Bar" doesn't swallow items belonging to "Foo.BarBaz".
                if (id.Length == prefix.Length || id[prefix.Length] is '_' or '.' or '/' or '-')
                    return name;
            }

            // An unnamespaced string ID is almost always base-game 1.6 content; anything else we can't attribute.
            return id.Contains('.') || id.Contains('_') ? UnknownSource : VanillaSource;
        }
    }
}
