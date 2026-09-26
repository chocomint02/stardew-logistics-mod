using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
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

        /// <summary>Whether the player confirmed their changes.</summary>
        private bool Confirmed;

        private bool FertilizerTab;
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


        /*********
        ** Public methods
        *********/
        public HarvesterPlanMenu(NetworkManager networks, ITranslationHelper translations, GameLocation location, Vector2 machineTile, HarvesterSettings settings, HarvesterSettings original, bool preview, Action<bool, IReadOnlyList<Vector2>> onClose)
        {
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
            if (this.ProblemsStale)
                this.RecheckPlan();

            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);
            drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);

            string title = this.Translations.Get(this.IsPreview ? "plan.preview-title" : "plan.title", new { width = this.Settings.Width, height = this.Settings.Height });
            Utility.drawTextWithShadow(b, title, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + 32, this.yPositionOnScreen + 24), Game1.textColor);

            this.DrawGrid(b);
            if (this.IsPreview)
                this.DrawPreviewPanel(b);
            else
            {
                this.DrawPalette(b);
                this.DrawProblems(b);
            }
            this.DrawButtons(b);

            base.draw(b);
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
            drawTextureBox(b, box.X, box.Y, box.Width, box.Height, Color.White);

            Utility.drawTextWithShadow(b, this.Translations.Get("plan.force-title"), Game1.dialogueFont, new Vector2(box.X + 32, box.Y + 28), Color.Firebrick);

            string body = this.ForceTiles.Count > 0
                ? this.Translations.Get("plan.force-body", new { count = this.ForceTiles.Count })
                : this.Translations.Get("plan.force-none");
            string wrapped = Game1.parseText(body, Game1.smallFont, box.Width - 64);
            Utility.drawTextWithShadow(b, wrapped, Game1.smallFont, new Vector2(box.X + 32, box.Y + 96), Game1.textColor);

            this.ForceBack = new Rectangle(box.X + 32, box.Bottom - 88, 220, 56);
            this.ForceApply = new Rectangle(box.Right - 32 - 260, box.Bottom - 88, 260, 56);
            foreach ((Rectangle bounds, string key, Color tint) in new[] { (this.ForceBack, "plan.button-back", Color.White), (this.ForceApply, "plan.button-apply", new Color(255, 120, 110)) })
            {
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, tint, 2f, drawShadow: false);
                string label = this.Translations.Get(key);
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), Game1.textColor);
            }
        }


        /*********
        ** Private methods: editing
        *********/
        /// <summary>Applies the current palette choice to a tile.</summary>
        private void Paint(Point cell)
        {
            if (this.IsPreview)
                return;

            this.Settings.Tiles.TryGetValue(cell, out TilePlan plan);
            plan ??= new TilePlan();

            if (!this.FertilizerTab && this.SelectedSeed != null)
            {
                if (string.Equals(plan.SeedId, this.SelectedSeed.QualifiedItemId, StringComparison.OrdinalIgnoreCase))
                    return;

                plan.SeedId = this.SelectedSeed.QualifiedItemId;
                plan.Planted = false; // a new crop starts with its own first planting
            }
            else if (this.FertilizerTab && this.SelectedFertilizer != null)
            {
                if (string.Equals(plan.FertilizerId, this.SelectedFertilizer.QualifiedItemId, StringComparison.OrdinalIgnoreCase))
                    return;

                plan.FertilizerId = this.SelectedFertilizer.QualifiedItemId;
            }
            else
                return;

            this.Settings.Tiles[cell] = plan;
            this.OnPlanChanged();
        }

        /// <summary>Clears a tile: its fertilizer on the fertilizer tab, otherwise its seed and fertilizer both.</summary>
        private void Erase(Point cell)
        {
            if (!this.Settings.Tiles.TryGetValue(cell, out TilePlan plan))
                return;

            if (this.FertilizerTab && plan.SeedId != null)
                plan.FertilizerId = null;
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
                case "tab-seeds": this.FertilizerTab = false; break;
                case "tab-fertilizer": this.FertilizerTab = true; break;
                case "stage-down": this.Stage = Math.Max(0, this.Stage - 1); break;
                case "stage-up": this.Stage = Math.Min(this.MaxStage, this.Stage + 1); break;
                case "fill":
                    for (int y = 0; y < this.Settings.Height; y++)
                    {
                        for (int x = 0; x < this.Settings.Width; x++)
                            this.Paint(new Point(x, y));
                    }
                    break;
                case "clear":
                    this.Settings.Tiles.Clear();
                    this.OnPlanChanged();
                    break;
                case "force":
                    this.Force = !this.Force;
                    this.ProblemsStale = true;
                    Game1.playSound(this.Force ? "trashcanlid" : "drumkit6");
                    return;
                case "confirm":
                    if (this.Force)
                    {
                        // Say exactly what will be destroyed, and wait for a second yes.
                        this.RecheckPlan();
                        this.ConfirmingForce = true;
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

        private List<Item> PaletteItems => this.FertilizerTab ? this.Fertilizers : this.Seeds;

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

                    // Fertilizer, as its mark on the soil.
                    string fertilizer = this.IsPreview ? soil?.fertilizer.Value : plan?.FertilizerId ?? soil?.fertilizer.Value;
                    if (!string.IsNullOrEmpty(fertilizer))
                        this.DrawFertilizer(b, fertilizer, cell);

                    // The crop: what's growing in preview, what's planned when planning.
                    if (this.IsPreview)
                    {
                        if (soil?.crop != null)
                            DrawRealCrop(b, soil.crop, cell, tile);
                    }
                    else if (plan?.SeedId != null)
                        this.DrawPlannedCrop(b, plan.SeedId, cell);

                    if (tile == this.MachineTile)
                        this.DrawOutline(b, cell, Color.SteelBlue, 3);

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

            crop.currentPhase.Value = Math.Min(this.Stage, crop.phaseDays.Count - 1);
            DrawCropSprite(b, crop, cell, 1);
        }

        /// <summary>Draws a crop that's really growing, as it is now.</summary>
        private static void DrawRealCrop(SpriteBatch b, Crop crop, Rectangle cell, Vector2 tile)
        {
            DrawCropSprite(b, crop, cell, (int)((tile.X * 7) + (tile.Y * 11)));
        }

        /// <summary>Draws a crop's current sprite fitted to a cell.</summary>
        private static void DrawCropSprite(SpriteBatch b, Crop crop, Rectangle cell, int variation)
        {
            try
            {
                CropData data = crop.GetData();
                Texture2D texture = Game1.content.Load<Texture2D>(string.IsNullOrEmpty(data?.Texture) ? "TileSheets\\crops" : data.Texture);

                // Crop sprites are 16x32 and stand on their tile: fit the height to the cell, bottom-aligned.
                int height = cell.Height;
                int width = height / 2;
                b.Draw(texture, new Rectangle(cell.Center.X - (width / 2), cell.Bottom - height, width, height), crop.getSourceRect(variation), Color.White);
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

        /// <summary>Draws the palette, with how many of each storage holds, and the replant choices.</summary>
        private void DrawPalette(SpriteBatch b)
        {
            List<Item> items = this.PaletteItems;
            Item selected = this.FertilizerTab ? this.SelectedFertilizer : this.SelectedSeed;

            for (int i = 0; i < items.Count; i++)
            {
                Rectangle cell = this.GetPaletteCell(i);
                bool isSelected = selected != null && items[i].QualifiedItemId == selected.QualifiedItemId;
                b.Draw(Game1.staminaRect, cell, isSelected ? Color.Gold * 0.6f : Color.Wheat * 0.35f);
                ItemIcon.Draw(b, items[i], new Rectangle(cell.X + 3, cell.Y + 3, cell.Width - 6, cell.Height - 6), 1f, showQuality: false);

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
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), pill.X, pill.Y, pill.Width, pill.Height, replant ? Color.LightGreen : Color.White, 2f, drawShadow: false);
                string state = this.Translations.Get(replant ? "plan.replant-yes" : "plan.replant-no");
                Vector2 stateSize = Game1.smallFont.MeasureString(state);
                Utility.drawTextWithShadow(b, state, Game1.smallFont, new Vector2(pill.Center.X - (stateSize.X / 2), pill.Center.Y - (stateSize.Y / 2)), Game1.textColor);

                Marquee.Draw(b, GetName(seedId), Game1.smallFont, new Vector2(row.X + 42, row.Y + 8), pill.X - row.X - 50, Game1.textColor);
                y += 44;
            }
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
                Marquee.Draw(b, this.Translations.Get("plan.no-problems"), Game1.smallFont, new Vector2(box.X + 10, box.Y + 10), box.Width - 20, new Color(40, 120, 40));
                return;
            }

            Marquee.Draw(b, this.Translations.Get("plan.problems", new { count = this.Problems.Count }), Game1.smallFont, new Vector2(box.X + 10, box.Y + 8), box.Width - 20, Color.Firebrick);

            int y = box.Y + 44;
            foreach (PlanProblem problem in this.Problems)
            {
                if (y > box.Bottom - 34)
                    break;

                Rectangle row = new(box.X + 4, y - 2, box.Width - 8, 34);
                this.ProblemRows.Add((row, problem));
                if (this.HoveredProblem == problem)
                    b.Draw(Game1.staminaRect, row, Color.Yellow * 0.25f);

                b.Draw(Game1.staminaRect, new Rectangle(box.X + 10, y + 8, 12, 12), Color.Firebrick);
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
                    b.Draw(Game1.mouseCursors, bounds, arrow, enabled ? Color.White : Color.White * 0.35f);
                    continue;
                }

                if (action == "force")
                {
                    // A red-backed checkbox: this one destroys things.
                    b.Draw(Game1.staminaRect, bounds, new Color(200, 50, 40) * (this.Force ? 0.55f : 0.2f));
                    Rectangle box = new(bounds.X + 8, bounds.Center.Y - 18, 36, 36);
                    b.Draw(Game1.mouseCursors, box, this.Force ? OptionsCheckbox.sourceRectChecked : OptionsCheckbox.sourceRectUnchecked, Color.White);
                    Marquee.Draw(b, this.Translations.Get("plan.force"), Game1.smallFont, new Vector2(box.Right + 10, bounds.Center.Y - 16), bounds.Right - box.Right - 14, this.Force ? Color.White : Color.Firebrick);
                    continue;
                }

                bool active = (action == "tab-seeds" && !this.FertilizerTab) || (action == "tab-fertilizer" && this.FertilizerTab);
                Color tint = action == "confirm" ? (this.Force ? new Color(255, 120, 110) : Color.LightGreen) : active ? Color.Gold : Color.White;
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, tint, 2f, drawShadow: false);

                string label = this.Translations.Get("plan.button-" + action);
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), Game1.textColor);
            }

            if (this.IsPreview)
                return;

            // What stage the crops are drawn at, in the space between the arrows.
            Rectangle down = this.Buttons.First(button => button.Action == "stage-down").Bounds;
            Rectangle up = this.Buttons.First(button => button.Action == "stage-up").Bounds;
            string stage = this.Stage == 0
                ? this.Translations.Get("plan.stage-seed")
                : this.Stage >= this.MaxStage && this.MaxStage > 0
                    ? this.Translations.Get("plan.stage-grown")
                    : this.Translations.Get("plan.stage", new { stage = this.Stage });
            Vector2 stageSize = Game1.smallFont.MeasureString(stage);
            int room = up.X - down.Right - 8;
            Marquee.Draw(b, stage, Game1.smallFont, new Vector2(down.Right + 4 + Math.Max(0, (room - stageSize.X) / 2), down.Y + 10), room, Game1.textColor);
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

            this.Buttons.Add((new Rectangle(this.PanelBounds.X, this.PanelBounds.Y, 180, 48), "tab-seeds"));
            this.Buttons.Add((new Rectangle(this.PanelBounds.X + 190, this.PanelBounds.Y, 190, 48), "tab-fertilizer"));

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

            // What's in the ground now.
            if (feature is HoeDirt { crop: not null } soil)
            {
                string growing = GetName(ItemRegistry.QualifyItemId(soil.crop.indexOfHarvest.Value ?? ""));
                int? left = CropMath.DaysUntilHarvest(soil);
                lines.Add(left == 0
                    ? this.Translations.Get("plan.growing-ready", new { name = growing })
                    : left != null ? this.Translations.Get("plan.growing-days", new { name = growing, days = left }) : growing);
            }

            // What's planned, when planning.
            if (!this.IsPreview && this.Settings.Tiles.TryGetValue(cell, out TilePlan plan))
            {
                if (plan.SeedId != null)
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
