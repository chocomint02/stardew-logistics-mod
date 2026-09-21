using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewLogistics.Menus
{
    /// <summary>Draws the hover panel for a recipe, counting ingredients against the network.</summary>
    /// <remarks>
    /// The vanilla <see cref="IClickableMenu.drawToolTip"/> can't be used here for two reasons. It counts a
    /// recipe's ingredients against the player's own inventory, so every line read "0" however much the network
    /// held; and when handed both a hovered item and a recipe it drew the description twice, once measured and
    /// once not, which is what pushed text outside the frame.
    ///
    /// Drawing it here means the panel is measured before it is drawn, so it always fits, and the counts come
    /// from the same aggregated stock the rest of the terminal uses.
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

            string title = entry.DisplayName;
            string description = Game1.parseText(entry.Recipe.description ?? "", Game1.smallFont, MaxTextWidth);
            string heading = translations.Get("ui.ingredients");

            List<Line> lines = BuildIngredientLines(entry, stock, translations);

            // Measure everything before drawing any of it, so the frame can't be smaller than its contents.
            Vector2 titleSize = Game1.smallFont.MeasureString(title);
            Vector2 descriptionSize = string.IsNullOrWhiteSpace(description) ? Vector2.Zero : Game1.smallFont.MeasureString(description);
            Vector2 headingSize = Game1.smallFont.MeasureString(heading);

            float contentWidth = Math.Max(titleSize.X, Math.Max(descriptionSize.X, headingSize.X));
            foreach (Line line in lines)
                contentWidth = Math.Max(contentWidth, IconSize + 12 + line.NameWidth + 40 + line.CountWidth);

            int width = (int)contentWidth + (Padding * 2);
            int height = (int)(titleSize.Y + 8
                + (descriptionSize.Y > 0 ? descriptionSize.Y + 12 : 0)
                + (lines.Count > 0 ? headingSize.Y + 6 + (lines.Count * RowHeight) : 0))
                + (Padding * 2);

            int x = mouseX + 32;
            int y = mouseY + 32;
            if (x + width > Game1.uiViewport.Width)
                x = Math.Max(0, mouseX - width - 16);
            if (y + height > Game1.uiViewport.Height)
                y = Math.Max(0, Game1.uiViewport.Height - height - 8);

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), x, y, width, height, Color.White, 1f, drawShadow: true);

            int textX = x + Padding;
            int textY = y + Padding;

            Utility.drawTextWithShadow(b, title, Game1.smallFont, new Vector2(textX, textY), Game1.textColor);
            textY += (int)titleSize.Y + 8;

            if (descriptionSize.Y > 0)
            {
                Utility.drawTextWithShadow(b, description, Game1.smallFont, new Vector2(textX, textY), Game1.textColor * 0.85f);
                textY += (int)descriptionSize.Y + 12;
            }

            if (lines.Count == 0)
                return;

            Utility.drawTextWithShadow(b, heading, Game1.smallFont, new Vector2(textX, textY), Game1.textColor);
            textY += (int)headingSize.Y + 6;

            foreach (Line line in lines)
            {
                line.Icon?.drawInMenu(b, new Vector2(textX - 16, textY - 16), 0.5f, 1f, 0.9f, StackDrawType.Hide, Color.White, drawShadow: false);
                Utility.drawTextWithShadow(b, line.Name, Game1.smallFont, new Vector2(textX + IconSize + 12, textY + 4), Game1.textColor);

                Utility.drawTextWithShadow(
                    b,
                    line.Counts,
                    Game1.smallFont,
                    new Vector2(x + width - Padding - line.CountWidth, textY + 4),
                    line.Enough ? Game1.textColor : Color.Firebrick
                );

                textY += RowHeight;
            }
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Builds one measured row per ingredient.</summary>
        private static List<Line> BuildIngredientLines(RecipeEntry entry, IReadOnlyList<IFilterableEntry> stock, ITranslationHelper translations)
        {
            List<Line> lines = new();

            foreach (KeyValuePair<string, int> ingredient in entry.Recipe.recipeList)
            {
                long have = entry.CountAvailable(ingredient.Key, stock, includePlayerInventory: true);
                string name = entry.Recipe.getNameFromIndex(ingredient.Key) ?? ingredient.Key;
                string counts = $"{NumberFormat.Abbreviate(have)} / {ingredient.Value}";

                lines.Add(new Line
                {
                    Icon = entry.CreateIngredientIcon(ingredient.Key),
                    Name = name,
                    Counts = counts,
                    Enough = have >= ingredient.Value,
                    NameWidth = Game1.smallFont.MeasureString(name).X,
                    CountWidth = Game1.smallFont.MeasureString(counts).X
                });
            }

            return lines;
        }


        /*********
        ** Nested types
        *********/
        /// <summary>One measured ingredient row.</summary>
        private class Line
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
