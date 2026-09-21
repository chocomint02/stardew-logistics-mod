using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Framework;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Menus;

namespace StardewLogistics.Menus
{
    /// <summary>The terminal's crafting tab: a searchable recipe browser that crafts from network stock.</summary>
    /// <remarks>
    /// This replaces handing the player the vanilla <see cref="CraftingPage"/>. That menu can't be drawn inside a
    /// tab, has no search or filtering, and could never host the autocrafting queue, so the terminal draws its own
    /// browser and reuses the Items tab's search box and Type/Mod dropdowns over recipes instead of stock.
    ///
    /// The game still does the parts it does well: <see cref="CraftingRecipe.consumeIngredients"/> takes the
    /// network's chests directly, and the hover tooltip is the vanilla ingredient panel with its have/need colouring.
    /// </remarks>
    internal partial class TerminalMenu
    {
        /*********
        ** Fields
        *********/
        private readonly RecipeIndex Recipes = new();
        private List<RecipeEntry> VisibleRecipes = new();
        private ClickableTextureComponent CraftableOnlyButton;
        private bool CraftableOnly;
        private RecipeEntry HoverRecipe;


        /*********
        ** Private methods: data
        *********/
        /// <summary>Rebuilds the recipe list and recomputes what the network can make.</summary>
        private void RefreshRecipes()
        {
            if (!this.CanCraft)
                return;

            this.Recipes.Refresh(this.AllStock);
            this.ApplyRecipeFilter();
        }

        /// <summary>Applies the search box, dropdowns and craftable-only toggle to the recipe list.</summary>
        private void ApplyRecipeFilter()
        {
            IEnumerable<RecipeEntry> query = this.Recipes.All;

            if (this.CraftableOnly)
                query = query.Where(entry => entry.CanCraft);

            if (!this.Filter.IsEmpty)
                query = query.Where(entry => this.Filter.Matches(entry));

            query = this.Sort switch
            {
                SortMode.Count => query.OrderByDescending(entry => entry.CraftableCount).ThenBy(entry => entry.DisplayName),
                SortMode.Category => query.OrderBy(entry => entry.Category).ThenBy(entry => entry.DisplayName),
                _ => query.OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            };

            this.VisibleRecipes = query.ToList();
            this.ScrollOffset = Math.Max(0, Math.Min(this.ScrollOffset, this.GetMaxRecipeScroll()));
        }

        /// <summary>The largest scroll offset that still shows recipes.</summary>
        private int GetMaxRecipeScroll()
        {
            int totalRows = (int)Math.Ceiling(this.VisibleRecipes.Count / (double)Columns);
            return Math.Max(0, totalRows - this.Rows);
        }

        /// <summary>The recipe under a screen position, or <c>null</c>.</summary>
        private RecipeEntry GetRecipeAt(int x, int y)
        {
            Rectangle grid = this.GetGridBounds();
            if (!grid.Contains(x, y))
                return null;

            int column = (x - grid.X) / SlotSize;
            int row = (y - grid.Y) / SlotSize;
            int index = ((this.ScrollOffset + row) * Columns) + column;

            return index >= 0 && index < this.VisibleRecipes.Count
                ? this.VisibleRecipes[index]
                : null;
        }


        /*********
        ** Private methods: crafting
        *********/
        /// <summary>Crafts a recipe repeatedly, stopping when the ingredients run out.</summary>
        /// <param name="entry">The recipe to craft.</param>
        /// <param name="times">How many batches to make; pass <see cref="int.MaxValue"/> to make as many as possible.</param>
        private void CraftRecipe(RecipeEntry entry, int times)
        {
            if (this.Network == null)
            {
                this.ShowError(this.Translations.Get("error.not-connected"));
                return;
            }

            if (!entry.CanCraft)
            {
                this.ShowError(this.Translations.Get("error.missing-ingredients"));
                return;
            }

            int made = 0;
            for (int i = 0; i < times; i++)
            {
                // Re-check between batches rather than trusting the count from before the first one: consuming
                // ingredients can also consume things a later batch needed.
                entry.RefreshAvailability(this.AllStock);
                if (!entry.CanCraft)
                    break;

                if (!this.CraftOnce(entry))
                    break;

                made++;

                // Stock changed underneath us, so the next iteration's availability check has to see it.
                this.AllStock = this.Network.Aggregate();
            }

            if (made > 0)
            {
                Game1.playSound("coin");
                Game1.stats.checkForCraftingAchievements();
                this.RefreshStock();
            }
            else
                this.ShowError(this.Translations.Get("error.missing-ingredients"));
        }

        /// <summary>Crafts one batch, consuming from the network and the player's bag.</summary>
        /// <returns>Whether a batch was produced.</returns>
        private bool CraftOnce(RecipeEntry entry)
        {
            List<IInventory> materials = this.Network.GetMaterialInventories();

            Item product;
            try
            {
                entry.Recipe.consumeIngredients(materials);
                product = entry.Recipe.createItem();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to craft '{entry.Recipe.name}'.", ex);
                this.ShowError(this.Translations.Get("error.craft-failed"));
                return false;
            }

            if (product == null)
                return false;

            // Record the craft the same way the vanilla menu does, so achievements and the crafting count agree.
            if (Game1.player.craftingRecipes.ContainsKey(entry.Recipe.name))
                Game1.player.craftingRecipes[entry.Recipe.name] += entry.Recipe.numberProducedPerCraft;

            this.DeliverProduct(product);
            return true;
        }

        /// <summary>Puts a crafted item into the network, falling back to the player and then the ground.</summary>
        private void DeliverProduct(Item product)
        {
            this.Network.Insert(product);
            if (product.Stack <= 0)
                return;

            if (Game1.player.addItemToInventoryBool(product))
                return;

            // Never destroy a crafted item: dropping it at the player's feet is what the game does when a
            // machine's output has nowhere to go.
            Game1.createItemDebris(product, Game1.player.getStandingPosition(), Game1.player.FacingDirection);
        }


        /*********
        ** Private methods: drawing
        *********/
        /// <summary>Draws the recipe grid.</summary>
        private void DrawCraftTab(SpriteBatch b)
        {
            Rectangle grid = this.GetGridBounds();

            if (this.Network == null)
            {
                this.DrawCentredMessage(b, grid, this.Translations.Get("error.not-connected"));
                return;
            }

            int firstIndex = this.ScrollOffset * Columns;

            for (int row = 0; row < this.Rows; row++)
            {
                for (int column = 0; column < Columns; column++)
                {
                    int x = grid.X + (column * SlotSize);
                    int y = grid.Y + (row * SlotSize);

                    b.Draw(Game1.menuTexture, new Vector2(x, y), Game1.getSourceRectForStandardTileSheet(Game1.menuTexture, 10), Color.White);

                    int index = firstIndex + (row * Columns) + column;
                    if (index >= this.VisibleRecipes.Count)
                        continue;

                    RecipeEntry entry = this.VisibleRecipes[index];

                    // Recipes that can't be made are dimmed rather than hidden, so the player can still see what
                    // exists and read what it needs.
                    entry.Output.drawInMenu(
                        b,
                        new Vector2(x, y),
                        1f,
                        entry.CanCraft ? 1f : 0.3f,
                        0.9f,
                        StackDrawType.Hide,
                        Color.White,
                        drawShadow: entry.CanCraft
                    );

                    if (entry.CanCraft)
                    {
                        string count = NumberFormat.Abbreviate(entry.CraftableCount);
                        Vector2 size = Game1.tinyFont.MeasureString(count);
                        Utility.drawTextWithShadow(b, count, Game1.tinyFont, new Vector2(x + SlotSize - size.X - 6, y + SlotSize - size.Y - 4), Color.White);
                    }
                }
            }

            if (this.VisibleRecipes.Count == 0)
            {
                this.DrawCentredMessage(b, grid, this.Translations.Get(
                    this.Recipes.All.Count == 0 ? "ui.no-recipes" : "ui.no-recipe-results"));
            }

            this.DrawScrollbar(b, grid, this.Rows, (int)Math.Ceiling(this.VisibleRecipes.Count / (double)Columns));

            string summary = this.Translations.Get("ui.craft-summary", new
            {
                shown = this.VisibleRecipes.Count,
                craftable = this.Recipes.All.Count(entry => entry.CanCraft),
                total = this.Recipes.All.Count
            });
            Utility.drawTextWithShadow(b, summary, Game1.smallFont, new Vector2(grid.X, grid.Bottom + 8), Game1.textColor);

            // The click hint lives here rather than in the tooltip: it is the same for every recipe, and a long
            // line of it inside drawToolTip overflows the box that the ingredient panel sized.
            string hint = this.Translations.Get("ui.craft-footer");
            Vector2 hintSize = Game1.smallFont.MeasureString(hint);
            Utility.drawTextWithShadow(b, hint, Game1.smallFont, new Vector2(grid.Right - hintSize.X, grid.Bottom + 8), Game1.textColor * 0.6f);
        }

        /// <summary>Opens the quantity dialog for a recipe, returning to the terminal when it closes.</summary>
        private void OpenBulkCraft(RecipeEntry entry)
        {
            if (this.Network == null)
            {
                this.ShowError(this.Translations.Get("error.not-connected"));
                return;
            }

            if (!entry.CanCraft)
            {
                this.ShowError(this.Translations.Get("error.missing-ingredients"));
                return;
            }

            this.ReleaseKeyboard();

            TerminalMenu parent = this;
            BulkCraftMenu dialog = new(entry, this.AllStock, this.Translations, count => parent.CraftRecipe(entry, count));

            // Restoring the terminal on exit covers both paths: confirming runs this first and then crafts, and
            // closing with the X or escape lands the player back where they were rather than in the world.
            dialog.exitFunction = () => Game1.activeClickableMenu = parent;

            Game1.playSound("smallSelect");
            Game1.activeClickableMenu = dialog;
        }

        /// <summary>Draws the hovered recipe's tooltip, including the vanilla ingredient panel.</summary>
        private void DrawRecipeTooltip(SpriteBatch b)
        {
            if (this.HoverRecipe == null)
                return;

            // drawToolTip sizes its box from the item and the ingredient panel; a long hoverText is drawn but
            // not measured, which is what pushed text outside the frame. Keep the text out of it entirely.
            IClickableMenu.drawToolTip(
                b,
                "",
                this.HoverRecipe.DisplayName,
                this.HoverRecipe.Output,
                craftingIngredients: this.HoverRecipe.Recipe
            );
        }
    }
}
