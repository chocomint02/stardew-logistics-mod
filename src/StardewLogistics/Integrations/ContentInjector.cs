using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewModdingAPI;
using StardewValley.GameData.BigCraftables;
using StardewValley.GameData.FloorsAndPaths;
using StardewValley.GameData.Objects;

namespace StardewLogistics.Integrations
{
    /// <summary>Defines the content this mod adds and injects it into the game's data assets.</summary>
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
            ["Crafting Terminal"] = 8
        };


        /*********
        ** Public methods
        *********/
        public ContentInjector(ITranslationHelper translations)
        {
            this.Translations = translations;
        }

        /// <summary>Adds the mod's terminals to <c>Data/BigCraftables</c>.</summary>
        /// <remarks>Sprite indexes 0, 1, 4 and 5 in the craftables sheet are unused: they held the cable before it
        /// became a floor, and the controller and buses before those were removed.</remarks>
        public void EditBigCraftables(IDictionary<string, BigCraftableData> data)
        {
            this.AddCraftable(data, ModIds.Terminal, "terminal", spriteIndex: 2, price: 500);
            this.AddCraftable(data, ModIds.CraftingTerminal, "crafting-terminal", spriteIndex: 3, price: 900);
        }

        /// <summary>Adds the cable item to <c>Data/Objects</c>.</summary>
        /// <remarks>
        /// The cable is an object rather than a big craftable because placing it lays a <c>Flooring</c>, and only
        /// objects listed in <c>Data/FloorsAndPaths</c> do that. Its icon is borrowed from the floor tilesheet:
        /// on a 64px-wide sheet, sprite index 14 lands on the straight horizontal run.
        /// </remarks>
        public void EditObjects(IDictionary<string, ObjectData> data)
        {
            data[ModIds.Cable] = new ObjectData
            {
                Name = ModIds.Cable,
                DisplayName = this.Name("cable"),
                Description = this.Translations.Get("item.cable.description"),
                Type = "Crafting",
                Category = -24,
                Price = 10,
                Texture = ModIds.CableFloorTexture,
                SpriteIndex = 14,
                Edibility = -300,
                CanBeGivenAsGift = false,
                CanBeTrashed = true,
                ExcludeFromRandomSale = true,
                ContextTags = new List<string> { "logistics_device", "floor_item" },
                CustomFields = new Dictionary<string, string>()
            };
        }

        /// <summary>Registers the cable floor in <c>Data/FloorsAndPaths</c>.</summary>
        /// <remarks>
        /// <c>ConnectType.Default</c> makes neighbouring cables merge into a continuous run, which is what the
        /// sixteen-variant tilesheet is for. The winter texture points at the same sheet so cables don't vanish
        /// under snow.
        ///
        /// Note the asset is a dictionary keyed by floor ID, not a list, so the entry is assigned rather than
        /// appended and its <c>Id</c> has to match the key.
        /// </remarks>
        public void EditFloors(IDictionary<string, FloorPathData> data)
        {
            data[ModIds.CableFloorId] = new FloorPathData
            {
                Id = ModIds.CableFloorId,
                ItemId = ModIds.Cable,
                Texture = ModIds.CableFloorTexture,
                Corner = Point.Zero,
                WinterTexture = ModIds.CableFloorTexture,
                WinterCorner = Point.Zero,
                PlacementSound = "crafting",
                RemovalSound = null,
                RemovalDebrisType = 0,
                FootstepSound = "stoneStep",
                ConnectType = FloorPathConnectType.Default,
                ShadowType = FloorPathShadowType.None,
                CornerSize = 0,
                FarmSpeedBuff = 0.1f
            };
        }

        /// <summary>Adds the mod's recipes to <c>Data/CraftingRecipes</c>.</summary>
        /// <remarks>
        /// The format is <c>ingredients / unused / yield / isBigCraftable / unlock conditions / display name</c>.
        /// The unlock field is <c>null</c> because the mod grants recipes itself, which lets the
        /// <c>UnlockAllRecipes</c> setting work without a second data edit.
        /// </remarks>
        public void EditRecipes(IDictionary<string, string> data)
        {
            // 334 copper bar, 335 iron bar, 336 gold bar, 338 refined quartz, 390 stone, 709 hardwood, 787 battery.
            data["Logistics Cable"] = $"334 1 390 5/Home/{ModIds.Cable} 8/false/null/{this.Name("cable")}";
            data["Storage Terminal"] = $"335 2 338 5 709 10/Home/{ModIds.Terminal} 1/true/null/{this.Name("terminal")}";
            data["Crafting Terminal"] = $"336 3 338 10 787 1/Home/{ModIds.CraftingTerminal} 1/true/null/{this.Name("crafting-terminal")}";
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Adds one big craftable entry.</summary>
        private void AddCraftable(IDictionary<string, BigCraftableData> data, string id, string translationKey, int spriteIndex, int price)
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
