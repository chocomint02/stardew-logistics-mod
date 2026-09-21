using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using SObject = StardewValley.Object;

namespace StardewLogistics.Network
{
    /// <summary>The role an object plays on a storage network.</summary>
    /// <remarks>
    /// Cables are deliberately absent: they are floor tiles rather than objects, so the network's shape lives in
    /// <see cref="StorageNetwork.CableTiles"/> and this enum only covers things that <em>attach</em> to it.
    /// </remarks>
    internal enum NodeKind
    {
        /// <summary>An access terminal: opens the storage UI.</summary>
        Terminal,

        /// <summary>A crafting terminal: an access terminal that can also craft from network stock.</summary>
        CraftingTerminal,

        /// <summary>A vanilla machine the network can collect from and load into.</summary>
        Machine
    }

    /// <summary>A device attached to a storage network.</summary>
    internal class NetworkNode
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The device's role on the network.</summary>
        public NodeKind Kind { get; }

        /// <summary>The tile the device occupies.</summary>
        public Vector2 Tile { get; }

        /// <summary>The placed object itself.</summary>
        public SObject Object { get; }

        /// <summary>Whether this node is one of the mod's terminals.</summary>
        public bool IsTerminal => this.Kind is NodeKind.Terminal or NodeKind.CraftingTerminal;


        /*********
        ** Public methods
        *********/
        public NetworkNode(NodeKind kind, Vector2 tile, SObject obj)
        {
            this.Kind = kind;
            this.Tile = tile;
            this.Object = obj;
        }

        /// <summary>Reads the filter configured on this device, or an empty filter if it has none.</summary>
        public ItemFilter GetFilter()
        {
            return this.Object != null && this.Object.modData.TryGetValue(ModIds.FilterKey, out string raw)
                ? ItemFilter.Parse(raw)
                : new ItemFilter();
        }

        /// <summary>Stores a filter on this device so it survives saving and reaches farmhands.</summary>
        public void SaveFilter(ItemFilter filter)
        {
            if (this.Object == null)
                return;

            string raw = filter?.Serialise() ?? string.Empty;
            if (raw.Length == 0)
                this.Object.modData.Remove(ModIds.FilterKey);
            else
                this.Object.modData[ModIds.FilterKey] = raw;
        }

        /// <summary>Maps an item ID to the node role it represents, or <c>null</c> if it isn't one of the mod's devices.</summary>
        /// <remarks>Machines aren't covered here: they're identified by having machine data, not by a known ID.</remarks>
        public static NodeKind? GetKind(string itemId)
        {
            return itemId switch
            {
                ModIds.Terminal => NodeKind.Terminal,
                ModIds.CraftingTerminal => NodeKind.CraftingTerminal,
                _ => null
            };
        }
    }
}
