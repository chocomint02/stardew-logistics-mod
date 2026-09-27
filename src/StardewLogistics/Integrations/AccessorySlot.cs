using System;
using System.Collections.Generic;
using System.Linq;
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
    /// equipment block clear of the funds and date text -- or, where another mod has put something there, the first
    /// free place among the other equipment slots (see <see cref="GetSlotBounds"/>). It's drawn before the page, so a long farmer name --
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

        /// <summary>Where the slot goes on each page, worked out once per page.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InventoryPage, object> SlotByPage = new();

        /// <summary>The slot's bounds on a page.</summary>
        /// <remarks>
        /// Normally at the foot of the ring column, under the Boots, lined up with the page's own slots. Mods that
        /// rearrange the equipment -- Wear More Rings puts the clothes in that column, Boots included -- can leave
        /// something else there, so the first of a few places that nothing on the page occupies is used instead:
        /// the last free cell of the grid the equipment slots make, so it still sits among them; failing that, a
        /// column added to that grid beside its top row, next to the first rings. It always stays inside the page.
        /// </remarks>
        private static Rectangle GetSlotBounds(InventoryPage page)
        {
            if (SlotByPage.TryGetValue(page, out object cached))
                return (Rectangle)cached;

            Rectangle slot = ChooseSlot(page);
            SlotByPage.AddOrUpdate(page, slot);
            return slot;
        }

        /// <summary>Picks the first free place for the slot on a page.</summary>
        private static Rectangle ChooseSlot(InventoryPage page)
        {
            List<Rectangle> taken = new();
            void Take(ClickableComponent component)
            {
                if (component != null && component.bounds.Width > 0)
                    taken.Add(component.bounds);
            }

            foreach (ClickableComponent icon in page.equipmentIcons ?? new List<ClickableComponent>())
                Take(icon);
            foreach (ClickableComponent slot in page.inventory?.inventory ?? new List<ClickableComponent>())
                Take(slot);
            foreach (ClickableComponent component in page.allClickableComponents ?? new List<ClickableComponent>())
                Take(component);
            Take(page.organizeButton);
            Take(page.trashCan);

            List<(string Name, Rectangle Bounds)> candidates = new()
            {
                ("under the Boots", new Rectangle(page.xPositionOnScreen + 48, page.yPositionOnScreen + IClickableMenu.borderWidth + IClickableMenu.spaceToClearTopBorder + 4 + 448 - 12, 64, 64))
            };

            // Inside the page, on the grid the equipment slots make. First the last cell of it nothing fills -- with
            // Wear More Rings, the foot of the ring columns until trinkets take it -- then a column added beside the
            // top row, next to the first rings.
            List<ClickableComponent> equipment = (page.equipmentIcons ?? new List<ClickableComponent>()).Where(icon => icon?.bounds.Width > 0).ToList();
            if (equipment.Count > 0)
            {
                Rectangle panel = new(page.xPositionOnScreen, page.yPositionOnScreen, page.width, page.height);
                List<int> columns = equipment.Select(icon => icon.bounds.X).Distinct().OrderBy(x => x).ToList();
                List<int> rows = equipment.Select(icon => icon.bounds.Y).Distinct().OrderBy(y => y).ToList();
                Rectangle? lastFree = null;
                foreach (int y in rows)
                {
                    foreach (int x in columns)
                    {
                        Rectangle cell = new(x, y, 64, 64);
                        if (panel.Contains(cell) && !taken.Any(other => other.Intersects(cell)))
                            lastFree = cell;
                    }
                }
                if (lastFree is Rectangle free)
                    candidates.Add(("at the end of the equipment slots", free));

                int step = columns.Count > 1 ? columns[^1] - columns[^2] : 64;
                candidates.Add(("beside the first row of rings", new Rectangle(columns[^1] + Math.Max(64, step), rows[0], 64, 64)));
            }

            Rectangle screen = new(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height);
            Rectangle inside = new(page.xPositionOnScreen, page.yPositionOnScreen, page.width, page.height);
            foreach ((string name, Rectangle bounds) in candidates)
            {
                if (!screen.Contains(bounds) || !inside.Contains(bounds) || taken.Any(other => other.Intersects(bounds)))
                    continue;

                if (name != candidates[0].Name)
                    Log.Trace($"The Wireless Terminal slot's usual place is taken (another mod rearranging the equipment?); it's {name} instead.");
                return bounds;
            }

            // Nowhere is free: the usual place, drawn over whatever's there.
            Log.Trace("No free place was found for the Wireless Terminal slot; it's in its usual place.");
            return candidates[0].Bounds;
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
                        Menus.TooltipFx.Around(b, () => IClickableMenu.drawToolTip(b, text, terminal.DisplayName, terminal));
                    }
                    else
                        Menus.TooltipFx.Around(b, () => IClickableMenu.drawHoverText(b, Game1.parseText(Translations.Get("accessory.empty", new { key = HotkeyName() }), Game1.smallFont, 400), Game1.smallFont));
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
