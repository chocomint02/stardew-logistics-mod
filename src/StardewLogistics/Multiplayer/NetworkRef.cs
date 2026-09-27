using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewLogistics.Network;
using StardewValley;

namespace StardewLogistics.Multiplayer
{
    /// <summary>Names a network in a way every player can resolve: a terminal's tile, or a wireless channel.</summary>
    /// <remarks>
    /// Networks are rebuilt from the world on each machine, so they can't be passed between players themselves.
    /// A farmhand's request says where its terminal is -- "L|Farm|12|8" -- or which channel its Wireless Terminal is
    /// tuned to -- "C|3" -- and the host finds the same network from its own copy of the world.
    /// </remarks>
    internal static class NetworkRef
    {
        /// <summary>The reference for a placed terminal.</summary>
        public static string ForTile(GameLocation location, Vector2 tile)
        {
            return string.Join("|", "L", location?.NameOrUniqueName ?? "", ((int)tile.X).ToString(CultureInfo.InvariantCulture), ((int)tile.Y).ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The reference for a wireless channel.</summary>
        public static string ForChannel(int channel) => "C|" + channel.ToString(CultureInfo.InvariantCulture);

        /// <summary>Finds the network a reference names, or <c>null</c>.</summary>
        public static StorageNetwork Resolve(NetworkManager networks, string reference)
        {
            if (networks == null || string.IsNullOrEmpty(reference))
                return null;

            string[] parts = reference.Split('|');
            if (parts[0] == "C" && parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int channel))
                return GetNetworkOnChannel(networks, channel, out _);

            if (parts[0] == "L" && parts.Length >= 4
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
            {
                GameLocation location = Game1.getLocationFromName(parts[1]);
                return location == null ? null : networks.GetNetworkAt(location, new Vector2(x, y));
            }

            return null;
        }

        /// <summary>The network a channel's transmitter is on, if the channel has one.</summary>
        public static StorageNetwork GetNetworkOnChannel(NetworkManager networks, int channel, out NetworkNode transmitter)
        {
            transmitter = networks.GetChannelInfo(channel).Devices.FirstOrDefault(node => node.Kind == NodeKind.WirelessTransmitter);
            return transmitter == null ? null : networks.GetNetworkAt(transmitter.Location, transmitter.Tile);
        }
    }
}
