using System.Collections.Generic;
using StardewLogistics.Framework;
using StardewModdingAPI;
using StardewValley.GameData.BigCraftables;

namespace StardewLogistics.Integrations
{
    /// <summary>Defines the craftables this mod adds and injects them into the game's data assets.</summary>
    internal class ContentInjector
    {
        /*********
        ** Fields
        *********/
        private readonly ITranslationHelper Translations;


        /*********
        ** Accessors
        *********/
        /// <summary>The crafting recipes this mod adds, mapped to the mining level that teaches them.</summary>
        /// <remarks>Recipe keys double as the entries in <c>Data/CraftingRecipes</c>, so they stay in English.</remarks>
        public static readonly IReadOnlyDictionary<string, int> RecipeUnlockLevels = new Dictionary<string, int>
        {
            ["Logistics Cable"] = 2,
            ["Storage Terminal"] = 4,
            ["Import Bus"] = 5,
            ["Export Bus"] = 5,
            ["Logistics Controller"] = 7,
            ["Crafting Terminal"] = 8
        };


        /*********
        ** Public methods
        *********/
        public ContentInjector(ITranslationHelper translations)
        {
            this.Translations = translations;
        }

        /// <summary>Adds the mod's big craftables to <c>Data/BigCraftables</c>.</summary>
        public void EditBigCraftables(IDictionary<string, BigCraftableData> data)
        {
            this.Add(data, ModIds.Cable, "cable", spriteIndex: 0, price: 30);
            this.Add(data, ModIds.Controller, "controller", spriteIndex: 1, price: 800);
            this.Add(data, ModIds.Terminal, "terminal", spriteIndex: 2, price: 500);
            this.Add(data, ModIds.CraftingTerminal, "crafting-terminal", spriteIndex: 3, price: 900);
            this.Add(data, ModIds.ImportBus, "import-bus", spriteIndex: 4, price: 220);
            this.Add(data, ModIds.ExportBus, "export-bus", spriteIndex: 5, price: 220);
        }

        /// <summary>Adds the mod's recipes to <c>Data/CraftingRecipes</c>.</summary>
        /// <remarks>
        /// The format is <c>ingredients / unused / yield / isBigCraftable / unlock conditions / display name</c>.
        /// The unlock field is <c>null</c> because the mod grants recipes itself, which lets the
        /// <c>UnlockAllRecipes</c> setting work without a second data edit.
        /// </remarks>
        public void EditRecipes(IDictionary<string, string> data)
        {
            // 334 copper bar, 335 iron bar, 336 gold bar, 337 iridium bar, 338 refined quartz,
            // 390 stone, 709 hardwood, 787 battery pack.
            data["Logistics Cable"] = $"334 1 390 5/Home/{ModIds.Cable} 8/true/null/{this.Name("cable")}";
            data["Storage Terminal"] = $"335 2 338 5 709 10/Home/{ModIds.Terminal} 1/true/null/{this.Name("terminal")}";
            data["Import Bus"] = $"335 2 338 2 390 20/Home/{ModIds.ImportBus} 1/true/null/{this.Name("import-bus")}";
            data["Export Bus"] = $"335 2 338 2 390 20/Home/{ModIds.ExportBus} 1/true/null/{this.Name("export-bus")}";
            data["Logistics Controller"] = $"337 1 787 2 336 5/Home/{ModIds.Controller} 1/true/null/{this.Name("controller")}";
            data["Crafting Terminal"] = $"336 3 338 10 787 1/Home/{ModIds.CraftingTerminal} 1/true/null/{this.Name("crafting-terminal")}";
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Adds one big craftable entry.</summary>
        private void Add(IDictionary<string, BigCraftableData> data, string id, string translationKey, int spriteIndex, int price)
        {
            data[id] = new BigCraftableData
            {
                Name = id,
                DisplayName = this.Name(translationKey),
                Description = this.Translations.Get($"item.{translationKey}.description"),
                Price = price,
                Fragility = 0,
                CanBePlacedOutdoors = true,
                CanBePlacedIndoors = true,
                IsLamp = false,
                Texture = ModIds.TextureAsset,
                SpriteIndex = spriteIndex,
                ContextTags = new List<string> { "logistics_device" },
                CustomFields = new Dictionary<string, string>()
            };
        }

        /// <summary>The translated display name for a device.</summary>
        private string Name(string translationKey) => this.Translations.Get($"item.{translationKey}.name");
    }
}
