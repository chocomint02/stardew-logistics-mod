namespace StardewLogistics.Framework
{
    /// <summary>Item IDs, asset names and <see cref="StardewValley.Item.modData"/> keys used by the mod.</summary>
    /// <remarks>
    /// Everything the mod places in the world is a big craftable, so its qualified ID is prefixed with "(BC)".
    /// Per-device configuration is stored in the placed object's <c>modData</c> rather than in SMAPI save data:
    /// modData travels with the object, is saved by the game, and is synchronised to farmhands automatically,
    /// which keeps the mod working in multiplayer without any custom message plumbing.
    /// </remarks>
    internal static class ModIds
    {
        /// <summary>The mod's unique ID, matching <c>manifest.json</c>.</summary>
        public const string ModId = "chocomint.StardewLogistics";

        /*********
        ** Item IDs
        *********/
        /// <summary>The cable item. Unlike the other devices this is a plain object, because placing it lays a floor.</summary>
        public const string Cable = ModId + "_Cable";

        /// <summary>The <c>Data/FloorsAndPaths</c> entry the cable item places.</summary>
        public const string CableFloorId = ModId + "_CableFloor";
        public const string Terminal = ModId + "_Terminal";
        public const string CraftingTerminal = ModId + "_CraftingTerminal";

        /*********
        ** Assets
        *********/
        /// <summary>The spritesheet holding every craftable this mod adds.</summary>
        public const string TextureAsset = ModId + "/Craftables";

        /// <summary>The cable floor's tilesheet, holding the sixteen connection variants.</summary>
        public const string CableFloorTexture = ModId + "/CableFloor";

        /// <summary>The terminal's own UI icons, drawn rather than cropped out of the game's shared cursor sheet.</summary>
        public const string UiIconsTexture = ModId + "/UiIcons";

        /*********
        ** modData keys
        *********/
        /// <summary>An integer insertion priority stored on a networked chest. Higher fills first.</summary>
        public const string PriorityKey = ModId + "/priority";

        /// <summary>A serialised <see cref="ItemFilter"/> stored on a chest or bus.</summary>
        public const string FilterKey = ModId + "/filter";

        /// <summary>Returns the qualified item ID for one of the mod's big craftables.</summary>
        public static string Qualify(string itemId) => "(BC)" + itemId;

        /// <summary>Returns the qualified item ID for the cable, which is a plain object rather than a big craftable.</summary>
        public static string QualifyCable() => "(O)" + Cable;

        /// <summary>Whether the given unqualified item ID is one of the mod's devices.</summary>
        public static bool IsModDevice(string itemId)
        {
            return itemId == Cable
                || itemId == Terminal
                || itemId == CraftingTerminal;
        }
    }
}
