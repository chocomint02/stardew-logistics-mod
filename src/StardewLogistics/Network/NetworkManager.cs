using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewValley;
using SObject = StardewValley.Object;

namespace StardewLogistics.Network
{
    /// <summary>Caches the storage networks per location, links them wirelessly, and rebuilds them when the world changes.</summary>
    /// <remarks>
    /// Scanning is cheap but not free, and the terminal asks for its network on every frame it draws, so results are
    /// cached per location and dropped whenever an object or cable is placed or removed there. Item <em>contents</em>
    /// never invalidate the cache, since the network reads chests live rather than storing their stock.
    ///
    /// Wireless linking sits on top. Each location's scan gives its segments -- connected runs of cable. A channel is
    /// live once any segment has a transmitter on it, and every segment with a transmitter or receiver on a live
    /// channel is joined into one network. Linking can join segments in any location, which is what puts a cellar's
    /// casks on the farm's network.
    /// </remarks>
    internal class NetworkManager
    {
        /*********
        ** Fields
        *********/
        private readonly ModConfig Config;

        /// <summary>Each location's segments, as scanned.</summary>
        private readonly Dictionary<string, List<StorageNetwork>> Segments = new();

        /// <summary>The network each segment belongs to once wireless links are applied. Dropped whenever anything changes.</summary>
        private Dictionary<StorageNetwork, StorageNetwork> Linked;

        /// <summary>The names of locations holding a wireless device, or <c>null</c> if they need finding again.</summary>
        /// <remarks>Finding them means looking at every object in the world, so it's done only when a device is placed or removed.</remarks>
        private HashSet<string> WirelessLocations;


        /*********
        ** Public methods
        *********/
        public NetworkManager(ModConfig config)
        {
            this.Config = config;
        }

        /// <summary>Returns every network reaching a location, scanning and linking as needed.</summary>
        public List<StorageNetwork> GetNetworks(GameLocation location)
        {
            if (location == null)
                return new List<StorageNetwork>();

            Dictionary<StorageNetwork, StorageNetwork> linked = this.GetLinks();

            return this.GetSegments(location)
                .Select(segment => linked.TryGetValue(segment, out StorageNetwork network) ? network : segment)
                .Distinct()
                .ToList();
        }

        /// <summary>Returns the network containing a given tile, or <c>null</c> if that tile isn't on one.</summary>
        /// <remarks>The tile can be cable or anything attached to it.</remarks>
        public StorageNetwork GetNetworkAt(GameLocation location, Vector2 tile)
        {
            return this
                .GetNetworks(location)
                .FirstOrDefault(network => network.Contains(location, tile));
        }

        /// <summary>Returns the network a tile is attached to: cable under it, or cable beside it.</summary>
        /// <remarks>Works for a machine that has just been removed, since the cable it sat on or beside is still there.</remarks>
        public StorageNetwork GetNetworkTouching(GameLocation location, Vector2 tile)
        {
            foreach (StorageNetwork network in this.GetNetworks(location))
            {
                foreach (StorageNetwork segment in network.Segments.Where(segment => segment.Location == location))
                {
                    if (segment.CableTiles.Contains(tile)
                        || segment.CableTiles.Contains(tile + new Vector2(0, -1))
                        || segment.CableTiles.Contains(tile + new Vector2(0, 1))
                        || segment.CableTiles.Contains(tile + new Vector2(-1, 0))
                        || segment.CableTiles.Contains(tile + new Vector2(1, 0)))
                        return network;
                }
            }

            return null;
        }

        /// <summary>Returns the node at a tile together with its network.</summary>
        public bool TryGetNode(GameLocation location, Vector2 tile, out NetworkNode node, out StorageNetwork network)
        {
            foreach (StorageNetwork candidate in this.GetNetworks(location))
            {
                foreach (NetworkNode candidateNode in candidate.Nodes)
                {
                    if (candidateNode.Location == location && candidateNode.Tile == tile)
                    {
                        node = candidateNode;
                        network = candidate;
                        return true;
                    }
                }
            }

            node = null;
            network = null;
            return false;
        }

        /// <summary>Describes a channel: who is on it, and whether it's live.</summary>
        public ChannelInfo GetChannelInfo(int channel)
        {
            List<NetworkNode> devices = this.GetAllWirelessSegments()
                .SelectMany(segment => segment.WirelessNodes)
                .Where(node => node.Channel == channel)
                .ToList();

            return new ChannelInfo(channel, devices);
        }

        /// <summary>Drops the cached networks for a location, so the next request rescans it.</summary>
        public void Invalidate(GameLocation location)
        {
            if (location != null)
                this.Segments.Remove(location.NameOrUniqueName);

            // A segment here may be linked to networks elsewhere, so every link is worked out afresh.
            this.Linked = null;
        }

        /// <summary>Forgets where the wireless devices are, for when one is placed, removed or retuned.</summary>
        public void InvalidateWireless()
        {
            this.WirelessLocations = null;
            this.Linked = null;
        }

        /// <summary>Drops every cached network.</summary>
        public void InvalidateAll()
        {
            this.Segments.Clear();
            this.Linked = null;
            this.WirelessLocations = null;
        }

        /// <summary>Whether an object is one of the wireless devices.</summary>
        public static bool IsWirelessDevice(SObject obj)
        {
            return obj != null && NetworkNode.GetKind(obj.ItemId) is NodeKind.WirelessTransmitter or NodeKind.WirelessReceiver;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Returns a location's segments, scanning it if the cache is cold.</summary>
        private List<StorageNetwork> GetSegments(GameLocation location)
        {
            string key = location.NameOrUniqueName;
            if (!this.Segments.TryGetValue(key, out List<StorageNetwork> segments))
            {
                this.Segments[key] = segments = NetworkScanner.Scan(location, this.Config);

                // Stay quiet about the many locations that hold no cable at all, or this would log a line per
                // location every time a cache invalidation sweeps the world.
                if (segments.Count > 0)
                    Log.Trace($"Scanned {key}: {Describe(segments)}.");
            }

            return segments;
        }

        /// <summary>Works out which segments are linked, if that isn't already known.</summary>
        private Dictionary<StorageNetwork, StorageNetwork> GetLinks()
        {
            if (this.Linked != null)
                return this.Linked;

            Dictionary<StorageNetwork, StorageNetwork> linked = new();
            List<StorageNetwork> wireless = this.GetAllWirelessSegments();

            if (wireless.Count > 0)
            {
                // A channel is live when something on it transmits. Receivers on a channel nobody transmits on
                // stay on their own, which is what makes the transmitter matter.
                HashSet<int> live = new(wireless
                    .SelectMany(segment => segment.WirelessNodes)
                    .Where(node => node.Kind == NodeKind.WirelessTransmitter)
                    .Select(node => node.Channel));

                // Union segments that share a live channel. A segment with devices on two channels bridges them,
                // so this is a proper union rather than a group-by.
                Dictionary<StorageNetwork, StorageNetwork> parent = wireless.ToDictionary(segment => segment, segment => segment);
                StorageNetwork Find(StorageNetwork segment)
                {
                    while (parent[segment] != segment)
                        segment = parent[segment] = parent[parent[segment]];
                    return segment;
                }

                Dictionary<int, StorageNetwork> firstOnChannel = new();
                foreach (StorageNetwork segment in wireless)
                {
                    foreach (int channel in segment.WirelessNodes.Select(node => node.Channel).Where(live.Contains).Distinct())
                    {
                        if (firstOnChannel.TryGetValue(channel, out StorageNetwork other))
                            parent[Find(segment)] = Find(other);
                        else
                            firstOnChannel[channel] = segment;
                    }
                }

                foreach (IGrouping<StorageNetwork, StorageNetwork> group in wireless.GroupBy(Find))
                {
                    if (group.Count() < 2)
                        continue;

                    // The segment holding a transmitter goes first, so the network's anchor stays put as
                    // receivers come and go.
                    List<StorageNetwork> members = group
                        .OrderByDescending(segment => segment.WirelessNodes.Any(node => node.Kind == NodeKind.WirelessTransmitter))
                        .ThenBy(segment => segment.Location?.NameOrUniqueName)
                        .ToList();

                    StorageNetwork network = StorageNetwork.Link(members);
                    foreach (StorageNetwork member in members)
                        linked[member] = network;

                    Log.Trace($"Linked {members.Count} segments wirelessly across {string.Join(", ", network.Locations.Select(location => location.NameOrUniqueName))}.");
                }
            }

            return this.Linked = linked;
        }

        /// <summary>Every segment, in any location, that has a wireless device attached.</summary>
        private List<StorageNetwork> GetAllWirelessSegments()
        {
            List<StorageNetwork> segments = new();

            foreach (string name in this.GetWirelessLocations())
            {
                GameLocation location = Game1.getLocationFromName(name);
                if (location == null)
                    continue;

                segments.AddRange(this.GetSegments(location).Where(segment => segment.WirelessNodes.Any()));
            }

            return segments;
        }

        /// <summary>The names of every location with a wireless device in it.</summary>
        private HashSet<string> GetWirelessLocations()
        {
            if (this.WirelessLocations != null)
                return this.WirelessLocations;

            HashSet<string> found = new();
            if (StardewModdingAPI.Context.IsWorldReady)
            {
                Utility.ForEachLocation(location =>
                {
                    foreach (SObject obj in location.Objects.Values)
                    {
                        if (IsWirelessDevice(obj))
                        {
                            found.Add(location.NameOrUniqueName);
                            break;
                        }
                    }
                    return true;
                }, includeInteriors: true, includeGenerated: false);
            }

            return this.WirelessLocations = found;
        }

        /// <summary>Summarises a location's segments for the log.</summary>
        private static string Describe(List<StorageNetwork> segments)
        {
            return string.Join(", ", segments.Select((network, index) =>
                $"network {index + 1} has {network.CableTiles.Count} cable tiles, "
                + $"{network.Storages.Count} chests, "
                + $"{network.Terminals.Count()} terminals, "
                + $"{network.Machines.Count()} machines"
                + (network.WirelessNodes.Any() ? $", {network.WirelessNodes.Count()} wireless" : "")));
        }
    }

    /// <summary>Who is on a wireless channel.</summary>
    internal class ChannelInfo
    {
        /// <summary>The channel number.</summary>
        public int Channel { get; }

        /// <summary>Every transmitter and receiver tuned to it.</summary>
        public IReadOnlyList<NetworkNode> Devices { get; }

        /// <summary>How many transmitters are on it.</summary>
        public int Transmitters => this.Devices.Count(node => node.Kind == NodeKind.WirelessTransmitter);

        /// <summary>How many receivers are on it.</summary>
        public int Receivers => this.Devices.Count(node => node.Kind == NodeKind.WirelessReceiver);

        /// <summary>Whether the channel links anything: it needs a transmitter.</summary>
        public bool IsLive => this.Transmitters > 0;

        /// <summary>The locations reached on this channel.</summary>
        public IEnumerable<GameLocation> Locations => this.Devices.Select(node => node.Location).Where(location => location != null).Distinct();

        public ChannelInfo(int channel, IReadOnlyList<NetworkNode> devices)
        {
            this.Channel = channel;
            this.Devices = devices;
        }
    }
}
