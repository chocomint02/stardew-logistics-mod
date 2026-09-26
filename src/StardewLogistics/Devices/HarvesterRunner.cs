using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewValley;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace StardewLogistics.Devices
{
    /// <summary>Tends the ground under every auto-harvester: tills, fertilizes, plants, waters and harvests.</summary>
    /// <remarks>
    /// Everything is done with the game's own soil and crop code -- <c>makeHoeDirt</c>, <c>HoeDirt.plant</c>,
    /// <c>Crop.harvest</c> -- so crops behave exactly as if the player had done the work: season rules, Speed-Gro,
    /// quality and extra harvests all come out the same. Seeds and fertilizer come from the network, and the
    /// harvest goes into it. A harvester that isn't wired to a network does nothing, since it has nowhere to get
    /// seeds from or put crops.
    ///
    /// Runs on the host, each morning and every ten in-game minutes, so a newly painted plan starts without
    /// waiting for tomorrow.
    /// </remarks>
    internal class HarvesterRunner
    {
        /*********
        ** Fields
        *********/
        private readonly NetworkManager Networks;
        private readonly StardewModdingAPI.ITranslationHelper Translations;

        /// <summary>Every auto-harvester in the world, or <c>null</c> if they need finding again.</summary>
        private List<(GameLocation Location, Vector2 Tile)> Harvesters;


        /*********
        ** Accessors
        *********/
        /// <summary>Takes a crop's harvest for the autocrafting job that reserved it, if any.</summary>
        /// <remarks>Returns where the harvest should go instead of storage, or <c>null</c> if the crop isn't reserved.</remarks>
        public Func<GameLocation, Vector2, JobBuffer> ClaimHarvest { get; set; }


        /*********
        ** Public methods
        *********/
        public HarvesterRunner(NetworkManager networks, StardewModdingAPI.ITranslationHelper translations)
        {
            this.Networks = networks;
            this.Translations = translations;
        }

        /// <summary>Forgets where the harvesters are, for when one is placed or removed.</summary>
        public void Invalidate() => this.Harvesters = null;

        /// <summary>Works every harvester's area once.</summary>
        public void Run()
        {
            foreach ((GameLocation location, Vector2 tile) in this.GetHarvesters())
            {
                if (!location.Objects.TryGetValue(tile, out SObject machine) || machine.ItemId != ModIds.AutoHarvester)
                {
                    this.Invalidate();
                    continue;
                }

                StorageNetwork network = this.Networks.GetNetworkAt(location, tile);
                if (network == null)
                    continue;

                this.Work(location, tile, machine, network);
            }
        }

        /// <summary>Works one harvester's area once.</summary>
        public void Work(GameLocation location, Vector2 machineTile, SObject machine, StorageNetwork network)
        {
            HarvesterSettings settings = HarvesterSettings.Read(machine);
            Rectangle area = settings.GetArea(machineTile);
            bool raining = location.IsOutdoors && location.IsRainingHere();
            bool changed = false;
            int harvested = 0;
            int planted = 0;
            int waiting = 0;

            for (int y = 0; y < area.Height; y++)
            {
                for (int x = 0; x < area.Width; x++)
                {
                    Vector2 tile = new(area.X + x, area.Y + y);
                    settings.Tiles.TryGetValue(new Point(x, y), out TilePlan plan);

                    HoeDirt soil = location.terrainFeatures.TryGetValue(tile, out TerrainFeature feature) ? feature as HoeDirt : null;

                    // Clear the dead and gather the ripe, planned or not: the whole area is the harvester's.
                    if (soil?.crop != null)
                    {
                        if (soil.crop.dead.Value)
                            soil.destroyCrop(showAnimation: false);
                        else if (soil.readyForHarvest())
                        {
                            // A crop an autocrafting job reserved goes to that job, not storage, so storage room
                            // doesn't matter for it.
                            JobBuffer reserved = this.ClaimHarvest?.Invoke(location, tile);
                            if (reserved != null)
                            {
                                if (this.Harvest(location, tile, soil, item => { reserved.Add(item); return true; }))
                                    harvested++;
                            }

                            // Otherwise leave it in the field rather than harvest onto the ground: it keeps there
                            // until storage has room, and the next pass tries again.
                            else if (!HasRoomForHarvest(soil.crop, network))
                                waiting++;
                            else if (this.Harvest(location, tile, soil, item => { network.Insert(item); return item.Stack <= 0; }))
                                harvested++;
                        }
                    }

                    if (plan?.SeedId != null)
                    {
                        soil ??= this.Till(location, tile);
                        if (soil != null && soil.crop == null)
                        {
                            this.Fertilize(soil, plan, network);
                            if (this.TryPlant(location, tile, soil, plan, settings, network))
                            {
                                planted++;
                                changed = true;
                            }
                        }
                    }

                    // Water anything growing here, and planned soil waiting for its seed; the rain does it outdoors.
                    bool wanted = soil?.crop != null ? soil.crop.GetData()?.NeedsWatering != false : plan?.SeedId != null;
                    if (soil != null && wanted && !raining && soil.state.Value == HoeDirt.dry)
                        soil.state.Value = HoeDirt.watered;
                }
            }

            if (changed)
                settings.Write(machine);

            if (waiting > 0)
            {
                string key = $"full|{location.NameOrUniqueName}|{machineTile}";
                if (LogOnce(key, $"Auto-harvester at {location.NameOrUniqueName} {machineTile}: storage is full, so {waiting} ripe crops are waiting in the field."))
                    Game1.addHUDMessage(new HUDMessage(this.Translations.Get("harvester.storage-full", new { count = waiting }), HUDMessage.error_type));
            }

            if (harvested > 0 || planted > 0)
                Log.Trace($"Auto-harvester at {location.NameOrUniqueName} {machineTile}: harvested {harvested}, planted {planted}.");
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Harvests a ripe crop with the game's own code, and puts what it gives into storage.</summary>
        /// <remarks>
        /// The harvest is done as a scythe harvest, which drops the produce instead of putting it in the player's
        /// bag. Exactly the drops that harvest made are then taken into storage; anything storage can't hold is left
        /// on the ground rather than lost.
        ///
        /// The game drops the produce wherever the <em>player</em> is, not where the crop is -- so a morning harvest,
        /// with the player just awake in the farmhouse, drops it on the farmhouse floor. Both places are checked.
        /// </remarks>
        /// <param name="store">Takes one harvested item, returning whether all of it was taken.</param>
        private bool Harvest(GameLocation location, Vector2 tile, HoeDirt soil, Func<Item, bool> store)
        {
            Crop crop = soil.crop;
            GameLocation playerLocation = Game1.currentLocation;
            int before = location.debris.Count;
            int beforeElsewhere = playerLocation != null && playerLocation != location ? playerLocation.debris.Count : 0;

            bool done;
            try
            {
                done = crop.harvest((int)tile.X, (int)tile.Y, soil, null, isForcedScytheHarvest: true);
            }
            catch (System.Exception ex)
            {
                Log.Trace($"Couldn't harvest the crop at {tile}: {ex.Message}");
                return false;
            }

            TakeNewDrops(location, before, store);
            if (playerLocation != null && playerLocation != location)
                TakeNewDrops(playerLocation, beforeElsewhere, store);

            // A one-harvest crop is spent; a regrowing one has already reset itself for its next harvest.
            if (done && !crop.RegrowsAfterHarvest())
                soil.destroyCrop(showAnimation: false);

            return done;
        }

        /// <summary>Whether storage could hold the most a ripe crop might give.</summary>
        private static bool HasRoomForHarvest(Crop crop, StorageNetwork network)
        {
            string harvestId = crop.indexOfHarvest.Value;
            Item sample = string.IsNullOrEmpty(harvestId) ? null : ItemRegistry.Create(harvestId, allowNull: true);
            return sample == null || network.HasRoomFor(sample, CropMath.MaxYield(crop.GetData()));
        }

        /// <summary>Moves items dropped since a point in a location's debris list into storage.</summary>
        private static void TakeNewDrops(GameLocation location, int before, Func<Item, bool> store)
        {
            for (int i = location.debris.Count - 1; i >= before; i--)
            {
                Item item = location.debris[i].item;
                if (item != null && store(item))
                    location.debris.RemoveAt(i);
            }
        }

        /// <summary>Every crop growing under the harvesters on a network, with when each will be ready.</summary>
        /// <remarks>
        /// A crop that won't be ready before its season -- or run of seasons -- ends is left out: it will die first,
        /// so nothing can be planned on it. Only the guaranteed yield is counted.
        /// </remarks>
        public List<IncomingCrop> Forecast(StorageNetwork network)
        {
            List<IncomingCrop> crops = new();
            if (network == null)
                return crops;

            foreach ((GameLocation location, Vector2 machineTile) in this.GetHarvesters())
            {
                if (!network.Contains(location, machineTile) || !location.Objects.TryGetValue(machineTile, out SObject machine))
                    continue;

                Rectangle area = HarvesterSettings.ReadCached(machine).GetArea(machineTile);
                for (int y = area.Top; y < area.Bottom; y++)
                {
                    for (int x = area.Left; x < area.Right; x++)
                    {
                        Vector2 tile = new(x, y);
                        if (!location.terrainFeatures.TryGetValue(tile, out TerrainFeature feature) || feature is not HoeDirt { crop: not null } soil || soil.crop.dead.Value)
                            continue;

                        int? days = CropMath.DaysUntilHarvest(soil);
                        string harvest = soil.crop.indexOfHarvest.Value;
                        if (days == null || string.IsNullOrEmpty(harvest))
                            continue;

                        int window = CropMath.DaysLeftToGrow(soil.crop.netSeedIndex.Value, location);
                        if (window != int.MaxValue && days > Math.Max(0, window))
                            continue;

                        crops.Add(new IncomingCrop
                        {
                            Location = location,
                            Tile = tile,
                            HarvesterTile = machineTile,
                            ItemId = ItemRegistry.QualifyItemId(harvest),
                            Count = Math.Max(1, soil.crop.GetData()?.HarvestMinStack ?? 1),
                            Days = days.Value
                        });
                    }
                }
            }

            return crops;
        }

        /// <summary>The auto-harvesters on a network.</summary>
        public IEnumerable<(GameLocation Location, Vector2 Tile)> GetHarvestersOn(StorageNetwork network)
        {
            return network == null
                ? Enumerable.Empty<(GameLocation, Vector2)>()
                : this.GetHarvesters().Where(entry => network.Contains(entry.Item1, entry.Item2));
        }

        /// <summary>Tills a tile for planting, if the ground allows it.</summary>
        /// <returns>The new soil, or <c>null</c> if the tile can't be tilled -- something's in the way, or it isn't soil.</returns>
        private HoeDirt Till(GameLocation location, Vector2 tile)
        {
            if (location.Objects.ContainsKey(tile) || location.terrainFeatures.ContainsKey(tile))
                return null;

            if (!location.makeHoeDirt(tile))
                return null;

            return location.terrainFeatures.TryGetValue(tile, out TerrainFeature feature) ? feature as HoeDirt : null;
        }

        /// <summary>Lays the planned fertilizer on empty soil, from storage.</summary>
        private void Fertilize(HoeDirt soil, TilePlan plan, StorageNetwork network)
        {
            if (plan.FertilizerId == null || soil.HasFertilizer() || !soil.CanApplyFertilizer(plan.FertilizerId))
                return;

            Item fertilizer = network.ExtractById(plan.FertilizerId, 1).FirstOrDefault();
            if (fertilizer == null)
                return;

            if (!soil.plant(plan.FertilizerId, Game1.player, isFertilizer: true))
                network.Insert(fertilizer);
        }

        /// <summary>Plants the planned seed on empty soil, if it should be and there's time for it to grow.</summary>
        private bool TryPlant(GameLocation location, Vector2 tile, HoeDirt soil, TilePlan plan, HarvesterSettings settings, StorageNetwork network)
        {
            if (!ShouldPlant(location, tile, soil.fertilizer.Value, plan, settings))
                return false;

            Item seed = network.ExtractById(plan.SeedId, 1).FirstOrDefault();
            if (seed == null)
            {
                LogOnce($"no {plan.SeedId} in storage", $"Auto-harvester at {location.NameOrUniqueName}: no {StockId.GetDisplayName(plan.SeedId)} in storage to plant. Seeds in the player's bag aren't used.");
                return false;
            }

            // Crop data is keyed by the seed's plain ID ("472"), which is what the game hands this from a player's
            // click. Given the qualified form, the lookup finds nothing and planting silently fails.
            if (!soil.plant(CropMath.Unqualify(plan.SeedId), Game1.player, isFertilizer: false))
            {
                network.Insert(seed);
                LogOnce($"refused {plan.SeedId}", $"Auto-harvester at {location.NameOrUniqueName}: the game wouldn't let {StockId.GetDisplayName(plan.SeedId)} be planted at {tile}.");
                return false;
            }

            plan.Planted = true;
            return true;
        }

        /// <summary>Whether a tile's planned seed should be planted now.</summary>
        /// <remarks>
        /// Only the first planting is automatic. After a harvest the seed goes back in if its crop is set to replant,
        /// and in any case only where it will be ready before its season -- or run of seasons -- ends. That's worked
        /// out with the fertilizer actually on the tile, so Speed-Gro buys the extra days it really does.
        /// </remarks>
        public static bool ShouldPlant(GameLocation location, Vector2 tile, string fertilizerId, TilePlan plan, HarvesterSettings settings)
        {
            if (plan?.SeedId == null || CropMath.GetData(plan.SeedId) == null)
                return false;

            if (plan.Planted && !settings.ShouldReplant(plan.SeedId, CropMath.Regrows(plan.SeedId)))
                return false;

            int window = CropMath.DaysLeftToGrow(plan.SeedId, location);
            if (window < 0)
                return false;

            if (window != int.MaxValue)
            {
                int? needed = CropMath.DaysToGrow(plan.SeedId, fertilizerId ?? plan.FertilizerId, location, tile);
                if (needed == null || needed > window)
                    return false;
            }

            return location.CanPlantSeedsHere(CropMath.Unqualify(plan.SeedId), (int)tile.X, (int)tile.Y, isGardenPot: false, out _);
        }

        /// <summary>Why a tile wasn't planted, logged once a day per reason so the log says without flooding.</summary>
        /// <returns>Whether this is the first time today, and so was logged.</returns>
        private static bool LogOnce(string key, string message)
        {
            string dated = Game1.Date.TotalDays + "|" + key;
            if (!Logged.Add(dated))
                return false;

            Log.Trace(message);
            return true;
        }

        private static readonly HashSet<string> Logged = new();

        /// <summary>Every auto-harvester in the world, found once and remembered.</summary>
        private List<(GameLocation, Vector2)> GetHarvesters()
        {
            if (this.Harvesters != null)
                return this.Harvesters;

            List<(GameLocation, Vector2)> found = new();
            Utility.ForEachLocation(location =>
            {
                foreach ((Vector2 tile, SObject obj) in location.Objects.Pairs)
                {
                    if (obj?.ItemId == ModIds.AutoHarvester)
                        found.Add((location, tile));
                }
                return true;
            }, includeInteriors: true, includeGenerated: false);

            return this.Harvesters = found;
        }
    }
}
