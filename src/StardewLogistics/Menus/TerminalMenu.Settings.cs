using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewLogistics.Menus
{
    /// <summary>The terminal's Settings tab: animation speed and colour scheme.</summary>
    /// <remarks>
    /// The same settings Generic Mod Config Menu offers, so they can be tried where they're seen. A change takes
    /// effect at once -- the window redraws in a scheme as soon as it's picked -- and is written to the config.
    /// </remarks>
    internal partial class TerminalMenu
    {
        /*********
        ** Fields
        *********/
        /// <summary>Writes the config to disk, for settings changed here.</summary>
        public static Action<Framework.ModConfig> SaveConfig { get; set; }

        /// <summary>How many scheme cards sit side by side.</summary>
        private const int SchemeColumns = 4;

        /// <summary>The height of a scheme card.</summary>
        private const int SchemeCardHeight = 48;

        /// <summary>The space between scheme cards.</summary>
        private const int SchemeCardGap = 8;

        /// <summary>How much one click changes the animation speed, and with Shift held.</summary>
        private const int SpeedStep = 10;
        private const int SpeedBigStep = 50;


        /*********
        ** Private methods: layout
        *********/
        /// <summary>The animation speed buttons.</summary>
        private (Rectangle Minus, Rectangle Plus) GetSpeedButtons()
        {
            Rectangle grid = this.GetGridBounds();
            int labelWidth = (int)Game1.smallFont.MeasureString(this.Translations.Get("settings.animation-speed")).X + 24;
            Rectangle minus = new(grid.X + labelWidth, grid.Y + 4, 44, 44);
            return (minus, new Rectangle(minus.Right + 100, minus.Y, 44, 44));
        }

        /// <summary>The top of the scheme cards.</summary>
        private int SchemeCardsTop => this.GetGridBounds().Y + 104;

        /// <summary>How many rows of scheme cards fit.</summary>
        private int VisibleSchemeRows => Math.Max(1, (this.GetContentBounds().Bottom - this.SchemeCardsTop) / (SchemeCardHeight + SchemeCardGap));

        /// <summary>How many rows of scheme cards there are.</summary>
        private static int SchemeRows => (int)Math.Ceiling(UiTheme.Names.Count / (double)SchemeColumns);

        /// <summary>The scheme cards on show, with the scheme each picks.</summary>
        private IEnumerable<(Rectangle Bounds, string Scheme)> GetSchemeCards()
        {
            Rectangle grid = this.GetGridBounds();
            int width = (grid.Width - ((SchemeColumns - 1) * SchemeCardGap)) / SchemeColumns;
            int first = this.ScrollOffset * SchemeColumns;
            int last = Math.Min(UiTheme.Names.Count, first + (this.VisibleSchemeRows * SchemeColumns));

            for (int i = first; i < last; i++)
            {
                int row = (i - first) / SchemeColumns;
                int column = i % SchemeColumns;
                yield return (new Rectangle(grid.X + (column * (width + SchemeCardGap)), this.SchemeCardsTop + (row * (SchemeCardHeight + SchemeCardGap)), width, SchemeCardHeight), UiTheme.Names[i]);
            }
        }

        /// <summary>The largest scroll offset for the scheme cards.</summary>
        private int GetMaxSettingsScroll() => Math.Max(0, SchemeRows - this.VisibleSchemeRows);


        /*********
        ** Private methods: drawing
        *********/
        /// <summary>Draws the Settings tab.</summary>
        private void DrawSettingsTab(SpriteBatch b)
        {
            Rectangle grid = this.GetGridBounds();

            // Animation speed: - 100% +
            (Rectangle minus, Rectangle plus) = this.GetSpeedButtons();
            Utility.drawTextWithShadow(b, this.Translations.Get("settings.animation-speed"), Game1.smallFont, new Vector2(grid.X, minus.Y + 10), Game1.textColor);
            foreach ((Rectangle bounds, string label) in new[] { (minus, "-"), (plus, "+") })
            {
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White, 2f, drawShadow: false);
                Vector2 size = Game1.smallFont.MeasureString(label);
                Utility.drawTextWithShadow(b, label, Game1.smallFont, new Vector2(bounds.Center.X - (size.X / 2), bounds.Center.Y - (size.Y / 2)), Game1.textColor);
            }

            string speed = this.Config.AnimationSpeed == 0 ? this.Translations.Get("config.animation-speed.off") : this.Config.AnimationSpeed + "%";
            Vector2 speedSize = Game1.smallFont.MeasureString(speed);
            Utility.drawTextWithShadow(b, speed, Game1.smallFont, new Vector2(((minus.Right + plus.X) / 2) - (speedSize.X / 2), minus.Y + 10), Game1.textColor);

            // Colour scheme, as a gallery of cards.
            string current = this.Translations.Get("settings.scheme", new { name = this.GetSchemeName(this.Config.Theme) });
            Utility.drawTextWithShadow(b, current, Game1.smallFont, new Vector2(grid.X, this.SchemeCardsTop - 42), Game1.textColor);

            foreach ((Rectangle bounds, string scheme) in this.GetSchemeCards())
            {
                bool chosen = string.Equals(scheme, this.Config.Theme, StringComparison.OrdinalIgnoreCase);
                drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, chosen ? Color.White : Color.White * 0.7f, 3f, drawShadow: false);
                if (chosen)
                    DrawOutline(b, bounds, Color.Gold);

                Rectangle swatch = new(bounds.X + 10, bounds.Y + 8, 44, bounds.Height - 16);
                UiTheme.DrawSwatch(b, scheme, swatch);

                int textX = swatch.Right + 10;
                Marquee.Draw(b, this.GetSchemeName(scheme), Game1.smallFont, new Vector2(textX, bounds.Y + 12), bounds.Right - 10 - textX, chosen ? Game1.textColor : Game1.textColor * 0.8f);
            }

            int visible = this.VisibleSchemeRows;
            if (SchemeRows > visible)
            {
                Rectangle area = new(grid.X, this.SchemeCardsTop, grid.Width, (visible * (SchemeCardHeight + SchemeCardGap)) - SchemeCardGap);
                this.DrawScrollbar(b, area, visible, SchemeRows);
            }
        }

        /// <summary>Draws a two-pixel outline round a box.</summary>
        private static void DrawOutline(SpriteBatch b, Rectangle box, Color colour)
        {
            b.Draw(Game1.staminaRect, new Rectangle(box.X, box.Y, box.Width, 2), colour);
            b.Draw(Game1.staminaRect, new Rectangle(box.X, box.Bottom - 2, box.Width, 2), colour);
            b.Draw(Game1.staminaRect, new Rectangle(box.X, box.Y, 2, box.Height), colour);
            b.Draw(Game1.staminaRect, new Rectangle(box.Right - 2, box.Y, 2, box.Height), colour);
        }

        /// <summary>A scheme's translated name.</summary>
        private string GetSchemeName(string scheme) => this.Translations.Get("theme." + scheme.ToLowerInvariant()).Default(scheme);


        /*********
        ** Private methods: input
        *********/
        /// <summary>Handles a click on the Settings tab.</summary>
        private void ReceiveClickOnSettings(int x, int y)
        {
            (Rectangle minus, Rectangle plus) = this.GetSpeedButtons();
            if (minus.Contains(x, y) || plus.Contains(x, y))
            {
                int step = (IsShiftDown() ? SpeedBigStep : SpeedStep) * (plus.Contains(x, y) ? 1 : -1);
                this.Config.AnimationSpeed = Math.Clamp(this.Config.AnimationSpeed + step, 0, 300);
                this.SaveAppearance();
                Game1.playSound("drumkit6");
                return;
            }

            foreach ((Rectangle bounds, string scheme) in this.GetSchemeCards())
            {
                if (!bounds.Contains(x, y) || string.Equals(scheme, this.Config.Theme, StringComparison.OrdinalIgnoreCase))
                    continue;

                this.Config.Theme = scheme;
                this.SaveAppearance();
                UiTheme.Restyle(this.SearchBox);
                Game1.playSound("smallSelect");
                return;
            }
        }

        /// <summary>Sets the hover text on the Settings tab.</summary>
        private void PerformHoverOnSettings(int x, int y)
        {
            (Rectangle minus, Rectangle plus) = this.GetSpeedButtons();
            if (minus.Contains(x, y) || plus.Contains(x, y))
                this.HoverText = this.Translations.Get("settings.speed-hint");
        }

        /// <summary>Puts the appearance settings into effect and writes them to the config.</summary>
        private void SaveAppearance()
        {
            this.Config.Normalise();
            this.Config.ApplyAppearance();
            SaveConfig?.Invoke(this.Config);
        }
    }
}
