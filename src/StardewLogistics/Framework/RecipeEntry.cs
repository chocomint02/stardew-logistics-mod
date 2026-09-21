using System;
using System.Collections.Generic;
using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>One crafting recipe, with how many batches the network's stock could make.</summary>
    /// <remarks>
    /// Availability is worked out against the terminal's already-aggregated stock rather than by rescanning chests,
    /// which keeps it cheap on a network holding thousands of stacks. Ingredient matching is delegated to
    /// <see cref="CraftingRecipe.ItemMatchesForCrafting"/> so that category ingredients — a recipe asking for "any
    /// egg" or "any milk" — behave exactly as they do in the vanilla menu.
    /// </remarks>
    internal class RecipeEntry : IFilterableEntry
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The underlying recipe.</summary>
        public CraftingRecipe Recipe { get; }

        /// <summary>The item this recipe produces, built once for display.</summary>
        public Item Output { get; }

        /// <summary>How many times this recipe could be crafted from network stock plus the player's bag.</summary>
        public int CraftableCount { get; private set; }

        /// <summary>Whether the recipe can be crafted at least once right now.</summary>
        public bool CanCraft => this.CraftableCount > 0;

        /// <inheritdoc />
        public string DisplayName { get; }

        /// <inheritdoc />
        public int Category { get; }

        /// <inheritdoc />
        public string SourceMod { get; }

        /// <inheritdoc />
        public long Count => this.CraftableCount;

        /// <inheritdoc />
        public Item Sample => this.Output;


        /*********
        ** Public methods
        *********/
        public RecipeEntry(CraftingRecipe recipe, Item output)
        {
            this.Recipe = recipe;
            this.Output = output;
            this.DisplayName = recipe.DisplayName ?? output?.DisplayName ?? recipe.name;
            this.Category = output?.Category ?? 0;
            this.SourceMod = ItemSource.GetSourceName(output);
        }

        /// <summary>Recomputes how many batches are makeable from the given stock.</summary>
        /// <param name="stock">The network's aggregated stock.</param>
        /// <param name="includePlayerInventory">Whether the player's own bag counts toward ingredients, as it does when crafting.</param>
        public void RefreshAvailability(IEnumerable<IFilterableEntry> stock, bool includePlayerInventory = true)
        {
            int batches = int.MaxValue;

            foreach (KeyValuePair<string, int> ingredient in this.Recipe.recipeList)
            {
                if (ingredient.Value <= 0)
                    continue;

                long available = this.CountAvailable(ingredient.Key, stock, includePlayerInventory);
                batches = (int)Math.Min(batches, available / ingredient.Value);

                if (batches <= 0)
                    break;
            }

            // A recipe with no ingredients at all would otherwise report int.MaxValue batches.
            this.CraftableCount = batches == int.MaxValue ? 0 : Math.Max(0, batches);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Counts how much of one ingredient is reachable across the network and the player's bag.</summary>
        public long CountAvailable(string ingredientId, IEnumerable<IFilterableEntry> stock, bool includePlayerInventory)
        {
            long total = 0;

            foreach (IFilterableEntry entry in stock)
            {
                if (entry.Sample != null && this.Matches(entry.Sample, ingredientId))
                    total += entry.Count;
            }

            if (includePlayerInventory)
            {
                foreach (Item item in Game1.player.Items)
                {
                    if (item != null && this.Matches(item, ingredientId))
                        total += item.Stack;
                }
            }

            return total;
        }

        /// <summary>Whether an item satisfies an ingredient slot, including category ingredients.</summary>
        /// <remarks>The game's matcher is static, but it stays wrapped here for the guard around malformed recipes.</remarks>
        private bool Matches(Item item, string ingredientId)
        {
            try
            {
                return CraftingRecipe.ItemMatchesForCrafting(item, ingredientId);
            }
            catch
            {
                // A malformed recipe shouldn't take the whole crafting list down with it.
                return false;
            }
        }
    }
}
