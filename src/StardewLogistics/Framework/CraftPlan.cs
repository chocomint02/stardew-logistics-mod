using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>How one item in a plan is to be supplied.</summary>
    internal enum PlanStepKind
    {
        /// <summary>Already in storage.</summary>
        FromStock,

        /// <summary>Made with a crafting recipe.</summary>
        Craft,

        /// <summary>Made by running a machine.</summary>
        Process,

        /// <summary>Can't be supplied: nothing in stock and no way to make it.</summary>
        Missing
    }

    /// <summary>Why a plan step couldn't be supplied.</summary>
    /// <remarks>
    /// "Missing" on its own is a useless diagnostic: not knowing a recipe, not owning the machine, and there
    /// being no recipe at all are three different problems with three different fixes.
    /// </remarks>
    internal enum MissingReason
    {
        /// <summary>Nothing is missing.</summary>
        None,

        /// <summary>A raw material the player simply hasn't got.</summary>
        NotEnoughStock,

        /// <summary>Nothing the player knows how to craft or process produces it.</summary>
        NoRecipe,

        /// <summary>A machine could make it, but none of that kind is on the network.</summary>
        NoMachineAvailable,

        /// <summary>The plan ran deeper than the configured limit.</summary>
        DepthLimit,

        /// <summary>The recipe graph looped back on itself.</summary>
        RecipeLoop,

        /// <summary>The recipe asked for a category rather than a specific item.</summary>
        NotAnItem,

        /// <summary>A crop that could be grown, but there aren't enough free automation tiles for it.</summary>
        NoFreeTiles,

        /// <summary>A crop with free automation tiles, but none where it would be ready before its season ends.</summary>
        CantGrowInTime
    }

    /// <summary>A share of a processing step handed to one kind of machine.</summary>
    /// <remarks>
    /// A step is split when more than one machine can do the job: a Heavy Furnace takes twenty-five ore and
    /// returns five bars, a plain one takes five and returns one, so an order for seven is best served by one
    /// Heavy run plus two plain ones.
    /// </remarks>
    internal class MachineAssignment
    {
        /// <summary>The recipe this share runs.</summary>
        public MachineRecipe Recipe { get; init; }

        /// <summary>How many runs of it.</summary>
        public int Runs { get; set; }

        /// <summary>How many of the output these runs yield.</summary>
        public int Output => this.Runs * Math.Max(1, this.Recipe?.OutputCount ?? 1);

        /// <summary>In-game minutes one run takes.</summary>
        public int MinutesPerRun => (this.Recipe?.Minutes ?? 0) + ((this.Recipe?.Days ?? 0) * CraftPlan.MinutesPerDay);
    }

    /// <summary>One item in a crafting plan, with whatever it takes to supply it hanging beneath it.</summary>
    internal class PlanNode
    {
        /*********
        ** Accessors
        *********/
        /// <summary>How this item is supplied.</summary>
        public PlanStepKind Kind { get; set; }

        /// <summary>The qualified item ID, or a category ID for an ingredient that names a group.</summary>
        public string ItemId { get; set; }

        /// <summary>The quality this item must be, or <see cref="Quality.Any"/>.</summary>
        public int RequiredQuality { get; set; } = Quality.Any;

        /// <summary>How many will come from crops still growing under an auto-harvester.</summary>
        public int FromHarvest { get; set; }

        /// <summary>Days until the last of those crops is ready.</summary>
        public int HarvestDays { get; set; }

        /// <summary>The growing crops this row waits on, which a job reserves.</summary>
        public List<IncomingCrop> Harvests { get; set; } = new();

        /// <summary>Seeds to plant on free automation tiles for this row, whose harvest a job waits on.</summary>
        /// <remarks>Their seed and any fertilizer are this row's children, drawn from storage like any ingredient.</remarks>
        public List<PlannedPlanting> Plantings { get; set; } = new();

        /// <summary>How much was drawn from storage at each quality, lowest first.</summary>
        public List<(int Quality, int Count)> StockParts { get; set; } = new();

        /// <summary>The item's display name.</summary>
        public string DisplayName { get; set; }

        /// <summary>How many of this item the parent step needs.</summary>
        public int Requested { get; set; }

        /// <summary>How many came from storage.</summary>
        public int FromStock { get; set; }

        /// <summary>How many are to be made.</summary>
        public int ToProduce { get; set; }

        /// <summary>How many couldn't be supplied at all.</summary>
        public int Missing { get; set; }

        /// <summary>Why they couldn't be supplied.</summary>
        public MissingReason Reason { get; set; }

        /// <summary>The crafting recipe used, if this is a <see cref="PlanStepKind.Craft"/> step.</summary>
        public CraftingRecipe CraftRecipe { get; set; }

        /// <summary>How the step's runs are shared between machine types.</summary>
        public List<MachineAssignment> Assignments { get; } = new();

        /// <summary>The machine doing most of the work, used for the icon and the machine picker.</summary>
        public MachineRecipe MachineRecipe => this.Assignments.Count > 0 ? this.Assignments[0].Recipe : null;

        /// <summary>Other machines that could do the same job, offered to the player as alternatives.</summary>
        public IReadOnlyList<MachineRecipe> Alternatives { get; set; } = new List<MachineRecipe>();

        /// <summary>How many times the recipe runs.</summary>
        public int Batches { get; set; }

        /// <summary>In-game minutes one batch takes; zero for crafting, which is instant.</summary>
        public int MinutesPerBatch { get; set; }

        /// <summary>Whole days one batch takes, for machines that finish overnight.</summary>
        public int DaysPerBatch { get; set; }

        /// <summary>What this step consumes.</summary>
        public List<PlanNode> Children { get; } = new();

        /// <summary>How deep this node sits, used by the tree view for indentation.</summary>
        public int Depth { get; set; }

        /// <summary>Whether this step and everything under it can be supplied.</summary>
        public bool IsSatisfied => this.Missing == 0 && this.Children.All(child => child.IsSatisfied);

        /// <summary>The worst-case processing time for this step alone, if a single machine ran every batch.</summary>
        public int SequentialMinutes => (this.MinutesPerBatch + (this.DaysPerBatch * CraftPlan.MinutesPerDay)) * this.Batches;


        /*********
        ** Public methods
        *********/
        /// <summary>Walks this node and everything beneath it, parents first.</summary>
        public IEnumerable<PlanNode> Walk()
        {
            yield return this;

            foreach (PlanNode child in this.Children)
            {
                foreach (PlanNode descendant in child.Walk())
                    yield return descendant;
            }
        }
    }

    /// <summary>A worked-out route from what's in storage to a requested item.</summary>
    internal class CraftPlan
    {
        /*********
        ** Accessors
        *********/
        /// <summary>In-game minutes a machine counts down between one morning and the next.</summary>
        /// <remarks>
        /// Machines tick ten minutes at a time while the player is awake, and when they sleep the game credits
        /// <c>Utility.CalculateMinutesUntilMorning</c> -- the time from bedtime to 6am. However late the player
        /// stays up, the two always add up to a full 24 hours.
        /// </remarks>
        public const int MinutesPerDay = 1440;

        /// <summary>The requested item.</summary>
        public PlanNode Root { get; init; }

        /// <summary>The number requested.</summary>
        public int RequestedCount { get; init; }

        /// <summary>Whether the whole plan can be carried out with what's in storage.</summary>
        public bool IsSatisfied => this.Root?.IsSatisfied == true;

        /// <summary>Whether the planner ran out of depth before resolving everything.</summary>
        public bool HitDepthLimit { get; set; }

        /// <summary>Everything the plan came up short on, totalled.</summary>
        public IReadOnlyList<ItemCost> Shortfalls => this.Root == null
            ? new List<ItemCost>()
            : this.Root.Walk()
                .Where(node => node.Missing > 0)
                .GroupBy(node => node.ItemId)
                .Select(group => new ItemCost(group.Key, group.Sum(node => node.Missing)))
                .ToList();

        /// <summary>What the plan is short of, each with why where there's more to say than "not in storage".</summary>
        /// <param name="getName">Names an item.</param>
        public string DescribeShortfalls(Func<string, string> getName, int max = int.MaxValue)
        {
            if (this.Root == null)
                return "";

            IEnumerable<string> parts = this.Root.Walk()
                .Where(node => node.Missing > 0)
                .GroupBy(node => (Id: node.ItemId?.ToLowerInvariant(), node.Reason))
                .Select(group =>
                {
                    PlanNode first = group.First();
                    string text = $"{group.Sum(node => node.Missing)}x {getName(first.ItemId)}";
                    string why = first.Reason switch
                    {
                        MissingReason.NoFreeTiles => "no free automation tiles",
                        MissingReason.CantGrowInTime => "can't grow in time on automation tiles",
                        MissingReason.NoMachineAvailable => first.Alternatives.Count > 0 ? $"no {first.Alternatives[0].MachineName} on the network" : "no machine on the network",
                        MissingReason.RecipeLoop => "recipe loops back on itself",
                        MissingReason.DepthLimit => "too many steps",
                        _ => null
                    };
                    return why == null ? text : $"{text} ({why})";
                });

            return string.Join(", ", parts.Take(max));
        }

        /// <summary>Every step that needs a machine, in the order they'd have to run.</summary>
        public IReadOnlyList<PlanNode> ProcessingSteps => this.Root == null
            ? new List<PlanNode>()
            : this.Root.Walk().Where(node => node.Kind == PlanStepKind.Process).Reverse().ToList();

        /// <summary>The longest processing time the plan could take, assuming one machine per step.</summary>
        /// <remarks>
        /// A floor rather than a forecast: steps that depend on each other must run in sequence, but independent
        /// ones and extra machines both shorten it. The scheduler refines this once machines are assigned.
        /// </remarks>
        public int WorstCaseMinutes => this.Root?.Walk().Sum(node => node.SequentialMinutes) ?? 0;

        /// <summary>How many steps the plan has, excluding items taken straight from storage.</summary>
        public int StepCount => this.Root?.Walk().Count(node => node.Kind is PlanStepKind.Craft or PlanStepKind.Process) ?? 0;
    }
}
