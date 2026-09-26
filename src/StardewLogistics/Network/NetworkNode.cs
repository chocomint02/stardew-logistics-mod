using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewValley;
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
        Machine,

        /// <summary>Makes its channel live, linking every network on that channel into one.</summary>
        WirelessTransmitter,

        /// <summary>Links its network to a channel that has a transmitter.</summary>
        WirelessReceiver,

        /// <summary>Farms an area, using the network for seeds and to store the harvest.</summary>
        Harvester,

        /// <summary>A shipping bin -- the farm's building, or a Mini-Shipping Bin -- the network can sell through.</summary>
        /// <remarks>For the building there's no placed object: <see cref="NetworkNode.Object"/> is <c>null</c>.</remarks>
        ShippingBin,

        /// <summary>A tapper on a tree, counted in the income forecast.</summary>
        /// <remarks>Not a <see cref="Machine"/>: the network doesn't collect from it or load it.</remarks>
        Tapper
    }

    /// <summary>A device attached to a storage network.</summary>
    internal class NetworkNode
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The device's role on the network.</summary>
        public NodeKind Kind { get; }

        /// <summary>The location the device is in.</summary>
        /// <remarks>A network linked wirelessly spans locations, so a tile alone no longer says where a device is.</remarks>
        public GameLocation Location { get; }

        /// <summary>The tile the device occupies.</summary>
        public Vector2 Tile { get; }

        /// <summary>The placed object itself.</summary>
        public SObject Object { get; }

        /// <summary>Whether this node is one of the mod's terminals.</summary>
        public bool IsTerminal => this.Kind is NodeKind.Terminal or NodeKind.CraftingTerminal;

        /// <summary>Whether this node is a wireless transmitter or receiver.</summary>
        public bool IsWireless => this.Kind is NodeKind.WirelessTransmitter or NodeKind.WirelessReceiver;

        /// <summary>The channel a wireless node is tuned to.</summary>
        public int Channel => GetChannel(this.Object);


        /*********
        ** Public methods
        *********/
        public NetworkNode(NodeKind kind, GameLocation location, Vector2 tile, SObject obj)
        {
            this.Kind = kind;
            this.Location = location;
            this.Tile = tile;
            this.Object = obj;
        }

        /// <summary>The lowest and highest channel a device can be tuned to.</summary>
        public const int MinChannel = 1;
        public const int MaxChannel = 999;

        /// <summary>Reads the channel a wireless device is tuned to.</summary>
        /// <remarks>
        /// A new device starts on channel 1, so a transmitter and a receiver work together straight out of the
        /// box. Separating networks is a matter of retuning, not of setting up the first link.
        /// </remarks>
        public static int GetChannel(SObject obj)
        {
            return obj != null
                && obj.modData.TryGetValue(ModIds.ChannelKey, out string raw)
                && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int channel)
                ? System.Math.Clamp(channel, MinChannel, MaxChannel)
                : MinChannel;
        }

        /// <summary>Tunes a wireless device, storing the channel on the object so it saves and syncs.</summary>
        public static void SetChannel(SObject obj, int channel)
        {
            if (obj == null)
                return;

            channel = System.Math.Clamp(channel, MinChannel, MaxChannel);
            if (channel == MinChannel)
                obj.modData.Remove(ModIds.ChannelKey);
            else
                obj.modData[ModIds.ChannelKey] = channel.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Reads the filter configured on this device, or an empty filter if it has none.</summary>
        public ItemFilter GetFilter()
        {
            return this.Object != null && this.Object.modData.TryGetValue(ModIds.FilterKey, out string raw)
                ? ItemFilter.Parse(raw)
                : new ItemFilter();
        }

        /// <summary>Whether this machine will accept an item as input, according to the filter set on it.</summary>
        /// <remarks>
        /// A machine with no filter takes anything, which is what keeps the common case free of configuration.
        /// An allow list restricts it to those inputs; a deny list excludes them. This is what lets a player
        /// keep one furnace clear of copper so it stays free for iridium.
        /// </remarks>
        public bool AcceptsInput(string qualifiedItemId)
        {
            return this.GetFilter().AcceptsId(qualifiedItemId, acceptAllWhenEmpty: true);
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
                ModIds.WirelessTransmitter => NodeKind.WirelessTransmitter,
                ModIds.WirelessReceiver => NodeKind.WirelessReceiver,
                ModIds.AutoHarvester => NodeKind.Harvester,
                _ => null
            };
        }
    }
}
