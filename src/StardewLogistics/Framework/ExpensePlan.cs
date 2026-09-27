using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>A purchase the player is saving for: a barn, a house upgrade, a pile of seeds.</summary>
    internal class PlannedExpense
    {
        public string Name { get; set; }
        public long Amount { get; set; }
    }

    /// <summary>The player's expense plan: what they're saving for, and what their inputs cost them.</summary>
    /// <remarks>
    /// Item costs are what a player pays for an input, per item: Starfruit Seeds at 400g. They're taken off what a
    /// producer earns to give its profit. A crop's harvest costs its seed spread over the guaranteed yield, unless
    /// the harvest itself has a cost set. Kept in the player's own <c>modData</c>, so each player has their own.
    /// </remarks>
    internal class ExpensePlan
    {
        /*********
        ** Fields
        *********/
        private const string Key = ModIds.ModId + "/expenses";


        /*********
        ** Accessors
        *********/
        /// <summary>What the player is saving for.</summary>
        public List<PlannedExpense> Expenses { get; } = new();

        /// <summary>What an item costs the player, by qualified item ID.</summary>
        public Dictionary<string, long> ItemCosts { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether gold on hand counts towards the planned expenses.</summary>
        public bool CountGoldOnHand { get; set; } = true;

        /// <summary>The total of the planned expenses.</summary>
        public long Total => this.Expenses.Sum(expense => expense.Amount);


        /*********
        ** Public methods
        *********/
        /// <summary>What one of an item costs: its own cost, or for a crop's harvest, its seed's cost over the yield.</summary>
        public double CostOf(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
                return 0;

            string id = ItemRegistry.QualifyItemId(StockId.BaseId(itemId)) ?? itemId;
            if (this.ItemCosts.TryGetValue(id, out long cost))
                return cost;

            foreach (string seed in CropMath.SeedsFor(id))
            {
                if (this.ItemCosts.TryGetValue(seed, out long seedCost))
                    return seedCost / (double)CropMath.GuaranteedYield(seed);
            }

            return 0;
        }

        /// <summary>Reads the current player's plan.</summary>
        public static ExpensePlan Load()
        {
            ExpensePlan plan = new();
            if (Game1.player == null || !Game1.player.modData.TryGetValue(Key, out string raw) || string.IsNullOrEmpty(raw))
                return plan;

            foreach (string line in raw.Split('\n'))
            {
                string[] fields = line.Split('\t');
                if (fields.Length >= 3 && fields[0] == "E" && long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount))
                    plan.Expenses.Add(new PlannedExpense { Name = fields[1], Amount = amount });
                else if (fields.Length >= 3 && fields[0] == "C" && long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long cost))
                    plan.ItemCosts[fields[1]] = cost;
                else if (fields.Length >= 2 && fields[0] == "H")
                    plan.CountGoldOnHand = fields[1] == "1";
            }

            return plan;
        }

        /// <summary>Saves the plan to the current player.</summary>
        public void Save()
        {
            if (Game1.player == null)
                return;

            IEnumerable<string> lines = new[] { "H\t" + (this.CountGoldOnHand ? "1" : "0") }
                .Concat(this.Expenses.Select(expense => $"E\t{Clean(expense.Name)}\t{expense.Amount.ToString(CultureInfo.InvariantCulture)}"))
                .Concat(this.ItemCosts.Select(pair => $"C\t{pair.Key}\t{pair.Value.ToString(CultureInfo.InvariantCulture)}"));

            Game1.player.modData[Key] = string.Join("\n", lines);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Keeps a name from breaking the format.</summary>
        private static string Clean(string text) => (text ?? "").Replace('\t', ' ').Replace('\n', ' ').Trim();
    }
}
