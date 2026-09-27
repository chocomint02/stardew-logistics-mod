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
        FairyDust,
        Fertilizer
    }

    internal class AutoCraftMenu : IClickableMenu
    {
        /*********
        ** Fields
        *********/
        private const int MenuWidth = 1000;
        private const int RowHeight = 40;

        /// <summary>How long a split icon shows each substitute when there are several.</summary>
        private const double SubstituteCycleMs = 1500;

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

        /// <summary>How long a plan row takes to slide in, and the gap between one row starting and the next.</summary>
        private const double RowInMs = 200;
        private const double RowStaggerMs = 35;

        /// <summary>When the window opened, for its opening animation.</summary>
        private readonly DateTime OpenedAt = DateTime.UtcNow;

        /// <summary>When each row of the plan first appeared, by its place in the tree, so only new rows animate in.</summary>
        private Dictionary<string, DateTime> RowAppeared = new();

        /// <summary>The highlight behind the row under the cursor, fading in and out.</summary>
        private readonly HoverScales RowHover = new();

        /// <summary>The totals under the plan, counting to each new figure.</summary>
        private readonly AnimatedValue ShownMinutes = new();
        private readonly AnimatedValue ShownValue = new();
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

        /// <summary>The fertilizer laid under crops the job plants, or <c>null</c> for none.</summary>
        private string FertilizerId;

        /// <summary>How much of that fertilizer storage holds.</summary>
        private long FertilizerAvailable;

        /// <summary>Whether the fertilizer choice is shown: the plan plants something, or a fertilizer is chosen.</summary>
        private bool ShowFertilizer;

        /// <summary>Speed-Gro icons for the fertilizer choice, by item ID.</summary>
        private readonly Dictionary<string, Item> FertilizerIcons = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The stock rule being changed, if the menu was opened for one.</summary>
        private readonly StockRule EditingRule;

        /// <summary>Saves a stock rule, with the key of the rule it replaces if it's an edit; <c>null</c> if rules can't be set here.</summary>
        private readonly Action<StockRule, string> OnKeepStocked;

        private ClickableComponent KeepButton;

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
        public AutoCraftMenu(string targetId, string targetName, StorageNetwork network, RecipeIndex crafting, MachineRecipeIndex machineRecipes, JobRunner jobs, ModConfig config, ITranslationHelper translations, Action onClose, StockRule rule = null, Action<StockRule, string> onKeepStocked = null)
        {
            this.EditingRule = rule;
            this.OnKeepStocked = onKeepStocked;
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

            this.QuantityBox = new TextBox(UiTheme.TextBoxTexture(), null, Game1.smallFont, UiTheme.TextColour)
            {
                X = this.xPositionOnScreen + 300,
                Y = this.yPositionOnScreen + 84,
                Width = 140,
                Height = 44,
                Text = "1"
            };

            // Editing a rule starts from what it's set to.
            if (rule != null)
            {
                this.Quantity = Math.Clamp(rule.Target, 1, 9999);
                this.QuantityBox.Text = this.LastText = this.Quantity.ToString();
                this.TargetQuality = this.ShowQuality ? rule.Quality : Quality.Any;
                this.UseFairyDust = rule.UseFairyDust;
                this.FertilizerId = rule.FertilizerId;
                if (rule.MaxMachines > 0)
                {
                    this.MaxMachines = rule.MaxMachines;
                    this.MachinesPinned = true;
                }
            }

            this.BuildButtons();
            this.Replan();
            this.initializeUpperRightCloseButton();
        }

        /// <inheritdoc />
        public override void update(GameTime time)
        {
            base.update(time);
            this.RowHover.Update(time);

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
                if (!bounds.Contains(x, y) || (action == StepAction.FairyDust && !this.CanDust) || (action == StepAction.Fertilizer && !this.ShowFertilizer))
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
                    case StepAction.Fertilizer:
                        // None, then each Speed-Gro from weakest to strongest, then back round.
                        int index = Array.IndexOf(CropMath.SpeedGro, this.FertilizerId);
                        this.FertilizerId = index + 1 < CropMath.SpeedGro.Length ? CropMath.SpeedGro[index + 1] : null;
                        this.Replan();
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

            // A harvest row opens the field it's growing in, or will be planted in.
            if (row?.Harvests.Count > 0)
            {
                IncomingCrop crop = row.Harvests[0];
                HarvesterPlanMenu.OpenFieldView(this.Jobs.NetworkManager, this.Translations, crop.Location, crop.HarvesterTile, this.Jobs.GetReservation);
                return;
            }
            if (row?.Plantings.Count > 0)
            {
                PlannedPlanting planting = row.Plantings[0];
                HarvesterPlanMenu.OpenFieldView(this.Jobs.NetworkManager, this.Translations, planting.Location, planting.HarvesterTile, this.Jobs.GetReservation);
                return;
            }

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

            if (this.StartButton != null && this.StartButton.containsPoint(x, y))
                this.Start();
            else if (this.KeepButton != null && this.KeepButton.containsPoint(x, y))
                this.KeepStocked();
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
            PlanNode hovered = this.Dropdown.IsOpen ? null : this.GetRowAt(x, y);
            this.RowHover.Hover(hovered != null ? this.Rows.IndexOf(hovered) : null);

            if (this.Dropdown.IsOpen)
            {
                this.Dropdown.PerformHover(x, y);
                return;
            }

            if (this.StartButton?.containsPoint(x, y) == true)
            {
                this.HoverText = this.Plan?.IsSatisfied == true
                    ? this.Translations.Get("auto.start-hint")
                    : this.Translations.Get("auto.cannot-start");
                return;
            }

            if (this.KeepButton?.containsPoint(x, y) == true)
            {
                this.HoverText = this.Translations.Get("auto.keep-hint", new { count = this.Quantity, name = this.TargetName });
                return;
            }

            if (this.ShowFertilizer && this.StepButtons.Any(button => button.Action == StepAction.Fertilizer && button.Bounds.Contains(x, y)))
            {
                this.HoverText = this.FertilizerId == null
                    ? this.Translations.Get("auto.fertilizer-off")
                    : this.Translations.Get("auto.fertilizer-on", new { name = GetName(this.FertilizerId), count = this.FertilizerAvailable });
                return;
            }

            // The Fairy Dust button is a picture, so it says what it does on hover.
            if (this.CanDust && this.StepButtons.Any(button => button.Action == StepAction.FairyDust && button.Bounds.Contains(x, y)))
            {
                this.HoverText = this.Translations.Get(this.UseFairyDust ? "auto.fairy-dust-on" : "auto.fairy-dust-off", new { count = this.DustAvailable });
                return;
            }

            PlanNode row = this.GetRowAt(x, y);
            if (row?.Substitutes.Count > 0)
                this.HoverText = this.Translations.Get("auto.substitutes", new { items = string.Join(", ", row.Substitutes.Select(GetName)) });
            else if (row?.Plantings.Count > 0)
                this.HoverText = this.Translations.Get("auto.grow-hint", new { count = row.Plantings.Count });
            else if (row?.Harvests.Count > 0)
                this.HoverText = this.Translations.Get("auto.harvest-hint", new { count = row.Harvests.Count });
            else if (row is { Kind: PlanStepKind.Process } && row.Alternatives.Count > 1)
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
            // In the chosen colour scheme, tooltips included.
            using (UiTheme.Apply())
                this.DrawThemed(b);
        }

        /// <summary>Draws the menu, with the colour scheme in effect.</summary>
        private void DrawThemed(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.5f);

            // The window grows into place as it opens, rising a little as it does.
            bool growing = UiAnimation.PushOpening(b, this.OpenedAt, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
            try
            {
                drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White, 1f, drawShadow: true);

                this.DrawHeader(b);
                this.DrawTree(b);
                this.DrawFooter(b);

                this.upperRightCloseButton?.draw(b);
                this.Dropdown.Draw(b);
            }
            finally
            {
                if (growing)
                    UiBatch.Pop(b);
            }

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
            this.Plan = planner.Plan(this.TargetId, this.Quantity, filterable, this.Preferences, this.CountUsable, this.TargetQuality, this.Jobs.GetIncoming(this.Network), this.Jobs.GetFreeTiles(this.Network), this.FertilizerId);

            this.Rows = this.Plan.Root?.Walk().ToList() ?? new List<PlanNode>();
            this.Scroll = Math.Clamp(this.Scroll, 0, Math.Max(0, this.Rows.Count - this.GetVisibleRows()));
            this.NoteNewRows();

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

            // Speed-Gro only matters when the plan plants something; once chosen it stays, so it can be switched off.
            this.ShowFertilizer = this.FertilizerId != null || this.Rows.Any(node => node.Plantings.Count > 0);
            this.FertilizerAvailable = this.FertilizerId != null ? this.Network?.CountById(this.FertilizerId) ?? 0 : this.Network?.CountById(CropMath.SpeedGro[0]) ?? 0;

            this.DustAvailable = (int)Math.Min(int.MaxValue, this.Network?.CountById(Devices.JobRunner.FairyDustId) ?? 0);
            this.CanDust = this.DustAvailable > 0
                && processing.Any(node => node.Assignments.Any(assignment => this.MachineRecipes.AllowsFairyDust(assignment.Recipe.MachineId)));
            if (!this.CanDust)
                this.UseFairyDust = false;
            this.UpdateDustEstimate();
        }

        /// <summary>Notes which rows are new since the last plan, so they slide in one after another; the rest stay put.</summary>
        /// <remarks>A row is known by its path down the tree, so raising the quantity doesn't replay rows that were already there.</remarks>
        private void NoteNewRows()
        {
            Dictionary<string, DateTime> appeared = new();
            Dictionary<string, int> seen = new();
            DateTime now = DateTime.UtcNow;
            int fresh = 0;

            foreach (string key in this.Rows.Select(RowKey))
            {
                seen[key] = seen.TryGetValue(key, out int count) ? count + 1 : 0;
                string unique = key + "#" + seen[key];

                appeared[unique] = this.RowAppeared.TryGetValue(unique, out DateTime when)
                    ? when
                    : now.AddMilliseconds(fresh++ * RowStaggerMs / Math.Max(0.01, UiAnimation.SpeedFactor));
            }

            this.RowAppeared = appeared;
        }

        /// <summary>Identifies a row by what it is and how deep it sits.</summary>
        private static string RowKey(PlanNode node) => $"{node.Depth}|{node.Kind}|{node.ItemId}|{node.RequiredQuality}";

        /// <summary>How far a row has slid in: 0 before it starts, 1 once it's in place.</summary>
        private float RowProgress(int index)
        {
            if (index < 0 || index >= this.Rows.Count)
                return 1f;

            string key = RowKey(this.Rows[index]);
            int occurrence = this.Rows.Take(index).Count(row => RowKey(row) == key);
            return this.RowAppeared.TryGetValue(key + "#" + occurrence, out DateTime start)
                ? UiAnimation.EaseOut(UiAnimation.Progress(start, RowInMs))
                : 1f;
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
            int processing = this.Rows
                .Where(node => node.Kind == PlanStepKind.Process)
                .Sum(this.GetStepMinutes);

            // Waiting for crops comes first: nothing that needs them can start until they're harvested.
            int wait = this.Rows.Select(node => node.HarvestDays).DefaultIfEmpty(0).Max();
            return processing + (wait > 0 ? Utility.CalculateMinutesUntilMorning(Game1.timeOfDay, wait) : 0);
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

            CraftJob job = this.Jobs.TryQueue(this.TargetId, this.Quantity, this.Network, this.MaxMachines, this.Preferences, out string error, this.TargetQuality, this.UseFairyDust, this.FertilizerId);
            if (job == null)
            {
                Game1.addHUDMessage(new HUDMessage(error, HUDMessage.error_type));
                return;
            }

            Game1.playSound("bigSelect");
            Game1.addHUDMessage(new HUDMessage(this.Translations.Get("auto.queued", new { count = this.Quantity, name = this.TargetName }), HUDMessage.newQuest_type));
            this.exitThisMenu();
        }


        /// <summary>Saves a minimum-stock rule for the item, at the quantity and settings chosen, and closes.</summary>
        private void KeepStocked()
        {
            if (this.OnKeepStocked == null)
                return;

            StockRule rule = new()
            {
                ItemId = this.TargetId,
                Quality = this.TargetQuality,
                Target = this.Quantity,
                UseFairyDust = this.UseFairyDust,
                FertilizerId = this.FertilizerId,
                MaxMachines = this.MachinesPinned ? this.MaxMachines : 0
            };

            this.OnKeepStocked(rule, this.EditingRule?.Key);
            Game1.playSound("bigSelect");
            Game1.addHUDMessage(new HUDMessage(this.Translations.Get("auto.rule-saved", new { count = this.Quantity, name = this.TargetName }), HUDMessage.newQuest_type));
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

            // And Speed-Gro beside it, which does the same for the crops the job plants.
            this.StepButtons.Add((new Rectangle(this.xPositionOnScreen + 800, machineY - 2, 52, 44), StepAction.Fertilizer, 0));

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
            // Start and Keep stocked side by side; editing a rule offers only the save.
            int buttonY = this.yPositionOnScreen + this.height - 88;
            int centre = this.xPositionOnScreen + (this.width / 2);
            if (this.EditingRule != null && this.OnKeepStocked != null)
                this.KeepButton = new ClickableComponent(new Rectangle(centre - 130, buttonY, 260, 64), "keep");
            else if (this.OnKeepStocked != null)
            {
                this.StartButton = new ClickableComponent(new Rectangle(centre - 270, buttonY, 260, 64), "start");
                this.KeepButton = new ClickableComponent(new Rectangle(centre + 10, buttonY, 260, 64), "keep");
            }
            else
                this.StartButton = new ClickableComponent(new Rectangle(centre - 130, buttonY, 260, 64), "start");
        }

        /// <summary>The area the tree is drawn in.</summary>
        /// <remarks>Above the buttons, it leaves two lines: the time and what the job is worth, then any shortfall.</remarks>
        private Rectangle GetTreeBounds()
        {
            int top = this.ShowQuality ? 256 : 204;
            return new(this.xPositionOnScreen + 28, this.yPositionOnScreen + top, this.width - 56, this.height - top - 172);
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
            string title = this.Translations.Get(this.EditingRule != null ? "auto.rule-title" : "auto.title", new { name = this.TargetName });
            Marquee.Draw(b, title, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + 28, this.yPositionOnScreen + 24), this.width - 120, Game1.textColor);

            Utility.drawTextWithShadow(b, this.Translations.Get(this.EditingRule != null ? "auto.keep-quantity" : "auto.quantity"), Game1.smallFont, new Vector2(this.xPositionOnScreen + 28, this.yPositionOnScreen + 96), Game1.textColor);
            Utility.drawTextWithShadow(b, this.Translations.Get("auto.machines"), Game1.smallFont, new Vector2(this.xPositionOnScreen + 28, this.yPositionOnScreen + 148), Game1.textColor);
            if (this.ShowQuality)
                Utility.drawTextWithShadow(b, this.Translations.Get("auto.quality"), Game1.smallFont, new Vector2(this.xPositionOnScreen + 28, this.yPositionOnScreen + 200), Game1.textColor);

            foreach ((Rectangle bounds, StepAction action, int delta) in this.StepButtons)
            {
                if (action == StepAction.FairyDust && !this.CanDust)
                    continue;

                // Speed-Gro works the same way: the chosen one lit in gold, or plain Speed-Gro dimmed when off.
                if (action == StepAction.Fertilizer)
                {
                    if (!this.ShowFertilizer)
                        continue;

                    string shown = this.FertilizerId ?? CropMath.SpeedGro[0];
                    if (!this.FertilizerIcons.TryGetValue(shown, out Item fertilizerIcon))
                        this.FertilizerIcons[shown] = fertilizerIcon = ItemRegistry.Create(shown, allowNull: true);

                    bool on = this.FertilizerId != null;
                    UiTheme.DrawButton(b, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height), on ? Color.Gold : Color.White, 2f);
                    fertilizerIcon?.drawInMenu(b, new Vector2(bounds.Center.X - 32, bounds.Center.Y - 32), 0.6f, on ? 1f : 0.4f, 0.9f, StackDrawType.Hide, Color.White, drawShadow: false);

                    string held = "x" + this.FertilizerAvailable;
                    Vector2 heldSize = Game1.smallFont.MeasureString(held);
                    Utility.drawTextWithShadow(b, held, Game1.smallFont, new Vector2(bounds.Right + 8, bounds.Center.Y - (heldSize.Y / 2)), on ? Game1.textColor : Game1.textColor * 0.6f);
                    continue;
                }

                // Fairy Dust is a picture of the dust itself: lit in a gold frame when on, dimmed when off, with
                // how much storage holds beside it.
                if (action == StepAction.FairyDust)
                {
                    UiTheme.DrawButton(b, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height), this.UseFairyDust ? Color.Gold : Color.White, 2f);
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

                // A soft highlight behind the row under the cursor, in the text's colour so it suits any scheme.
                float glow = (this.RowHover.Get(index) - 1f) / (HoverScales.MaxScale - 1f);
                if (glow > 0)
                    b.Draw(Game1.staminaRect, new Rectangle(row.X + 4, row.Y, row.Width - 8, row.Height - 2), Game1.textColor * (0.08f * glow));

                // A new row slides in from the left as it fades up.
                float shown = this.RowProgress(index);
                if (shown <= 0f)
                    continue;
                int indent = 20 + (node.Depth * 28) - (int)((1f - shown) * 24);

                Color colour = (node.Kind == PlanStepKind.Missing ? UiTheme.Bad : Game1.textColor) * shown;

                // The branch marker makes depth readable without drawing connecting lines.
                if (node.Depth > 0)
                    Utility.drawTextWithShadow(b, "└", Game1.smallFont, new Vector2(row.X + indent - 20, row.Y + 6), Game1.textColor * (0.5f * shown), shadowIntensity: shown);

                // Item icon, so the tree can be read at a glance rather than by reading every name.
                // An ingredient others would stand in for turns through them all, starting with the one asked for.
                string iconId = node.ItemId;
                if (node.Substitutes.Count > 0)
                {
                    int cycle = (int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / SubstituteCycleMs) % (node.Substitutes.Count + 1);
                    if (cycle > 0)
                        iconId = node.Substitutes[cycle - 1];
                }
                DrawIcon(b, GetIcon(iconId, node.RequiredQuality), row.X + indent, row.Y + 4, (node.Kind == PlanStepKind.Missing ? 0.4f : 1f) * shown);

                string detail = this.DescribeStep(node);
                Vector2 detailSize = Game1.smallFont.MeasureString(detail);
                float detailX = row.Right - detailSize.X - 16;
                Utility.drawTextWithShadow(b, detail, Game1.smallFont, new Vector2(detailX, row.Y + 6), colour * 0.85f, shadowIntensity: shown);

                // And the machine's own icon next to its name, which is the quickest way to tell a Heavy
                // Furnace step from a plain one.
                bool hasMachineIcon = node.Kind == PlanStepKind.Process && node.MachineRecipe != null;
                if (hasMachineIcon)
                    DrawIcon(b, GetIcon(node.MachineRecipe.MachineId), (int)detailX - 40, row.Y + 4, shown);

                // The item name gets whatever the step details leave; a deep, long-named row scrolls in that space.
                int labelX = row.X + indent + 38;
                int labelRight = (int)detailX - (hasMachineIcon ? 40 : 0) - 12;
                string label = node.Substitutes.Count > 0 ? CraftPlan.DescribeWithSubstitutes(node, GetName) : node.DisplayName;
                Marquee.Draw(b, $"{node.Requested}x {label}", Game1.smallFont, new Vector2(labelX, row.Y + 6), labelRight - labelX, colour);
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

            // How long it takes, and what the result sells for: in all, and a day over that time.
            if (this.Plan != null)
            {
                this.ShownMinutes.Set(this.GetTotalMinutes());
                int minutes = (int)Math.Round(this.ShownMinutes.Current);
                string summary = this.Translations.Get("auto.time", new { time = FormatTotal(minutes) }) + "   ·   " + this.DescribeValue(minutes);
                Marquee.Draw(b, summary, Game1.smallFont, new Vector2(tree.X + 4, tree.Bottom + 18), tree.Width - 8, Game1.textColor);
            }

            if (this.Plan?.IsSatisfied == false)
            {
                string shortfall = this.Translations.Get("auto.shortfall", new { items = this.Plan.DescribeShortfalls(GetName, max: 3) });
                Marquee.Draw(b, shortfall, Game1.smallFont, new Vector2(tree.X + 4, tree.Bottom + 50), tree.Width - 8, UiTheme.Bad);
            }

            if (this.StartButton != null)
            {
                bool enabled = this.Plan?.IsSatisfied == true;
                Rectangle bounds = this.StartButton.bounds;
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, enabled ? Color.White : Color.Gray, 3f, drawShadow: false);

                string label = this.Translations.Get("auto.start");
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), enabled ? Game1.textColor : Color.DimGray);
            }

            // A rule can be set whether or not the network could make any right now: it waits until it can.
            if (this.KeepButton != null)
            {
                Rectangle bounds = this.KeepButton.bounds;
                UiTheme.DrawButton(b, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height), Color.LightGoldenrodYellow, 3f);

                string label = this.Translations.Get(this.EditingRule != null ? "auto.update-rule" : "auto.keep-stocked");
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), Game1.textColor);
            }
        }

        /// <summary>What one of the target sells for, by quality; worked out once rather than every frame.</summary>
        private readonly Dictionary<int, int?> UnitPrices = new();

        /// <summary>What the finished order sells for, and that over the time it takes.</summary>
        private string DescribeValue(int minutes)
        {
            int quality = this.TargetQuality > 0 ? this.TargetQuality : StardewValley.Object.lowQuality;
            if (!this.UnitPrices.TryGetValue(quality, out int? unit))
                this.UnitPrices[quality] = unit = Selling.UnitPrice(this.TargetId, quality);
            if (unit == null)
                return this.Translations.Get("auto.value-none");

            this.ShownValue.Set(unit.Value * (double)this.Quantity);
            double value = Math.Round(this.ShownValue.Current);
            double days = Selling.Days(minutes);
            return days > 0
                ? this.Translations.Get("auto.value", new { gold = Selling.Gold(value), rate = Selling.Gold(value / days) })
                : this.Translations.Get("auto.value-instant", new { gold = Selling.Gold(value) });
        }

        /// <summary>Describes how a plan step will be supplied.</summary>
        private string DescribeStep(PlanNode node)
        {
            if (node.Plantings.Count > 0)
                return this.Translations.Get("auto.step-grow", new { tiles = node.Plantings.Count, days = node.HarvestDays });

            if (node.Kind == PlanStepKind.FromStock && node.FromHarvest > 0)
            {
                return node.HarvestDays <= 0
                    ? this.Translations.Get("auto.step-harvest-today")
                    : this.Translations.Get("auto.step-harvest", new { days = node.HarvestDays });
            }

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
