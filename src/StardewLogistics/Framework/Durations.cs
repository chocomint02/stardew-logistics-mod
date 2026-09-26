using System.Collections.Generic;

namespace StardewLogistics.Framework
{
    /// <summary>Formats in-game durations the same way everywhere they're shown.</summary>
    internal static class Durations
    {
        /// <summary>Formats a number of in-game minutes as days, hours and minutes, dropping empty units.</summary>
        /// <remarks>A keg's week of wine reads "6d 22h 40m" rather than "166h 40m".</remarks>
        public static string Format(int minutes)
        {
            minutes = System.Math.Max(0, minutes);

            int days = minutes / CraftPlan.MinutesPerDay;
            int rest = minutes % CraftPlan.MinutesPerDay;
            int hours = rest / 60;
            int mins = rest % 60;

            List<string> parts = new();
            if (days > 0)
                parts.Add($"{days}d");
            if (hours > 0)
                parts.Add($"{hours}h");
            if (mins > 0 || parts.Count == 0)
                parts.Add($"{mins}m");

            return string.Join(" ", parts);
        }
    }
}
