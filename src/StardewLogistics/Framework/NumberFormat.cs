using System.Globalization;

namespace StardewLogistics.Framework
{
    /// <summary>Formats the large counts a storage network accumulates so they still fit in an inventory slot.</summary>
    internal static class NumberFormat
    {
        /// <summary>Abbreviates a count to at most five characters, e.g. <c>9999</c>, <c>12.3K</c>, <c>4.5M</c>.</summary>
        public static string Abbreviate(long value)
        {
            if (value < 0)
                return "0";
            if (value < 10000)
                return value.ToString(CultureInfo.InvariantCulture);
            if (value < 1000000)
                return Trim(value / 1000d) + "K";
            if (value < 1000000000)
                return Trim(value / 1000000d) + "M";
            return Trim(value / 1000000000d) + "B";
        }

        /// <summary>Formats a count in full with thousands separators, for tooltips.</summary>
        public static string Full(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

        /// <summary>Renders a scaled value with one decimal place, dropping a trailing ".0".</summary>
        private static string Trim(double value)
        {
            if (value >= 100)
                return ((long)value).ToString(CultureInfo.InvariantCulture);

            string text = value.ToString("0.0", CultureInfo.InvariantCulture);
            return text.EndsWith(".0") ? text.Substring(0, text.Length - 2) : text;
        }
    }
}
