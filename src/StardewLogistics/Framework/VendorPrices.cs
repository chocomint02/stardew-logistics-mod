using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.GameData.Shops;
using StardewValley.Internal;

namespace StardewLogistics.Framework
{
    /// <summary>What shops charge for an item, for filling in what an input costs.</summary>
    /// <remarks>
    /// Read from the shops' own data and priced by the game's own <c>ShopBuilder</c>, with the shop's and item's
    /// price modifiers applied -- so JojaMart comes out dearer than Pierre, as in the shops. A shop's conditions
    /// (the season, the weekday) are ignored: a seed sold in spring is still worth knowing the price of in winter.
    /// Only shops selling for gold count; trades and other currencies don't.
    /// </remarks>
    internal static class VendorPrices
    {
        /*********
        ** Fields
        *********/
        /// <summary>Friendly names for shops whose ID or owner doesn't say who they are.</summary>
        private static readonly Dictionary<string, string> ShopNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["SeedShop"] = "Pierre",
            ["Joja"] = "JojaMart",
            ["Sandy"] = "Oasis",
            ["Traveler"] = "Traveling Cart",
            ["AnimalShop"] = "Marnie",
            ["Blacksmith"] = "Clint",
            ["FishShop"] = "Willy",
            ["Saloon"] = "Gus",
            ["Carpenter"] = "Robin",
            ["Hospital"] = "Harvey",
            ["IceCreamStand"] = "Ice Cream Stand",
            ["AdventureShop"] = "Marlon",
            ["ShadowShop"] = "Krobus",
            ["VolcanoShop"] = "Volcano Dwarf",
            ["IslandNorthTrader"] = "Island Trader",
            ["ResortBar"] = "Resort Bar",
            ["Raccoon"] = "Raccoon"
        };


        /*********
        ** Public methods
        *********/
        /// <summary>Every shop selling an item for gold, cheapest first.</summary>
        public static List<(string Vendor, int Price)> For(Item item)
        {
            List<(string Vendor, int Price)> prices = new();
            if (item == null)
                return prices;

            Dictionary<string, ShopData> shops;
            try
            {
                shops = DataLoader.Shops(Game1.content);
            }
            catch
            {
                return prices;
            }

            string wanted = item.QualifiedItemId;
            foreach ((string shopId, ShopData shop) in shops)
            {
                if (shop?.Items == null || shop.Currency != 0)
                    continue;

                foreach (ShopItemData entry in shop.Items)
                {
                    if (entry == null || !string.IsNullOrEmpty(entry.TradeItemId) || !Matches(entry.ItemId, wanted))
                        continue;

                    int? price = PriceOf(item, shop, entry);
                    if (price is not > 0)
                        continue;

                    string vendor = VendorName(shopId, shop);
                    if (!prices.Any(existing => existing.Vendor == vendor && existing.Price == price.Value))
                        prices.Add((vendor, price.Value));
                    break;
                }
            }

            return prices.OrderBy(entry => entry.Price).ThenBy(entry => entry.Vendor).ToList();
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Whether a shop entry sells exactly this item, rather than something from a query.</summary>
        private static bool Matches(string entryId, string wanted)
        {
            if (string.IsNullOrWhiteSpace(entryId) || entryId.Contains(' '))
                return false;

            try
            {
                return string.Equals(ItemRegistry.QualifyItemId(entryId), wanted, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>What a shop charges for an item, with its price modifiers.</summary>
        private static int? PriceOf(Item item, ShopData shop, ShopItemData entry)
        {
            try
            {
                Item sample = item.getOne();
                float price = ShopBuilder.GetBasePrice(new ItemQueryResult(sample), shop, entry, sample, outOfSeasonPrice: false, entry.UseObjectDataPrice);

                if (!entry.IgnoreShopPriceModifiers && shop.PriceModifiers?.Count > 0)
                    price = Utility.ApplyQuantityModifiers(price, shop.PriceModifiers, shop.PriceModifierMode, targetItem: sample);
                if (entry.PriceModifiers?.Count > 0)
                    price = Utility.ApplyQuantityModifiers(price, entry.PriceModifiers, entry.PriceModifierMode, targetItem: sample);

                return (int)Math.Round(price);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Who runs a shop, for display.</summary>
        private static string VendorName(string shopId, ShopData shop)
        {
            if (ShopNames.TryGetValue(shopId, out string name))
                return name;

            string owner = shop.Owners?.Select(entry => entry?.Name).FirstOrDefault(entry => !string.IsNullOrEmpty(entry) && entry is not ("Any" or "AnyOrNone" or "None"));
            if (owner != null)
                return Game1.getCharacterFromName(owner)?.displayName ?? owner;

            return shopId;
        }
    }
}
