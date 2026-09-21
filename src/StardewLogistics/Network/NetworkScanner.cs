using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewValley;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewLogistics.Network
{
    /// <summary>Rebuilds the storage networks in a location by flood-filling the cables placed there.</summary>
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
        /// <param name="config">The mod settings, which decide the channel budget and the size cap.</param>
        public static List<StorageNetwork> Scan(GameLocation location, ModConfig config)
        {
            List<StorageNetwork> networks = new();
            if (location == null)
                return networks;

            // Index the mod's devices once, so the flood fill is pure dictionary lookups afterwards.
            Dictionary<Vector2, NetworkNode> conductors = new();
            Dictionary<Vector2, NetworkNode> devices = new();
            foreach (KeyValuePair<Vector2, SObject> pair in location.Objects.Pairs)
            {
                NodeKind? kind = NetworkNode.GetKind(pair.Value?.ItemId);
                if (kind == null)
                    continue;

                NetworkNode node = new(kind.Value, pair.Key, pair.Value);
                if (node.IsConductive)
                    conductors[pair.Key] = node;
                else
                    devices[pair.Key] = node;
            }

            if (conductors.Count == 0)
                return networks;

            HashSet<Vector2> globallyVisited = new();
            Queue<Vector2> queue = new();

            foreach (Vector2 seed in conductors.Keys)
            {
                if (globallyVisited.Contains(seed))
                    continue;

                List<NetworkNode> nodes = new();
                List<StorageEntry> storages = new();
                HashSet<Vector2> attached = new();

                queue.Clear();
                queue.Enqueue(seed);
                globallyVisited.Add(seed);

                while (queue.Count > 0)
                {
                    Vector2 tile = queue.Dequeue();
                    nodes.Add(conductors[tile]);

                    // A pathological cable run shouldn't be able to stall the game; stop growing and work with
                    // what we have, which still leaves the player a usable (if truncated) network.
                    if (nodes.Count >= config.MaxNetworkSize)
                        break;

                    foreach (Vector2 direction in Directions)
                    {
                        Vector2 neighbour = tile + direction;

                        if (conductors.ContainsKey(neighbour))
                        {
                            if (globallyVisited.Add(neighbour))
                                queue.Enqueue(neighbour);
                            continue;
                        }

                        if (!attached.Add(neighbour))
                            continue;

                        if (devices.TryGetValue(neighbour, out NetworkNode device))
                            nodes.Add(device);
                        else if (location.Objects.TryGetValue(neighbour, out SObject obj) && IsNetworkStorage(obj, out Chest chest))
                            storages.Add(new StorageEntry(chest, neighbour));
                    }
                }

                networks.Add(new StorageNetwork(location, nodes, storages));
            }

            return networks;
        }

        /// <summary>Whether a placed object is a chest the network may use for storage.</summary>
        /// <remarks>
        /// Loot chests, shipping bins and Junimo chests are excluded: they either aren't the player's to take from, or
        /// they already share their contents through some other mechanism, and letting the network drain them would be
        /// a surprise rather than a feature.
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
