using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewValley;

namespace StardewLogistics.Network
{
    /// <summary>Caches the storage networks per location and rebuilds them when the world changes.</summary>
    /// <remarks>
    /// Scanning is cheap but not free, and the terminal asks for its network on every frame it draws, so results are
    /// cached per location and dropped whenever an object is placed or removed there. Item <em>contents</em> never
    /// invalidate the cache, since the network reads chests live rather than storing their stock.
    /// </remarks>
    internal class NetworkManager
    {
        /*********
        ** Fields
        *********/
        private readonly ModConfig Config;
        private readonly Dictionary<string, List<StorageNetwork>> Cache = new();


        /*********
        ** Public methods
        *********/
        public NetworkManager(ModConfig config)
        {
            this.Config = config;
        }

        /// <summary>Returns every network in a location, scanning it if the cache is cold.</summary>
        public List<StorageNetwork> GetNetworks(GameLocation location)
        {
            if (location == null)
                return new List<StorageNetwork>();

            string key = location.NameOrUniqueName;
            if (!this.Cache.TryGetValue(key, out List<StorageNetwork> networks))
                this.Cache[key] = networks = NetworkScanner.Scan(location, this.Config);

            return networks;
        }

        /// <summary>Returns the network containing a given tile, or <c>null</c> if that tile isn't on one.</summary>
        public StorageNetwork GetNetworkAt(GameLocation location, Vector2 tile)
        {
            return this
                .GetNetworks(location)
                .FirstOrDefault(network => network.Nodes.Any(node => node.Tile == tile));
        }

        /// <summary>Returns the node at a tile together with its network.</summary>
        public bool TryGetNode(GameLocation location, Vector2 tile, out NetworkNode node, out StorageNetwork network)
        {
            foreach (StorageNetwork candidate in this.GetNetworks(location))
            {
                foreach (NetworkNode candidateNode in candidate.Nodes)
                {
                    if (candidateNode.Tile == tile)
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

        /// <summary>Drops the cached networks for a location, so the next request rescans it.</summary>
        public void Invalidate(GameLocation location)
        {
            if (location != null)
                this.Cache.Remove(location.NameOrUniqueName);
        }

        /// <summary>Drops every cached network.</summary>
        public void InvalidateAll() => this.Cache.Clear();
    }
}
