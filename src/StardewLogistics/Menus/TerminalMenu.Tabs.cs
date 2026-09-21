using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewValley;
using StardewValley.Menus;

namespace StardewLogistics.Menus
{
    /// <summary>The terminal's drawing code and its configuration tabs.</summary>
    internal partial class TerminalMenu
    {
        /*********
        ** Fields
        *********/
        private const int RowHeight = 84;
        private const int FilterSlotSize = 32;

        private readonly List<ConfigRow> ConfigRows = new();
        private ConfigRow PendingFilterRow;
        private int PendingFilterSlot = -1;


        /*********
        ** Nested types
        *********/
        /// <summary>One configurable device on the Storage tab: a chest or a bus.</summary>
        private class ConfigRow
        {
            /// <summary>The device name shown on the row.</summary>
            public string Title;

            /// <summary>A short status line under the title.</summary>
            public string Subtitle;

            /// <summary>The chest this row configures, or <c>null</c> for a bus.</summary>
            public StorageEntry Entry;

            /// <summary>The bus this row configures, or <c>null</c> for a chest.</summary>
            public NetworkNode Node;

            /// <summary>The filter being edited.</summary>
            public ItemFilter Filter;

            /// <summary>Whether this row has an insertion priority, which buses don't.</summary>
            public bool HasPriority => this.Entry != null;

            /// <summary>Writes the filter back to the underlying object's <c>modData</c>.</summary>
            public void Save()
            {
                if (this.Entry != null)
                    this.Entry.SetFilter(this.Filter);
                else
                    this.Node?.SaveFilter(this.Filter);
            }
        }


        /*********
        ** Private methods: shared chrome
        *********/
        /// <summary>Draws the title, tabs, search box and the Items-tab toolbar.</summary>
        private void DrawHeader(SpriteBatch b)
        {
            foreach (ClickableComponent tab in this.TabButtons)
            {
                bool active = tab.name == "craft"
                    ? false
                    : this.Tab.ToString() == tab.name;

                drawTextureBox(
                    b,
                    Game1.mouseCursors,
                    new Rectangle(384, 396, 15, 15),
                    tab.bounds.X,
                    tab.bounds.Y,
                    tab.bounds.Width,
                    tab.bounds.Height,
                    active ? Color.White : Color.White * 0.65f,
                    3f,
                    drawShadow: false
                );

                Utility.drawTextWithShadow(
                    b,
                    this.GetTabLabel(tab.name),
                    Game1.smallFont,
                    new Vector2(tab.bounds.X + 16, tab.bounds.Y + 10),
                    active ? Game1.textColor : Game1.textColor * 0.7f
                );
            }

            this.SearchBox.Draw(b);
            if (string.IsNullOrEmpty(this.SearchBox.Text) && !this.SearchBox.Selected)
            {
                Utility.drawTextWithShadow(
                    b,
                    this.Translations.Get("ui.search-hint"),
                    Game1.smallFont,
                    new Vector2(this.SearchBox.X + 20, this.SearchBox.Y + 10),
                    Game1.textColor * 0.45f
                );
            }

            if (this.Tab != TerminalTab.Items)
                return;

            this.SortButton.draw(b);
            this.DepositAllButton.draw(b);

            long totalItems = this.AllStock.Sum(entry => entry.Count);
            string summary = this.Translations.Get("ui.summary", new
            {
                types = NumberFormat.Full(this.AllStock.Count),
                items = NumberFormat.Full(totalItems),
                free = NumberFormat.Full(this.Network?.FreeSlots ?? 0)
            });

            Utility.drawTextWithShadow(b, summary, Game1.smallFont, new Vector2(this.xPositionOnScreen + 148, this.yPositionOnScreen + 76), Game1.textColor);
        }

        /// <summary>Draws a vertical scrollbar beside a scrollable area.</summary>
        private void DrawScrollbar(SpriteBatch b, Rectangle area, int visibleRows, int totalRows)
        {
            if (totalRows <= visibleRows)
                return;

            int barX = area.Right + 12;
            drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6), barX, area.Y, 24, area.Height, Color.White, 4f, drawShadow: false);

            float progress = this.ScrollOffset / (float)Math.Max(1, totalRows - visibleRows);
            int travel = area.Height - 64;
            int thumbY = area.Y + (int)(progress * travel);

            b.Draw(
                Game1.mouseCursors,
                new Rectangle(barX, thumbY, 24, 40),
                new Rectangle(435, 463, 6, 10),
                Color.White
            );
        }


        /*********
        ** Private methods: items tab
        *********/
        /// <summary>Draws the network's aggregated stock as a grid of slots.</summary>
        private void DrawItemsTab(SpriteBatch b)
        {
            Rectangle grid = this.GetGridBounds();

            if (this.Network == null)
            {
                this.DrawCentredMessage(b, grid, this.Translations.Get("error.not-connected"));
                return;
            }

            if (!this.Network.IsOnline)
            {
                this.DrawCentredMessage(b, grid, this.Translations.Get("error.overloaded", new { used = this.Network.UsedChannels, capacity = this.Network.ChannelCapacity }));
                return;
            }

            int firstIndex = this.ScrollOffset * Columns;

            for (int row = 0; row < this.Rows; row++)
            {
                for (int column = 0; column < Columns; column++)
                {
                    int x = grid.X + (column * SlotSize);
                    int y = grid.Y + (row * SlotSize);

                    b.Draw(Game1.menuTexture, new Vector2(x, y), Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10), Color.White);

                    int index = firstIndex + (row * Columns) + column;
                    if (index >= this.VisibleStock.Count)
                        continue;

                    NetworkItemStack entry = this.VisibleStock[index];
                    entry.Sample.drawInMenu(b, new Vector2(x, y), 1f, 1f, 0.9f, StackDrawType.Hide, Color.White, drawShadow: true);

                    // Vanilla stack numbers max out long before a network does, so draw the count ourselves.
                    string count = NumberFormat.Abbreviate(entry.Count);
                    Vector2 size = Game1.tinyFont.MeasureString(count);
                    Utility.drawTextWithShadow(b, count, Game1.tinyFont, new Vector2(x + SlotSize - size.X - 6, y + SlotSize - size.Y - 4), Color.White);
                }
            }

            if (this.VisibleStock.Count == 0)
            {
                string message = string.IsNullOrWhiteSpace(this.SearchBox.Text)
                    ? this.Translations.Get("ui.empty-network")
                    : this.Translations.Get("ui.no-results");
                this.DrawCentredMessage(b, grid, message);
            }

            int totalRows = (int)Math.Ceiling(this.VisibleStock.Count / (double)Columns);
            this.DrawScrollbar(b, grid, this.Rows, totalRows);
        }


        /*********
        ** Private methods: storage tab
        *********/
        /// <summary>Rebuilds the list of configurable devices.</summary>
        private void RefreshConfigRows()
        {
            this.ConfigRows.Clear();
            if (this.Network == null)
                return;

            foreach (StorageEntry entry in this.Network.Storages)
            {
                this.ConfigRows.Add(new ConfigRow
                {
                    Title = this.Translations.Get("device.chest", new { x = (int)entry.Tile.X, y = (int)entry.Tile.Y }),
                    Subtitle = this.Translations.Get("device.chest-slots", new { used = entry.UsedSlots, total = entry.Capacity }),
                    Entry = entry,
                    Filter = entry.Filter
                });
            }

            foreach (NetworkNode node in this.Network.Nodes.Where(n => n.Kind is NodeKind.ImportBus or NodeKind.ExportBus))
            {
                this.ConfigRows.Add(new ConfigRow
                {
                    Title = this.Translations.Get(node.Kind == NodeKind.ImportBus ? "device.import-bus" : "device.export-bus", new { x = (int)node.Tile.X, y = (int)node.Tile.Y }),
                    Subtitle = this.Translations.Get(node.Kind == NodeKind.ImportBus ? "device.import-hint" : "device.export-hint"),
                    Node = node,
                    Filter = node.GetFilter()
                });
            }
        }

        /// <summary>Draws the per-chest and per-bus configuration rows.</summary>
        private void DrawStorageTab(SpriteBatch b)
        {
            Rectangle grid = this.GetGridBounds();

            if (this.ConfigRows.Count == 0)
            {
                this.DrawCentredMessage(b, grid, this.Translations.Get("ui.no-devices"));
                return;
            }

            int visibleRows = grid.Height / RowHeight;

            for (int i = 0; i < visibleRows; i++)
            {
                int index = this.ScrollOffset + i;
                if (index >= this.ConfigRows.Count)
                    break;

                ConfigRow row = this.ConfigRows[index];
                int y = grid.Y + (i * RowHeight);

                drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), grid.X, y, grid.Width, RowHeight - 8, Color.White * 0.9f, 1f, drawShadow: false);

                Utility.drawTextWithShadow(b, row.Title, Game1.smallFont, new Vector2(grid.X + 20, y + 14), Game1.textColor);
                Utility.drawTextWithShadow(b, row.Subtitle, Game1.smallFont, new Vector2(grid.X + 20, y + 42), Game1.textColor * 0.6f);

                // Priority controls
                if (row.HasPriority)
                {
                    Rectangle minus = this.GetPriorityButton(grid, y, increase: false);
                    Rectangle plus = this.GetPriorityButton(grid, y, increase: true);

                    b.Draw(Game1.mouseCursors, minus, new Rectangle(177, 345, 7, 8), Color.White);
                    b.Draw(Game1.mouseCursors, plus, new Rectangle(184, 345, 7, 8), Color.White);

                    string priority = row.Entry.Priority.ToString();
                    Vector2 size = Game1.smallFont.MeasureString(priority);
                    Utility.drawTextWithShadow(b, priority, Game1.smallFont, new Vector2(minus.Right + 20 - (size.X / 2), y + 26), Game1.textColor);
                }

                // Allow/deny toggle
                Rectangle mode = this.GetModeButton(grid, y);
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), mode.X, mode.Y, mode.Width, mode.Height, Color.White, 2f, drawShadow: false);
                Utility.drawTextWithShadow(
                    b,
                    this.Translations.Get(row.Filter.Mode == FilterMode.Allow ? "filter.allow" : "filter.deny"),
                    Game1.tinyFont,
                    new Vector2(mode.X + 8, mode.Y + 10),
                    Game1.textColor
                );

                // Filter slots
                List<Item> samples = row.Filter.GetSampleItems().ToList();
                for (int slot = 0; slot < ItemFilter.MaxEntries; slot++)
                {
                    Rectangle bounds = this.GetFilterSlot(grid, y, slot);
                    bool pending = this.PendingFilterRow == row && this.PendingFilterSlot == slot;

                    b.Draw(Game1.menuTexture, bounds, Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10), pending ? Color.Gold : Color.White);

                    if (slot < samples.Count && samples[slot] != null)
                        samples[slot].drawInMenu(b, new Vector2(bounds.X, bounds.Y), 0.5f, 1f, 0.9f, StackDrawType.Hide, Color.White, drawShadow: false);
                }
            }

            this.DrawScrollbar(b, grid, visibleRows, this.ConfigRows.Count);

            if (this.PendingFilterRow != null)
            {
                Utility.drawTextWithShadow(
                    b,
                    this.Translations.Get("ui.pick-filter-item"),
                    Game1.smallFont,
                    new Vector2(grid.X, grid.Bottom + 4),
                    Game1.textColor
                );
            }
        }

        /// <summary>The bounds of a priority button on a row.</summary>
        private Rectangle GetPriorityButton(Rectangle grid, int rowY, bool increase)
        {
            int x = grid.X + 300 + (increase ? 72 : 0);
            return new Rectangle(x, rowY + 24, 28, 32);
        }

        /// <summary>The bounds of the allow/deny toggle on a row.</summary>
        private Rectangle GetModeButton(Rectangle grid, int rowY) => new(grid.X + 400, rowY + 20, 64, 40);

        /// <summary>The bounds of one filter slot on a row.</summary>
        private Rectangle GetFilterSlot(Rectangle grid, int rowY, int slot)
        {
            return new Rectangle(grid.X + 480 + (slot * FilterSlotSize), rowY + 22, FilterSlotSize, FilterSlotSize);
        }


        /*********
        ** Private methods: network tab
        *********/
        /// <summary>Draws a readout of the network's size, channel usage and capacity.</summary>
        private void DrawNetworkTab(SpriteBatch b)
        {
            Rectangle grid = this.GetGridBounds();

            if (this.Network == null)
            {
                this.DrawCentredMessage(b, grid, this.Translations.Get("error.not-connected"));
                return;
            }

            List<string> lines = new()
            {
                this.Translations.Get(this.Network.IsOnline ? "network.status-online" : "network.status-overloaded"),
                "",
                this.Translations.Get("network.channels", new { used = this.Network.UsedChannels, capacity = this.Network.ChannelCapacity }),
                this.Translations.Get("network.controllers", new { count = this.Network.ControllerCount }),
                this.Translations.Get("network.cables", new { count = this.Network.GetNodes(NodeKind.Cable).Count() }),
                this.Translations.Get("network.terminals", new { count = this.Network.GetNodes(NodeKind.Terminal).Count() + this.Network.GetNodes(NodeKind.CraftingTerminal).Count() }),
                this.Translations.Get("network.buses", new
                {
                    imports = this.Network.GetNodes(NodeKind.ImportBus).Count(),
                    exports = this.Network.GetNodes(NodeKind.ExportBus).Count()
                }),
                "",
                this.Translations.Get("network.chests", new { count = this.Network.Storages.Count }),
                this.Translations.Get("network.slots", new { used = NumberFormat.Full(this.Network.UsedSlots), total = NumberFormat.Full(this.Network.TotalSlots) }),
                this.Translations.Get("network.stock", new
                {
                    types = NumberFormat.Full(this.AllStock.Count),
                    items = NumberFormat.Full(this.AllStock.Sum(entry => entry.Count))
                })
            };

            if (!this.Network.IsOnline)
            {
                lines.Add("");
                lines.Add(this.Translations.Get("network.overload-hint"));
            }

            int y = grid.Y + 8;
            foreach (string line in lines)
            {
                if (!string.IsNullOrEmpty(line))
                    Utility.drawTextWithShadow(b, line, Game1.smallFont, new Vector2(grid.X + 16, y), Game1.textColor);
                y += 32;
            }
        }


        /*********
        ** Private methods: input on the non-item tabs
        *********/
        /// <summary>Handles a click while the Storage or Network tab is showing.</summary>
        private void ReceiveClickOnTab(int x, int y, bool rightClick)
        {
            if (this.Tab != TerminalTab.Storage)
                return;

            Rectangle grid = this.GetGridBounds();
            int visibleRows = grid.Height / RowHeight;

            // Assigning an item to a filter slot: the slot was armed by an earlier click, and now the player
            // picks the item from their own inventory.
            if (this.PendingFilterRow != null)
            {
                int slot = this.PlayerInventory.getInventoryPositionOfClick(x, y);
                if (slot >= 0 && slot < Game1.player.Items.Count && Game1.player.Items[slot] != null)
                {
                    if (this.PendingFilterRow.Filter.Add(Game1.player.Items[slot]))
                    {
                        this.PendingFilterRow.Save();
                        Game1.playSound("smallSelect");
                    }
                    this.PendingFilterRow = null;
                    this.PendingFilterSlot = -1;
                    return;
                }
            }

            for (int i = 0; i < visibleRows; i++)
            {
                int index = this.ScrollOffset + i;
                if (index >= this.ConfigRows.Count)
                    break;

                ConfigRow row = this.ConfigRows[index];
                int rowY = grid.Y + (i * RowHeight);

                if (row.HasPriority)
                {
                    if (this.GetPriorityButton(grid, rowY, increase: false).Contains(x, y))
                    {
                        row.Entry.Priority -= rightClick ? 10 : 1;
                        this.Networks.Invalidate(this.TerminalLocation);
                        this.RefreshStock();
                        Game1.playSound("drumkit6");
                        return;
                    }
                    if (this.GetPriorityButton(grid, rowY, increase: true).Contains(x, y))
                    {
                        row.Entry.Priority += rightClick ? 10 : 1;
                        this.Networks.Invalidate(this.TerminalLocation);
                        this.RefreshStock();
                        Game1.playSound("drumkit6");
                        return;
                    }
                }

                if (this.GetModeButton(grid, rowY).Contains(x, y))
                {
                    row.Filter.Mode = row.Filter.Mode == FilterMode.Allow ? FilterMode.Deny : FilterMode.Allow;
                    row.Save();
                    Game1.playSound("shwip");
                    return;
                }

                for (int slot = 0; slot < ItemFilter.MaxEntries; slot++)
                {
                    if (!this.GetFilterSlot(grid, rowY, slot).Contains(x, y))
                        continue;

                    if (slot < row.Filter.Count)
                    {
                        // A filled slot is cleared by clicking it.
                        row.Filter.RemoveAt(slot);
                        row.Save();
                        Game1.playSound("trashcan");
                    }
                    else
                    {
                        this.PendingFilterRow = row;
                        this.PendingFilterSlot = slot;
                        Game1.playSound("smallSelect");
                    }
                    return;
                }
            }
        }

        /// <summary>Sets the hover text while the Storage or Network tab is showing.</summary>
        private void PerformHoverOnTab(int x, int y)
        {
            if (this.Tab != TerminalTab.Storage)
                return;

            Rectangle grid = this.GetGridBounds();
            int visibleRows = grid.Height / RowHeight;

            for (int i = 0; i < visibleRows; i++)
            {
                int index = this.ScrollOffset + i;
                if (index >= this.ConfigRows.Count)
                    break;

                ConfigRow row = this.ConfigRows[index];
                int rowY = grid.Y + (i * RowHeight);

                if (row.HasPriority && (this.GetPriorityButton(grid, rowY, false).Contains(x, y) || this.GetPriorityButton(grid, rowY, true).Contains(x, y)))
                {
                    this.HoverText = this.Translations.Get("ui.priority-hint");
                    return;
                }

                if (this.GetModeButton(grid, rowY).Contains(x, y))
                {
                    this.HoverText = this.Translations.Get("ui.mode-hint");
                    return;
                }

                for (int slot = 0; slot < ItemFilter.MaxEntries; slot++)
                {
                    if (this.GetFilterSlot(grid, rowY, slot).Contains(x, y))
                    {
                        this.HoverText = slot < row.Filter.Count
                            ? this.Translations.Get("ui.filter-clear-hint")
                            : this.Translations.Get("ui.filter-set-hint");
                        return;
                    }
                }
            }

            Item hovered = this.PlayerInventory.hover(x, y, null);
            if (hovered != null)
            {
                this.HoverItem = hovered;
                this.HoverText = this.PendingFilterRow != null
                    ? this.Translations.Get("ui.pick-filter-item")
                    : "";
            }
        }

        /// <summary>The largest scroll offset for the current non-item tab.</summary>
        private int GetMaxScrollForTab()
        {
            if (this.Tab != TerminalTab.Storage)
                return 0;

            int visibleRows = this.GetGridBounds().Height / RowHeight;
            return Math.Max(0, this.ConfigRows.Count - visibleRows);
        }

        /// <summary>Draws a message in the middle of an area, for empty or error states.</summary>
        private void DrawCentredMessage(SpriteBatch b, Rectangle area, string message)
        {
            Vector2 size = Game1.smallFont.MeasureString(message);
            Utility.drawTextWithShadow(
                b,
                message,
                Game1.smallFont,
                new Vector2(area.Center.X - (size.X / 2), area.Center.Y - (size.Y / 2)),
                Game1.textColor * 0.8f
            );
        }
    }
}
