using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>What an autocrafting job is currently doing.</summary>
    internal enum JobStatus
    {
        /// <summary>Waiting for the scheduler to pick it up.</summary>
        Pending,

        /// <summary>Machines are running or crafts are being made.</summary>
        Running,

        /// <summary>Everything requested has been produced.</summary>
        Complete,

        /// <summary>Stalled: something it needs can't be supplied.</summary>
        Blocked,

        /// <summary>Nothing wrong, just queued behind something: busy machines, or an earlier step still running.</summary>
        Waiting,

        /// <summary>Stopped by the player.</summary>
        Cancelled
    }

    /// <summary>One machine run that has been started and not yet collected.</summary>
    internal class RunningBatch
    {
        /// <summary>The location holding the machine.</summary>
        public string LocationName { get; init; }

        /// <summary>The machine's tile.</summary>
        public Microsoft.Xna.Framework.Vector2 Tile { get; init; }

        /// <summary>How many in-game minutes the run was expected to take.</summary>
        public int ExpectedMinutes { get; init; }

        /// <summary>How many in-game minutes the machine had left when last checked.</summary>
        public int MinutesLeft { get; set; }

        /// <summary>How many of the output this run will yield.</summary>
        public int Yield { get; init; }

        /// <summary>Whether Fairy Dust has already been used on this run. A cask is the exception: it takes one per quality.</summary>
        public bool Dusted { get; set; }

        /// <summary>The exact items put into the machine, kept so cancelling can give them back.</summary>
        /// <remarks>The real items rather than a recipe's list, so a gold Starfruit comes back gold.</remarks>
        public List<Item> Inputs { get; init; } = new();
    }

    /// <summary>One stage of a job: make N of something, either by crafting or by running a machine.</summary>
    /// <remarks>
    /// Steps come from flattening a <see cref="CraftPlan"/> deepest-first, so a step's inputs are always produced
    /// by steps earlier in the list. The scheduler walks them in that order and only advances a step once the
    /// things it depends on exist.
    /// </remarks>
    internal class JobStep
    {
        /*********
        ** Accessors
        *********/
        /// <summary>Whether this step crafts or processes.</summary>
        public PlanStepKind Kind { get; init; }

        /// <summary>The qualified item ID this step produces.</summary>
        public string OutputId { get; init; }

        /// <summary>The item's display name.</summary>
        public string DisplayName { get; init; }

        /// <summary>The crafting recipe, for a craft step.</summary>
        public CraftingRecipe CraftRecipe { get; init; }

        /// <summary>The machine recipe, for a processing step.</summary>
        public MachineRecipe MachineRecipe { get; init; }

        /// <summary>How many runs are still to be started.</summary>
        public int RemainingBatches { get; set; }

        /// <summary>How many runs have finished and been collected.</summary>
        public int CompletedBatches { get; set; }

        /// <summary>The total number of runs this step began with.</summary>
        public int TotalBatches { get; init; }

        /// <summary>Why the step couldn't start anything on its last attempt, for the Jobs tab.</summary>
        public string WaitReason { get; set; }

        /// <summary>Whether the step has no machine on the network that could run it at all.</summary>
        public bool NoMachines { get; set; }

        /// <summary>Idle machines this step has claimed ahead of its inputs arriving, by location and tile.</summary>
        /// <remarks>
        /// While a keg works on the wine, the cask it will go into is held for it, so another job can't take the
        /// cask in the meantime. Claims are made as machines come free -- including casks placed after the job
        /// was queued -- and released when the step no longer needs them.
        /// </remarks>
        public List<(string Location, Microsoft.Xna.Framework.Vector2 Tile)> Reserved { get; } = new();

        /// <summary>Whether that reason is a real problem rather than ordinary queuing.</summary>
        public bool IsStuck { get; set; }

        /// <summary>Whether this step makes the item the job was ordered for, so its output goes to storage.</summary>
        /// <remarks>Every other step makes something the job needs next, which stays in the job's buffer.</remarks>
        public bool DeliversToStorage { get; init; }

        /// <summary>How many machines a step may occupy at once; zero for no limit.</summary>
        /// <remarks>Zero means no limit beyond however many the network has.</remarks>
        public int MaxMachines { get; set; }

        /// <summary>Runs currently under way.</summary>
        public List<RunningBatch> InFlight { get; } = new();

        /// <summary>Whether every run has finished.</summary>
        public bool IsComplete => this.RemainingBatches <= 0 && this.InFlight.Count == 0;

        /// <summary>How many of the output one run yields.</summary>
        public int YieldPerBatch => this.Kind == PlanStepKind.Process
            ? Math.Max(1, this.MachineRecipe?.OutputCount ?? 1)
            : Math.Max(1, this.CraftRecipe?.numberProducedPerCraft ?? 1);

        /// <summary>In-game minutes one run takes.</summary>
        public int MinutesPerBatch => this.Kind == PlanStepKind.Process
            ? (this.MachineRecipe?.Minutes ?? 0) + ((this.MachineRecipe?.Days ?? 0) * CraftPlan.MinutesPerDay)
            : 0;
    }

    /// <summary>A request to make a number of something, and the steps that will get there.</summary>
    internal class CraftJob
    {
        /*********
        ** Accessors
        *********/
        /// <summary>A short identifier, used to mark the machines this job has claimed.</summary>
        public string Id { get; set; }

        /// <summary>The qualified item ID being made.</summary>
        public string TargetId { get; init; }

        /// <summary>The target's display name.</summary>
        public string DisplayName { get; init; }

        /// <summary>How many were asked for.</summary>
        public int TargetCount { get; init; }

        /// <summary>The location whose network this job runs on.</summary>
        public string LocationName { get; init; }

        /// <summary>A cable tile on that network, so the right one is found again if the location has several.</summary>
        public Microsoft.Xna.Framework.Vector2 AnchorTile { get; init; }

        /// <summary>The steps, in the order they must run.</summary>
        public List<JobStep> Steps { get; init; } = new();

        /// <summary>What the job is doing.</summary>
        public JobStatus Status { get; set; } = JobStatus.Pending;

        /// <summary>Why the job is blocked, if it is.</summary>
        public string BlockedReason { get; set; }

        /// <summary>A value unique to this job across sessions, used to claim machines and name its buffer.</summary>
        /// <remarks>
        /// <see cref="Id"/> restarts at J1 every session, so a claim left on a keg by last session's J1 would
        /// otherwise look like it belonged to this session's.
        /// </remarks>
        public string Token { get; init; }

        /// <summary>The quality the job ages its product to, or <see cref="Quality.Any"/>.</summary>
        public int TargetQuality { get; init; } = Quality.Any;

        /// <summary>Crops still growing that this job has reserved, whose harvest comes to it instead of storage.</summary>
        public List<IncomingCrop> CropReservations { get; } = new();

        /// <summary>Seeds still to be planted on automation tiles for this job.</summary>
        public List<PlannedPlanting> Plantings { get; } = new();

        /// <summary>Whether the job is waiting on crops: growing, or still to be planted.</summary>
        public bool WaitingOnFields => this.CropReservations.Count > 0 || this.Plantings.Count > 0;

        /// <summary>In-game minutes the job was expected to take when it started, for its gold a day; -1 until known.</summary>
        public int PlannedMinutes { get; set; } = -1;

        /// <summary>How many of the ordered item have gone to storage so far.</summary>
        public int Delivered { get; set; }

        /// <summary>The fertilizer laid under crops planted for this job, or <c>null</c> for none.</summary>
        public string FertilizerId { get; init; }

        /// <summary>In-game minutes until the last reserved crop is ready, kept current by the scheduler.</summary>
        public int HarvestWaitMinutes { get; set; }

        /// <summary>Whether the job speeds its machines up with Fairy Dust.</summary>
        /// <remarks>Dust reserved when the job was queued is used first; after that, and for a job switched on later, from storage.</remarks>
        public bool UseFairyDust { get; set; }

        /// <summary>Machines the player has taken this job's item out of today, which it leaves alone until tomorrow.</summary>
        /// <remarks>Striking a cask is how a player takes it back. Refilling it straight away would undo that.</remarks>
        public List<(string Location, Microsoft.Xna.Framework.Vector2 Tile)> Excluded { get; } = new();

        /// <summary>The day <see cref="Excluded"/> applies to.</summary>
        public int ExcludedDay { get; set; }

        /// <summary>Whether a machine this job was using was removed since the scheduler last looked.</summary>
        public bool MachineLost { get; set; }

        /// <summary>The ingredients and intermediates this job has set aside.</summary>
        public StardewLogistics.Devices.JobBuffer Buffer { get; init; }

        /// <summary>The key of the minimum-stock rule that queued this job, or <c>null</c> if the player did.</summary>
        public string RuleKey { get; init; }

        /// <summary>Whether a minimum-stock rule queued this job, rather than the player.</summary>
        public bool FromStockRule => this.RuleKey != null;

        /// <summary>The host's figure for how far through the job is, on a farmhand, who has no steps to work it out from.</summary>
        public double? RemoteProgress { get; set; }

        /// <summary>The host's estimate of the minutes left, on a farmhand.</summary>
        public int? RemoteEta { get; set; }

        /// <summary>How far through the job is, from zero to one.</summary>
        public double Progress
        {
            get
            {
                if (this.RemoteProgress.HasValue)
                    return this.RemoteProgress.Value;

                int total = this.Steps.Sum(step => step.TotalBatches);
                if (total <= 0)
                    return this.Status == JobStatus.Complete ? 1 : 0;

                // Runs in flight count for how far along they are, so a week of wine moves the bar day by day
                // rather than sitting at zero -- and jumps when Fairy Dust hurries it along.
                double done = this.Steps.Sum(step => step.CompletedBatches + step.InFlight.Sum(batch => batch.ExpectedMinutes > 0
                    ? Math.Clamp(1 - (batch.MinutesLeft / (double)batch.ExpectedMinutes), 0, 1)
                    : 0));

                return Math.Clamp(done / total, 0, 1);
            }
        }

        /// <summary>The in-game minutes still expected, assuming the machines already assigned.</summary>
        /// <remarks>
        /// Runs in flight contribute what's left on the machine; runs not yet started contribute a full batch
        /// each, divided by how many can run at once. It's an estimate, and it gets better as the job proceeds.
        /// </remarks>
        public int EstimatedMinutesRemaining
        {
            get
            {
                if (this.RemoteEta.HasValue)
                    return this.RemoteEta.Value;

                int total = 0;

                foreach (JobStep step in this.Steps)
                {
                    if (step.IsComplete || step.MinutesPerBatch <= 0)
                        continue;

                    int parallel = Math.Max(1, step.MaxMachines > 0 ? step.MaxMachines : Math.Max(1, step.InFlight.Count));
                    int waves = (int)Math.Ceiling(step.RemainingBatches / (double)parallel);

                    total += waves * step.MinutesPerBatch;

                    // What the busiest machine actually has left, so the estimate counts down while a keg works
                    // rather than showing a full week until the wine comes out.
                    if (step.InFlight.Count > 0)
                        total += step.InFlight.Max(batch => batch.MinutesLeft);
                }

                // Nothing can start until the crops it's waiting on are harvested.
                return total + this.HarvestWaitMinutes;
            }
        }

        /// <summary>The step the scheduler should work on next, or <c>null</c> if everything is done.</summary>
        public JobStep NextStep => this.Steps.FirstOrDefault(step => !step.IsComplete);
    }
}
