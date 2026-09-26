using System;
using System.Collections.Generic;
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
    /// <summary>Asks how many of a recipe to craft, showing what each batch costs against what the network holds.</summary>
    /// <remarks>
    /// Opened by right-clicking a recipe. Left-click and shift-click stay as one-and-five shortcuts, so this is for
    /// the case where the player wants a specific number. The quantity box takes arithmetic as well as plain digits,
    /// which is quicker than holding a button when the answer is "two stacks" rather than a round number.
    /// </remarks>
    internal class BulkCraftMenu : IClickableMenu
    {
        /*********
        ** Fields
        *********/
        /// <summary>The step sizes offered either side of the quantity box, innermost first.</summary>
        private static readonly int[] Steps = { 1, 10, 25, 50, 100 };

        /// <summary>Sentinel deltas for the buttons that jump straight to a limit rather than stepping.</summary>
        private const int JumpToMax = int.MaxValue;
        private const int JumpToMin = int.MinValue;

        private const int MenuWidth = 1000;
        private const int MenuHeight = 520;
        private const int StepButtonWidth = 58;
        private const int StepButtonGap = 6;
        private const int QuantityBoxWidth = 180;

        private readonly RecipeEntry Entry;
        private readonly IReadOnlyList<IFilterableEntry> Stock;
        private readonly ITranslationHelper Translations;
        private readonly Action<int> OnCraft;

        private readonly List<(Rectangle Bounds, int Delta)> StepButtons = new();
        private readonly TextBox QuantityBox;
        private ClickableComponent QuantityBounds;
        private ClickableComponent CraftButton;

        /// <summary>The last value the box parsed to, kept so malformed input doesn't reset the quantity.</summary>
        private int Quantity = 1;

        private string LastText = "1";
        private string HoverText = "";


        /*********
        ** Public methods
        *********/
        public BulkCraftMenu(RecipeEntry entry, IReadOnlyList<IFilterableEntry> stock, ITranslationHelper translations, Action<int> onCraft)
        {
            this.Entry = entry;
            this.Stock = stock;
            this.Translations = translations;
            this.OnCraft = onCraft;

            this.width = MenuWidth;
            this.height = MenuHeight;
            this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
            this.yPositionOnScreen = (Game1.uiViewport.Height - this.height) / 2;

            this.Quantity = Math.Max(1, Math.Min(entry.CraftableCount, 1));

            // smallFont rather than dialogueFont: the larger face overflowed the box as soon as the value ran
            // past two digits, and an expression like "1+18" never fitted at all.
            this.QuantityBox = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
            {
                X = this.xPositionOnScreen + (this.width / 2) - (QuantityBoxWidth / 2),
                Y = this.yPositionOnScreen + 336,
                Width = QuantityBoxWidth,
                Height = 48,
                Text = this.Quantity.ToString()
            };
            this.LastText = this.QuantityBox.Text;

            this.BuildButtons();
            this.initializeUpperRightCloseButton();
        }

        /// <inheritdoc />
        public override void update(GameTime time)
        {
            base.update(time);

            // The box accepts arithmetic, so re-evaluate as it changes rather than only on submit.
            if (this.QuantityBox.Text != this.LastText)
            {
                this.LastText = this.QuantityBox.Text;
                if (MathExpression.TryEvaluate(this.QuantityBox.Text, out int value))
                    this.Quantity = Math.Clamp(value, 1, Math.Max(1, this.Entry.CraftableCount));
            }
        }

        /// <inheritdoc />
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (this.upperRightCloseButton?.containsPoint(x, y) == true)
            {
                this.exitThisMenu();
                return;
            }

            foreach ((Rectangle bounds, int delta) in this.StepButtons)
            {
                if (!bounds.Contains(x, y))
                    continue;

                this.SetQuantity(delta switch
                {
                    JumpToMax => this.Entry.CraftableCount,
                    JumpToMin => 1,
                    _ => this.Quantity + delta
                });
                Game1.playSound("drumkit6");
                return;
            }

            bool clickedBox = this.QuantityBounds.containsPoint(x, y);
            this.QuantityBox.Selected = clickedBox;
            if (clickedBox)
                return;

            if (this.CraftButton.containsPoint(x, y))
            {
                this.Confirm();
                return;
            }

            // Clicking outside the panel dismisses it, matching how the game's other small dialogs behave.
            if (!new Rectangle(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height).Contains(x, y))
                this.exitThisMenu();
        }

        /// <inheritdoc />
        public override void receiveRightClick(int x, int y, bool playSound = true) { }

        /// <inheritdoc />
        public override void receiveScrollWheelAction(int direction)
        {
            this.SetQuantity(this.Quantity + (direction > 0 ? 1 : -1));
        }

        /// <inheritdoc />
        public override void receiveKeyPress(Keys key)
        {
            if (this.QuantityBox.Selected)
            {
                if (key == Keys.Escape)
                {
                    this.QuantityBox.Selected = false;
                    return;
                }
                if (key == Keys.Enter)
                {
                    this.Confirm();
                    return;
                }
                return;
            }

            if (key == Keys.Escape || Game1.options.doesInputListContain(Game1.options.menuButton, key))
            {
                this.exitThisMenu();
                return;
            }

            if (key == Keys.Enter)
                this.Confirm();
        }

        /// <inheritdoc />
        public override void performHoverAction(int x, int y)
        {
            this.HoverText = "";

            if (this.CraftButton.containsPoint(x, y))
            {
                this.HoverText = this.Entry.CraftableCount > 0
                    ? this.Translations.Get("bulk.craft-hint", new { count = this.Quantity })
                    : this.Translations.Get("error.missing-ingredients");
            }
        }

        /// <inheritdoc />
        protected override void cleanupBeforeExit()
        {
            if (Game1.keyboardDispatcher.Subscriber == this.QuantityBox)
                Game1.keyboardDispatcher.Subscriber = null;

            base.cleanupBeforeExit();
        }

        /// <inheritdoc />
        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.5f);
            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White, 1f, drawShadow: true);

            this.DrawHeading(b);
            this.DrawIngredients(b);
            this.DrawQuantityRow(b);
            this.DrawCraftButton(b);

            this.upperRightCloseButton?.draw(b);

            if (!string.IsNullOrEmpty(this.HoverText))
                drawHoverText(b, this.HoverText, Game1.smallFont);

            this.drawMouse(b);
        }


        /*********
        ** Private methods: layout
        *********/
        /// <summary>Builds the increment buttons and the craft button.</summary>
        /// <remarks>
        /// The row is laid out left to right as a single strip rather than growing outwards from the box, which
        /// is what pushed the outermost buttons through the menu's edge.
        /// </remarks>
        private void BuildButtons()
        {
            // Decrements descend towards the box; increments ascend away from it. Min and Max sit furthest out.
            List<int> left = new() { JumpToMin };
            left.AddRange(Steps.Reverse().Select(step => -step));

            List<int> right = new(Steps) { JumpToMax };

            int stride = StepButtonWidth + StepButtonGap;
            int rowWidth = ((left.Count + right.Count) * stride) + QuantityBoxWidth + StepButtonGap;
            int x = this.xPositionOnScreen + ((this.width - rowWidth) / 2);
            int y = this.yPositionOnScreen + 340;

            foreach (int delta in left)
            {
                this.StepButtons.Add((new Rectangle(x, y, StepButtonWidth, 40), delta));
                x += stride;
            }

            this.QuantityBox.X = x;
            x += QuantityBoxWidth + StepButtonGap;

            foreach (int delta in right)
            {
                this.StepButtons.Add((new Rectangle(x, y, StepButtonWidth, 40), delta));
                x += stride;
            }

            this.QuantityBounds = new ClickableComponent(new Rectangle(this.QuantityBox.X, this.QuantityBox.Y, this.QuantityBox.Width, this.QuantityBox.Height), "quantity");
            this.CraftButton = new ClickableComponent(new Rectangle(this.xPositionOnScreen + (this.width / 2) - 110, this.yPositionOnScreen + this.height - 104, 220, 64), "craft");
        }

        /// <summary>Clamps and stores a new quantity, keeping the text box in step.</summary>
        private void SetQuantity(int value)
        {
            this.Quantity = Math.Clamp(value, 1, Math.Max(1, this.Entry.CraftableCount));
            this.QuantityBox.Text = this.Quantity.ToString();
            this.LastText = this.QuantityBox.Text;
        }

        /// <summary>Crafts the chosen quantity and closes.</summary>
        private void Confirm()
        {
            if (this.Entry.CraftableCount <= 0)
            {
                Game1.playSound("cancel");
                return;
            }

            int count = Math.Clamp(this.Quantity, 1, this.Entry.CraftableCount);
            this.exitThisMenu();
            this.OnCraft(count);
        }


        /*********
        ** Private methods: drawing
        *********/
        /// <summary>Draws the title, the recipe icon and how many batches are possible.</summary>
        private void DrawHeading(SpriteBatch b)
        {
            string title = this.Translations.Get("bulk.title");
            Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
            Utility.drawTextWithShadow(b, title, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + (this.width / 2) - (titleSize.X / 2), this.yPositionOnScreen + 28), Game1.textColor);

            int y = this.yPositionOnScreen + 92;
            this.Entry.Output.drawInMenu(b, new Vector2(this.xPositionOnScreen + 36, y - 8), 1f, 1f, 0.9f, StackDrawType.Hide, Color.White, drawShadow: true);

            Utility.drawTextWithShadow(b, this.Entry.DisplayName, Game1.dialogueFont, new Vector2(this.xPositionOnScreen + 116, y), Game1.textColor);
            Utility.drawTextWithShadow(
                b,
                this.Translations.Get("bulk.craftable", new { count = this.Entry.CraftableCount }),
                Game1.smallFont,
                new Vector2(this.xPositionOnScreen + 116, y + 44),
                this.Entry.CraftableCount > 0 ? Game1.textColor : Color.Firebrick
            );
        }

        /// <summary>Draws the ingredient cost for the chosen quantity, in two columns.</summary>
        private void DrawIngredients(SpriteBatch b)
        {
            int panelX = this.xPositionOnScreen + 32;
            int panelY = this.yPositionOnScreen + 176;
            int panelWidth = this.width - 64;

            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), panelX, panelY, panelWidth, 132, Color.White * 0.85f, 1f, drawShadow: false);

            List<KeyValuePair<string, int>> ingredients = this.Entry.Recipe.recipeList.ToList();
            int columnWidth = panelWidth / 2;

            for (int i = 0; i < ingredients.Count && i < 6; i++)
            {
                KeyValuePair<string, int> ingredient = ingredients[i];
                int column = i % 2;
                int row = i / 2;

                int x = panelX + 20 + (column * columnWidth);
                int y = panelY + 18 + (row * 38);

                long needed = (long)ingredient.Value * this.Quantity;
                long have = this.Entry.CountAvailable(ingredient.Key, this.Stock, includePlayerInventory: true);

                // The ingredient icon: recipes can ask for a category ("any egg"), so the game resolves which
                // sprite stands for the requirement rather than us assuming the ID is a real item.
                Item icon = this.TryCreateIcon(ingredient.Key);
                if (icon != null)
                    icon.drawInMenu(b, new Vector2(x - 16, y - 20), 0.5f, 1f, 0.9f, StackDrawType.Hide, Color.White, drawShadow: false);

                string name = this.Entry.Recipe.getNameFromIndex(ingredient.Key);
                Marquee.Draw(b, name, Game1.smallFont, new Vector2(x + 30, y), columnWidth - 160, Game1.textColor);

                string counts = $"{have} / {needed}";
                Vector2 size = Game1.smallFont.MeasureString(counts);
                Utility.drawTextWithShadow(
                    b,
                    counts,
                    Game1.smallFont,
                    new Vector2(x + columnWidth - size.X - 44, y),
                    have >= needed ? Game1.textColor : Color.Firebrick
                );
            }
        }

        /// <summary>Draws the increment buttons and the quantity box.</summary>
        private void DrawQuantityRow(SpriteBatch b)
        {
            foreach ((Rectangle bounds, int delta) in this.StepButtons)
            {
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White, 2f, drawShadow: false);

                string label = delta switch
                {
                    JumpToMax => this.Translations.Get("bulk.max"),
                    JumpToMin => this.Translations.Get("bulk.min"),
                    _ => delta > 0 ? "+" + delta : delta.ToString()
                };
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), Game1.textColor);
            }

            this.QuantityBox.Draw(b);

            // No separate "resolved value" line: the craft button already reads "Craft 19", which says the same
            // thing in the place the player is about to click.
        }

        /// <summary>Draws the confirm button.</summary>
        private void DrawCraftButton(SpriteBatch b)
        {
            bool enabled = this.Entry.CraftableCount > 0;
            Rectangle bounds = this.CraftButton.bounds;

            drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, enabled ? Color.White : Color.Gray, 3f, drawShadow: false);

            string label = this.Translations.Get("bulk.craft", new { count = this.Quantity });
            Vector2 size = Game1.smallFont.MeasureString(label);
            Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), enabled ? Game1.textColor : Color.DimGray);
        }

        /// <summary>Builds a drawable icon for an ingredient, which may be a category rather than a specific item.</summary>
        private Item TryCreateIcon(string ingredientId)
        {
            try
            {
                string resolved = this.Entry.Recipe.getSpriteIndexFromRawIndex(ingredientId);
                return ItemRegistry.Create(resolved, 1, 0, allowNull: true)
                    ?? ItemRegistry.Create(ingredientId, 1, 0, allowNull: true);
            }
            catch
            {
                return null;
            }
        }
    }
}
