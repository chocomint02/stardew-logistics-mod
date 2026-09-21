using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>Builds and refreshes the list of recipes the crafting terminal offers.</summary>
    /// <remarks>
    /// Only recipes the player has actually learned are listed, matching the vanilla crafting menu: the terminal
    /// removes the walk to a chest, not the progression. Cooking recipes are excluded for now because they need a
    /// kitchen, which the network has no equivalent of yet.
    /// </remarks>
    internal class RecipeIndex
    {
        /*********
        ** Fields
        *********/
        /// <summary>The recipes known when the index was last built, so it can be rebuilt when the player learns one.</summary>
        private int KnownRecipeCount = -1;

        private List<RecipeEntry> Entries = new();

        /// <summary>Recipes by the qualified item ID they produce, for the planner.</summary>
        private Dictionary<string, RecipeEntry> ByOutput = new(StringComparer.OrdinalIgnoreCase);


        /*********
        ** Public methods
        *********/
        /// <summary>The indexed recipes.</summary>
        public IReadOnlyList<RecipeEntry> All => this.Entries;

        /// <summary>Returns the known recipe producing an item, or <c>null</c> if the player can't craft it.</summary>
        public RecipeEntry FindByOutput(string qualifiedItemId)
        {
            return qualifiedItemId != null && this.ByOutput.TryGetValue(qualifiedItemId, out RecipeEntry entry)
                ? entry
                : null;
        }

        /// <summary>Rebuilds the recipe list if the player has learned or forgotten any, then refreshes availability.</summary>
        /// <param name="stock">The network's aggregated stock, used to work out what's makeable.</param>
        public void Refresh(IReadOnlyList<IFilterableEntry> stock)
        {
            if (!Context.IsWorldReady)
                return;

            int known = Game1.player.craftingRecipes.Count();
            if (known != this.KnownRecipeCount)
            {
                this.Entries = this.Build();
                this.KnownRecipeCount = known;

                // First recipe wins where two produce the same item, which keeps the choice stable between rebuilds.
                this.ByOutput = new Dictionary<string, RecipeEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (RecipeEntry entry in this.Entries)
                {
                    string id = entry.Output?.QualifiedItemId;
                    if (id != null)
                        this.ByOutput.TryAdd(id, entry);
                }
            }

            foreach (RecipeEntry entry in this.Entries)
                entry.RefreshAvailability(stock);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Builds a recipe entry for everything the player knows how to craft.</summary>
        private List<RecipeEntry> Build()
        {
            List<RecipeEntry> entries = new();

            foreach (string name in Game1.player.craftingRecipes.Keys)
            {
                // A recipe the player knows but whose data has gone (an uninstalled mod, say) would otherwise
                // throw on construction and take the whole tab with it.
                try
                {
                    CraftingRecipe recipe = new(name, isCookingRecipe: false);
                    Item output = recipe.createItem();
                    if (output == null)
                        continue;

                    entries.Add(new RecipeEntry(recipe, output));
                }
                catch (Exception ex)
                {
                    Log.Trace($"Skipped crafting recipe '{name}': {ex.Message}");
                }
            }

            return entries
                .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }
}
