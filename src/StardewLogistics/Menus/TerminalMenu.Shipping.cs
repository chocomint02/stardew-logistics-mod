using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Devices;
using StardewLogistics.Framework;
using StardewValley;
using StardewValley.Menus;

namespace StardewLogistics.Menus
{
    /// <summary>The terminal's Shipping tab: sell from storage through a connected shipping bin, and see what the network earns.</summary>
    internal partial class TerminalMenu
    {
        /*********
        ** Fields
        *********/
        /// <summary>Stored items the shipping bin would take, after the search and filters.</summary>
        private List<NetworkItemStack> SellStock = new();

        /// <summary>Whether the grid shows what's waiting in the shipping bin, rather than what's in storage.</summary>
        private bool ShowingBin;

        /// <summary>The shipping summary as last worked out, and when; it walks every machine on the network.</summary>
        private ShippingSummary SummaryCache;
        private double SummaryAge = -1;


        /*********
        ** Nested types
        *********/
        /// <summary>Totals for the Shipping tab.</summary>
        private class ShippingSummary
        {
            public long NetWorth { get; init; }
            public bool HasBin { get; init; }
            public List<Item> BinItems { get; init; } = new();
            public long BinValue { get; init; }
            public List<IncomeSource> Income { get; init; } = new();
            public double DailyIncome { get; init; }
        }


        /*********
        ** Private methods: data
        *********/
        /// <summary>Narrows the filtered stock to what can be shipped.</summary>
        private void ApplyShippingFilter()
        {
            this.SellStock = this.VisibleStock.Where(entry => Selling.CanSell(entry.Sample)).ToList();
            this.ClampScroll(TerminalTab.Shipping, this.GetMaxShippingScroll());
        }

        /// <summary>Net worth, the bin, and the income forecast, refreshed at most once a second.</summary>
        private ShippingSummary GetShippingSummary()
        {
            double now = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
            if (this.SummaryCache != null && now - this.SummaryAge < 1)
                return this.SummaryCache;

            ShippingSummary summary = new();
            if (this.Network != null)
            {
                List<Item> bin = ShippingService.GetContents(this.Network);
                List<IncomeSource> income = IncomeForecast.Build(this.Network, this.Jobs);
                summary = new ShippingSummary
                {
                    NetWorth = this.AllStock.Sum(entry => Selling.Value(entry.Sample, entry.Count)),
                    HasBin = ShippingService.GetBins(this.Network).Count > 0,
                    BinItems = bin,
                    BinValue = bin.Sum(item => Selling.Value(item, item.Stack)),
                    Income = income,
                    DailyIncome = IncomeForecast.DailyIncome(income)
                };
            }

            this.SummaryAge = now;
            return this.SummaryCache = summary;
        }

        /// <summary>How many grid items the Shipping tab is showing.</summary>
        private int ShippingItemCount => this.ShowingBin ? this.GetShippingSummary().BinItems.Count : this.SellStock.Count;

        /// <summary>The largest scroll offset for the Shipping grid.</summary>
        private int GetMaxShippingScroll()
        {
            int rows = (int)Math.Ceiling(this.ShippingItemCount / (double)Columns);
            return Math.Max(0, rows - this.Rows);
        }

        /// <summary>The grid index under a screen position, or -1.</summary>
        private int GetShippingIndexAt(int x, int y)
        {
            Rectangle grid = this.GetGridBounds();
            if (!grid.Contains(x, y))
                return -1;

            int index = ((this.ScrollOffset + ((y - grid.Y) / SlotSize)) * Columns) + ((x - grid.X) / SlotSize);
            return index >= 0 && index < this.ShippingItemCount ? index : -1;
        }

        /// <summary>The switch between storage and the bin, on its own row below the grid.</summary>
        private (Rectangle Storage, Rectangle Bin) GetBinViewButtons()
        {
            Rectangle grid = this.GetGridBounds();
            int storageWidth = (int)Game1.smallFont.MeasureString(this.Translations.Get("shipping.view-storage")).X + 40;
            int binWidth = (int)Game1.smallFont.MeasureString(this.Translations.Get("shipping.view-bin")).X + 40;
            Rectangle storage = new(grid.X, grid.Bottom + 4, storageWidth, 40);
            return (storage, new Rectangle(storage.Right + 6, storage.Y, binWidth, 40));
        }


        /*********
        ** Private methods: input
        *********/
        /// <summary>Handles a click on the Shipping tab.</summary>
        private void ReceiveClickOnShipping(int x, int y)
        {
            if (this.HandleSharedHeaderClick(x, y))
                return;

            (Rectangle storageButton, Rectangle binButton) = this.GetBinViewButtons();
            if (storageButton.Contains(x, y) || binButton.Contains(x, y))
            {
                this.ShowingBin = binButton.Contains(x, y);
                this.ScrollOffset = 0;
                this.SummaryCache = null;
                Game1.playSound("smallSelect");
                return;
            }

            int index = this.GetShippingIndexAt(x, y);
            if (index < 0 || this.Network == null)
                return;

            // In the bin: take it back.
            if (this.ShowingBin)
            {
                Item item = this.GetShippingSummary().BinItems[index];
                string name = item.DisplayName;

                // A farmhand's bin is emptied by the host.
                if (Multiplayer.MultiplayerSync.IsRemote)
                {
                    ItemKey key = ItemKey.From(item);
                    Multiplayer.MultiplayerSync.Instance?.Send(new Multiplayer.ShipRequest { Network = this.NetworkReference, ItemId = key.QualifiedId, Quality = key.Quality, Variant = key.Variant }, Multiplayer.MessageTypes.Return);
                    Game1.playSound("coin");
                    return;
                }

                int moved = ShippingService.Return(this.Network, item);
                this.SummaryCache = null;
                if (moved > 0)
                {
                    Game1.playSound("coin");
                    Game1.addHUDMessage(new HUDMessage(this.Translations.Get("shipping.returned", new { count = moved, name }), HUDMessage.newQuest_type));
                    this.RefreshStock();
                }
                else
                {
                    Game1.playSound("cancel");
                    this.ShowError(this.Translations.Get("shipping.storage-full"));
                }
                return;
            }

            // In storage: choose how many to ship.
            NetworkItemStack entry = this.SellStock[index];
            this.ReleaseKeyboard();
            TerminalMenu terminal = this;
            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new SellMenu(this.Network, entry, this.Translations, this.NetworkReference, () =>
            {
                terminal.SummaryCache = null;
                terminal.RefreshStock();
                Game1.activeClickableMenu = terminal;
            });
        }

        /// <summary>Sets the hover text for the Shipping tab.</summary>
        private void PerformHoverOnShipping(int x, int y)
        {
            (Rectangle storageButton, Rectangle binButton) = this.GetBinViewButtons();
            if (storageButton.Contains(x, y) || binButton.Contains(x, y))
            {
                this.HoverText = this.Translations.Get("shipping.view-hint");
                return;
            }
            if (this.TypeFilterButton.containsPoint(x, y) || this.ModFilterButton.containsPoint(x, y))
            {
                this.HoverText = this.Translations.Get("ui.filter-hint");
                return;
            }

            int index = this.GetShippingIndexAt(x, y);
            if (index < 0)
                return;

            if (this.ShowingBin)
            {
                Item item = this.GetShippingSummary().BinItems[index];
                this.HoverItem = item;
                this.HoverText = this.Translations.Get("shipping.hover-bin", new { count = NumberFormat.Full(item.Stack), total = Selling.Gold(Selling.Value(item, item.Stack)) });
                return;
            }

            NetworkItemStack entry = this.SellStock[index];
            this.HoverItem = entry.Sample;
            this.HoverText = this.Translations.Get("shipping.hover", new
            {
                price = Selling.Gold(Selling.UnitPrice(entry.Sample) ?? 0),
                count = NumberFormat.Full(entry.Count),
                total = Selling.Gold(Selling.Value(entry.Sample, entry.Count))
            });
        }


        /*********
        ** Private methods: drawing
        *********/
        /// <summary>Draws the Shipping tab: the grid, then net worth, income, and the bin.</summary>
        private void DrawShippingTab(SpriteBatch b)
        {
            Rectangle grid = this.GetGridBounds();
            if (this.Network == null)
            {
                this.DrawCentredMessage(b, grid, this.NotConnectedText);
                return;
            }

            ShippingSummary summary = this.GetShippingSummary();
            int first = this.ScrollOffset * Columns;
            int count = this.ShippingItemCount;

            for (int row = 0; row < this.Rows; row++)
            {
                for (int column = 0; column < Columns; column++)
                {
                    int x = grid.X + (column * SlotSize);
                    int y = grid.Y + (row * SlotSize);
                    b.Draw(Game1.menuTexture, new Vector2(x, y), Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10), Color.White);

                    int index = first + (row * Columns) + column;
                    if (index >= count)
                        continue;

                    if (this.ShowingBin)
                    {
                        Item item = summary.BinItems[index];
                        item.drawInMenu(b, new Vector2(x, y), this.GridScale(index), 1f, 0.9f, StackDrawType.HideButShowQuality, Color.White, drawShadow: true);
                        DrawSlotCount(b, NumberFormat.Abbreviate(item.Stack), x, y);
                    }
                    else
                    {
                        NetworkItemStack entry = this.SellStock[index];
                        (entry.Icon ?? entry.Sample).drawInMenu(b, new Vector2(x, y), this.GridScale(index), 1f, 0.9f, StackDrawType.HideButShowQuality, Color.White, drawShadow: true);
                        DrawSlotCount(b, NumberFormat.Abbreviate(entry.Count), x, y);
                    }
                }
            }

            if (count == 0)
                this.DrawCentredMessage(b, grid, this.Translations.Get(this.ShowingBin ? "shipping.bin-empty" : "shipping.none"));

            this.DrawScrollbar(b, grid, this.Rows, (int)Math.Ceiling(count / (double)Columns));

            // Storage or the bin, on a row of its own.
            (Rectangle storageButton, Rectangle binButton) = this.GetBinViewButtons();
            DrawPlainButton(b, storageButton, this.Translations.Get("shipping.view-storage"), !this.ShowingBin);
            DrawPlainButton(b, binButton, this.Translations.Get("shipping.view-bin"), this.ShowingBin);

            // The bin: what it holds, or that there isn't one.
            string bin = summary.HasBin
                ? this.Translations.Get("shipping.bin", new { count = NumberFormat.Full(summary.BinItems.Sum(item => (long)item.Stack)), gold = Selling.Gold(summary.BinValue) })
                : this.Translations.Get("shipping.no-bin");
            Marquee.DrawWrapped(b, bin, Game1.smallFont, new Vector2(grid.X, grid.Bottom + 52), grid.Width, summary.HasBin ? Game1.textColor * 0.8f : UiTheme.Bad, maxLines: 1);

            Marquee.DrawWrapped(b, this.Translations.Get(this.ShowingBin ? "shipping.hint-bin" : "shipping.hint-storage"), Game1.smallFont, new Vector2(grid.X, grid.Bottom + 84), grid.Width, Game1.textColor * 0.6f);
        }
    }
}
