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
