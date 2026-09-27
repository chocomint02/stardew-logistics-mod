using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Framework;
using StardewValley;

namespace StardewLogistics.Menus
{
    /// <summary>The terminal's Farm tab: the auto-harvesters on this network, and a live look at each field.</summary>
    internal partial class TerminalMenu
    {
        /*********
        ** Fields
        *********/
        private const int FarmRowHeight = 96;

        /// <summary>The rows as last worked out, and when; walking every field every frame would be wasteful.</summary>
        private List<FarmRow> FarmRowsCache;
        private double FarmRowsAge = -1;

        /// <summary>The harvester icon for the rows.</summary>
        private Item HarvesterIcon;


        /*********
        ** Nested types
        *********/
        /// <summary>One auto-harvester and what's growing under it.</summary>
        private class FarmRow
        {
            public GameLocation Location { get; init; }
            public Vector2 Tile { get; init; }
            public int Growing { get; init; }
            public int? Soonest { get; init; }
            public int Reserved { get; init; }
            public int AutomationTiles { get; init; }
            public int AutomationFree { get; init; }
        }


        /*********
        ** Private methods
        *********/
        /// <summary>The harvesters on this network, with a summary of each field.</summary>
        private List<FarmRow> GetFarmRows()
        {
            double now = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
            if (this.FarmRowsCache != null && now - this.FarmRowsAge < 1)
                return this.FarmRowsCache;

            List<FarmRow> rows = new();
            if (this.Network != null && this.Jobs?.HarvestersOn != null)
            {
                List<IncomingCrop> crops = this.Jobs.Forecast?.Invoke(this.Network) ?? new List<IncomingCrop>();
                List<FreeTile> free = this.Jobs.GetFreeTiles(this.Network);

                foreach ((GameLocation location, Vector2 tile) in this.Jobs.HarvestersOn(this.Network))
                {
                    List<IncomingCrop> mine = crops.Where(crop => crop.Location == location && crop.HarvesterTile == tile).ToList();
                    int automation = location.Objects.TryGetValue(tile, out StardewValley.Object machine)
                        ? HarvesterSettings.ReadCached(machine).Tiles.Values.Count(plan => plan.Automation)
                        : 0;

                    rows.Add(new FarmRow
                    {
                        Location = location,
                        Tile = tile,
                        Growing = mine.Count,
                        Soonest = mine.Count > 0 ? mine.Min(crop => crop.Days) : null,
                        Reserved = mine.Count(crop => this.Jobs.GetReservation(crop.Location, crop.Tile) != null),
                        AutomationTiles = automation,
                        AutomationFree = free.Count(entry => entry.Location == location && entry.HarvesterTile == tile)
                    });
                }
            }

            this.FarmRowsAge = now;
            return this.FarmRowsCache = rows;
        }

        /// <summary>Draws the list of harvesters.</summary>
        private void DrawFarmTab(SpriteBatch b)
        {
            Rectangle grid = this.GetGridBounds();
            List<FarmRow> rows = this.GetFarmRows();

            if (rows.Count == 0)
            {
                this.DrawCentredMessage(b, grid, this.Translations.Get("farm.none"));
                return;
            }

            this.HarvesterIcon ??= ItemRegistry.Create(ModIds.Qualify(ModIds.AutoHarvester), allowNull: true);
            int visible = grid.Height / FarmRowHeight;

            for (int i = 0; i < visible; i++)
            {
                int index = this.ScrollOffset + i;
                if (index >= rows.Count)
                    break;

                FarmRow row = rows[index];
                int y = grid.Y + (i * FarmRowHeight);
                drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), grid.X, y, grid.Width, FarmRowHeight - 8, Color.White * 0.9f, 1f, drawShadow: false);

                ItemIcon.Draw(b, this.HarvesterIcon, new Rectangle(grid.X + 14, y + 20, 48, 48), 1f, showQuality: false);

                Rectangle view = GetFarmViewBounds(grid, y);
                int textX = grid.X + 72;
                int textWidth = view.X - 16 - textX;

                string where = row.Location.GetDisplayName() ?? row.Location.Name;
                Marquee.Draw(b, this.Translations.Get("farm.title", new { location = where, x = (int)row.Tile.X, y = (int)row.Tile.Y }), Game1.smallFont, new Vector2(textX, y + 14), textWidth, Game1.textColor);

                string summary = row.Growing == 0
                    ? this.Translations.Get("farm.empty")
                    : row.Soonest == 0
                        ? this.Translations.Get("farm.summary-ready", new { count = row.Growing, reserved = row.Reserved })
                        : this.Translations.Get("farm.summary", new { count = row.Growing, days = row.Soonest, reserved = row.Reserved });
                if (row.AutomationTiles > 0)
                    summary += "  ·  " + this.Translations.Get("farm.automation", new { free = row.AutomationFree, total = row.AutomationTiles });
                Marquee.Draw(b, summary, Game1.smallFont, new Vector2(textX, y + 48), textWidth, Game1.textColor * 0.65f);

                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), view.X, view.Y, view.Width, view.Height, Color.White, 2f, drawShadow: false);
                this.Fx.Control(b, view);
                string label = this.Translations.Get("farm.view");
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(view.Center.X - (size.X / 2), view.Center.Y - (size.Y / 2)), Game1.textColor);
            }

            this.DrawScrollbar(b, grid, visible, rows.Count);
        }

        /// <summary>Handles a click on the Farm tab: View opens that harvester's field.</summary>
        private void ReceiveClickOnFarm(int x, int y)
        {
            Rectangle grid = this.GetGridBounds();
            List<FarmRow> rows = this.GetFarmRows();
            int visible = grid.Height / FarmRowHeight;

            for (int i = 0; i < visible; i++)
            {
                int index = this.ScrollOffset + i;
                if (index >= rows.Count)
                    break;

                if (!GetFarmViewBounds(grid, grid.Y + (i * FarmRowHeight)).Contains(x, y))
                    continue;

                FarmRow row = rows[index];
                this.ReleaseKeyboard();
                HarvesterPlanMenu.OpenFieldView(this.Networks, this.Translations, row.Location, row.Tile, this.Jobs.GetReservation);
                return;
            }
        }

        /// <summary>The bounds of a Farm row's View button.</summary>
        private static Rectangle GetFarmViewBounds(Rectangle grid, int rowY) => new(grid.Right - 150, rowY + 22, 130, 44);
    }
}
