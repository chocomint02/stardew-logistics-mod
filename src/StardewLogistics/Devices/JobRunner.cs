using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.Inventories;
using SObject = StardewValley.Object;

namespace StardewLogistics.Devices
{
    /// <summary>Creates autocrafting jobs and drives them to completion.</summary>
    /// <remarks>
    /// Machines are driven directly rather than through <c>Object.PlaceInMachine</c>. That method checks a
    /// machine's <c>AdditionalConsumedItems</c> — a furnace's coal — against the <em>player's</em> inventory, which
    /// is the wrong place for an automation mod: the coal is in the network, and the player may be asleep or in
    /// another location entirely. So the runner takes every input from storage itself and sets the machine's state
    /// to the outcome the recipe index already worked out. The game's own clock then runs the machine down and
    /// flags it ready exactly as usual.
    ///
    /// Machines a job has claimed are marked in their <c>modData</c>, which both stops two jobs fighting over the
    /// same furnace and keeps <see cref="NetworkTicker"/> from collecting output a job is waiting for.
    /// </remarks>
    internal class JobRunner
    {
        /*********
        ** Fields
        *********/
        private readonly NetworkManager Networks;
        private readonly MachineRecipeIndex MachineRecipes;
        private readonly RecipeIndex CraftingRecipes;
        private readonly ModConfig Config;

        private readonly List<CraftJob> JobList = new();
        private int NextJobNumber = 1;


        /*********
        ** Accessors
        *********/
        /// <summary>Every job, finished ones included until they're cleared.</summary>
        public IReadOnlyList<CraftJob> Jobs => this.JobList;


        /*********
        ** Public methods
        *********/
        public JobRunner(NetworkManager networks, MachineRecipeIndex machineRecipes, RecipeIndex craftingRecipes, ModConfig config)
        {
            this.Networks = networks;
            this.MachineRecipes = machineRecipes;
            this.CraftingRecipes = craftingRecipes;
            this.Config = config;
        }

        /// <summary>Plans a job and queues it.</summary>
        /// <param name="targetId">The qualified item ID to make.</param>
        /// <param name="count">How many to make.</param>
        /// <param name="network">The network the job runs on.</param>
        /// <param name="maxMachines">How many machines each processing step may occupy at once; zero for no limit.</param>
        /// <param name="preferredMachines">The player's machine choice per output item.</param>
        /// <param name="error">Why the job couldn't be queued.</param>
        public CraftJob TryQueue(string targetId, int count, StorageNetwork network, int maxMachines, IReadOnlyDictionary<string, string> preferredMachines, out string error)
        {
            error = null;

            if (network == null)
            {
                error = "no network here";
                return null;
            }

            List<NetworkItemStack> stock = network.Aggregate();
            IReadOnlyList<IFilterableEntry> filterable = stock.Cast<IFilterableEntry>().ToList();
            this.CraftingRecipes.Refresh(filterable);

            CraftPlanner planner = new(this.CraftingRecipes, this.MachineRecipes, this.Config.MaxCraftDepth);
            CraftPlan plan = planner.Plan(targetId, count, filterable, preferredMachines, network.CountUsableMachines);

            if (!plan.IsSatisfied)
            {
                error = "short of " + string.Join(", ", plan.Shortfalls.Select(cost => $"{cost.Count}x {GetName(cost.ItemId)}"));
                return null;
            }

            CraftJob job = new()
            {
                Id = "J" + this.NextJobNumber++,
                TargetId = targetId,
                DisplayName = GetName(targetId),
                TargetCount = count,
                LocationName = network.Location?.NameOrUniqueName,
                AnchorTile = network.CableTiles.FirstOrDefault(),
                Steps = Flatten(plan, maxMachines, network.CountUsableMachines)
            };

            if (job.Steps.Count == 0)
            {
                error = "nothing to do; it's already in storage";
                return null;
            }

            job.Status = JobStatus.Pending;
            this.JobList.Add(job);
            Log.Debug($"Queued {job.Id}: {count}x {job.DisplayName} in {job.Steps.Count} steps.");
            return job;
        }

        /// <summary>Stops a job and releases any machines it holds.</summary>
        public bool Cancel(string jobId)
        {
            CraftJob job = this.JobList.FirstOrDefault(candidate => string.Equals(candidate.Id, jobId, StringComparison.OrdinalIgnoreCase));
            if (job == null || job.Status is JobStatus.Complete or JobStatus.Cancelled)
                return false;

            // Machines already running keep their contents; releasing the claim just lets the network collect
            // them normally rather than stranding whatever is inside.
            foreach (JobStep step in job.Steps)
            {
                foreach (RunningBatch batch in step.InFlight)
                {
                    SObject machine = this.FindMachine(batch);
                    machine?.modData.Remove(ModIds.JobKey);
                }
                step.InFlight.Clear();
            }

            job.Status = JobStatus.Cancelled;

            // Drop it straight away. Leaving a cancelled job on the list reads as "still there", and there is
            // nothing left to tell the player about it.
            this.JobList.Remove(job);
            return true;
        }

        /// <summary>Removes one finished job from the list.</summary>
        public bool Dismiss(string jobId)
        {
            return this.JobList.RemoveAll(job =>
                string.Equals(job.Id, jobId, StringComparison.OrdinalIgnoreCase)
                && job.Status is JobStatus.Complete or JobStatus.Cancelled) > 0;
        }

        /// <summary>Removes finished and cancelled jobs from the list.</summary>
        public int ClearFinished()
        {
            return this.JobList.RemoveAll(job => job.Status is JobStatus.Complete or JobStatus.Cancelled);
        }

        /// <summary>Advances every running job by one scheduler tick.</summary>
        public void Run()
        {
            foreach (CraftJob job in this.JobList.ToList())
            {
                if (job.Status is JobStatus.Complete or JobStatus.Cancelled)
                    continue;

                StorageNetwork network = this.ResolveNetwork(job);
                if (network == null)
                {
                    job.Status = JobStatus.Blocked;
                    job.BlockedReason = "the network is gone";
                    continue;
                }

                this.CollectFinished(job, network);
                this.StartWork(job, network);

                if (job.Steps.All(step => step.IsComplete))
                {
                    job.Status = JobStatus.Complete;
                    Log.Debug($"{job.Id} finished: {job.TargetCount}x {job.DisplayName}.");
                }
            }
        }


        /*********
        ** Private methods: planning to steps
        *********/
        /// <summary>Flattens a plan into steps, deepest first so a step's inputs are produced before it runs.</summary>
        private static List<JobStep> Flatten(CraftPlan plan, int maxMachines, Func<MachineRecipe, int> countMachines)
        {
            List<PlanNode> nodes = plan.Root
                .Walk()
                .Where(node => node.Kind is PlanStepKind.Craft or PlanStepKind.Process)
                .OrderByDescending(node => node.Depth)
                .ToList();

            List<JobStep> steps = new();

            foreach (PlanNode node in nodes)
            {
                // A processing step split between machine types becomes one step per share. The runner still
                // sees "one recipe, N runs", so the split costs it no extra cases.
                if (node.Kind == PlanStepKind.Process && node.Assignments.Count > 0)
                {
                    // Divide the budget across the shares the same way the planner dialog showed it, so the job
                    // occupies the machines the player was told it would.
                    Dictionary<MachineAssignment, int> allocation = MachineAllocator.Allocate(node, maxMachines, countMachines);

                    foreach (MachineAssignment assignment in node.Assignments)
                    {
                        steps.Add(new JobStep
                        {
                            Kind = PlanStepKind.Process,
                            OutputId = node.ItemId,
                            DisplayName = node.DisplayName,
                            MachineRecipe = assignment.Recipe,
                            RemainingBatches = assignment.Runs,
                            TotalBatches = assignment.Runs,
                            MaxMachines = allocation.TryGetValue(assignment, out int machines) ? machines : 1
                        });
                    }
                    continue;
                }

                steps.Add(new JobStep
                {
                    Kind = node.Kind,
                    OutputId = node.ItemId,
                    DisplayName = node.DisplayName,
                    CraftRecipe = node.CraftRecipe,
                    RemainingBatches = node.Batches,
                    TotalBatches = node.Batches,
                    MaxMachines = maxMachines
                });
            }

            return steps;
        }


        /*********
        ** Private methods: execution
        *********/
        /// <summary>Takes finished output from this job's machines into storage.</summary>
        private void CollectFinished(CraftJob job, StorageNetwork network)
        {
            foreach (JobStep step in job.Steps)
            {
                foreach (RunningBatch batch in step.InFlight.ToList())
                {
                    SObject machine = this.FindMachine(batch);
                    if (machine == null)
                    {
                        // The machine was broken or removed while working; the run is lost, so put the batch back.
                        step.InFlight.Remove(batch);
                        step.RemainingBatches++;
                        continue;
                    }

                    // Finished, and still holding its output: the normal path.
                    if (machine.readyForHarvest.Value && machine.heldObject.Value != null)
                    {
                        this.Collect(machine, step, batch, network);
                        continue;
                    }

                    // Idle, but we still hold the claim: the run finished and something else emptied the
                    // machine. Another automation mod working the same machines does exactly this. The output
                    // went to the chests this job is filling either way, so credit the batch rather than
                    // waiting forever for output that has already arrived.
                    if (!machine.readyForHarvest.Value && machine.heldObject.Value == null && machine.MinutesUntilReady <= 0)
                    {
                        machine.modData.Remove(ModIds.JobKey);
                        step.InFlight.Remove(batch);
                        step.CompletedBatches++;
                        Log.Trace($"{job.Id}: a run finished but was collected by something else; counting it.");
                    }
                }
            }
        }

        /// <summary>Takes a finished machine's output into storage and closes off the batch.</summary>
        private void Collect(SObject machine, JobStep step, RunningBatch batch, StorageNetwork network)
        {
            network.Insert(machine.heldObject.Value);

            machine.heldObject.Value = null;
            machine.readyForHarvest.Value = false;
            machine.showNextIndex.Value = false;
            machine.minutesUntilReady.Value = 0;
            machine.modData.Remove(ModIds.JobKey);

            step.InFlight.Remove(batch);
            step.CompletedBatches++;
        }

        /// <summary>Starts whatever work the job can start right now.</summary>
        private void StartWork(CraftJob job, StorageNetwork network)
        {
            bool didSomething = false;

            foreach (JobStep step in job.Steps)
            {
                if (step.RemainingBatches <= 0)
                    continue;

                didSomething |= step.Kind == PlanStepKind.Craft
                    ? this.RunCraftStep(step, network)
                    : this.RunProcessStep(job, step, network);
            }

            if (didSomething)
            {
                job.Status = JobStatus.Running;
                job.BlockedReason = null;
            }
            else if (job.Steps.Any(step => step.InFlight.Count > 0))
                job.Status = JobStatus.Running;
            else if (job.Status != JobStatus.Complete)
            {
                job.Status = JobStatus.Blocked;
                job.BlockedReason ??= "waiting on materials";
            }
        }

        /// <summary>Runs as many crafting batches as the materials allow. Crafting is instant.</summary>
        private bool RunCraftStep(JobStep step, StorageNetwork network)
        {
            bool any = false;

            while (step.RemainingBatches > 0)
            {
                List<IInventory> materials = network.GetMaterialInventories();
                if (!step.CraftRecipe.doesFarmerHaveIngredientsInInventory(materials.SelectMany(inventory => inventory).ToList()))
                    break;

                try
                {
                    step.CraftRecipe.consumeIngredients(materials);
                    Item product = step.CraftRecipe.createItem();
                    if (product == null)
                        break;

                    network.Insert(product);
                }
                catch (Exception ex)
                {
                    Log.Error($"Autocraft step for {step.DisplayName} failed.", ex);
                    break;
                }

                step.RemainingBatches--;
                step.CompletedBatches++;
                any = true;
            }

            return any;
        }

        /// <summary>Loads idle machines with this step's inputs, up to the step's machine allowance.</summary>
        private bool RunProcessStep(CraftJob job, JobStep step, StorageNetwork network)
        {
            MachineRecipe recipe = step.MachineRecipe;
            if (recipe == null)
                return false;

            int allowance = step.MaxMachines > 0 ? step.MaxMachines - step.InFlight.Count : int.MaxValue;
            if (allowance <= 0)
                return false;

            bool any = false;

            foreach (NetworkNode node in network.Machines)
            {
                if (allowance <= 0 || step.RemainingBatches <= 0)
                    break;

                SObject machine = node.Object;
                if (machine == null || !string.Equals(machine.QualifiedItemId, recipe.MachineId, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Busy, or already claimed by another job.
                if (machine.heldObject.Value != null || machine.MinutesUntilReady > 0 || machine.modData.ContainsKey(ModIds.JobKey))
                    continue;

                // The player has told this one not to take this input.
                if (!node.AcceptsInput(recipe.InputId))
                    continue;

                if (!this.TryLoadMachine(machine, recipe, network))
                    continue;

                machine.modData[ModIds.JobKey] = job.Id;
                step.InFlight.Add(new RunningBatch
                {
                    LocationName = network.Location?.NameOrUniqueName,
                    Tile = node.Tile,
                    ExpectedMinutes = recipe.Minutes,
                    Yield = recipe.OutputCount
                });

                step.RemainingBatches--;
                allowance--;
                any = true;
            }

            return any;
        }

        /// <summary>Takes a run's inputs from storage and sets the machine working.</summary>
        /// <returns>Whether the machine was loaded.</returns>
        private bool TryLoadMachine(SObject machine, MachineRecipe recipe, StorageNetwork network)
        {
            List<ItemCost> inputs = recipe.GetAllInputs().ToList();

            // Check everything is present before taking any of it, so a partial withdrawal can't strand materials.
            foreach (ItemCost input in inputs)
            {
                if (network.CountById(input.ItemId) < input.Count)
                    return false;
            }

            Item output;
            try
            {
                output = ItemRegistry.Create(recipe.OutputId, recipe.OutputCount, allowNull: true);
            }
            catch
            {
                output = null;
            }

            if (output is not SObject product)
                return false;

            foreach (ItemCost input in inputs)
                network.ExtractById(input.ItemId, input.Count);

            // Set the machine to the outcome the recipe index already worked out. The game's clock counts
            // minutesUntilReady down and raises readyForHarvest on its own from here.
            machine.heldObject.Value = product;
            machine.minutesUntilReady.Value = Math.Max(10, recipe.Minutes + (recipe.Days * CraftPlan.MinutesPerDay));
            machine.readyForHarvest.Value = false;

            MachineData data = machine.GetMachineData();
            machine.showNextIndex.Value = data?.ShowNextIndexWhileWorking ?? false;

            return true;
        }


        /*********
        ** Private methods: lookup
        *********/
        /// <summary>Resolves the network a job runs on.</summary>
        private StorageNetwork ResolveNetwork(CraftJob job)
        {
            GameLocation location = Game1.getLocationFromName(job.LocationName);
            if (location == null)
                return null;

            return this.Networks.GetNetworkAt(location, job.AnchorTile)
                ?? this.Networks.GetNetworks(location).FirstOrDefault();
        }

        /// <summary>Finds the machine a running batch occupies.</summary>
        private SObject FindMachine(RunningBatch batch)
        {
            GameLocation location = Game1.getLocationFromName(batch.LocationName);
            return location != null && location.Objects.TryGetValue(batch.Tile, out SObject machine)
                ? machine
                : null;
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
