using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewLogistics.Menus
{
    /// <summary>Timing for the mod's menu animations, scaled by the player's animation speed.</summary>
    /// <remarks>
    /// Every animation is written for normal speed and asks here how far along it is. At 200% each takes half as
    /// long; at 0% animations are off, and everything is shown in its final state straight away.
    /// </remarks>
    internal static class UiAnimation
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The animation speed, as a percentage of normal; zero turns animations off.</summary>
        public static int SpeedPercent { get; set; } = 100;

        /// <summary>Whether animations play at all.</summary>
        public static bool Enabled => SpeedPercent > 0;

        /// <summary>How much faster than normal animations run.</summary>
        public static float SpeedFactor => SpeedPercent / 100f;


        /*********
        ** Public methods
        *********/
        /// <summary>How far through an animation is: 0 when it starts, 1 once it's done.</summary>
        /// <param name="start">When it started.</param>
        /// <param name="normalMilliseconds">How long it takes at normal speed.</param>
        public static float Progress(DateTime start, double normalMilliseconds)
        {
            if (!Enabled || normalMilliseconds <= 0)
                return 1f;

            double elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
            return (float)Math.Clamp(elapsed * SpeedFactor / normalMilliseconds, 0, 1);
        }

        /// <summary>Eases progress to slow into its end: quick at first, settling gently.</summary>
        public static float EaseOut(float t) => 1f - ((1f - t) * (1f - t) * (1f - t));

        /// <summary>Eases progress to overshoot its end a little and settle back, for something popping into place.</summary>
        public static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + (c3 * u * u * u) + (c1 * u * u);
        }

        /// <summary>How long a window takes to grow into place as it opens, at normal speed.</summary>
        public const double OpenMilliseconds = 260;

        /// <summary>Starts drawing a window growing into place, if it's still opening.</summary>
        /// <param name="b">The sprite batch.</param>
        /// <param name="openedAt">When the window opened.</param>
        /// <param name="window">The window's bounds.</param>
        /// <returns>Whether the batch was changed, in which case the caller must <see cref="UiBatch.Pop"/> once the window is drawn.</returns>
        public static bool PushOpening(SpriteBatch b, DateTime openedAt, Rectangle window)
        {
            float t = EaseOut(Progress(openedAt, OpenMilliseconds));
            if (t >= 1f)
                return false;

            Vector2 centre = new(window.Center.X, window.Center.Y);
            return UiBatch.Push(b, null, UiBatch.ScaleAbout(centre, 0.92f + (0.08f * t), new Vector2(0, (1f - t) * 24)));
        }

        /// <summary>A box scaled about its centre.</summary>
        public static Rectangle Scale(Rectangle box, float scale)
        {
            int width = (int)(box.Width * scale);
            int height = (int)(box.Height * scale);
            return new Rectangle(box.Center.X - (width / 2), box.Center.Y - (height / 2), width, height);
        }
    }

    /// <summary>Grows grid icons a little while the cursor is over them, the way the inventory's do.</summary>
    /// <remarks>
    /// The game grows a hovered inventory item to 110% over a few frames, and lets it shrink back more slowly
    /// once the cursor leaves. Slots are keyed by whatever identifies them to the caller -- a tab and an index.
    /// </remarks>
    internal class HoverScales
    {
        /*********
        ** Fields
        *********/
        /// <summary>How big a hovered icon grows.</summary>
        public const float MaxScale = 1.1f;

        /// <summary>How fast an icon grows, in scale per second at normal speed: the game's 0.05 a frame.</summary>
        private const float GrowPerSecond = 3f;

        /// <summary>How fast it shrinks back: the game's 0.025 a frame.</summary>
        private const float ShrinkPerSecond = 1.5f;

        /// <summary>Each slot's current scale, for those not at rest.</summary>
        private readonly Dictionary<int, float> Scales = new();

        /// <summary>The slot under the cursor, if any.</summary>
        private int? Hovered;


        /*********
        ** Public methods
        *********/
        /// <summary>Sets which slot the cursor is over, or <c>null</c> for none.</summary>
        public void Hover(int? key) => this.Hovered = key;

        /// <summary>Moves every slot's scale towards where it's headed.</summary>
        public void Update(GameTime time)
        {
            if (!UiAnimation.Enabled)
            {
                this.Scales.Clear();
                if (this.Hovered is int hovered)
                    this.Scales[hovered] = MaxScale;
                return;
            }

            float seconds = (float)time.ElapsedGameTime.TotalSeconds * UiAnimation.SpeedFactor;

            if (this.Hovered is int key)
                this.Scales[key] = Math.Min(MaxScale, this.Get(key) + (GrowPerSecond * seconds));

            foreach (int other in this.Scales.Keys.Where(other => other != this.Hovered).ToList())
            {
                float scale = this.Scales[other] - (ShrinkPerSecond * seconds);
                if (scale <= 1f)
                    this.Scales.Remove(other);
                else
                    this.Scales[other] = scale;
            }
        }

        /// <summary>A slot's scale: 1 at rest.</summary>
        public float Get(int key) => this.Scales.TryGetValue(key, out float scale) ? scale : 1f;

        /// <summary>A box grown about its centre by a slot's scale, for icons drawn to a box.</summary>
        public Rectangle Grow(int key, Rectangle box)
        {
            float scale = this.Get(key);
            if (scale <= 1f)
                return box;

            int width = (int)(box.Width * scale);
            int height = (int)(box.Height * scale);
            return new Rectangle(box.Center.X - (width / 2), box.Center.Y - (height / 2), width, height);
        }
    }

    /// <summary>Restarts a menu's sprite batch with a transform and a clip, and back.</summary>
    /// <remarks>
    /// A batch can only move, scale or clip what it draws when it's begun, so each means ending it and beginning
    /// another. Done in one place, the settings stack: text scrolling inside a tab that's sliding in stays clipped
    /// to its own space and moves with the tab. Batches are begun with the settings the game draws menus with.
    /// </remarks>
    internal static class UiBatch
    {
        /*********
        ** Fields
        *********/
        /// <summary>A rasterizer state with clipping on. Created once; building one per frame would churn the GPU state cache.</summary>
        private static readonly RasterizerState Clipping = new() { ScissorTestEnable = true };

        /// <summary>The settings in force before each push.</summary>
        private static readonly Stack<(Matrix Transform, Rectangle? Clip, Rectangle Scissor)> Saved = new();

        /// <summary>How everything drawn is moved and scaled onto the screen.</summary>
        public static Matrix Transform { get; private set; } = Matrix.Identity;

        /// <summary>The screen area drawing is clipped to, if any.</summary>
        public static Rectangle? Clip { get; private set; }


        /*********
        ** Public methods
        *********/
        /// <summary>Moves and clips what's drawn from here until the matching <see cref="Pop"/>.</summary>
        /// <param name="b">The sprite batch, begun.</param>
        /// <param name="clip">The area to clip to, where the caller would draw it, or <c>null</c> to keep the current clip.</param>
        /// <param name="offset">How much further to move what's drawn.</param>
        /// <returns>Whether anything can be drawn: false if the clip leaves nothing, in which case the batch is left as it was and there's nothing to pop.</returns>
        public static bool Push(SpriteBatch b, Rectangle? clip, Vector2 offset)
        {
            return Push(b, clip, Matrix.CreateTranslation(offset.X, offset.Y, 0));
        }

        /// <summary>Transforms and clips what's drawn from here until the matching <see cref="Pop"/>.</summary>
        /// <param name="b">The sprite batch, begun.</param>
        /// <param name="clip">The area to clip to, where the caller would draw it, or <c>null</c> to keep the current clip.</param>
        /// <param name="transform">How to move or scale what's drawn, on top of any transform already in force.</param>
        /// <returns>Whether anything can be drawn: false if the clip leaves nothing, in which case the batch is left as it was and there's nothing to pop.</returns>
        public static bool Push(SpriteBatch b, Rectangle? clip, Matrix transform)
        {
            Rectangle? screenClip = Clip;
            if (clip is Rectangle area)
            {
                Rectangle onScreen = ToScreen(area, Transform);
                onScreen = Rectangle.Intersect(onScreen, screenClip ?? b.GraphicsDevice.Viewport.Bounds);
                onScreen = Rectangle.Intersect(onScreen, b.GraphicsDevice.Viewport.Bounds);
                if (onScreen.Width <= 0 || onScreen.Height <= 0)
                    return false;
                screenClip = onScreen;
            }

            Saved.Push((Transform, Clip, b.GraphicsDevice.ScissorRectangle));
            b.End();
            Transform = transform * Transform;
            Clip = screenClip;
            Begin(b);
            return true;
        }

        /// <summary>Goes back to the settings before the last <see cref="Push"/>.</summary>
        public static void Pop(SpriteBatch b)
        {
            if (Saved.Count == 0)
                return;

            (Matrix transform, Rectangle? clip, Rectangle scissor) = Saved.Pop();
            b.End();
            Transform = transform;
            Clip = clip;
            b.GraphicsDevice.ScissorRectangle = scissor;
            Begin(b);
        }

        /// <summary>A transform that scales about a point and then moves, for a window growing into place.</summary>
        public static Matrix ScaleAbout(Vector2 centre, float scale, Vector2 offset)
        {
            return ScaleAbout(centre, new Vector2(scale, scale), offset);
        }

        /// <summary>A transform that scales about a point, differently across and down, and then moves.</summary>
        public static Matrix ScaleAbout(Vector2 centre, Vector2 scale, Vector2 offset)
        {
            return Matrix.CreateTranslation(-centre.X, -centre.Y, 0)
                * Matrix.CreateScale(scale.X, scale.Y, 1)
                * Matrix.CreateTranslation(centre.X + offset.X, centre.Y + offset.Y, 0);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Begins the batch with the current settings.</summary>
        private static void Begin(SpriteBatch b)
        {
            Matrix? transform = Transform == Matrix.Identity ? null : Transform;

            if (Clip is Rectangle clip)
            {
                b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, Clipping, null, transform);
                b.GraphicsDevice.ScissorRectangle = clip;
            }
            else
                b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, transform);
        }

        /// <summary>Where an area ends up on screen under a transform.</summary>
        private static Rectangle ToScreen(Rectangle area, Matrix transform)
        {
            Vector2 a = Vector2.Transform(new Vector2(area.Left, area.Top), transform);
            Vector2 c = Vector2.Transform(new Vector2(area.Right, area.Bottom), transform);
            int left = (int)Math.Floor(Math.Min(a.X, c.X));
            int top = (int)Math.Floor(Math.Min(a.Y, c.Y));
            int right = (int)Math.Ceiling(Math.Max(a.X, c.X));
            int bottom = (int)Math.Ceiling(Math.Max(a.Y, c.Y));
            return new Rectangle(left, top, right - left, bottom - top);
        }
    }

    /// <summary>A number shown counting smoothly to each new value, like a total changing.</summary>
    internal class AnimatedValue
    {
        /*********
        ** Fields
        *********/
        /// <summary>How long a change takes at normal speed.</summary>
        private readonly double Milliseconds;

        /// <summary>Where the current change started from, and is heading to.</summary>
        private double From;
        private double To;

        /// <summary>When the current change started.</summary>
        private DateTime Start = DateTime.MinValue;

        /// <summary>Whether a value has been set yet.</summary>
        private bool HasValue;


        /*********
        ** Public methods
        *********/
        public AnimatedValue(double milliseconds = 350)
        {
            this.Milliseconds = milliseconds;
        }

        /// <summary>The value to show now.</summary>
        public double Current => this.From + ((this.To - this.From) * UiAnimation.EaseOut(UiAnimation.Progress(this.Start, this.Milliseconds)));

        /// <summary>Sets the value to count to, from wherever it's shown now. The first value counts up from zero.</summary>
        public void Set(double value)
        {
            if (this.HasValue && value == this.To)
                return;

            this.From = this.HasValue ? this.Current : 0;
            this.To = value;
            this.Start = DateTime.UtcNow;
            this.HasValue = true;
        }
    }
}
