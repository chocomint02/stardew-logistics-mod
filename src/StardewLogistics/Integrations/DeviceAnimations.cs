using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Framework;
using StardewLogistics.Menus;
using StardewValley;
using StardewValley.ItemTypeDefinitions;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace StardewLogistics.Integrations
{
    /// <summary>Animates the mod's devices where they stand, and the data flowing along its cables.</summary>
    /// <remarks>
    /// Each device has two rows of frames in the craftables sheet (see <c>tools/make_sprites.py</c>): a boot
    /// sequence it plays once when placed, and a loop it runs from then on. Rather than draw the device itself --
    /// and have to match how the game places, shakes, tints and layers a big craftable -- the game draws it as
    /// usual, and while it does, the sprite it looks up is swapped for the current frame. Nothing else changes.
    ///
    /// Cables get a pulse drawn over them: bright packets moving along the core, from a sheet laid out like the
    /// floor's own. Both follow the animation speed setting, and stand still with it at zero.
    /// </remarks>
    internal static class DeviceAnimations
    {
        /*********
        ** Fields
        *********/
        /// <summary>Sprites to a row in the craftables sheet, and frames in each animation.</summary>
        private const int Columns = 8;
        private const int Frames = 8;

        /// <summary>How long each cable pulse frame shows, at normal speed.</summary>
        private const double PulseFrameMs = 140;

        /// <summary>Each device's place in the sheet's animation rows, and how long its boot and running frames show.</summary>
        /// <remarks>The order is the one <c>tools/make_sprites.py</c> lays the rows out in.</remarks>
        private static readonly Dictionary<string, (int Row, double BootMs, double RunMs)> Devices = new()
        {
            [ModIds.AutoHarvester] = (0, 130, 240),
            [ModIds.Terminal] = (1, 110, 170),
            [ModIds.CraftingTerminal] = (2, 110, 190),
            [ModIds.WirelessTransmitter] = (3, 110, 130),
            [ModIds.WirelessReceiver] = (4, 110, 140)
        };

        /// <summary>When each device was placed, by location and tile, while its boot sequence plays.</summary>
        private static readonly Dictionary<(string Location, Vector2 Tile), double> PlacedAt = new();

        /// <summary>The device being drawn right now and the frame it should show, while the game draws it.</summary>
        [ThreadStatic]
        private static string DrawingItem;
        [ThreadStatic]
        private static int DrawingIndex;

        /// <summary>The cable pulse sheet.</summary>
        private static Texture2D PulseTexture;


        /*********
        ** Public methods
        *********/
        /// <summary>Applies the patches.</summary>
        public static void Apply(Harmony harmony)
        {
            harmony.Patch(
                AccessTools.Method(typeof(SObject), nameof(SObject.draw), new[] { typeof(SpriteBatch), typeof(int), typeof(int), typeof(float) }),
                prefix: new HarmonyMethod(typeof(DeviceAnimations), nameof(Before_Draw)),
                finalizer: new HarmonyMethod(typeof(DeviceAnimations), nameof(After_Draw))
            );
            harmony.Patch(
                AccessTools.Method(typeof(ParsedItemData), nameof(ParsedItemData.GetSourceRect)),
                prefix: new HarmonyMethod(typeof(DeviceAnimations), nameof(Before_GetSourceRect))
            );
            harmony.Patch(
                AccessTools.Method(typeof(Flooring), nameof(Flooring.draw), new[] { typeof(SpriteBatch) }),
                postfix: new HarmonyMethod(typeof(DeviceAnimations), nameof(After_FlooringDraw))
            );
        }

        /// <summary>Whether an object is one of the animated devices.</summary>
        public static bool IsAnimated(SObject obj) => obj is { bigCraftable.Value: true } && Devices.ContainsKey(obj.ItemId);

        /// <summary>Starts a device's boot sequence: it's just been placed.</summary>
        public static void Placed(GameLocation location, Vector2 tile)
        {
            if (location != null)
                PlacedAt[(location.NameOrUniqueName, tile)] = Now();
        }

        /// <summary>Forgets every boot sequence, for when the player leaves the save.</summary>
        public static void Reset()
        {
            PlacedAt.Clear();
            PulseTexture = null;
        }


        /*********
        ** Private methods: devices
        *********/
        /// <summary>Notes the frame a device should show, for the sprite lookup the draw is about to make.</summary>
        private static void Before_Draw(SObject __instance, int x, int y)
        {
            DrawingItem = null;
            if (!IsAnimated(__instance) || !UiAnimation.Enabled)
                return;

            try
            {
                DrawingIndex = GetFrame(__instance, new Vector2(x, y));
                DrawingItem = __instance.QualifiedItemId;
            }
            catch
            {
                DrawingItem = null;
            }
        }

        /// <summary>Stops swapping sprites once the device is drawn, however the draw ended.</summary>
        private static Exception After_Draw(Exception __exception)
        {
            DrawingItem = null;
            return __exception;
        }

        /// <summary>Points the sprite lookup at the current frame while a device is drawn.</summary>
        private static void Before_GetSourceRect(ParsedItemData __instance, ref int offset, ref int? spriteIndex)
        {
            if (DrawingItem == null || __instance.QualifiedItemId != DrawingItem)
                return;

            spriteIndex = DrawingIndex;
            offset = 0;
        }

        /// <summary>The sheet index of the frame a device shows now: booting if it was just placed, otherwise running.</summary>
        private static int GetFrame(SObject device, Vector2 tile)
        {
            (int row, double bootMs, double runMs) = Devices[device.ItemId];
            double now = Now();
            float speed = UiAnimation.SpeedFactor;

            (string, Vector2) key = (device.Location?.NameOrUniqueName, tile);
            if (key.Item1 != null && PlacedAt.TryGetValue(key, out double placed))
            {
                int boot = (int)((now - placed) * speed / bootMs);
                if (boot < Frames)
                    return ((1 + (row * 2)) * Columns) + Math.Max(0, boot);

                PlacedAt.Remove(key);
            }

            // Running. Devices side by side start at different points, so a row of terminals doesn't flicker in step.
            int phase = (((int)tile.X * 3) + ((int)tile.Y * 5)) % Frames;
            int frame = ((int)(now * speed / runMs) + phase) % Frames;
            return ((2 + (row * 2)) * Columns) + frame;
        }


        /*********
        ** Private methods: cables
        *********/
        /// <summary>Draws the data pulse over a cable after the game draws the cable itself.</summary>
        /// <param name="___neighborMask">Which sides the cable joins others on; private to the game, so Harmony passes it in.</param>
        private static void After_FlooringDraw(Flooring __instance, SpriteBatch spriteBatch, byte ___neighborMask)
        {
            if (__instance.whichFloor.Value != ModIds.CableFloorId || !UiAnimation.Enabled)
                return;

            try
            {
                PulseTexture ??= Game1.content.Load<Texture2D>(ModIds.CablePulseTexture);
                if (!Flooring.drawGuide.TryGetValue(___neighborMask, out int index))
                    return;

                int frame = (int)(Now() * UiAnimation.SpeedFactor / PulseFrameMs) % Frames;
                Rectangle source = new((index % 16) * 16, ((index / 16) * 16) + (frame * 64), 16, 16);
                Vector2 position = Game1.GlobalToLocal(Game1.viewport, __instance.Tile * 64f);

                // Just above the cable, and below anything standing on it.
                spriteBatch.Draw(PulseTexture, position, source, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1.5E-09f);
            }
            catch
            {
                // A pulse is decoration: never worth breaking the world's drawing over.
            }
        }

        /// <summary>The clock animations run on: real time, so they keep moving while the game is paused.</summary>
        private static double Now() => Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;
    }
}
