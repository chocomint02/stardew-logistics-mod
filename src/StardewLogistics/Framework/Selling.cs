using System;
using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>What items fetch in the shipping bin.</summary>
    /// <remarks>
    /// Prices come from the game's own <c>sellToStorePrice</c>, the figure shipping pays out: quality, and the
    /// player's professions (Artisan, Tiller and the rest), are already in it. What the shipping bin won't take is
    /// worth nothing here, and can't be shipped from storage either.
    /// </remarks>
    internal static class Selling
    {
        /// <summary>Whether the shipping bin takes an item.</summary>
        public static bool CanSell(Item item)
        {
            if (item == null)
                return false;

            try
            {
                return Utility.highlightShippableObjects(item);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>What one of an item sells for, or <c>null</c> if it can't be shipped.</summary>
        public static int? UnitPrice(Item item)
        {
            if (!CanSell(item))
                return null;

            try
            {
                return Math.Max(0, item.sellToStorePrice());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>What an item would sell for at another quality, or <c>null</c> if it can't be shipped.</summary>
        public static int? UnitPrice(Item item, int quality)
        {
            if (item == null)
                return null;
            if (quality < 0 || item.Quality == quality)
                return UnitPrice(item);

            Item copy = item.getOne();
            copy.Quality = quality;
            return UnitPrice(copy);
        }

        /// <summary>What a number of an item sells for; zero if it can't be shipped.</summary>
        public static long Value(Item item, long count)
        {
            return (UnitPrice(item) ?? 0) * Math.Max(0, count);
        }

        /// <summary>What a stock ID sells for at a quality, or <c>null</c> if it can't be shipped.</summary>
        public static int? UnitPrice(string stockId, int quality)
        {
            Item sample = StockId.Create(stockId);
            return sample == null ? null : UnitPrice(sample, Math.Max(0, quality));
        }

        /// <summary>Converts in-game minutes to days, for gold a day.</summary>
        /// <remarks>
        /// Something taking a day or more is ready on a morning, so it counts whole days: a keg's 10,000 minutes is
        /// seven days, and 3,150g wine earns 450g a day. Anything shorter keeps its fraction.
        /// </remarks>
        public static double Days(int minutes)
        {
            if (minutes <= 0)
                return 0;
            return minutes >= CraftPlan.MinutesPerDay ? Math.Ceiling(minutes / (double)CraftPlan.MinutesPerDay) : minutes / (double)CraftPlan.MinutesPerDay;
        }

        /// <summary>Formats gold for display: "3,150g".</summary>
        public static string Gold(double amount) => NumberFormat.Full((long)Math.Round(amount)) + "g";
    }
}
