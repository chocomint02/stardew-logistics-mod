using System.Collections.Generic;
using System.Linq;
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
        /// <summary>Whether to learn from this save how long machines and crops really take and what shipping really pays, and plan by that.</summary>
        /// <remarks>Covers mods that change timing or prices in code rather than data. Off, plans go by the game's data alone.</remarks>
        public bool AdaptiveCalibration { get; set; } = true;

        /// <summary>Whether to teach the player every logistics recipe, rather than gating them behind progression.</summary>
        public bool UnlockAllRecipes { get; set; } = true;

        /// <summary>A key that opens the terminal for the network under the cursor, as an alternative to clicking it.</summary>
        public KeybindList OpenTerminalKey { get; set; } = new KeybindList();

        /// <summary>The key that opens the equipped Wireless Terminal, anywhere.</summary>
        public KeybindList OpenWirelessTerminalKey { get; set; } = KeybindList.Parse("B");

        /*********
        ** Appearance
        *********/
        /// <summary>The colour scheme the mod's windows are drawn in: one of <see cref="Menus.UiTheme.Names"/>.</summary>
        public string Theme { get; set; } = Menus.UiTheme.Vanilla;

        /// <summary>How fast menu animations play, as a percentage of normal; zero turns them off.</summary>
        public int AnimationSpeed { get; set; } = 100;

        /// <summary>The income graph's tier colours, lowest first, as "#RRGGBB".</summary>
        public List<string> IncomeTierColours { get; set; } = MoneyColours.DefaultTiers.Select(MoneyColours.ToHex).ToList();

        /// <summary>Colours chosen for particular income sources on the graph, by name, as "#RRGGBB".</summary>
        public Dictionary<string, string> IncomeSourceColours { get; set; } = new();

        /// <summary>Clamps every setting to a usable range, so a hand-edited config can't break the mod.</summary>
        public void Normalise()
        {
            this.MaxCraftDepth = Clamp(this.MaxCraftDepth, 1, 12);
            this.MaxNetworkSize = Clamp(this.MaxNetworkSize, 64, 200000);
            this.BusIntervalTicks = Clamp(this.BusIntervalTicks, 6, 3600);
            this.BusItemsPerRun = Clamp(this.BusItemsPerRun, 1, 999);
            this.OpenTerminalKey ??= new KeybindList();
            this.OpenWirelessTerminalKey ??= KeybindList.Parse("B");
            this.AnimationSpeed = Clamp(this.AnimationSpeed, 0, 300);

            // A colour for each tier; a missing or unreadable one goes back to its default.
            this.IncomeTierColours ??= new List<string>();
            for (int i = 0; i < MoneyColours.DefaultTiers.Length; i++)
            {
                if (i >= this.IncomeTierColours.Count)
                    this.IncomeTierColours.Add(MoneyColours.ToHex(MoneyColours.DefaultTiers[i]));
                else if (!MoneyColours.TryParseHex(this.IncomeTierColours[i], out _))
                    this.IncomeTierColours[i] = MoneyColours.ToHex(MoneyColours.DefaultTiers[i]);
            }
            if (this.IncomeTierColours.Count > MoneyColours.DefaultTiers.Length)
                this.IncomeTierColours.RemoveRange(MoneyColours.DefaultTiers.Length, this.IncomeTierColours.Count - MoneyColours.DefaultTiers.Length);
            this.IncomeSourceColours ??= new Dictionary<string, string>();
            if (!Menus.UiTheme.IsKnown(this.Theme))
                this.Theme = Menus.UiTheme.Vanilla;
        }

        /// <summary>Puts the appearance settings into effect.</summary>
        public void ApplyAppearance()
        {
            Calibration.Enabled = this.AdaptiveCalibration;
            Menus.UiTheme.Current = this.Theme;
            MoneyColours.Configure(this.IncomeTierColours, this.IncomeSourceColours);
            Menus.UiAnimation.SpeedPercent = this.AnimationSpeed;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            return value > max ? max : value;
        }
    }
}
