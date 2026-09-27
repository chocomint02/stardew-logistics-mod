using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewLogistics.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewLogistics.Menus
{
    /// <summary>Adds an entry to the expense plan: something to save for, or what an input item costs.</summary>
    internal class ExpenseInputMenu : IClickableMenu
    {
        /// <summary>Hover highlights and click ripples on the menu's controls.</summary>
        private readonly UiFx Fx = new();

        /*********
        ** Fields
        *********/
        private readonly ITranslationHelper Translations;
        private readonly Action OnClose;

        /// <summary>Whether this sets an item's cost, rather than adding a planned expense.</summary>
        private readonly bool ForItem;
        private readonly Action<string, long> OnSave;
        private readonly List<Item> Candidates;

        private readonly TextBox NameBox;
        private readonly TextBox AmountBox;
        private readonly DropdownPopup Dropdown = new();
        private Rectangle NameBounds;
        private Rectangle AmountBounds;
        private Rectangle SaveButton;
        private Rectangle CancelButton;
        private Item SelectedItem;

        /// <summary>What shops charge for the chosen item, as buttons that fill in the cost.</summary>
        private List<(string Vendor, int Price)> Vendors = new();
        private readonly List<(Rectangle Bounds, int Price)> VendorButtons = new();


        /*********
        ** Public methods
        *********/
        /// <summary>Constructs the window.</summary>
        /// <param name="translations">The mod's translations.</param>
        /// <param name="forItem">Whether this sets an item's cost, rather than adding a planned expense.</param>
        /// <param name="candidates">The items to choose from, when setting a cost.</param>
        /// <param name="onSave">Saves the entry: the expense's name or the item's ID, and the amount.</param>
        /// <param name="onClose">Called when the window closes, saved or not.</param>
        public ExpenseInputMenu(ITranslationHelper translations, bool forItem, List<Item> candidates, Action<string, long> onSave, Action onClose)
        {
            this.Translations = translations;
            this.ForItem = forItem;
            this.Candidates = candidates ?? new List<Item>();
            this.OnSave = onSave;
            this.OnClose = onClose;

            this.width = 720;
            this.height = forItem ? 470 : 380;
            this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
            this.yPositionOnScreen = (Game1.uiViewport.Height - this.height) / 2;

            Texture2D textBox = UiTheme.TextBoxTexture();
            this.NameBox = new TextBox(textBox, null, Game1.smallFont, UiTheme.TextColour) { X = this.xPositionOnScreen + 220, Y = this.yPositionOnScreen + 110, Width = 460, Height = 44 };
            this.AmountBox = new TextBox(textBox, null, Game1.smallFont, UiTheme.TextColour) { X = this.xPositionOnScreen + 220, Y = this.yPositionOnScreen + 180, Width = 260, Height = 44 };
            this.NameBounds = new Rectangle(this.NameBox.X, this.NameBox.Y, this.NameBox.Width, this.NameBox.Height);
            this.AmountBounds = new Rectangle(this.AmountBox.X, this.AmountBox.Y, this.AmountBox.Width, this.AmountBox.Height);

            int bottom = this.yPositionOnScreen + this.height - 88;
            this.SaveButton = new Rectangle(this.xPositionOnScreen + this.width - 32 - 180, bottom, 180, 60);
            this.CancelButton = new Rectangle(this.SaveButton.X - 16 - 180, bottom, 180, 60);

            if (!forItem)
                this.NameBox.Selected = true;
            this.initializeUpperRightCloseButton();
        }

        /// <inheritdoc />
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (this.Dropdown.IsOpen)
            {
                if (this.Dropdown.ReceiveLeftClick(x, y, out object chosen))
                {
                    if (chosen is Item item)
                        this.SelectItem(item);
                    return;
                }
            }

            if (this.upperRightCloseButton?.containsPoint(x, y) == true || this.CancelButton.Contains(x, y))
            {
                this.exitThisMenu();
                return;
            }

            if (this.SaveButton.Contains(x, y))
            {
                this.Save();
                return;
            }

            foreach ((Rectangle bounds, int price) in this.VendorButtons)
            {
                if (bounds.Contains(x, y))
                {
                    this.AmountBox.Text = price.ToString();
                    Game1.playSound("coin");
                    return;
                }
            }

            // The item picker sits where the name box would.
            if (this.ForItem && this.NameBounds.Contains(x, y))
            {
                this.Dropdown.Open(this.Candidates.Select(item => (item.DisplayName, (object)item, item)), this.NameBounds);
                Game1.playSound("shwip");
                return;
            }

            this.NameBox.Selected = !this.ForItem && this.NameBounds.Contains(x, y);
            this.AmountBox.Selected = this.AmountBounds.Contains(x, y);
        }

        /// <inheritdoc />
        public override void receiveScrollWheelAction(int direction)
        {
            this.Dropdown.ReceiveScroll(direction);
        }

        /// <inheritdoc />
        public override void performHoverAction(int x, int y)
        {
            if (this.Dropdown.IsOpen)
                this.Dropdown.PerformHover(x, y);
        }

        /// <inheritdoc />
        public override void receiveKeyPress(Keys key)
        {
            if (this.NameBox.Selected || this.AmountBox.Selected)
            {
                if (key == Keys.Enter)
                    this.Save();
                else if (key == Keys.Tab && !this.ForItem)
                {
                    bool toAmount = this.NameBox.Selected;
                    this.NameBox.Selected = !toAmount;
                    this.AmountBox.Selected = toAmount;
                }
                else if (key == Keys.Escape)
                {
                    this.NameBox.Selected = false;
                    this.AmountBox.Selected = false;
                }
                return;
            }

            if (key == Keys.Escape || Game1.options.doesInputListContain(Game1.options.menuButton, key))
                this.exitThisMenu();
        }

        /// <inheritdoc />
        protected override void cleanupBeforeExit()
        {
            if (Game1.keyboardDispatcher.Subscriber == this.NameBox || Game1.keyboardDispatcher.Subscriber == this.AmountBox)
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
            Utility.drawTextWithShadow(b, this.Translations.Get(this.ForItem ? "expense.add-cost-title" : "expense.add-title"), Game1.dialogueFont, new Vector2(left, this.yPositionOnScreen + 28), Game1.textColor);

            Utility.drawTextWithShadow(b, this.Translations.Get(this.ForItem ? "expense.item" : "expense.name"), Game1.smallFont, new Vector2(left, this.NameBounds.Y + 10), Game1.textColor);
            if (this.ForItem)
            {
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), this.NameBounds.X, this.NameBounds.Y, this.NameBounds.Width, this.NameBounds.Height, Color.White, 2f, drawShadow: false);
                this.Fx.Control(b, this.NameBounds);
                if (this.SelectedItem != null)
                {
                    ItemIcon.Draw(b, this.SelectedItem, new Rectangle(this.NameBounds.X + 8, this.NameBounds.Y + 6, 32, 32), 1f, showQuality: false);
                    Marquee.Draw(b, this.SelectedItem.DisplayName, Game1.smallFont, new Vector2(this.NameBounds.X + 48, this.NameBounds.Y + 10), this.NameBounds.Width - 60, Game1.textColor);
                }
                else
                    Utility.drawTextWithShadow(b, this.Translations.Get("expense.pick-item"), Game1.smallFont, new Vector2(this.NameBounds.X + 12, this.NameBounds.Y + 10), Game1.textColor * 0.5f);
            }
            else
                this.NameBox.Draw(b);

            Utility.drawTextWithShadow(b, this.Translations.Get(this.ForItem ? "expense.cost-each" : "expense.amount"), Game1.smallFont, new Vector2(left, this.AmountBounds.Y + 10), Game1.textColor);
            this.AmountBox.Draw(b);
            Utility.drawTextWithShadow(b, "g", Game1.smallFont, new Vector2(this.AmountBounds.Right + 10, this.AmountBounds.Y + 10), Game1.textColor);

            // Shop prices for the chosen item, one click to use.
            this.VendorButtons.Clear();
            if (this.ForItem && this.SelectedItem != null)
            {
                int vy = this.AmountBounds.Bottom + 20;
                if (this.Vendors.Count == 0)
                    Utility.drawTextWithShadow(b, this.Translations.Get("expense.no-vendor"), Game1.smallFont, new Vector2(left, vy + 8), Game1.textColor * 0.6f);
                else
                {
                    Utility.drawTextWithShadow(b, this.Translations.Get("expense.vendor-label"), Game1.smallFont, new Vector2(left, vy + 8), Game1.textColor);
                    int vx = this.AmountBounds.X;
                    foreach ((string vendor, int price) in this.Vendors.Take(3))
                    {
                        string label = this.Translations.Get("expense.vendor-price", new { vendor, gold = Selling.Gold(price) });
                        int width = (int)Game1.smallFont.MeasureString(label).X + 28;
                        if (vx + width > this.xPositionOnScreen + this.width - 32)
                            break;

                        Rectangle bounds = new(vx, vy, width, 44);
                        this.VendorButtons.Add((bounds, price));
                        drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White, 2f, drawShadow: false);
                        this.Fx.Control(b, bounds);
                        Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.X + 14, bounds.Y + 10), Game1.textColor);
                        vx += width + 8;
                    }
                }
            }

            bool canSave = this.TryGetEntry(out _, out _);
            foreach ((Rectangle bounds, string key, Color tint) in new[] { (this.CancelButton, "sell.cancel", Color.White), (this.SaveButton, "expense.save", canSave ? Color.LightGreen : Color.Gray) })
            {
                UiTheme.DrawButton(b, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height), tint, 2f);
                if (tint != Color.Gray)
                    this.Fx.Control(b, bounds);
                string label = this.Translations.Get(key);
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), Game1.textColor);
            }

            base.draw(b);
            this.Dropdown.Draw(b);
            this.drawMouse(b);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Chooses the item a cost is for, and looks up what shops charge for it.</summary>
        private void SelectItem(Item item)
        {
            this.SelectedItem = item;
            this.Vendors = VendorPrices.For(item);

            // The cheapest shop's price is the likeliest cost; fill it in unless one's been typed.
            if (this.Vendors.Count > 0 && string.IsNullOrWhiteSpace(this.AmountBox.Text))
                this.AmountBox.Text = this.Vendors[0].Price.ToString();
        }

        /// <summary>The entry as typed, if it's complete.</summary>
        private bool TryGetEntry(out string key, out long amount)
        {
            key = this.ForItem ? this.SelectedItem?.QualifiedItemId : this.NameBox.Text?.Trim();
            string clean = new((this.AmountBox.Text ?? "").Where(ch => char.IsDigit(ch)).ToArray());
            return long.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out amount)
                && amount >= 0
                && !string.IsNullOrWhiteSpace(key);
        }

        private void Save()
        {
            if (!this.TryGetEntry(out string key, out long amount))
            {
                Game1.playSound("cancel");
                return;
            }

            this.OnSave?.Invoke(key, amount);
            Game1.playSound("coin");
            this.exitThisMenu();
        }
    }
}
