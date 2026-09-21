using StardewModdingAPI.Utilities;

namespace StardewLogistics.Framework
{
    /// <summary>The mod settings, editable through <c>config.json</c> or Generic Mod Config Menu.</summary>
    internal class ModConfig
    {
        /*********
        ** Network rules
        *********/
        /// <summary>How many recipe steps deep autocrafting may plan.</summary>
        /// <remarks>Ore to bar to a crafted item is three; beyond about six the plans stop being comprehensible.</remarks>
        public int MaxCraftDepth { get; set; } = 6;

        /// <summary>The most cable tiles a single network may span, as a safety valve against runaway scans.</summary>
        public int MaxNetworkSize { get; set; } = 20000;

        /*********
        ** Automation
        *********/
        /// <summary>How often import and export buses run, in game ticks (60 ticks = 1 second).</summary>
        public int BusIntervalTicks { get; set; } = 30;

        /// <summary>The most items a single bus may move per run.</summary>
        public int BusItemsPerRun { get; set; } = 64;

        /// <summary>Whether buses may harvest finished machines and load raw materials into them.</summary>
        public bool EnableMachineAutomation { get; set; } = true;

        /*********
        ** Convenience
        *********/
        /// <summary>Whether to teach the player every logistics recipe, rather than gating them behind progression.</summary>
        public bool UnlockAllRecipes { get; set; } = true;

        /// <summary>A key that opens the terminal for the network under the cursor, as an alternative to clicking it.</summary>
        public KeybindList OpenTerminalKey { get; set; } = new KeybindList();

        /// <summary>Clamps every setting to a usable range, so a hand-edited config can't break the mod.</summary>
        public void Normalise()
        {
            this.MaxCraftDepth = Clamp(this.MaxCraftDepth, 1, 12);
            this.MaxNetworkSize = Clamp(this.MaxNetworkSize, 64, 200000);
            this.BusIntervalTicks = Clamp(this.BusIntervalTicks, 6, 3600);
            this.BusItemsPerRun = Clamp(this.BusItemsPerRun, 1, 999);
            this.OpenTerminalKey ??= new KeybindList();
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            return value > max ? max : value;
        }
    }
}
