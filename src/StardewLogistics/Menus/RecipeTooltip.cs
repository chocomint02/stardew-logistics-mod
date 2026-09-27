using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using SObject = StardewValley.Object;

namespace StardewLogistics.Menus
{
    /// <summary>Draws the hover panel for a recipe, counting ingredients against the network.</summary>
    /// <remarks>
    /// The vanilla <see cref="IClickableMenu.drawToolTip"/> can't be used here for two reasons. It counts a
    /// recipe's ingredients against the player's own inventory, so every line read "0" however much the network
    /// held; and when handed both a hovered item and a recipe it drew the description twice, once measured and
    /// once not, which pushed text outside the frame.
    ///
    /// Drawing it here means the panel is measured before it is drawn, so it always fits, and the counts come
    /// from the same aggregated stock the rest of the terminal uses. The type face and the coloured category line
    /// deliberately mirror the game's own item tooltips, so the two tabs don't look like different mods.
    /// </remarks>
    internal static class RecipeTooltip
    {
        /*********
        ** Fields
        *********/
        private const int Padding = 20;
        private const int RowHeight = 36;
        private const int IconSize = 32;
        private const int MaxTextWidth = 460;


        /*********
        ** Public methods
        *********/
        /// <summary>Draws the panel near the cursor, kept inside the screen.</summary>
        /// <param name="b">The sprite batch.</param>
        /// <param name="entry">The recipe being hovered.</param>
        /// <param name="stock">The network's aggregated stock, used for the have/need counts.</param>
        /// <param name="translations">The mod's translations.</param>
        /// <param name="mouseX">The cursor X position.</param>
        /// <param name="mouseY">The cursor Y position.</param>
        public static void Draw(SpriteBatch b, RecipeEntry entry, IReadOnlyList<IFilterableEntry> stock, ITranslationHelper translations, int mouseX, int mouseY)
        {
            if (entry == null)
                return;

            List<Section> sections = new() { new Section(translations.Get("ui.ingredients"), BuildIngredientLines(entry, stock)) };
            DrawPanel(b, entry.DisplayName, entry.Output, entry.Recipe.description, sections, null, mouseX, mouseY);
        }

        /// <summary>Draws a panel for an item: its name, category and description, then what it's made from.</summary>
        /// <param name="b">The sprite batch.</param>
        /// <param name="title">The item's name.</param>
        /// <param name="output">The item, for its category.</param>
        /// <param name="description">The item's description.</param>
        /// <param name="sections">What it's made from, each way under its own heading.</param>
        /// <param name="footer">A faint line at the foot, if any.</param>
        /// <param name="mouseX">The cursor X position.</param>
        /// <param name="mouseY">The cursor Y position.</param>
        public static void DrawPanel(SpriteBatch b, string title, Item output, string description, IReadOnlyList<Section> sections, string footer, int mouseX, int mouseY)
        {
            // Match the game's item tooltips: dialogueFont for the name, smallFont for everything under it.
            string category = output?.getCategoryName() ?? "";
            Color categoryColour = output is SObject obj ? obj.getCategoryColor() : Game1.textColor;
            description = Game1.parseText(description ?? "", Game1.smallFont, MaxTextWidth);
            sections = sections.Where(section => section.Lines.Count > 0).ToList();

            // Measure everything before drawing any of it, so the frame can't be smaller than its contents.
            Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
            Vector2 categorySize = category.Length > 0 ? Game1.smallFont.MeasureString(category) : Vector2.Zero;
            Vector2 descriptionSize = string.IsNullOrWhiteSpace(description) ? Vector2.Zero : Game1.smallFont.MeasureString(description);
            Vector2 footerSize = string.IsNullOrEmpty(footer) ? Vector2.Zero : Game1.smallFont.MeasureString(footer);
            float headingHeight = Game1.smallFont.MeasureString("Ay").Y;

            float contentWidth = Math.Max(titleSize.X, Math.Max(categorySize.X, Math.Max(descriptionSize.X, footerSize.X)));
            foreach (Section section in sections)
            {
                contentWidth = Math.Max(contentWidth, Game1.smallFont.MeasureString(section.Heading).X);
                foreach (Line line in section.Lines)
                    contentWidth = Math.Max(contentWidth, IconSize + 12 + line.NameWidth + 40 + line.CountWidth);
            }

            bool hasDivider = sections.Count > 0;

            int width = (int)contentWidth + (Padding * 2);
            int height = (int)(
                titleSize.Y + 4
                + (categorySize.Y > 0 ? categorySize.Y + 8 : 0)
                + (descriptionSize.Y > 0 ? descriptionSize.Y + 12 : 0)
                + (hasDivider ? 20 : 0)
                + sections.Sum(section => headingHeight + 6 + (section.Lines.Count * RowHeight) + 8)
                + (footerSize.Y > 0 ? footerSize.Y + 4 : 0)
            ) + (Padding * 2);

            int x = mouseX + 32;
            int y = mouseY + 32;
            if (x + width > Game1.uiViewport.Width)
                x = Math.Max(0, mouseX - width - 16);
            if (y + height > Game1.uiViewport.Height)
                y = Math.Max(0, Game1.uiViewport.Height - height - 8);

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), x, y, width, height, Color.White, 1f, drawShadow: true);

            int textX = x + Padding;
            int textY = y + Padding;

            Utility.drawTextWithShadow(b, title, Game1.dialogueFont, new Vector2(textX, textY), Game1.textColor);
            textY += (int)titleSize.Y + 4;

            if (categorySize.Y > 0)
            {
                Utility.drawTextWithShadow(b, category, Game1.smallFont, new Vector2(textX, textY), categoryColour);
                textY += (int)categorySize.Y + 8;
            }

            if (descriptionSize.Y > 0)
            {
                Utility.drawTextWithShadow(b, description, Game1.smallFont, new Vector2(textX, textY), Game1.textColor * 0.85f);
                textY += (int)descriptionSize.Y + 12;
            }

            if (hasDivider)
            {
                DrawDivider(b, textX, textY + 4, (int)contentWidth);
                textY += 20;
            }

            foreach (Section section in sections)
            {
                Utility.drawTextWithShadow(b, section.Heading, Game1.smallFont, new Vector2(textX, textY), Game1.textColor);
                textY += (int)headingHeight + 6;

                foreach (Line line in section.Lines)
                {
                    line.Icon?.drawInMenu(b, new Vector2(textX - 16, textY - 16), 0.5f, 1f, 0.9f, StackDrawType.Hide, Color.White, drawShadow: false);
                    Utility.drawTextWithShadow(b, line.Name, Game1.smallFont, new Vector2(textX + IconSize + 12, textY + 4), Game1.textColor);
                    Utility.drawTextWithShadow(
                        b,
                        line.Counts,
                        Game1.smallFont,
                        new Vector2(x + width - Padding - line.CountWidth, textY + 4),
                        line.Enough ? Game1.textColor : UiTheme.Bad
                    );
                    textY += RowHeight;
                }
                textY += 8;
            }

            if (footerSize.Y > 0)
                Utility.drawTextWithShadow(b, footer, Game1.smallFont, new Vector2(textX, textY), Game1.textColor * 0.6f);
        }

        /// <summary>One ingredient row: how many storage has against how many are needed.</summary>
        public static Line Ingredient(Item icon, string name, long have, int need)
        {
            string counts = $"{NumberFormat.Abbreviate(have)} / {need}";
            return new Line
            {
                Icon = icon,
                Name = name,
                Counts = counts,
                Enough = have >= need,
                NameWidth = Game1.smallFont.MeasureString(name).X,
                CountWidth = Game1.smallFont.MeasureString(counts).X
            };
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Draws the rule between the description and the ingredient list.</summary>
        /// <remarks>Two tones rather than one flat line, so it reads as engraved against the menu background.</remarks>
        private static void DrawDivider(SpriteBatch b, int x, int y, int width)
        {
            b.Draw(Game1.staminaRect, new Rectangle(x, y, width, 2), new Color(112, 74, 42) * 0.55f);
            b.Draw(Game1.staminaRect, new Rectangle(x, y + 2, width, 2), new Color(255, 232, 178) * 0.55f);
        }

        /// <summary>Builds one measured row per ingredient.</summary>
        private static List<Line> BuildIngredientLines(RecipeEntry entry, IReadOnlyList<IFilterableEntry> stock)
        {
            List<Line> lines = new();

            foreach (KeyValuePair<string, int> ingredient in entry.Recipe.recipeList)
            {
                long have = entry.CountAvailable(ingredient.Key, stock, includePlayerInventory: true);
                string name = entry.Recipe.getNameFromIndex(ingredient.Key) ?? ingredient.Key;
                lines.Add(Ingredient(entry.CreateIngredientIcon(ingredient.Key), name, have, ingredient.Value));
            }

            return lines;
        }


        /*********
        ** Nested types
        *********/
        /// <summary>What an item's made from one way, under a heading.</summary>
        public record Section(string Heading, List<Line> Lines);

        /// <summary>One measured ingredient row.</summary>
        public class Line
        {
            public Item Icon;
            public string Name;
            public string Counts;
            public bool Enough;
            public float NameWidth;
            public float CountWidth;
        }
    }
}
