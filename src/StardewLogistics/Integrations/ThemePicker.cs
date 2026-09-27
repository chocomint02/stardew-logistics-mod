using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewLogistics.Framework;
using StardewLogistics.Menus;
using StardewModdingAPI;
using StardewValley;

namespace StardewLogistics.Integrations
{
    /// <summary>The colour scheme option in Generic Mod Config Menu: arrows either side of a preview and its name.</summary>
    /// <remarks>
    /// A plain dropdown can only list names, so the option is drawn here instead. The choice is held until the
    /// player saves, as with every other option on the page, and dropped if they leave without saving.
    /// </remarks>
    internal class ThemePicker
    {
        /*********
        ** Fields
        *********/
        private readonly Func<ModConfig> Config;
        private readonly ITranslationHelper Translations;

        /// <summary>The scheme picked but not yet saved.</summary>
        private string Pending = UiTheme.Vanilla;

        /// <summary>Whether the left button was down last frame, to tell a click from a hold.</summary>
        private bool WasPressed;


        /*********
        ** Public methods
        *********/
        public ThemePicker(Func<ModConfig> config, ITranslationHelper translations)
        {
            this.Config = config;
            this.Translations = translations;
        }

        /// <summary>Adds the option to the config menu.</summary>
        public void Register(IGenericModConfigMenuApi api, IManifest manifest)
        {
            api.AddComplexOption(
                manifest,
                name: () => this.Translations.Get("config.theme.name"),
                draw: this.Draw,
                tooltip: () => this.Translations.Get("config.theme.tooltip"),
                beforeMenuOpened: () =>
                {
                    this.Pending = this.Config().Theme;
                    this.WasPressed = true; // the click that opened the menu isn't one on the picker
                },
                beforeSave: () => this.Config().Theme = this.Pending,
                afterReset: () => this.Pending = UiTheme.Vanilla,
                height: () => 52
            );
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Draws the picker and handles clicks on it.</summary>
        /// <remarks>Config menu options are only drawn, so clicks are read from the mouse here.</remarks>
        private void Draw(SpriteBatch b, Vector2 position)
        {
            int x = (int)position.X;
            int y = (int)position.Y;

            Rectangle left = new(x, y + 12, 36, 32);
            Rectangle swatch = new(left.Right + 12, y + 4, 56, 44);
            int nameWidth = 0;
            foreach (string scheme in UiTheme.Names)
                nameWidth = Math.Max(nameWidth, (int)Game1.smallFont.MeasureString(this.GetName(scheme)).X);
            Rectangle name = new(swatch.Right + 14, y + 4, nameWidth, 44);
            Rectangle right = new(name.Right + 14, y + 12, 36, 32);

            // Clicks: the arrows step through the schemes, and clicking the preview or name moves on one.
            bool pressed = Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;
            if (pressed && !this.WasPressed)
            {
                int mouseX = Game1.getMouseX();
                int mouseY = Game1.getMouseY();
                int step = left.Contains(mouseX, mouseY) ? -1
                    : right.Contains(mouseX, mouseY) || swatch.Contains(mouseX, mouseY) || name.Contains(mouseX, mouseY) ? 1
                    : 0;

                if (step != 0)
                {
                    this.Pending = UiTheme.Step(this.Pending, step);
                    Game1.playSound("smallSelect");
                }
            }
            this.WasPressed = pressed;

            b.Draw(Game1.mouseCursors, left, new Rectangle(352, 495, 12, 11), Color.White);
            b.Draw(Game1.mouseCursors, right, new Rectangle(365, 495, 12, 11), Color.White);

            UiTheme.DrawSwatch(b, this.Pending, swatch);
            Utility.drawTextWithShadow(b, this.GetName(this.Pending), Game1.smallFont, new Vector2(name.X, name.Y + 10), Game1.textColor);
        }

        /// <summary>A scheme's translated name.</summary>
        private string GetName(string scheme) => this.Translations.Get("theme." + scheme.ToLowerInvariant()).Default(scheme);
    }
}
