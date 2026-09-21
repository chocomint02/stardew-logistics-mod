using System.Collections.Generic;
using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>One way a machine turns an input into an output, read out of <c>Data/Machines</c>.</summary>
    /// <remarks>
    /// This is the processing counterpart to a <see cref="CraftingRecipe"/>: five copper ore plus a coal in a
    /// furnace yields a copper bar in thirty minutes. Unlike crafting recipes these aren't a list the game keeps
    /// anywhere, so <see cref="MachineRecipeIndex"/> derives them from the machine data at load.
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

        /// <summary>The qualified item ID this recipe consumes.</summary>
        public string InputId { get; init; }

        /// <summary>How many of the input one run consumes.</summary>
        public int InputCount { get; init; }

        /// <summary>Anything else a run consumes, such as a furnace's coal.</summary>
        public IReadOnlyList<ItemCost> ExtraInputs { get; init; } = new List<ItemCost>();

        /// <summary>The qualified item ID this recipe produces.</summary>
        public string OutputId { get; init; }

        /// <summary>How many of the output a run is <em>guaranteed</em> to produce.</summary>
        /// <remarks>
        /// Machines with a variable yield — the Heavy Furnace being the obvious one — declare a stack range. Only
        /// the bottom of that range can be promised, so planning uses it: a job that assumed the average would
        /// come up short about half the time, which is worse than finishing early.
        /// </remarks>
        public int OutputCount { get; init; }

        /// <summary>The top of the output range, kept only so the UI can say a run <em>may</em> yield more.</summary>
        public int MaxOutputCount { get; init; }

        /// <summary>In-game minutes one run takes.</summary>
        public int Minutes { get; init; }

        /// <summary>Whole days one run takes, for machines that finish overnight.</summary>
        public int Days { get; init; }

        /// <summary>Whether the yield varies, so the UI can mark the figure as a floor rather than a promise.</summary>
        public bool HasVariableYield => this.MaxOutputCount > this.OutputCount;

        /// <summary>A stable key for this recipe, used to remember the player's machine preferences.</summary>
        public string Key => $"{this.MachineId}|{this.InputId}|{this.OutputId}";


        /*********
        ** Public methods
        *********/
        /// <summary>Returns everything one run consumes, including the primary input.</summary>
        public IEnumerable<ItemCost> GetAllInputs()
        {
            yield return new ItemCost(this.InputId, this.InputCount);

            foreach (ItemCost extra in this.ExtraInputs)
                yield return extra;
        }

        public override string ToString() => $"{this.MachineName}: {this.InputCount}x {this.InputId} -> {this.OutputCount}x {this.OutputId}";
    }

    /// <summary>A quantity of one item, used for recipe inputs.</summary>
    internal readonly struct ItemCost
    {
        /// <summary>The qualified item ID, or a category ID for recipes that accept a group.</summary>
        public string ItemId { get; }

        /// <summary>How many are needed.</summary>
        public int Count { get; }

        public ItemCost(string itemId, int count)
        {
            this.ItemId = itemId;
            this.Count = count;
        }

        public override string ToString() => $"{this.Count}x {this.ItemId}";
    }
}
