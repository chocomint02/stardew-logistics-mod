using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Framework;
using StardewValley;

namespace StardewLogistics.Menus
{
    /// <summary>A colour scheme for the mod's windows.</summary>
    /// <remarks>
    /// Six colours stand in for the six shades the game's menu sprites are drawn in, darkest first -- outline,
    /// edge, frame, inner shading, panel and highlight -- so a recoloured window keeps every line and bevel of
    /// the original. A dark scheme can run them out of order: a light frame round a dark panel.
    /// </remarks>
    internal sealed class UiPalette
    {
        public string Name { get; init; }
        public Color Outline { get; init; }
        public Color Edge { get; init; }
        public Color Frame { get; init; }
        public Color Shading { get; init; }
        public Color Panel { get; init; }
        public Color Highlight { get; init; }

        /// <summary>The text colour.</summary>
        public Color Text { get; init; }

        /// <summary>The colour of the shadow the game draws under text.</summary>
        public Color TextShadow { get; init; }

        /// <summary>Whether text is light on a dark panel, so fixed status colours need lifting to be read.</summary>
        public bool IsDark { get; init; }
    }

    /// <summary>Draws the mod's windows in the chosen colour scheme.</summary>
    /// <remarks>
    /// The game draws every window, slot, button and tooltip from two shared textures, and all text in one colour,
    /// each held in a static field. While one of the mod's menus draws, <see cref="Apply"/> points those fields at
    /// recoloured copies and puts them back afterwards, so the game's own drawing -- the inventory, tooltips,
    /// text boxes -- comes out in the scheme with no code of its own, and nothing outside the mod's menus is
    /// touched.
    ///
    /// Only the parts of each texture the windows are made of are recoloured, by mapping each pixel's brightness
    /// onto the scheme; icons that share the textures keep their colours. A copy is made once per scheme, the
    /// first time it's drawn.
    /// </remarks>
    internal static class UiTheme
    {
        /*********
        ** Fields
        *********/
        /// <summary>The scheme that leaves the game's look alone.</summary>
        public const string Vanilla = "Vanilla";

        /// <summary>The brightness of each of the game's six menu shades, which the scheme's colours replace.</summary>
        private static readonly float[] Stops = { 0.13f, 0.28f, 0.54f, 0.66f, 0.80f, 0.91f };

        /// <summary>The parts of the menu texture windows are made of: the window box, and an inventory slot.</summary>
        private static readonly Rectangle[] MenuRegions = { new(0, 256, 60, 60), new(128, 128, 64, 64) };

        /// <summary>The parts of the cursor sheet buttons are made of: a button, the scrollbar and its thumb, and the -/+ arrows.</summary>
        private static readonly Rectangle[] CursorRegions = { new(384, 396, 15, 15), new(403, 383, 6, 6), new(435, 463, 6, 10), new(177, 345, 7, 8), new(184, 345, 7, 8) };

        /// <summary>The schemes on offer.</summary>
        /// <remarks>Vanilla's colours are the game's own, for its preview; it's never applied.</remarks>
        private static readonly UiPalette[] Palettes =
        {
            new()
            {
                Name = "Vanilla",
                Outline = new(91, 43, 42), Edge = new(133, 54, 5), Frame = new(220, 123, 5), Shading = new(236, 166, 95), Panel = new(255, 210, 132), Highlight = new(255, 228, 161),
                Text = new(34, 17, 34), TextShadow = new(221, 148, 84)
            },
            new()
            {
                Name = "Cream",
                Outline = new(84, 62, 50), Edge = new(138, 108, 84), Frame = new(201, 172, 134), Shading = new(226, 206, 172), Panel = new(243, 231, 205), Highlight = new(251, 245, 230),
                Text = new(62, 46, 38), TextShadow = new(222, 205, 175)
            },
            new()
            {
                Name = "Light",
                Outline = new(58, 62, 72), Edge = new(106, 112, 126), Frame = new(168, 176, 190), Shading = new(208, 214, 224), Panel = new(234, 238, 244), Highlight = new(248, 250, 252),
                Text = new(36, 40, 48), TextShadow = new(206, 211, 220)
            },
            new()
            {
                Name = "Dark",
                Outline = new(10, 10, 12), Edge = new(34, 35, 40), Frame = new(86, 88, 98), Shading = new(62, 64, 72), Panel = new(46, 47, 53), Highlight = new(60, 62, 70),
                Text = new(230, 230, 236), TextShadow = new(16, 16, 20), IsDark = true
            },
            new()
            {
                Name = "Midnight",
                Outline = new(6, 8, 22), Edge = new(20, 26, 56), Frame = new(62, 84, 150), Shading = new(40, 52, 100), Panel = new(26, 33, 70), Highlight = new(38, 48, 96),
                Text = new(216, 226, 255), TextShadow = new(6, 8, 22), IsDark = true
            },
            new()
            {
                Name = "Eclipse",
                Outline = new(0, 0, 0), Edge = new(30, 42, 96), Frame = new(54, 76, 156), Shading = new(16, 22, 48), Panel = new(0, 0, 0), Highlight = new(10, 13, 28),
                Text = new(200, 212, 255), TextShadow = new(0, 0, 0), IsDark = true
            },
            new()
            {
                Name = "Forest",
                Outline = new(26, 40, 24), Edge = new(52, 80, 44), Frame = new(104, 144, 84), Shading = new(158, 188, 128), Panel = new(204, 224, 176), Highlight = new(228, 240, 210),
                Text = new(30, 46, 28), TextShadow = new(180, 202, 158)
            },
            new()
            {
                Name = "Pine",
                Outline = new(8, 18, 10), Edge = new(22, 48, 28), Frame = new(70, 130, 80), Shading = new(40, 76, 48), Panel = new(22, 44, 28), Highlight = new(34, 62, 40),
                Text = new(220, 244, 220), TextShadow = new(6, 14, 8), IsDark = true
            },
            new()
            {
                Name = "Sakura",
                Outline = new(90, 38, 58), Edge = new(148, 68, 96), Frame = new(220, 128, 158), Shading = new(238, 178, 196), Panel = new(251, 220, 228), Highlight = new(255, 238, 243),
                Text = new(82, 30, 52), TextShadow = new(236, 192, 206)
            },
            new()
            {
                Name = "Coral",
                Outline = new(96, 40, 36), Edge = new(170, 76, 66), Frame = new(238, 124, 104), Shading = new(248, 178, 156), Panel = new(255, 222, 208), Highlight = new(255, 240, 232),
                Text = new(96, 36, 30), TextShadow = new(246, 196, 180)
            },
            new()
            {
                Name = "Ocean",
                Outline = new(10, 40, 48), Edge = new(22, 84, 96), Frame = new(40, 150, 160), Shading = new(130, 206, 208), Panel = new(200, 236, 234), Highlight = new(228, 248, 246),
                Text = new(12, 52, 60), TextShadow = new(170, 220, 220)
            },
            new()
            {
                Name = "Aurora",
                Outline = new(4, 10, 14), Edge = new(16, 40, 48), Frame = new(40, 170, 150), Shading = new(30, 70, 80), Panel = new(14, 30, 38), Highlight = new(24, 46, 56),
                Text = new(210, 255, 240), TextShadow = new(2, 8, 10), IsDark = true
            },
            new()
            {
                Name = "Glacier",
                Outline = new(40, 60, 84), Edge = new(84, 116, 150), Frame = new(150, 190, 225), Shading = new(200, 225, 245), Panel = new(228, 242, 252), Highlight = new(244, 250, 255),
                Text = new(30, 50, 76), TextShadow = new(196, 216, 236)
            },
            new()
            {
                Name = "Lavender",
                Outline = new(60, 44, 84), Edge = new(104, 82, 140), Frame = new(168, 146, 212), Shading = new(206, 192, 236), Panel = new(232, 224, 248), Highlight = new(246, 242, 255),
                Text = new(58, 40, 82), TextShadow = new(212, 200, 236)
            },
            new()
            {
                Name = "Amethyst",
                Outline = new(12, 6, 20), Edge = new(40, 22, 62), Frame = new(120, 70, 170), Shading = new(70, 44, 104), Panel = new(44, 28, 66), Highlight = new(60, 40, 88),
                Text = new(236, 222, 255), TextShadow = new(12, 6, 20), IsDark = true
            },
            new()
            {
                Name = "Ember",
                Outline = new(8, 6, 6), Edge = new(40, 26, 20), Frame = new(200, 90, 30), Shading = new(74, 48, 38), Panel = new(40, 32, 30), Highlight = new(56, 44, 40),
                Text = new(255, 226, 200), TextShadow = new(12, 8, 6), IsDark = true
            },
            new()
            {
                Name = "Citrus",
                Outline = new(74, 70, 10), Edge = new(130, 130, 30), Frame = new(200, 210, 60), Shading = new(236, 236, 140), Panel = new(250, 248, 200), Highlight = new(255, 255, 228),
                Text = new(70, 66, 14), TextShadow = new(230, 228, 160)
            }
        };

        /// <summary>Recoloured copies of the game's textures, by scheme and original.</summary>
        private static readonly Dictionary<(string Scheme, Texture2D Source), Texture2D> Copies = new();

        /// <summary>Every copy made, so a copy is never recoloured again when schemes nest.</summary>
        private static readonly HashSet<Texture2D> OwnCopies = new();

        /// <summary>The game's text box texture, loaded once.</summary>
        private static Texture2D TextBoxSource;


        /*********
        ** Accessors
        *********/
        /// <summary>The names of the schemes, in the order they're offered.</summary>
        public static IReadOnlyList<string> Names { get; } = Palettes.Select(palette => palette.Name).ToArray();

        /// <summary>The chosen scheme's name.</summary>
        public static string Current { get; set; } = Vanilla;

        /// <summary>The chosen scheme, or <c>null</c> to leave the game's look alone.</summary>
        private static UiPalette Palette
        {
            get
            {
                UiPalette palette = Find(Current);
                return palette == null || palette.Name == Vanilla ? null : palette;
            }
        }

        /// <summary>Whether the scheme puts light text on dark panels.</summary>
        public static bool IsDark => Palette?.IsDark == true;

        /// <summary>The scheme's text colour, for things drawn outside <see cref="Apply"/>, like a text box's.</summary>
        public static Color TextColour => Palette?.Text ?? Game1.textColor;

        /// <summary>A colour for something wrong: a shortfall, an error.</summary>
        public static Color Bad => IsDark ? new Color(255, 110, 100) : Color.Firebrick;

        /// <summary>A colour for something fine: stocked, linked.</summary>
        public static Color Good => IsDark ? new Color(120, 210, 120) : new Color(40, 120, 40);


        /*********
        ** Public methods
        *********/
        /// <summary>Whether a name is one of the schemes.</summary>
        public static bool IsKnown(string name) => Find(name) != null;

        /// <summary>The scheme a number of places along the list from another, wrapping round.</summary>
        public static string Step(string name, int by)
        {
            int index = Math.Max(0, Array.FindIndex(Palettes, palette => string.Equals(palette.Name, name, StringComparison.OrdinalIgnoreCase)));
            int count = Palettes.Length;
            return Palettes[(((index + by) % count) + count) % count].Name;
        }

        /// <summary>Draws a small preview of a scheme: a window with a slot and two lines of text.</summary>
        /// <remarks>Drawn in flat colour, so it shows the scheme whichever one the window around it is in.</remarks>
        public static void DrawSwatch(SpriteBatch b, string name, Rectangle area, float alpha = 1f)
        {
            UiPalette palette = Find(name);
            if (palette == null || area.Width < 12 || area.Height < 12)
                return;

            void Fill(Rectangle box, Color colour) => b.Draw(Game1.staminaRect, box, null, colour * alpha, 0f, Vector2.Zero, SpriteEffects.None, 0.99f);

            int line = Math.Max(2, area.Height / 12);
            Fill(area, palette.Outline);
            Rectangle frame = Shrink(area, line);
            Fill(frame, palette.Frame);
            Rectangle panel = Shrink(frame, line + 1);
            Fill(panel, palette.Panel);

            // A slot, and two lines of text beside it.
            int slotSize = Math.Max(4, panel.Height - (line * 3));
            Rectangle slot = new(panel.X + line + 1, panel.Center.Y - (slotSize / 2), slotSize, slotSize);
            Fill(slot, palette.Edge);
            Fill(Shrink(slot, Math.Max(1, line / 2)), palette.Highlight);

            int textX = slot.Right + line + 1;
            int textWidth = panel.Right - line - textX;
            if (textWidth > 2)
            {
                Fill(new Rectangle(textX, slot.Y + 1, textWidth, line), palette.Text);
                Fill(new Rectangle(textX, slot.Bottom - line - 1, textWidth * 2 / 3, line), palette.Text * 0.7f);
            }
        }

        /// <summary>Redraws a text box in the current scheme, for a scheme chosen while it's showing.</summary>
        public static void Restyle(StardewValley.Menus.TextBox box)
        {
            if (box == null)
                return;

            TextBoxTextureField?.SetValue(box, TextBoxTexture());
            TextBoxColourField?.SetValue(box, TextColour);
        }

        /// <summary>Draws in the scheme until the returned scope is disposed.</summary>
        /// <remarks>Wrap a menu's whole draw in it. Scopes nest: a menu drawn inside another is drawn the same way.</remarks>
        public static IDisposable Apply()
        {
            UiPalette palette = Palette;
            if (palette == null)
                return Scope.None;

            Scope scope = new();
            try
            {
                Game1.menuTexture = Recoloured(Game1.menuTexture, MenuRegions, palette);
                Game1.mouseCursors = Recoloured(Game1.mouseCursors, CursorRegions, palette);
                Game1.textColor = palette.Text;
                Game1.textShadowColor = palette.TextShadow;
                Game1.textShadowDarkerColor = palette.TextShadow;
                Game1.unselectedOptionColor = palette.Text * 0.6f;
            }
            catch (Exception ex)
            {
                // Never worth breaking a menu over: draw it in the game's colours instead.
                scope.Dispose();
                Log.Warn($"Couldn't draw in the {palette.Name} colour scheme, so the game's own is used: {ex.Message}");
                Current = Vanilla;
                return Scope.None;
            }

            return scope;
        }

        /// <summary>The text box texture in the scheme, for building a <see cref="StardewValley.Menus.TextBox"/>.</summary>
        public static Texture2D TextBoxTexture()
        {
            TextBoxSource ??= Game1.content.Load<Texture2D>("LooseSprites\\textBox");

            UiPalette palette = Palette;
            if (palette == null)
                return TextBoxSource;

            try
            {
                return Recoloured(TextBoxSource, new[] { TextBoxSource.Bounds }, palette);
            }
            catch
            {
                return TextBoxSource;
            }
        }

        /// <summary>Draws a button, tinted to mark it out: green to confirm, gold when on.</summary>
        /// <remarks>
        /// The game tints a button by multiplying its colours, which on a light scheme gives a pale green or gold
        /// button with dark text on it. On a dark scheme the same tint only muddies a dark button, and the light
        /// text on it loses its contrast; there the button is drawn plain with the accent laid over its face
        /// instead, bright enough to stand out and still dark enough to read on. A grey tint dims the button
        /// either way, as for something that can't be pressed.
        /// </remarks>
        public static void DrawButton(SpriteBatch b, Rectangle bounds, Color tint, float scale = 2f)
        {
            bool accent = tint != Color.White && !(tint.R == tint.G && tint.G == tint.B);
            if (!IsDark || !accent)
            {
                StardewValley.Menus.IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, tint, scale, drawShadow: false);
                return;
            }

            StardewValley.Menus.IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 396, 15, 15), bounds.X, bounds.Y, bounds.Width, bounds.Height, Color.White, scale, drawShadow: false);
            int inset = (int)Math.Ceiling(3 * scale);
            b.Draw(Game1.staminaRect, new Rectangle(bounds.X + inset, bounds.Y + inset, bounds.Width - (inset * 2), bounds.Height - (inset * 2)), tint * (0.45f * (tint.A / 255f)));
        }

        /// <summary>A fixed colour made readable on the scheme's panels: lifted towards white on a dark one.</summary>
        public static Color Legible(Color colour)
        {
            return IsDark ? Color.Lerp(colour, Color.White, 0.35f) : colour;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>A scheme by name, or <c>null</c>.</summary>
        private static UiPalette Find(string name) => Palettes.FirstOrDefault(palette => string.Equals(palette.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>A box shrunk by a margin on every side.</summary>
        private static Rectangle Shrink(Rectangle box, int by) => new(box.X + by, box.Y + by, Math.Max(0, box.Width - (by * 2)), Math.Max(0, box.Height - (by * 2)));

        /// <summary>A text box's texture and text colour, which it doesn't expose.</summary>
        private static readonly System.Reflection.FieldInfo TextBoxTextureField = typeof(StardewValley.Menus.TextBox).GetField("_textBoxTexture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.FieldInfo TextBoxColourField = typeof(StardewValley.Menus.TextBox).GetField("_textColor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>A copy of a texture with some of it recoloured, made once per scheme.</summary>
        private static Texture2D Recoloured(Texture2D source, Rectangle[] regions, UiPalette palette)
        {
            if (source == null || OwnCopies.Contains(source))
                return source;

            if (Copies.TryGetValue((palette.Name, source), out Texture2D copy) && !copy.IsDisposed)
                return copy;

            Color[] pixels = new Color[source.Width * source.Height];
            source.GetData(pixels);

            foreach (Rectangle region in regions)
            {
                Rectangle area = Rectangle.Intersect(region, source.Bounds);
                for (int y = area.Top; y < area.Bottom; y++)
                {
                    for (int x = area.Left; x < area.Right; x++)
                    {
                        int i = (y * source.Width) + x;
                        pixels[i] = Map(pixels[i], palette);
                    }
                }
            }

            copy = new Texture2D(Game1.graphics.GraphicsDevice, source.Width, source.Height);
            copy.SetData(pixels);

            Copies[(palette.Name, source)] = copy;
            OwnCopies.Add(copy);
            return copy;
        }

        /// <summary>Recolours one pixel by where its brightness falls among the game's menu shades.</summary>
        /// <remarks>Texture data is premultiplied, so the colour is taken out of its alpha first and put back after.</remarks>
        private static Color Map(Color pixel, UiPalette palette)
        {
            if (pixel.A == 0)
                return pixel;

            float alpha = pixel.A / 255f;
            float r = pixel.R / alpha, g = pixel.G / alpha, b = pixel.B / alpha;
            float brightness = ((0.299f * r) + (0.587f * g) + (0.114f * b)) / 255f;

            Color[] shades = { palette.Outline, palette.Edge, palette.Frame, palette.Shading, palette.Panel, palette.Highlight };
            Color mapped;
            if (brightness <= Stops[0])
                mapped = shades[0];
            else if (brightness >= Stops[^1])
                mapped = shades[^1];
            else
            {
                int i = 0;
                while (brightness > Stops[i + 1])
                    i++;
                mapped = Color.Lerp(shades[i], shades[i + 1], (brightness - Stops[i]) / (Stops[i + 1] - Stops[i]));
            }

            return new Color((int)(mapped.R * alpha), (int)(mapped.G * alpha), (int)(mapped.B * alpha), pixel.A);
        }

        /// <summary>Puts back what <see cref="Apply"/> changed.</summary>
        private sealed class Scope : IDisposable
        {
            public static readonly IDisposable None = new NoScope();

            private readonly Texture2D MenuTexture = Game1.menuTexture;
            private readonly Texture2D MouseCursors = Game1.mouseCursors;
            private readonly Color TextColor = Game1.textColor;
            private readonly Color TextShadowColor = Game1.textShadowColor;
            private readonly Color TextShadowDarkerColor = Game1.textShadowDarkerColor;
            private readonly Color UnselectedOptionColor = Game1.unselectedOptionColor;
            private bool Disposed;

            public void Dispose()
            {
                if (this.Disposed)
                    return;
                this.Disposed = true;

                Game1.menuTexture = this.MenuTexture;
                Game1.mouseCursors = this.MouseCursors;
                Game1.textColor = this.TextColor;
                Game1.textShadowColor = this.TextShadowColor;
                Game1.textShadowDarkerColor = this.TextShadowDarkerColor;
                Game1.unselectedOptionColor = this.UnselectedOptionColor;
            }

            private sealed class NoScope : IDisposable
            {
                public void Dispose() { }
            }
        }
    }
}
