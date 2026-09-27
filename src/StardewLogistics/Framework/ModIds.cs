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

        /// <summary>Makes a channel live and joins its network to everything else on that channel.</summary>
        public const string WirelessTransmitter = ModId + "_WirelessTransmitter";

        /// <summary>Joins its network to a live channel's.</summary>
        public const string WirelessReceiver = ModId + "_WirelessReceiver";

        /// <summary>The handheld terminal, worn in its own accessory slot and opened with a hotkey anywhere.</summary>
        public const string WirelessTerminal = ModId + "_WirelessTerminal";

        /// <summary>Tills, plants, waters and harvests an area, drawing seeds from and sending crops to the network.</summary>
        public const string AutoHarvester = ModId + "_AutoHarvester";

        /*********
        ** Assets
        *********/
        /// <summary>The spritesheet holding every craftable this mod adds.</summary>
        public const string TextureAsset = ModId + "/Craftables";

        /// <summary>The cable floor's tilesheet, holding the sixteen connection variants.</summary>
        public const string CableFloorTexture = ModId + "/CableFloor";

        /// <summary>The terminal's own UI icons, drawn rather than cropped out of the game's shared cursor sheet.</summary>
        public const string UiIconsTexture = ModId + "/UiIcons";

        /// <summary>The spritesheet for the mod's 16x16 objects.</summary>
        public const string ItemsTexture = ModId + "/Items";

        /// <summary>The asset name for the data pulses drawn over cables.</summary>
        public const string CablePulseTexture = ModId + "/CablePulse";

        /// <summary>The global inventory holding a player's equipped Wireless Terminal, by player ID.</summary>
        public const string AccessoryInventoryPrefix = ModId + "/accessory/";

        /// <summary>Global inventories that carry items between a farmhand and the host's network, by player ID.</summary>
        /// <remarks>The host puts what a farmhand withdrew in their mailbox; the farmhand puts what they deposit in their outbox.</remarks>
        public const string MailboxPrefix = ModId + "/mailbox/";
        public const string OutboxPrefix = ModId + "/outbox/";

        /// <summary>The network a player's open terminal is using, on the player, so the host knows where deposits go.</summary>
        public const string TerminalRefKey = ModId + "/terminal-ref";

        /*********
        ** modData keys
        *********/
        /// <summary>An integer insertion priority stored on a networked chest. Higher fills first.</summary>
        public const string PriorityKey = ModId + "/priority";

        /// <summary>A serialised <see cref="ItemFilter"/> stored on a chest or bus.</summary>
        public const string FilterKey = ModId + "/filter";

        /// <summary>An auto-harvester's area, crop plan and replant choices.</summary>
        public const string HarvesterKey = ModId + "/harvester";

        /// <summary>Marks soil whose crop autocrafting planted, holding the seed it planted.</summary>
        /// <remarks>What tells a job's crop on an automation tile from one the player left there.</remarks>
        public const string AutomationCropKey = ModId + "/automation-crop";

        /// <summary>A terminal's minimum-stock rules.</summary>
        public const string StockRulesKey = ModId + "/stock-rules";

        /// <summary>The channel a wireless transmitter or receiver is tuned to.</summary>
        public const string ChannelKey = ModId + "/channel";

        /// <summary>The autocrafting job that has claimed a machine.</summary>
        /// <remarks>Stops two jobs fighting over one furnace, and keeps the network ticker from collecting
        /// output a job is waiting for.</remarks>
        public const string JobKey = ModId + "/job";

        /// <summary>The <c>modData</c> key marking a machine a job filled itself, rather than through the game's own loading.</summary>
        /// <remarks>Its timer is the job's, not the game's, so it says nothing about what timers the game sets (see Calibration).</remarks>
        public const string DirectLoadKey = ModId + "/direct-load";

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
