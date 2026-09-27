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
        private readonly StardewLogistics.Devices.JobRunner Jobs;


        /*********
        ** Public methods
        *********/
        public ConsoleCommands(MachineRecipeIndex machines, RecipeIndex crafting, NetworkManager networks, ModConfig config, StardewLogistics.Devices.JobRunner jobs)
        {
            this.Machines = machines;
            this.Crafting = crafting;
            this.Networks = networks;
            this.Config = config;
            this.Jobs = jobs;
        }

        /// <summary>Registers the commands.</summary>
        public void Register(ICommandHelper commands)
        {
            commands.Add("logistics_machines", "Lists indexed processing recipes. Usage: logistics_machines [name filter]", this.ListMachines);
            commands.Add("logistics_rawmachine", "Dumps the raw Data/Machines rules for a machine. Usage: logistics_rawmachine <name filter>", this.DumpRawMachine);
            commands.Add("logistics_plan", "Builds an autocrafting plan. Usage: logistics_plan <qualified item id> [count]", this.ShowPlan);
            commands.Add("logistics_stock", "Lists what the network at your location holds.", this.ShowStock);
            commands.Add("logistics_craft", "Queues an autocrafting job. Usage: logistics_craft <item id> <count> [max machines]", this.QueueJob);
            commands.Add("logistics_jobs", "Lists autocrafting jobs and their progress.", this.ListJobs);
            commands.Add("logistics_cancel", "Cancels a job. Usage: logistics_cancel <job id>", this.CancelJob);
            commands.Add("logistics_calibration", "Shows what's been learned about how long machines and crops really take, and what shipping really pays. Usage: logistics_calibration [reset]", this.ShowCalibration);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Prints the indexed processing recipes, optionally filtered by output name.</summary>
        private void ListMachines(string command, string[] args)
        {
            if (this.Machines.Count == 0)
            {
                Log.Debug("No processing recipes indexed. Load a save first.");
                return;
            }

            string filter = args.Length > 0 ? string.Join(" ", args) : null;

            // List what the network here could order, which includes recipes found from its stock.
            List<Item> held = Context.IsWorldReady
                ? this.GetStock().Select(entry => entry.Sample).Where(sample => sample != null).ToList()
                : new List<Item>();

            List<MachineRecipe> matches = this.Machines.GetOrderable(held)
                .Where(recipe => filter == null
                    || recipe.OutputName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || recipe.MachineName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(recipe => recipe.MachineName)
                .ThenBy(recipe => recipe.OutputName)
                .ToList();

            StringBuilder output = new();
            output.AppendLine($"{matches.Count} processing recipes match, of {this.Machines.Count} known ({this.Machines.SkippedRules} inputs skipped as unpredictable, "
                + $"{this.Machines.ExternalRequirementSkips} waiting for an extra ingredient to appear in storage). "
                + "Recipes marked [stock] are listed because their input is in storage here:");

            foreach (MachineRecipe recipe in matches.Take(60))
            {
                string inputs = recipe.DescribeInputs(GetName);
                string yield = recipe.HasVariableYield
                    ? $"{recipe.OutputCount}-{recipe.MaxOutputCount}x {recipe.OutputName} (planning uses {recipe.OutputCount})"
                    : $"{recipe.OutputCount}x {recipe.OutputName}";

                if (recipe.FromStock)
                    yield += "  [stock]";

                output.AppendLine($"  {recipe.MachineName,-22} {inputs}  ->  {yield}   [{FormatTime(recipe.Minutes, recipe.Days)}]");
            }

            if (matches.Count > 60)
                output.AppendLine($"  ... and {matches.Count - 60} more");

            Log.Debug(output.ToString());
        }

        /// <summary>Prints the unprocessed rules for a machine, exactly as Data/Machines states them.</summary>
        /// <remarks>
        /// The index reads this data through several assumptions, and when a recipe goes missing there is no way to
        /// tell from the indexed side whether the data lacked it or the reading dropped it. This prints the source.
        /// </remarks>
        private void DumpRawMachine(string command, string[] args)
        {
            string filter = args.Length > 0 ? string.Join(" ", args) : null;
            if (filter == null)
            {
                Log.Debug("Usage: logistics_rawmachine <name filter>");
                return;
            }

            Dictionary<string, StardewValley.GameData.Machines.MachineData> machines;
            try
            {
                machines = DataLoader.Machines(Game1.content);
            }
            catch (Exception ex)
            {
                Log.Debug("Couldn't read Data/Machines: " + ex.Message);
                return;
            }

            StringBuilder output = new();
            int shown = 0;

            foreach ((string machineId, StardewValley.GameData.Machines.MachineData data) in machines)
            {
                string name = GetName(machineId);
                if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && machineId.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (data?.OutputRules == null)
                    continue;

                shown++;
                output.AppendLine($"{name}  [{machineId}]");

                foreach (var rule in data.OutputRules)
                {
                    output.AppendLine($"  rule '{rule?.Id}'  {rule?.MinutesUntilReady}m / {rule?.DaysUntilReady}d");

                    foreach (var trigger in rule?.Triggers ?? new List<StardewValley.GameData.Machines.MachineOutputTriggerRule>())
                    {
                        string tags = trigger?.RequiredTags is { Count: > 0 } ? string.Join(" ", trigger.RequiredTags) : "-";
                        output.AppendLine($"    trigger {trigger?.Trigger} item='{trigger?.RequiredItemId ?? "-"}' tags=[{tags}] count={trigger?.RequiredCount} cond='{trigger?.Condition ?? "-"}'");
                    }

                    foreach (var item in rule?.OutputItem ?? new List<StardewValley.GameData.Machines.MachineItemOutput>())
                    {
                        string random = item?.RandomItemId is { Count: > 0 } ? $" random={item.RandomItemId.Count}" : "";
                        output.AppendLine($"    out id='{item?.ItemId ?? "-"}' preserveType='{item?.PreserveType ?? "-"}' preserveId='{item?.PreserveId ?? "-"}' "
                            + $"method='{item?.OutputMethod ?? "-"}' stack={item?.MinStack}-{item?.MaxStack}{random} cond='{item?.Condition ?? "-"}'");
                    }
                }
            }

            Log.Debug(shown == 0
                ? $"No machine matched '{filter}'."
                : $"Raw rules for {shown} machine(s) matching '{filter}':" + Environment.NewLine + output);
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

            StorageNetwork network = this.Networks.GetNetworks(Game1.currentLocation).FirstOrDefault();
            IReadOnlyCollection<string> availableMachines = this.GetAvailableMachines();

            CraftPlanner planner = new(this.Crafting, this.Machines, this.Config.MaxCraftDepth);
            CraftPlan plan = planner.Plan(itemId, count, stock, preferredMachines: null, countUsableMachines: network != null ? network.CountUsableMachines : null);

            StringBuilder output = new();
            output.AppendLine($"Plan for {count}x {GetName(itemId)}");
            output.AppendLine($"  storage: {stock.Count} kinds   known recipes: {this.Crafting.All.Count}   machines on network: "
                + (availableMachines.Count == 0 ? "none" : string.Join(", ", availableMachines.Select(GetName))));
            Describe(plan.Root, output);

            output.AppendLine($"  steps: {plan.StepCount}   worst-case time: {FormatTime(plan.WorstCaseMinutes, 0)}");
            output.AppendLine(plan.IsSatisfied
                ? "  status: can be completed from stock"
                : "  status: MISSING " + string.Join(", ", plan.Shortfalls.Select(cost => $"{cost.Count}x {GetName(cost.ItemId)}")));

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

        /// <summary>Queues an autocrafting job for the network at the player's location.</summary>
        private void QueueJob(string command, string[] args)
        {
            if (!Context.IsWorldReady) { Log.Debug("Load a save first."); return; }
            if (args.Length < 2)
            {
                Log.Debug("Usage: logistics_craft <qualified item id> <count> [max machines]");
                return;
            }

            int count = int.TryParse(args[1], out int parsed) ? Math.Max(1, parsed) : 1;
            int maxMachines = args.Length > 2 && int.TryParse(args[2], out int cap) ? Math.Max(0, cap) : 0;

            StorageNetwork network = this.Networks.GetNetworks(Game1.currentLocation).FirstOrDefault();
            CraftJob job = this.Jobs.TryQueue(args[0], count, network, maxMachines, null, out string error);

            Log.Debug(job != null
                ? $"Queued {job.Id}: {count}x {job.DisplayName} in {job.Steps.Count} steps"
                    + (maxMachines > 0 ? $", up to {maxMachines} machines per step." : ".")
                : $"Couldn't queue that: {error}");
        }

        /// <summary>Lists jobs and how far along they are.</summary>
        private void ListJobs(string command, string[] args)
        {
            if (this.Jobs.Jobs.Count == 0) { Log.Debug("No autocrafting jobs."); return; }

            StringBuilder output = new();
            output.AppendLine($"{this.Jobs.Jobs.Count} jobs:");

            foreach (CraftJob job in this.Jobs.Jobs)
            {
                output.AppendLine($"  {job.Id}  {job.TargetCount}x {job.DisplayName,-24} {job.Status,-9} "
                    + $"{job.Progress * 100:0}%  ~{FormatTime(job.EstimatedMinutesRemaining, 0)} left"
                    + (job.BlockedReason != null ? $"  ({job.BlockedReason})" : ""));

                foreach (JobStep step in job.Steps)
                {
                    output.AppendLine($"      {step.CompletedBatches}/{step.TotalBatches} {step.DisplayName}"
                        + (step.Kind == PlanStepKind.Process ? $" via {step.MachineRecipe.MachineName}" : " (craft)")
                        + (step.InFlight.Count > 0 ? $"  [{step.InFlight.Count} running]" : ""));
                }
            }

            Log.Debug(output.ToString());
        }

        /// <summary>Shows or clears what's been learned about timings and prices.</summary>
        private void ShowCalibration(string command, string[] args)
        {
            if (args.Length > 0 && args[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                Calibration.Reset();
                Log.Debug("Forgot everything learned about timings and prices; plans go by the game's data until more is seen.");
                return;
            }

            if (!Calibration.Enabled)
            {
                Log.Debug("Adaptive timing and prices is switched off in the config.");
                return;
            }

            List<string> lines = Calibration.Describe(GetName);
            Log.Debug(lines.Count == 0
                ? $"Timings and prices match the game's data ({Calibration.ObservationCount} observations)."
                : "Learned from this save:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", lines));
        }

        /// <summary>Cancels a job.</summary>
        private void CancelJob(string command, string[] args)
        {
            if (args.Length == 0) { Log.Debug("Usage: logistics_cancel <job id>"); return; }
            Log.Debug(this.Jobs.Cancel(args[0]) ? $"Cancelled {args[0]}." : $"No running job called {args[0]}.");
        }

        /// <summary>Returns the qualified IDs of machines wired to any network in the player's location.</summary>
        private IReadOnlyCollection<string> GetAvailableMachines()
        {
            HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);

            foreach (StorageNetwork network in this.Networks.GetNetworks(Game1.currentLocation))
            {
                foreach (NetworkNode machine in network.Machines)
                {
                    string id = machine.Object?.QualifiedItemId;
                    if (id != null)
                        ids.Add(id);
                }
            }

            return ids;
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
                PlanStepKind.Process => string.Join(" + ", node.Assignments.Select(assignment =>
                        $"{assignment.Recipe.MachineName} x{assignment.Runs} runs @ {FormatTime(assignment.Recipe.Minutes, assignment.Recipe.Days)}"))
                    + (node.Alternatives.Select(option => option.MachineId).Distinct().Count() > 1 ? "  [split]" : ""),
                _ => "MISSING"
            };

            if (node.Kind == PlanStepKind.Missing)
            {
                detail += node.Reason switch
                {
                    MissingReason.NotEnoughStock => " (not in storage, and nothing makes it)",
                    MissingReason.NoRecipe => " (no recipe you know produces it)",
                    MissingReason.NoMachineAvailable => " (needs "
                        + string.Join(" or ", node.Alternatives.Select(option => option.MachineName).Distinct().Take(3))
                        + ", not on the network)",
                    MissingReason.DepthLimit => " (hit the depth limit)",
                    MissingReason.RecipeLoop => " (recipe loops back on itself)",
                    MissingReason.OnlyFromItself => " (not in storage, and only made from one of itself)",
                    MissingReason.NotAnItem => " (recipe asks for a category, not an item)",
                    _ => ""
                };
            }

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
            return minutes <= 0 ? "instant" : Durations.Format(minutes);
        }

        /// <summary>The display name for an item ID.</summary>
        private static string GetName(string qualifiedId) => StockId.GetDisplayName(qualifiedId);
    }
}
