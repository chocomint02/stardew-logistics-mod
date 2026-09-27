using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewLogistics.Menus
{
    /// <summary>Shows a menu's tooltip growing out from the cursor as it appears, and shrinking back as it goes.</summary>
    /// <remarks>
    /// A menu hands over, each frame, how to draw the tooltip for whatever's hovered -- holding what it shows, not
    /// looking it up -- or nothing. That lets a tooltip still be drawn for a moment after the cursor has left
    /// it, shrinking into the spot where it was. Moving straight from one thing to the next just changes what the
    /// tooltip shows, without popping, as does a gap too short to notice. Tooltips place themselves by the cursor,
    /// so one going away is moved back to where the cursor was when it went.
    /// </remarks>
    internal class TooltipPresenter
    {
        /*********
        ** Fields
        *********/
        /// <summary>How long a tooltip takes to grow in, and to shrink away, at normal speed.</summary>
        private const double GrowMs = 170;
        private const double ShrinkMs = 110;

        /// <summary>A gap between tooltips shorter than this doesn't make the next one grow in again.</summary>
        private const double GapMs = 90;

        /// <summary>How small a tooltip starts growing from, and shrinks to.</summary>
        private const float SmallestScale = 0.7f;

        /// <summary>The tooltip showing, when it appeared, and where the cursor was last.</summary>
        private Action<SpriteBatch> Showing;
        private DateTime ShownAt = DateTime.MinValue;
        private Point ShowingAt;

        /// <summary>The tooltip going away, when it went, and where the cursor was then.</summary>
        private Action<SpriteBatch> Leaving;
        private DateTime LeftAt = DateTime.MinValue;
        private Point LeftFrom;


        /*********
        ** Public methods
        *********/
        /// <summary>Draws this frame's tooltip, or the last one going away.</summary>
        /// <param name="b">The sprite batch.</param>
        /// <param name="draw">How to draw the tooltip for what's hovered now, or <c>null</c> if nothing is.</param>
        public void Draw(SpriteBatch b, Action<SpriteBatch> draw)
        {
            DateTime now = DateTime.UtcNow;
            Point mouse = new(Game1.getOldMouseX(), Game1.getOldMouseY());

            if (draw != null)
            {
                if (this.Showing == null)
                {
                    // Back a moment after one went: carry on as if it never left.
                    bool returning = this.Leaving != null && (now - this.LeftAt).TotalMilliseconds < GapMs;
                    this.ShownAt = returning ? DateTime.MinValue : now;
                    this.Leaving = null;
                }

                this.Showing = draw;
                this.ShowingAt = mouse;
                float t = UiAnimation.Progress(this.ShownAt, GrowMs);
                DrawScaled(b, draw, mouse, SmallestScale + ((1f - SmallestScale) * UiAnimation.EaseOutBack(t)), Vector2.Zero);
                return;
            }

            if (this.Showing != null)
            {
                this.Leaving = UiAnimation.Enabled ? this.Showing : null;
                this.LeftAt = now;
                this.LeftFrom = this.ShowingAt;
                this.Showing = null;
            }

            if (this.Leaving != null)
            {
                float t = UiAnimation.Progress(this.LeftAt, ShrinkMs);
                if (t >= 1f)
                {
                    this.Leaving = null;
                    return;
                }

                DrawScaled(b, this.Leaving, mouse, 1f - ((1f - SmallestScale) * t * t), (this.LeftFrom - mouse).ToVector2());
            }
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Draws a tooltip moved and scaled about the cursor.</summary>
        /// <param name="b">The sprite batch.</param>
        /// <param name="draw">Draws the tooltip where it places itself, by the cursor.</param>
        /// <param name="mouse">Where the cursor is now.</param>
        /// <param name="scale">How big to draw it.</param>
        /// <param name="offset">How far to move it, after scaling about the cursor.</param>
        private static void DrawScaled(SpriteBatch b, Action<SpriteBatch> draw, Point mouse, float scale, Vector2 offset)
        {
            bool transformed = (Math.Abs(scale - 1f) > 0.001f || offset != Vector2.Zero)
                && UiBatch.Push(b, null, UiBatch.ScaleAbout(mouse.ToVector2(), scale, offset));
            try
            {
                draw(b);
            }
            finally
            {
                if (transformed)
                    UiBatch.Pop(b);
            }
        }
    }
}
