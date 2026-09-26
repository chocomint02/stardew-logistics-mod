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

        /// <summary>Reports the crops growing under a network's auto-harvesters.</summary>
        public Func<StorageNetwork, List<IncomingCrop>> Forecast { get; set; }

        /// <summary>Lists the auto-harvesters on a network.</summary>
        public Func<StorageNetwork, IEnumerable<(GameLocation Location, Microsoft.Xna.Framework.Vector2 Tile)>> HarvestersOn { get; set; }

        /// <summary>The network cache, for menus that open a harvester's field view.</summary>
        public NetworkManager NetworkManager => this.Networks;

        /// <summary>The Fairy Dust item.</summary>
        public const string FairyDustId = "(O)872";

        /// <summary>Scheduler ticks since leftover buffers were last looked for.</summary>
        private int TicksSinceOrphanCheck;


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
        /// <param name="targetQuality">The quality to age the product to in casks, or <see cref="Quality.Any"/>.</param>
        /// <param name="useFairyDust">Whether to set aside Fairy Dust from storage and use it on the job's machines.</param>
        public CraftJob TryQueue(string targetId, int count, StorageNetwork network, int maxMachines, IReadOnlyDictionary<string, string> preferredMachines, out string error, int targetQuality = Quality.Any, bool useFairyDust = false)
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
            CraftPlan plan = planner.Plan(targetId, count, filterable, preferredMachines, network.CountUsableMachines, targetQuality, this.GetIncoming(network));

            if (!plan.IsSatisfied)
            {
                error = "short of " + string.Join(", ", plan.Shortfalls.Select(cost => $"{cost.Count}x {GetName(cost.ItemId)}"));
                return null;
            }

            string token = Guid.NewGuid().ToString("N");
            string locationName = network.Location?.NameOrUniqueName;
            Vector2 anchor = network.CableTiles.FirstOrDefault();

            CraftJob job = new()
            {
                Id = "J" + this.NextJobNumber++,
                Token = token,
                Buffer = JobBuffer.Create(token, locationName, anchor),
                TargetId = targetId,
                TargetQuality = targetQuality,
                DisplayName = GetName(targetId),
                TargetCount = count,
                LocationName = locationName,
                AnchorTile = anchor,
                Steps = Flatten(plan, maxMachines, network.CountUsableMachines)
            };

            if (job.Steps.Count == 0)
            {
                error = "nothing to do; it's already in storage";
                return null;
            }

            // Take everything the plan draws from storage now, so it's the job's from this moment on.
            if (!Reserve(job, plan, network, out string shortfall))
            {
                job.Buffer.ReturnTo(network);
                error = "storage changed while queuing (" + shortfall + "); try again";
                return null;
            }

            if (useFairyDust)
            {
                job.UseFairyDust = true;
                this.ReserveFairyDust(job, network);
            }

            // Crops the plan waits on are the job's from now: their harvest comes here instead of storage.
            foreach (PlanNode node in plan.Root.Walk())
                job.CropReservations.AddRange(node.Harvests);
            if (job.CropReservations.Count > 0)
                Log.Debug($"{job.Id}: reserved {job.CropReservations.Count} growing crops, the last ready in {job.CropReservations.Max(crop => crop.Days)} days.");

            job.Status = JobStatus.Pending;
            this.JobList.Add(job);
            Log.Debug($"Queued {job.Id}: {count}x {job.DisplayName} in {job.Steps.Count} steps.");

            // Start straight away rather than on the next tick, so the fruit is in the keg before the player
            // has looked away from the terminal.
            this.StartWork(job, network);
            return job;
        }

        /// <summary>Stops a job and releases any machines it holds.</summary>
        public bool Cancel(string jobId)
        {
            CraftJob job = this.JobList.FirstOrDefault(candidate => string.Equals(candidate.Id, jobId, StringComparison.OrdinalIgnoreCase));
            if (job == null || job.Status is JobStatus.Complete or JobStatus.Cancelled)
                return false;

            // Stop every run this job has going and take back what went in, so cancelling a week of wine gives
            // the Starfruit back rather than leaving the keg to finish on its own.
            foreach (JobStep step in job.Steps)
            {
                foreach (RunningBatch batch in step.InFlight)
                    this.StopRun(job, step, batch);
                step.InFlight.Clear();
                this.ReleaseReservations(job, step);
            }

            job.Status = JobStatus.Cancelled;

            // Give back everything it had set aside. If storage is full, the rest stays in the buffer and is
            // returned once there's room.
            if (job.Buffer != null && !job.Buffer.ReturnTo(this.ResolveNetwork(job)))
                Log.Debug($"{job.Id} cancelled; storage is full, so some of its reserved items will be returned once there's room.");

            // Drop it straight away. Leaving a cancelled job on the list reads as "still there", and there is
            // nothing left to tell the player about it.
            this.JobList.Remove(job);
            return true;
        }

        /// <summary>Empties a machine a cancelled job was using, returning what's in it to the job's buffer.</summary>
        /// <remarks>
        /// A run that hasn't finished gives back its inputs. One that has finished gives back its product
        /// instead: the work is done, and undoing it would only throw away the time spent.
        /// </remarks>
        private void StopRun(CraftJob job, JobStep step, RunningBatch batch)
        {
            SObject machine = this.FindMachine(batch);
            if (machine == null)
                return;

            // Only touch a machine this job still holds. If the claim is gone, something else has taken over
            // the machine since, and its contents aren't this job's to take.
            if (!machine.modData.TryGetValue(ModIds.JobKey, out string claim) || claim != job.Token)
                return;

            // A cask holds the item itself, partly aged; that's what goes back, at whatever quality it reached.
            if (step.MachineRecipe?.IsAging == true)
            {
                if (machine.heldObject.Value != null)
                    job.Buffer?.Add(machine.heldObject.Value);
            }
            else if (machine.readyForHarvest.Value && machine.heldObject.Value != null)
                job.Buffer?.Add(machine.heldObject.Value);
            else
            {
                foreach (Item input in batch.Inputs)
                    job.Buffer?.Add(input);
            }

            machine.heldObject.Value = null;
            machine.readyForHarvest.Value = false;
            machine.showNextIndex.Value = false;
            machine.minutesUntilReady.Value = 0;
            machine.modData.Remove(ModIds.JobKey);
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

        /// <summary>Forgets every job, for when the player leaves the save.</summary>
        /// <remarks>Their buffers are in the save, and are returned to storage next time it's loaded.</remarks>
        public void Reset()
        {
            this.JobList.Clear();
            this.NextJobNumber = 1;
            this.TicksSinceOrphanCheck = 0;
        }

        /// <summary>Whether a machine claim belongs to a job that's still running.</summary>
        public bool IsLiveClaim(string token)
        {
            return !string.IsNullOrEmpty(token)
                && this.JobList.Any(job => job.Token == token && job.Status is not (JobStatus.Complete or JobStatus.Cancelled));
        }

        /// <summary>Advances every running job by one scheduler tick.</summary>
        public void Run()
        {
            if (++this.TicksSinceOrphanCheck >= 10)
            {
                this.TicksSinceOrphanCheck = 0;
                this.ReturnOrphanedBuffers();
            }

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

                this.RefreshCropReservations(job);
                this.CollectFinished(job, network);
                this.StartWork(job, network);
                this.ApplyFairyDust(job, network);

                if (job.Steps.All(step => step.IsComplete))
                {
                    job.Status = JobStatus.Complete;
                    Log.Debug($"{job.Id} finished: {job.TargetCount}x {job.DisplayName}.");

                    // Anything left over -- a bulk machine's extra output, an intermediate made in whole batches
                    // -- is the network's again.
                    job.Buffer?.ReturnTo(network);
                    foreach (JobStep step in job.Steps)
                        this.ReleaseReservations(job, step);
                }
            }
        }


        /*********
        ** Private methods: planning to steps
        *********/
        /// <summary>Withdraws everything a plan takes from storage into the job's buffer.</summary>
        /// <returns>Whether all of it was there. It will be unless storage changed between planning and now.</returns>
        private static bool Reserve(CraftJob job, CraftPlan plan, StorageNetwork network, out string shortfall)
        {
            shortfall = null;

            // The planner has already decided which node draws what from stock; summing those gives exactly the
            // materials it counted on, coal and intermediates already on the shelf included.
            // Grouped by quality as well, so iridium wool the plan set aside for its own recipe is taken as
            // iridium, and everything else takes the lowest quality first.
            var wanted = plan.Root
                .Walk()
                .Where(node => node.FromStock > 0 && !string.IsNullOrEmpty(node.ItemId))
                .GroupBy(node => (Id: node.ItemId.ToLowerInvariant(), node.RequiredQuality))
                .Select(group => (ItemId: group.First().ItemId, Quality: group.Key.RequiredQuality, Count: group.Sum(node => node.FromStock)))
                .ToList();

            foreach ((string itemId, int quality, int count) in wanted)
            {
                int got = 0;
                foreach (Item item in network.ExtractById(itemId, count, quality))
                {
                    got += item.Stack;
                    job.Buffer.Add(item);
                }

                if (got < count)
                {
                    shortfall = $"{count - got}x {GetName(itemId)} missing";
                    return false;
                }
            }

            return true;
        }

        /// <summary>Returns buffers whose job no longer exists to the network they came from.</summary>
        /// <remarks>
        /// Jobs live only as long as the session, but their buffers are in the save. Loading a save with reserved
        /// items -- or cancelling a job while storage was full -- leaves a buffer with no job, and this is what
        /// gives its contents back.
        /// </remarks>
        private void ReturnOrphanedBuffers()
        {
            HashSet<string> live = new(this.JobList.Select(job => job.Buffer?.Key).Where(key => key != null), StringComparer.Ordinal);

            foreach (string key in Game1.player.team.globalInventories.Keys.Where(JobBuffer.IsBufferKey).ToList())
            {
                if (live.Contains(key))
                    continue;

                JobBuffer buffer = JobBuffer.FromKey(key);
                if (!buffer.TryGetOrigin(out string locationName, out Vector2 tile))
                    continue;

                GameLocation location = Game1.getLocationFromName(locationName);
                if (location == null)
                    continue;

                StorageNetwork network = this.Networks.GetNetworkAt(location, tile) ?? this.Networks.GetNetworks(location).FirstOrDefault();
                if (network == null)
                    continue;

                if (buffer.ReturnTo(network))
                    Log.Debug($"Returned items reserved by an earlier autocrafting job to the {locationName} network.");
            }
        }

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
                            DeliversToStorage = node == plan.Root,
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
                    DeliversToStorage = node == plan.Root,
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
                        // Gone without the removal being seen (removal is normally handled as it happens). Take
                        // back what went in and run it again elsewhere.
                        foreach (Item input in batch.Inputs)
                            job.Buffer?.Add(input);
                        step.InFlight.Remove(batch);
                        step.RemainingBatches++;
                        job.MachineLost = true;
                        continue;
                    }

                    if (step.MachineRecipe?.IsAging == true)
                    {
                        this.CheckAging(job, step, batch, machine, network);
                        continue;
                    }

                    // Finished, and still holding its output: the normal path.
                    if (machine.readyForHarvest.Value && machine.heldObject.Value != null)
                    {
                        this.Collect(machine, job, step, batch, network);
                        continue;
                    }

                    batch.MinutesLeft = Math.Max(0, machine.MinutesUntilReady);

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

        /// <summary>Collects an aging run once it reaches its quality, and keeps its time left up to date.</summary>
        private void CheckAging(CraftJob job, JobStep step, RunningBatch batch, SObject machine, StorageNetwork network)
        {
            int target = step.MachineRecipe.TargetQuality;
            SObject held = machine.heldObject.Value;

            // Emptied early: a player hitting the cask pops the item out. It's theirs now, so run the batch again
            // with another from the buffer or storage.
            if (held == null)
            {
                machine.modData.Remove(ModIds.JobKey);
                step.InFlight.Remove(batch);
                step.RemainingBatches++;
                Log.Trace($"{job.Id}: a cask was emptied before its item reached {Quality.Name(target)}; aging another.");
                return;
            }

            if (held.Quality >= target)
            {
                this.Collect(machine, job, step, batch, network);
                return;
            }

            if (machine is StardewValley.Objects.Cask cask)
                batch.MinutesLeft = MachineRecipeIndex.AgingDaysLeft(cask, target) * CraftPlan.MinutesPerDay;
        }

        /// <summary>Takes a job's item back out of a cask the player has struck, so it isn't dropped.</summary>
        /// <returns>Whether the cask was a job's, in which case its item is back in the job and the cask is empty.</returns>
        /// <remarks>
        /// The item is partly aged, so it goes back into the job rather than storage: the job ages that same item
        /// on in another cask, from the quality it reached, rather than starting a fresh one. The struck cask is
        /// left alone for the rest of the day -- striking it is how a player says they want it.
        /// </remarks>
        public bool ReclaimFromCask(StardewValley.Objects.Cask cask)
        {
            if (!StardewModdingAPI.Context.IsMainPlayer || cask?.heldObject.Value == null)
                return false;
            if (!cask.modData.TryGetValue(ModIds.JobKey, out string claim) || !this.IsLiveClaim(claim))
                return false;

            string locationName = cask.Location?.NameOrUniqueName;
            Microsoft.Xna.Framework.Vector2 tile = cask.TileLocation;
            CraftJob job = this.JobList.First(candidate => candidate.Token == claim);

            foreach (JobStep step in job.Steps.Where(step => step.MachineRecipe?.IsAging == true))
            {
                RunningBatch batch = step.InFlight.FirstOrDefault(candidate => candidate.LocationName == locationName && candidate.Tile == tile);
                if (batch == null)
                    continue;

                SObject held = cask.heldObject.Value;
                job.Buffer?.Add(held);

                cask.heldObject.Value = null;
                cask.readyForHarvest.Value = false;
                cask.minutesUntilReady.Value = 0;
                cask.modData.Remove(ModIds.JobKey);

                step.InFlight.Remove(batch);
                step.RemainingBatches++;

                if (job.ExcludedDay != Game1.Date.TotalDays)
                    job.Excluded.Clear();
                job.ExcludedDay = Game1.Date.TotalDays;
                job.Excluded.Add((locationName, tile));

                Log.Debug($"{job.Id}: the player struck a cask aging {held.DisplayName} ({Quality.Name(held.Quality)}); it's back in the job and will age on in another cask.");
                return true;
            }

            return false;
        }

        /// <summary>Growing crops available to plan with: the network's forecast, less what jobs have already reserved.</summary>
        public List<IncomingCrop> GetIncoming(StorageNetwork network)
        {
            List<IncomingCrop> crops = this.Forecast?.Invoke(network) ?? new List<IncomingCrop>();
            return crops.Where(crop => this.GetReservation(crop.Location, crop.Tile) == null).ToList();
        }

        /// <summary>The job that has reserved a growing crop, if one has.</summary>
        public CraftJob GetReservation(GameLocation location, Microsoft.Xna.Framework.Vector2 tile)
        {
            return this.JobList.FirstOrDefault(job =>
                job.Status is not (JobStatus.Complete or JobStatus.Cancelled)
                && job.CropReservations.Any(crop => crop.Location == location && crop.Tile == tile));
        }

        /// <summary>Takes a reserved crop's harvest for its job, if it's reserved.</summary>
        /// <returns>The job's buffer to put the harvest in, or <c>null</c> if no job reserved the crop.</returns>
        public JobBuffer ClaimHarvest(GameLocation location, Microsoft.Xna.Framework.Vector2 tile)
        {
            CraftJob job = this.GetReservation(location, tile);
            if (job?.Buffer == null)
                return null;

            job.CropReservations.RemoveAll(crop => crop.Location == location && crop.Tile == tile);
            Log.Trace($"{job.Id}: harvested a reserved crop at {tile}; {job.CropReservations.Count} still growing.");
            return job.Buffer;
        }

        /// <summary>Drops reservations on crops that are gone, and updates how long the job waits on the rest.</summary>
        /// <remarks>
        /// A reserved crop can die, be cleared, or be dug up. Its reservation is dropped rather than waited on
        /// forever; the step that needed it then draws from storage, or says it's short.
        /// </remarks>
        private void RefreshCropReservations(CraftJob job)
        {
            int longest = 0;

            foreach (IncomingCrop crop in job.CropReservations.ToList())
            {
                int? days = crop.Location.terrainFeatures.TryGetValue(crop.Tile, out StardewValley.TerrainFeatures.TerrainFeature feature)
                    && feature is StardewValley.TerrainFeatures.HoeDirt { crop: not null } soil
                    && !soil.crop.dead.Value
                    && string.Equals(ItemRegistry.QualifyItemId(soil.crop.indexOfHarvest.Value ?? ""), crop.ItemId, StringComparison.OrdinalIgnoreCase)
                        ? CropMath.DaysUntilHarvest(soil)
                        : null;

                if (days == null)
                {
                    job.CropReservations.Remove(crop);
                    Log.Debug($"{job.Id}: a reserved {StockId.GetDisplayName(crop.ItemId)} crop at {crop.Tile} is gone; the job will look to storage instead.");
                    continue;
                }

                longest = Math.Max(longest, days.Value);
            }

            job.HarvestWaitMinutes = longest <= 0 ? 0 : Utility.CalculateMinutesUntilMorning(Game1.timeOfDay, longest);
        }

        /// <summary>Says what a job is waiting on from the fields.</summary>
        private static string DescribeCropWait(CraftJob job)
        {
            IncomingCrop last = job.CropReservations.OrderByDescending(crop => crop.Days).First();
            int days = Math.Max(0, (int)Math.Ceiling(job.HarvestWaitMinutes / (double)CraftPlan.MinutesPerDay));
            return days == 0
                ? $"waiting for {job.CropReservations.Count} {StockId.GetDisplayName(last.ItemId)} to be harvested today"
                : $"waiting for {job.CropReservations.Count} {StockId.GetDisplayName(last.ItemId)} to grow ({days}d)";
        }

        /// <summary>Handles a machine being removed from the world, if a job was using it.</summary>
        /// <returns>Whether the machine belonged to a job, in which case the job has dealt with its contents.</returns>
        /// <remarks>
        /// Breaking a working machine destroys what's in it. For a job's machine the exact inputs are known, so
        /// they go back into the job's buffer and the run is started again on another machine. A run that had
        /// already finished gives its product instead. If no machine of that kind is left, the job is cancelled
        /// on the next scheduler pass and everything it holds returns to storage.
        /// </remarks>
        public bool HandleMachineRemoved(GameLocation location, Microsoft.Xna.Framework.Vector2 tile, SObject machine)
        {
            string locationName = location?.NameOrUniqueName;
            if (locationName == null || machine == null)
                return false;

            foreach (CraftJob job in this.JobList)
            {
                if (job.Status is JobStatus.Complete or JobStatus.Cancelled)
                    continue;

                foreach (JobStep step in job.Steps)
                {
                    // A machine held for later: just forget it.
                    if (step.Reserved.RemoveAll(reserved => reserved.Location == locationName && reserved.Tile == tile) > 0)
                    {
                        job.MachineLost = true;
                        return true;
                    }

                    RunningBatch batch = step.InFlight.FirstOrDefault(candidate => candidate.LocationName == locationName && candidate.Tile == tile);
                    if (batch == null)
                        continue;

                    SObject held = machine.heldObject.Value;
                    bool aging = step.MachineRecipe?.IsAging == true;

                    if (!aging && machine.readyForHarvest.Value && held != null)
                    {
                        // Finished: keep the product and count the run.
                        StorageNetwork network = this.ResolveNetwork(job);
                        if (network != null)
                            this.Deliver(held, job, step, network);
                        else
                            job.Buffer?.Add(held);
                        step.CompletedBatches++;
                    }
                    else
                    {
                        // Unfinished: the inputs come back and the run goes again. A cask's item is the input.
                        if (aging)
                        {
                            if (held != null)
                                job.Buffer?.Add(held);
                        }
                        else
                        {
                            foreach (Item input in batch.Inputs)
                                job.Buffer?.Add(input);
                        }
                        step.RemainingBatches++;
                    }

                    step.InFlight.Remove(batch);
                    job.MachineLost = true;
                    Log.Debug($"{job.Id}: a {machine.DisplayName} it was using was removed; its run has been returned to the job.");
                    return true;
                }
            }

            return false;
        }

        /// <summary>Forgets one reserved machine that's been put to other use.</summary>
        /// <remarks>The claim is only removed if none of the job's own runs is using the machine.</remarks>
        private void DropReservation(CraftJob job, JobStep step, NetworkNode node, StorageNetwork network)
        {
            string here = (node.Location ?? network.Location)?.NameOrUniqueName;
            if (step.Reserved.RemoveAll(reserved => reserved.Location == here && reserved.Tile == node.Tile) == 0)
                return;

            bool running = job.Steps.Any(other => other.InFlight.Any(batch => batch.LocationName == here && batch.Tile == node.Tile));
            if (!running && node.Object.modData.TryGetValue(ModIds.JobKey, out string claim) && claim == job.Token)
                node.Object.modData.Remove(ModIds.JobKey);
        }

        /// <summary>Lets go of machines a step claimed ahead of time and no longer needs.</summary>
        private void ReleaseReservations(CraftJob job, JobStep step)
        {
            foreach ((string locationName, Microsoft.Xna.Framework.Vector2 tile) in step.Reserved)
            {
                SObject machine = this.FindMachine(new RunningBatch { LocationName = locationName, Tile = tile });
                if (machine != null && machine.heldObject.Value == null
                    && machine.modData.TryGetValue(ModIds.JobKey, out string claim) && claim == job.Token)
                    machine.modData.Remove(ModIds.JobKey);
            }

            step.Reserved.Clear();
        }

        /// <summary>Takes a finished machine's output and closes off the batch.</summary>
        /// <remarks>
        /// The ordered item goes to storage; anything else is an ingredient for a later step, and stays in the
        /// job's buffer where nothing else can take it.
        /// </remarks>
        private void Collect(SObject machine, CraftJob job, JobStep step, RunningBatch batch, StorageNetwork network)
        {
            this.Deliver(machine.heldObject.Value, job, step, network);

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

                step.WaitReason = null;
                step.IsStuck = false;

                didSomething |= step.Kind == PlanStepKind.Craft
                    ? this.RunCraftStep(job, step, network)
                    : this.RunProcessStep(job, step, network);
            }

            if (job.Status == JobStatus.Complete)
                return;

            // A step with nothing left to reserve machines for lets them go.
            foreach (JobStep step in job.Steps.Where(step => step.RemainingBatches <= 0 && step.Reserved.Count > 0))
                this.ReleaseReservations(job, step);

            // A machine the job was using was removed, and now a step has no machine left to run on at all.
            // Waiting would wait forever, so give everything back instead.
            if (job.MachineLost)
            {
                job.MachineLost = false;
                JobStep stranded = job.Steps.FirstOrDefault(step => step.RemainingBatches > 0 && step.NoMachines && step.InFlight.Count == 0);
                if (stranded != null)
                {
                    string machineName = stranded.MachineRecipe?.MachineName ?? "machine";
                    Log.Debug($"{job.Id} cancelled: the last {machineName} it could use was removed. Its materials are back in storage.");
                    Game1.addHUDMessage(new HUDMessage($"{job.DisplayName}: cancelled, no {machineName} left. Materials returned.", HUDMessage.error_type));
                    this.Cancel(job.Id);
                    return;
                }
            }

            if (didSomething || job.Steps.Any(step => step.InFlight.Count > 0))
            {
                job.Status = JobStatus.Running;
                job.BlockedReason = null;
                return;
            }

            // Nothing is running and nothing could start. Say why, using the first step with work left: later
            // steps are only waiting on it. Ordinary queuing -- every keg busy -- is a wait, not a fault; only a
            // step that can't go on without the player doing something counts as blocked.
            JobStep next = job.Steps.FirstOrDefault(step => step.RemainingBatches > 0);
            job.Status = next?.IsStuck == true ? JobStatus.Blocked : JobStatus.Waiting;
            job.BlockedReason = next?.WaitReason;
        }

        /// <summary>Runs as many crafting batches as the materials allow. Crafting is instant.</summary>
        /// <remarks>
        /// Ingredients come from the job's buffer only. The game's own <c>consumeIngredients</c> isn't used,
        /// because it always takes from the player's backpack first -- an autocraft job would quietly spend what
        /// the player was carrying.
        /// </remarks>
        private bool RunCraftStep(CraftJob job, JobStep step, StorageNetwork network)
        {
            bool any = false;

            while (step.RemainingBatches > 0)
            {
                // Normally already reserved; this only matters if something went missing along the way. Not while
                // crops are still coming: that stock isn't this job's.
                if (job.CropReservations.Count == 0)
                {
                    foreach ((string ingredient, int required) in step.CraftRecipe.recipeList)
                    {
                        if (!ingredient.StartsWith("-"))
                            job.Buffer.EnsureHas(QualifyOrSelf(ingredient), required, network);
                    }
                }

                if (!job.Buffer.HasIngredientsFor(step.CraftRecipe))
                {
                    if (!any)
                    {
                        if (job.CropReservations.Count > 0)
                            step.WaitReason = DescribeCropWait(job);
                        else if (HasEarlierWork(job, step))
                            step.WaitReason = "waiting for an earlier step";
                        else
                        {
                            step.WaitReason = $"missing ingredients for {step.DisplayName}";
                            step.IsStuck = true;
                        }
                    }
                    break;
                }

                try
                {
                    job.Buffer.ConsumeIngredientsFor(step.CraftRecipe);
                    Item product = step.CraftRecipe.createItem();
                    if (product == null)
                        break;

                    this.Deliver(product, job, step, network);
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
            {
                step.WaitReason = "its recipe no longer exists";
                step.IsStuck = true;
                return false;
            }

            // At its machine limit, so it's waiting on its own runs; the job shows as running.
            int allowance = step.MaxMachines > 0 ? step.MaxMachines - step.InFlight.Count : int.MaxValue;
            if (allowance <= 0)
                return false;

            bool any = false;
            int usable = 0;
            int free = 0;
            ItemCost? missing = null;

            foreach (NetworkNode node in network.Machines)
            {
                if (allowance <= 0 || step.RemainingBatches <= 0)
                    break;

                SObject machine = node.Object;
                if (machine == null || !string.Equals(machine.QualifiedItemId, recipe.MachineId, StringComparison.OrdinalIgnoreCase))
                    continue;

                // The player has told this one not to take this input.
                // Filters are set on the item, not a flavour of it, so match on the base ID.
                if (!node.AcceptsInput(StockId.BaseId(recipe.InputId)))
                    continue;

                // A cask where aging isn't allowed is wired up but useless.
                if (!MachineIO.IsOperable(machine))
                    continue;

                usable++;

                // One the player took this job's item out of today.
                if (this.IsExcluded(job, (node.Location ?? network.Location)?.NameOrUniqueName, node.Tile))
                    continue;

                // Busy, or claimed by another job that's still running. A claim left by a job that's gone is
                // ignored, or one reload would take the machine out of service for good; this job's own claim is
                // a machine it reserved, and is exactly the one to use.
                if (machine.heldObject.Value != null || machine.MinutesUntilReady > 0)
                {
                    // A machine this step was holding has been put to other use -- by the player's hand, say. Let
                    // it go, or the network would never collect whatever the player put in it.
                    this.DropReservation(job, step, node, network);
                    continue;
                }
                if (machine.modData.TryGetValue(ModIds.JobKey, out string claim) && claim != job.Token && this.IsLiveClaim(claim))
                    continue;

                free++;

                // While an earlier step is still making this one's input, wait for it rather than taking some from
                // storage: that stock isn't this job's.
                bool inputComing = HasEarlierWork(job, step);
                bool waitingOnCrops = job.CropReservations.Count > 0;

                if (!this.TryLoadMachine(machine, recipe, job.Buffer, inputComing || waitingOnCrops ? null : network, out int minutes, out List<Item> consumed, out ItemCost? lacking))
                {
                    missing ??= lacking;

                    // Hold the machine for when an earlier step's output arrives, so another job can't take it
                    // meanwhile. Not for crops: holding a keg idle for the days a crop takes to grow would only
                    // keep it from everyone else.
                    string here = (node.Location ?? network.Location)?.NameOrUniqueName;
                    if (inputComing && here != null
                        && step.Reserved.Count < Math.Min(step.RemainingBatches, allowance)
                        && !step.Reserved.Any(reserved => reserved.Location == here && reserved.Tile == node.Tile))
                    {
                        machine.modData[ModIds.JobKey] = job.Token;
                        step.Reserved.Add((here, node.Tile));
                    }
                    continue;
                }

                // Now running, so no longer merely held -- by this step or any other in the job.
                string loaded = (node.Location ?? network.Location)?.NameOrUniqueName;
                foreach (JobStep other in job.Steps)
                    other.Reserved.RemoveAll(reserved => reserved.Location == loaded && reserved.Tile == node.Tile);
                machine.modData[ModIds.JobKey] = job.Token;
                step.InFlight.Add(new RunningBatch
                {
                    // The machine's own location: on a wirelessly linked network it may be the cellar, not here.
                    LocationName = (node.Location ?? network.Location)?.NameOrUniqueName,
                    Tile = node.Tile,
                    MinutesLeft = minutes,
                    Yield = recipe.OutputCount,
                    Inputs = consumed,

                    // An aging run is measured against the whole climb from normal, so a silver wine going back in
                    // shows as already a quarter done rather than starting the bar again.
                    ExpectedMinutes = recipe.IsAging ? Math.Max(minutes, recipe.Days * CraftPlan.MinutesPerDay) : minutes
                });

                step.RemainingBatches--;
                allowance--;
                any = true;
            }

            if (!any)
                this.ExplainIdleProcessStep(job, step, recipe, usable, free, missing);

            return any;
        }

        /// <summary>Records why a processing step couldn't start a run.</summary>
        private void ExplainIdleProcessStep(CraftJob job, JobStep step, MachineRecipe recipe, int usable, int free, ItemCost? missing)
        {
            step.NoMachines = usable == 0;

            if (usable == 0)
            {
                // Removed, or every one filtered against this input. Nothing will change until the player acts.
                step.WaitReason = recipe.IsAging
                    ? $"no {recipe.MachineName} on the network is somewhere it can age things"
                    : $"no {recipe.MachineName} on the network will take {StockId.GetDisplayName(recipe.InputId)}";
                step.IsStuck = true;
            }
            else if (free == 0)
            {
                step.WaitReason = $"ingredients reserved, waiting for a free {recipe.MachineName}";
            }
            else if (missing is ItemCost lacking)
            {
                // A crop still growing, or an earlier step still to finish, is where the ingredient is coming from;
                // otherwise it's gone.
                if (job.CropReservations.Count > 0 && !HasEarlierWork(job, step))
                    step.WaitReason = DescribeCropWait(job);
                else if (HasEarlierWork(job, step))
                {
                    step.WaitReason = $"waiting for {StockId.GetDisplayName(lacking.ItemId)} from an earlier step";
                    if (step.Reserved.Count > 0)
                        step.WaitReason += $"; {step.Reserved.Count} {recipe.MachineName} reserved";
                }
                else
                {
                    step.WaitReason = $"missing {lacking.Count}x {StockId.GetDisplayName(lacking.ItemId)}";
                    step.IsStuck = true;
                }
            }
            else
            {
                step.WaitReason = $"couldn't make {recipe.OutputName}";
                step.IsStuck = true;
            }
        }

        /// <summary>Whether a step comes after one that still has work to do.</summary>
        private static bool HasEarlierWork(CraftJob job, JobStep step)
        {
            return job.Steps.TakeWhile(candidate => candidate != step).Any(candidate => !candidate.IsComplete);
        }

        /// <summary>Takes a run's inputs from storage and sets the machine working.</summary>
        /// <returns>Whether the machine was loaded.</returns>
        /// <param name="network">Where a shortfall may be drawn from, or <c>null</c> to use only what the job holds.</param>
        private bool TryLoadMachine(SObject machine, MachineRecipe recipe, JobBuffer buffer, StorageNetwork network, out int minutes, out List<Item> consumed, out ItemCost? missing)
        {
            minutes = 0;
            consumed = new List<Item>();
            missing = null;

            if (recipe.IsAging)
                return this.TryStartAging(machine, recipe, buffer, network, out minutes, out consumed, out missing);

            List<ItemCost> inputs = recipe.GetAllInputs().ToList();

            // Check everything is in the job's buffer before taking any of it, so a partial load can't strand
            // materials. They were reserved when the job was queued, so this only draws on storage if something
            // went astray since.
            foreach (ItemCost input in inputs)
            {
                if (!buffer.EnsureHas(input.ItemId, input.Count, network, input.RequiredQuality))
                {
                    missing = input;
                    return false;
                }
            }

            // A copy of what the game's own machine code made when the recipe was indexed, so a wine comes out
            // named, coloured and priced for its fruit.
            Item output;
            try
            {
                output = recipe.CreateOutput();
            }
            catch
            {
                output = null;
            }

            if (output is not SObject product)
                return false;

            // Kept with the run, so cancelling can hand back exactly these.
            foreach (ItemCost input in inputs)
                consumed.AddRange(buffer.Take(input.ItemId, input.Count, input.RequiredQuality));

            // Set the machine to the outcome the recipe index already worked out. The game's clock counts
            // minutesUntilReady down and raises readyForHarvest on its own from here.
            // A rule measured in days finishes at 6am, the way the game schedules it: a dehydrator loaded at
            // noon is ready tomorrow morning, not at noon tomorrow.
            minutes = recipe.Days > 0
                ? Utility.CalculateMinutesUntilMorning(Game1.timeOfDay, recipe.Days)
                : recipe.Minutes;
            minutes = Math.Max(10, minutes);

            machine.heldObject.Value = product;
            machine.minutesUntilReady.Value = minutes;
            machine.readyForHarvest.Value = false;

            MachineData data = machine.GetMachineData();
            machine.showNextIndex.Value = data?.ShowNextIndexWhileWorking ?? false;

            return true;
        }


        /*********
        ** Private methods: lookup
        *********/
        /// <summary>Puts one item from the job into a cask to age towards the step's quality.</summary>
        private bool TryStartAging(SObject machine, MachineRecipe recipe, JobBuffer buffer, StorageNetwork network, out int minutes, out List<Item> consumed, out ItemCost? missing)
        {
            minutes = 0;
            consumed = new List<Item>();
            missing = null;

            Item input = buffer.TakeBestBelow(recipe.InputId, recipe.TargetQuality);
            if (input == null && network != null && buffer.EnsureHas(recipe.InputId, 1, network))
                input = buffer.TakeBestBelow(recipe.InputId, recipe.TargetQuality);

            if (input == null)
            {
                missing = new ItemCost(recipe.InputId, 1);
                return false;
            }

            if (!this.MachineRecipes.StartAging(machine, input))
            {
                buffer.Add(input);
                return false;
            }

            consumed.Add(input);
            minutes = MachineRecipeIndex.AgingDays(input.Quality, recipe.TargetQuality, recipe.AgingRate) * CraftPlan.MinutesPerDay;
            return true;
        }

        /// <summary>Whether the player has taken this job's item out of a machine today.</summary>
        private bool IsExcluded(CraftJob job, string locationName, Microsoft.Xna.Framework.Vector2 tile)
        {
            if (job.Excluded.Count == 0)
                return false;

            if (job.ExcludedDay != Game1.Date.TotalDays)
            {
                job.Excluded.Clear();
                return false;
            }

            return job.Excluded.Any(excluded => excluded.Location == locationName && excluded.Tile == tile);
        }

        /// <summary>Sets aside the Fairy Dust a job could use from storage.</summary>
        /// <remarks>
        /// One per run on a machine that takes dust, and one per quality level for a cask run, since each use only
        /// moves a cask's item up a level. Counted from normal quality, so it may be more than a job ends up using;
        /// what's left over returns with everything else when the job finishes.
        /// </remarks>
        private void ReserveFairyDust(CraftJob job, StorageNetwork network)
        {
            int wanted = job.Steps
                .Where(step => step.Kind == PlanStepKind.Process && step.MachineRecipe != null && this.MachineRecipes.AllowsFairyDust(step.MachineRecipe.MachineId))
                .Sum(step => step.RemainingBatches * (step.MachineRecipe.IsAging ? Quality.Steps(SObject.lowQuality, step.MachineRecipe.TargetQuality) : 1));

            int available = (int)Math.Min(int.MaxValue, network.CountById(FairyDustId));
            int take = Math.Min(wanted, available);
            if (take <= 0)
                return;

            foreach (Item dust in network.ExtractById(FairyDustId, take))
                job.Buffer.Add(dust);

            Log.Debug($"{job.Id}: set aside {take} Fairy Dust for {wanted} possible uses.");
        }

        /// <summary>Uses Fairy Dust on the job's running machines, if the job has it switched on.</summary>
        /// <remarks>
        /// Uses the game's own <c>TryApplyFairyDust</c>, the same as sprinkling it by hand, so each machine behaves
        /// as it would for the player: most finish their run, a cask moves its item up one quality. Dust comes
        /// from what the job set aside, then from storage. A cask gets one a tick until it reaches the job's
        /// quality; any other machine one per run.
        /// </remarks>
        private void ApplyFairyDust(CraftJob job, StorageNetwork network)
        {
            if (!job.UseFairyDust || job.Status is JobStatus.Complete or JobStatus.Cancelled)
                return;

            foreach (JobStep step in job.Steps.Where(step => step.Kind == PlanStepKind.Process && step.MachineRecipe != null))
            {
                bool aging = step.MachineRecipe.IsAging;

                foreach (RunningBatch batch in step.InFlight)
                {
                    if (!aging && batch.Dusted)
                        continue;

                    SObject machine = this.FindMachine(batch);
                    if (machine == null)
                        continue;
                    if (aging && (machine.heldObject.Value == null || machine.heldObject.Value.Quality >= step.MachineRecipe.TargetQuality))
                        continue;
                    if (!machine.TryApplyFairyDust(probe: true))
                        continue;

                    Item dust = job.Buffer.Take(FairyDustId, 1).FirstOrDefault()
                        ?? network.ExtractById(FairyDustId, 1).FirstOrDefault();
                    if (dust == null)
                        return; // out of dust; nothing more to do this tick

                    if (machine.TryApplyFairyDust(probe: false))
                        batch.Dusted = true;
                    else
                        job.Buffer.Add(dust);
                }
            }
        }

        /// <summary>Puts a step's output where it belongs: storage for the ordered item, the buffer for the rest.</summary>
        /// <remarks>If storage is full, the ordered item waits in the buffer too, rather than being lost.</remarks>
        private void Deliver(Item product, CraftJob job, JobStep step, StorageNetwork network)
        {
            if (product == null)
                return;

            if (step.DeliversToStorage)
                network.Insert(product);

            if (product.Stack > 0)
                job.Buffer.Add(product);
        }

        /// <summary>Qualifies a crafting ingredient ID, leaving it as-is if it doesn't resolve.</summary>
        private static string QualifyOrSelf(string itemId)
        {
            try
            {
                return ItemRegistry.QualifyItemId(itemId) ?? itemId;
            }
            catch
            {
                return itemId;
            }
        }

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

        /// <summary>The display name for a stock ID.</summary>
        private static string GetName(string qualifiedId) => StockId.GetDisplayName(qualifiedId);
    }
}
