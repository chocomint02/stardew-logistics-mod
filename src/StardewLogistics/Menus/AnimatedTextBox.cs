using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace StardewLogistics.Menus
{
    /// <summary>The game's text box, drawn with typing that animates.</summary>
    /// <remarks>
    /// Typing works exactly as in the game's box; only the drawing differs. Each letter typed pops up into place
    /// as it fades in, and each one deleted floats up and fades away. The caret glides to where the next letter
    /// goes, stays solid while typing, and breathes gently when idle rather than blinking. Clicking into the box
    /// fades in a focus outline with a line growing along its foot, and fades the placeholder out. Everything
    /// follows the animation speed; at zero it draws as the game's box does.
    /// </remarks>
    internal class AnimatedTextBox : TextBox
    {
        /*********
        ** Fields
        *********/
        /// <summary>How long a letter takes to appear, and to go, and the box to take focus, at normal speed.</summary>
        private const double LetterInMs = 150;
        private const double LetterOutMs = 170;
        private const double FocusMs = 180;

        /// <summary>How long after a key the caret stays solid before it starts to breathe.</summary>
        private const double CaretHoldMs = 500;

        private static readonly FieldInfo TextureField = typeof(TextBox).GetField("_textBoxTexture", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo FontField = typeof(TextBox).GetField("_font", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo ColourField = typeof(TextBox).GetField("_textColor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>The text as last drawn, and when each of its letters appeared.</summary>
        private string LastText;
        private readonly List<DateTime> Born = new();

        /// <summary>Letters deleted and fading away: the letter, how far along the text it was, and when it went.</summary>
        private readonly List<(string Letter, float X, DateTime At)> Ghosts = new();

        /// <summary>Where the caret is drawn, gliding to the end of the text, and when a key was last typed.</summary>
        private float CaretX = -1;
        private DateTime LastTypedAt = DateTime.MinValue;

        /// <summary>How focused the box looks, from 0 to 1, and what it was easing from when focus last changed.</summary>
        private float Focus;
        private float FocusFrom;
        private bool? WasSelected;
        private DateTime FocusChangedAt = DateTime.MinValue;


        /*********
        ** Accessors
        *********/
        /// <summary>Text shown faintly while the box is empty and not being typed in.</summary>
        public string Placeholder { get; set; }


        /*********
        ** Public methods
        *********/
        /// <inheritdoc cref="TextBox(Texture2D, Texture2D, SpriteFont, Color)"/>
        public AnimatedTextBox(Texture2D textBoxTexture, Texture2D caretTexture, SpriteFont font, Color textColor)
            : base(textBoxTexture, caretTexture, font, textColor) { }

        /// <inheritdoc />
        public override void Draw(SpriteBatch spriteBatch, bool drawShadow = true)
        {
            Texture2D texture = TextureField?.GetValue(this) as Texture2D;
            SpriteFont font = FontField?.GetValue(this) as SpriteFont ?? Game1.smallFont;
            Color colour = ColourField?.GetValue(this) is Color value ? value : Game1.textColor;
            DateTime now = DateTime.UtcNow;

            // The box, as the game draws it.
            if (texture != null)
            {
                spriteBatch.Draw(texture, new Rectangle(this.X, this.Y, 16, this.Height), new Rectangle(0, 0, 16, this.Height), Color.White);
                spriteBatch.Draw(texture, new Rectangle(this.X + 16, this.Y, this.Width - 32, this.Height), new Rectangle(16, 0, 4, this.Height), Color.White);
                spriteBatch.Draw(texture, new Rectangle(this.X + this.Width - 16, this.Y, 16, this.Height), new Rectangle(texture.Bounds.Width - 16, 0, 16, this.Height), Color.White);
            }
            else
                Game1.drawDialogueBox(this.X - 32, this.Y - 112 + 10, this.Width + 80, this.Height, speaker: false, drawOnlyBox: true);

            string text = this.PasswordBox ? new string('*', this.Text?.Length ?? 0) : this.Text ?? "";
            this.Track(text, font, now);
            this.UpdateFocus();

            // Focus: an outline fading in, and a line growing out from the middle of the foot.
            if (this.Focus > 0.01f)
            {
                Rectangle inner = new(this.X + 4, this.Y + 4, this.Width - 8, this.Height - 8);
                DrawRing(spriteBatch, inner, colour * (0.25f * this.Focus));
                int line = (int)((this.Width - 32) * this.Focus);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(this.X + (this.Width / 2) - (line / 2), this.Y + this.Height - 7, line, 2), colour * (0.55f * this.Focus));
            }

            // The text centred down the box, clear of the focus line at its foot.
            float textHeight = font.MeasureString("Ay").Y;
            Vector2 origin = new(this.X + 16, this.Y + (int)((this.Height - textHeight) / 2) + 1);

            // The placeholder, fading and drifting aside as the box takes focus.
            if (text.Length == 0 && !string.IsNullOrEmpty(this.Placeholder) && this.Focus < 0.99f)
                Utility.drawTextWithShadow(spriteBatch, this.Placeholder, font, origin + new Vector2(10 * this.Focus, 0), Game1.textColor * (0.45f * (1f - this.Focus)), shadowIntensity: 1f - this.Focus);

            // Letters going: floating up as they fade.
            foreach ((string letter, float x, DateTime at) in this.Ghosts)
            {
                float t = UiAnimation.EaseOut(UiAnimation.Progress(at, LetterOutMs));
                Utility.drawTextWithShadow(spriteBatch, letter, font, origin + new Vector2(x, -8 * t), colour * (1f - t), shadowIntensity: 1f - t);
            }

            // Letters, each popping into place as it fades in.
            float along = 0;
            for (int i = 0; i < text.Length; i++)
            {
                string letter = text[i].ToString();
                Vector2 size = font.MeasureString(letter);
                float t = i < this.Born.Count ? UiAnimation.EaseOut(UiAnimation.Progress(this.Born[i], LetterInMs)) : 1f;
                float scale = 1f + (0.3f * (1f - t));
                Vector2 position = origin + new Vector2(along, 6 * (1f - t)) - (size * (scale - 1f) / 2f);
                Utility.drawTextWithShadow(spriteBatch, letter, font, position, colour * t, scale, shadowIntensity: t);

                // Measured with what comes before, so kerning matches the game's own drawing.
                along = font.MeasureString(text.Substring(0, i + 1)).X;
            }

            // The caret: gliding to the end, solid while typing, breathing when idle.
            if (this.Selected)
            {
                float target = origin.X + along + 2;
                float seconds = (float)(Game1.currentGameTime?.ElapsedGameTime.TotalSeconds ?? 0.016);
                float ease = UiAnimation.Enabled ? 1f - (float)Math.Exp(-seconds * 28 * UiAnimation.SpeedFactor) : 1f;
                this.CaretX = this.CaretX < 0 ? target : MathHelper.Lerp(this.CaretX, target, ease);

                double idle = (now - this.LastTypedAt).TotalMilliseconds - CaretHoldMs;
                float alpha = !UiAnimation.Enabled
                    ? (now.Millisecond < 500 ? 1f : 0f)
                    : idle <= 0 ? 1f : 0.55f + (0.45f * (float)Math.Cos(idle / 1000.0 * Math.PI * 2 * UiAnimation.SpeedFactor));
                int height = (int)font.MeasureString("A").Y - 4;
                spriteBatch.Draw(Game1.staminaRect, new Rectangle((int)this.CaretX, (int)origin.Y + 2, 3, height), colour * (alpha * this.Focus));
            }
            else
                this.CaretX = -1;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Notices what's been typed or deleted since the last frame, for animating it.</summary>
        private void Track(string text, SpriteFont font, DateTime now)
        {
            // What's there when first shown is just there.
            if (this.LastText == null || this.Born.Count != (this.LastText?.Length ?? 0))
            {
                this.LastText = text;
                this.Born.Clear();
                this.Born.AddRange(Enumerable.Repeat(DateTime.MinValue, text.Length));
            }

            if (text != this.LastText)
            {
                // What changed: the part between what's the same at the start and the same at the end.
                string old = this.LastText;
                int prefix = 0;
                while (prefix < old.Length && prefix < text.Length && old[prefix] == text[prefix])
                    prefix++;
                int suffix = 0;
                while (suffix < old.Length - prefix && suffix < text.Length - prefix && old[old.Length - 1 - suffix] == text[text.Length - 1 - suffix])
                    suffix++;

                int removed = old.Length - prefix - suffix;
                int added = text.Length - prefix - suffix;
                for (int k = 0; k < removed; k++)
                    this.Ghosts.Add((old[prefix + k].ToString(), font.MeasureString(old.Substring(0, prefix + k)).X, now));

                this.Born.RemoveRange(prefix, removed);
                this.Born.InsertRange(prefix, Enumerable.Repeat(now, added));
                this.LastText = text;
                this.LastTypedAt = now;
            }

            this.Ghosts.RemoveAll(ghost => UiAnimation.Progress(ghost.At, LetterOutMs) >= 1f);
        }

        /// <summary>Eases the focus look towards whether the box is being typed in.</summary>
        private void UpdateFocus()
        {
            if (this.WasSelected != this.Selected)
            {
                this.FocusFrom = this.WasSelected == null ? (this.Selected ? 1f : 0f) : this.Focus;
                this.FocusChangedAt = this.WasSelected == null ? DateTime.MinValue : DateTime.UtcNow;
                this.WasSelected = this.Selected;
            }

            float t = UiAnimation.EaseOut(UiAnimation.Progress(this.FocusChangedAt, FocusMs));
            this.Focus = MathHelper.Lerp(this.FocusFrom, this.Selected ? 1f : 0f, t);
        }

        /// <summary>A one-pixel outline.</summary>
        private static void DrawRing(SpriteBatch b, Rectangle area, Color colour)
        {
            b.Draw(Game1.staminaRect, new Rectangle(area.X, area.Y, area.Width, 1), colour);
            b.Draw(Game1.staminaRect, new Rectangle(area.X, area.Bottom - 1, area.Width, 1), colour);
            b.Draw(Game1.staminaRect, new Rectangle(area.X, area.Y, 1, area.Height), colour);
            b.Draw(Game1.staminaRect, new Rectangle(area.Right - 1, area.Y, 1, area.Height), colour);
        }
    }
}
