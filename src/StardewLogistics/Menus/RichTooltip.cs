using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace StardewLogistics.Menus
{
    /// <summary>A hover tooltip whose lines can mix colours and carry a colour key and an icon.</summary>
    /// <remarks>
    /// The game's hover text is one colour throughout. This draws in the same box, placed the same way beside the
    /// cursor, so a graph's tooltip can colour each source as its legend does and each amount by how good it is.
    /// </remarks>
    internal class RichTooltip
    {
        /*********
        ** Fields
        *********/
        /// <summary>The size of a line's colour key and icon.</summary>
        private const int KeySize = 20;

        /// <summary>The space inside the box's border.</summary>
        private const int Padding = 20;

        /// <summary>The lines, top to bottom.</summary>
        private readonly List<TooltipLine> Lines = new();


        /*********
        ** Nested types
        *********/
        /// <summary>One line: coloured runs of text, after an optional colour key, icon and indent.</summary>
        private class TooltipLine
        {
            public List<(string Text, Color? Colour)> Parts { get; } = new();
            public Color? Key { get; init; }
            public Item Icon { get; init; }
            public int Indent { get; init; }
        }


        /*********
        ** Public methods
        *********/
        /// <summary>Starts a new line.</summary>
        /// <param name="key">A colour square to draw first, like a legend's.</param>
        /// <param name="icon">An item icon to draw after the key.</param>
        /// <param name="indent">How far to indent the line.</param>
        public RichTooltip Line(Color? key = null, Item icon = null, int indent = 0)
        {
            this.Lines.Add(new TooltipLine { Key = key, Icon = icon, Indent = indent });
            return this;
        }

        /// <summary>Adds text to the current line.</summary>
        /// <param name="text">The text.</param>
        /// <param name="colour">Its colour, or <c>null</c> for the text colour when it's drawn (the colour scheme's, in a themed menu).</param>
        public RichTooltip Add(string text, Color? colour = null)
        {
            if (this.Lines.Count == 0)
                this.Line();

            this.Lines[^1].Parts.Add((text, colour));
            return this;
        }

        /// <summary>Draws the tooltip beside the cursor, kept on screen.</summary>
        public void Draw(SpriteBatch b)
        {
            if (this.Lines.Count == 0)
                return;

            SpriteFont font = Game1.smallFont;
            int lineHeight = (int)Math.Max(font.MeasureString("Ay").Y, KeySize) + 4;
            int width = this.Lines.Max(this.MeasureLine) + (Padding * 2);
            int height = (this.Lines.Count * lineHeight) + (Padding * 2) - 4;

            // Placed as the game places hover text: below and right of the cursor, flipped to stay on screen.
            int x = Game1.getOldMouseX() + 32;
            int y = Game1.getOldMouseY() + 32;
            if (x + width > Game1.uiViewport.Width)
                x = Math.Max(0, Game1.getOldMouseX() - width - 16);
            if (y + height > Game1.uiViewport.Height)
                y = Math.Max(0, Game1.getOldMouseY() - height - 16);

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), x, y, width, height, Color.White, 1f, drawShadow: true);

            int lineY = y + Padding - 2;
            foreach (TooltipLine line in this.Lines)
            {
                int lineX = x + Padding + line.Indent;
                int middle = lineY + ((lineHeight - 4) / 2);

                if (line.Key is Color key)
                {
                    b.Draw(Game1.staminaRect, new Rectangle(lineX, middle - (KeySize / 2) + 2, KeySize - 4, KeySize - 4), key);
                    lineX += KeySize + 2;
                }

                if (line.Icon != null)
                {
                    ItemIcon.Draw(b, line.Icon, new Rectangle(lineX, middle - (KeySize / 2), KeySize, KeySize), showQuality: false);
                    lineX += KeySize + 4;
                }

                foreach ((string text, Color? colour) in line.Parts)
                {
                    Utility.drawTextWithShadow(b, text, font, new Vector2(lineX, lineY), colour ?? Game1.textColor);
                    lineX += (int)font.MeasureString(text).X;
                }

                lineY += lineHeight;
            }
        }


        /*********
        ** Private methods
        *********/
        /// <summary>How wide a line is drawn.</summary>
        private int MeasureLine(TooltipLine line)
        {
            int width = line.Indent + line.Parts.Sum(part => (int)Game1.smallFont.MeasureString(part.Text).X);
            if (line.Key != null)
                width += KeySize + 2;
            if (line.Icon != null)
                width += KeySize + 4;
            return width;
        }
    }
}
