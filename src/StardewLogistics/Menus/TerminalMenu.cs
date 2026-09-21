using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewLogistics.Menus
{
    /// <summary>How the item list is ordered.</summary>
    internal enum SortMode
    {
        Name,
        Count,
        Category
    }

    /// <summary>Which page of the terminal is showing.</summary>
    internal enum TerminalTab
    {
        Items,
        Storage,
        Network
    }

    /// <summary>The storage terminal: one searchable, sortable view of everything on the network.</summary>
    /// <remarks>
    /// The grid is virtualised — only the visible rows are drawn, and the underlying list is rebuilt from the
    /// network rather than stored — so the menu stays responsive whether the network holds fifty items or fifty
    /// thousand. Counts are drawn with the mod's own abbreviations because an inventory slot can't fit "14286".
    /// </remarks>
    internal partial class TerminalMenu : IClickableMenu
    {
        /*********
        ** Fields
        *********/
        private const int SlotSize = 64;
        private const int Columns = 12;
        private const int HeaderHeight = 112;
        private const int InventoryHeight = 3 * SlotSize + 28;

        private readonly ITranslationHelper Translations;
        private readonly NetworkManager Networks;
        private readonly GameLocation TerminalLocation;
        private readonly Vector2 TerminalTile;
        private readonly bool CanCraft;

        private StorageNetwork Network;
        private List<NetworkItemStack> AllStock = new();
        private List<NetworkItemStack> VisibleStock = new();

        private readonly InventoryMenu PlayerInventory;
        private readonly TextBox SearchBox;
        private ClickableComponent SearchBoxBounds;
        private ClickableTextureComponent SortButton;
        private ClickableTextureComponent DepositAllButton;
        private readonly List<ClickableComponent> TabButtons = new();

        private int Rows;
        private int ScrollOffset;
        private SortMode Sort = SortMode.Name;
        private TerminalTab Tab = TerminalTab.Items;
        private string HoverText = "";
        private string LastSearch = "";
        private Item HoverItem;
        private int RefreshCounter;


        /*********
        ** Public methods
        *********/
        /// <summary>Constructs the terminal for a network.</summary>
        /// <param name="networks">The network cache, used to re-resolve the network as the world changes.</param>
        /// <param name="translations">The mod's translation helper.</param>
        /// <param name="location">The location holding the terminal.</param>
        /// <param name="tile">The tile the terminal occupies.</param>
        /// <param name="canCraft">Whether this terminal offers the crafting page.</param>
        public TerminalMenu(NetworkManager networks, ITranslationHelper translations, GameLocation location, Vector2 tile, bool canCraft)
        {
            this.Networks = networks;
            this.Translations = translations;
            this.TerminalLocation = location;
            this.TerminalTile = tile;
            this.CanCraft = canCraft;

            // Shrink the grid on small windows rather than overflowing off-screen.
            int available = Game1.uiViewport.Height - (HeaderHeight + InventoryHeight + 160);
            this.Rows = Math.Clamp(available / SlotSize, 3, 6);

            this.width = (Columns * SlotSize) + 96;
            this.height = HeaderHeight + (this.Rows * SlotSize) + 32 + InventoryHeight;
            this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
            this.yPositionOnScreen = (Game1.uiViewport.Height - this.height) / 2;

            this.PlayerInventory = new InventoryMenu(
                this.xPositionOnScreen + 32,
                this.yPositionOnScreen + this.height - InventoryHeight + 24,
                playerInventory: true
            );

            this.SearchBox = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
            {
                X = this.xPositionOnScreen + this.width - 300,
                Y = this.yPositionOnScreen + 64,
                Width = 256,
                Height = 40
            };

            this.SetUpComponents();
            this.RefreshStock();
            this.initializeUpperRightCloseButton();
        }

        /// <inheritdoc />
        public override void update(GameTime time)
        {
            base.update(time);

            // TextBox has no "text changed" event, so poll it: re-filtering is a list pass over data we already hold.
            if (this.SearchBox.Text != this.LastSearch)
            {
                this.LastSearch = this.SearchBox.Text;
                this.ScrollOffset = 0;
                this.ApplyFilterAndSort();
            }

            // The network is live: chests can be filled by buses, farmhands or other mods while the menu is open.
            if (++this.RefreshCounter >= 30)
            {
                this.RefreshCounter = 0;
                this.RefreshStock();
            }
        }

        /// <inheritdoc />
        protected override void cleanupBeforeExit()
        {
            this.ReleaseKeyboard();
            base.cleanupBeforeExit();
        }

        /// <summary>Hands the keyboard back to the game.</summary>
        /// <remarks>Leaving the search box subscribed would swallow the player's key presses after the menu goes away.</remarks>
        private void ReleaseKeyboard()
        {
            if (Game1.keyboardDispatcher.Subscriber == this.SearchBox)
                Game1.keyboardDispatcher.Subscriber = null;
        }

        /// <inheritdoc />
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (this.upperRightCloseButton?.containsPoint(x, y) == true)
            {
                this.exitThisMenu();
                return;
            }

            // Tabs
            foreach (ClickableComponent tab in this.TabButtons)
            {
                if (!tab.containsPoint(x, y))
                    continue;

                if (tab.name == "craft")
                {
                    this.OpenCraftingPage();
                    return;
                }

                this.Tab = Enum.Parse<TerminalTab>(tab.name);
                this.ScrollOffset = 0;
                Game1.playSound("smallSelect");
                return;
            }

            // Search box
            bool clickedSearch = this.SearchBoxBounds.containsPoint(x, y);
            this.SearchBox.Selected = clickedSearch;
            if (clickedSearch)
                return;

            if (this.Tab != TerminalTab.Items)
            {
                this.ReceiveClickOnTab(x, y, rightClick: false);
                return;
            }

            if (this.SortButton.containsPoint(x, y))
            {
                this.Sort = (SortMode)(((int)this.Sort + 1) % 3);
                this.ApplyFilterAndSort();
                Game1.playSound("shwip");
                return;
            }

            if (this.DepositAllButton.containsPoint(x, y))
            {
                this.DepositAll();
                return;
            }

            // Network grid
            NetworkItemStack clicked = this.GetStackAt(x, y);
            if (clicked != null)
            {
                bool bulk = IsShiftDown();
                this.Withdraw(clicked, bulk ? int.MaxValue : clicked.Sample.maximumStackSize());
                return;
            }

            // Player inventory: clicking an item sends it to the network.
            int slot = this.PlayerInventory.getInventoryPositionOfClick(x, y);
            if (slot >= 0 && slot < Game1.player.Items.Count)
                this.DepositSlot(slot, allOfType: IsShiftDown());
        }

        /// <inheritdoc />
        public override void receiveRightClick(int x, int y, bool playSound = true)
        {
            if (this.Tab != TerminalTab.Items)
            {
                this.ReceiveClickOnTab(x, y, rightClick: true);
                return;
            }

            NetworkItemStack clicked = this.GetStackAt(x, y);
            if (clicked != null)
            {
                this.Withdraw(clicked, 1);
                return;
            }

            int slot = this.PlayerInventory.getInventoryPositionOfClick(x, y);
            if (slot >= 0 && slot < Game1.player.Items.Count)
                this.DepositSlot(slot, allOfType: false, singleItem: true);
        }

        /// <inheritdoc />
        public override void receiveScrollWheelAction(int direction)
        {
            int rows = this.Tab == TerminalTab.Items ? this.Rows : 1;
            int step = direction > 0 ? -1 : 1;
            this.ScrollOffset = Math.Max(0, Math.Min(this.ScrollOffset + step, Math.Max(0, this.GetMaxScroll(rows))));
            Game1.playSound("shiny4");
        }

        /// <inheritdoc />
        public override void receiveKeyPress(Keys key)
        {
            // While the player is typing a search, keys belong to the text box rather than the menu.
            if (this.SearchBox.Selected)
            {
                if (key == Keys.Escape)
                {
                    this.SearchBox.Selected = false;
                    this.SearchBox.Text = "";
                    this.ApplyFilterAndSort();
                }
                return;
            }

            if (Game1.options.doesInputListContain(Game1.options.menuButton, key) || key == Keys.Escape)
            {
                this.exitThisMenu();
                return;
            }

            base.receiveKeyPress(key);
        }

        /// <inheritdoc />
        public override void performHoverAction(int x, int y)
        {
            this.HoverText = "";
            this.HoverItem = null;

            this.SortButton?.tryHover(x, y);
            this.DepositAllButton?.tryHover(x, y);

            if (this.Tab != TerminalTab.Items)
            {
                this.PerformHoverOnTab(x, y);
                return;
            }

            if (this.SortButton.containsPoint(x, y))
                this.HoverText = this.Translations.Get("ui.sort-by", new { mode = this.Translations.Get("sort." + this.Sort.ToString().ToLowerInvariant()) });
            else if (this.DepositAllButton.containsPoint(x, y))
                this.HoverText = this.Translations.Get("ui.deposit-all");

            NetworkItemStack hovered = this.GetStackAt(x, y);
            if (hovered != null)
            {
                this.HoverItem = hovered.Sample;
                this.HoverText = this.Translations.Get("ui.stored-count", new { count = NumberFormat.Full(hovered.Count) });
                return;
            }

            Item inventoryItem = this.PlayerInventory.hover(x, y, null);
            if (inventoryItem != null)
            {
                this.HoverItem = inventoryItem;
                this.HoverText = this.Translations.Get("ui.click-to-store");
            }
        }

        /// <inheritdoc />
        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            this.ReleaseKeyboard();
            Game1.activeClickableMenu = new TerminalMenu(this.Networks, this.Translations, this.TerminalLocation, this.TerminalTile, this.CanCraft);
        }

        /// <inheritdoc />
        public override void draw(SpriteBatch b)
        {
            // Dim the world behind the menu.
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.5f);

            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White, 1f, drawShadow: true);

            this.DrawHeader(b);

            switch (this.Tab)
            {
                case TerminalTab.Items:
                    this.DrawItemsTab(b);
                    break;
                case TerminalTab.Storage:
                    this.DrawStorageTab(b);
                    break;
                case TerminalTab.Network:
                    this.DrawNetworkTab(b);
                    break;
            }

            this.PlayerInventory.draw(b);
            this.upperRightCloseButton?.draw(b);

            if (this.HoverItem != null)
                drawToolTip(b, this.HoverText, this.HoverItem.DisplayName, this.HoverItem);
            else if (!string.IsNullOrEmpty(this.HoverText))
                drawHoverText(b, this.HoverText, Game1.smallFont);

            this.drawMouse(b);
        }


        /*********
        ** Private methods: layout
        *********/
        /// <summary>Builds the clickable components whose positions never change.</summary>
        private void SetUpComponents()
        {
            this.SearchBoxBounds = new ClickableComponent(new Rectangle(this.SearchBox.X, this.SearchBox.Y, this.SearchBox.Width, this.SearchBox.Height), "search");

            int tabX = this.xPositionOnScreen + 32;
            int tabY = this.yPositionOnScreen + 16;
            foreach (string name in this.GetTabNames())
            {
                int tabWidth = (int)Game1.smallFont.MeasureString(this.GetTabLabel(name)).X + 32;
                this.TabButtons.Add(new ClickableComponent(new Rectangle(tabX, tabY, tabWidth, 44), name));
                tabX += tabWidth + 8;
            }

            int buttonY = this.yPositionOnScreen + 64;
            this.SortButton = new ClickableTextureComponent(
                new Rectangle(this.xPositionOnScreen + 32, buttonY, 44, 44),
                Game1.mouseCursors,
                new Rectangle(162, 440, 16, 16),
                2.75f
            );
            this.DepositAllButton = new ClickableTextureComponent(
                new Rectangle(this.xPositionOnScreen + 88, buttonY, 44, 44),
                Game1.mouseCursors,
                new Rectangle(526, 218, 16, 16),
                2.75f
            );
        }

        /// <summary>The tabs this terminal shows, which depends on whether it can craft.</summary>
        private IEnumerable<string> GetTabNames()
        {
            yield return nameof(TerminalTab.Items);
            if (this.CanCraft)
                yield return "craft";
            yield return nameof(TerminalTab.Storage);
            yield return nameof(TerminalTab.Network);
        }

        /// <summary>The translated label for a tab.</summary>
        private string GetTabLabel(string name) => this.Translations.Get("tab." + name.ToLowerInvariant());

        /// <summary>The pixel bounds of the network item grid.</summary>
        private Rectangle GetGridBounds()
        {
            return new Rectangle(
                this.xPositionOnScreen + 32,
                this.yPositionOnScreen + HeaderHeight,
                Columns * SlotSize,
                this.Rows * SlotSize
            );
        }

        /// <summary>The stock entry under a screen position, or <c>null</c>.</summary>
        private NetworkItemStack GetStackAt(int x, int y)
        {
            Rectangle grid = this.GetGridBounds();
            if (!grid.Contains(x, y))
                return null;

            int column = (x - grid.X) / SlotSize;
            int row = (y - grid.Y) / SlotSize;
            int index = ((this.ScrollOffset + row) * Columns) + column;

            return index >= 0 && index < this.VisibleStock.Count
                ? this.VisibleStock[index]
                : null;
        }

        /// <summary>The largest scroll offset that still shows content.</summary>
        private int GetMaxScroll(int rows)
        {
            if (this.Tab != TerminalTab.Items)
                return this.GetMaxScrollForTab();

            int totalRows = (int)Math.Ceiling(this.VisibleStock.Count / (double)Columns);
            return Math.Max(0, totalRows - rows);
        }


        /*********
        ** Private methods: data
        *********/
        /// <summary>Re-resolves the network and rebuilds the item list.</summary>
        private void RefreshStock()
        {
            this.Network = this.Networks.GetNetworkAt(this.TerminalLocation, this.TerminalTile);
            this.AllStock = this.Network?.Aggregate() ?? new List<NetworkItemStack>();
            this.RefreshConfigRows();
            this.ApplyFilterAndSort();
        }

        /// <summary>Applies the search text and sort order to the full stock list.</summary>
        private void ApplyFilterAndSort()
        {
            IEnumerable<NetworkItemStack> query = this.AllStock;

            string search = this.SearchBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(search))
                query = query.Where(entry => Matches(entry, search));

            query = this.Sort switch
            {
                SortMode.Count => query.OrderByDescending(entry => entry.Count).ThenBy(entry => entry.DisplayName),
                SortMode.Category => query.OrderBy(entry => entry.Category).ThenBy(entry => entry.DisplayName),
                _ => query.OrderBy(entry => entry.DisplayName).ThenByDescending(entry => entry.Count)
            };

            this.VisibleStock = query.ToList();
            this.ScrollOffset = Math.Max(0, Math.Min(this.ScrollOffset, this.GetMaxScroll(this.Rows)));
        }

        /// <summary>Whether a stock entry matches the search box.</summary>
        /// <remarks>Supports a plain name search, <c>#tag</c> for context tags, and <c>@category</c> for categories.</remarks>
        private static bool Matches(NetworkItemStack entry, string search)
        {
            if (search.StartsWith("#") && search.Length > 1)
            {
                string tag = search.Substring(1);
                try
                {
                    return entry.Sample.GetContextTags().Any(value => value.Contains(tag, StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    return false;
                }
            }

            if (search.StartsWith("@") && search.Length > 1)
            {
                string category = search.Substring(1);
                string name = entry.Sample.getCategoryName();
                return !string.IsNullOrEmpty(name) && name.Contains(category, StringComparison.OrdinalIgnoreCase);
            }

            return entry.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase);
        }


        /*********
        ** Private methods: actions
        *********/
        /// <summary>Withdraws items from the network into the player's inventory.</summary>
        /// <param name="entry">The stock entry to withdraw.</param>
        /// <param name="requested">The most items to withdraw; pass <see cref="int.MaxValue"/> to fill the inventory.</param>
        private void Withdraw(NetworkItemStack entry, int requested)
        {
            if (this.Network?.IsOnline != true)
            {
                this.ShowError(this.Translations.Get("error.offline"));
                return;
            }

            int space = CountSpaceFor(entry.Sample);
            if (space <= 0)
            {
                this.ShowError(this.Translations.Get("error.inventory-full"));
                return;
            }

            int count = (int)Math.Min(Math.Min((long)requested, entry.Count), space);
            if (count <= 0)
                return;

            List<Item> withdrawn = this.Network.ExtractMerged(entry.Key, entry.Sample, count);
            int returned = 0;
            foreach (Item item in withdrawn)
            {
                if (!Game1.player.addItemToInventoryBool(item))
                {
                    // Anything the inventory turned down goes straight back rather than vanishing.
                    returned += this.Network.Insert(item);
                }
            }

            if (withdrawn.Count > 0)
                Game1.playSound("dwop");
            if (returned > 0)
                this.ShowError(this.Translations.Get("error.inventory-full"));

            this.RefreshStock();
        }

        /// <summary>Sends one inventory slot to the network.</summary>
        /// <param name="slot">The inventory slot index.</param>
        /// <param name="allOfType">Whether to also send every other stack of the same item.</param>
        /// <param name="singleItem">Whether to send a single item rather than the whole stack.</param>
        private void DepositSlot(int slot, bool allOfType, bool singleItem = false)
        {
            if (this.Network?.IsOnline != true)
            {
                this.ShowError(this.Translations.Get("error.offline"));
                return;
            }

            Item item = Game1.player.Items[slot];
            if (item == null)
                return;

            int moved;
            if (singleItem)
            {
                Item one = item.getOne();
                moved = this.Network.Insert(one);
                if (moved > 0)
                {
                    item.Stack -= moved;
                    if (item.Stack <= 0)
                        Game1.player.Items[slot] = null;
                }
            }
            else if (allOfType)
            {
                moved = 0;
                ItemKey key = ItemKey.From(item);
                for (int i = 0; i < Game1.player.Items.Count; i++)
                {
                    Item candidate = Game1.player.Items[i];
                    if (candidate == null || !ItemKey.From(candidate).Equals(key))
                        continue;
                    moved += this.DepositWholeSlot(i);
                }
            }
            else
            {
                moved = this.DepositWholeSlot(slot);
            }

            if (moved > 0)
            {
                Game1.playSound("Ship");
                this.RefreshStock();
            }
            else
                this.ShowError(this.Translations.Get("error.network-full"));
        }

        /// <summary>Sends every item in one inventory slot to the network.</summary>
        /// <returns>The number of items stored.</returns>
        private int DepositWholeSlot(int slot)
        {
            Item item = Game1.player.Items[slot];
            if (item == null)
                return 0;

            int moved = this.Network.Insert(item);
            if (item.Stack <= 0)
                Game1.player.Items[slot] = null;

            return moved;
        }

        /// <summary>Sends the player's whole inventory to the network, keeping tools and equipped items.</summary>
        private void DepositAll()
        {
            if (this.Network?.IsOnline != true)
            {
                this.ShowError(this.Translations.Get("error.offline"));
                return;
            }

            int moved = 0;
            for (int i = 0; i < Game1.player.Items.Count; i++)
            {
                Item item = Game1.player.Items[i];

                // Leave tools and anything the player is holding alone: dumping a scythe into storage from a
                // "deposit all" button is never what someone meant.
                if (item == null || item is Tool || i == Game1.player.CurrentToolIndex)
                    continue;

                moved += this.DepositWholeSlot(i);
            }

            if (moved > 0)
            {
                Game1.playSound("Ship");
                this.RefreshStock();
            }
        }

        /// <summary>Opens the vanilla crafting page backed by the network's chests.</summary>
        /// <remarks>
        /// The game's own crafting menu already knows how to craft from a list of nearby chests, so the crafting
        /// terminal hands it every chest on the network instead of reimplementing recipe matching.
        /// </remarks>
        private void OpenCraftingPage()
        {
            if (this.Network?.IsOnline != true)
            {
                this.ShowError(this.Translations.Get("error.offline"));
                return;
            }

            this.ReleaseKeyboard();
            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new CraftingPage(
                this.xPositionOnScreen,
                this.yPositionOnScreen,
                this.width,
                this.height,
                cooking: false,
                standalone_menu: true,
                material_containers: this.Network.GetMaterialInventories()
            );
        }

        /// <summary>How many of an item the player's inventory could still take.</summary>
        private static int CountSpaceFor(Item sample)
        {
            int space = 0;
            int maxStack = sample.maximumStackSize();

            for (int i = 0; i < Game1.player.Items.Count; i++)
            {
                Item item = Game1.player.Items[i];
                if (item == null)
                    space += maxStack;
                else if (item.canStackWith(sample))
                    space += Math.Max(0, maxStack - item.Stack);
            }

            return space;
        }

        /// <summary>Whether either shift key is held, which turns single transfers into bulk ones.</summary>
        private static bool IsShiftDown()
        {
            KeyboardState state = Game1.input.GetKeyboardState();
            return state.IsKeyDown(Keys.LeftShift) || state.IsKeyDown(Keys.RightShift);
        }

        /// <summary>Shows a transient error above the toolbar.</summary>
        private void ShowError(string message)
        {
            Game1.addHUDMessage(new HUDMessage(message, HUDMessage.error_type));
        }
    }
}
