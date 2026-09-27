using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace StardewLogistics.Menus
{
    /// <summary>A scrollable list that opens over the menu, used by the terminal's type and mod filters.</summary>
    /// <remarks>
    /// Stardew's <c>OptionsDropDown</c> is built for the options menu and expects to own its own layout, so this is a
    /// smaller purpose-built replacement: it draws above everything else, closes on the next click wherever that
    /// lands, and reports the chosen value back to the caller rather than mutating shared state.
    /// </remarks>
    internal class DropdownPopup
    {
        /*********
        ** Fields
        *********/
        private const int RowHeight = 40;
        private const int MaxVisibleRows = 10;

        /// <summary>How long the list takes to unfurl, and to fold away, at normal speed.</summary>
        private const double OpenMs = 170;
        private const double CloseMs = 110;

        private readonly List<Option> Options = new();
        private Rectangle Bounds;
        private int Scroll;
        private int HoverIndex = -1;

        /// <summary>Whether the list hangs above its button, having no room below; it then unfurls upwards.</summary>
        private bool OpensUp;

        /// <summary>When the list opened, and when it started closing, for its animations.</summary>
        private DateTime OpenedAt;
        private DateTime? ClosedAt;

        /// <summary>The highlight behind the row under the cursor, fading in and out.</summary>
        private readonly HoverScales RowHover = new();


        /*********
        ** Accessors
        *********/
        /// <summary>Whether the list is currently showing.</summary>
        public bool IsOpen { get; private set; }


        /*********
        ** Public methods
        *********/
        /// <summary>Opens the list below an anchor rectangle.</summary>
        /// <param name="options">The choices, in display order. A null value means "no restriction".</param>
        /// <param name="anchor">The button the list hangs from.</param>
        public void Open(IEnumerable<(string Label, object Value)> options, Rectangle anchor)
        {
            this.Open(options.Select(option => (option.Label, option.Value, (Item)null)), anchor);
        }

        /// <summary>Opens the list below an anchor rectangle, with an icon beside each choice.</summary>
        /// <param name="options">The choices, in display order, each with an item to draw beside it.</param>
        /// <param name="anchor">The button the list hangs from.</param>
        public void Open(IEnumerable<(string Label, object Value, Item Icon)> options, Rectangle anchor)
        {
            this.Options.Clear();
            foreach ((string label, object value, Item icon) in options)
                this.Options.Add(new Option(label, value, icon));

            this.Scroll = 0;
            this.HoverIndex = -1;
            this.IsOpen = true;
            this.OpenedAt = DateTime.UtcNow;
            this.ClosedAt = null;
            this.OpensUp = false;

            int visible = Math.Min(this.Options.Count, MaxVisibleRows);
            int width = Math.Max(anchor.Width, this.MeasureWidth());
            int height = (visible * RowHeight) + 16;
            int x = anchor.X;
            int y = anchor.Bottom + 4;

            // Flip above the anchor if the list would run off the bottom of the screen.
            if (y + height > Game1.uiViewport.Height)
            {
                y = Math.Max(0, anchor.Y - height - 4);
                this.OpensUp = true;
            }
            if (x + width > Game1.uiViewport.Width)
                x = Math.Max(0, Game1.uiViewport.Width - width);

            this.Bounds = new Rectangle(x, y, width, height);
        }

        /// <summary>Closes the list without choosing anything.</summary>
        /// <remarks>The list stops taking input at once, and folds away over the next few frames.</remarks>
        public void Close()
        {
            if (!this.IsOpen)
                return;

            this.IsOpen = false;
            this.ClosedAt = DateTime.UtcNow;
        }

        /// <summary>Handles a click anywhere on screen while the list is open.</summary>
        /// <param name="x">The cursor X position.</param>
        /// <param name="y">The cursor Y position.</param>
        /// <param name="value">The chosen value, if a row was clicked.</param>
        /// <returns>Whether the click was consumed by the list, including a click outside it that dismissed it.</returns>
        public bool ReceiveLeftClick(int x, int y, out object value)
        {
            value = null;
            if (!this.IsOpen)
                return false;

            if (!this.Bounds.Contains(x, y))
            {
                // A click anywhere else closes the list, and is swallowed so it doesn't also hit the menu behind it.
                this.Close();
                return true;
            }

            int index = this.GetRowAt(x, y);
            if (index >= 0)
            {
                value = this.Options[index].Value;
                Game1.playSound("smallSelect");
                this.Close();
                return true;
            }

            return true;
        }

        /// <summary>Tracks which row the cursor is over.</summary>
        public void PerformHover(int x, int y)
        {
            this.HoverIndex = this.IsOpen ? this.GetRowAt(x, y) : -1;
            this.RowHover.Hover(this.HoverIndex >= 0 ? this.HoverIndex : null);
        }

        /// <summary>Scrolls the list.</summary>
        /// <returns>Whether the scroll was consumed.</returns>
        public bool ReceiveScroll(int direction)
        {
            if (!this.IsOpen || this.Options.Count <= MaxVisibleRows)
                return this.IsOpen;

            this.Scroll = Math.Clamp(this.Scroll + (direction > 0 ? -1 : 1), 0, this.Options.Count - MaxVisibleRows);
            return true;
        }

        /// <summary>Draws the list. Call this last, so it sits above the rest of the menu.</summary>
        public void Draw(SpriteBatch b)
        {
            // Unfurling from the button as it opens; folding back into it once closed, then gone.
            float shown;
            if (this.IsOpen)
                shown = UiAnimation.EaseOut(UiAnimation.Progress(this.OpenedAt, OpenMs));
            else
            {
                float closing = this.ClosedAt is DateTime closedAt ? UiAnimation.Progress(closedAt, CloseMs) : 1f;
                if (closing >= 1f)
                {
                    this.Options.Clear();
                    this.ClosedAt = null;
                    return;
                }
                shown = 1f - (closing * closing);
            }

            if (Game1.currentGameTime != null)
                this.RowHover.Update(Game1.currentGameTime);

            if (shown >= 1f)
            {
                this.DrawList(b);
                return;
            }

            // Clipped to the part unfurled so far, with the list sliding out from under its button.
            int height = (int)Math.Ceiling(this.Bounds.Height * shown) + 12;
            Rectangle clip = this.OpensUp
                ? new Rectangle(this.Bounds.X - 4, this.Bounds.Bottom - height + 12, this.Bounds.Width + 16, height)
                : new Rectangle(this.Bounds.X - 4, this.Bounds.Y, this.Bounds.Width + 16, height);
            Vector2 slide = new(0, (1f - shown) * 20 * (this.OpensUp ? 1 : -1));

            if (!UiBatch.Push(b, clip, slide))
                return;
            try
            {
                this.DrawList(b);
            }
            finally
            {
                UiBatch.Pop(b);
            }
        }

        /// <summary>Draws the list itself.</summary>
        private void DrawList(SpriteBatch b)
        {
            IClickableMenu.drawTextureBox(
                b,
                Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                this.Bounds.X,
                this.Bounds.Y,
                this.Bounds.Width,
                this.Bounds.Height,
                Color.White,
                1f,
                drawShadow: true
            );

            int visible = Math.Min(this.Options.Count, MaxVisibleRows);
            for (int i = 0; i < visible; i++)
            {
                int index = this.Scroll + i;
                if (index >= this.Options.Count)
                    break;

                Rectangle row = this.GetRowBounds(i);
                float glow = (this.RowHover.Get(index) - 1f) / (HoverScales.MaxScale - 1f);
                if (glow > 0)
                    b.Draw(Game1.staminaRect, row, Color.Wheat * (0.55f * glow));

                Option option = this.Options[index];
                int textX = row.X + 12;
                if (option.Icon != null)
                {
                    ItemIcon.Draw(b, option.Icon, new Rectangle(row.X + 8, row.Y + 4, 32, 32), 1f, showQuality: false);
                    textX += 38;
                }
                Marquee.Draw(b, option.Label, Game1.smallFont, new Vector2(textX, row.Y + 8), row.Right - 12 - textX, Game1.textColor);
            }

            if (this.Options.Count > MaxVisibleRows)
            {
                string more = $"+{this.Options.Count - MaxVisibleRows}";
                Utility.drawTextWithShadow(b, more, Game1.tinyFont, new Vector2(this.Bounds.Right - 44, this.Bounds.Bottom - 22), Game1.textColor * 0.6f);
            }
        }


        /*********
        ** Private methods
        *********/
        /// <summary>The index of the option under a screen position, or -1.</summary>
        private int GetRowAt(int x, int y)
        {
            int visible = Math.Min(this.Options.Count, MaxVisibleRows);
            for (int i = 0; i < visible; i++)
            {
                if (this.GetRowBounds(i).Contains(x, y))
                {
                    int index = this.Scroll + i;
                    return index < this.Options.Count ? index : -1;
                }
            }
            return -1;
        }

        /// <summary>The bounds of the i'th visible row.</summary>
        private Rectangle GetRowBounds(int visibleIndex)
        {
            return new Rectangle(
                this.Bounds.X + 8,
                this.Bounds.Y + 8 + (visibleIndex * RowHeight),
                this.Bounds.Width - 16,
                RowHeight
            );
        }

        /// <summary>The width needed to show the longest label, capped so one long mod name can't fill the screen.</summary>
        private int MeasureWidth()
        {
            float widest = 0;
            foreach (Option option in this.Options)
                widest = Math.Max(widest, Game1.smallFont.MeasureString(option.Label).X + (option.Icon != null ? 38 : 0));

            return Math.Clamp((int)widest + 48, 160, 460);
        }


        /*********
        ** Nested types
        *********/
        /// <summary>One selectable row.</summary>
        private class Option
        {
            public readonly string Label;
            public readonly object Value;
            public readonly Item Icon;

            public Option(string label, object value, Item icon)
            {
                this.Label = label;
                this.Value = value;
                this.Icon = icon;
            }
        }
    }
}
