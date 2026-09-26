using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewLogistics.Menus
{
    /// <summary>Draws text that scrolls sideways when it's too long for its space, like a news ticker.</summary>
    /// <remarks>
    /// Text that fits is drawn as-is. Text that doesn't rests at its start, then scrolls left continuously and
    /// comes round again after a gap -- always reading left to right, never reversing -- so every word is
    /// readable in turn, rather than being cut short with "...".
    ///
    /// Smooth movement needs the text clipped to its space, which a <see cref="SpriteBatch"/> can only do by
    /// being restarted with a scissor rectangle. The batch is restarted with the settings the game draws menus
    /// with, so nothing else on screen is affected; text that fits never restarts it at all.
    /// </remarks>
    internal static class Marquee
    {
        /*********
        ** Fields
        *********/
        /// <summary>How fast the text moves.</summary>
        private const float PixelsPerSecond = 45f;

        /// <summary>How long the text rests at its start before scrolling.</summary>
        private const double PauseMilliseconds = 1500;

        /// <summary>The gap between the end of the text and its start coming round again.</summary>
        private const float Gap = 60f;

        /// <summary>Extra room above and below the text so its shadow and descenders aren't clipped.</summary>
        private const int VerticalSlack = 6;

        /// <summary>A rasterizer state with clipping on. Created once; building one per frame would churn the GPU state cache.</summary>
        private static readonly RasterizerState Clipping = new() { ScissorTestEnable = true };


        /*********
        ** Public methods
        *********/
        /// <summary>Draws text within a width, scrolling it if it doesn't fit.</summary>
        /// <param name="b">The sprite batch, begun with the game's usual menu settings.</param>
        /// <param name="text">The text to draw.</param>
        /// <param name="font">The font to draw it in.</param>
        /// <param name="position">The top-left corner of the text's space.</param>
        /// <param name="maxWidth">The width available.</param>
        /// <param name="colour">The text colour.</param>
        public static void Draw(SpriteBatch b, string text, SpriteFont font, Vector2 position, int maxWidth, Color colour)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0)
                return;

            Vector2 size = font.MeasureString(text);
            if (size.X <= maxWidth)
            {
                Utility.drawTextWithShadow(b, text, font, position, colour);
                return;
            }

            float loop = size.X + Gap;
            float offset = GetOffset(loop);

            Rectangle clip = new((int)position.X, (int)position.Y - VerticalSlack, maxWidth, (int)size.Y + (VerticalSlack * 2));
            clip = Rectangle.Intersect(clip, b.GraphicsDevice.Viewport.Bounds);
            if (clip.Width <= 0 || clip.Height <= 0)
                return;

            Rectangle previous = b.GraphicsDevice.ScissorRectangle;

            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, Clipping);
            b.GraphicsDevice.ScissorRectangle = clip;
            try
            {
                Utility.drawTextWithShadow(b, text, font, new Vector2(position.X - offset, position.Y), colour);
                if (offset > loop - maxWidth)
                    Utility.drawTextWithShadow(b, text, font, new Vector2(position.X - offset + loop, position.Y), colour);
            }
            finally
            {
                b.End();
                b.GraphicsDevice.ScissorRectangle = previous;
                b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            }
        }


        /*********
        ** Private methods
        *********/
        /// <summary>How far the text is scrolled right now: resting at the start, then moving steadily through one loop.</summary>
        /// <remarks>
        /// Driven by real time rather than a per-menu counter, so it keeps moving while the game is paused and
        /// needs no state per piece of text.
        /// </remarks>
        private static float GetOffset(float loop)
        {
            double travel = loop / PixelsPerSecond * 1000;
            double now = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
            double t = now % (PauseMilliseconds + travel);

            return t < PauseMilliseconds ? 0 : (float)((t - PauseMilliseconds) / travel * loop);
        }

        /// <summary>Draws text wrapped onto as many lines as it needs, up to a limit; only a last line that still doesn't fit scrolls.</summary>
        /// <returns>The height drawn.</returns>
        public static int DrawWrapped(SpriteBatch b, string text, SpriteFont font, Vector2 position, int maxWidth, Color colour, int maxLines = 2)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0)
                return 0;

            int lineHeight = (int)font.MeasureString("Ay").Y;
            List<string> lines = Wrap(text, font, maxWidth);
            int shown = Math.Min(lines.Count, Math.Max(1, maxLines));

            for (int i = 0; i < shown; i++)
            {
                // Only text that still doesn't fit in the lines allowed runs on, and scrolls, along the last one.
                string line = i == shown - 1 ? string.Join(" ", lines.Skip(i)) : lines[i];
                Draw(b, line, font, new Vector2(position.X, position.Y + (i * lineHeight)), maxWidth, colour);
            }

            return shown * lineHeight;
        }

        /// <summary>Breaks text into lines no wider than a width, measured in the font it's drawn in.</summary>
        /// <remarks>
        /// Done here rather than with the game's own wrapping, whose lines can come out slightly wider than asked
        /// for -- and a line even a pixel too wide would scroll.
        /// </remarks>
        private static List<string> Wrap(string text, SpriteFont font, int maxWidth)
        {
            List<string> lines = new();
            string current = "";
            foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && font.MeasureString(candidate).X > maxWidth)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                    current = candidate;
            }

            if (current.Length > 0)
                lines.Add(current);
            return lines;
        }
    }
}
