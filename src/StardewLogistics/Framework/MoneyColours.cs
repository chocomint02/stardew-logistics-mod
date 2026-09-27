using System;
using System.Collections.Generic;
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
        /// <summary>The tier colours out of the box, lowest first.</summary>
        public static readonly Color[] DefaultTiers =
        {
            new(120, 96, 72),
            new(60, 150, 60),
            new(50, 110, 200),
            new(150, 70, 200),
            new(220, 150, 20)
        };

        /// <summary>The tier colours in use, lowest first: the player's choice, or the defaults.</summary>
        public static Color[] Tiers { get; private set; } = (Color[])DefaultTiers.Clone();

        /// <summary>Colours the player has chosen for particular income sources, by name.</summary>
        private static Dictionary<string, Color> SourceColours = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Colours on offer when the player picks one: a spread of hues, then earthy tones and greys.</summary>
        public static readonly Color[] Choices =
        {
            new(220, 90, 70), new(240, 130, 60), new(230, 170, 40), new(240, 210, 70), new(200, 200, 70), new(160, 200, 70),
            new(110, 190, 80), new(60, 150, 60), new(60, 190, 170), new(90, 200, 220), new(70, 160, 220), new(50, 110, 200),
            new(120, 140, 230), new(150, 70, 200), new(170, 100, 210), new(230, 120, 180), new(220, 80, 120), new(190, 60, 60),
            new(140, 120, 90), new(120, 96, 72), new(190, 160, 120), new(230, 230, 235), new(160, 160, 170), new(90, 90, 100)
        };

        /// <summary>Where each daily-income tier starts, after the first.</summary>
        public static readonly double[] DailySteps = { 1_000, 5_000, 25_000, 100_000 };

        /// <summary>Where each net-worth tier starts, after the first.</summary>
        public static readonly double[] WorthSteps = { 10_000, 100_000, 1_000_000, 10_000_000 };


        /*********
        ** Public methods
        *********/
        /// <summary>The colour for gold made in a day, or a day's rate.</summary>
        public static Color ForDaily(double gold) => Menus.UiTheme.Legible(Tiers[Tier(gold, DailySteps)]);

        /// <summary>The colour for a total held, like net worth.</summary>
        public static Color ForWorth(double gold) => Menus.UiTheme.Legible(Tiers[Tier(gold, WorthSteps)]);

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

        /// <summary>An income source's colour: the player's choice for it, or the n'th in the palette.</summary>
        public static Color ForSource(string name, int index) => name != null && SourceColours.TryGetValue(name, out Color chosen) ? chosen : ForSource(index);

        /// <summary>Sets the colours the player has chosen: tier colours as hex, lowest first, and source colours by name.</summary>
        public static void Configure(IReadOnlyList<string> tiers, IReadOnlyDictionary<string, string> sources)
        {
            Color[] chosen = (Color[])DefaultTiers.Clone();
            for (int i = 0; i < chosen.Length && tiers != null && i < tiers.Count; i++)
            {
                if (TryParseHex(tiers[i], out Color colour))
                    chosen[i] = colour;
            }
            Tiers = chosen;

            Dictionary<string, Color> named = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string name, string hex) in sources ?? new Dictionary<string, string>())
            {
                if (name != null && TryParseHex(hex, out Color colour))
                    named[name] = colour;
            }
            SourceColours = named;
        }

        /// <summary>A colour as "#RRGGBB".</summary>
        public static string ToHex(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

        /// <summary>Reads a colour written as "#RRGGBB" or "RRGGBB".</summary>
        public static bool TryParseHex(string text, out Color colour)
        {
            colour = Color.White;
            string hex = text?.Trim().TrimStart('#');
            if (hex == null || hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int value))
                return false;

            colour = new Color((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
            return true;
        }

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
