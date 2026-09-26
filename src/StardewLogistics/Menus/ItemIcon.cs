using System;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Framework;
using StardewValley;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewLogistics.Menus
{
    /// <summary>Draws an item's icon to fit a box, cleanly at any size.</summary>
    /// <remarks>
    /// The game's <c>drawInMenu</c> is built around a 64px slot and doesn't shrink everything it draws. Even Better
    /// Artisan Good Icons replaces it for artisan goods and always adds a shadow at a fixed offset, which below
    /// full size leaves the bottle floating above its shadow. So where that mod has a sprite for an item, the
    /// sprite is drawn here directly, to size and without a shadow; everything else goes through the game as
    /// usual. The mod is found by reflection, so none of this depends on it being installed.
    /// </remarks>
    internal static class ItemIcon
    {
        /*********
        ** Fields
        *********/
        /// <summary>Even Better Artisan Good Icons' sprite lookup, or <c>null</c> if it isn't installed.</summary>
        private static MethodInfo ArtisanDrawInfo;

        /// <summary>Whether the lookup above has been attempted.</summary>
        private static bool LookedForArtisanIcons;


        /*********
        ** Public methods
        *********/
        /// <summary>Draws an item's icon filling a square box.</summary>
        /// <param name="b">The sprite batch.</param>
        /// <param name="item">The item.</param>
        /// <param name="area">The box to fill.</param>
        /// <param name="alpha">The opacity.</param>
        /// <param name="showQuality">Whether to draw the item's quality star in the box's corner.</param>
        public static void Draw(SpriteBatch b, Item item, Rectangle area, float alpha = 1f, bool showQuality = true)
        {
            if (item == null)
                return;

            if (!TryDrawArtisanSprite(b, item, area, alpha) && !TryDrawColoredSprite(b, item, area, alpha))
            {
                // The game's drawing centres the sprite on position + (32, 32) of a 64px slot. A big craftable is
                // twice as tall, so it takes half the scale to fit the same box.
                bool tall = item is SObject { bigCraftable.Value: true };
                float scale = (area.Width / 64f) * (tall ? 0.5f : 1f);
                item.drawInMenu(b, new Vector2(area.Center.X - 32, area.Center.Y - 32), scale, alpha, 0.9f, StackDrawType.Hide, Color.White, drawShadow: false);
            }

            if (showQuality)
                DrawQualityStar(b, item.Quality, area, alpha);
        }

        /// <summary>Draws a quality star in the bottom-left of a box, the way the game marks quality on items.</summary>
        public static void DrawQualityStar(SpriteBatch b, int quality, Rectangle area, float alpha = 1f)
        {
            Rectangle? star = quality switch
            {
                SObject.medQuality => new Rectangle(338, 400, 8, 8),
                SObject.highQuality => new Rectangle(346, 400, 8, 8),
                SObject.bestQuality => new Rectangle(346, 392, 8, 8),
                _ => null
            };
            if (star == null)
                return;

            int size = Math.Max(12, area.Width / 2);
            b.Draw(Game1.mouseCursors, new Rectangle(area.X - 2, area.Bottom - size + 2, size, size), star.Value, Color.White * alpha, 0f, Vector2.Zero, SpriteEffects.None, 1f);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Draws Even Better Artisan Good Icons' sprite for an item, if it has one.</summary>
        private static bool TryDrawArtisanSprite(SpriteBatch b, Item item, Rectangle area, float alpha)
        {
            if (item is not SObject obj)
                return false;

            MethodInfo lookup = GetArtisanLookup();
            if (lookup == null)
                return false;

            object[] args = { obj, null, null, null };
            try
            {
                if (lookup.Invoke(null, args) is not true || args[1] is not Texture2D texture || args[2] is not Rectangle main || main.IsEmpty)
                    return false;

                b.Draw(texture, area, main, Color.White * alpha, 0f, Vector2.Zero, SpriteEffects.None, 0.9f);

                // The small ingredient badge the mod can add, in the top-left, as it draws it.
                if (args[3] is Rectangle badge && !badge.IsEmpty)
                {
                    int size = area.Width / 2;
                    b.Draw(texture, new Rectangle(area.X, area.Y, size, size), badge, Color.White * alpha, 0f, Vector2.Zero, SpriteEffects.None, 0.91f);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Draws a tinted item -- a flavoured wine or jelly -- from its sprite, fitted to the box.</summary>
        /// <remarks>
        /// The game's <c>ColoredObject.drawInMenu</c> positions itself differently from a plain item's and only
        /// centres correctly at full size, so a half-size wine drew up and to the left of its slot. Drawing it
        /// here is the base sprite, then its colour layer tinted -- the same two layers the game draws. Smoked fish
        /// has drawing of its own and is left to the game.
        /// </remarks>
        private static bool TryDrawColoredSprite(SpriteBatch b, Item item, Rectangle area, float alpha)
        {
            if (item is not ColoredObject coloured || coloured.bigCraftable.Value || coloured.QualifiedItemId == "(O)SmokedFish")
                return false;

            try
            {
                ParsedItemData data = ItemRegistry.GetDataOrErrorItem(coloured.QualifiedItemId);
                Texture2D texture = data.GetTexture();
                Rectangle baseSprite = data.GetSourceRect();

                if (coloured.ColorSameIndexAsParentSheetIndex)
                {
                    b.Draw(texture, area, baseSprite, coloured.color.Value * alpha, 0f, Vector2.Zero, SpriteEffects.None, 0.9f);
                }
                else
                {
                    b.Draw(texture, area, baseSprite, Color.White * alpha, 0f, Vector2.Zero, SpriteEffects.None, 0.9f);
                    b.Draw(texture, area, data.GetSourceRect(1), coloured.color.Value * alpha, 0f, Vector2.Zero, SpriteEffects.None, 0.91f);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Finds Even Better Artisan Good Icons' sprite lookup, once.</summary>
        private static MethodInfo GetArtisanLookup()
        {
            if (LookedForArtisanIcons)
                return ArtisanDrawInfo;

            LookedForArtisanIcons = true;
            try
            {
                Type manager = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(assembly => assembly.GetName().Name == "EvenBetterArtisanGoodIcons")
                    .Select(assembly => assembly.GetType("BetterArtisanGoodIcons.ArtisanGoodsManager"))
                    .FirstOrDefault(type => type != null);

                ArtisanDrawInfo = manager?.GetMethod("GetDrawInfo", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (ArtisanDrawInfo != null)
                    Log.Trace("Drawing artisan goods with Even Better Artisan Good Icons' sprites at small sizes.");
            }
            catch
            {
                ArtisanDrawInfo = null;
            }

            return ArtisanDrawInfo;
        }
    }
}
