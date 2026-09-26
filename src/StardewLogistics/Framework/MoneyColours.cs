using Microsoft.Xna.Framework;

namespace StardewLogistics.Framework
{
    /// <summary>Colours for amounts of gold, so a glance says how well the farm is doing.</summary>
    /// <remarks>
    /// Five tiers, dull to bright: brown, green, blue, purple, gold. Daily income and net worth use the same colours
    /// on their own scales, since a good day's income is a poor net worth.
    /// </remarks>
    internal static class MoneyColours
    {
        /*********
        ** Fields
        *********/
        /// <summary>The tier colours, lowest first.</summary>
        public static readonly Color[] Tiers =
        {
            new(120, 96, 72),
            new(60, 150, 60),
            new(50, 110, 200),
            new(150, 70, 200),
            new(220, 150, 20)
        };

        /// <summary>Where each daily-income tier starts, after the first.</summary>
        public static readonly double[] DailySteps = { 1_000, 5_000, 25_000, 100_000 };

        /// <summary>Where each net-worth tier starts, after the first.</summary>
        public static readonly double[] WorthSteps = { 10_000, 100_000, 1_000_000, 10_000_000 };


        /*********
        ** Public methods
        *********/
        /// <summary>The colour for gold made in a day, or a day's rate.</summary>
        public static Color ForDaily(double gold) => Tiers[Tier(gold, DailySteps)];

        /// <summary>The colour for a total held, like net worth.</summary>
        public static Color ForWorth(double gold) => Tiers[Tier(gold, WorthSteps)];

        /// <summary>Which tier an amount falls in.</summary>
        public static int Tier(double gold, double[] steps)
        {
            int tier = 0;
            while (tier < steps.Length && gold >= steps[tier])
                tier++;
            return tier;
        }

        /// <summary>A distinct colour for the n'th income source in a breakdown.</summary>
        public static Color ForSource(int index) => SourcePalette[((index % SourcePalette.Length) + SourcePalette.Length) % SourcePalette.Length];

        private static readonly Color[] SourcePalette =
        {
            new(220, 90, 70),
            new(70, 160, 220),
            new(110, 190, 80),
            new(230, 170, 40),
            new(170, 100, 210),
            new(60, 190, 170),
            new(230, 120, 180),
            new(140, 120, 90),
            new(120, 140, 230),
            new(200, 200, 70)
        };
    }
}
