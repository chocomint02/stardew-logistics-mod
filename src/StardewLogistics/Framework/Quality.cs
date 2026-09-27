using System;
using SObject = StardewValley.Object;

namespace StardewLogistics.Framework
{
    /// <summary>Helpers for item quality as autocrafting uses it.</summary>
    /// <remarks>
    /// Planning normally treats quality as interchangeable and spends the lowest first, so a gold Starfruit is
    /// only used once the normal ones run out. A recipe tied to one quality -- iridium wool that always weaves two
    /// cloth -- carries that quality explicitly, and <see cref="Any"/> marks everything that doesn't care.
    /// </remarks>
    internal static class Quality
    {
        /// <summary>Accepts any quality, spending the lowest first.</summary>
        public const int Any = -1;

        /// <summary>The name of a quality, for plan rows and logs.</summary>
        public static string Name(int quality)
        {
            return quality switch
            {
                SObject.medQuality => "silver",
                SObject.highQuality => "gold",
                SObject.bestQuality => "iridium",
                _ => "normal"
            };
        }

        /// <summary>How many quality levels lie between two qualities: normal to iridium is three.</summary>
        /// <remarks>Fairy Dust on a cask moves its item up one level, so this is how many a cask run needs.</remarks>
        public static int Steps(int from, int to)
        {
            int steps = 0;
            int quality = Math.Max(SObject.lowQuality, from);
            while (quality < to)
            {
                quality = quality switch
                {
                    SObject.lowQuality => SObject.medQuality,
                    SObject.medQuality => SObject.highQuality,
                    _ => SObject.bestQuality
                };
                steps++;
            }
            return steps;
        }

        /// <summary>How much more a quality sells for than normal, which is how much more it costs to use one.</summary>
        public static double PriceMultiplier(int quality)
        {
            return quality switch
            {
                SObject.medQuality => 1.25,
                SObject.highQuality => 1.5,
                SObject.bestQuality => 2.0,
                _ => 1.0
            };
        }
    }
}
