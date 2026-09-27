using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewValley;
using StardewValley.Locations;

namespace StardewLogistics.Integrations
{
    /// <summary>Keeps tilled soil in an auto-harvester's area from reverting overnight while nothing grows in it.</summary>
    /// <remarks>
    /// Each night the game gives every empty patch of hoed soil a chance to turn back into plain ground, asking the
    /// location how likely that is per tile. Answering "never" for the harvester's tiles keeps them tilled -- and
    /// keeps any fertilizer in them -- without touching soil anywhere else. Ginger Island's farm has its own answer,
    /// so it's patched too.
    /// </remarks>
    internal static class SoilPatches
    {
        /*********
        ** Fields
        *********/
        /// <summary>Whether a tile is inside an auto-harvester's area.</summary>
        private static Func<GameLocation, Vector2, bool> IsProtected;


        /*********
        ** Public methods
        *********/
        /// <summary>Applies the patches.</summary>
        public static void Apply(Harmony harmony, Func<GameLocation, Vector2, bool> isProtected)
        {
            IsProtected = isProtected;

            foreach (Type type in new[] { typeof(GameLocation), typeof(IslandWest) })
            {
                var method = AccessTools.DeclaredMethod(type, nameof(GameLocation.GetDirtDecayChance), new[] { typeof(Vector2) });
                if (method == null)
                {
                    Log.Debug($"Couldn't find {type.Name}.GetDirtDecayChance; empty soil under auto-harvesters there may revert overnight.");
                    continue;
                }

                harmony.Patch(method, prefix: new HarmonyMethod(typeof(SoilPatches), nameof(Before_GetDirtDecayChance)));
            }
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Runs before the game decides how likely a tile of empty soil is to revert tonight.</summary>
        /// <returns>Whether to run the game's own answer; <c>false</c> for a harvester's tile, which never reverts.</returns>
        private static bool Before_GetDirtDecayChance(GameLocation __instance, Vector2 tile, ref double __result)
        {
            try
            {
                if (IsProtected?.Invoke(__instance, tile) != true)
                    return true;

                __result = 0;
                return false;
            }
            catch (Exception ex)
            {
                Log.Trace($"Soil protection check failed at {tile}: {ex.Message}");
                return true;
            }
        }
    }
}
