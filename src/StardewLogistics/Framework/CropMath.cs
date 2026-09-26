using System;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.GameData.Crops;
using StardewValley.TerrainFeatures;

namespace StardewLogistics.Framework
{
    /// <summary>Works out how long crops take and whether there's time to grow them.</summary>
    /// <remarks>
    /// Growth time isn't a fixed number: Speed-Gro and the Agriculturist profession each shorten a crop's phases,
    /// and the game rounds per phase. Rather than reproduce that arithmetic, a throwaway soil and crop are built
    /// with the tile's fertilizer and the game's own <c>applySpeedIncreases</c> is left to adjust them, so the
    /// answer matches the real crop to the day.
    /// </remarks>
    internal static class CropMath
    {
        /*********
        ** Fields
        *********/
        /// <summary>The number of days in a season.</summary>
        public const int DaysPerSeason = 28;

        /// <summary>The marker the game puts in a crop's last phase, meaning "grown": not a real length.</summary>
        private const int GrownMarker = 99999;


        /*********
        ** Public methods
        *********/
        /// <summary>The crop data for a seed, or <c>null</c> if it isn't a seed.</summary>
        public static CropData GetData(string seedId)
        {
            string id = Unqualify(seedId);
            return id != null && Game1.cropData != null && Game1.cropData.TryGetValue(id, out CropData data) ? data : null;
        }

        /// <summary>Whether a seed's crop keeps producing after its first harvest.</summary>
        public static bool Regrows(string seedId) => GetData(seedId)?.RegrowDays > 0;

        /// <summary>Days from planting a seed until it can first be harvested, on a tile with a given fertilizer.</summary>
        /// <returns>The days, or <c>null</c> if the seed isn't a crop.</returns>
        public static int? DaysToGrow(string seedId, string fertilizerId, GameLocation location, Vector2 tile)
        {
            string id = Unqualify(seedId);
            if (id == null || GetData(seedId) == null)
                return null;

            try
            {
                HoeDirt soil = new(0, location);
                if (!string.IsNullOrEmpty(fertilizerId))
                    soil.fertilizer.Value = fertilizerId;

                Crop crop = new(id, (int)tile.X, (int)tile.Y, location);
                soil.crop = crop;
                soil.applySpeedIncreases(Game1.player);

                return crop.phaseDays.Where(days => days > 0 && days < GrownMarker).Sum();
            }
            catch
            {
                // Fall back on the unadjusted phases rather than refusing to plan at all.
                return GetData(seedId)?.DaysInPhase?.Sum();
            }
        }

        /// <summary>Days a seed planted today has to grow before its season, and the ones after it, run out.</summary>
        /// <returns>
        /// <see cref="int.MaxValue"/> where seasons don't matter -- the greenhouse, Ginger Island, anywhere that grows
        /// all year -- or a negative number if the seed can't be planted here in this season at all.
        /// </returns>
        /// <remarks>
        /// A crop that grows across consecutive seasons -- corn in summer and fall -- has the whole run of them.
        /// Planted on day D, a crop needing N days is ready on day D + N, which has to fall within that run.
        /// </remarks>
        public static int DaysLeftToGrow(string seedId, GameLocation location)
        {
            CropData data = GetData(seedId);
            if (data == null || location == null)
                return -1;

            if (location.SeedsIgnoreSeasonsHere())
                return int.MaxValue;

            Season season = location.GetSeason();
            if (data.Seasons == null || !data.Seasons.Contains(season))
                return -1;

            int days = DaysPerSeason - Game1.dayOfMonth;
            Season next = season;
            for (int i = 0; i < 3; i++)
            {
                next = (Season)(((int)next + 1) % 4);
                if (!data.Seasons.Contains(next))
                    break;
                days += DaysPerSeason;
            }

            return days;
        }

        /// <summary>Days until a growing crop can be harvested, assuming it's watered every day.</summary>
        /// <returns>Zero if it's ready now, or <c>null</c> for a crop that won't be ready (dead, or not really a crop).</returns>
        public static int? DaysUntilHarvest(HoeDirt soil)
        {
            Crop crop = soil?.crop;
            if (crop == null || crop.dead.Value)
                return null;

            if (soil.readyForHarvest())
                return 0;

            // A regrowing crop counts down the days to its next harvest in its current phase.
            if (crop.fullyGrown.Value)
                return Math.Max(0, crop.dayOfCurrentPhase.Value);

            int phase = crop.currentPhase.Value;
            int total = 0;
            for (int i = phase; i < crop.phaseDays.Count; i++)
            {
                int length = crop.phaseDays[i];
                if (length >= GrownMarker)
                    break;

                total += i == phase ? Math.Max(0, length - crop.dayOfCurrentPhase.Value) : length;
            }

            return total;
        }

        /// <summary>The fewest items a harvest of a seed's crop is guaranteed to give.</summary>
        /// <remarks>Extra-harvest chances and farming-level bonuses are chances, not promises, so they don't count.</remarks>
        public static int GuaranteedYield(string seedId)
        {
            CropData data = GetData(seedId);
            return Math.Max(1, data?.HarvestMinStack ?? 1);
        }

        /// <summary>The most a single harvest of a crop could give, for checking there's room to store it.</summary>
        /// <remarks>
        /// The top of the crop's range, raised by the player's farming level where the crop allows it, plus a
        /// couple for the extra-harvest chance -- the harvest isn't rolled until it happens, so this errs high.
        /// </remarks>
        public static int MaxYield(CropData data)
        {
            if (data == null)
                return 1;

            int top = Math.Max(data.HarvestMinStack, data.HarvestMaxStack);
            top += (int)Math.Floor(data.HarvestMaxIncreasePerFarmingLevel * (Game1.player?.FarmingLevel ?? 0));
            if (data.ExtraHarvestChance > 0)
                top += 2;

            return Math.Max(1, top);
        }

        /// <summary>The qualified item ID a seed's crop yields.</summary>
        public static string HarvestItemId(string seedId)
        {
            string id = GetData(seedId)?.HarvestItemId;
            return string.IsNullOrEmpty(id) ? null : ItemRegistry.QualifyItemId(id);
        }

        /// <summary>A seed's unqualified ID, which is how crop data is keyed.</summary>
        public static string Unqualify(string seedId)
        {
            if (string.IsNullOrWhiteSpace(seedId))
                return null;

            return ItemRegistry.GetData(seedId)?.ItemId ?? seedId;
        }
    }
}
