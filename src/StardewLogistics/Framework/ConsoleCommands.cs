using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewLogistics.Network;
using StardewModdingAPI;
using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>SMAPI console commands for inspecting the autocrafting data.</summary>
    /// <remarks>
    /// Machine recipes are derived from game data rather than declared anywhere, and plans are built from a
    /// recursive walk over them. Neither can be checked by reading the code — the only way to know the index
    /// matches reality is to print what it produced against a real save, which is what these are for.
    /// </remarks>
    internal class ConsoleCommands
    {
        /*********
        ** Fields
        *********/
        private readonly MachineRecipeIndex Machines;
        private readonly RecipeIndex Crafting;
        private readonly NetworkManager Networks;
        private readonly ModConfig Config;


        /*********
        ** Public methods
        *********/
        public ConsoleCommands(MachineRecipeIndex machines, RecipeIndex crafting, NetworkManager networks, ModConfig config)
        {
            this.Machines = machines;
            this.Crafting = crafting;
            this.Networks = networks;
            this.Config = config;
        }

        /// <summary>Registers the commands.</summary>
        public void Register(ICommandHelper commands)
        {
            commands.Add("logistics_machines", "Lists indexed processing recipes. Usage: logistics_machines [name filter]", this.ListMachines);
            commands.Add("logistics_plan", "Builds an autocrafting plan. Usage: logistics_plan <qualified item id> [count]", this.ShowPlan);
            commands.Add("logistics_stock", "Lists what the network at your location holds.", this.ShowStock);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Prints the indexed processing recipes, optionally filtered by output name.</summary>
        private void ListMachines(string command, string[] args)
        {
            if (this.Machines.All.Count == 0)
            {
                Log.Debug("No processing recipes indexed. Load a save first.");
                return;
            }

            string filter = args.Length > 0 ? string.Join(" ", args) : null;

            List<MachineRecipe> matches = this.Machines.All
                .Where(recipe => filter == null
                    || GetName(recipe.OutputId).Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || recipe.MachineName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(recipe => recipe.MachineName)
                .ThenBy(recipe => GetName(recipe.OutputId))
                .ToList();

            StringBuilder output = new();
            output.AppendLine($"{matches.Count} of {this.Machines.All.Count} processing recipes ({this.Machines.SkippedRules} rules skipped as unpredictable):");

            foreach (MachineRecipe recipe in matches.Take(60))
            {
                string inputs = string.Join(" + ", recipe.GetAllInputs().Select(input => $"{input.Count}x {GetName(input.ItemId)}"));
                string yield = recipe.HasVariableYield
                    ? $"{recipe.OutputCount}-{recipe.MaxOutputCount}x {GetName(recipe.OutputId)} (planning uses {recipe.OutputCount})"
                    : $"{recipe.OutputCount}x {GetName(recipe.OutputId)}";

                output.AppendLine($"  {recipe.MachineName,-22} {inputs}  ->  {yield}   [{FormatTime(recipe.Minutes, recipe.Days)}]");
            }

            if (matches.Count > 60)
                output.AppendLine($"  ... and {matches.Count - 60} more");

            Log.Debug(output.ToString());
        }

        /// <summary>Builds a plan for an item and prints it as a tree.</summary>
        private void ShowPlan(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                Log.Debug("Load a save first.");
                return;
            }

            if (args.Length == 0)
            {
                Log.Debug("Usage: logistics_plan <qualified item id> [count]   e.g. logistics_plan (O)337 5");
                return;
            }

            string itemId = args[0];
            int count = args.Length > 1 && int.TryParse(args[1], out int parsed) ? Math.Max(1, parsed) : 1;

            IReadOnlyList<IFilterableEntry> stock = this.GetStock();
            this.Crafting.Refresh(stock);

            CraftPlanner planner = new(this.Crafting, this.Machines, this.Config.MaxCraftDepth);
            CraftPlan plan = planner.Plan(itemId, count, stock);

            StringBuilder output = new();
            output.AppendLine($"Plan for {count}x {GetName(itemId)} against {stock.Count} kinds in storage:");
            Describe(plan.Root, output);

            output.AppendLine($"  steps: {plan.StepCount}   worst-case time: {FormatTime(plan.WorstCaseMinutes, 0)}");
            output.AppendLine(plan.IsSatisfied
                ? "  status: can be completed from stock"
                : "  status: SHORT OF " + string.Join(", ", plan.Shortfalls.Select(cost => $"{cost.Count}x {GetName(cost.ItemId)}")));

            if (plan.HitDepthLimit)
                output.AppendLine($"  note: hit the depth limit of {this.Config.MaxCraftDepth}; some branches were left unresolved.");

            Log.Debug(output.ToString());
        }

        /// <summary>Prints what the network at the player's location holds.</summary>
        private void ShowStock(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                Log.Debug("Load a save first.");
                return;
            }

            IReadOnlyList<IFilterableEntry> stock = this.GetStock();
            StringBuilder output = new();
            output.AppendLine($"{stock.Count} kinds in storage:");

            foreach (IFilterableEntry entry in stock.OrderByDescending(entry => entry.Count).Take(40))
                output.AppendLine($"  {entry.Count,8}  {entry.DisplayName}   [{entry.Sample?.QualifiedItemId}]");

            Log.Debug(output.ToString());
        }

        /// <summary>Returns the stock of the first network in the player's current location.</summary>
        private IReadOnlyList<IFilterableEntry> GetStock()
        {
            StorageNetwork network = this.Networks.GetNetworks(Game1.currentLocation).FirstOrDefault();
            return network?.Aggregate().Cast<IFilterableEntry>().ToList() ?? new List<IFilterableEntry>();
        }

        /// <summary>Writes one plan node and its children, indented by depth.</summary>
        private static void Describe(PlanNode node, StringBuilder output)
        {
            if (node == null)
                return;

            string indent = new(' ', 2 + (node.Depth * 3));
            string detail = node.Kind switch
            {
                PlanStepKind.FromStock => "from stock",
                PlanStepKind.Craft => $"craft x{node.Batches}",
                PlanStepKind.Process => $"{node.MachineRecipe.MachineName} x{node.Batches} @ {FormatTime(node.MinutesPerBatch, node.DaysPerBatch)} each"
                    + (node.Alternatives.Count > 1 ? $" ({node.Alternatives.Count} machines could)" : ""),
                _ => "MISSING"
            };

            string supply = node.FromStock > 0 && node.Kind != PlanStepKind.FromStock
                ? $" ({node.FromStock} from stock)"
                : "";

            output.AppendLine($"{indent}{node.Requested}x {node.DisplayName} - {detail}{supply}");

            foreach (PlanNode child in node.Children)
                Describe(child, output);
        }

        /// <summary>Formats an in-game duration.</summary>
        private static string FormatTime(int minutes, int days)
        {
            if (days > 0)
                return days == 1 ? "overnight" : $"{days} days";
            if (minutes <= 0)
                return "instant";
            if (minutes < 60)
                return $"{minutes}m";

            return minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h {minutes % 60}m";
        }

        /// <summary>The display name for an item ID.</summary>
        private static string GetName(string qualifiedId)
        {
            try
            {
                return ItemRegistry.GetData(qualifiedId)?.DisplayName ?? qualifiedId;
            }
            catch
            {
                return qualifiedId;
            }
        }
    }
}
