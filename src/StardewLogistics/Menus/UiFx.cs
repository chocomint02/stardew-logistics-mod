using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;

namespace StardewLogistics.Menus
{
    /// <summary>Small touches that answer the cursor: a highlight on what it's over, and a ripple where it clicks.</summary>
    /// <remarks>
    /// A menu registers each control as it draws it, and gets back, in the same call, the control's hover
    /// highlight -- a soft wash that fades in, and an accent line growing from the middle of its bottom edge -- and
    /// any ripple spreading from where it was clicked, clipped to the control. Controls are known by where they are,
    /// so nothing about them needs to be stored. It keeps itself up to date: once a tick, on the first control drawn,
    /// it moves the highlights along and notices a click from the mouse itself, so a menu needs nothing but the
    /// calls where it draws its controls. Everything follows the animation speed; at zero, the highlight is just on
    /// or off and there are no ripples.
    /// </remarks>
    internal class UiFx
    {
        /*********
        ** Fields
        *********/
        /// <summary>How long a ripple takes to spread and fade, at normal speed.</summary>
        private const double RippleMs = 420;

        /// <summary>The controls drawn this frame, for a click to find the one it's on.</summary>
        private readonly List<Rectangle> Controls = new();

        /// <summary>The controls drawn last frame, which is when the click happened.</summary>
        private List<Rectangle> LastControls = new();

        /// <summary>Each control's highlight, by where it is.</summary>
        private readonly HoverScales Hover = new();

        /// <summary>Ripples spreading, each within its control.</summary>
        private readonly List<(Rectangle Control, Point From, DateTime At)> Ripples = new();

        /// <summary>A soft white disc, for ripples.</summary>
        private static Texture2D Disc;

        /// <summary>The game tick the controls being registered belong to.</summary>
        private int FrameTick = -1;

        /// <summary>Whether the left button was down last tick, for noticing a click.</summary>
        private bool WasPressed = true;


        /*********
        ** Public methods
        *********/
        /// <summary>Registers a control and draws its highlight and any ripple over it. Call after drawing the control itself.</summary>
        /// <param name="b">The sprite batch.</param>
        /// <param name="control">Where the control is.</param>
        /// <param name="inset">How far inside its bounds the highlight sits, clear of a border.</param>
        public void Control(SpriteBatch b, Rectangle control, int inset = 4)
        {
            if (control.Width <= 0 || control.Height <= 0)
                return;

            this.Tick();
            this.Controls.Add(control);

            float glow = (this.Hover.Get(Key(control)) - 1f) / (HoverScales.MaxScale - 1f);
            Rectangle face = new(control.X + inset, control.Y + inset, control.Width - (inset * 2), control.Height - (inset * 2));
            if (glow > 0f && face.Width > 0 && face.Height > 0)
            {
                // A soft wash in the text colour, so it suits any scheme, and an accent line growing from the middle.
                b.Draw(Game1.staminaRect, face, Game1.textColor * (0.07f * glow));
                int line = (int)(face.Width * glow);
                b.Draw(Game1.staminaRect, new Rectangle(face.Center.X - (line / 2), face.Bottom - 2, line, 2), Game1.textColor * (0.45f * glow));
            }

            foreach ((Rectangle within, Point from, DateTime at) in this.Ripples.Where(ripple => ripple.Control == control).ToList())
                this.DrawRipple(b, within, from, at);
        }

        /// <summary>A soft white disc, for glows and ripples.</summary>
        public static Texture2D SoftDisc => Disc ??= MakeDisc(64);

        /// <summary>How lit a control's highlight is, from 0 to 1, for controls that draw their own.</summary>
        public float GlowOf(Rectangle control) => (this.Hover.Get(Key(control)) - 1f) / (HoverScales.MaxScale - 1f);


        /*********
        ** Private methods
        *********/
        /// <summary>Once a tick: the last frame's controls become the ones the cursor is tested against, the highlights
        /// move towards the one under it, and a click starts a ripple on the control it's on.</summary>
        private void Tick()
        {
            if (Game1.ticks == this.FrameTick)
                return;
            this.FrameTick = Game1.ticks;

            this.LastControls = new List<Rectangle>(this.Controls);
            this.Controls.Clear();

            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();
            Rectangle over = this.LastControls.LastOrDefault(control => control.Contains(mouseX, mouseY));
            this.Hover.Hover(over.IsEmpty ? null : Key(over));
            if (Game1.currentGameTime != null)
                this.Hover.Update(Game1.currentGameTime);

            bool pressed = Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;
            if (pressed && !this.WasPressed && !over.IsEmpty && UiAnimation.Enabled)
                this.Ripples.Add((over, new Point(mouseX, mouseY), DateTime.UtcNow));
            this.WasPressed = pressed;

            this.Ripples.RemoveAll(ripple => UiAnimation.Progress(ripple.At, RippleMs) >= 1f);
        }

        /// <summary>Draws a ripple spreading from a click to fill its control, fading as it goes.</summary>
        private void DrawRipple(SpriteBatch b, Rectangle control, Point from, DateTime at)
        {
            float t = UiAnimation.Progress(at, RippleMs);
            if (t >= 1f || !UiBatch.Push(b, control, Vector2.Zero))
                return;

            try
            {
                Disc ??= MakeDisc(64);
                float reach = (float)Math.Sqrt(Math.Pow(Math.Max(from.X - control.Left, control.Right - from.X), 2) + Math.Pow(Math.Max(from.Y - control.Top, control.Bottom - from.Y), 2));
                float radius = Math.Max(6f, reach * UiAnimation.EaseOut(t));
                float alpha = 0.22f * (1f - t);
                b.Draw(Disc, new Rectangle((int)(from.X - radius), (int)(from.Y - radius), (int)(radius * 2), (int)(radius * 2)), Game1.textColor * alpha);
            }
            finally
            {
                UiBatch.Pop(b);
            }
        }

        /// <summary>A control's key, from where it is.</summary>
        private static int Key(Rectangle control) => HashCode.Combine(control.X, control.Y, control.Width, control.Height);

        /// <summary>Makes a white disc with a soft edge.</summary>
        private static Texture2D MakeDisc(int size)
        {
            Color[] pixels = new Color[size * size];
            float centre = (size - 1) / 2f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = (float)Math.Sqrt(((x - centre) * (x - centre)) + ((y - centre) * (y - centre))) / centre;
                    float alpha = Math.Clamp((1f - distance) * 6f, 0f, 1f);
                    pixels[(y * size) + x] = Color.White * alpha;
                }
            }

            Texture2D texture = new(Game1.graphics.GraphicsDevice, size, size);
            texture.SetData(pixels);
            return texture;
        }
    }
}
