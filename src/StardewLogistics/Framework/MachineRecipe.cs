using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>One way a machine turns an input into an output, as the game itself resolves it.</summary>
    /// <remarks>
    /// This is the processing counterpart to a <see cref="CraftingRecipe"/>: five copper ore plus a coal in a
    /// furnace yields a copper bar in thirty minutes. Unlike crafting recipes these aren't a list the game keeps
    /// anywhere, so <see cref="MachineRecipeIndex"/> works them out by asking the game what each machine would do
    /// with a given input.
    ///
    /// Input and output are <see cref="StockId"/>s, so a keg recipe reads "Starfruit in, Starfruit Wine out"
    /// rather than "fruit in, some wine out".
    /// </remarks>
    internal class MachineRecipe
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The qualified item ID of the machine, e.g. <c>(BC)13</c> for a furnace.</summary>
        public string MachineId { get; init; }

        /// <summary>The machine's display name, for the plan and the UI.</summary>
        public string MachineName { get; init; }

        /// <summary>The stock ID this recipe consumes.</summary>
        public string InputId { get; init; }

        /// <summary>The context tags the machine matched the input on, when it accepts a category rather than an item.</summary>
        /// <remarks>Only for display: the input is always a specific item by the time a recipe exists.</remarks>
        public IReadOnlyList<string> InputTags { get; init; } = new List<string>();

        /// <summary>How many of the input one run consumes.</summary>
        public int InputCount { get; init; }

        /// <summary>The quality the input must be, or <see cref="Quality.Any"/> for a recipe that takes any.</summary>
        /// <remarks>
        /// Almost every recipe takes any quality, and planning spends the lowest first. A few machines do more
        /// with a better input -- iridium wool always weaves two cloth -- and those are indexed as a separate recipe
        /// tied to that quality, so the planner can choose it when it genuinely saves ingredients and time.
        /// </remarks>
        public int InputQuality { get; init; } = Quality.Any;

        /// <summary>Anything else a run consumes, such as a furnace's coal.</summary>
        public IReadOnlyList<ItemCost> ExtraInputs { get; init; } = new List<ItemCost>();

        /// <summary>The stock ID this recipe produces.</summary>
        public string OutputId { get; init; }

        /// <summary>A real instance of the output, as the machine would make it.</summary>
        /// <remarks>
        /// Kept because a flavoured output can't be rebuilt from its ID: the name, colour and price of a wine are
        /// set by the keg. Running a job hands the machine a copy of this.
        /// </remarks>
        public Item OutputSample { get; init; }

        /// <summary>Whether this is a cask aging an item to a better quality, rather than a machine transforming it.</summary>
        /// <remarks>
        /// Casks don't fit the mould of other machines: the output is the input, a run lasts until a quality is
        /// reached rather than a set time, and how long depends on the quality it went in at. The planner treats
        /// aging as an ordinary processing step, with these fields saying what "done" means.
        /// </remarks>
        public bool IsAging { get; init; }

        /// <summary>The quality an aging run stops at.</summary>
        public int TargetQuality { get; init; } = Quality.Any;

        /// <summary>How many cask-days pass per real day for this item: Wine 1, Cheese 4.</summary>
        public float AgingRate { get; init; } = 1f;

        /// <summary>Whether the output takes its identity from the input, like a wine from its fruit.</summary>
        public bool OutputIsFlavoured => StockId.IsFlavoured(this.OutputId);

        /// <summary>Whether this recipe only exists because its input is in storage.</summary>
        /// <remarks>
        /// Machines that take a category -- a keg takes any fruit -- aren't listed for every item in the game,
        /// only for what the network holds. These are the recipes that come and go with stock.
        /// </remarks>
        public bool FromStock { get; init; }

        /// <summary>How many of the output a run is <em>guaranteed</em> to produce.</summary>
        /// <remarks>
        /// Machines with a variable yield — the Heavy Furnace being the obvious one — declare a stack range. Only
        /// the bottom of that range can be promised, so planning uses it: a job that assumed the average would
        /// come up short about half the time, which is worse than finishing early.
        /// </remarks>
        public int OutputCount { get; init; }

        /// <summary>The top of the output range, kept only so the UI can say a run <em>may</em> yield more.</summary>
        public int MaxOutputCount { get; init; }

        /// <summary>In-game minutes one run takes: its data's figure, allowing for how long runs really take in this save.</summary>
        /// <remarks>See <see cref="Calibration"/>. Setting it sets <see cref="BaseMinutes"/>, the data's own figure.</remarks>
        public int Minutes
        {
            get => this.IsAging ? this.BaseMinutes : Calibration.Scale(this.BaseMinutes, Calibration.MachineFactor(this.MachineId, this.OutputId));
            init => this.BaseMinutes = value;
        }

        /// <summary>Whole days one run takes, for machines that finish overnight, allowing for how long runs really take.</summary>
        public int Days
        {
            get => this.IsAging ? this.BaseDays : Calibration.ScaleDays(this.BaseDays, Calibration.MachineFactor(this.MachineId, this.OutputId));
            init => this.BaseDays = value;
        }

        /// <summary>The minutes one run takes by the game's data, ready-time modifiers included.</summary>
        public int BaseMinutes { get; private set; }

        /// <summary>The days one run takes by the game's data.</summary>
        public int BaseDays { get; private set; }

        /// <summary>Whether the yield varies, so the UI can mark the figure as a floor rather than a promise.</summary>
        public bool HasVariableYield => this.MaxOutputCount > this.OutputCount;

        /// <summary>A stable key for this recipe, used to remember the player's machine preferences.</summary>
        public string Key => $"{this.MachineId}|{this.InputId}|{this.OutputId}"
            + (this.InputQuality >= 0 ? $"|q{this.InputQuality}" : "")
            + (this.IsAging ? $"|age{this.TargetQuality}" : "")
            + (this.RecipeExtras > 0 ? "|+" + string.Join("+", this.ExtraInputs.Skip(this.ExtraInputs.Count - this.RecipeExtras).Select(extra => extra.ItemId)) : "");

        /// <summary>How many of <see cref="ExtraInputs"/> belong to this recipe, rather than to the machine.</summary>
        /// <remarks>A recipe's own extras -- an Extra Machine Config ingredient -- are the last ones listed.</remarks>
        public int RecipeExtras { get; init; }

        /// <summary>The output's display name.</summary>
        public string OutputName => this.OutputSample?.DisplayName ?? StockId.GetDisplayName(this.OutputId);


        /*********
        ** Public methods
        *********/
        /// <summary>Returns everything one run consumes, including the primary input.</summary>
        public IEnumerable<ItemCost> GetAllInputs()
        {
            yield return new ItemCost(this.InputId, this.InputCount, this.InputQuality);

            foreach (ItemCost extra in this.ExtraInputs)
                yield return extra;
        }

        /// <summary>Creates one run's output, ready to put in the machine.</summary>
        public Item CreateOutput()
        {
            Item output = this.OutputSample?.getOne() ?? StockId.Create(this.OutputId);
            if (output != null)
                output.Stack = Math.Max(1, this.OutputCount);
            return output;
        }

        /// <summary>Describes the input side for logs and the console.</summary>
        public string DescribeInputs(Func<string, string> getName)
        {
            string primary = $"{this.InputCount}x {getName(this.InputId)}" + (this.InputQuality >= 0 ? $" ({Quality.Name(this.InputQuality)})" : "");
            if (this.InputTags.Count > 0)
                primary += $" [{string.Join(" ", this.InputTags)}]";

            return this.ExtraInputs.Count == 0
                ? primary
                : primary + " + " + string.Join(" + ", this.ExtraInputs.Select(extra => $"{extra.Count}x {getName(extra.ItemId)}"));
        }

        public override string ToString() => $"{this.MachineName}: {this.InputCount}x {this.InputId} -> {this.OutputCount}x {this.OutputId}";
    }

    /// <summary>A quantity of one item, used for recipe inputs.</summary>
    internal readonly struct ItemCost
    {
        /// <summary>The stock ID, or a category ID for recipes that accept a group.</summary>
        public string ItemId { get; }

        /// <summary>How many are needed.</summary>
        public int Count { get; }

        /// <summary>The quality required, or <see cref="Quality.Any"/>.</summary>
        public int RequiredQuality { get; }

        public ItemCost(string itemId, int count, int requiredQuality = Quality.Any)
        {
            this.ItemId = itemId;
            this.Count = count;
            this.RequiredQuality = requiredQuality;
        }

        public override string ToString() => $"{this.Count}x {this.ItemId}";
    }
}
