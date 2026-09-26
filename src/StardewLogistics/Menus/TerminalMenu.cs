using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewLogistics.Devices;
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
        Craft,
        Auto,
        Jobs,
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
        private const int Columns = 13;
        /// <summary>Vertical space above the grid: the tab row, the control row, and the search row.</summary>
        /// <remarks>
        /// The search box has a row to itself so it can span the window. Sharing the control row meant it took
        /// whatever was left over, which was never much and cut off longer queries.
        /// </remarks>
        private const int HeaderHeight = 168;
        /// <summary>Vertical space reserved for the player inventory.</summary>
        /// <remarks>
        /// InventoryMenu spaces its rows by more than the slot size and draws hotbar key labels above the first
        /// row, so reserving exactly three slots' worth pushes the bottom row through the menu's own border.
        /// </remarks>
        private const int InventoryHeight = 3 * SlotSize + 84;

        /// <summary>Vertical space between the grid and the player inventory.</summary>
        /// <remarks>
        /// This has to clear two things, not one: the summary line the terminal draws, and the row of hotbar key
        /// labels ("1 2 3 ... 0 - =") that <see cref="InventoryMenu"/> draws above its own top row. Sizing it for
        /// the summary alone puts the two on top of each other.
        /// </remarks>
        private const int SummaryBand = 148;

        /// <summary>The most grid rows to show, when the window is tall enough for them.</summary>
        private const int MaxRows = 8;

        private readonly ITranslationHelper Translations;
        private readonly NetworkManager Networks;
        private readonly MachineRecipeIndex MachineRecipes;
        private readonly JobRunner Jobs;
        private readonly ModConfig Config;
        private readonly GameLocation TerminalLocation;
        private readonly Vector2 TerminalTile;
        private readonly bool CanCraft;

        private StorageNetwork Network;
        private List<NetworkItemStack> AllStock = new();
        private List<NetworkItemStack> VisibleStock = new();

        private readonly InventoryMenu PlayerInventory;
        private readonly TextBox SearchBox;
        private ClickableComponent SearchBoxBounds;
        private ClickableComponent SortButton;
        private ClickableTextureComponent DepositAllButton;
        private ClickableComponent TypeFilterButton;
        private ClickableComponent ModFilterButton;
        private readonly DropdownPopup Dropdown = new();
        private readonly StockFilter Filter = new();
        private string OpenDropdownName;
        private readonly List<ClickableComponent> TabButtons = new();

        private int Rows;
        /// <summary>Where each tab is scrolled to.</summary>
        /// <remarks>
        /// One position per tab, not one shared between them. A single field was being clamped by every tab's
        /// filter on each refresh, so a tab with few rows dragged a longer tab's position back to the top every
        /// time the stock list rebuilt. Keeping them apart also means switching tabs no longer loses your place.
        /// </remarks>
        private readonly Dictionary<TerminalTab, int> ScrollByTab = new();

        /// <summary>Where the current tab is scrolled to.</summary>
        private int ScrollOffset
        {
            get => this.ScrollByTab.TryGetValue(this.Tab, out int value) ? value : 0;
            set => this.ScrollByTab[this.Tab] = Math.Max(0, value);
        }
        private int SearchBoxLeft;
        private int SearchBoxWidth;
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
        public TerminalMenu(NetworkManager networks, ITranslationHelper translations, GameLocation location, Vector2 tile, bool canCraft, MachineRecipeIndex machineRecipes, JobRunner jobs, ModConfig config)
        {
            this.Networks = networks;
            this.MachineRecipes = machineRecipes;
            this.Jobs = jobs;
            this.Config = config;
            this.Translations = translations;
            this.TerminalLocation = location;
            this.TerminalTile = tile;
            this.CanCraft = canCraft;

            // Grow the grid to fill a tall window, but shrink it rather than overflowing a short one.
            int available = Game1.uiViewport.Height - (HeaderHeight + SummaryBand + InventoryHeight + 120);
            this.Rows = Math.Clamp(available / SlotSize, 3, MaxRows);

            this.width = (Columns * SlotSize) + 96;
            this.height = HeaderHeight + (this.Rows * SlotSize) + SummaryBand + InventoryHeight;
            this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
            this.yPositionOnScreen = (Game1.uiViewport.Height - this.height) / 2;

            // The player's inventory is always 12 slots wide; the grid above it is not, so centre it rather than
            // left-aligning it under a wider grid.
            this.PlayerInventory = new InventoryMenu(
                this.xPositionOnScreen + ((this.width - (12 * SlotSize)) / 2),
                this.yPositionOnScreen + this.height - InventoryHeight + 24,
                playerInventory: true
            );

            this.SetUpComponents();

            this.SearchBox = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
            {
                X = this.SearchBoxLeft,
                Y = this.yPositionOnScreen + 116,
                Width = this.SearchBoxWidth,
                Height = 40
            };
            this.SearchBoxBounds = new ClickableComponent(new Rectangle(this.SearchBox.X, this.SearchBox.Y, this.SearchBox.Width, this.SearchBox.Height), "search");

            this.RefreshStock();
            this.initializeUpperRightCloseButton();

            this.LogOpened(location, tile);
        }

        /// <inheritdoc />
        public override void update(GameTime time)
        {
            base.update(time);

            // TextBox has no "text changed" event, so poll it: re-filtering is a list pass over data we already hold.
            if (this.SearchBox.Text != this.LastSearch)
            {
                this.LastSearch = this.SearchBox.Text;
                this.Filter.SetSearch(this.SearchBox.Text);
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
            // An open dropdown sits above everything else, so it consumes the click either way.
            if (this.Dropdown.IsOpen)
            {
                bool wasChoosing = this.OpenDropdownName != null;
                if (this.Dropdown.ReceiveLeftClick(x, y, out object chosen))
                {
                    if (wasChoosing && chosen != null)
                        this.ApplyDropdownChoice(chosen);
                    else if (wasChoosing && !this.Dropdown.IsOpen)
                        this.ApplyDropdownChoice(null);
                    return;
                }
            }

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

                this.Tab = Enum.Parse<TerminalTab>(tab.name);

                // Don't leave the search box holding the keyboard on a tab that has no search box.
                if (!this.TabHasSearch)
                {
                    this.SearchBox.Selected = false;
                    this.ReleaseKeyboard();
                }

                Game1.playSound("smallSelect");
                return;
            }

            // Search box. It filters the item grid and the recipe grid, but nothing on the other tabs.
            if (this.TabHasSearch)
            {
                bool clickedSearch = this.SearchBoxBounds.containsPoint(x, y);
                this.SearchBox.Selected = clickedSearch;
                if (clickedSearch)
                    return;
            }

            if (this.Tab == TerminalTab.Auto)
            {
                if (this.HandleSharedHeaderClick(x, y))
                    return;

                AutoTarget target = this.GetTargetAt(x, y);
                if (target != null)
                    this.OpenPlanner(target);
                return;
            }

            if (this.Tab == TerminalTab.Jobs)
            {
                this.ReceiveClickOnJobs(x, y);
                return;
            }

            if (this.Tab == TerminalTab.Craft)
            {
                if (this.CraftableOnlyButton.containsPoint(x, y))
                {
                    this.CraftableOnly = !this.CraftableOnly;
                    this.ScrollOffset = 0;
                    this.ApplyRecipeFilter();
                    Game1.playSound("smallSelect");
                    return;
                }

                if (this.HandleSharedHeaderClick(x, y))
                    return;

                RecipeEntry recipe = this.GetRecipeAt(x, y);
                if (recipe != null)
                    this.CraftRecipe(recipe, IsShiftDown() ? 5 : 1);
                return;
            }

            if (this.Tab != TerminalTab.Items)
            {
                this.ReceiveClickOnTab(x, y, rightClick: false);
                return;
            }

            if (this.HandleSharedHeaderClick(x, y))
                return;

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
            if (this.Dropdown.IsOpen)
            {
                this.Dropdown.Close();
                this.OpenDropdownName = null;
                return;
            }

            // Right-clicking a filter button clears it, rather than making the player reopen the list to pick "all".
            if (this.Tab == TerminalTab.Items && this.TypeFilterButton.containsPoint(x, y))
            {
                this.Filter.Category = null;
                this.Filter.CategoryLabel = null;
                this.ApplyFilterAndSort();
                Game1.playSound("trashcan");
                return;
            }

            if (this.Tab == TerminalTab.Items && this.ModFilterButton.containsPoint(x, y))
            {
                this.Filter.Mod = null;
                this.ApplyFilterAndSort();
                Game1.playSound("trashcan");
                return;
            }

            if (this.Tab == TerminalTab.Craft)
            {
                RecipeEntry recipe = this.GetRecipeAt(x, y);
                if (recipe != null)
                    this.OpenBulkCraft(recipe);
                return;
            }

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
            if (this.Dropdown.ReceiveScroll(direction))
                return;

            int rows = this.Tab is TerminalTab.Items or TerminalTab.Craft or TerminalTab.Auto ? this.Rows : 1;
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

            if (this.Dropdown.IsOpen)
            {
                this.Dropdown.PerformHover(x, y);
                return;
            }

            this.DepositAllButton?.tryHover(x, y);
            this.CraftableOnlyButton?.tryHover(x, y);

            this.HoverRecipe = null;
            this.HoverTarget = null;
            this.HoverX = x;
            this.HoverY = y;

            if (this.Tab == TerminalTab.Auto)
            {
                if (this.TypeFilterButton.containsPoint(x, y) || this.ModFilterButton.containsPoint(x, y))
                    this.HoverText = this.Translations.Get("ui.filter-hint");
                else
                {
                    this.HoverTarget = this.GetTargetAt(x, y);
                    if (this.HoverTarget != null)
                    {
                        this.HoverItem = this.HoverTarget.Sample;
                        this.HoverText = this.Translations.Get("auto.target-hint", new { count = NumberFormat.Full(this.HoverTarget.Count) });
                    }
                }
                return;
            }

            if (this.Tab == TerminalTab.Craft)
            {
                if (this.CraftableOnlyButton.containsPoint(x, y))
                    this.HoverText = this.Translations.Get("ui.craftable-only");
                else if (this.TypeFilterButton.containsPoint(x, y) || this.ModFilterButton.containsPoint(x, y))
                    this.HoverText = this.Translations.Get("ui.filter-hint");
                else if (this.TabHasSearch && this.SearchBoxBounds.containsPoint(x, y))
                    this.HoverText = this.Translations.Get("ui.search-help");
                else
                    this.HoverRecipe = this.GetRecipeAt(x, y);
                return;
            }

            if (this.Tab != TerminalTab.Items)
            {
                this.PerformHoverOnTab(x, y);
                return;
            }

            if (this.TabHasSearch && this.SearchBoxBounds.containsPoint(x, y))
                this.HoverText = this.Translations.Get("ui.search-help");
            else if (this.TypeFilterButton.containsPoint(x, y) || this.ModFilterButton.containsPoint(x, y))
                this.HoverText = this.Translations.Get("ui.filter-hint");
            else if (this.SortButton.containsPoint(x, y))
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
            Game1.activeClickableMenu = new TerminalMenu(this.Networks, this.Translations, this.TerminalLocation, this.TerminalTile, this.CanCraft, this.MachineRecipes, this.Jobs, this.Config);
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
                case TerminalTab.Craft:
                    this.DrawCraftTab(b);
                    break;
                case TerminalTab.Auto:
                    this.DrawAutoTab(b);
                    break;
                case TerminalTab.Jobs:
                    this.DrawJobsTab(b);
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
            this.Dropdown.Draw(b);

            if (this.HoverRecipe != null)
                this.DrawRecipeTooltip(b);
            else if (this.HoverItem != null)
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
            int tabX = this.xPositionOnScreen + 32;
            int tabY = this.yPositionOnScreen + 16;
            foreach (string name in this.GetTabNames())
            {
                int tabWidth = (int)Game1.smallFont.MeasureString(this.GetTabLabel(name)).X + 32;
                this.TabButtons.Add(new ClickableComponent(new Rectangle(tabX, tabY, tabWidth, 44), name));
                tabX += tabWidth + 8;
            }

            int buttonY = this.yPositionOnScreen + 64;

            // Icons come from the mod's own sheet: picking rectangles out of the game's shared cursor texture is
            // guesswork, and a wrong guess renders as a meaningless crop rather than failing visibly.
            Texture2D icons = Game1.content.Load<Texture2D>(ModIds.UiIconsTexture);

            // Size each labelled button to its widest possible caption rather than a fixed width, so the sort
            // mode never has to be abbreviated. Measuring every sort option keeps the row from shifting about
            // as the player cycles through them.
            int sortWidth = this.GetNames<SortMode>()
                .Select(mode => MeasureButton(this.Translations.Get("ui.sort-label", new { mode = this.Translations.Get("sort." + mode.ToLowerInvariant()) })))
                .Max();
            int typeWidth = MeasureButton(this.Translations.Get("ui.filter-type", new { value = this.Translations.Get("ui.filter-all-types") }));
            int modWidth = MeasureButton(this.Translations.Get("ui.filter-mod", new { value = this.Translations.Get("ui.filter-all-mods") }));

            int x = this.xPositionOnScreen + 32;

            this.SortButton = new ClickableComponent(new Rectangle(x, buttonY, sortWidth, 44), "sort");
            x += sortWidth + 10;

            this.DepositAllButton = new ClickableTextureComponent(new Rectangle(x, buttonY, 44, 44), icons, new Rectangle(32, 0, 16, 16), 2.5f);
            this.CraftableOnlyButton = new ClickableTextureComponent(new Rectangle(x, buttonY, 44, 44), icons, new Rectangle(0, 0, 16, 16), 2.5f);
            x += 54;

            this.TypeFilterButton = new ClickableComponent(new Rectangle(x, buttonY, typeWidth, 44), "type");
            x += typeWidth + 10;

            this.ModFilterButton = new ClickableComponent(new Rectangle(x, buttonY, modWidth, 44), "mod");
            x += modWidth + 10;

            // The search box sits on its own row and spans the full content width.
            this.SearchBoxLeft = this.xPositionOnScreen + 32;
            this.SearchBoxWidth = Columns * SlotSize;
        }

        /// <summary>The width a labelled header button needs to show a caption without truncating it.</summary>
        private static int MeasureButton(string label)
        {
            // Text inset plus room for the dropdown caret on the right.
            return (int)Game1.smallFont.MeasureString(label).X + 54;
        }

        /// <summary>The names of an enum's values, for measuring every caption a button might show.</summary>
        private IEnumerable<string> GetNames<T>() where T : struct, Enum => Enum.GetNames<T>();

        /// <summary>Whether the current tab uses the search box and filter dropdowns.</summary>
        private bool TabHasSearch => this.Tab is TerminalTab.Items or TerminalTab.Craft or TerminalTab.Auto;

        /// <summary>Handles a click on the controls shared by the Items and Craft tabs.</summary>
        /// <returns>Whether the click was consumed.</returns>
        private bool HandleSharedHeaderClick(int x, int y)
        {
            if (this.TypeFilterButton.containsPoint(x, y))
            {
                this.OpenDropdownName = "type";
                this.Dropdown.Open(this.BuildTypeOptions(), this.TypeFilterButton.bounds);
                Game1.playSound("shwip");
                return true;
            }

            if (this.ModFilterButton.containsPoint(x, y))
            {
                this.OpenDropdownName = "mod";
                this.Dropdown.Open(this.BuildModOptions(), this.ModFilterButton.bounds);
                Game1.playSound("shwip");
                return true;
            }

            if (this.SortButton.containsPoint(x, y))
            {
                this.Sort = (SortMode)(((int)this.Sort + 1) % 3);
                this.ApplyFilterAndSort();
                Game1.playSound("shwip");
                return true;
            }

            return false;
        }

        /// <summary>The tabs this terminal shows, which depends on whether it can craft.</summary>
        private IEnumerable<string> GetTabNames()
        {
            yield return nameof(TerminalTab.Items);
            if (this.CanCraft)
            {
                yield return nameof(TerminalTab.Craft);
                yield return nameof(TerminalTab.Auto);
                yield return nameof(TerminalTab.Jobs);
            }
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

        /// <summary>Clamps one tab's scroll position to its own maximum.</summary>
        private void ClampScroll(TerminalTab tab, int max)
        {
            int current = this.ScrollByTab.TryGetValue(tab, out int value) ? value : 0;
            this.ScrollByTab[tab] = Math.Max(0, Math.Min(current, Math.Max(0, max)));
        }

        /// <summary>The largest scroll offset that still shows content.</summary>
        private int GetMaxScroll(int rows)
        {
            // A switch rather than a chain of ifs: the chain began with "not Items, defer to the storage tab",
            // which made every branch after it unreachable and left Craft, Auto and Jobs reporting no scroll
            // room at all.
            switch (this.Tab)
            {
                case TerminalTab.Craft:
                    return this.GetMaxRecipeScroll();

                case TerminalTab.Auto:
                    return this.GetMaxTargetScroll();

                case TerminalTab.Jobs:
                    return Math.Max(0, (this.Jobs?.Jobs.Count ?? 0) - (this.GetGridBounds().Height / 96));

                case TerminalTab.Storage:
                    return this.GetMaxScrollForTab();

                case TerminalTab.Network:
                    return 0;

                default:
                    int totalRows = (int)Math.Ceiling(this.VisibleStock.Count / (double)Columns);
                    return Math.Max(0, totalRows - rows);
            }
        }


        /*********
        ** Private methods: data
        *********/
        /// <summary>Records what the terminal found, so a report of "it looked wrong" can be checked against the log.</summary>
        private void LogOpened(GameLocation location, Vector2 tile)
        {
            string where = $"{location?.NameOrUniqueName} ({tile.X}, {tile.Y})";

            if (this.Network == null)
            {
                Log.Trace($"Terminal opened at {where}: not connected to a cable.");
                return;
            }

            Log.Trace(
                $"Terminal opened at {where}: {this.Network.TotalCableCount} cable tiles in {this.Network.Segments.Count} segment(s), "
                + $"{this.Network.Machines.Count()} machines, "
                + $"{this.Network.Storages.Count} chests, "
                + $"{this.AllStock.Count} item kinds, "
                + $"{this.AllStock.Sum(entry => entry.Count)} items, "
                + $"{this.Network.FreeSlots} free slots; "
                + $"grid is {Columns}x{this.Rows} at {this.width}x{this.height}."
            );
        }

        /// <summary>Re-resolves the network and rebuilds the item list.</summary>
        private void RefreshStock()
        {
            this.Network = this.Networks.GetNetworkAt(this.TerminalLocation, this.TerminalTile);
            this.AllStock = this.Network?.Aggregate() ?? new List<NetworkItemStack>();
            this.RefreshConfigRows();
            this.RefreshRecipes();
            this.RefreshTargets();
            this.ApplyFilterAndSort();
        }

        /// <summary>Applies the search text and sort order to the full stock list.</summary>
        private void ApplyFilterAndSort()
        {
            IEnumerable<NetworkItemStack> query = this.AllStock;

            if (!this.Filter.IsEmpty)
                query = query.Where(entry => this.Filter.Matches(entry));

            query = this.Sort switch
            {
                SortMode.Count => query.OrderByDescending(entry => entry.Count).ThenBy(entry => entry.DisplayName),
                SortMode.Category => query.OrderBy(entry => entry.Category).ThenBy(entry => entry.DisplayName),
                _ => query.OrderBy(entry => entry.DisplayName).ThenByDescending(entry => entry.Count)
            };

            this.VisibleStock = query.ToList();
            this.ApplyRecipeFilter();
            this.ApplyTargetFilter();

            int totalRows = (int)Math.Ceiling(this.VisibleStock.Count / (double)Columns);
            this.ClampScroll(TerminalTab.Items, totalRows - this.Rows);
        }

        /// <summary>Builds the choices for the type filter, counting how many kinds of item each category holds.</summary>
        private IEnumerable<(string Label, object Value)> BuildTypeOptions()
        {
            yield return (this.Translations.Get("ui.filter-all-types"), null);

            foreach (IGrouping<int, NetworkItemStack> group in this.AllStock.GroupBy(entry => entry.Category).OrderBy(group => group.Key))
            {
                string name = group.First().Sample.getCategoryName();
                if (string.IsNullOrWhiteSpace(name))
                    name = this.Translations.Get("ui.filter-no-category");

                yield return ($"{name} ({group.Count()})", group.Key);
            }
        }

        /// <summary>Builds the choices for the mod filter.</summary>
        private IEnumerable<(string Label, object Value)> BuildModOptions()
        {
            yield return (this.Translations.Get("ui.filter-all-mods"), null);

            foreach (IGrouping<string, NetworkItemStack> group in this.AllStock.GroupBy(entry => entry.SourceMod).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                yield return ($"{group.Key} ({group.Count()})", group.Key);
        }

        /// <summary>Applies a choice made in an open dropdown.</summary>
        private void ApplyDropdownChoice(object value)
        {
            if (this.OpenDropdownName == "type")
            {
                this.Filter.Category = value as int?;
                this.Filter.CategoryLabel = value == null
                    ? null
                    : this.AllStock.FirstOrDefault(entry => entry.Category == (int)value)?.Sample.getCategoryName();
            }
            else if (this.OpenDropdownName == "mod")
            {
                this.Filter.Mod = value as string;
            }

            this.OpenDropdownName = null;
            this.ScrollOffset = 0;
            this.ApplyFilterAndSort();
        }

        /// <summary>The label shown on a filter button, including its current selection.</summary>
        private string GetFilterButtonLabel(string which)
        {
            if (which == "type")
            {
                string type = this.Filter.Category == null
                    ? this.Translations.Get("ui.filter-all-types")
                    : this.Filter.CategoryLabel ?? this.Translations.Get("ui.filter-no-category");
                return this.Translations.Get("ui.filter-type", new { value = type });
            }

            string mod = this.Filter.Mod ?? this.Translations.Get("ui.filter-all-mods");
            return this.Translations.Get("ui.filter-mod", new { value = mod });
        }


        /*********
        ** Private methods: actions
        *********/
        /// <summary>Withdraws items from the network into the player's inventory.</summary>
        /// <param name="entry">The stock entry to withdraw.</param>
        /// <param name="requested">The most items to withdraw; pass <see cref="int.MaxValue"/> to fill the inventory.</param>
        private void Withdraw(NetworkItemStack entry, int requested)
        {
            if (this.Network == null)
            {
                this.ShowError(this.Translations.Get("error.not-connected"));
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
            if (this.Network == null)
            {
                this.ShowError(this.Translations.Get("error.not-connected"));
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
            if (this.Network == null)
            {
                this.ShowError(this.Translations.Get("error.not-connected"));
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
