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
    /// <summary>What one of the planner's stepper buttons does.</summary>
    /// <remarks>Named rather than encoded as sentinel deltas, which stopped scaling once Min and Max joined the row.</remarks>
    internal enum StepAction
    {
        Quantity,
        MachineDelta,
        MachineMin,
        MachineMax,
        Quality,
        FairyDust
    }

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
        private readonly List<(Rectangle Bounds, StepAction Action, int Delta)> StepButtons = new();
        private readonly DropdownPopup Dropdown = new();

        /// <summary>Icons by item ID. Building one per row per frame would be wasteful; they never change.</summary>
        private static readonly Dictionary<string, Item> IconCache = new(StringComparer.OrdinalIgnoreCase);

        private CraftPlan Plan;
        private List<PlanNode> Rows = new();
        private int Quantity = 1;
        private int MaxMachines = 1;

        /// <summary>Whether the player has set the machine count themselves; until they do, it follows the most available.</summary>
        private bool MachinesPinned;

        /// <summary>Whether to speed the job up with Fairy Dust from storage.</summary>
        private bool UseFairyDust;

        /// <summary>The display row listing the Fairy Dust the job would use, when it's switched on.</summary>
        private PlanNode DustRow;

        /// <summary>The Fairy Dust icon for the toggle.</summary>
        private Item FairyDustIcon;

        /// <summary>How much Fairy Dust storage holds.</summary>
        private int DustAvailable;

        /// <summary>Whether the plan has any machine Fairy Dust works on, with dust in storage to use.</summary>
        private bool CanDust;

        /// <summary>Runs per share that Fairy Dust will speed up, for the time estimate.</summary>
        private readonly Dictionary<MachineAssignment, int> Dusted = new();

        /// <summary>The quality to age the product to, or <see cref="Quality.Any"/> for no aging.</summary>
        private int TargetQuality = Quality.Any;

        /// <summary>Whether the quality row is shown: the item can be aged, and a usable cask is on the network.</summary>
        private readonly bool ShowQuality;

        /// <summary>The most machines this plan could put to work, which caps the control.</summary>
        private int MachinesAvailable = 1;

        /// <summary>The fewest the plan can run on: one of each machine type it uses.</summary>
        private int MachinesFloor = 1;

        /// <summary>How the budget is currently divided, worked out once per re-plan rather than per frame.</summary>
        private readonly Dictionary<PlanNode, Dictionary<MachineAssignment, int>> Allocations = new();
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

            // Quality is only worth offering where a cask can deliver it. An item with nothing to make more of it
            // from -- a wine on the shelf whose fruit is gone -- can only be aged, so the dialog opens on iridium.
            MachineRecipe aging = machineRecipes.GetAgingRecipe(targetId, StardewValley.Object.bestQuality);
            this.ShowQuality = aging != null && network != null && network.CountUsableMachines(aging) > 0;
            if (this.ShowQuality && !machineRecipes.CanProduce(targetId) && crafting.FindByOutput(targetId) == null)
                this.TargetQuality = StardewValley.Object.bestQuality;

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

            foreach ((Rectangle bounds, StepAction action, int delta) in this.StepButtons)
            {
                if (!bounds.Contains(x, y) || (action == StepAction.FairyDust && !this.CanDust))
                    continue;

                switch (action)
                {
                    case StepAction.Quantity:
                        this.SetQuantity(this.Quantity + delta);
                        break;
                    case StepAction.MachineDelta:
                        this.SetMachines(this.MaxMachines + delta);
                        this.MachinesPinned = this.MaxMachines < this.MachinesAvailable;
                        break;
                    case StepAction.MachineMin:
                        this.SetMachines(this.MachinesFloor);
                        this.MachinesPinned = this.MaxMachines < this.MachinesAvailable;
                        break;
                    case StepAction.MachineMax:
                        this.SetMachines(this.MachinesAvailable);
                        this.MachinesPinned = false;
                        break;
                    case StepAction.FairyDust:
                        this.UseFairyDust = !this.UseFairyDust;
                        this.UpdateDustEstimate();
                        break;
                    case StepAction.Quality:
                        this.TargetQuality = delta <= 0 ? Quality.Any : delta;
                        this.Replan();
                        break;
                }

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

            // The Fairy Dust button is a picture, so it says what it does on hover.
            if (this.CanDust && this.StepButtons.Any(button => button.Action == StepAction.FairyDust && button.Bounds.Contains(x, y)))
            {
                this.HoverText = this.Translations.Get(this.UseFairyDust ? "auto.fairy-dust-on" : "auto.fairy-dust-off", new { count = this.DustAvailable });
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
            this.Plan = planner.Plan(this.TargetId, this.Quantity, filterable, this.Preferences, this.CountUsable, this.TargetQuality);

            this.Rows = this.Plan.Root?.Walk().ToList() ?? new List<PlanNode>();
            this.Scroll = Math.Clamp(this.Scroll, 0, Math.Max(0, this.Rows.Count - this.GetVisibleRows()));

            // The control's range comes from the plan, not from one machine type. A step split between a Heavy
            // Furnace and a plain one occupies the sum of both, and needs at least one of each to start at all.
            List<PlanNode> processing = this.Rows.Where(node => node.Kind == PlanStepKind.Process && node.Assignments.Count > 0).ToList();

            this.MachinesFloor = processing.Count == 0 ? 1 : processing.Max(MachineAllocator.MinimumBudget);
            this.MachinesAvailable = processing.Count == 0
                ? 1
                : processing.Max(node => MachineAllocator.MaximumBudget(node, this.CountUsable));

            // Every machine that can help, unless the player has asked for fewer. Ordering more wine should put
            // more kegs to work without having to raise the count by hand each time.
            this.MaxMachines = this.MachinesPinned
                ? Math.Clamp(this.MaxMachines, this.MachinesFloor, this.MachinesAvailable)
                : this.MachinesAvailable;

            this.Allocations.Clear();
            foreach (PlanNode node in processing)
                this.Allocations[node] = MachineAllocator.Allocate(node, this.MaxMachines, this.CountUsable);

            this.DustAvailable = (int)Math.Min(int.MaxValue, this.Network?.CountById(Devices.JobRunner.FairyDustId) ?? 0);
            this.CanDust = this.DustAvailable > 0
                && processing.Any(node => node.Assignments.Any(assignment => this.MachineRecipes.AllowsFairyDust(assignment.Recipe.MachineId)));
            if (!this.CanDust)
                this.UseFairyDust = false;
            this.UpdateDustEstimate();
        }

        /// <summary>Shows the Fairy Dust the job would set aside as its own row at the foot of the plan.</summary>
        /// <remarks>
        /// Counted the way the job reserves it -- a run per dust on most machines, a quality level per dust in a
        /// cask, from normal -- so the row says exactly what will leave storage. Display only: the dust isn't an
        /// ingredient of any step, so it isn't part of the plan itself.
        /// </remarks>
        private void UpdateDustRow()
        {
            if (this.DustRow != null)
                this.Rows.Remove(this.DustRow);
            this.DustRow = null;

            if (!this.UseFairyDust)
                return;

            int wanted = this.Rows
                .Where(node => node.Kind == PlanStepKind.Process)
                .SelectMany(node => node.Assignments)
                .Where(assignment => this.MachineRecipes.AllowsFairyDust(assignment.Recipe.MachineId))
                .Sum(assignment => assignment.Runs * (assignment.Recipe.IsAging ? Quality.Steps(StardewValley.Object.lowQuality, assignment.Recipe.TargetQuality) : 1));

            int used = Math.Min(wanted, this.DustAvailable);
            if (used <= 0)
                return;

            this.DustRow = new PlanNode
            {
                Kind = PlanStepKind.FromStock,
                ItemId = Devices.JobRunner.FairyDustId,
                DisplayName = GetName(Devices.JobRunner.FairyDustId),
                Requested = used,
                FromStock = used,
                Depth = 1
            };
            this.Rows.Add(this.DustRow);
        }

        /// <summary>Works out which runs the dust in storage would speed up, longest steps first.</summary>
        private void UpdateDustEstimate()
        {
            this.Dusted.Clear();
            this.UpdateDustRow();
            if (!this.UseFairyDust)
                return;

            int dust = this.DustAvailable;
            foreach (PlanNode node in this.Rows.Where(node => node.Kind == PlanStepKind.Process).OrderByDescending(node => node.MinutesPerBatch + (node.DaysPerBatch * CraftPlan.MinutesPerDay)))
            {
                foreach (MachineAssignment assignment in node.Assignments.Where(assignment => this.MachineRecipes.AllowsFairyDust(assignment.Recipe.MachineId)))
                {
                    int perRun = assignment.Recipe.IsAging ? Math.Max(1, Quality.Steps(StardewValley.Object.lowQuality, assignment.Recipe.TargetQuality)) : 1;
                    int covered = Math.Min(assignment.Runs, dust / perRun);
                    if (covered <= 0)
                        continue;

                    this.Dusted[assignment] = covered;
                    dust -= covered * perRun;
                }
            }
        }

        /// <summary>Sets the quantity and re-plans.</summary>
        private void SetQuantity(int value)
        {
            this.Quantity = Math.Clamp(value, 1, 9999);
            this.QuantityBox.Text = this.Quantity.ToString();
            this.LastText = this.QuantityBox.Text;
            this.Replan();
        }

        /// <summary>Sets the step's machine budget, then re-divides it across the split.</summary>
        private void SetMachines(int value)
        {
            this.MaxMachines = Math.Clamp(value, this.MachinesFloor, this.MachinesAvailable);

            this.Allocations.Clear();
            foreach (PlanNode node in this.Rows.Where(node => node.Kind == PlanStepKind.Process && node.Assignments.Count > 0))
                this.Allocations[node] = MachineAllocator.Allocate(node, this.MaxMachines, this.CountUsable);
        }

        /// <summary>Describes a processing step, naming each machine type sharing the work.</summary>
        private string DescribeProcess(PlanNode node)
        {
            Dictionary<MachineAssignment, int> allocation = this.GetAllocation(node);

            string split = string.Join(
                " + ",
                node.Assignments.Select(assignment =>
                    $"{assignment.Recipe.MachineName} x{(allocation.TryGetValue(assignment, out int machines) ? machines : 1)}")
            );

            return $"{split}  ·  {FormatTotal(this.GetStepMinutes(node))}";
        }

        /// <summary>The current allocation for a step, computed on demand if the cache has been cleared.</summary>
        private Dictionary<MachineAssignment, int> GetAllocation(PlanNode node)
        {
            if (!this.Allocations.TryGetValue(node, out Dictionary<MachineAssignment, int> allocation))
                this.Allocations[node] = allocation = MachineAllocator.Allocate(node, this.MaxMachines, this.CountUsable);

            return allocation;
        }

        /// <summary>How many machines a step will actually occupy.</summary>
        /// <remarks>
        /// Bounded three ways: what the network has, what the player allowed, and how many runs there are. Showing
        /// the run count instead would claim six furnaces for a six-run job on a farm with five.
        /// </remarks>
        /// <summary>How long a processing step takes under the current allocation.</summary>
        private int GetStepMinutes(PlanNode node)
        {
            return MachineAllocator.StepMinutes(node, this.GetAllocation(node), this.UseFairyDust ? this.Dusted : null);
        }

        /// <summary>The whole plan's processing time, with each step spread across its machines.</summary>
        private int GetTotalMinutes()
        {
            return this.Rows
                .Where(node => node.Kind == PlanStepKind.Process)
                .Sum(this.GetStepMinutes);
        }

        /// <summary>Counts the machines that could run a recipe, respecting any input filters set on them.</summary>
        private int CountUsable(MachineRecipe recipe)
        {
            return this.Network?.CountUsableMachines(recipe) ?? 0;
        }

        /// <summary>Queues the job and closes.</summary>
        private void Start()
        {
            if (this.Plan?.IsSatisfied != true)
            {
                Game1.playSound("cancel");
                return;
            }

            CraftJob job = this.Jobs.TryQueue(this.TargetId, this.Quantity, this.Network, this.MaxMachines, this.Preferences, out string error, this.TargetQuality, this.UseFairyDust);
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
            // Two rows rather than one. Sharing a row meant the quantity stepper and the machine control ran
            // into each other as soon as either label was more than a word or two.
            int y = this.yPositionOnScreen + 86;
            int x = this.xPositionOnScreen + 200;

            foreach (int step in new[] { -10, -1 })
            {
                this.StepButtons.Add((new Rectangle(x, y, 64, 40), StepAction.Quantity, step));
                x += 70;
            }

            this.QuantityBox.X = x + 6;
            this.QuantityBox.Y = y + 2;
            x = this.QuantityBox.X + this.QuantityBox.Width + 12;

            foreach (int step in new[] { 1, 10 })
            {
                this.StepButtons.Add((new Rectangle(x, y, 64, 40), StepAction.Quantity, step));
                x += 70;
            }

            // Machine allowance on its own row: Min, minus, value, plus, Max.
            int machineY = y + 52;
            int mx = this.xPositionOnScreen + 200;
            this.StepButtons.Add((new Rectangle(mx, machineY, 64, 40), StepAction.MachineMin, 0));
            mx += 70;
            this.StepButtons.Add((new Rectangle(mx, machineY, 44, 40), StepAction.MachineDelta, -1));
            mx += 106;
            this.StepButtons.Add((new Rectangle(mx, machineY, 44, 40), StepAction.MachineDelta, 1));
            mx += 50;
            this.StepButtons.Add((new Rectangle(mx, machineY, 64, 40), StepAction.MachineMax, 0));

            // Fairy Dust sits at the end of the machine row: it's another way of getting more out of the machines.
            this.StepButtons.Add((new Rectangle(this.xPositionOnScreen + 660, machineY - 2, 52, 44), StepAction.FairyDust, 0));

            // Quality on a third row, only for things a cask can age: Normal (no aging), then a star per quality.
            if (this.ShowQuality)
            {
                int qualityY = machineY + 52;
                int qx = this.xPositionOnScreen + 200;
                this.StepButtons.Add((new Rectangle(qx, qualityY, 110, 40), StepAction.Quality, 0));
                qx += 116;
                foreach (int quality in new[] { StardewValley.Object.medQuality, StardewValley.Object.highQuality, StardewValley.Object.bestQuality })
                {
                    this.StepButtons.Add((new Rectangle(qx, qualityY, 64, 40), StepAction.Quality, quality));
                    qx += 70;
                }
            }

            this.QuantityBounds = new ClickableComponent(new Rectangle(this.QuantityBox.X, this.QuantityBox.Y, this.QuantityBox.Width, this.QuantityBox.Height), "quantity");
            this.StartButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + (this.width / 2) - 130, this.yPositionOnScreen + this.height - 88, 260, 64), "start");
        }

        /// <summary>The area the tree is drawn in.</summary>
        private Rectangle GetTreeBounds()
        {
            int top = this.ShowQuality ? 256 : 204;
            return new(this.xPositionOnScreen + 28, this.yPositionOnScreen + top, this.width - 56, this.height - top - 108);
        }

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
            Utility.drawTextWithShadow(b, this.Translations.Get("auto.machines"), Game1.smallFont, new Vector2(this.xPositionOnScreen + 28, this.yPositionOnScreen + 148), Game1.textColor);
            if (this.ShowQuality)
                Utility.drawTextWithShadow(b, this.Translations.Get("auto.quality"), Game1.smallFont, new Vector2(this.xPositionOnScreen + 28, this.yPositionOnScreen + 200), Game1.textColor);

            foreach ((Rectangle bounds, StepAction action, int delta) in this.StepButtons)
            {
                if (action == StepAction.FairyDust && !this.CanDust)
                    continue;

                // Fairy Dust is a picture of the dust itself: lit in a gold frame when on, dimmed when off, with
                // how much storage holds beside it.
                if (action == StepAction.FairyDust)
                {
                    drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, this.UseFairyDust ? Color.Gold : Color.White, 2f, drawShadow: false);
                    this.FairyDustIcon ??= ItemRegistry.Create(Devices.JobRunner.FairyDustId);
                    this.FairyDustIcon.drawInMenu(b, new Vector2(bounds.Center.X - 32, bounds.Center.Y - 32), 0.6f, this.UseFairyDust ? 1f : 0.4f, 0.9f, StackDrawType.Hide, Color.White, drawShadow: false);

                    string count = "x" + this.DustAvailable;
                    Vector2 countSize = Game1.smallFont.MeasureString(count);
                    Utility.drawTextWithShadow(b, count, Game1.smallFont, new Vector2(bounds.Right + 8, bounds.Center.Y - (countSize.Y / 2)), this.UseFairyDust ? Game1.textColor : Game1.textColor * 0.6f);
                    continue;
                }

                bool selected = action == StepAction.Quality && (delta <= 0 ? this.TargetQuality <= 0 : this.TargetQuality == delta);
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, selected ? Color.Wheat : Color.White, 2f, drawShadow: false);

                // A quality button shows its star, like the stars on items, rather than a word.
                if (action == StepAction.Quality && delta > 0)
                {
                    Rectangle star = delta switch
                    {
                        StardewValley.Object.medQuality => new Rectangle(338, 400, 8, 8),
                        StardewValley.Object.highQuality => new Rectangle(346, 400, 8, 8),
                        _ => new Rectangle(346, 392, 8, 8)
                    };
                    b.Draw(Game1.mouseCursors, new Rectangle(bounds.Center.X - 12, bounds.Center.Y - 12, 24, 24), star, Color.White);
                    continue;
                }

                string label = action switch
                {
                    StepAction.MachineMin => this.Translations.Get("auto.min"),
                    StepAction.MachineMax => this.Translations.Get("auto.max"),
                    StepAction.MachineDelta => delta > 0 ? "+" : "-",
                    StepAction.Quality => this.Translations.Get("auto.quality-normal"),
                    _ => delta > 0 ? "+" + delta : delta.ToString()
                };
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), Game1.textColor);
            }

            this.QuantityBox.Draw(b);

            string machines = this.MaxMachines.ToString();
            Vector2 machineSize = Game1.smallFont.MeasureString(machines);
            Utility.drawTextWithShadow(b, machines, Game1.smallFont, new Vector2(this.xPositionOnScreen + 348 - (machineSize.X / 2), this.yPositionOnScreen + 148), Game1.textColor);

            // Say what the ceiling is, so a greyed-out "+" is explained rather than just unresponsive.
            Utility.drawTextWithShadow(
                b,
                this.Translations.Get("auto.machines-available", new { count = this.MachinesAvailable }),
                Game1.smallFont,
                new Vector2(this.xPositionOnScreen + 510, this.yPositionOnScreen + 148),
                Game1.textColor * 0.6f
            );
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

                // Item icon, so the tree can be read at a glance rather than by reading every name.
                DrawIcon(b, GetIcon(node.ItemId, node.RequiredQuality), row.X + indent, row.Y + 4, node.Kind == PlanStepKind.Missing ? 0.4f : 1f);

                string detail = this.DescribeStep(node);
                Vector2 detailSize = Game1.smallFont.MeasureString(detail);
                float detailX = row.Right - detailSize.X - 16;
                Utility.drawTextWithShadow(b, detail, Game1.smallFont, new Vector2(detailX, row.Y + 6), colour * 0.85f);

                // And the machine's own icon next to its name, which is the quickest way to tell a Heavy
                // Furnace step from a plain one.
                bool hasMachineIcon = node.Kind == PlanStepKind.Process && node.MachineRecipe != null;
                if (hasMachineIcon)
                    DrawIcon(b, GetIcon(node.MachineRecipe.MachineId), (int)detailX - 40, row.Y + 4, 1f);

                // The item name gets whatever the step details leave; a deep, long-named row scrolls in that space.
                int labelX = row.X + indent + 38;
                int labelRight = (int)detailX - (hasMachineIcon ? 40 : 0) - 12;
                Marquee.Draw(b, $"{node.Requested}x {node.DisplayName}", Game1.smallFont, new Vector2(labelX, row.Y + 6), labelRight - labelX, colour);
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
            string summary = this.Plan == null ? "" : FormatTotal(this.GetTotalMinutes());
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
                PlanStepKind.Process => this.DescribeProcess(node),
                _ => this.Translations.Get("auto.step-missing")
            };
        }

        /// <summary>Draws a small item icon inside a tree row.</summary>
        private static void DrawIcon(SpriteBatch b, Item icon, int x, int y, float alpha)
        {
            // Sized to the row, with the quality star in its corner: it's how rows of one item differ.
            ItemIcon.Draw(b, icon, new Rectangle(x, y, 32, 32), alpha);
        }

        /// <summary>Builds a drawable icon for an item ID, cached for the life of the menu.</summary>
        private static Item GetIcon(string qualifiedId, int quality = Quality.Any)
        {
            if (string.IsNullOrEmpty(qualifiedId))
                return null;

            // Cached per quality, so a normal and an iridium Starfruit row each get their own star.
            string key = quality > 0 ? $"{qualifiedId}#{quality}" : qualifiedId;
            if (IconCache.TryGetValue(key, out Item cached))
                return cached;

            Item icon = null;
            try
            {
                icon = StockId.Create(qualifiedId);
            }
            catch
            {
                // A category ID or a removed mod's item; the row still reads fine without a picture.
            }

            if (icon != null && quality > 0)
                icon.Quality = quality;

            IconCache[key] = icon;
            return icon;
        }

        /// <summary>Formats a total duration as days, hours and minutes, dropping empty leading units.</summary>
        private static string FormatTotal(int minutes)
        {
            return minutes <= 0 ? "instant" : Durations.Format(minutes);
        }

        /// <summary>Formats a single batch's duration, used in the machine picker.</summary>
        private static string FormatTime(int minutes, int days)
        {
            return FormatTotal(minutes + (days * CraftPlan.MinutesPerDay));
        }

        /// <summary>The display name for a stock ID.</summary>
        private static string GetName(string qualifiedId) => StockId.GetDisplayName(qualifiedId);
    }
}
