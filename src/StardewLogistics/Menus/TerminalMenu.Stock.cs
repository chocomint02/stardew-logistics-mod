using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Devices;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewValley;
using SObject = StardewValley.Object;

namespace StardewLogistics.Menus
{
    /// <summary>The terminal's Stock tab: the network's minimum-stock rules, and how each is doing.</summary>
    internal partial class TerminalMenu
    {
        /*********
        ** Fields
        *********/
        private const int StockRowHeight = 96;

        /// <summary>The rows as last worked out, and when; counting storage for every rule every frame would be wasteful.</summary>
        private List<StockRow> StockRowsCache;
        private double StockRowsAge = -1;


        /*********
        ** Nested types
        *********/
        /// <summary>One minimum-stock rule and where it stands.</summary>
        private class StockRow
        {
            public StockRule Rule { get; init; }
            public StockRuleStatus Status { get; init; }
            public Item Icon { get; init; }
            public string Name { get; init; }
        }


        /*********
        ** Private methods
        *********/
        /// <summary>The network's rules, with where each stands.</summary>
        private List<StockRow> GetStockRows()
        {
            double now = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
            if (this.StockRowsCache != null && now - this.StockRowsAge < 1)
                return this.StockRowsCache;

            List<StockRow> rows = new();
            StockKeeper keeper = this.Jobs?.Stock;
            if (keeper != null && this.Network != null)
            {
                foreach ((StockRule rule, NetworkNode _) in keeper.GetRules(this.Network))
                {
                    Item icon = this.GetJobIcon(rule.ItemId, rule.Quality);
                    rows.Add(new StockRow
                    {
                        Rule = rule,
                        Status = keeper.GetStatus(rule, this.Network),
                        Icon = icon,
                        Name = icon?.DisplayName ?? StockId.GetDisplayName(rule.ItemId)
                    });
                }
            }

            this.StockRowsAge = now;
            return this.StockRowsCache = rows.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Rule.Quality).ToList();
        }

        /// <summary>Draws the list of rules.</summary>
        private void DrawStockTab(SpriteBatch b)
        {
            Rectangle grid = this.GetGridBounds();
            if (this.Network == null)
            {
                this.DrawCentredMessage(b, grid, this.NotConnectedText);
                return;
            }

            List<StockRow> rows = this.GetStockRows();
            if (rows.Count == 0)
            {
                this.DrawCentredMessage(b, grid, this.Translations.Get("stock.none"));
                return;
            }

            int visible = grid.Height / StockRowHeight;
            for (int i = 0; i < visible; i++)
            {
                int index = this.ScrollOffset + i;
                if (index >= rows.Count)
                    break;

                StockRow row = rows[index];
                int y = grid.Y + (i * StockRowHeight);
                drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), grid.X, y, grid.Width, StockRowHeight - 8, Color.White * 0.9f, 1f, drawShadow: false);

                ItemIcon.Draw(b, row.Icon, new Rectangle(grid.X + 14, y + 20, 48, 48));

                int textX = grid.X + 72;
                int textWidth = GetStockMinusBounds(grid, y).X - 16 - textX;
                Marquee.Draw(b, this.Translations.Get("stock.title", new { count = row.Rule.Target, name = row.Name }), Game1.smallFont, new Vector2(textX, y + 14), textWidth, Game1.textColor);

                (string status, Color colour) = this.DescribeStockRow(row);
                Marquee.Draw(b, status, Game1.smallFont, new Vector2(textX, y + 48), textWidth, colour);

                // Target: minus, the number, plus. Then Remove.
                Rectangle minus = GetStockMinusBounds(grid, y);
                Rectangle plus = GetStockPlusBounds(grid, y);
                foreach ((Rectangle bounds, string label) in new[] { (minus, "-"), (plus, "+") })
                {
                    drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White, 2f, drawShadow: false);
                    this.Fx.Control(b, bounds);
                    Vector2 size = Game1.smallFont.MeasureString(label);
                    Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), Game1.textColor);
                }

                string target = NumberFormat.Full(row.Rule.Target);
                Vector2 targetSize = Game1.smallFont.MeasureString(target);
                int middle = (minus.Right + plus.X) / 2;
                Utility.drawTextWithShadow(b, target, Game1.smallFont, new Vector2(middle - (targetSize.X / 2), minus.Center.Y - (targetSize.Y / 2)), Game1.textColor);

                Rectangle remove = GetStockRemoveBounds(grid, y);
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), remove.X, remove.Y, remove.Width, remove.Height, Color.White, 2f, drawShadow: false);
                this.Fx.Control(b, remove);
                string removeLabel = this.Translations.Get("stock.remove");
                Vector2 removeSize = Game1.smallFont.MeasureString(removeLabel);
                Utility.drawTextWithShadow(b, removeLabel, Game1.smallFont, new Vector2(remove.Center.X - (removeSize.X / 2), remove.Center.Y - (removeSize.Y / 2)), Game1.textColor);
            }

            this.DrawScrollbar(b, grid, visible, rows.Count);
        }

        /// <summary>A rule's status line: stocked, making more, or why it can't.</summary>
        private (string Text, Color Colour) DescribeStockRow(StockRow row)
        {
            StockRuleStatus status = row.Status;
            string have = NumberFormat.Full(status.Have);

            if (status.Coming > 0)
                return (this.Translations.Get("stock.making", new { have, count = status.Coming }), Game1.textColor * 0.65f);
            if (status.Have >= row.Rule.Target)
                return (this.Translations.Get("stock.stocked", new { have }), UiTheme.Good);
            if (status.Error != null)
                return (this.Translations.Get("stock.error", new { have, reason = status.Error }), UiTheme.Bad);
            return (this.Translations.Get("stock.checking", new { have }), Game1.textColor * 0.65f);
        }

        /// <summary>Handles a click on the Stock tab: change a target, remove a rule, or open one to edit.</summary>
        private void ReceiveClickOnStock(int x, int y)
        {
            Rectangle grid = this.GetGridBounds();
            List<StockRow> rows = this.GetStockRows();
            int visible = grid.Height / StockRowHeight;

            for (int i = 0; i < visible; i++)
            {
                int index = this.ScrollOffset + i;
                if (index >= rows.Count)
                    break;

                int rowY = grid.Y + (i * StockRowHeight);
                if (!new Rectangle(grid.X, rowY, grid.Width, StockRowHeight - 8).Contains(x, y))
                    continue;

                StockRow row = rows[index];
                int step = IsShiftDown() ? 10 : 1;

                if (GetStockMinusBounds(grid, rowY).Contains(x, y) || GetStockPlusBounds(grid, rowY).Contains(x, y))
                {
                    StockRule changed = row.Rule.Clone();
                    changed.Target = Math.Clamp(changed.Target + (GetStockPlusBounds(grid, rowY).Contains(x, y) ? step : -step), 1, 9999);
                    if (changed.Target != row.Rule.Target)
                        this.SaveStockRule(changed, null);
                    Game1.playSound("drumkit6");
                    return;
                }

                if (GetStockRemoveBounds(grid, rowY).Contains(x, y))
                {
                    this.Jobs.Stock?.RemoveRule(this.Network, row.Rule.Key);
                    this.StockRowsCache = null;
                    Game1.playSound("trashcan");
                    return;
                }

                this.OpenRuleEditor(row);
                return;
            }
        }

        /// <summary>The hover text for the Stock tab.</summary>
        private string GetStockHover(int x, int y)
        {
            Rectangle grid = this.GetGridBounds();
            int visible = grid.Height / StockRowHeight;
            int count = this.GetStockRows().Count;

            for (int i = 0; i < visible && this.ScrollOffset + i < count; i++)
            {
                int rowY = grid.Y + (i * StockRowHeight);
                if (GetStockMinusBounds(grid, rowY).Contains(x, y) || GetStockPlusBounds(grid, rowY).Contains(x, y))
                    return this.Translations.Get("stock.step-hint");
                if (new Rectangle(grid.X, rowY, GetStockMinusBounds(grid, rowY).X - grid.X, StockRowHeight - 8).Contains(x, y))
                    return this.Translations.Get("stock.edit-hint");
            }

            return "";
        }

        /// <summary>Opens the planner to change a rule.</summary>
        private void OpenRuleEditor(StockRow row)
        {
            this.ReleaseKeyboard();
            TerminalMenu parent = this;

            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new AutoCraftMenu(
                row.Rule.ItemId,
                row.Name,
                this.Network,
                this.Recipes,
                this.MachineRecipes,
                this.Jobs,
                this.Config,
                this.Translations,
                onClose: () => Game1.activeClickableMenu = parent,
                rule: row.Rule,
                onKeepStocked: this.SaveStockRule
            );
        }

        /// <summary>Saves a rule on this network, keeping it on this terminal if no other has it.</summary>
        private void SaveStockRule(StockRule rule, string replacing)
        {
            SObject terminal = this.TerminalLocation.Objects.TryGetValue(this.TerminalTile, out SObject found) ? found : null;
            this.Jobs.Stock?.SetRule(this.Network, terminal, rule, replacing);
            this.StockRowsCache = null;
        }

        /// <summary>A rule row's minus button.</summary>
        private static Rectangle GetStockMinusBounds(Rectangle grid, int rowY) => new(grid.Right - 352, rowY + 22, 44, 44);

        /// <summary>A rule row's plus button.</summary>
        private static Rectangle GetStockPlusBounds(Rectangle grid, int rowY) => new(grid.Right - 210, rowY + 22, 44, 44);

        /// <summary>A rule row's Remove button.</summary>
        private static Rectangle GetStockRemoveBounds(Rectangle grid, int rowY) => new(grid.Right - 150, rowY + 22, 130, 44);
    }
}
