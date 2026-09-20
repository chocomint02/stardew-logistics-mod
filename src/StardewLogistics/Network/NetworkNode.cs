using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using SObject = StardewValley.Object;

namespace StardewLogistics.Network
{
    /// <summary>The role a placed object plays on a storage network.</summary>
    internal enum NodeKind
    {
        /// <summary>A cable: carries the network between tiles but does nothing on its own.</summary>
        Cable,

        /// <summary>A controller: conducts like a cable and raises the network's channel budget.</summary>
        Controller,

        /// <summary>An access terminal: opens the storage UI.</summary>
        Terminal,

        /// <summary>A crafting terminal: an access terminal that can also craft from network stock.</summary>
        CraftingTerminal,

        /// <summary>An import bus: pulls items from the adjacent chest or machine onto the network.</summary>
        ImportBus,

        /// <summary>An export bus: pushes filtered items from the network into the adjacent chest or machine.</summary>
        ExportBus
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

        /// <summary>Whether the node conducts the network to its neighbours, rather than just consuming it.</summary>
        public bool IsConductive => this.Kind is NodeKind.Cable or NodeKind.Controller;

        /// <summary>Whether the node spends one of the network's channels.</summary>
        public bool UsesChannel => !this.IsConductive;


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
        public static NodeKind? GetKind(string itemId)
        {
            return itemId switch
            {
                ModIds.Cable => NodeKind.Cable,
                ModIds.Controller => NodeKind.Controller,
                ModIds.Terminal => NodeKind.Terminal,
                ModIds.CraftingTerminal => NodeKind.CraftingTerminal,
                ModIds.ImportBus => NodeKind.ImportBus,
                ModIds.ExportBus => NodeKind.ExportBus,
                _ => null
            };
        }
    }
}
