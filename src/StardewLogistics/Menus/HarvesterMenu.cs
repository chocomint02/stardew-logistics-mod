using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace StardewLogistics.Menus
{
    /// <summary>An auto-harvester's settings: how big its area is, where it sits, and whether it's shown.</summary>
    internal class HarvesterMenu : IClickableMenu
    {
        /*********
        ** Fields
        *********/
        private const int MenuWidth = 780;
        private const int MenuHeight = 520;

        private readonly NetworkManager Networks;
        private readonly ITranslationHelper Translations;
        private readonly Action<GameLocation, Vector2, SObject> OnPlanSaved;

        /// <summary>The settings being edited, written back to the machine on every change.</summary>
        private HarvesterSettings Settings;

        /// <summary>The buttons, by what they change.</summary>
        private readonly List<(Rectangle Bounds, string Action, int Delta, string Label)> Buttons = new();


        /*********
        ** Accessors
        *********/
        /// <summary>The machine being configured.</summary>
        public SObject Machine { get; }

        /// <summary>The location the machine is in.</summary>
        public GameLocation Location { get; }

        /// <summary>The machine's tile.</summary>
        public Vector2 Tile { get; }


        /*********
        ** Public methods
        *********/
        public HarvesterMenu(NetworkManager networks, ITranslationHelper translations, GameLocation location, Vector2 tile, SObject machine, Action<GameLocation, Vector2, SObject> onPlanSaved)
            : base((Game1.uiViewport.Width - MenuWidth) / 2, (Game1.uiViewport.Height - MenuHeight) / 2, MenuWidth, MenuHeight, showUpperRightCloseButton: true)
        {
            this.Networks = networks;
            this.Translations = translations;
            this.Location = location;
            this.Tile = tile;
            this.Machine = machine;
            this.OnPlanSaved = onPlanSaved;
            this.Settings = HarvesterSettings.Read(machine);

            this.Layout();
        }

        /// <inheritdoc />
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            foreach ((Rectangle bounds, string action, int delta, string _) in this.Buttons)
            {
                if (!bounds.Contains(x, y))
                    continue;

                switch (action)
                {
                    case "width":
                        this.Settings.Resize(this.Settings.Width + delta, this.Settings.Height);
                        break;
                    case "height":
                        this.Settings.Resize(this.Settings.Width, this.Settings.Height + delta);
                        break;
                    case "north":
                        // Shown with north positive; stored with south positive, as screen rows run.
                        this.Settings.OffsetY = Math.Clamp(this.Settings.OffsetY - delta, -HarvesterSettings.MaxOffset, HarvesterSettings.MaxOffset);
                        break;
                    case "east":
                        this.Settings.OffsetX = Math.Clamp(this.Settings.OffsetX + delta, -HarvesterSettings.MaxOffset, HarvesterSettings.MaxOffset);
                        break;
                    case "reset":
                        this.Settings.OffsetX = 0;
                        this.Settings.OffsetY = 0;
                        break;
                    case "show":
                        this.Settings.ShowPreview = !this.Settings.ShowPreview;
                        break;
                    case "preview":
                        this.OpenGrid(preview: true);
                        return;
                    case "plan":
                        this.OpenGrid(preview: false);
                        return;
                }

                this.Settings.Write(this.Machine);
                Game1.playSound("drumkit6");
                return;
            }
        }

        /// <inheritdoc />
        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            this.xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
            this.yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            this.initializeUpperRightCloseButton();
            this.Layout();
        }

        /// <inheritdoc />
        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
            drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);

            int left = this.xPositionOnScreen + 40;
            string title = this.Machine.DisplayName;
            Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
            Utility.drawTextWithShadow(b, title, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + (this.width - titleSize.X) / 2, this.yPositionOnScreen + 28), Game1.textColor);

            (string Label, string Value, int Row)[] rows =
            {
                (this.Translations.Get("harvester.width"), this.Settings.Width.ToString(), 0),
                (this.Translations.Get("harvester.height"), this.Settings.Height.ToString(), 1),
                (this.Translations.Get("harvester.north-south"), Signed(-this.Settings.OffsetY), 2),
                (this.Translations.Get("harvester.west-east"), Signed(this.Settings.OffsetX), 3)
            };

            foreach ((string label, string value, int row) in rows)
            {
                int y = this.RowY(row);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(left, y + 10), Game1.textColor);
                Vector2 size = Game1.smallFont.MeasureString(value);
                Utility.drawTextWithShadow(b, value, Game1.smallFont, new Vector2(this.ValueCentreX() - (size.X / 2), y + 10), Game1.textColor);
            }

            foreach ((Rectangle bounds, string action, int _, string label) in this.Buttons)
            {
                bool active = action == "show" && this.Settings.ShowPreview;
                bool hover = bounds.Contains(Game1.getMouseX(), Game1.getMouseY());
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, active ? Color.LightGreen : hover ? Color.Wheat : Color.White, 2f, drawShadow: false);

                string text = action == "show"
                    ? this.Translations.Get(this.Settings.ShowPreview ? "harvester.preview-on" : "harvester.preview-off")
                    : label;
                Vector2 size = Game1.smallFont.MeasureString(text);
                Utility.drawTextWithShadow(b, text, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), Game1.textColor);
            }

            // Where the area ends up, and whether the machine can do anything.
            Rectangle area = this.Settings.GetArea(this.Tile);
            int infoY = this.RowY(4) + 4;
            Utility.drawTextWithShadow(b, this.Translations.Get("harvester.area", new { width = area.Width, height = area.Height, left = area.Left, top = area.Top, right = area.Right - 1, bottom = area.Bottom - 1 }), Game1.smallFont, new Vector2(left, infoY), Game1.textColor * 0.8f);

            StorageNetwork network = this.Networks.GetNetworkAt(this.Location, this.Tile);
            int planned = this.Settings.Tiles.Values.Count(plan => plan.SeedId != null);
            string status = network == null
                ? this.Translations.Get("harvester.not-connected")
                : this.Translations.Get("harvester.status", new { planned });
            Marquee.Draw(b, status, Game1.smallFont, new Vector2(left, infoY + 36), this.width - 80, network == null ? Color.Firebrick : Game1.textColor);

            base.draw(b);
            this.drawMouse(b);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Opens the crop grid, to plan or just to look, returning here when it closes.</summary>
        /// <remarks>
        /// Planning works on a copy. Only Confirm writes it back; Cancel, Escape or the close button leave the
        /// machine's plan as it was.
        /// </remarks>
        private void OpenGrid(bool preview)
        {
            Game1.playSound("bigSelect");
            HarvesterSettings copy = HarvesterSettings.Read(this.Machine);
            HarvesterSettings original = HarvesterSettings.Read(this.Machine);

            Game1.activeClickableMenu = new HarvesterPlanMenu(this.Networks, this.Translations, this.Location, this.Tile, copy, original, preview, onClose: (confirmed, clear) =>
            {
                if (confirmed)
                {
                    // Force change: the crops in the new plan's way go now, so it can be planted straight away.
                    foreach (Vector2 tile in clear)
                    {
                        if (this.Location.terrainFeatures.TryGetValue(tile, out StardewValley.TerrainFeatures.TerrainFeature feature) && feature is StardewValley.TerrainFeatures.HoeDirt { crop: not null } soil)
                            soil.destroyCrop(showAnimation: true);
                    }
                    if (clear.Count > 0)
                        Log.Debug($"Force change cleared {clear.Count} crops for the auto-harvester at {this.Location.NameOrUniqueName} {this.Tile}.");

                    copy.Write(this.Machine);
                    this.OnPlanSaved?.Invoke(this.Location, this.Tile, this.Machine);
                }

                this.Settings = HarvesterSettings.Read(this.Machine);
                Game1.activeClickableMenu = this;
            });
        }

        /// <summary>An offset as a signed number: "+3", "-2", "0".</summary>
        private static string Signed(int value) => value > 0 ? "+" + value : value.ToString();

        /// <summary>The top of a settings row.</summary>
        private int RowY(int row) => this.yPositionOnScreen + 96 + (row * 58);

        /// <summary>The centre of the value column, between the steppers.</summary>
        private int ValueCentreX() => this.xPositionOnScreen + 500;

        /// <summary>Places the buttons.</summary>
        private void Layout()
        {
            this.Buttons.Clear();

            // Four steppers: two on each side of the value.
            (string Action, string Minus10, string Minus1, string Plus1, string Plus10)[] steppers =
            {
                ("width", "-10", "-1", "+1", "+10"),
                ("height", "-10", "-1", "+1", "+10"),
                ("north", "-10", "-1", "+1", "+10"),
                ("east", "-10", "-1", "+1", "+10")
            };

            int centre = this.ValueCentreX();
            for (int row = 0; row < steppers.Length; row++)
            {
                int y = this.RowY(row);
                var stepper = steppers[row];
                this.Buttons.Add((new Rectangle(centre - 186, y, 64, 44), stepper.Action, -10, stepper.Minus10));
                this.Buttons.Add((new Rectangle(centre - 116, y, 56, 44), stepper.Action, -1, stepper.Minus1));
                this.Buttons.Add((new Rectangle(centre + 60, y, 56, 44), stepper.Action, 1, stepper.Plus1));
                this.Buttons.Add((new Rectangle(centre + 122, y, 64, 44), stepper.Action, 10, stepper.Plus10));
            }

            int bottom = this.yPositionOnScreen + this.height - 88;
            int left = this.xPositionOnScreen + 40;
            int buttonWidth = (this.width - 80 - 36) / 4;
            string[] actions = { "reset", "show", "preview", "plan" };
            for (int i = 0; i < actions.Length; i++)
                this.Buttons.Add((new Rectangle(left + (i * (buttonWidth + 12)), bottom, buttonWidth, 56), actions[i], 0, this.Translations.Get("harvester." + actions[i])));
        }
    }
}
