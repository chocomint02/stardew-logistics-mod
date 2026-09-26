using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewLogistics.Menus
{
    /// <summary>Draws text that scrolls sideways when it's too long for its space, like a music player's title.</summary>
    /// <remarks>
    /// Text that fits is drawn as-is. Text that doesn't pauses at its start, glides left until its end is showing,
    /// pauses again, and glides back -- so every word is readable in turn, rather than being cut short with "...".
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

        /// <summary>How long the text rests at each end before moving again.</summary>
        private const double PauseMilliseconds = 1500;

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

            float offset = GetOffset(size.X - maxWidth);

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
        /// <summary>How far the text is scrolled right now, for a given overflow.</summary>
        /// <remarks>
        /// Driven by real time rather than a per-menu counter, so it keeps moving while the game is paused and
        /// needs no state per piece of text.
        /// </remarks>
        private static float GetOffset(float overflow)
        {
            double travel = overflow / PixelsPerSecond * 1000;
            double cycle = (PauseMilliseconds + travel) * 2;
            double now = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
            double t = now % cycle;

            // Rest at the start, glide to the end, rest there, glide back.
            if (t < PauseMilliseconds)
                return 0;
            t -= PauseMilliseconds;

            if (t < travel)
                return (float)(overflow * Ease(t / travel));
            t -= travel;

            if (t < PauseMilliseconds)
                return overflow;
            t -= PauseMilliseconds;

            return (float)(overflow * (1 - Ease(t / travel)));
        }

        /// <summary>Softens the start and end of each glide, so the text doesn't lurch into motion.</summary>
        private static double Ease(double x)
        {
            x = Math.Clamp(x, 0, 1);
            return x * x * (3 - (2 * x));
        }
    }
}
