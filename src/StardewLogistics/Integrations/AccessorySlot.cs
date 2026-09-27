using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Menus;

namespace StardewLogistics.Integrations
{
    /// <summary>A dedicated equipment slot for the Wireless Terminal, on the player's inventory page.</summary>
    /// <remarks>
    /// The game has no spare equipment slot, so this one is drawn and clicked through patches on
    /// <see cref="InventoryPage"/>. It sits at the foot of the ring column, under the Boots: the only spot in the
    /// equipment block clear of the funds and date text. It's drawn before the page, so a long farmer name --
    /// centred under the portrait, on the same row -- stays readable over it. What's in it is kept in a global inventory
    /// named for the player: global inventories save with the farm and sync to every player, so a farmhand's
    /// terminal survives the session and the host can see it.
    /// </remarks>
    internal static class AccessorySlot
    {
        /*********
        ** Fields
        *********/
        private static ITranslationHelper Translations;
        private static Func<string> HotkeyName;

        /// <summary>A sample terminal, drawn faintly in the empty slot.</summary>
        private static Item Placeholder;


        /*********
        ** Public methods
        *********/
        /// <summary>Applies the patches.</summary>
        public static void Apply(Harmony harmony, ITranslationHelper translations, Func<string> hotkeyName)
        {
            Translations = translations;
            HotkeyName = hotkeyName;

            harmony.Patch(
                AccessTools.Method(typeof(InventoryPage), nameof(InventoryPage.draw), new[] { typeof(SpriteBatch) }),
                prefix: new HarmonyMethod(typeof(AccessorySlot), nameof(Before_Draw)),
                postfix: new HarmonyMethod(typeof(AccessorySlot), nameof(After_Draw))
            );
            harmony.Patch(AccessTools.Method(typeof(InventoryPage), nameof(InventoryPage.receiveLeftClick)), prefix: new HarmonyMethod(typeof(AccessorySlot), nameof(Before_ReceiveLeftClick)));
        }

        /// <summary>The Wireless Terminal a player has equipped, if any.</summary>
        public static Item GetTerminal(Farmer who)
        {
            IInventory slot = GetInventory(who);
            return slot != null && slot.Count > 0 ? slot[0] : null;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>The global inventory holding a player's slot.</summary>
        private static IInventory GetInventory(Farmer who)
        {
            return who == null ? null : Game1.player.team.GetOrCreateGlobalInventory(ModIds.AccessoryInventoryPrefix + who.UniqueMultiplayerID);
        }

        /// <summary>Puts an item in a player's slot, or empties it.</summary>
        private static void SetTerminal(Farmer who, Item item)
        {
            IInventory slot = GetInventory(who);
            if (slot == null)
                return;

            if (slot.Count == 0)
                slot.Add(item);
            else
                slot[0] = item;
        }

        /// <summary>The slot's bounds: the ring column, one row below the Boots.</summary>
        /// <remarks>Worked out the way the page places its own equipment slots, so it lines up with them exactly.</remarks>
        private static Rectangle GetSlotBounds(InventoryPage page)
        {
            int x = page.xPositionOnScreen + 48;
            int y = page.yPositionOnScreen + IClickableMenu.borderWidth + IClickableMenu.spaceToClearTopBorder + 4 + 448 - 12;
            return new Rectangle(x, y, 64, 64);
        }

        /// <summary>Whether an item is a Wireless Terminal.</summary>
        private static bool IsTerminal(Item item) => item?.QualifiedItemId == "(O)" + ModIds.WirelessTerminal;

        /// <summary>Draws the slot and what's in it, before the page, so the page's own text lands on top.</summary>
        private static void Before_Draw(InventoryPage __instance, SpriteBatch b)
        {
            try
            {
                // An equipment slot like the others above it.
                Rectangle slot = GetSlotBounds(__instance);
                b.Draw(Game1.menuTexture, slot, Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10), Color.White);

                Item terminal = GetTerminal(Game1.player);
                if (terminal != null)
                    terminal.drawInMenu(b, new Vector2(slot.X, slot.Y), 1f, 1f, 0.9f, StackDrawType.Hide, Color.White, drawShadow: true);
                else
                {
                    Placeholder ??= ItemRegistry.Create("(O)" + ModIds.WirelessTerminal, allowNull: true);
                    Placeholder?.drawInMenu(b, new Vector2(slot.X, slot.Y), 1f, 0.2f, 0.9f, StackDrawType.Hide, Color.Black, drawShadow: false);
                }
            }
            catch (Exception ex)
            {
                Log.Trace($"Couldn't draw the Wireless Terminal slot: {ex.Message}");
            }
        }

        /// <summary>Draws the slot's tooltip, after the page, so it's above everything.</summary>
        private static void After_Draw(InventoryPage __instance, SpriteBatch b)
        {
            try
            {
                Rectangle slot = GetSlotBounds(__instance);
                Item terminal = GetTerminal(Game1.player);

                // A tooltip, but not over something the player is already carrying.
                if (slot.Contains(Game1.getMouseX(), Game1.getMouseY()) && Game1.player.CursorSlotItem == null)
                {
                    if (terminal != null)
                    {
                        string text = Translations.Get("accessory.equipped", new { key = HotkeyName(), channel = Network.NetworkNode.GetChannel(terminal as StardewValley.Object) });
                        IClickableMenu.drawToolTip(b, text, terminal.DisplayName, terminal);
                    }
                    else
                        IClickableMenu.drawHoverText(b, Game1.parseText(Translations.Get("accessory.empty", new { key = HotkeyName() }), Game1.smallFont, 400), Game1.smallFont);
                }
            }
            catch (Exception ex)
            {
                Log.Trace($"Couldn't draw the Wireless Terminal slot: {ex.Message}");
            }
        }

        /// <summary>Takes a click on the slot: put a held Wireless Terminal in, or take the equipped one out.</summary>
        /// <returns>Whether to run the page's own click handling; <c>false</c> when the slot took the click.</returns>
        private static bool Before_ReceiveLeftClick(InventoryPage __instance, int x, int y)
        {
            try
            {
                if (!GetSlotBounds(__instance).Contains(x, y))
                    return true;

                Item held = Game1.player.CursorSlotItem;
                Item equipped = GetTerminal(Game1.player);

                if (held != null && !IsTerminal(held))
                {
                    Game1.playSound("cancel");
                    Game1.showRedMessage(Translations.Get("accessory.wrong-item"));
                    return false;
                }

                // Swap whatever's held with whatever's there. A stack of terminals leaves the rest in hand.
                if (held != null && held.Stack > 1)
                {
                    if (equipped != null)
                    {
                        Game1.playSound("cancel");
                        return false;
                    }

                    Item one = held.getOne();
                    held.Stack--;
                    SetTerminal(Game1.player, one);
                }
                else
                {
                    SetTerminal(Game1.player, held);
                    Game1.player.CursorSlotItem = equipped;
                }

                Game1.playSound(held != null ? "crit" : "dwop");
                return false;
            }
            catch (Exception ex)
            {
                Log.Trace($"Couldn't use the Wireless Terminal slot: {ex.Message}");
                return true;
            }
        }
    }
}
