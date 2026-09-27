using System;
using System.Collections.Generic;
using System.Linq;
using StardewLogistics.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;

namespace StardewLogistics.Devices
{
    /// <summary>One kind of item shipped on a day.</summary>
    internal class LedgerItem
    {
        public string ItemId { get; set; }
        public string Name { get; set; }
        public int Quality { get; set; }
        public int Count { get; set; }
        public long Gold { get; set; }
    }

    /// <summary>What one day earned: shipped goods, and everything else.</summary>
    internal class LedgerDay
    {
        /// <summary>The day, as days since the farm began.</summary>
        public int TotalDays { get; set; }
        public int Season { get; set; }
        public int DayOfMonth { get; set; }
        public int Year { get; set; }

        /// <summary>What the shipping bins paid.</summary>
        public long Shipping { get; set; }

        /// <summary>Everything else earned that day: quests, mail, selling in shops, the lot.</summary>
        public long Other { get; set; }

        public List<LedgerItem> Items { get; set; } = new();

        public long Total => this.Shipping + this.Other;
    }

    /// <summary>The ledger as saved.</summary>
    internal class LedgerData
    {
        public List<LedgerDay> Days { get; set; } = new();

        /// <summary>The player's lifetime earnings when the last day was closed.</summary>
        public long EarnedBaseline { get; set; } = -1;
    }

    /// <summary>Keeps a history of what the farm earned each day, shipping itemised.</summary>
    /// <remarks>
    /// At the end of each day the shipping bins are noted, item by item, at the price they'll fetch. Overnight the
    /// game pays for them; when it saves, the day is closed, with the rest of the day's earnings found from the
    /// rise in the player's lifetime earnings -- which counts every gold the player receives, wherever from. Kept in
    /// the save, on the host.
    /// </remarks>
    internal class ShippingLedger
    {
        /*********
        ** Fields
        *********/
        private const string SaveKey = "shipping-ledger";
        private const int MaxDays = 1000;

        private readonly IDataHelper Data;
        private LedgerData State = new();

        /// <summary>The day being closed, noted at its end and finished when the game saves.</summary>
        private LedgerDay Pending;


        /*********
        ** Accessors
        *********/
        /// <summary>The recorded days, oldest first.</summary>
        /// <remarks>On a farmhand, the host's ledger as last heard: only the host keeps it.</remarks>
        public IReadOnlyList<LedgerDay> Days => Multiplayer.MultiplayerSync.IsRemote
            ? Multiplayer.MultiplayerSync.Instance?.RemoteLedger?.Days ?? new List<LedgerDay>()
            : this.State.Days;


        /*********
        ** Public methods
        *********/
        public ShippingLedger(IDataHelper data)
        {
            this.Data = data;
        }

        /// <summary>Loads the save's ledger.</summary>
        public void Load()
        {
            this.Pending = null;
            try
            {
                this.State = (Context.IsMainPlayer ? this.Data.ReadSaveData<LedgerData>(SaveKey) : null) ?? new LedgerData();
            }
            catch (Exception ex)
            {
                Log.Debug($"The shipping ledger couldn't be read, so it starts fresh: {ex.Message}");
                this.State = new LedgerData();
            }

            if (this.State.EarnedBaseline < 0)
                this.State.EarnedBaseline = Earned();
        }

        /// <summary>Forgets the ledger, for when the player leaves the save.</summary>
        public void Reset()
        {
            this.State = new LedgerData();
            this.Pending = null;
        }

        /// <summary>Notes what's in the shipping bins at the end of the day, before the game sells it.</summary>
        public void BeforeNight()
        {
            LedgerDay day = new()
            {
                TotalDays = Game1.Date.TotalDays,
                Season = (int)Game1.season,
                DayOfMonth = Game1.dayOfMonth,
                Year = Game1.year
            };

            foreach (Item item in GetBinContents())
            {
                long gold = Selling.Value(item, item.Stack);
                LedgerItem line = day.Items.FirstOrDefault(existing => existing.ItemId == StockId.Of(item) && existing.Quality == item.Quality);
                if (line == null)
                    day.Items.Add(line = new LedgerItem { ItemId = StockId.Of(item), Name = item.DisplayName, Quality = item.Quality });

                line.Count += item.Stack;
                line.Gold += gold;
                day.Shipping += gold;
            }

            day.Items = day.Items.OrderByDescending(line => line.Gold).ToList();
            this.Pending = day;
        }

        /// <summary>Closes the day just ended, once the game has paid for its shipping.</summary>
        /// <remarks>Called as the game saves, so the day goes into the same save; and again each morning in case it didn't.</remarks>
        public void CloseDay()
        {
            if (this.Pending == null)
                return;

            long earned = Earned();
            long delta = Math.Max(0, earned - Math.Max(0, this.State.EarnedBaseline));
            this.Pending.Other = Math.Max(0, delta - this.Pending.Shipping);
            this.State.EarnedBaseline = earned;

            this.State.Days.RemoveAll(day => day.TotalDays == this.Pending.TotalDays);
            this.State.Days.Add(this.Pending);
            if (this.State.Days.Count > MaxDays)
                this.State.Days.RemoveRange(0, this.State.Days.Count - MaxDays);

            Log.Trace($"Ledger: day {this.Pending.TotalDays} earned {this.Pending.Shipping}g shipping and {this.Pending.Other}g otherwise.");
            this.Pending = null;

            if (Context.IsMainPlayer)
            {
                try
                {
                    this.Data.WriteSaveData(SaveKey, this.State);
                    Multiplayer.MultiplayerSync.Instance?.SendLedger();
                }
                catch (Exception ex)
                {
                    Log.Debug($"The shipping ledger couldn't be saved: {ex.Message}");
                }
            }
        }

        /// <summary>A day's date for display: "Spring 12, Year 1".</summary>
        public static string FormatDate(LedgerDay day)
        {
            string season = Utility.getSeasonNameFromNumber(day.Season);
            return $"{season} {day.DayOfMonth}, Y{day.Year}";
        }


        /*********
        ** Private methods
        *********/
        /// <summary>The player's lifetime earnings: their own with separate wallets, the farm's otherwise.</summary>
        private static long Earned()
        {
            if (Game1.player == null)
                return 0;

            return Game1.player.team.useSeparateWallets.Value
                ? Game1.player.stats.IndividualMoneyEarned
                : Game1.player.totalMoneyEarned;
        }

        /// <summary>Everything waiting to be sold tonight: the farm's bin, and every Mini-Shipping Bin.</summary>
        private static List<Item> GetBinContents()
        {
            List<Item> items = new();
            if (Game1.getFarm() is Farm farm)
                items.AddRange(farm.getShippingBin(Game1.player).Where(item => item != null));

            Utility.ForEachLocation(location =>
            {
                foreach (StardewValley.Object obj in location.Objects.Values)
                {
                    if (obj is Chest { SpecialChestType: Chest.SpecialChestTypes.MiniShippingBin } bin)
                        items.AddRange(bin.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Where(item => item != null));
                }
                return true;
            }, includeInteriors: true, includeGenerated: false);

            return items;
        }
    }
}
