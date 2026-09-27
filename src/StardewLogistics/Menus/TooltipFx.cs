using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Framework;
using StardewValley;
using StardewValley.Menus;

namespace StardewLogistics.Menus
{
    /// <summary>Animates the frame of the tooltip under the cursor: resizing smoothly when what it shows changes, and a quiet streak of light travelling round its border.</summary>
    /// <remarks>
    /// Every tooltip is drawn inside <see cref="Around"/>, the game's own and the mod's alike. While it runs, the
    /// first box drawn is the tooltip's frame, and it's intercepted: its natural size is noted, and it's drawn at
    /// the size it's growing or shrinking to instead, with what's inside clipped to it. So moving from one item to
    /// another eases the frame from the old size to the new, revealing the new contents as it opens out, without
    /// knowing anything about what any tooltip contains. The streak starts again from the top-left corner and
    /// fades in when a tooltip appears, and carries on round a frame that's only resizing. Everything follows the
    /// animation speed; at zero, frames are drawn as they are and nothing travels round them.
    /// </remarks>
    internal static class TooltipFx
    {
        /*********
        ** Fields
        *********/
        /// <summary>How long the streak takes to go once round, at normal speed.</summary>
        private const double LapMs = 4200;

        /// <summary>How long the streak takes to fade in when a tooltip appears.</summary>
        private const double FadeInMs = 300;

        /// <summary>How long a frame takes to resize to new contents, at normal speed.</summary>
        private const double ResizeMs = 160;

        /// <summary>A tooltip drawn within this long of the last is the same one carrying on, not a new one.</summary>
        private const double ContinuityMs = 150;

        /// <summary>How far inside the box's edge the streak runs: along the frame, clear of the contents.</summary>
        private const int Inset = 8;

        /// <summary>How far a frame's shadow reaches left and down, kept inside the clip.</summary>
        private const int ShadowReach = 8;

        /// <summary>Whether a wrapped call is running, the frame it drew, and whether its contents are clipped.</summary>
        private static bool Capturing;
        private static Rectangle? Shown;
        private static bool Clipped;

        /// <summary>When the tooltip on screen appeared, and when a frame was last drawn.</summary>
        private static DateTime AppearedAt = DateTime.MinValue;
        private static DateTime LastFrameAt = DateTime.MinValue;

        /// <summary>The size the last frame's contents wanted, the size it was drawn, and the resize under way.</summary>
        private static Point LastNatural;
        private static Point LastShown;
        private static Point ResizeFrom;
        private static DateTime ResizedAt = DateTime.MinValue;


        /*********
        ** Public methods
        *********/
        /// <summary>Watches the game's box drawing, for <see cref="Around"/> to find and resize a tooltip's frame.</summary>
        public static void Apply(Harmony harmony)
        {
            // The overload taking everything, which the others and the game's tooltips go through.
            MethodInfo method = typeof(IClickableMenu)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(candidate => candidate.Name == nameof(IClickableMenu.drawTextureBox))
                .Where(candidate => candidate.GetParameters().Any(parameter => parameter.ParameterType == typeof(Texture2D)))
                .OrderByDescending(candidate => candidate.GetParameters().Length)
                .FirstOrDefault();
            if (method == null)
                return;

            try
            {
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(TooltipFx), nameof(Before_DrawTextureBox)));
            }
            catch (Exception ex)
            {
                // Tooltips just draw as they are.
                Log.Trace($"Couldn't watch tooltip frames: {ex.Message}");
            }
        }

        /// <summary>Draws a tooltip, animating its frame.</summary>
        /// <param name="b">The sprite batch.</param>
        /// <param name="draw">Draws the tooltip, frame first, where it places itself.</param>
        public static void Around(SpriteBatch b, Action draw)
        {
            Capturing = true;
            Shown = null;
            Clipped = false;
            try
            {
                draw();
            }
            finally
            {
                Capturing = false;
                if (Clipped)
                    UiBatch.Pop(b);
                Clipped = false;
            }

            if (Shown is Rectangle box)
                DrawIdle(b, box);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Draws the streak on a tooltip's frame.</summary>
        private static void DrawIdle(SpriteBatch b, Rectangle box)
        {
            DateTime now = DateTime.UtcNow;
            if (!UiAnimation.Enabled)
                return;

            Rectangle path = new(box.X + Inset, box.Y + Inset, box.Width - (Inset * 2), box.Height - (Inset * 2));
            if (path.Width <= 8 || path.Height <= 8)
                return;

            float shown = UiAnimation.EaseOut(UiAnimation.Progress(AppearedAt, FadeInMs));
            double elapsed = (now - AppearedAt).TotalMilliseconds * UiAnimation.SpeedFactor;
            float perimeter = 2f * (path.Width + path.Height);

            // A faint breath on the whole frame, slow enough to barely notice.
            float breath = 0.5f - (0.5f * (float)Math.Cos(elapsed / LapMs * Math.PI * 2));
            DrawRing(b, path, Color.White * (0.06f * breath * shown));

            // The streak: brightest at its head, fading along its tail, going round clockwise from the top-left.
            float head = (float)(elapsed / LapMs % 1.0) * perimeter;
            float tail = Math.Min(140f, perimeter / 4f);
            const float step = 3f;
            for (float along = 0; along <= tail; along += step)
            {
                float fade = 1f - (along / tail);
                Vector2 point = PointAt(path, head - along, perimeter);
                b.Draw(Game1.staminaRect, new Rectangle((int)point.X - 1, (int)point.Y - 1, 3, 3), Color.White * (0.5f * fade * fade * shown));
            }
        }

        /// <summary>Intercepts the first box a wrapped call draws -- the tooltip's frame -- drawing it at the size it's resizing through.</summary>
        private static void Before_DrawTextureBox(SpriteBatch b, ref int x, ref int y, ref int width, ref int height)
        {
            if (!Capturing || Shown != null)
                return;

            Rectangle natural = new(x, y, width, height);
            Rectangle shown = GetShownFrame(natural);
            Shown = shown;
            if (shown == natural)
                return;

            x = shown.X;
            y = shown.Y;
            width = shown.Width;
            height = shown.Height;

            // What's inside is laid out for the full size: clip it to the frame as it opens out or closes in.
            Clipped = UiBatch.Push(b, new Rectangle(shown.X - ShadowReach, shown.Y, shown.Width + ShadowReach, shown.Height + ShadowReach), Vector2.Zero);
        }

        /// <summary>Where to draw a tooltip's frame this frame: eased from the size it was to the size its contents want.</summary>
        private static Rectangle GetShownFrame(Rectangle natural)
        {
            DateTime now = DateTime.UtcNow;
            Point size = new(natural.Width, natural.Height);

            // A tooltip not drawn a moment ago is a new one: it's just its own size. The same one with new
            // contents eases from wherever it had got to.
            if ((now - LastFrameAt).TotalMilliseconds > ContinuityMs)
            {
                AppearedAt = now;
                ResizedAt = DateTime.MinValue;
                LastShown = size;
            }
            else if (size != LastNatural && UiAnimation.Enabled)
            {
                ResizeFrom = LastShown;
                ResizedAt = now;
            }
            LastFrameAt = now;
            LastNatural = size;

            float t = UiAnimation.EaseOut(UiAnimation.Progress(ResizedAt, ResizeMs));
            if (t >= 1f)
            {
                LastShown = size;
                return natural;
            }

            int width = (int)MathHelper.Lerp(ResizeFrom.X, size.X, t);
            int height = (int)MathHelper.Lerp(ResizeFrom.Y, size.Y, t);
            LastShown = new Point(width, height);

            // Anchored at the corner nearest the cursor, which the tooltip hangs from: its right edge if it's been
            // flipped to the left of the cursor, its foot if it's been lifted above it.
            int mouseX = Game1.getOldMouseX();
            int mouseY = Game1.getOldMouseY();
            int left = natural.Right <= mouseX ? natural.Right - width : natural.X;
            int top = natural.Bottom <= mouseY ? natural.Bottom - height : natural.Y;
            return new Rectangle(left, top, width, height);
        }

        /// <summary>The point a distance round a rectangle's edge, clockwise from its top-left corner.</summary>
        private static Vector2 PointAt(Rectangle path, float distance, float perimeter)
        {
            distance %= perimeter;
            if (distance < 0)
                distance += perimeter;

            if (distance < path.Width)
                return new Vector2(path.X + distance, path.Y);
            distance -= path.Width;
            if (distance < path.Height)
                return new Vector2(path.Right, path.Y + distance);
            distance -= path.Height;
            if (distance < path.Width)
                return new Vector2(path.Right - distance, path.Bottom);
            distance -= path.Width;
            return new Vector2(path.X, path.Bottom - distance);
        }

        /// <summary>A one-pixel outline.</summary>
        private static void DrawRing(SpriteBatch b, Rectangle area, Color colour)
        {
            if (colour.A == 0)
                return;
            b.Draw(Game1.staminaRect, new Rectangle(area.X, area.Y, area.Width, 1), colour);
            b.Draw(Game1.staminaRect, new Rectangle(area.X, area.Bottom, area.Width, 1), colour);
            b.Draw(Game1.staminaRect, new Rectangle(area.X, area.Y, 1, area.Height), colour);
            b.Draw(Game1.staminaRect, new Rectangle(area.Right, area.Y, 1, area.Height + 1), colour);
        }
    }
}
