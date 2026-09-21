using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewLogistics.Devices;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewLogistics.Menus
{
    /// <summary>Shows how an autocrafting job would be carried out, and lets the player adjust it before starting.</summary>
    /// <remarks>
    /// The tree is the point of this screen. An autocrafting job can involve several machines running for hours,
    /// so the player should see what it will consume, which machines it will occupy and roughly how long it will
    /// take <em>before</em> committing, rather than discovering it afterwards.
    ///
    /// Changing the quantity or a machine choice re-plans from scratch. Planning is cheap next to drawing, and
    /// re-planning avoids a whole class of bug where the displayed tree and the queued job disagree.
    /// </remarks>
    internal class AutoCraftMenu : IClickableMenu
    {
        /*********
        ** Fields
        *********/
        private const int MenuWidth = 1000;
        private const int RowHeight = 40;

        private readonly RecipeIndex Crafting;
        private readonly MachineRecipeIndex MachineRecipes;
        private readonly JobRunner Jobs;
        private readonly StorageNetwork Network;
        private readonly ITranslationHelper Translations;
        private readonly ModConfig Config;
        private readonly string TargetId;
        private readonly string TargetName;
        private readonly Action OnClose;

        /// <summary>The player's machine choice per output item, which re-plans when changed.</summary>
        private readonly Dictionary<string, string> Preferences = new(StringComparer.OrdinalIgnoreCase);

        private readonly TextBox QuantityBox;
        private ClickableComponent QuantityBounds;
        private ClickableComponent StartButton;
        private readonly List<(Rectangle Bounds, int Delta)> StepButtons = new();
        private readonly DropdownPopup Dropdown = new();

        private CraftPlan Plan;
        private List<PlanNode> Rows = new();
        private int Quantity = 1;
        private int MaxMachines = 1;
        private int Scroll;
        private string LastText = "1";
        private string HoverText = "";
        private PlanNode PendingMachineChoice;


        /*********
        ** Public methods
        *********/
        public AutoCraftMenu(string targetId, string targetName, StorageNetwork network, RecipeIndex crafting, MachineRecipeIndex machineRecipes, JobRunner jobs, ModConfig config, ITranslationHelper translations, Action onClose)
        {
            this.TargetId = targetId;
            this.TargetName = targetName;
            this.Network = network;
            this.Crafting = crafting;
            this.MachineRecipes = machineRecipes;
            this.Jobs = jobs;
            this.Config = config;
            this.Translations = translations;
            this.OnClose = onClose;

            this.width = MenuWidth;
            this.height = Math.Min(760, Game1.uiViewport.Height - 80);
            this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
            this.yPositionOnScreen = (Game1.uiViewport.Height - this.height) / 2;

            this.QuantityBox = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
            {
                X = this.xPositionOnScreen + 300,
                Y = this.yPositionOnScreen + 84,
                Width = 140,
                Height = 44,
                Text = "1"
            };

            this.BuildButtons();
            this.Replan();
            this.initializeUpperRightCloseButton();
        }

        /// <inheritdoc />
        public override void update(GameTime time)
        {
            base.update(time);

            if (this.QuantityBox.Text != this.LastText)
            {
                this.LastText = this.QuantityBox.Text;
                if (MathExpression.TryEvaluate(this.QuantityBox.Text, out int value) && value > 0)
                {
                    this.Quantity = Math.Clamp(value, 1, 9999);
                    this.Replan();
                }
            }
        }

        /// <inheritdoc />
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (this.Dropdown.IsOpen)
            {
                if (this.Dropdown.ReceiveLeftClick(x, y, out object chosen))
                {
                    if (chosen is string machineId && this.PendingMachineChoice != null)
                    {
                        this.Preferences[this.PendingMachineChoice.ItemId] = machineId;
                        this.Replan();
                    }
                    this.PendingMachineChoice = null;
                    return;
                }
            }

            if (this.upperRightCloseButton?.containsPoint(x, y) == true)
            {
                this.exitThisMenu();
                return;
            }

            foreach ((Rectangle bounds, int delta) in this.StepButtons)
            {
                if (!bounds.Contains(x, y))
                    continue;

                if (delta == 0)
                    this.SetMachines(this.MaxMachines - 1);
                else if (delta == int.MaxValue)
                    this.SetMachines(this.MaxMachines + 1);
                else
                    this.SetQuantity(this.Quantity + delta);

                Game1.playSound("drumkit6");
                return;
            }

            bool clickedBox = this.QuantityBounds.containsPoint(x, y);
            this.QuantityBox.Selected = clickedBox;
            if (clickedBox)
                return;

            // A processing step with more than one capable machine can be reassigned.
            PlanNode row = this.GetRowAt(x, y);
            if (row is { Kind: PlanStepKind.Process } && row.Alternatives.Count > 1)
            {
                this.PendingMachineChoice = row;
                this.Dropdown.Open(
                    row.Alternatives
                        .GroupBy(option => option.MachineId)
                        .Select(group => (Label: $"{group.First().MachineName}  ({group.First().OutputCount} per {FormatTime(group.First().Minutes, group.First().Days)})", Value: (object)group.Key)),
                    this.GetRowBounds(this.Rows.IndexOf(row) - this.Scroll)
                );
                Game1.playSound("shwip");
                return;
            }

            if (this.StartButton.containsPoint(x, y))
                this.Start();
        }

        /// <inheritdoc />
        public override void receiveScrollWheelAction(int direction)
        {
            if (this.Dropdown.ReceiveScroll(direction))
                return;

            int visible = this.GetVisibleRows();
            this.Scroll = Math.Clamp(this.Scroll + (direction > 0 ? -1 : 1), 0, Math.Max(0, this.Rows.Count - visible));
        }

        /// <inheritdoc />
        public override void receiveKeyPress(Keys key)
        {
            if (this.QuantityBox.Selected)
            {
                if (key is Keys.Escape or Keys.Enter)
                    this.QuantityBox.Selected = false;
                return;
            }

            if (key == Keys.Escape || Game1.options.doesInputListContain(Game1.options.menuButton, key))
                this.exitThisMenu();
        }

        /// <inheritdoc />
        public override void performHoverAction(int x, int y)
        {
            this.HoverText = "";

            if (this.Dropdown.IsOpen)
            {
                this.Dropdown.PerformHover(x, y);
                return;
            }

            if (this.StartButton.containsPoint(x, y))
            {
                this.HoverText = this.Plan?.IsSatisfied == true
                    ? this.Translations.Get("auto.start-hint")
                    : this.Translations.Get("auto.cannot-start");
                return;
            }

            PlanNode row = this.GetRowAt(x, y);
            if (row is { Kind: PlanStepKind.Process } && row.Alternatives.Count > 1)
                this.HoverText = this.Translations.Get("auto.change-machine");
        }

        /// <inheritdoc />
        protected override void cleanupBeforeExit()
        {
            if (Game1.keyboardDispatcher.Subscriber == this.QuantityBox)
                Game1.keyboardDispatcher.Subscriber = null;

            base.cleanupBeforeExit();
            this.OnClose?.Invoke();
        }

        /// <inheritdoc />
        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.5f);
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White, 1f, drawShadow: true);

            this.DrawHeader(b);
            this.DrawTree(b);
            this.DrawFooter(b);

            this.upperRightCloseButton?.draw(b);
            this.Dropdown.Draw(b);

            if (!string.IsNullOrEmpty(this.HoverText))
                drawHoverText(b, this.HoverText, Game1.smallFont);

            this.drawMouse(b);
        }


        /*********
        ** Private methods: state
        *********/
        /// <summary>Rebuilds the plan from the current quantity and machine choices.</summary>
        private void Replan()
        {
            List<NetworkItemStack> stock = this.Network?.Aggregate() ?? new List<NetworkItemStack>();
            IReadOnlyList<IFilterableEntry> filterable = stock.Cast<IFilterableEntry>().ToList();
            this.Crafting.Refresh(filterable);

            HashSet<string> available = new(
                (this.Network?.Machines ?? Enumerable.Empty<NetworkNode>())
                    .Select(node => node.Object?.QualifiedItemId)
                    .Where(id => id != null),
                StringComparer.OrdinalIgnoreCase);

            CraftPlanner planner = new(this.Crafting, this.MachineRecipes, this.Config.MaxCraftDepth);
            this.Plan = planner.Plan(this.TargetId, this.Quantity, filterable, this.Preferences, available);

            this.Rows = this.Plan.Root?.Walk().ToList() ?? new List<PlanNode>();
            this.Scroll = Math.Clamp(this.Scroll, 0, Math.Max(0, this.Rows.Count - this.GetVisibleRows()));
        }

        /// <summary>Sets the quantity and re-plans.</summary>
        private void SetQuantity(int value)
        {
            this.Quantity = Math.Clamp(value, 1, 9999);
            this.QuantityBox.Text = this.Quantity.ToString();
            this.LastText = this.QuantityBox.Text;
            this.Replan();
        }

        /// <summary>Sets how many machines each processing step may occupy at once.</summary>
        private void SetMachines(int value)
        {
            this.MaxMachines = Math.Clamp(value, 1, 99);
        }

        /// <summary>Queues the job and closes.</summary>
        private void Start()
        {
            if (this.Plan?.IsSatisfied != true)
            {
                Game1.playSound("cancel");
                return;
            }

            CraftJob job = this.Jobs.TryQueue(this.TargetId, this.Quantity, this.Network, this.MaxMachines, this.Preferences, out string error);
            if (job == null)
            {
                Game1.addHUDMessage(new HUDMessage(error, HUDMessage.error_type));
                return;
            }

            Game1.playSound("bigSelect");
            Game1.addHUDMessage(new HUDMessage(this.Translations.Get("auto.queued", new { count = this.Quantity, name = this.TargetName }), HUDMessage.newQuest_type));
            this.exitThisMenu();
        }


        /*********
        ** Private methods: layout
        *********/
        /// <summary>Builds the quantity and machine-count buttons.</summary>
        private void BuildButtons()
        {
            int y = this.yPositionOnScreen + 86;
            int x = this.xPositionOnScreen + 150;

            foreach (int step in new[] { -10, -1 })
            {
                this.StepButtons.Add((new Rectangle(x, y, 64, 40), step));
                x += 70;
            }

            x = this.QuantityBox.X + this.QuantityBox.Width + 10;
            foreach (int step in new[] { 1, 10 })
            {
                this.StepButtons.Add((new Rectangle(x, y, 64, 40), step));
                x += 70;
            }

            // Machine allowance, using sentinel deltas so one click handler covers both rows.
            this.StepButtons.Add((new Rectangle(this.xPositionOnScreen + 700, y, 44, 40), 0));
            this.StepButtons.Add((new Rectangle(this.xPositionOnScreen + 806, y, 44, 40), int.MaxValue));

            this.QuantityBounds = new ClickableComponent(new Rectangle(this.QuantityBox.X, this.QuantityBox.Y, this.QuantityBox.Width, this.QuantityBox.Height), "quantity");
            this.StartButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + (this.width / 2) - 130, this.yPositionOnScreen + this.height - 88, 260, 64), "start");
        }

        /// <summary>The area the tree is drawn in.</summary>
        private Rectangle GetTreeBounds() => new(this.xPositionOnScreen + 28, this.yPositionOnScreen + 150, this.width - 56, this.height - 150 - 108);

        /// <summary>How many tree rows fit.</summary>
        private int GetVisibleRows() => Math.Max(1, this.GetTreeBounds().Height / RowHeight);

        /// <summary>The bounds of the i'th visible row.</summary>
        private Rectangle GetRowBounds(int visibleIndex)
        {
            Rectangle tree = this.GetTreeBounds();
            return new Rectangle(tree.X, tree.Y + (visibleIndex * RowHeight), tree.Width, RowHeight);
        }

        /// <summary>The plan row under a screen position.</summary>
        private PlanNode GetRowAt(int x, int y)
        {
            Rectangle tree = this.GetTreeBounds();
            if (!tree.Contains(x, y))
                return null;

            int index = this.Scroll + ((y - tree.Y) / RowHeight);
            return index >= 0 && index < this.Rows.Count ? this.Rows[index] : null;
        }


        /*********
        ** Private methods: drawing
        *********/
        /// <summary>Draws the title, quantity controls and machine allowance.</summary>
        private void DrawHeader(SpriteBatch b)
        {
            string title = this.Translations.Get("auto.title", new { name = this.TargetName });
            Utility.drawTextWithShadow(b, title, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + 28, this.yPositionOnScreen + 24), Game1.textColor);

            Utility.drawTextWithShadow(b, this.Translations.Get("auto.quantity"), Game1.smallFont, new Vector2(this.xPositionOnScreen + 28, this.yPositionOnScreen + 96), Game1.textColor);
            Utility.drawTextWithShadow(b, this.Translations.Get("auto.machines"), Game1.smallFont, new Vector2(this.xPositionOnScreen + 560, this.yPositionOnScreen + 96), Game1.textColor);

            foreach ((Rectangle bounds, int delta) in this.StepButtons)
            {
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White, 2f, drawShadow: false);

                string label = delta switch
                {
                    0 => "-",
                    int.MaxValue => "+",
                    _ => delta > 0 ? "+" + delta : delta.ToString()
                };
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), Game1.textColor);
            }

            this.QuantityBox.Draw(b);

            string machines = this.MaxMachines.ToString();
            Vector2 machineSize = Game1.smallFont.MeasureString(machines);
            Utility.drawTextWithShadow(b, machines, Game1.smallFont, new Vector2(this.xPositionOnScreen + 778 - (machineSize.X / 2), this.yPositionOnScreen + 96), Game1.textColor);
        }

        /// <summary>Draws the plan as an indented tree.</summary>
        private void DrawTree(SpriteBatch b)
        {
            Rectangle tree = this.GetTreeBounds();
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), tree.X, tree.Y - 8, tree.Width, tree.Height + 16, Color.White * 0.85f, 1f, drawShadow: false);

            int visible = this.GetVisibleRows();

            for (int i = 0; i < visible; i++)
            {
                int index = this.Scroll + i;
                if (index >= this.Rows.Count)
                    break;

                PlanNode node = this.Rows[index];
                Rectangle row = this.GetRowBounds(i);
                int indent = 20 + (node.Depth * 28);

                Color colour = node.Kind == PlanStepKind.Missing ? Color.Firebrick : Game1.textColor;

                // The branch marker makes depth readable without drawing connecting lines.
                if (node.Depth > 0)
                    Utility.drawTextWithShadow(b, "└", Game1.smallFont, new Vector2(row.X + indent - 20, row.Y + 6), Game1.textColor * 0.5f);

                string label = $"{node.Requested}x {node.DisplayName}";
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(row.X + indent, row.Y + 6), colour);

                string detail = this.DescribeStep(node);
                Vector2 detailSize = Game1.smallFont.MeasureString(detail);
                Utility.drawTextWithShadow(b, detail, Game1.smallFont, new Vector2(row.Right - detailSize.X - 16, row.Y + 6), colour * 0.85f);
            }

            if (this.Rows.Count > visible)
            {
                string more = this.Translations.Get("auto.more-rows", new { count = this.Rows.Count - visible });
                Utility.drawTextWithShadow(b, more, Game1.tinyFont, new Vector2(tree.X + 12, tree.Bottom - 4), Game1.textColor * 0.6f);
            }
        }

        /// <summary>Draws the totals and the start button.</summary>
        private void DrawFooter(SpriteBatch b)
        {
            Rectangle tree = this.GetTreeBounds();
            string summary = this.Plan == null
                ? ""
                : this.Translations.Get("auto.summary", new
                {
                    steps = this.Plan.StepCount,
                    time = FormatTime(this.Plan.WorstCaseMinutes, 0)
                });

            Utility.drawTextWithShadow(b, summary, Game1.smallFont, new Vector2(tree.X + 4, tree.Bottom + 20), Game1.textColor);

            if (this.Plan?.IsSatisfied == false)
            {
                string shortfall = this.Translations.Get("auto.shortfall", new
                {
                    items = string.Join(", ", this.Plan.Shortfalls.Take(3).Select(cost => $"{cost.Count}x {GetName(cost.ItemId)}"))
                });
                Utility.drawTextWithShadow(b, shortfall, Game1.smallFont, new Vector2(tree.X + 4, tree.Bottom + 52), Color.Firebrick);
            }

            bool enabled = this.Plan?.IsSatisfied == true;
            Rectangle bounds = this.StartButton.bounds;
            drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, enabled ? Color.White : Color.Gray, 3f, drawShadow: false);

            string label = this.Translations.Get("auto.start");
            Vector2 size = Game1.smallFont.MeasureString(label);
            Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), enabled ? Game1.textColor : Color.DimGray);
        }

        /// <summary>Describes how a plan step will be supplied.</summary>
        private string DescribeStep(PlanNode node)
        {
            return node.Kind switch
            {
                PlanStepKind.FromStock => this.Translations.Get("auto.step-stock"),
                PlanStepKind.Craft => this.Translations.Get("auto.step-craft", new { count = node.Batches }),
                PlanStepKind.Process => this.Translations.Get("auto.step-process", new
                {
                    machine = node.MachineRecipe.MachineName,
                    count = node.Batches,
                    time = FormatTime(node.MinutesPerBatch, node.DaysPerBatch)
                }),
                _ => this.Translations.Get("auto.step-missing")
            };
        }

        /// <summary>Formats an in-game duration.</summary>
        private static string FormatTime(int minutes, int days)
        {
            if (days > 0)
                return days == 1 ? "overnight" : $"{days}d";
            if (minutes <= 0)
                return "instant";
            if (minutes < 60)
                return $"{minutes}m";
            return minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h {minutes % 60}m";
        }

        /// <summary>The display name for an item ID.</summary>
        private static string GetName(string qualifiedId)
        {
            try
            {
                return ItemRegistry.GetData(qualifiedId)?.DisplayName ?? qualifiedId;
            }
            catch
            {
                return qualifiedId;
            }
        }
    }
}
