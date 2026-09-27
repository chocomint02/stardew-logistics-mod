using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace StardewLogistics.Menus
{
    /// <summary>A list whose rows animate: new ones slide in, removed ones fade away, and the rest glide into place.</summary>
    /// <typeparam name="T">What each row shows.</typeparam>
    /// <remarks>
    /// The list is given what it holds each frame, each row with a key that stays the same while the row does, and
    /// works out what's come and gone since. What's there when it's first shown is just there.
    /// </remarks>
    internal class AnimatedRows<T>
    {
        /*********
        ** Fields
        *********/
        /// <summary>How long rows take to slide in, fade out, and glide to a new place, at normal speed.</summary>
        private const double EnterMs = 240;
        private const double LeaveMs = 200;
        private const double MoveMs = 240;

        /// <summary>How far a row slides as it comes and goes.</summary>
        private const float SlideDistance = 18;

        /// <summary>The rows shown, and any still fading away, by key.</summary>
        private readonly Dictionary<string, Row> Rows = new();

        /// <summary>Whether the list has been shown yet: until then, rows don't animate in.</summary>
        private bool Seeded;

        /// <summary>When the list last became empty, for what's shown in its place to fade in.</summary>
        private DateTime? EmptySince;


        /*********
        ** Nested types
        *********/
        private class Row
        {
            public T Data;
            public int Order;
            public float FromY;
            public float TargetY;
            public DateTime MovedAt = DateTime.MinValue;
            public DateTime AddedAt = DateTime.MinValue;
            public DateTime? LeftAt;

            /// <summary>Where the row is now, gliding from where it was to where it's going.</summary>
            public float Y => MathHelper.Lerp(this.FromY, this.TargetY, UiAnimation.EaseOut(UiAnimation.Progress(this.MovedAt, MoveMs)));
        }


        /*********
        ** Public methods
        *********/
        /// <summary>Lays out this frame's rows: where each is and how far in or out it's faded. Rows fading away come first, to be drawn beneath.</summary>
        /// <param name="current">What the list holds, in order, each with a key that identifies it.</param>
        /// <param name="rowHeight">How far apart rows are.</param>
        public List<(T Data, float Y, float Alpha, float Slide, bool Leaving)> Layout(IList<(string Key, T Data)> current, int rowHeight)
        {
            DateTime now = DateTime.UtcNow;
            HashSet<string> present = new();

            for (int i = 0; i < current.Count; i++)
            {
                (string key, T data) = current[i];
                if (!present.Add(key))
                    continue;

                float target = i * rowHeight;
                if (!this.Rows.TryGetValue(key, out Row row))
                {
                    this.Rows[key] = new Row { Data = data, FromY = target, TargetY = target, AddedAt = this.Seeded ? now : DateTime.MinValue };
                    row = this.Rows[key];
                }
                else
                {
                    if (row.LeftAt != null)
                    {
                        // Back before it had gone: it comes back in.
                        row.LeftAt = null;
                        row.AddedAt = now;
                    }
                    if (Math.Abs(row.TargetY - target) > 0.5f)
                    {
                        row.FromY = row.Y;
                        row.TargetY = target;
                        row.MovedAt = now;
                    }
                }
                row.Data = data;
                row.Order = i;
            }

            foreach ((string key, Row row) in this.Rows.ToList())
            {
                if (present.Contains(key))
                    continue;

                if (row.LeftAt == null)
                    row.LeftAt = this.Seeded ? now : DateTime.MinValue;
                if (UiAnimation.Progress(row.LeftAt.Value, LeaveMs) >= 1f)
                    this.Rows.Remove(key);
            }

            if (this.Rows.Count == 0)
                this.EmptySince ??= this.Seeded ? now : DateTime.MinValue;
            else
                this.EmptySince = null;
            this.Seeded = true;

            // Going first, beneath; then the rest in order.
            List<(T, float, float, float, bool)> laid = new();
            foreach (Row row in this.Rows.Values.Where(row => row.LeftAt != null).OrderBy(row => row.Order).ToList())
            {
                float t = UiAnimation.EaseOut(UiAnimation.Progress(row.LeftAt.Value, LeaveMs));
                laid.Add((row.Data, row.Y, 1f - t, -SlideDistance * t, true));
            }
            foreach (Row row in this.Rows.Values.Where(row => row.LeftAt == null).OrderBy(row => row.Order).ToList())
            {
                float t = UiAnimation.EaseOut(UiAnimation.Progress(row.AddedAt, EnterMs));
                laid.Add((row.Data, row.Y, t, SlideDistance * (1f - t), false));
            }
            return laid;
        }

        /// <summary>How far in to fade what's shown when the list is empty: nothing while rows are still fading away.</summary>
        public float EmptyAlpha => this.EmptySince is DateTime since ? UiAnimation.EaseOut(UiAnimation.Progress(since, EnterMs)) : 0f;
    }
}
