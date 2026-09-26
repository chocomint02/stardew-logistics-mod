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
        public string Id { get; init; }

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

        /// <summary>The ingredients and intermediates this job has set aside.</summary>
        public StardewLogistics.Devices.JobBuffer Buffer { get; init; }

        /// <summary>Whether the player asked for this job, or a minimum-stock rule did.</summary>
        public bool FromStockRule { get; init; }

        /// <summary>How far through the job is, from zero to one.</summary>
        public double Progress
        {
            get
            {
                int total = this.Steps.Sum(step => step.TotalBatches);
                if (total <= 0)
                    return this.Status == JobStatus.Complete ? 1 : 0;

                return Math.Clamp(this.Steps.Sum(step => step.CompletedBatches) / (double)total, 0, 1);
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

                return total;
            }
        }

        /// <summary>The step the scheduler should work on next, or <c>null</c> if everything is done.</summary>
        public JobStep NextStep => this.Steps.FirstOrDefault(step => !step.IsComplete);
    }
}
