using System;
using System.Collections.Generic;
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

        /// <summary>Speed-Gro, Deluxe Speed-Gro and Hyper Speed-Gro, in the order the planner offers them.</summary>
        public static readonly string[] SpeedGro = { "(O)465", "(O)466", "(O)918" };

        /// <summary>Seeds by what they yield, built from the crop data it was built from.</summary>
        private static Dictionary<string, List<string>> SeedsByHarvest;
        private static object SeedsSource;


        /*********
        ** Public methods
        *********/
        /// <summary>The crop data for a seed, or <c>null</c> if it isn't a seed.</summary>
        public static CropData GetData(string seedId)
        {
            // Crop data is keyed by object ID; a big craftable numbered like a seed isn't one.
            if (seedId != null && seedId.StartsWith("(") && !seedId.StartsWith("(O)", StringComparison.OrdinalIgnoreCase))
                return null;

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

                // The game's own speed-ups are in the phases now; how fast crops really grow here is learned.
                int days = crop.phaseDays.Where(days => days > 0 && days < GrownMarker).Sum();
                return Calibration.CropDays(HarvestItemId(seedId), days);
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
        /// <remarks>Allows for how fast crops have been seen to grow in this save; see <see cref="Calibration"/>.</remarks>
        public static int? DaysUntilHarvest(HoeDirt soil)
        {
            int? days = RawDaysUntilHarvest(soil);
            string harvest = soil?.crop?.indexOfHarvest.Value;
            return days is > 0 && !string.IsNullOrEmpty(harvest)
                ? Calibration.CropDays(ItemRegistry.QualifyItemId(harvest) ?? harvest, days.Value)
                : days;
        }

        /// <summary>Days until a growing crop can be harvested by the game's own count, a day's growth a night.</summary>
        public static int? RawDaysUntilHarvest(HoeDirt soil)
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

        /// <summary>The seeds whose crop yields an item.</summary>
        /// <remarks>
        /// The season seed packs are left out: they grow whatever forage the season picks, so they can't be
        /// planted for anything in particular.
        /// </remarks>
        public static IReadOnlyList<string> SeedsFor(string itemId)
        {
            if (string.IsNullOrEmpty(itemId) || Game1.cropData == null)
                return Array.Empty<string>();

            if (SeedsByHarvest == null || !ReferenceEquals(SeedsSource, Game1.cropData))
            {
                Dictionary<string, List<string>> index = new(StringComparer.OrdinalIgnoreCase);
                foreach ((string seed, CropData data) in Game1.cropData)
                {
                    if (string.IsNullOrEmpty(data?.HarvestItemId) || seed is "495" or "496" or "497" or "498")
                        continue;

                    string seedId = ItemRegistry.QualifyItemId(seed);
                    string harvest = ItemRegistry.QualifyItemId(data.HarvestItemId);
                    if (seedId == null || harvest == null || ItemRegistry.GetData(seedId) == null)
                        continue;

                    if (!index.TryGetValue(harvest, out List<string> seeds))
                        index[harvest] = seeds = new List<string>();
                    seeds.Add(seedId);
                }

                SeedsByHarvest = index;
                SeedsSource = Game1.cropData;
            }

            return SeedsByHarvest.TryGetValue(itemId, out List<string> found) ? found : Array.Empty<string>();
        }

        /// <summary>Whether two fertilizer IDs are the same fertilizer, however each is written.</summary>
        public static bool SameFertilizer(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);
            return string.Equals(ItemRegistry.QualifyItemId(a), ItemRegistry.QualifyItemId(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether a fertilizer is one of the Speed-Gros.</summary>
        public static bool IsSpeedGro(string fertilizerId)
        {
            string id = string.IsNullOrEmpty(fertilizerId) ? null : ItemRegistry.QualifyItemId(fertilizerId);
            return id != null && SpeedGro.Contains(id, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The fertilizers in some soil: one, or several where a mod lets them stack.</summary>
        public static IReadOnlyList<string> FertilizersOf(HoeDirt soil) => SplitFertilizers(FertilizerOf(soil));

        /// <summary>The fertilizers a soil's fertilizer field holds.</summary>
        /// <remarks>
        /// The game keeps one fertilizer ID in the field. Mods that let fertilizers stack keep a list in the same
        /// field instead -- "(O)465|(O)369" -- since it's the one that saves and syncs. A value that isn't itself an
        /// item is split on the usual separators, keeping the parts that are items, so any such mod reads right.
        /// </remarks>
        public static List<string> SplitFertilizers(string raw)
        {
            List<string> found = new();
            if (string.IsNullOrWhiteSpace(raw) || raw == "0")
                return found;

            if (IsItem(raw))
            {
                found.Add(raw);
                return found;
            }

            foreach (char separator in Separators)
            {
                if (!raw.Contains(separator))
                    continue;

                foreach (string part in raw.Split(separator, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (IsItem(part.Trim()))
                        found.Add(part.Trim());
                }

                // Remember how the world writes a stack, to write one the same way when working out growth times.
                if (found.Count > 1)
                    StackSeparator = separator;
                return found;
            }
            return found;
        }

        /// <summary>The separators a mod might put between stacked fertilizers.</summary>
        private static readonly char[] Separators = { '|', ',', ';', ' ' };

        /// <summary>The separator stacked fertilizer has been seen written with in this game; '|' until one's seen.</summary>
        private static char StackSeparator = '|';

        /// <summary>Whether a soil's fertilizer field includes a fertilizer.</summary>
        public static bool HasFertilizer(string raw, string fertilizerId) => fertilizerId != null && SplitFertilizers(raw).Any(id => SameFertilizer(id, fertilizerId));

        /// <summary>Whether a soil's fertilizer field includes any Speed-Gro.</summary>
        public static bool HasSpeedGro(string raw) => SplitFertilizers(raw).Any(IsSpeedGro);

        /// <summary>Lays a fertilizer on soil the way a player would, under whatever rules the game -- and any mod -- sets.</summary>
        /// <param name="soil">The soil.</param>
        /// <param name="fertilizerId">The fertilizer.</param>
        /// <param name="replace">Whether to take off what's there when the soil won't take another, as the game only allows one.</param>
        /// <returns>Whether the soil has the fertilizer now.</returns>
        /// <remarks>
        /// The soil is asked first whether it takes this fertilizer as it is, and it's laid through the game's own
        /// planting: a mod that lets fertilizers stack, or be laid after sprouting, says yes and adds it its own way.
        /// Only where the soil won't take it is what's there removed first, and put back if even that doesn't work.
        /// </remarks>
        public static bool LayFertilizer(HoeDirt soil, string fertilizerId, bool replace)
        {
            if (soil == null || string.IsNullOrEmpty(fertilizerId))
                return false;
            if (HasFertilizer(FertilizerOf(soil), fertilizerId))
                return true;

            try
            {
                if (soil.CanApplyFertilizer(fertilizerId))
                    return soil.plant(fertilizerId, Game1.player, isFertilizer: true);

                if (!replace || !soil.HasFertilizer())
                    return false;

                string existing = soil.fertilizer.Value;
                soil.fertilizer.Value = null;
                if (soil.CanApplyFertilizer(fertilizerId) && soil.plant(fertilizerId, Game1.player, isFertilizer: true))
                    return true;

                soil.fertilizer.Value = existing;
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The fertilizer field a tile would have once a fertilizer is laid on it, for working out growth time.</summary>
        /// <param name="existing">The tile's fertilizer field now.</param>
        /// <param name="added">The fertilizer to lay, or <c>null</c> for none.</param>
        /// <param name="location">The tile's location.</param>
        /// <param name="tile">The tile.</param>
        /// <param name="keepSpeedGro">Whether a Speed-Gro already there stays rather than being replaced, where the soil takes only one.</param>
        /// <remarks>
        /// Where the soil takes another fertilizer (a mod letting them stack), both, written the way stacked
        /// fertilizer has been seen written in this game; otherwise the new one in place of the old. Only an
        /// estimate: once planted, the crop's own growth is read from the crop itself.
        /// </remarks>
        public static string FertilizerAfterLaying(string existing, string added, GameLocation location, Vector2 tile, bool keepSpeedGro = false)
        {
            if (string.IsNullOrEmpty(added) || HasFertilizer(existing, added))
                return existing;
            if (SplitFertilizers(existing).Count == 0)
                return added;

            try
            {
                HoeDirt probe = new(0, location);
                probe.fertilizer.Value = existing;
                if (probe.CanApplyFertilizer(added))
                    return existing + StackSeparator + added;
            }
            catch
            {
                // Treat it as taking one.
            }

            return keepSpeedGro && HasSpeedGro(existing) ? existing : added;
        }

        /// <summary>Whether an ID names an item.</summary>
        private static bool IsItem(string id)
        {
            try
            {
                return ItemRegistry.GetData(ItemRegistry.QualifyItemId(id)) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The fertilizer in some soil, or <c>null</c> if it has none.</summary>
        /// <remarks>
        /// Read through this rather than the field: tilled soil can hold "0", which the game treats as no
        /// fertilizer. Taken at face value, it made every hoed tile look fertilized, so Speed-Gro was never laid.
        /// </remarks>
        public static string FertilizerOf(HoeDirt soil)
        {
            return soil != null && soil.HasFertilizer() ? soil.fertilizer.Value : null;
        }

        /// <summary>Whether the crop in some soil was planted by autocrafting.</summary>
        public static bool IsAutomationCrop(HoeDirt soil)
        {
            return soil?.crop != null
                && soil.modData.TryGetValue(ModIds.AutomationCropKey, out string seed)
                && string.Equals(seed, soil.crop.netSeedIndex.Value, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Marks the crop in some soil as planted by autocrafting.</summary>
        public static void MarkAutomationCrop(HoeDirt soil)
        {
            if (soil?.crop != null)
                soil.modData[ModIds.AutomationCropKey] = soil.crop.netSeedIndex.Value ?? "";
        }

        /// <summary>Clears the automation mark from soil whose crop is gone.</summary>
        public static void ClearAutomationMark(HoeDirt soil) => soil?.modData.Remove(ModIds.AutomationCropKey);

        /// <summary>A seed's unqualified ID, which is how crop data is keyed.</summary>
        public static string Unqualify(string seedId)
        {
            if (string.IsNullOrWhiteSpace(seedId))
                return null;

            return ItemRegistry.GetData(seedId)?.ItemId ?? seedId;
        }
    }
}
