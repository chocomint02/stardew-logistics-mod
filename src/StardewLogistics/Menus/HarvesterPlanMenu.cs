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
using StardewValley.GameData.Crops;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace StardewLogistics.Menus
{
    /// <summary>An auto-harvester's area as a grid: to plan seeds and fertilizer, or to see what's growing.</summary>
    /// <remarks>
    /// The grid is the area tile for tile, drawn the way the ground really looks: hoed soil, fertilizer marks,
    /// and crops with the game's own sprites. Planning shows planned crops at a growth stage the player picks and
    /// checks the plan as it's drawn, marking tiles that can't be carried out in red and listing why -- much as a
    /// code editor underlines errors. Preview shows only what's actually in the ground, and changes nothing.
    ///
    /// Planning edits a copy of the machine's settings; only Confirm keeps the changes.
    /// </remarks>
    /// <summary>What painting a tile does in the planning grid.</summary>
    internal enum PaletteMode
    {
        Seeds,
        Fertilizer,
        Automation
    }

    internal class HarvesterPlanMenu : IClickableMenu
    {
        /*********
        ** Fields
        *********/
        private const int PanelWidth = 380;
        private const int PaletteCell = 56;
        private const int PaletteColumns = 6;

        private readonly NetworkManager Networks;
        private readonly ITranslationHelper Translations;
        private readonly GameLocation Location;
        private readonly Vector2 MachineTile;
        private readonly HarvesterSettings Settings;

        /// <summary>The plan as it was when the window opened, to tell which tiles an edit changes.</summary>
        private readonly HarvesterSettings Original;

        /// <summary>Called on close with whether to keep the changes, and the growing crops to clear for them.</summary>
        private readonly Action<bool, IReadOnlyList<Vector2>> OnClose;

        /// <summary>Whether Force change is on: clear crops in the way now, rather than wait for them to finish.</summary>
        private bool Force;

        /// <summary>Whether the "clear crops in the way?" confirmation is showing.</summary>
        private bool ConfirmingForce;

        /// <summary>The growing crops Force change would clear, worked out with the plan check.</summary>
        private List<Vector2> ForceTiles = new();
        private HashSet<Point> ForcePoints = new();
        private Rectangle ForceBack;
        private Rectangle ForceApply;

        /// <summary>Whether this is the read-only view of what's growing, rather than the planner.</summary>
        private readonly bool IsPreview;

        /// <summary>Finds the autocrafting job that has reserved a growing crop, if any.</summary>
        private readonly Func<GameLocation, Vector2, CraftJob> ReservedBy;

        /// <summary>Whether the player confirmed their changes.</summary>
        private bool Confirmed;

        /// <summary>What painting a tile does: plant a seed, lay fertilizer, or set the tile aside for automation.</summary>
        private PaletteMode Mode;
        private bool FertilizerTab => this.Mode == PaletteMode.Fertilizer;
        private Item SelectedSeed;
        private Item SelectedFertilizer;

        /// <summary>The growth stage planned crops are drawn at: 0 is freshly planted.</summary>
        private int Stage;

        /// <summary>The most stages any planned crop has, which caps the stage control.</summary>
        private int MaxStage;

        private List<Item> Seeds = new();
        private List<Item> Fertilizers = new();

        /// <summary>How many of each seed and fertilizer storage holds.</summary>
        private readonly Dictionary<string, long> Stock = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A crop per seed, used only to draw its sprites.</summary>
        private readonly Dictionary<string, Crop> CropSprites = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Soil used only to look up how a fertilizer is drawn.</summary>
        private readonly HoeDirt FertilizerSample = new();

        private readonly List<(Rectangle Bounds, string Action)> Buttons = new();
        private readonly List<(Rectangle Bounds, string SeedId)> ReplantToggles = new();
        private readonly List<(Rectangle Bounds, PlanProblem Problem)> ProblemRows = new();

        /// <summary>Everything wrong with the plan as it stands, and the tiles each affects.</summary>
        private List<PlanProblem> Problems = new();
        private HashSet<Point> ProblemTiles = new();
        private bool ProblemsStale = true;

        /// <summary>The problem the cursor is over, whose tiles are highlighted.</summary>
        private PlanProblem HoveredProblem;

        private Rectangle GridBounds;
        private Rectangle PanelBounds;
        private int Cell;
        private string HoverText = "";
        private bool Painting;

        /// <summary>When the window opened, for it to grow into place.</summary>
        private readonly DateTime OpenedAt = DateTime.UtcNow;

        /// <summary>When the palette tab last changed, which way the new one slides in, and where its highlight glides from.</summary>
        private DateTime ModeChangedAt = DateTime.MinValue;
        private int ModeDirection;
        private Rectangle ModeHighlightFrom;

        /// <summary>When each tile was last painted, so what's put on it pops into place.</summary>
        private readonly Dictionary<Point, DateTime> PaintedAt = new();

        /// <summary>When the Force change confirmation opened, for it to pop up.</summary>
        private DateTime ForceShownAt;

        /// <summary>The outline on the tile under the cursor, and the size of each palette icon, fading as the cursor moves.</summary>
        private readonly HoverScales CellHover = new();
        private readonly HoverScales PaletteHover = new();

        /// <summary>When Force change was last switched, for its switch animating.</summary>
        private DateTime ForceToggledAt = DateTime.MinValue;

        /// <summary>The growth stage crops were drawn at before the last change, when it changed, and which way.</summary>
        private int PreviousStage;
        private DateTime StageChangedAt = DateTime.MinValue;
        private int StageDirection;

        /// <summary>When each stage arrow was last pressed, so it nudges the way it points.</summary>
        private readonly Dictionary<string, DateTime> ArrowPressedAt = new();

        /// <summary>How long a growth stage change and the Force change switch take, at normal speed.</summary>
        private const double StageTransitionMs = 280;
        private const double ForceToggleMs = 320;

        /// <summary>How long switching palette tabs and a painted tile popping in take, at normal speed.</summary>
        private const double ModeTransitionMs = 240;
        private const double PaintPopMs = 220;


        /*********
        ** Public methods
        *********/
        public HarvesterPlanMenu(NetworkManager networks, ITranslationHelper translations, GameLocation location, Vector2 machineTile, HarvesterSettings settings, HarvesterSettings original, bool preview, Action<bool, IReadOnlyList<Vector2>> onClose, Func<GameLocation, Vector2, CraftJob> reservedBy = null)
        {
            this.ReservedBy = reservedBy;
            this.Networks = networks;
            this.Translations = translations;
            this.Location = location;
            this.MachineTile = machineTile;
            this.Settings = settings;
            this.Original = original;
            this.IsPreview = preview;
            this.OnClose = onClose;

            this.RefreshPalette();
            this.Layout();
        }

        /// <inheritdoc />
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            // The confirmation takes every click while it's up.
            if (this.ConfirmingForce)
            {
                if (this.ForceApply.Contains(x, y))
                {
                    this.Confirmed = true;
                    Game1.playSound("cut");
                    this.exitThisMenu();
                }
                else if (this.ForceBack.Contains(x, y))
                {
                    this.ConfirmingForce = false;
                    Game1.playSound("bigDeSelect");
                }
                return;
            }

            if (this.upperRightCloseButton?.containsPoint(x, y) == true)
            {
                this.exitThisMenu();
                return;
            }

            foreach ((Rectangle bounds, string action) in this.Buttons)
            {
                if (bounds.Contains(x, y))
                {
                    this.Press(action);
                    return;
                }
            }

            if (this.IsPreview)
                return;

            foreach ((Rectangle bounds, string seedId) in this.ReplantToggles)
            {
                if (bounds.Contains(x, y))
                {
                    this.Settings.Replant[seedId] = !this.Settings.ShouldReplant(seedId, regrows: false);
                    this.ProblemsStale = true;
                    Game1.playSound("drumkit6");
                    return;
                }
            }

            Item picked = this.GetPaletteItemAt(x, y);
            if (picked != null)
            {
                if (this.FertilizerTab)
                    this.SelectedFertilizer = picked;
                else
                    this.SelectedSeed = picked;
                Game1.playSound("smallSelect");
                return;
            }

            if (this.TryGetCell(x, y, out Point cell))
            {
                this.Painting = true;
                this.Paint(cell);
            }
        }

        /// <inheritdoc />
        public override void leftClickHeld(int x, int y)
        {
            if (this.Painting && this.TryGetCell(x, y, out Point cell))
                this.Paint(cell);
        }

        /// <inheritdoc />
        public override void releaseLeftClick(int x, int y) => this.Painting = false;

        /// <inheritdoc />
        public override void receiveRightClick(int x, int y, bool playSound = true)
        {
            if (!this.IsPreview && !this.ConfirmingForce && this.TryGetCell(x, y, out Point cell))
                this.Erase(cell);
        }

        /// <inheritdoc />
        public override void update(GameTime time)
        {
            base.update(time);
            this.CellHover.Update(time);
            this.PaletteHover.Update(time);

            // Right-drag erases, the way left-drag paints. There's no held-right event, so it's read directly.
            if (!this.IsPreview && !this.ConfirmingForce && Game1.input.GetMouseState().RightButton == ButtonState.Pressed
                && this.TryGetCell(Game1.getMouseX(), Game1.getMouseY(), out Point cell))
                this.Erase(cell);
        }

        /// <inheritdoc />
        public override void performHoverAction(int x, int y)
        {
            base.performHoverAction(x, y);
            this.HoverText = "";
            if (this.ConfirmingForce)
                return;

            this.HoveredProblem = this.ProblemRows.FirstOrDefault(row => row.Bounds.Contains(x, y)).Problem;
            this.CellHover.Hover(this.TryGetCell(x, y, out Point hoveredCell) ? (hoveredCell.Y * 10000) + hoveredCell.X : null);
            int paletteIndex = this.GetPaletteIndexAt(x, y);
            this.PaletteHover.Hover(paletteIndex >= 0 ? paletteIndex : null);

            if (this.Buttons.Any(button => button.Action == "force" && button.Bounds.Contains(x, y)))
            {
                this.HoverText = this.Translations.Get("plan.force-hint");
                return;
            }

            if (this.TryGetCell(x, y, out Point cell))
            {
                this.HoverText = this.DescribeCell(cell);
                return;
            }

            Item item = this.GetPaletteItemAt(x, y);
            if (item != null)
                this.HoverText = this.Translations.Get("plan.palette-hint", new { name = item.DisplayName, count = this.CountInStorage(item.QualifiedItemId) });
        }

        /// <inheritdoc />
        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            this.Layout();
        }

        /// <inheritdoc />
        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();
            this.OnClose?.Invoke(this.Confirmed, this.Confirmed && this.Force ? this.ForceTiles : Array.Empty<Vector2>());
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
            if (this.ProblemsStale)
                this.RecheckPlan();

            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            // The window grows into place as it opens.
            bool growing = UiAnimation.PushOpening(b, this.OpenedAt, new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height));
            try
            {
                drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);

                string title = this.Translations.Get(this.IsPreview ? "plan.preview-title" : "plan.title", new { width = this.Settings.Width, height = this.Settings.Height });
                Utility.drawTextWithShadow(b, title, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + 32, this.yPositionOnScreen + 24), Game1.textColor);

                this.DrawGrid(b);
                if (this.IsPreview)
                    this.DrawPreviewPanel(b);
                else
                {
                    this.DrawPaletteSliding(b);
                    this.DrawProblems(b);
                }
                this.DrawButtons(b);

                base.draw(b);
            }
            finally
            {
                if (growing)
                    UiBatch.Pop(b);
            }

            if (this.ConfirmingForce)
                this.DrawForceConfirmation(b);
            else if (!string.IsNullOrEmpty(this.HoverText))
                drawHoverText(b, Game1.parseText(this.HoverText, Game1.smallFont, 520), Game1.smallFont);
            this.drawMouse(b);
        }

        /// <summary>Asks before Force change destroys anything, saying how much.</summary>
        private void DrawForceConfirmation(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            int width = 640;
            int height = 300;
            Rectangle box = new(this.xPositionOnScreen + ((this.width - width) / 2), this.yPositionOnScreen + ((this.height - height) / 2), width, height);

            // Pops up, overshooting a touch, so it reads as a question that needs an answer.
            float pop = UiAnimation.EaseOutBack(UiAnimation.Progress(this.ForceShownAt, 240));
            bool popping = pop < 1f && UiBatch.Push(b, null, UiBatch.ScaleAbout(new Vector2(box.Center.X, box.Center.Y), 0.85f + (0.15f * pop), Vector2.Zero));
            try
            {
                this.DrawForceBox(b, box);
            }
            finally
            {
                if (popping)
                    UiBatch.Pop(b);
            }
        }

        /// <summary>Draws the Force change confirmation's box and buttons.</summary>
        private void DrawForceBox(SpriteBatch b, Rectangle box)
        {
            drawTextureBox(b, box.X, box.Y, box.Width, box.Height, Color.White);

            Utility.drawTextWithShadow(b, this.Translations.Get("plan.force-title"), Game1.dialogueFont, new Vector2(box.X + 32, box.Y + 28), UiTheme.Bad);

            string body = this.ForceTiles.Count > 0
                ? this.Translations.Get("plan.force-body", new { count = this.ForceTiles.Count })
                : this.Translations.Get("plan.force-none");
            string wrapped = Game1.parseText(body, Game1.smallFont, box.Width - 64);
            Utility.drawTextWithShadow(b, wrapped, Game1.smallFont, new Vector2(box.X + 32, box.Y + 96), Game1.textColor);

            this.ForceBack = new Rectangle(box.X + 32, box.Bottom - 88, 220, 56);
            this.ForceApply = new Rectangle(box.Right - 32 - 260, box.Bottom - 88, 260, 56);
            foreach ((Rectangle bounds, string key, Color tint) in new[] { (this.ForceBack, "plan.button-back", Color.White), (this.ForceApply, "plan.button-apply", new Color(255, 120, 110)) })
            {
                UiTheme.DrawButton(b, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height), tint, 2f);
                string label = this.Translations.Get(key);
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), Game1.textColor);
            }
        }


        /*********
        ** Private methods: editing
        *********/
        /// <summary>Applies the current palette choice to a tile.</summary>
        /// <param name="cell">The tile.</param>
        /// <param name="delayMs">How long to wait before what's painted pops in, so a fill ripples out rather than landing at once.</param>
        private void Paint(Point cell, double delayMs = 0)
        {
            if (this.IsPreview)
                return;

            this.Settings.Tiles.TryGetValue(cell, out TilePlan plan);
            plan ??= new TilePlan();

            if (this.Mode == PaletteMode.Automation)
            {
                // Set aside for autocrafting, which brings its own seed: whatever was planned here goes.
                if (plan.Automation)
                    return;

                plan = new TilePlan { Automation = true };
            }
            else if (this.Mode == PaletteMode.Seeds && this.SelectedSeed != null)
            {
                if (string.Equals(plan.SeedId, this.SelectedSeed.QualifiedItemId, StringComparison.OrdinalIgnoreCase))
                    return;

                plan.SeedId = this.SelectedSeed.QualifiedItemId;
                plan.Planted = false; // a new crop starts with its own first planting
                plan.Automation = false;
            }
            else if (this.FertilizerTab && this.SelectedFertilizer != null)
            {
                // An automation tile's fertilizer is the job's to choose.
                if (plan.Automation || string.Equals(plan.FertilizerId, this.SelectedFertilizer.QualifiedItemId, StringComparison.OrdinalIgnoreCase))
                    return;

                plan.FertilizerId = this.SelectedFertilizer.QualifiedItemId;
            }
            else
                return;

            this.Settings.Tiles[cell] = plan;
            this.PaintedAt[cell] = DateTime.UtcNow.AddMilliseconds(delayMs / Math.Max(0.01, UiAnimation.SpeedFactor));
            this.OnPlanChanged();
        }

        /// <summary>Clears a tile: its fertilizer on the fertilizer tab, otherwise its seed and fertilizer both.</summary>
        private void Erase(Point cell)
        {
            if (!this.Settings.Tiles.TryGetValue(cell, out TilePlan plan))
                return;

            if (this.FertilizerTab && plan.SeedId != null)
                plan.FertilizerId = null;
            else if (this.Mode == PaletteMode.Automation && !plan.Automation)
                return; // erasing automation leaves a planned crop alone
            else
                this.Settings.Tiles.Remove(cell);

            this.OnPlanChanged();
        }

        private void OnPlanChanged()
        {
            this.ProblemsStale = true;
            this.RecountStages();
        }

        /// <summary>Handles a button.</summary>
        private void Press(string action)
        {
            switch (action)
            {
                case "tab-seeds": this.SwitchMode(PaletteMode.Seeds); break;
                case "tab-fertilizer": this.SwitchMode(PaletteMode.Fertilizer); break;
                case "tab-automation": this.SwitchMode(PaletteMode.Automation); break;
                case "stage-down":
                case "stage-up":
                    int stage = Math.Clamp(this.Stage + (action == "stage-up" ? 1 : -1), 0, this.MaxStage);
                    this.ArrowPressedAt[action] = DateTime.UtcNow;
                    if (stage != this.Stage)
                    {
                        // From what's showing now, so pressing quickly doesn't jump.
                        this.PreviousStage = this.StageTransitionProgress() < 1f ? this.PreviousStage : this.Stage;
                        this.StageDirection = Math.Sign(stage - this.Stage);
                        this.StageChangedAt = DateTime.UtcNow;
                        this.Stage = stage;
                    }
                    break;
                case "fill":
                    for (int y = 0; y < this.Settings.Height; y++)
                    {
                        for (int x = 0; x < this.Settings.Width; x++)
                            this.Paint(new Point(x, y), (x + y) * 18);
                    }
                    break;
                case "clear":
                    this.Settings.Tiles.Clear();
                    this.OnPlanChanged();
                    break;
                case "force":
                    this.Force = !this.Force;
                    this.ForceToggledAt = DateTime.UtcNow;
                    this.ProblemsStale = true;
                    Game1.playSound(this.Force ? "trashcanlid" : "drumkit6");
                    return;
                case "confirm":
                    if (this.Force)
                    {
                        // Say exactly what will be destroyed, and wait for a second yes.
                        this.RecheckPlan();
                        this.ConfirmingForce = true;
                        this.ForceShownAt = DateTime.UtcNow;
                        Game1.playSound("bigSelect");
                        return;
                    }

                    this.Confirmed = true;
                    Game1.playSound("coin");
                    this.exitThisMenu();
                    return;
                case "cancel":
                case "close":
                    this.exitThisMenu();
                    return;
            }

            Game1.playSound("drumkit6");
        }

        /// <summary>Works out how many growth stages the stage control needs.</summary>
        private void RecountStages()
        {
            this.MaxStage = this.Settings.Tiles.Values
                .Where(plan => plan.SeedId != null)
                .Select(plan => this.GetCropSprite(plan.SeedId)?.phaseDays.Count - 1 ?? 0)
                .DefaultIfEmpty(0)
                .Max();
            this.Stage = Math.Min(this.Stage, this.MaxStage);
        }

        /// <summary>Checks the plan against the ground and storage.</summary>
        private void RecheckPlan()
        {
            this.ProblemsStale = false;
            StorageNetwork network = this.Networks.GetNetworkAt(this.Location, this.MachineTile);
            this.Problems = HarvesterPlanCheck.Check(this.Settings, this.Location, this.MachineTile, network, GetName, (key, tokens) => this.Translations.Get(key, tokens), this.Original, this.Force);
            this.ProblemTiles = new HashSet<Point>(this.Problems.SelectMany(problem => problem.Tiles));

            var inTheWay = this.Force
                ? HarvesterPlanCheck.CropsInTheWay(this.Settings, this.Original, this.Location, this.MachineTile).ToList()
                : new List<(Vector2 Tile, Point Point, HoeDirt Soil)>();
            this.ForceTiles = inTheWay.Select(entry => entry.Tile).ToList();
            this.ForcePoints = new HashSet<Point>(inTheWay.Select(entry => entry.Point));
        }


        /*********
        ** Private methods: palette
        *********/
        /// <summary>Gathers the seeds and fertilizer on offer, with how many storage holds.</summary>
        /// <remarks>
        /// Seeds in the player's bag are offered too, since that's usually where they are, but the count shows what
        /// storage holds -- which is all the harvester can use -- and the plan check says when they need moving.
        /// </remarks>
        private void RefreshPalette()
        {
            List<Item> candidates = new();
            this.Stock.Clear();

            StorageNetwork network = this.Networks.GetNetworkAt(this.Location, this.MachineTile);
            if (network != null)
            {
                foreach (NetworkItemStack entry in network.Aggregate())
                {
                    candidates.Add(entry.Sample);
                    string id = entry.Sample?.QualifiedItemId;
                    if (id != null)
                        this.Stock[id] = this.Stock.TryGetValue(id, out long have) ? have + entry.Count : entry.Count;
                }
            }

            candidates.AddRange(Game1.player.Items.Where(item => item != null));
            foreach (TilePlan plan in this.Settings.Tiles.Values)
            {
                if (plan.SeedId != null)
                    candidates.Add(ItemRegistry.Create(plan.SeedId, allowNull: true));
                if (plan.FertilizerId != null)
                    candidates.Add(ItemRegistry.Create(plan.FertilizerId, allowNull: true));
            }

            List<Item> distinct = candidates
                .Where(item => item != null)
                .GroupBy(item => item.QualifiedItemId, StringComparer.OrdinalIgnoreCase)
                .Select(group => { Item one = group.First().getOne(); one.Quality = SObject.lowQuality; return one; })
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            this.Seeds = distinct.Where(item => item.Category == SObject.SeedsCategory && CropMath.GetData(item.QualifiedItemId) != null).ToList();
            this.Fertilizers = distinct.Where(item => item.Category == SObject.fertilizerCategory).ToList();

            this.SelectedSeed ??= this.Seeds.FirstOrDefault();
            this.SelectedFertilizer ??= this.Fertilizers.FirstOrDefault();
            this.RecountStages();
        }

        private long CountInStorage(string itemId) => itemId != null && this.Stock.TryGetValue(itemId, out long count) ? count : 0;

        private List<Item> PaletteItems => this.Mode switch
        {
            PaletteMode.Seeds => this.Seeds,
            PaletteMode.Fertilizer => this.Fertilizers,
            _ => new List<Item>()
        };

        /// <summary>The palette item under a screen position.</summary>
        private Item GetPaletteItemAt(int x, int y)
        {
            if (this.IsPreview)
                return null;

            List<Item> items = this.PaletteItems;
            for (int i = 0; i < items.Count; i++)
            {
                if (this.GetPaletteCell(i).Contains(x, y))
                    return items[i];
            }
            return null;
        }

        /// <summary>The bounds of one palette cell.</summary>
        private Rectangle GetPaletteCell(int index)
        {
            int top = this.PanelBounds.Y + 64;
            return new Rectangle(this.PanelBounds.X + ((index % PaletteColumns) * PaletteCell), top + ((index / PaletteColumns) * PaletteCell), PaletteCell - 6, PaletteCell - 6);
        }


        /*********
        ** Private methods: drawing
        *********/
        /// <summary>Draws the area, tile by tile, as the ground looks.</summary>
        private void DrawGrid(SpriteBatch b)
        {
            Rectangle area = this.Settings.GetArea(this.MachineTile);
            b.Draw(Game1.staminaRect, this.GridBounds, new Color(40, 30, 20));
            Texture2D dirt = Game1.content.Load<Texture2D>("TerrainFeatures\\hoeDirt");

            for (int y = 0; y < this.Settings.Height; y++)
            {
                for (int x = 0; x < this.Settings.Width; x++)
                {
                    Point point = new(x, y);
                    Rectangle cell = this.GetCellBounds(x, y);
                    Vector2 tile = new(area.X + x, area.Y + y);
                    this.Location.terrainFeatures.TryGetValue(tile, out TerrainFeature feature);
                    HoeDirt soil = feature as HoeDirt;
                    this.Settings.Tiles.TryGetValue(point, out TilePlan plan);

                    // Ground: hoed soil where it's tilled (or will be), grass or floor where it isn't.
                    bool showSoil = soil != null || (!this.IsPreview && plan?.SeedId != null && HarvesterPlanCheck.CanTill(this.Location, tile, feature));
                    if (showSoil)
                        b.Draw(dirt, cell, this.GetDirtSource(point, area), Color.White);
                    else
                        b.Draw(Game1.staminaRect, cell, this.GetGroundColour(tile, feature));

                    // What's just been painted pops into place, overshooting a touch.
                    Rectangle painted = cell;
                    if (this.PaintedAt.TryGetValue(point, out DateTime paintedAt))
                    {
                        float pop = UiAnimation.Progress(paintedAt, PaintPopMs);
                        if (pop >= 1f)
                            this.PaintedAt.Remove(point);
                        else
                            painted = UiAnimation.Scale(cell, Math.Max(0f, UiAnimation.EaseOutBack(pop)));
                    }

                    // Fertilizer, as its mark on the soil.
                    string fertilizer = this.IsPreview ? CropMath.FertilizerOf(soil) : plan?.FertilizerId ?? CropMath.FertilizerOf(soil);
                    if (!string.IsNullOrEmpty(fertilizer))
                        this.DrawFertilizer(b, fertilizer, plan?.FertilizerId != null ? painted : cell);

                    // Set aside for automation: shaded gold, the colour of everything autocrafting has claimed.
                    if (plan?.Automation == true)
                        b.Draw(Game1.staminaRect, painted, Color.Gold * 0.35f);

                    // The crop: what's growing in preview, what's planned when planning.
                    if (this.IsPreview)
                    {
                        if (soil?.crop != null)
                            DrawRealCrop(b, soil.crop, cell, tile);
                    }
                    else if (plan?.SeedId != null)
                        this.DrawPlannedCrop(b, plan.SeedId, painted);

                    // The tile under the cursor, outlined softly.
                    float hover = (this.CellHover.Get((y * 10000) + x) - 1f) / (HoverScales.MaxScale - 1f);
                    if (hover > 0)
                        this.DrawOutline(b, cell, Color.White * (0.7f * hover), 2);

                    if (tile == this.MachineTile)
                        this.DrawOutline(b, cell, Color.SteelBlue, 3);

                    // A crop an autocrafting job is waiting on, or a tile it's about to plant, outlined in gold.
                    if (this.ReservedBy?.Invoke(this.Location, tile) != null)
                        this.DrawOutline(b, cell, Color.Gold, 3);

                    // Crops Force change will clear, outlined so it's clear what goes.
                    if (!this.IsPreview && this.ForcePoints.Contains(point))
                        this.DrawOutline(b, cell, Color.OrangeRed, 3);

                    if (!this.IsPreview && this.ProblemTiles.Contains(point))
                    {
                        b.Draw(Game1.staminaRect, cell, Color.Red * 0.35f);
                        if (this.HoveredProblem?.Tiles.Contains(point) == true)
                            this.DrawOutline(b, cell, Color.Yellow, 2);
                    }
                }
            }
        }

        /// <summary>The hoed-soil sprite for a tile, joined to hoed neighbours the way the game joins it.</summary>
        private Rectangle GetDirtSource(Point point, Rectangle area)
        {
            bool IsSoil(int x, int y)
            {
                if (x < 0 || y < 0 || x >= this.Settings.Width || y >= this.Settings.Height)
                    return false;

                Vector2 tile = new(area.X + x, area.Y + y);
                if (this.Location.terrainFeatures.TryGetValue(tile, out TerrainFeature feature))
                    return feature is HoeDirt;

                return !this.IsPreview && this.Settings.Tiles.TryGetValue(new Point(x, y), out TilePlan plan) && plan.SeedId != null;
            }

            byte mask = 0;
            if (IsSoil(point.X, point.Y - 1)) mask |= 1;
            if (IsSoil(point.X + 1, point.Y)) mask |= 2;
            if (IsSoil(point.X, point.Y + 1)) mask |= 4;
            if (IsSoil(point.X - 1, point.Y)) mask |= 8;

            int index = HoeDirt.drawGuide != null && HoeDirt.drawGuide.TryGetValue(mask, out int found) ? found : 0;
            return new Rectangle(index % 4 * 16, index / 4 * 16, 16, 16);
        }

        /// <summary>The colour of ground that isn't hoed: open grass, something in the way, or somewhere that can't be tilled.</summary>
        private Color GetGroundColour(Vector2 tile, TerrainFeature feature)
        {
            if (feature != null || this.Location.Objects.ContainsKey(tile))
                return new Color(92, 70, 56);

            return this.Location.doesTileHaveProperty((int)tile.X, (int)tile.Y, "Diggable", "Back") != null
                ? new Color(96, 132, 60)
                : new Color(74, 74, 74);
        }

        /// <summary>Draws a fertilizer's mark on a cell, as it looks on hoed soil.</summary>
        private void DrawFertilizer(SpriteBatch b, string fertilizerId, Rectangle cell)
        {
            try
            {
                this.FertilizerSample.fertilizer.Value = fertilizerId;
                b.Draw(Game1.mouseCursors, cell, this.FertilizerSample.GetFertilizerSourceRect(), Color.White);
            }
            catch
            {
                // An unknown fertilizer has no mark; the tooltip still names it.
            }
        }

        /// <summary>Draws a planned crop at the chosen growth stage.</summary>
        private void DrawPlannedCrop(SpriteBatch b, string seedId, Rectangle cell)
        {
            Crop crop = this.GetCropSprite(seedId);
            if (crop == null)
                return;

            int last = crop.phaseDays.Count - 1;
            float t = this.StageTransitionProgress();
            if (t >= 1f)
            {
                crop.currentPhase.Value = Math.Min(this.Stage, last);
                DrawCropSprite(b, crop, cell, 1);
                return;
            }

            // Changing stage: the old sprite fades as the new one grows up out of the soil into its place.
            int from = Math.Min(this.PreviousStage, last);
            int to = Math.Min(this.Stage, last);
            if (from == to)
            {
                crop.currentPhase.Value = to;
                DrawCropSprite(b, crop, cell, 1);
                return;
            }

            float eased = UiAnimation.EaseOut(t);
            crop.currentPhase.Value = from;
            DrawCropSprite(b, crop, cell, 1, 1f - eased, 1f);
            crop.currentPhase.Value = to;
            DrawCropSprite(b, crop, cell, 1, eased, 0.75f + (0.25f * UiAnimation.EaseOutBack(t)));
        }

        /// <summary>How far through a growth stage change the grid is: 1 once it's done.</summary>
        private float StageTransitionProgress() => UiAnimation.Progress(this.StageChangedAt, StageTransitionMs);

        /// <summary>What a growth stage is called beside the arrows.</summary>
        private string DescribeStage(int stage)
        {
            return stage == 0
                ? this.Translations.Get("plan.stage-seed")
                : stage >= this.MaxStage && this.MaxStage > 0
                    ? this.Translations.Get("plan.stage-grown")
                    : this.Translations.Get("plan.stage", new { stage });
        }

        /// <summary>Draws a crop that's really growing, as it is now.</summary>
        private static void DrawRealCrop(SpriteBatch b, Crop crop, Rectangle cell, Vector2 tile)
        {
            DrawCropSprite(b, crop, cell, (int)((tile.X * 7) + (tile.Y * 11)));
        }

        /// <summary>Draws a crop's current sprite fitted to a cell.</summary>
        /// <param name="alpha">How opaque to draw it.</param>
        /// <param name="scale">How big to draw it, standing on the bottom of the cell as it grows.</param>
        private static void DrawCropSprite(SpriteBatch b, Crop crop, Rectangle cell, int variation, float alpha = 1f, float scale = 1f)
        {
            try
            {
                CropData data = crop.GetData();
                Texture2D texture = Game1.content.Load<Texture2D>(string.IsNullOrEmpty(data?.Texture) ? "TileSheets\\crops" : data.Texture);

                // Crop sprites are 16x32 and stand on their tile: fit the height to the cell, bottom-aligned.
                int height = (int)(cell.Height * scale);
                int width = height / 2;
                b.Draw(texture, new Rectangle(cell.Center.X - (width / 2), cell.Bottom - height, width, height), crop.getSourceRect(variation), Color.White * alpha);
            }
            catch
            {
                // A crop that can't be drawn still shows in the tooltip.
            }
        }

        /// <summary>Draws a rectangle's outline.</summary>
        private void DrawOutline(SpriteBatch b, Rectangle area, Color colour, int thickness)
        {
            b.Draw(Game1.staminaRect, new Rectangle(area.X, area.Y, area.Width, thickness), colour);
            b.Draw(Game1.staminaRect, new Rectangle(area.X, area.Bottom - thickness, area.Width, thickness), colour);
            b.Draw(Game1.staminaRect, new Rectangle(area.X, area.Y, thickness, area.Height), colour);
            b.Draw(Game1.staminaRect, new Rectangle(area.Right - thickness, area.Y, thickness, area.Height), colour);
        }

        /// <summary>Draws the palette, sliding and fading in from the side of its tab when the tab changes.</summary>
        private void DrawPaletteSliding(SpriteBatch b)
        {
            float t = UiAnimation.EaseOut(UiAnimation.Progress(this.ModeChangedAt, ModeTransitionMs));
            if (t >= 1f)
            {
                this.DrawPalette(b);
                return;
            }

            Rectangle area = new(this.PanelBounds.X - 8, this.PanelBounds.Y + 56, this.PanelBounds.Width + 16, this.ProblemsTop() - this.PanelBounds.Y - 60);
            bool sliding = UiBatch.Push(b, area, new Vector2((1f - t) * 40 * this.ModeDirection, 0));
            try
            {
                this.DrawPalette(b);
            }
            finally
            {
                if (sliding)
                    UiBatch.Pop(b);
            }

            // The window's own panel over it, fading away, so the new tab fades up from the background.
            if (UiBatch.Push(b, area, Vector2.Zero))
            {
                try
                {
                    // Without the window's drop shadow, which doesn't fade and would darken the panel until the end.
                    drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White * (1f - t), 1f, drawShadow: false);
                }
                finally
                {
                    UiBatch.Pop(b);
                }
            }
        }

        /// <summary>Changes the palette tab, starting its transition.</summary>
        private void SwitchMode(PaletteMode mode)
        {
            if (mode == this.Mode)
                return;

            this.ModeHighlightFrom = this.GetModeHighlight();
            this.ModeDirection = Math.Sign((int)mode - (int)this.Mode);
            this.ModeChangedAt = DateTime.UtcNow;
            this.Mode = mode;
        }

        /// <summary>Where the active palette tab's highlight is drawn: gliding from the last tab to this one.</summary>
        private Rectangle GetModeHighlight()
        {
            string action = this.Mode switch
            {
                PaletteMode.Fertilizer => "tab-fertilizer",
                PaletteMode.Automation => "tab-automation",
                _ => "tab-seeds"
            };
            Rectangle target = this.Buttons.FirstOrDefault(button => button.Action == action).Bounds;
            float t = UiAnimation.EaseOut(UiAnimation.Progress(this.ModeChangedAt, ModeTransitionMs));
            if (t >= 1f || this.ModeHighlightFrom.IsEmpty)
                return target;

            return new Rectangle(
                (int)MathHelper.Lerp(this.ModeHighlightFrom.X, target.X, t),
                (int)MathHelper.Lerp(this.ModeHighlightFrom.Y, target.Y, t),
                (int)MathHelper.Lerp(this.ModeHighlightFrom.Width, target.Width, t),
                (int)MathHelper.Lerp(this.ModeHighlightFrom.Height, target.Height, t)
            );
        }

        /// <summary>The index of the palette cell under a screen position, or -1.</summary>
        private int GetPaletteIndexAt(int x, int y)
        {
            if (this.IsPreview || this.Mode == PaletteMode.Automation)
                return -1;

            List<Item> items = this.PaletteItems;
            for (int i = 0; i < items.Count; i++)
            {
                if (this.GetPaletteCell(i).Contains(x, y))
                    return i;
            }
            return -1;
        }

        /// <summary>Draws the palette, with how many of each storage holds, and the replant choices.</summary>
        private void DrawPalette(SpriteBatch b)
        {
            if (this.Mode == PaletteMode.Automation)
            {
                this.DrawAutomationHelp(b);
                return;
            }

            List<Item> items = this.PaletteItems;
            Item selected = this.FertilizerTab ? this.SelectedFertilizer : this.SelectedSeed;

            for (int i = 0; i < items.Count; i++)
            {
                Rectangle cell = this.GetPaletteCell(i);
                bool isSelected = selected != null && items[i].QualifiedItemId == selected.QualifiedItemId;
                b.Draw(Game1.staminaRect, cell, isSelected ? Color.Gold * 0.6f : Color.Wheat * 0.35f);
                ItemIcon.Draw(b, items[i], this.PaletteHover.Grow(i, new Rectangle(cell.X + 3, cell.Y + 3, cell.Width - 6, cell.Height - 6)), 1f, showQuality: false);

                // The count storage holds; red when there's none the harvester could use.
                long count = this.CountInStorage(items[i].QualifiedItemId);
                string text = count > 999 ? "999+" : count.ToString();
                Vector2 size = Game1.smallFont.MeasureString(text) * 0.55f;
                Rectangle plate = new(cell.Right - (int)size.X - 6, cell.Bottom - (int)size.Y - 2, (int)size.X + 6, (int)size.Y + 2);
                b.Draw(Game1.staminaRect, plate, new Color(26, 22, 32) * 0.8f);
                b.DrawString(Game1.smallFont, text, new Vector2(plate.X + 3, plate.Y + 1), count > 0 ? Color.White : new Color(255, 120, 110), 0f, Vector2.Zero, 0.55f, SpriteEffects.None, 1f);
            }

            if (items.Count == 0)
                Marquee.Draw(b, this.Translations.Get("plan.palette-empty"), Game1.smallFont, new Vector2(this.PanelBounds.X, this.PanelBounds.Y + 64), this.PanelBounds.Width, Game1.textColor * 0.6f);

            // Replant choices: only crops that don't regrow need one. A regrowing crop keeps producing on its own.
            int rows = (int)Math.Ceiling(Math.Max(1, items.Count) / (double)PaletteColumns);
            int y = this.PanelBounds.Y + 64 + (rows * PaletteCell) + 16;

            this.ReplantToggles.Clear();
            List<string> oneHarvest = this.Settings.Tiles.Values
                .Select(plan => plan.SeedId)
                .Where(id => id != null && !CropMath.Regrows(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (oneHarvest.Count == 0)
                return;

            Marquee.Draw(b, this.Translations.Get("plan.replant-heading"), Game1.smallFont, new Vector2(this.PanelBounds.X, y), this.PanelBounds.Width, Game1.textColor);
            y += 34;

            foreach (string seedId in oneHarvest)
            {
                if (y > this.ProblemsTop() - 48)
                    break;

                bool replant = this.Settings.ShouldReplant(seedId, regrows: false);
                Rectangle row = new(this.PanelBounds.X, y, this.PanelBounds.Width, 40);
                this.ReplantToggles.Add((row, seedId));

                ItemIcon.Draw(b, ItemRegistry.Create(seedId, allowNull: true), new Rectangle(row.X + 2, row.Y + 4, 32, 32), 1f, showQuality: false);

                Rectangle pill = new(row.Right - 110, row.Y + 2, 110, 36);
                UiTheme.DrawButton(b, new Rectangle(pill.X, pill.Y, pill.Width, pill.Height), replant ? Color.LightGreen : Color.White, 2f);
                string state = this.Translations.Get(replant ? "plan.replant-yes" : "plan.replant-no");
                Vector2 stateSize = Game1.smallFont.MeasureString(state);
                Utility.drawTextWithShadow(b, state, Game1.smallFont, new Vector2(pill.Center.X - (stateSize.X / 2), pill.Center.Y - (stateSize.Y / 2)), Game1.textColor);

                Marquee.Draw(b, GetName(seedId), Game1.smallFont, new Vector2(row.X + 42, row.Y + 8), pill.X - row.X - 50, Game1.textColor);
                y += 44;
            }
        }

        /// <summary>Explains automation tiles in place of the palette, with how many are set aside.</summary>
        private void DrawAutomationHelp(SpriteBatch b)
        {
            int y = this.PanelBounds.Y + 64;
            int width = this.PanelBounds.Width;

            string help = Game1.parseText(this.Translations.Get("plan.automation-help"), Game1.smallFont, width);
            Utility.drawTextWithShadow(b, help, Game1.smallFont, new Vector2(this.PanelBounds.X, y), Game1.textColor);
            y += (int)Game1.smallFont.MeasureString(help).Y + 20;

            // A swatch per state, so the grid's colours read without hovering.
            (Color Fill, bool Outline, string Key)[] legend =
            {
                (Color.Gold * 0.35f, false, "plan.automation-legend-free"),
                (Color.Gold * 0.35f, true, "plan.automation-legend-used")
            };
            Texture2D dirt = Game1.content.Load<Texture2D>("TerrainFeatures\\hoeDirt");
            foreach ((Color fill, bool outline, string key) in legend)
            {
                // Drawn exactly as the grid draws a tile, hoed soil under the gold, so the colours match.
                Rectangle swatch = new(this.PanelBounds.X, y + 2, 28, 28);
                b.Draw(dirt, swatch, new Rectangle(16, 16, 16, 16), Color.White);
                b.Draw(Game1.staminaRect, swatch, fill);
                if (outline)
                    this.DrawOutline(b, swatch, Color.Gold, 3);
                Marquee.Draw(b, this.Translations.Get(key), Game1.smallFont, new Vector2(swatch.Right + 10, y), width - 40, Game1.textColor);
                y += 38;
            }

            int count = this.Settings.Tiles.Values.Count(plan => plan.Automation);
            Marquee.Draw(b, this.Translations.Get("plan.automation-count", new { count }), Game1.smallFont, new Vector2(this.PanelBounds.X, y + 8), width, Game1.textColor * 0.8f);
        }

        /// <summary>The top of the problems panel.</summary>
        private int ProblemsTop() => this.PanelBounds.Bottom - 230;

        /// <summary>Lists what's wrong with the plan, like a code editor's problems list.</summary>
        private void DrawProblems(SpriteBatch b)
        {
            int top = this.ProblemsTop();
            Rectangle box = new(this.PanelBounds.X, top, this.PanelBounds.Width, this.PanelBounds.Bottom - top);
            b.Draw(Game1.staminaRect, box, new Color(60, 40, 30) * 0.12f);

            this.ProblemRows.Clear();
            if (this.Problems.Count == 0)
            {
                Marquee.Draw(b, this.Translations.Get("plan.no-problems"), Game1.smallFont, new Vector2(box.X + 10, box.Y + 10), box.Width - 20, UiTheme.Good);
                return;
            }

            Marquee.Draw(b, this.Translations.Get("plan.problems", new { count = this.Problems.Count }), Game1.smallFont, new Vector2(box.X + 10, box.Y + 8), box.Width - 20, UiTheme.Bad);

            int y = box.Y + 44;
            foreach (PlanProblem problem in this.Problems)
            {
                if (y > box.Bottom - 34)
                    break;

                Rectangle row = new(box.X + 4, y - 2, box.Width - 8, 34);
                this.ProblemRows.Add((row, problem));
                if (this.HoveredProblem == problem)
                    b.Draw(Game1.staminaRect, row, Color.Yellow * 0.25f);

                b.Draw(Game1.staminaRect, new Rectangle(box.X + 10, y + 8, 12, 12), UiTheme.Bad);
                Marquee.Draw(b, problem.Message, Game1.smallFont, new Vector2(box.X + 30, y), box.Width - 40, Game1.textColor);
                y += 36;
            }
        }

        /// <summary>The side panel for the preview: what's growing, summarised.</summary>
        private void DrawPreviewPanel(SpriteBatch b)
        {
            Rectangle area = this.Settings.GetArea(this.MachineTile);
            Dictionary<string, (int Count, int Soonest)> growing = new(StringComparer.OrdinalIgnoreCase);

            for (int y = area.Top; y < area.Bottom; y++)
            {
                for (int x = area.Left; x < area.Right; x++)
                {
                    if (!this.Location.terrainFeatures.TryGetValue(new Vector2(x, y), out TerrainFeature feature) || feature is not HoeDirt { crop: not null } soil)
                        continue;

                    string harvest = soil.crop.indexOfHarvest.Value;
                    if (string.IsNullOrEmpty(harvest))
                        continue;

                    int days = CropMath.DaysUntilHarvest(soil) ?? int.MaxValue;
                    growing[harvest] = growing.TryGetValue(harvest, out var seen) ? (seen.Count + 1, Math.Min(seen.Soonest, days)) : (1, days);
                }
            }

            int row = this.PanelBounds.Y;

            if (growing.Count == 0)
                Marquee.Draw(b, this.Translations.Get("plan.preview-empty"), Game1.smallFont, new Vector2(this.PanelBounds.X, row), this.PanelBounds.Width, Game1.textColor * 0.6f);

            foreach ((string harvestId, (int count, int soonest)) in growing.OrderBy(pair => pair.Value.Soonest))
            {
                if (row > this.PanelBounds.Bottom - 40)
                    break;

                ItemIcon.Draw(b, ItemRegistry.Create(harvestId, allowNull: true), new Rectangle(this.PanelBounds.X, row, 32, 32), 1f, showQuality: false);
                string when = soonest == 0 ? this.Translations.Get("harvester.ready") : soonest == int.MaxValue ? "" : this.Translations.Get("plan.preview-soonest", new { days = soonest });
                Marquee.Draw(b, $"{count}x {GetName(ItemRegistry.QualifyItemId(harvestId))}  {when}", Game1.smallFont, new Vector2(this.PanelBounds.X + 40, row + 4), this.PanelBounds.Width - 44, Game1.textColor);
                row += 40;
            }
        }

        /// <summary>Draws the tabs, stage control and action buttons.</summary>
        private void DrawButtons(SpriteBatch b)
        {
            foreach ((Rectangle bounds, string action) in this.Buttons)
            {
                if (action is "stage-down" or "stage-up")
                {
                    // The game's own arrows: a "<" in this font would draw as a heart.
                    Rectangle arrow = action == "stage-down" ? new Rectangle(352, 495, 12, 11) : new Rectangle(365, 495, 12, 11);
                    bool enabled = action == "stage-down" ? this.Stage > 0 : this.Stage < this.MaxStage;

                    // Pressed, it nudges the way it points and springs back.
                    Rectangle drawn = bounds;
                    if (this.ArrowPressedAt.TryGetValue(action, out DateTime pressed))
                    {
                        float nudge = UiAnimation.Progress(pressed, 200);
                        drawn.X += (int)(Math.Sin(nudge * Math.PI) * 8 * (action == "stage-up" ? 1 : -1));
                    }
                    b.Draw(Game1.mouseCursors, drawn, arrow, enabled ? Color.White : Color.White * 0.35f);
                    continue;
                }

                if (action == "force")
                {
                    // A red-backed checkbox: this one destroys things. Switching it animates: the backing deepens
                    // or fades, the tick pops, and switching it on gives a short warning shake.
                    float toggle = UiAnimation.Progress(this.ForceToggledAt, ForceToggleMs);
                    float settled = UiAnimation.EaseOut(toggle);
                    float on = this.Force ? settled : 1f - settled;
                    Rectangle area = bounds;
                    if (this.Force && toggle < 1f)
                        area.X += (int)(Math.Sin(toggle * Math.PI * 6) * (1f - toggle) * 6);

                    b.Draw(Game1.staminaRect, area, new Color(200, 50, 40) * MathHelper.Lerp(0.2f, 0.55f, on));
                    Rectangle box = new(area.X + 8, area.Center.Y - 18, 36, 36);
                    if (toggle < 1f)
                        box = UiAnimation.Scale(box, 0.7f + (0.3f * UiAnimation.EaseOutBack(toggle)));
                    b.Draw(Game1.mouseCursors, box, this.Force ? OptionsCheckbox.sourceRectChecked : OptionsCheckbox.sourceRectUnchecked, Color.White);
                    int textX = area.X + 8 + 36 + 10;
                    Marquee.Draw(b, this.Translations.Get("plan.force"), Game1.smallFont, new Vector2(textX, area.Center.Y - 16), area.Right - textX - 4, Color.Lerp(UiTheme.Bad, Color.White, on));
                    continue;
                }

                Color tint = action == "confirm" ? (this.Force ? new Color(255, 120, 110) : Color.LightGreen) : Color.White;
                UiTheme.DrawButton(b, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height), tint, 2f);

                // Tab labels go on after the highlight, which glides beneath them.
                if (action.StartsWith("tab-"))
                    continue;

                string label = this.Translations.Get("plan.button-" + action);
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), Game1.textColor);
            }

            if (!this.IsPreview)
            {
                Rectangle highlight = this.GetModeHighlight();
                if (!highlight.IsEmpty)
                    UiTheme.DrawButton(b, new Rectangle(highlight.X, highlight.Y, highlight.Width, highlight.Height), Color.Gold, 2f);

                foreach ((Rectangle bounds, string action) in this.Buttons.Where(button => button.Action.StartsWith("tab-")))
                {
                    string label = this.Translations.Get("plan.button-" + action);
                    Vector2 size = Game1.smallFont.MeasureString(label);
                    Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), Game1.textColor);
                }
            }

            if (this.IsPreview)
                return;

            // What stage the crops are drawn at, in the space between the arrows.
            Rectangle down = this.Buttons.First(button => button.Action == "stage-down").Bounds;
            Rectangle up = this.Buttons.First(button => button.Action == "stage-up").Bounds;
            int room = up.X - down.Right - 8;
            float t = UiAnimation.EaseOut(this.StageTransitionProgress());
            if (t >= 1f)
            {
                this.DrawStageLabel(b, this.DescribeStage(this.Stage), down, room, 0, 1f);
                return;
            }

            // Changing: the old label slides out as the new one slides in from the side the stage moved to.
            Rectangle clip = new(down.Right + 4, down.Y, room, down.Height);
            if (!UiBatch.Push(b, clip, Vector2.Zero))
                return;
            try
            {
                // The old label goes quickly, so it's gone before the new one settles.
                this.DrawStageLabel(b, this.DescribeStage(this.PreviousStage), down, room, (int)(-t * 40 * this.StageDirection), (1f - t) * (1f - t));
                this.DrawStageLabel(b, this.DescribeStage(this.Stage), down, room, (int)((1f - t) * 40 * this.StageDirection), t);
            }
            finally
            {
                UiBatch.Pop(b);
            }
        }

        /// <summary>Draws a growth stage's name centred between the arrows.</summary>
        private void DrawStageLabel(SpriteBatch b, string label, Rectangle down, int room, int offset, float alpha)
        {
            Vector2 size = Game1.smallFont.MeasureString(label);
            Marquee.Draw(b, label, Game1.smallFont, new Vector2(down.Right + 4 + Math.Max(0, (room - size.X) / 2) + offset, down.Y + 10), room, Game1.textColor * alpha);
        }


        /*********
        ** Private methods: layout and lookup
        *********/
        /// <summary>Sizes the window, grid and panel to the screen and the area.</summary>
        private void Layout()
        {
            this.width = Math.Min(1340, Game1.uiViewport.Width - 64);
            this.height = Math.Min(900, Game1.uiViewport.Height - 64);
            this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
            this.yPositionOnScreen = (Game1.uiViewport.Height - this.height) / 2;
            this.initializeUpperRightCloseButton();

            // The grid gets everything left of the side panel, above the button bar; tiles are square and as big as fit.
            int gridWidth = this.width - PanelWidth - 104;
            int gridHeight = this.height - 96 - 104;
            this.Cell = Math.Clamp(Math.Min(gridWidth / this.Settings.Width, gridHeight / this.Settings.Height), 8, 72);
            int usedWidth = this.Cell * this.Settings.Width;
            int usedHeight = this.Cell * this.Settings.Height;
            this.GridBounds = new Rectangle(this.xPositionOnScreen + 32 + ((gridWidth - usedWidth) / 2), this.yPositionOnScreen + 88 + ((gridHeight - usedHeight) / 2), usedWidth, usedHeight);
            this.PanelBounds = new Rectangle(this.xPositionOnScreen + this.width - PanelWidth - 36, this.yPositionOnScreen + 92, PanelWidth, this.height - 92 - 108);

            this.Buttons.Clear();
            int bottom = this.yPositionOnScreen + this.height - 88;

            if (this.IsPreview)
            {
                this.Buttons.Add((new Rectangle(this.xPositionOnScreen + this.width - 216, bottom, 180, 56), "close"));
                return;
            }

            // Three tabs across the panel, each as wide as its label needs and the spare shared out.
            string[] tabs = { "tab-seeds", "tab-fertilizer", "tab-automation" };
            const int gap = 6;
            int[] widths = tabs.Select(tab => (int)Game1.smallFont.MeasureString(this.Translations.Get("plan.button-" + tab)).X + 24).ToArray();
            int spare = Math.Max(0, (PanelWidth - ((tabs.Length - 1) * gap) - widths.Sum()) / tabs.Length);
            int tabX = this.PanelBounds.X;
            for (int i = 0; i < tabs.Length; i++)
            {
                this.Buttons.Add((new Rectangle(tabX, this.PanelBounds.Y, widths[i] + spare, 48), tabs[i]));
                tabX += widths[i] + spare + gap;
            }

            int x = this.xPositionOnScreen + 32;
            this.Buttons.Add((new Rectangle(x, bottom + 6, 48, 44), "stage-down"));
            this.Buttons.Add((new Rectangle(x + 260, bottom + 6, 48, 44), "stage-up"));
            this.Buttons.Add((new Rectangle(x + 330, bottom, 160, 56), "fill"));
            this.Buttons.Add((new Rectangle(x + 500, bottom, 160, 56), "clear"));
            this.Buttons.Add((new Rectangle(x + 676, bottom, 230, 56), "force"));
            this.Buttons.Add((new Rectangle(this.xPositionOnScreen + this.width - 392, bottom, 170, 56), "cancel"));
            this.Buttons.Add((new Rectangle(this.xPositionOnScreen + this.width - 212, bottom, 176, 56), "confirm"));
        }

        /// <summary>The screen bounds of a grid cell.</summary>
        private Rectangle GetCellBounds(int x, int y)
        {
            // A one-pixel gap draws the grid lines for free.
            return new Rectangle(this.GridBounds.X + (x * this.Cell) + 1, this.GridBounds.Y + (y * this.Cell) + 1, this.Cell - 1, this.Cell - 1);
        }

        /// <summary>The grid cell under a screen position.</summary>
        private bool TryGetCell(int x, int y, out Point cell)
        {
            cell = Point.Zero;
            if (!this.GridBounds.Contains(x, y))
                return false;

            cell = new Point((x - this.GridBounds.X) / this.Cell, (y - this.GridBounds.Y) / this.Cell);
            return cell.X < this.Settings.Width && cell.Y < this.Settings.Height;
        }

        /// <summary>Describes a tile for its tooltip: what's there, what's planned, and what's wrong with it.</summary>
        private string DescribeCell(Point cell)
        {
            Rectangle area = this.Settings.GetArea(this.MachineTile);
            Vector2 tile = new(area.X + cell.X, area.Y + cell.Y);
            List<string> lines = new() { this.Translations.Get("harvester.tile", new { x = (int)tile.X, y = (int)tile.Y }) };
            this.Location.terrainFeatures.TryGetValue(tile, out TerrainFeature feature);

            // Set aside for automation, and whether it's free.
            if (this.Settings.Tiles.TryGetValue(cell, out TilePlan automation) && automation.Automation)
            {
                lines.Add(this.Translations.Get("plan.automation-tile"));
                if (feature is HoeDirt { crop: not null } other && !other.crop.dead.Value && !CropMath.IsAutomationCrop(other))
                    lines.Add(this.Translations.Get("plan.automation-waiting"));
            }

            // A job about to plant here.
            if (feature is not HoeDirt { crop: not null })
            {
                CraftJob planting = this.ReservedBy?.Invoke(this.Location, tile);
                PlannedPlanting order = planting?.Plantings.FirstOrDefault(entry => entry.Location == this.Location && entry.Tile == tile);
                if (order != null)
                    lines.Add(this.Translations.Get("plan.planting", new { seed = GetName(order.SeedId), count = planting.TargetCount, name = planting.DisplayName }));
            }

            // What's in the ground now.
            if (feature is HoeDirt { crop: not null } soil)
            {
                string growing = GetName(ItemRegistry.QualifyItemId(soil.crop.indexOfHarvest.Value ?? ""));
                int? left = CropMath.DaysUntilHarvest(soil);
                lines.Add(left == 0
                    ? this.Translations.Get("plan.growing-ready", new { name = growing })
                    : left != null ? this.Translations.Get("plan.growing-days", new { name = growing, days = left }) : growing);

                CraftJob job = this.ReservedBy?.Invoke(this.Location, tile);
                lines.Add(job != null
                    ? this.Translations.Get("plan.reserved", new { count = job.TargetCount, name = job.DisplayName })
                    : this.Translations.Get("plan.not-reserved"));
            }

            // What's planned, when planning.
            if (!this.IsPreview && this.Settings.Tiles.TryGetValue(cell, out TilePlan plan))
            {
                if (plan.SeedId != null && !plan.Automation)
                {
                    int? days = CropMath.DaysToGrow(plan.SeedId, plan.FertilizerId, this.Location, tile);
                    lines.Add(this.Translations.Get("plan.planned-seed", new { name = GetName(plan.SeedId), days = days ?? 0 }));
                }
                if (plan.FertilizerId != null)
                    lines.Add(this.Translations.Get("plan.planned-fertilizer", new { name = GetName(plan.FertilizerId) }));

                foreach (PlanProblem problem in this.Problems.Where(problem => problem.Tiles.Contains(cell)))
                    lines.Add("! " + problem.Message);
            }

            return string.Join("\n", lines);
        }

        /// <summary>Opens a harvester's field view, read-only, from any menu, returning to it when closed.</summary>
        /// <returns>Whether there was a harvester there to show.</returns>
        public static bool OpenFieldView(NetworkManager networks, ITranslationHelper translations, GameLocation location, Vector2 harvesterTile, Func<GameLocation, Vector2, CraftJob> reservedBy)
        {
            if (location == null || !location.Objects.TryGetValue(harvesterTile, out SObject machine) || machine.ItemId != ModIds.AutoHarvester)
                return false;

            IClickableMenu parent = Game1.activeClickableMenu;
            HarvesterSettings settings = HarvesterSettings.Read(machine);
            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new HarvesterPlanMenu(networks, translations, location, harvesterTile, settings, settings, preview: true, onClose: (_, _) => Game1.activeClickableMenu = parent, reservedBy);
            return true;
        }

        /// <summary>A crop for drawing a seed's sprites, made once per seed.</summary>
        private Crop GetCropSprite(string seedId)
        {
            if (this.CropSprites.TryGetValue(seedId, out Crop crop))
                return crop;

            try
            {
                crop = new Crop(CropMath.Unqualify(seedId), 0, 0, this.Location);
            }
            catch
            {
                crop = null;
            }

            this.CropSprites[seedId] = crop;
            return crop;
        }

        /// <summary>The display name for an item ID.</summary>
        private static string GetName(string itemId) => StockId.GetDisplayName(itemId);
    }
}
