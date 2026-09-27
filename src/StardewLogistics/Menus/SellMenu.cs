using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>Ships a chosen amount of one stored item: by count, or by how much gold it should raise.</summary>
    /// <remarks>
    /// A gold target works out the count for itself, rounding up: 400,000g of 3,150g wine is 127 bottles, which
    /// raise 400,050g. Either way the window shows what the count is worth, and what the whole stock would fetch.
    /// </remarks>
    internal class SellMenu : IClickableMenu
    {
        /*********
        ** Fields
        *********/
        private const int MenuWidth = 780;
        private const int MenuHeight = 620;

        private readonly StorageNetwork Network;
        private readonly NetworkItemStack Entry;
        private readonly ITranslationHelper Translations;
        private readonly Action OnClose;
        private readonly int UnitPrice;
        private readonly bool HasBin;

        private readonly TextBox QuantityBox;
        private readonly TextBox GoldBox;
        private Rectangle QuantityBounds;
        private Rectangle GoldBounds;
        private readonly List<(Rectangle Bounds, int Delta, bool Max)> StepButtons = new();
        private Rectangle ShipButton;
        private Rectangle CancelButton;

        private long Stored;
        private int Quantity;
        private string LastQuantityText;
        private string LastGoldText = "";
        private string HoverText = "";


        /*********
        ** Public methods
        *********/
        /// <summary>How other players' machines name the network, for a farmhand's request.</summary>
        private readonly string NetworkReference;

        public SellMenu(StorageNetwork network, NetworkItemStack entry, ITranslationHelper translations, string networkReference, Action onClose)
        {
            this.NetworkReference = networkReference;
            this.Network = network;
            this.Entry = entry;
            this.Translations = translations;
            this.OnClose = onClose;
            this.UnitPrice = Selling.UnitPrice(entry.Sample) ?? 0;
            this.HasBin = ShippingService.GetBins(network).Count > 0;
            this.Stored = network.CountOf(entry.Key);
            this.Quantity = this.Stored > 0 ? 1 : 0;

            this.width = MenuWidth;
            this.height = MenuHeight;
            this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
            this.yPositionOnScreen = (Game1.uiViewport.Height - this.height) / 2;

            Texture2D textBox = UiTheme.TextBoxTexture();
            this.QuantityBox = new TextBox(textBox, null, Game1.smallFont, UiTheme.TextColour) { Width = 220, Height = 44, Text = this.Quantity.ToString() };
            this.GoldBox = new TextBox(textBox, null, Game1.smallFont, UiTheme.TextColour) { Width = 220, Height = 44, Text = "" };
            this.LastQuantityText = this.QuantityBox.Text;

            this.Layout();
            this.initializeUpperRightCloseButton();
        }

        /// <inheritdoc />
        public override void update(GameTime time)
        {
            base.update(time);

            // Typing a count: take it, and let go of any gold target it no longer matches.
            if (this.QuantityBox.Text != this.LastQuantityText)
            {
                this.LastQuantityText = this.QuantityBox.Text;
                if (MathExpression.TryEvaluate(this.QuantityBox.Text, out int value))
                {
                    this.Quantity = this.Clamp(value);
                    if (this.QuantityBox.Selected)
                        this.SetGoldText("");
                }
            }

            // Typing a gold target: as many as it takes to reach it, rounding up. Red when storage can't reach it.
            if (this.GoldBox.Text != this.LastGoldText)
            {
                this.LastGoldText = this.GoldBox.Text;
                bool parsed = TryParseGold(this.GoldBox.Text, out long gold);
                if (parsed && this.UnitPrice > 0)
                    this.SetQuantity((long)Math.Ceiling(gold / (double)this.UnitPrice), clearGold: false);

                SetTextColour(this.GoldBox, parsed && gold > this.Stored * (long)this.UnitPrice ? UiTheme.Bad : UiTheme.TextColour);
            }
        }

        /// <inheritdoc />
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (this.upperRightCloseButton?.containsPoint(x, y) == true || this.CancelButton.Contains(x, y))
            {
                this.exitThisMenu();
                return;
            }

            this.QuantityBox.Selected = this.QuantityBounds.Contains(x, y);
            this.GoldBox.Selected = this.GoldBounds.Contains(x, y);
            if (this.QuantityBox.Selected || this.GoldBox.Selected)
                return;

            foreach ((Rectangle bounds, int delta, bool max) in this.StepButtons)
            {
                if (!bounds.Contains(x, y))
                    continue;

                long next = max ? (delta > 0 ? this.Stored : 0) : this.Quantity + delta;
                this.SetQuantity(next, clearGold: true);
                Game1.playSound("drumkit6");
                return;
            }

            if (this.ShipButton.Contains(x, y))
                this.Ship();
        }

        /// <inheritdoc />
        public override void receiveKeyPress(Keys key)
        {
            if (this.QuantityBox.Selected || this.GoldBox.Selected)
            {
                if (key is Keys.Escape or Keys.Enter or Keys.Tab)
                {
                    this.QuantityBox.Selected = false;
                    this.GoldBox.Selected = false;
                }
                return;
            }

            if (key == Keys.Escape || Game1.options.doesInputListContain(Game1.options.menuButton, key))
                this.exitThisMenu();
        }

        /// <inheritdoc />
        public override void performHoverAction(int x, int y)
        {
            this.HoverText = "";
            if (this.GoldBounds.Contains(x, y))
                this.HoverText = this.Translations.Get("sell.gold-hint");
            else if (this.ShipButton.Contains(x, y))
                this.HoverText = this.HasBin ? this.Translations.Get("sell.ship-hint") : this.Translations.Get("sell.no-bin");
        }

        /// <inheritdoc />
        protected override void cleanupBeforeExit()
        {
            if (Game1.keyboardDispatcher.Subscriber == this.QuantityBox || Game1.keyboardDispatcher.Subscriber == this.GoldBox)
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
            drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);

            int left = this.xPositionOnScreen + 32;
            int top = this.yPositionOnScreen;

            ItemIcon.Draw(b, this.Entry.Sample, new Rectangle(left, top + 28, 48, 48));
            Marquee.Draw(b, this.Translations.Get("sell.title", new { name = this.Entry.DisplayName }), Game1.dialogueFont, new Vector2(left + 60, top + 32), this.width - 180, Game1.textColor);

            Utility.drawTextWithShadow(b, this.Translations.Get("sell.unit", new { price = Selling.Gold(this.UnitPrice), stored = NumberFormat.Full(this.Stored) }), Game1.smallFont, new Vector2(left, top + 100), Game1.textColor * 0.8f);

            Utility.drawTextWithShadow(b, this.Translations.Get("sell.quantity"), Game1.smallFont, new Vector2(left, this.QuantityBounds.Y + 10), Game1.textColor);
            this.QuantityBox.Draw(b);

            foreach ((Rectangle bounds, int delta, bool max) in this.StepButtons)
            {
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, max ? Color.Wheat : Color.White, 2f, drawShadow: false);
                string label = max
                    ? (delta > 0 ? "+" : "-") + this.Translations.Get("sell.max")
                    : (delta > 0 ? "+" : "") + delta.ToString(CultureInfo.InvariantCulture);
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), Game1.textColor);
            }

            Utility.drawTextWithShadow(b, this.Translations.Get("sell.gold-target"), Game1.smallFont, new Vector2(left, this.GoldBounds.Y + 10), Game1.textColor);
            this.GoldBox.Draw(b);
            if (string.IsNullOrEmpty(this.GoldBox.Text) && !this.GoldBox.Selected)
                Utility.drawTextWithShadow(b, this.Translations.Get("sell.gold-placeholder"), Game1.smallFont, new Vector2(this.GoldBox.X + 16, this.GoldBox.Y + 10), Game1.textColor * 0.4f);

            if (TryParseGold(this.GoldBox.Text, out long target) && target > this.Stored * (long)this.UnitPrice)
                Marquee.DrawWrapped(b, this.Translations.Get("sell.gold-over", new { max = Selling.Gold((double)this.Stored * this.UnitPrice) }), Game1.smallFont, new Vector2(this.GoldBounds.X, this.GoldBounds.Bottom + 6), this.xPositionOnScreen + this.width - 32 - this.GoldBounds.X, Color.Red, maxLines: 1);

            int y = this.GoldBounds.Bottom + 48;
            Utility.drawTextWithShadow(b, this.Translations.Get("sell.total", new { count = NumberFormat.Full(this.Quantity), gold = Selling.Gold((double)this.Quantity * this.UnitPrice) }), Game1.smallFont, new Vector2(left, y), Game1.textColor);
            Utility.drawTextWithShadow(b, this.Translations.Get("sell.max-value", new { count = NumberFormat.Full(this.Stored), gold = Selling.Gold((double)this.Stored * this.UnitPrice) }), Game1.smallFont, new Vector2(left, y + 36), Game1.textColor * 0.75f);

            if (!this.HasBin)
                Marquee.DrawWrapped(b, this.Translations.Get("sell.no-bin"), Game1.smallFont, new Vector2(left, y + 72), this.width - 64, UiTheme.Bad, maxLines: 1);

            bool canShip = this.HasBin && this.Quantity > 0;
            drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), this.CancelButton.X, this.CancelButton.Y, this.CancelButton.Width, this.CancelButton.Height, Color.White, 2f, drawShadow: false);
            UiTheme.DrawButton(b, new Rectangle(this.ShipButton.X, this.ShipButton.Y, this.ShipButton.Width, this.ShipButton.Height), canShip ? Color.LightGreen : Color.Gray, 2f);
            foreach ((Rectangle bounds, string key, bool enabled) in new[] { (this.CancelButton, "sell.cancel", true), (this.ShipButton, "sell.ship", canShip) })
            {
                string label = this.Translations.Get(key);
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), enabled ? Game1.textColor : Color.DimGray);
            }

            base.draw(b);
            if (!string.IsNullOrEmpty(this.HoverText))
                drawHoverText(b, Game1.parseText(this.HoverText, Game1.smallFont, 480), Game1.smallFont);
            this.drawMouse(b);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Positions the boxes and buttons.</summary>
        private void Layout()
        {
            int left = this.xPositionOnScreen + 32;
            int top = this.yPositionOnScreen;

            this.QuantityBox.X = left + 190;
            this.QuantityBox.Y = top + 144;
            this.QuantityBounds = new Rectangle(this.QuantityBox.X, this.QuantityBox.Y, this.QuantityBox.Width, this.QuantityBox.Height);

            // A row taking away and a row adding, each ending in Max.
            int buttonWidth = 110;
            int gap = 8;
            int[] steps = { 1000, 100, 50, 10, 1 };
            int x = left;
            this.StepButtons.Add((new Rectangle(x, top + 204, buttonWidth, 44), -1, true));
            foreach (int step in steps)
            {
                x += buttonWidth + gap;
                this.StepButtons.Add((new Rectangle(x, top + 204, buttonWidth, 44), -step, false));
            }

            x = left;
            foreach (int step in steps.Reverse())
            {
                this.StepButtons.Add((new Rectangle(x, top + 256, buttonWidth, 44), step, false));
                x += buttonWidth + gap;
            }
            this.StepButtons.Add((new Rectangle(x, top + 256, buttonWidth, 44), 1, true));

            this.GoldBox.X = left + 190;
            this.GoldBox.Y = top + 324;
            this.GoldBounds = new Rectangle(this.GoldBox.X, this.GoldBox.Y, this.GoldBox.Width, this.GoldBox.Height);

            int bottom = top + this.height - 88;
            this.ShipButton = new Rectangle(this.xPositionOnScreen + this.width - 32 - 200, bottom, 200, 60);
            this.CancelButton = new Rectangle(this.ShipButton.X - 16 - 180, bottom, 180, 60);
        }

        /// <summary>Sets the count, within what storage holds.</summary>
        private void SetQuantity(long value, bool clearGold)
        {
            this.Quantity = this.Clamp(value);
            this.QuantityBox.Text = this.LastQuantityText = this.Quantity.ToString(CultureInfo.InvariantCulture);
            if (clearGold)
                this.SetGoldText("");
        }

        private void SetGoldText(string text)
        {
            this.GoldBox.Text = this.LastGoldText = text;
        }

        private int Clamp(long value) => (int)Math.Clamp(value, 0, Math.Min(this.Stored, int.MaxValue));

        /// <summary>Moves the chosen count into the shipping bin and closes.</summary>
        private void Ship()
        {
            if (!this.HasBin || this.Quantity <= 0)
            {
                Game1.playSound("cancel");
                return;
            }

            // A farmhand's shipment is made by the host, into the farmhand's own bin.
            if (Multiplayer.MultiplayerSync.IsRemote)
            {
                Multiplayer.MultiplayerSync.Instance?.Send(new Multiplayer.ShipRequest
                {
                    Network = this.NetworkReference,
                    ItemId = this.Entry.Key.QualifiedId,
                    Quality = this.Entry.Key.Quality,
                    Variant = this.Entry.Key.Variant,
                    Unique = this.Entry.Key.Unique,
                    Count = this.Quantity
                }, Multiplayer.MessageTypes.Ship);
                Game1.playSound("Ship");
                this.exitThisMenu();
                return;
            }

            int shipped = ShippingService.Ship(this.Network, this.Entry, this.Quantity);
            Multiplayer.MultiplayerSync.Instance?.NotifyChanged();
            if (shipped <= 0)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(this.Translations.Get("sell.bin-full"), HUDMessage.error_type));
                return;
            }

            Game1.playSound("Ship");
            string message = shipped < this.Quantity
                ? this.Translations.Get("sell.shipped-partly", new { count = NumberFormat.Full(shipped), wanted = NumberFormat.Full(this.Quantity), name = this.Entry.DisplayName, gold = Selling.Gold((double)shipped * this.UnitPrice) })
                : this.Translations.Get("sell.shipped", new { count = NumberFormat.Full(shipped), name = this.Entry.DisplayName, gold = Selling.Gold((double)shipped * this.UnitPrice) });
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.achievement_type));
            Log.Debug($"Shipped {shipped}x {this.Entry.DisplayName} from storage for {shipped * (long)this.UnitPrice}g.");
            this.exitThisMenu();
        }

        /// <summary>Changes a text box's colour, which the game only sets when it's made.</summary>
        private static void SetTextColour(TextBox box, Color colour)
        {
            try
            {
                TextColourField?.SetValue(box, colour);
            }
            catch
            {
                // Just stays the colour it was.
            }
        }

        private static readonly System.Reflection.FieldInfo TextColourField = typeof(TextBox).GetField("_textColor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        /// <summary>Reads a gold amount, allowing thousands separators and a trailing "g".</summary>
        private static bool TryParseGold(string text, out long gold)
        {
            gold = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string clean = new(text.Where(ch => !char.IsWhiteSpace(ch) && ch != ',' && ch != 'g' && ch != 'G').ToArray());
            if (clean.Length == 0)
                return false;
            if (long.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out gold))
                return gold > 0;
            if (MathExpression.TryEvaluate(clean, out int value))
            {
                gold = value;
                return gold > 0;
            }

            return false;
        }
    }
}
