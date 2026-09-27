using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Network;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace StardewLogistics.Menus
{
    /// <summary>Tunes a wireless transmitter or receiver, and says what it's linked to.</summary>
    internal class WirelessMenu : IClickableMenu
    {
        /// <summary>Hover highlights and click ripples on the menu's controls.</summary>
        private readonly UiFx Fx = new();

        /*********
        ** Fields
        *********/
        private const int MenuWidth = 640;
        private const int MenuHeight = 470;

        /// <summary>How far below the window's top its visible frame starts.</summary>
        private const int FrameTop = 64;

        /// <summary>When the window opened, for it to grow into place.</summary>
        private readonly DateTime OpenedAt = DateTime.UtcNow;

        private readonly NetworkManager Networks;
        private readonly ITranslationHelper Translations;
        private readonly GameLocation Location;
        private readonly Vector2 Tile;
        private readonly SObject Device;
        private readonly bool IsTransmitter;

        /// <summary>The channel steps offered, as (label, change).</summary>
        private static readonly (string Label, int Delta)[] Steps = { ("-10", -10), ("-1", -1), ("+1", 1), ("+10", 10) };

        private readonly List<Rectangle> StepButtons = new();

        /// <summary>The status lines shown under the channel, worked out when the channel changes rather than per frame.</summary>
        private List<(string Text, bool IsProblem)> Status = new();


        /*********
        ** Public methods
        *********/
        public WirelessMenu(NetworkManager networks, ITranslationHelper translations, GameLocation location, Vector2 tile, SObject device)
            : base(
                (Game1.uiViewport.Width - MenuWidth) / 2,
                (Game1.uiViewport.Height - MenuHeight) / 2,
                MenuWidth,
                MenuHeight,
                showUpperRightCloseButton: true)
        {
            this.Networks = networks;
            this.Translations = translations;
            this.Location = location;
            this.Tile = tile;
            this.Device = device;
            this.IsTransmitter = NetworkNode.GetKind(device.ItemId) == NodeKind.WirelessTransmitter;

            this.LayoutButtons();
            this.PlaceCloseButton();
            this.RefreshStatus();
        }

        /// <summary>Puts the close button on the frame's corner, where the mod's other windows have it.</summary>
        private void PlaceCloseButton()
        {
            if (this.upperRightCloseButton != null)
                this.upperRightCloseButton.bounds.Y += FrameTop;
        }

        /// <inheritdoc />
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            for (int i = 0; i < this.StepButtons.Count; i++)
            {
                if (!this.StepButtons[i].Contains(x, y))
                    continue;

                int before = NetworkNode.GetChannel(this.Device);
                NetworkNode.SetChannel(this.Device, before + Steps[i].Delta);

                if (NetworkNode.GetChannel(this.Device) != before)
                {
                    this.Networks.InvalidateWireless();
                    this.RefreshStatus();
                    Game1.playSound("smallSelect");
                }
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
            this.PlaceCloseButton();
            this.LayoutButtons();
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
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

            // The window grows into place as it opens.
            Rectangle frame = this.GetFrame();
            bool growing = UiAnimation.PushOpening(b, this.OpenedAt, frame);
            try
            {
                this.DrawWindow(b, frame);
            }
            finally
            {
                if (growing)
                    UiBatch.Pop(b);
            }

            this.drawMouse(b);
        }

        /// <summary>The window's visible frame: where the game's dialogue box used to draw it, below the top margin.</summary>
        private Rectangle GetFrame() => new(this.xPositionOnScreen, this.yPositionOnScreen + FrameTop, this.width, this.height - FrameTop);

        /// <summary>Draws the window and everything in it.</summary>
        /// <remarks>
        /// The frame is the same window box the mod's other windows use, rather than the game's dialogue box: the
        /// colour schemes recolour that box, and the dialogue box is drawn from other parts of the texture.
        /// </remarks>
        private void DrawWindow(SpriteBatch b, Rectangle frame)
        {
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), frame.X, frame.Y, frame.Width, frame.Height, Color.White, 1f, drawShadow: true);

            int left = this.xPositionOnScreen + 48;
            int contentWidth = this.width - 96;
            int y = this.yPositionOnScreen + 104;

            // Title
            string title = this.Device.DisplayName;
            Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
            Utility.drawTextWithShadow(b, title, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + (this.width - titleSize.X) / 2, y), Game1.textColor);
            y += 64;

            // Channel stepper: -10 -1 [ Channel 5 ] +1 +10
            for (int i = 0; i < this.StepButtons.Count; i++)
            {
                Rectangle button = this.StepButtons[i];
                bool hover = button.Contains(Game1.getMouseX(), Game1.getMouseY());
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), button.X, button.Y, button.Width, button.Height, hover ? Color.Wheat : Color.White, 4f, drawShadow: false);
                this.Fx.Control(b, button, inset: 6);

                Vector2 size = Game1.smallFont.MeasureString(Steps[i].Label);
                Utility.drawTextWithShadow(b, Steps[i].Label, Game1.smallFont, new Vector2(button.Center.X - (size.X / 2), button.Center.Y - (size.Y / 2)), Game1.textColor);
            }

            string channel = this.Translations.Get("wireless.channel", new { channel = NetworkNode.GetChannel(this.Device) });
            Vector2 channelSize = Game1.dialogueFont.MeasureString(channel);
            Utility.drawTextWithShadow(b, channel, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + (this.width - channelSize.X) / 2, this.StepButtons[0].Center.Y - (channelSize.Y / 2)), Game1.textColor);
            y = this.StepButtons[0].Bottom + 32;

            // What the channel is doing
            foreach ((string text, bool isProblem) in this.Status)
            {
                Marquee.Draw(b, text, Game1.smallFont, new Vector2(left, y), contentWidth, isProblem ? UiTheme.Bad : Game1.textColor);
                y += 36;
            }

            base.draw(b);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Places the channel buttons: two either side of the channel number.</summary>
        private void LayoutButtons()
        {
            this.StepButtons.Clear();

            const int buttonWidth = 64;
            const int buttonHeight = 56;
            const int gap = 12;
            const int middle = 240;

            int centre = this.xPositionOnScreen + (this.width / 2);
            int top = this.yPositionOnScreen + 176;

            this.StepButtons.Add(new Rectangle(centre - (middle / 2) - gap - buttonWidth - gap - buttonWidth, top, buttonWidth, buttonHeight));
            this.StepButtons.Add(new Rectangle(centre - (middle / 2) - gap - buttonWidth, top, buttonWidth, buttonHeight));
            this.StepButtons.Add(new Rectangle(centre + (middle / 2) + gap, top, buttonWidth, buttonHeight));
            this.StepButtons.Add(new Rectangle(centre + (middle / 2) + gap + buttonWidth + gap, top, buttonWidth, buttonHeight));
        }

        /// <summary>Works out what to say about the device and its channel.</summary>
        private void RefreshStatus()
        {
            List<(string, bool)> status = new();
            int channel = NetworkNode.GetChannel(this.Device);

            // A device off the cable joins nothing, however it's tuned.
            if (!this.Networks.TryGetNode(this.Location, this.Tile, out NetworkNode _, out StorageNetwork network))
            {
                status.Add((this.Translations.Get("wireless.not-connected"), true));
                this.Status = status;
                return;
            }

            ChannelInfo info = this.Networks.GetChannelInfo(channel);

            if (this.IsTransmitter)
            {
                status.Add((this.Translations.Get("wireless.broadcasting", new { count = info.Receivers }), false));
                if (info.Transmitters > 1)
                    status.Add((this.Translations.Get("wireless.shared-transmitters", new { count = info.Transmitters }), false));
            }
            else if (!info.IsLive)
                status.Add((this.Translations.Get("wireless.no-transmitter", new { channel }), true));
            else
                status.Add((this.Translations.Get("wireless.receiving"), false));

            if (info.IsLive && network.IsLinked)
            {
                string places = string.Join(", ", network.Locations.Select(location => location.GetDisplayName() ?? location.Name));
                status.Add((this.Translations.Get("wireless.reach", new { places }), false));
                status.Add((this.Translations.Get("wireless.pooled", new { chests = network.Storages.Count, machines = network.Machines.Count() }), false));
            }

            this.Status = status;
        }
    }
}
