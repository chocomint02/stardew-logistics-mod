using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SObject = StardewValley.Object;

namespace StardewLogistics.Framework
{
    /// <summary>A minimum-stock rule: keep at least so many of an item in storage, making more when it drops below.</summary>
    /// <remarks>
    /// Kept in the <c>modData</c> of the terminal it was set on, like every other device's settings, so it saves
    /// with the terminal and needs no save-data plumbing. A network's rules are those of every terminal on it.
    /// </remarks>
    internal class StockRule
    {
        /*********
        ** Fields
        *********/
        private const string Version = "1";


        /*********
        ** Accessors
        *********/
        /// <summary>The stock ID to keep.</summary>
        public string ItemId { get; set; }

        /// <summary>The quality to age it to in casks, or <see cref="Quality.Any"/> for no aging.</summary>
        public int Quality { get; set; } = Framework.Quality.Any;

        /// <summary>How many to keep in storage.</summary>
        public int Target { get; set; } = 1;

        /// <summary>Whether its jobs use Fairy Dust from storage.</summary>
        public bool UseFairyDust { get; set; }

        /// <summary>The fertilizer its jobs lay under crops they plant, or <c>null</c> for none.</summary>
        public string FertilizerId { get; set; }

        /// <summary>How many machines each step may occupy; zero for as many as the network has.</summary>
        public int MaxMachines { get; set; }

        /// <summary>What tells one rule from another: the item, and the quality it's kept at.</summary>
        public string Key => $"{this.ItemId}|q{this.Quality}";


        /*********
        ** Public methods
        *********/
        /// <summary>Reads the rules stored on a terminal.</summary>
        public static List<StockRule> Read(SObject terminal)
        {
            List<StockRule> rules = new();
            if (terminal == null || !terminal.modData.TryGetValue(ModIds.StockRulesKey, out string raw) || string.IsNullOrEmpty(raw))
                return rules;

            foreach (string line in raw.Split('\n'))
            {
                string[] fields = line.Split('\t');
                if (fields.Length < 7 || fields[0] != Version || string.IsNullOrWhiteSpace(fields[1]))
                    continue;

                rules.Add(new StockRule
                {
                    ItemId = fields[1],
                    Quality = Int(fields[2], Framework.Quality.Any),
                    Target = Math.Max(1, Int(fields[3], 1)),
                    UseFairyDust = fields[4] == "1",
                    FertilizerId = string.IsNullOrWhiteSpace(fields[5]) ? null : fields[5],
                    MaxMachines = Math.Max(0, Int(fields[6], 0))
                });
            }

            return rules;
        }

        /// <summary>Stores rules on a terminal, replacing what it had.</summary>
        public static void Write(SObject terminal, IEnumerable<StockRule> rules)
        {
            if (terminal == null)
                return;

            string text = string.Join("\n", rules.Select(rule => string.Join("\t",
                Version,
                rule.ItemId,
                rule.Quality.ToString(CultureInfo.InvariantCulture),
                rule.Target.ToString(CultureInfo.InvariantCulture),
                rule.UseFairyDust ? "1" : "0",
                rule.FertilizerId ?? "",
                rule.MaxMachines.ToString(CultureInfo.InvariantCulture))));

            if (text.Length == 0)
                terminal.modData.Remove(ModIds.StockRulesKey);
            else
                terminal.modData[ModIds.StockRulesKey] = text;
        }

        /// <summary>A copy, for editing without touching the stored rule.</summary>
        public StockRule Clone() => (StockRule)this.MemberwiseClone();


        /*********
        ** Private methods
        *********/
        private static int Int(string text, int fallback) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    }
}
