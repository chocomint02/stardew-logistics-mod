using System;
using System.Collections.Generic;
using System.Linq;
using StardewLogistics.Network;
using StardewValley;
using StardewValley.Inventories;

namespace StardewLogistics.Devices
{
    /// <summary>Crafts a recipe using only what a network's chests hold.</summary>
    /// <remarks>
    /// Used when the host crafts for a farmhand. The game's own <c>consumeIngredients</c> takes from the current
    /// player's bag first, and on the host that's the host's bag, not the farmhand's; so ingredients are matched and
    /// taken here instead, from the network alone, lowest quality first.
    /// </remarks>
    internal static class NetworkCrafting
    {
        /// <summary>Crafts a recipe as many times as asked, or as the network allows.</summary>
        /// <param name="network">The network to take ingredients from.</param>
        /// <param name="recipe">The recipe.</param>
        /// <param name="times">How many times to craft it.</param>
        /// <param name="deliver">Takes each product.</param>
        /// <returns>How many times it was crafted.</returns>
        public static int Craft(StorageNetwork network, CraftingRecipe recipe, int times, Action<Item> deliver)
        {
            int made = 0;
            for (int i = 0; i < times; i++)
            {
                List<IInventory> materials = network.GetMaterialInventories();
                if (!HasIngredients(materials, recipe))
                    break;

                Consume(materials, recipe);
                Item product = recipe.createItem();
                if (product == null)
                    break;

                deliver(product);
                made++;
            }

            return made;
        }

        /// <summary>Whether the network holds one craft's worth of a recipe's ingredients.</summary>
        private static bool HasIngredients(List<IInventory> materials, CraftingRecipe recipe)
        {
            foreach ((string ingredient, int required) in recipe.recipeList)
            {
                int held = materials.SelectMany(inventory => inventory).Where(item => item != null && CraftingRecipe.ItemMatchesForCrafting(item, ingredient)).Sum(item => item.Stack);
                if (held < required)
                    return false;
            }

            return true;
        }

        /// <summary>Takes one craft's worth of a recipe's ingredients, lowest quality first.</summary>
        private static void Consume(List<IInventory> materials, CraftingRecipe recipe)
        {
            foreach ((string ingredient, int required) in recipe.recipeList)
            {
                int remaining = required;
                var slots = materials
                    .SelectMany(inventory => Enumerable.Range(0, inventory.Count).Select(index => (Inventory: inventory, Index: index)))
                    .Where(slot => slot.Inventory[slot.Index] != null && CraftingRecipe.ItemMatchesForCrafting(slot.Inventory[slot.Index], ingredient))
                    .OrderBy(slot => slot.Inventory[slot.Index].Quality)
                    .ToList();

                foreach ((IInventory inventory, int index) in slots)
                {
                    if (remaining <= 0)
                        break;

                    Item item = inventory[index];
                    int take = Math.Min(remaining, item.Stack);
                    item.Stack -= take;
                    remaining -= take;
                    if (item.Stack <= 0)
                        inventory[index] = null;
                }
            }

            foreach (IInventory inventory in materials)
                inventory.RemoveEmptySlots();
        }
    }
}
