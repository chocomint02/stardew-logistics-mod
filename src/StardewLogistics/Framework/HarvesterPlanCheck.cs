using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewLogistics.Network;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace StardewLogistics.Framework
{
    /// <summary>One thing wrong with a crop plan, and the tiles it affects.</summary>
    internal class PlanProblem
    {
        /// <summary>What's wrong, in a sentence.</summary>
        public string Message { get; init; }

        /// <summary>The tiles it affects, by position in the area.</summary>
        public List<Point> Tiles { get; } = new();
    }

    /// <summary>Checks a crop plan against the world and storage, the way a code editor checks for errors.</summary>
    /// <remarks>
    /// Used by the planning grid to mark problem tiles red and list what's wrong, so a plan that can't be carried
    /// out says so before it's confirmed rather than quietly doing nothing. It asks the same questions the harvester
    /// does -- is there seed in storage, is there time in the season -- so what it flags is what won't happen.
    /// </remarks>
    internal static class HarvesterPlanCheck
    {
        /// <summary>Finds everything that would stop a plan being carried out.</summary>
        /// <param name="settings">The plan.</param>
        /// <param name="location">Where the harvester is.</param>
        /// <param name="machineTile">The harvester's tile.</param>
        /// <param name="network">The network it draws from, or <c>null</c> if it isn't connected.</param>
        /// <param name="getName">Names an item for messages.</param>
        /// <param name="translate">Builds a message from a translation key and tokens.</param>
        /// <param name="original">The plan as it was before this edit, to tell which tiles the edit changes.</param>
        /// <param name="force">Whether Force change is on, which clears crops in the way instead of waiting.</param>
        public static List<PlanProblem> Check(HarvesterSettings settings, GameLocation location, Vector2 machineTile, StorageNetwork network, Func<string, string> getName, Func<string, object, string> translate, HarvesterSettings original = null, bool force = false)
        {
            List<PlanProblem> problems = new();
            Rectangle area = settings.GetArea(machineTile);

            // Crops the new plan replaces or clears, which without Force change are left to finish. A regrowing crop
            // somewhere that never ends its season never finishes, so the change would never happen.
            if (!force)
            {
                Dictionary<string, PlanProblem> stuck = new(StringComparer.OrdinalIgnoreCase);
                foreach ((Vector2 tile, Point point, HoeDirt soil) in CropsInTheWay(settings, original, location, machineTile))
                {
                    if (!NeverMakesWay(soil, location))
                        continue;

                    string growing = soil.crop.netSeedIndex.Value;
                    Add(stuck, growing, point, () => translate("plan.problem-stuck", new { name = getName(ItemRegistry.QualifyItemId(soil.crop.indexOfHarvest.Value ?? growing)) }));
                }
                problems.AddRange(stuck.Values);
            }

            PlanProblem blocked = new() { Message = translate("plan.problem-blocked", null) };
            Dictionary<string, PlanProblem> seasonal = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<Point>> seedDemand = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<Point>> fertilizerDemand = new(StringComparer.OrdinalIgnoreCase);

            // Growth time depends on the seed and fertilizer, not the tile; working it out builds a throwaway
            // crop, so it's done once per pairing rather than once per tile of a 50x50 area.
            Dictionary<string, int?> growTimes = new(StringComparer.OrdinalIgnoreCase);
            int? DaysToGrow(string seed, string fertilizer, Vector2 tile)
            {
                string key = seed + "|" + fertilizer;
                if (!growTimes.TryGetValue(key, out int? days))
                    growTimes[key] = days = CropMath.DaysToGrow(seed, fertilizer, location, tile);
                return days;
            }

            foreach ((Point point, TilePlan plan) in settings.Tiles.OrderBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.X))
            {
                Vector2 tile = new(area.X + point.X, area.Y + point.Y);
                location.terrainFeatures.TryGetValue(tile, out TerrainFeature feature);
                HoeDirt soil = feature as HoeDirt;

                if (soil == null && !CanTill(location, tile, feature))
                {
                    blocked.Tiles.Add(point);
                    continue;
                }

                // Fertilizer is only needed where the soil doesn't have it yet.
                if (plan.FertilizerId != null && soil?.HasFertilizer() != true)
                    Demand(fertilizerDemand, plan.FertilizerId, point);

                if (plan.SeedId == null)
                    continue;

                // Already growing what's planned, or done and not to be replanted: nothing to find.
                bool growing = soil?.crop != null && string.Equals(soil.crop.netSeedIndex.Value, CropMath.Unqualify(plan.SeedId), StringComparison.OrdinalIgnoreCase);
                if (growing || (plan.Planted && !settings.ShouldReplant(plan.SeedId, CropMath.Regrows(plan.SeedId))))
                    continue;

                int window = CropMath.DaysLeftToGrow(plan.SeedId, location);
                if (window < 0)
                {
                    Add(seasonal, plan.SeedId, point, () => translate("plan.problem-season", new { name = getName(plan.SeedId) }));
                    continue;
                }

                if (window != int.MaxValue)
                {
                    int? days = DaysToGrow(plan.SeedId, soil?.fertilizer.Value ?? plan.FertilizerId, tile);
                    if (days > window)
                    {
                        Add(seasonal, plan.SeedId + "|late", point, () => translate("plan.problem-late", new { name = getName(plan.SeedId), days, left = window }));
                        continue;
                    }
                }

                Demand(seedDemand, plan.SeedId, point);
            }

            if (blocked.Tiles.Count > 0)
                problems.Add(blocked);
            problems.AddRange(seasonal.Values);

            // More wanted than storage holds: the tiles past what's there are the ones that go without.
            if (network == null && (seedDemand.Count > 0 || fertilizerDemand.Count > 0))
                problems.Add(new PlanProblem { Message = translate("plan.problem-not-connected", null) });
            else
            {
                AddShortfalls(problems, seedDemand, network, getName, translate, "plan.problem-seeds");
                AddShortfalls(problems, fertilizerDemand, network, getName, translate, "plan.problem-fertilizer");
            }

            return problems;
        }

        /// <summary>The growing crops a plan replaces or clears: on tiles planned now or before, but not what's planned now.</summary>
        /// <remarks>
        /// Only tiles the harvester has a plan for, or had one for. A crop the player planted by hand on a tile the
        /// harvester was never told about isn't in its way, however it's planned around.
        /// </remarks>
        public static IEnumerable<(Vector2 Tile, Point Point, HoeDirt Soil)> CropsInTheWay(HarvesterSettings settings, HarvesterSettings original, GameLocation location, Vector2 machineTile)
        {
            Rectangle area = settings.GetArea(machineTile);

            for (int y = 0; y < settings.Height; y++)
            {
                for (int x = 0; x < settings.Width; x++)
                {
                    Point point = new(x, y);
                    Vector2 tile = new(area.X + x, area.Y + y);
                    if (!location.terrainFeatures.TryGetValue(tile, out TerrainFeature feature) || feature is not HoeDirt { crop: not null } soil || soil.crop.dead.Value)
                        continue;

                    settings.Tiles.TryGetValue(point, out TilePlan now);
                    TilePlan before = null;
                    original?.Tiles.TryGetValue(point, out before);
                    if (now?.SeedId == null && before?.SeedId == null)
                        continue;

                    string planned = CropMath.Unqualify(now?.SeedId);
                    if (!string.Equals(soil.crop.netSeedIndex.Value, planned, StringComparison.OrdinalIgnoreCase))
                        yield return (tile, point, soil);
                }
            }
        }

        /// <summary>Whether a crop will never leave its tile on its own: it regrows, and its season never ends here.</summary>
        public static bool NeverMakesWay(HoeDirt soil, GameLocation location)
        {
            if (soil?.crop == null || !soil.crop.RegrowsAfterHarvest())
                return false;

            return location.SeedsIgnoreSeasonsHere() || soil.crop.GetData()?.Seasons?.Count >= 4;
        }

        /// <summary>Whether an empty tile could be tilled.</summary>
        public static bool CanTill(GameLocation location, Vector2 tile, TerrainFeature feature)
        {
            if (feature != null || location.Objects.ContainsKey(tile))
                return false;

            return location.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Diggable", "Back") != null;
        }


        /*********
        ** Private methods
        *********/
        private static void Demand(Dictionary<string, List<Point>> demand, string itemId, Point point)
        {
            if (!demand.TryGetValue(itemId, out List<Point> tiles))
                demand[itemId] = tiles = new List<Point>();
            tiles.Add(point);
        }

        private static void Add(Dictionary<string, PlanProblem> problems, string key, Point point, Func<string> message)
        {
            if (!problems.TryGetValue(key, out PlanProblem problem))
                problems[key] = problem = new PlanProblem { Message = message() };
            problem.Tiles.Add(point);
        }

        private static void AddShortfalls(List<PlanProblem> problems, Dictionary<string, List<Point>> demand, StorageNetwork network, Func<string, string> getName, Func<string, object, string> translate, string key)
        {
            foreach ((string itemId, List<Point> tiles) in demand)
            {
                long held = network?.CountById(itemId) ?? 0;
                if (held >= tiles.Count)
                    continue;

                // Say so when they're in the player's bag: the harvester only takes from storage.
                int inBag = Game1.player.Items.Where(item => item != null && string.Equals(item.QualifiedItemId, itemId, StringComparison.OrdinalIgnoreCase)).Sum(item => item.Stack);
                string message = inBag > 0
                    ? translate(key + "-bag", new { name = getName(itemId), needed = tiles.Count, held, bag = inBag })
                    : translate(key, new { name = getName(itemId), needed = tiles.Count, held });

                PlanProblem problem = new() { Message = message };
                problem.Tiles.AddRange(tiles.Skip((int)held));
                problems.Add(problem);
            }
        }
    }
}
