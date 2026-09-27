using System;
using HarmonyLib;
using StardewLogistics.Framework;
using StardewValley;
using StardewValley.Objects;

namespace StardewLogistics.Integrations
{
    /// <summary>Keeps an autocrafting job's cask contents from being knocked onto the ground.</summary>
    /// <remarks>
    /// Hitting a cask with a pickaxe or axe pops its item out as debris, which is how a player takes something
    /// out early. For a cask a job is using, that item is the job's -- a wine partway to iridium -- so it goes back
    /// into the job instead, which then carries on aging it in another cask. A patch because there's no event to
    /// hook, and by the time the debris exists the item has already left the cask.
    /// </remarks>
    internal static class CaskPatches
    {
        /*********
        ** Fields
        *********/
        /// <summary>Takes a job's item back out of a struck cask, returning whether it did.</summary>
        private static Func<Cask, bool> Reclaim;


        /*********
        ** Public methods
        *********/
        /// <summary>Applies the patch.</summary>
        public static void Apply(Harmony harmony, Func<Cask, bool> reclaim)
        {
            Reclaim = reclaim;
            harmony.Patch(
                original: AccessTools.Method(typeof(Cask), nameof(Cask.performToolAction)),
                prefix: new HarmonyMethod(typeof(CaskPatches), nameof(Before_PerformToolAction))
            );
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Runs before a cask is hit with a tool.</summary>
        /// <returns>Whether to run the game's own handling; <c>false</c> once the job has taken its item back.</returns>
        private static bool Before_PerformToolAction(Cask __instance, Tool t, ref bool __result)
        {
            try
            {
                // Mirror the game's own test for "this hit empties the cask", so every other hit is untouched.
                if (t == null || !t.isHeavyHitter() || __instance.heldObject.Value == null)
                    return true;

                if (Reclaim?.Invoke(__instance) != true)
                    return true;

                __instance.playNearbySoundAll("woodWhack");
                __result = false; // the cask stays put, as it would after the game's own emptying hit
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't return a struck cask's item to its autocrafting job; the game will drop it instead.", ex);
                return true;
            }
        }
    }
}
