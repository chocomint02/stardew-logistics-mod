using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Framework;
using StardewValley;

namespace StardewLogistics.Menus
{
    /// <summary>The terminal's autocrafting tabs: what can be ordered, and what's currently running.</summary>
    internal partial class TerminalMenu
    {
        /*********
        ** Fields
        *********/
        private List<AutoTarget> AllTargets = new();
        private List<AutoTarget> VisibleTargets = new();
        private AutoTarget HoverTarget;


        /*********
        ** Nested types
        *********/
        /// <summary>Something the network could be asked to make.</summary>
        /// <remarks>
        /// Wider than the Craft tab's list: that one only shows recipes the player can make right now from stock,
        /// while autocrafting can also make the ingredients. It also includes items with no crafting recipe at
        /// all, like a copper bar, which only a machine produces.
        /// </remarks>
        internal class AutoTarget : IFilterableEntry
        {
            /// <summary>The qualified item ID.</summary>
            public string ItemId { get; init; }

            /// <inheritdoc />
            public string DisplayName { get; init; }

            /// <inheritdoc />
            public int Category { get; init; }

            /// <inheritdoc />
            public string SourceMod { get; init; }

            /// <inheritdoc />
            public Item Sample { get; init; }

            /// <summary>How many the network already holds.</summary>
            public long Count { get; set; }

            /// <summary>Whether a machine is needed, as opposed to a plain crafting recipe.</summary>
            public bool NeedsMachine { get; init; }

            /// <summary>Whether the network currently holds enough to make at least one.</summary>
            public bool CanMake { get; set; }
        }


        /*********
        ** Private methods: data
        *********/
        /// <summary>Rebuilds the list of things the network could be asked to make.</summary>
        private void RefreshTargets()
        {
            if (!this.CanCraft || this.Jobs == null)
                return;

            Dictionary<string, AutoTarget> targets = new(StringComparer.OrdinalIgnoreCase);

            // Everything the player knows how to craft.
            foreach (RecipeEntry recipe in this.Recipes.All)
            {
                string id = recipe.Output?.QualifiedItemId;
                if (id == null || targets.ContainsKey(id))
                    continue;

                targets[id] = new AutoTarget
                {
                    ItemId = id,
                    DisplayName = recipe.DisplayName,
                    Category = recipe.Category,
                    SourceMod = recipe.SourceMod,
                    Sample = recipe.Output,
                    NeedsMachine = false
                };
            }

            // Plus anything the machines actually on this network can produce. Listing recipes for machines the
            // player doesn't own would just be a catalogue of things they can't order.
            HashSet<string> available = new(
                (this.Network?.Machines ?? Enumerable.Empty<Network.NetworkNode>())
                    .Select(node => node.Object?.QualifiedItemId)
                    .Where(id => id != null),
                StringComparer.OrdinalIgnoreCase);

            foreach (MachineRecipe recipe in this.MachineRecipes.All)
            {
                if (!available.Contains(recipe.MachineId) || targets.ContainsKey(recipe.OutputId))
                    continue;

                Item sample = TryCreate(recipe.OutputId);
                if (sample == null)
                    continue;

                targets[recipe.OutputId] = new AutoTarget
                {
                    ItemId = recipe.OutputId,
                    DisplayName = sample.DisplayName,
                    Category = sample.Category,
                    SourceMod = ItemSource.GetSourceName(sample),
                    Sample = sample,
                    NeedsMachine = true
                };
            }

            // Show what's already in storage next to each, so the player can see what's worth ordering.
            Dictionary<string, long> stock = new(StringComparer.OrdinalIgnoreCase);
            foreach (NetworkItemStack entry in this.AllStock)
            {
                string id = entry.Sample?.QualifiedItemId;
                if (id == null)
                    continue;
                stock[id] = stock.TryGetValue(id, out long existing) ? existing + entry.Count : entry.Count;
            }

            foreach (AutoTarget target in targets.Values)
                target.Count = stock.TryGetValue(target.ItemId, out long have) ? have : 0;

            this.AllTargets = targets.Values.ToList();
            this.RefreshFeasibility(available);
            this.ApplyTargetFilter();
        }

        /// <summary>Works out which targets the network could actually make one of right now.</summary>
        /// <remarks>
        /// This costs one plan per target, which is why it runs on the refresh timer rather than per frame. The
        /// list is bounded by what the player knows plus what their machines produce, so it stays small; the cap
        /// is a guard against a heavily modded save turning this into a stall.
        /// </remarks>
        private void RefreshFeasibility(HashSet<string> availableMachines)
        {
            const int cap = 400;

            IReadOnlyList<IFilterableEntry> stock = this.AllStock.Cast<IFilterableEntry>().ToList();
            CraftPlanner planner = new(this.Recipes, this.MachineRecipes, this.Config.MaxCraftDepth);

            int planned = 0;
            foreach (AutoTarget target in this.AllTargets)
            {
                if (planned++ >= cap)
                {
                    target.CanMake = true;
                    continue;
                }

                try
                {
                    // Same rule as an order: "can make one" means can produce one, not "there's one on the shelf".
                    target.CanMake = planner.Plan(target.ItemId, 1, stock, null, availableMachines).IsSatisfied;
                }
                catch
                {
                    target.CanMake = false;
                }
            }
        }

        /// <summary>Applies the search box and dropdowns to the target list.</summary>
        private void ApplyTargetFilter()
        {
            IEnumerable<AutoTarget> query = this.AllTargets;

            if (!this.Filter.IsEmpty)
                query = query.Where(target => this.Filter.Matches(target));

            query = this.Sort switch
            {
                SortMode.Count => query.OrderByDescending(target => target.Count).ThenBy(target => target.DisplayName),
                SortMode.Category => query.OrderBy(target => target.Category).ThenBy(target => target.DisplayName),
                _ => query.OrderBy(target => target.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            };

            this.VisibleTargets = query.ToList();
            this.ClampScroll(TerminalTab.Auto, this.GetMaxTargetScroll());
        }

        /// <summary>The largest scroll offset for the target grid.</summary>
        private int GetMaxTargetScroll()
        {
            int rows = (int)Math.Ceiling(this.VisibleTargets.Count / (double)Columns);
            return Math.Max(0, rows - this.Rows);
        }

        /// <summary>The target under a screen position.</summary>
        private AutoTarget GetTargetAt(int x, int y)
        {
            Rectangle grid = this.GetGridBounds();
            if (!grid.Contains(x, y))
                return null;

            int index = ((this.ScrollOffset + ((y - grid.Y) / SlotSize)) * Columns) + ((x - grid.X) / SlotSize);
            return index >= 0 && index < this.VisibleTargets.Count ? this.VisibleTargets[index] : null;
        }

        /// <summary>Opens the planner for a target.</summary>
        private void OpenPlanner(AutoTarget target)
        {
            if (this.Network == null)
            {
                this.ShowError(this.Translations.Get("error.not-connected"));
                return;
            }

            this.ReleaseKeyboard();
            TerminalMenu parent = this;

            AutoCraftMenu planner = new(
                target.ItemId,
                target.DisplayName,
                this.Network,
                this.Recipes,
                this.MachineRecipes,
                this.Jobs,
                this.Config,
                this.Translations,
                onClose: () => Game1.activeClickableMenu = parent
            );

            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = planner;
        }


        /*********
        ** Private methods: drawing
        *********/
        /// <summary>Draws the grid of things that can be ordered.</summary>
        private void DrawAutoTab(SpriteBatch b)
        {
            Rectangle grid = this.GetGridBounds();

            if (this.Network == null)
            {
                this.DrawCentredMessage(b, grid, this.Translations.Get("error.not-connected"));
                return;
            }

            int first = this.ScrollOffset * Columns;

            for (int row = 0; row < this.Rows; row++)
            {
                for (int column = 0; column < Columns; column++)
                {
                    int x = grid.X + (column * SlotSize);
                    int y = grid.Y + (row * SlotSize);
                    b.Draw(Game1.menuTexture, new Vector2(x, y), Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10), Color.White);

                    int index = first + (row * Columns) + column;
                    if (index >= this.VisibleTargets.Count)
                        continue;

                    AutoTarget target = this.VisibleTargets[index];

                    // Dimmed rather than hidden, matching the Craft tab: the player can still see what exists
                    // and open it to find out what it is short of.
                    target.Sample.drawInMenu(b, new Vector2(x, y), 1f, target.CanMake ? 1f : 0.3f, 0.9f, StackDrawType.Hide, Color.White, drawShadow: target.CanMake);

                    if (target.Count > 0)
                        DrawSlotCount(b, NumberFormat.Abbreviate(target.Count), x, y);

                    // A corner mark separates "a machine makes this" from "you can craft it by hand".
                    if (target.NeedsMachine)
                        b.Draw(Game1.staminaRect, new Rectangle(x + 4, y + 4, 8, 8), new Color(92, 222, 240) * 0.9f);
                }
            }

            if (this.VisibleTargets.Count == 0)
                this.DrawCentredMessage(b, grid, this.Translations.Get("auto.nothing"));

            this.DrawScrollbar(b, grid, this.Rows, (int)Math.Ceiling(this.VisibleTargets.Count / (double)Columns));

            Utility.drawTextWithShadow(
                b,
                this.Translations.Get("auto.tab-summary", new { shown = this.VisibleTargets.Count, total = this.AllTargets.Count }),
                Game1.smallFont,
                new Vector2(grid.X, grid.Bottom + 6),
                Game1.textColor
            );
            // Two lines rather than one clipped one: the legend never fitted beside the instruction.
            DrawClipped(b, this.Translations.Get("auto.tab-hint"), new Vector2(grid.X, grid.Bottom + 38), grid.Width, Game1.textColor * 0.6f);
            DrawClipped(b, this.Translations.Get("auto.tab-legend"), new Vector2(grid.X, grid.Bottom + 70), grid.Width, Game1.textColor * 0.6f);
        }

        /// <summary>Draws the list of running jobs.</summary>
        private void DrawJobsTab(SpriteBatch b)
        {
            Rectangle grid = this.GetGridBounds();
            IReadOnlyList<CraftJob> jobs = this.Jobs?.Jobs ?? Array.Empty<CraftJob>();

            if (jobs.Count == 0)
            {
                this.DrawCentredMessage(b, grid, this.Translations.Get("jobs.none"));
                return;
            }

            const int rowHeight = 96;
            int visible = grid.Height / rowHeight;

            for (int i = 0; i < visible; i++)
            {
                int index = this.ScrollOffset + i;
                if (index >= jobs.Count)
                    break;

                CraftJob job = jobs[index];
                int y = grid.Y + (i * rowHeight);
                drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), grid.X, y, grid.Width, rowHeight - 8, Color.White * 0.9f, 1f, drawShadow: false);

                Utility.drawTextWithShadow(b, $"{job.TargetCount}x {job.DisplayName}", Game1.smallFont, new Vector2(grid.X + 18, y + 14), Game1.textColor);

                string status = this.Translations.Get("jobs.status-" + job.Status.ToString().ToLowerInvariant());
                if (job.Status == JobStatus.Blocked && job.BlockedReason != null)
                    status += $": {job.BlockedReason}";

                Utility.drawTextWithShadow(b, status, Game1.smallFont, new Vector2(grid.X + 18, y + 48),
                    job.Status == JobStatus.Blocked ? Color.Firebrick : Game1.textColor * 0.65f);

                // Progress bar
                int barX = grid.X + 420;
                // Leaves room for "100%" between the bar and the Cancel/Clear button; at the old width the
                // percentage ran under the button.
                int barWidth = grid.Width - 700;
                b.Draw(Game1.staminaRect, new Rectangle(barX, y + 22, barWidth, 22), new Color(60, 44, 32) * 0.55f);
                b.Draw(Game1.staminaRect, new Rectangle(barX, y + 22, (int)(barWidth * job.Progress), 22), new Color(104, 196, 112));

                string percent = $"{job.Progress * 100:0}%";
                Utility.drawTextWithShadow(b, percent, Game1.smallFont, new Vector2(barX + barWidth + 14, y + 20), Game1.textColor);

                if (job.Status is JobStatus.Running or JobStatus.Pending)
                {
                    string eta = this.Translations.Get("jobs.eta", new { time = FormatGameTime(job.EstimatedMinutesRemaining) });
                    Utility.drawTextWithShadow(b, eta, Game1.smallFont, new Vector2(barX, y + 52), Game1.textColor * 0.7f);
                }

                Rectangle button = GetCancelBounds(grid, y);
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), button.X, button.Y, button.Width, button.Height, Color.White, 2f, drawShadow: false);

                string buttonLabel = this.Translations.Get(job.Status is JobStatus.Complete or JobStatus.Cancelled ? "jobs.clear" : "jobs.cancel");
                Vector2 buttonSize = Game1.smallFont.MeasureString(buttonLabel);
                Utility.drawTextWithShadow(b, buttonLabel, Game1.smallFont, new Vector2(button.Center.X - (buttonSize.X / 2), button.Center.Y - (buttonSize.Y / 2)), Game1.textColor);
            }

            this.DrawScrollbar(b, grid, visible, jobs.Count);
        }

        /// <summary>Handles a click on the jobs tab.</summary>
        private void ReceiveClickOnJobs(int x, int y)
        {
            Rectangle grid = this.GetGridBounds();
            IReadOnlyList<CraftJob> jobs = this.Jobs?.Jobs ?? Array.Empty<CraftJob>();

            const int rowHeight = 96;
            int visible = grid.Height / rowHeight;

            for (int i = 0; i < visible; i++)
            {
                int index = this.ScrollOffset + i;
                if (index >= jobs.Count)
                    break;

                if (!GetCancelBounds(grid, grid.Y + (i * rowHeight)).Contains(x, y))
                    continue;

                CraftJob job = jobs[index];
                bool acted = job.Status is JobStatus.Complete or JobStatus.Cancelled
                    ? this.Jobs.Dismiss(job.Id)
                    : this.Jobs.Cancel(job.Id);

                if (acted)
                    Game1.playSound("trashcan");
                return;
            }
        }

        /// <summary>Draws a line of text, shortened if it would run past a width.</summary>
        /// <remarks>
        /// A footer legend is the kind of string that grows when reworded or translated, and it sits right on the
        /// panel edge. Clamping it here means no wording can push it outside the frame.
        /// </remarks>
        private static void DrawClipped(SpriteBatch b, string text, Vector2 position, int maxWidth, Color colour)
        {
            if (string.IsNullOrEmpty(text))
                return;

            if (Game1.smallFont.MeasureString(text).X > maxWidth)
            {
                while (text.Length > 1 && Game1.smallFont.MeasureString(text + "...").X > maxWidth)
                    text = text.Substring(0, text.Length - 1);
                text += "...";
            }

            Utility.drawTextWithShadow(b, text, Game1.smallFont, position, colour);
        }

        /// <summary>The bounds of a job row's cancel button.</summary>
        private static Rectangle GetCancelBounds(Rectangle grid, int rowY) => new(grid.Right - 150, rowY + 22, 130, 44);

        /// <summary>Formats an in-game duration for the jobs list.</summary>
        private static string FormatGameTime(int minutes)
        {
            if (minutes <= 0)
                return "any moment";
            if (minutes < 60)
                return $"{minutes}m";
            return minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h {minutes % 60}m";
        }

        /// <summary>Builds a sample item, or <c>null</c> if the ID no longer resolves.</summary>
        private static Item TryCreate(string qualifiedId)
        {
            try
            {
                return ItemRegistry.Create(qualifiedId, 1, 0, allowNull: true);
            }
            catch
            {
                return null;
            }
        }
    }
}
