using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.ItemTypeDefinitions;

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

        /// <summary>The qualified item ID this recipe consumes, or <c>null</c> when it matches by tag instead.</summary>
        public string InputId { get; init; }

        /// <summary>Context tags the input must carry, for recipes that take a category rather than an item.</summary>
        /// <remarks>
        /// A keg doesn't have a recipe per fruit; it has one rule that accepts anything tagged as fruit. Which
        /// fruit only becomes known when there is one to put in, so these recipes are expanded against what the
        /// network is actually holding rather than against every fruit in the game.
        /// </remarks>
        public IReadOnlyList<string> InputTags { get; init; } = new List<string>();

        /// <summary>Whether the input is named by tag rather than by ID.</summary>
        public bool MatchesByTag => this.InputId == null && this.InputTags.Count > 0;

        /// <summary>How many of the input one run consumes.</summary>
        public int InputCount { get; init; }

        /// <summary>Anything else a run consumes, such as a furnace's coal.</summary>
        public IReadOnlyList<ItemCost> ExtraInputs { get; init; } = new List<ItemCost>();

        /// <summary>The qualified item ID this recipe produces, or the base item when the output is flavoured.</summary>
        public string OutputId { get; init; }

        /// <summary>The preserve kind this recipe produces, when its output takes its identity from the input.</summary>
        /// <remarks>
        /// A keg's output is not "wine", it is "wine made from whatever went in". The base item ID alone doesn't
        /// identify it, so the flavour is carried separately and resolved once the input is known.
        /// </remarks>
        public string PreserveType { get; init; }

        /// <summary>Whether the output's identity comes from the input.</summary>
        public bool OutputIsFlavoured { get; init; }

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
        public string Key => $"{this.MachineId}|{this.InputId ?? string.Join(",", this.InputTags)}|{this.OutputId}";

        /// <summary>Whether an item can be this recipe's input.</summary>
        public bool AcceptsInput(Item item)
        {
            if (item == null)
                return false;

            if (this.InputId != null)
                return string.Equals(item.QualifiedItemId, this.InputId, StringComparison.OrdinalIgnoreCase);

            if (this.InputTags.Count == 0)
                return false;

            try
            {
                // Every tag must match, which is how the game reads a trigger's tag list.
                return this.InputTags.All(tag => ItemContextTagManager.DoesTagMatch(tag, item.GetContextTags()));
            }
            catch
            {
                return false;
            }
        }


        /*********
        ** Public methods
        *********/
        /// <summary>Returns everything one run consumes, including the primary input.</summary>
        /// <remarks>Only meaningful for recipes naming a specific input; a tag-matched one has no ID to give.</remarks>
        public IEnumerable<ItemCost> GetAllInputs()
        {
            if (this.InputId != null)
                yield return new ItemCost(this.InputId, this.InputCount);

            foreach (ItemCost extra in this.ExtraInputs)
                yield return extra;
        }

        /// <summary>Describes the input side for logs and the console, naming tags where there is no item.</summary>
        public string DescribeInputs(Func<string, string> getName)
        {
            string primary = this.InputId != null
                ? $"{this.InputCount}x {getName(this.InputId)}"
                : $"{this.InputCount}x any [{string.Join(" ", this.InputTags)}]";

            return this.ExtraInputs.Count == 0
                ? primary
                : primary + " + " + string.Join(" + ", this.ExtraInputs.Select(extra => $"{extra.Count}x {getName(extra.ItemId)}"));
        }

        public override string ToString()
        {
            string input = this.InputId ?? "[" + string.Join(" ", this.InputTags) + "]";
            string output = this.OutputIsFlavoured ? $"{this.OutputId} flavoured by input" : this.OutputId;
            return $"{this.MachineName}: {this.InputCount}x {input} -> {this.OutputCount}x {output}";
        }
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
