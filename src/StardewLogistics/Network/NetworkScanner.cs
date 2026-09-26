using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewLogistics.Devices;
using StardewLogistics.Framework;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace StardewLogistics.Network
{
    /// <summary>Rebuilds the storage networks in a location by flood-filling the cable floor laid there.</summary>
    /// <remarks>
    /// Cables are a custom <see cref="Flooring"/>, not placed objects, which is what lets a chest or machine sit on
    /// the same tile as the cable feeding it. The graph is therefore a set of tiles in
    /// <see cref="GameLocation.terrainFeatures"/>, and anything in the object layer attaches to it by being on a
    /// cable tile or orthogonally beside one.
    /// </remarks>
    internal static class NetworkScanner
    {
        /*********
        ** Fields
        *********/
        /// <summary>The four orthogonal neighbours of a tile. Cables don't connect diagonally.</summary>
        private static readonly Vector2[] Directions =
        {
            new(0, -1),
            new(0, 1),
            new(-1, 0),
            new(1, 0)
        };


        /*********
        ** Public methods
        *********/
        /// <summary>Finds every storage network in a location.</summary>
        /// <param name="location">The location to scan.</param>
        /// <param name="config">The mod settings, which cap how far a single network may spread.</param>
        public static List<StorageNetwork> Scan(GameLocation location, ModConfig config)
        {
            List<StorageNetwork> networks = new();
            if (location == null)
                return networks;

            HashSet<Vector2> cables = FindCableTiles(location);
            if (cables.Count == 0)
                return networks;

            HashSet<Vector2> visited = new();
            Queue<Vector2> queue = new();

            foreach (Vector2 seed in cables)
            {
                if (visited.Contains(seed))
                    continue;

                HashSet<Vector2> component = new();
                queue.Clear();
                queue.Enqueue(seed);
                visited.Add(seed);

                while (queue.Count > 0)
                {
                    Vector2 tile = queue.Dequeue();
                    component.Add(tile);

                    // A pathological cable run shouldn't be able to stall the game; stop growing and work with
                    // what we have, which still leaves the player a usable if truncated network.
                    if (component.Count >= config.MaxNetworkSize)
                        break;

                    foreach (Vector2 direction in Directions)
                    {
                        Vector2 neighbour = tile + direction;
                        if (cables.Contains(neighbour) && visited.Add(neighbour))
                            queue.Enqueue(neighbour);
                    }
                }

                networks.Add(BuildNetwork(location, component));
            }

            return networks;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Collects every tile in a location carrying the mod's cable floor.</summary>
        private static HashSet<Vector2> FindCableTiles(GameLocation location)
        {
            HashSet<Vector2> tiles = new();

            foreach (KeyValuePair<Vector2, TerrainFeature> pair in location.terrainFeatures.Pairs)
            {
                if (pair.Value is Flooring floor && floor.whichFloor.Value == ModIds.CableFloorId)
                    tiles.Add(pair.Key);
            }

            return tiles;
        }

        /// <summary>Attaches everything touching a connected run of cable, and builds the network from it.</summary>
        private static StorageNetwork BuildNetwork(GameLocation location, HashSet<Vector2> cables)
        {
            List<NetworkNode> nodes = new();
            List<StorageEntry> storages = new();
            HashSet<Vector2> inspected = new();

            foreach (Vector2 cable in cables)
            {
                // The cable's own tile first: an object placed directly on the cable is connected, which is the
                // whole point of cables being a floor.
                Attach(location, cable, inspected, nodes, storages);

                foreach (Vector2 direction in Directions)
                    Attach(location, cable + direction, inspected, nodes, storages);
            }

            return new StorageNetwork(location, cables, nodes, storages);
        }

        /// <summary>Attaches whatever object occupies a tile, if it's something the network can use.</summary>
        private static void Attach(GameLocation location, Vector2 tile, HashSet<Vector2> inspected, List<NetworkNode> nodes, List<StorageEntry> storages)
        {
            if (!inspected.Add(tile))
                return;
            if (!location.Objects.TryGetValue(tile, out SObject obj) || obj == null)
                return;

            NodeKind? kind = NetworkNode.GetKind(obj.ItemId);
            if (kind != null)
            {
                nodes.Add(new NetworkNode(kind.Value, location, tile, obj));
                return;
            }

            if (IsNetworkStorage(obj, out Chest chest))
            {
                storages.Add(new StorageEntry(chest, location, tile));
                return;
            }

            // Anything else with machine data is a keg, furnace, preserves jar and so on: wiring one to the
            // network is how it becomes available for processing jobs.
            if (MachineIO.IsMachine(obj))
                nodes.Add(new NetworkNode(NodeKind.Machine, location, tile, obj));
        }

        /// <summary>Whether a placed object is a chest the network may use for storage.</summary>
        /// <remarks>
        /// Loot chests, shipping bins and Junimo chests are excluded: they either aren't the player's to take from,
        /// or they already share their contents some other way, and draining them would be a surprise rather than
        /// a feature.
        /// </remarks>
        private static bool IsNetworkStorage(SObject obj, out Chest chest)
        {
            chest = obj as Chest;
            if (chest == null || !chest.playerChest.Value)
                return false;

            return chest.specialChestType.Value is Chest.SpecialChestTypes.None or Chest.SpecialChestTypes.BigChest;
        }
    }
}
