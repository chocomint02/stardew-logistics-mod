using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewLogistics.Framework
{
    /// <summary>Shares a step's machine budget between the machine types working on it.</summary>
    /// <remarks>
    /// A split step runs on more than one kind of machine at once, so "max machines" has to be a budget for the
    /// whole step rather than a limit applied to each share. Applied per share, a budget of one let a Heavy
    /// Furnace and a plain Furnace take one machine each and occupy two.
    ///
    /// Both the planner dialog and the scheduler allocate through here, so what the player is shown is what the
    /// job actually does.
    /// </remarks>
    internal static class MachineAllocator
    {
        /*********
        ** Public methods
        *********/
        /// <summary>The smallest budget that lets every share start: one machine of each kind the plan uses.</summary>
        public static int MinimumBudget(PlanNode node)
        {
            return Math.Max(1, node?.Assignments.Count ?? 1);
        }

        /// <summary>The largest budget the step could use, beyond which extra machines would sit idle.</summary>
        /// <remarks>A share can't use more machines than it has runs, nor more than the network holds.</remarks>
        public static int MaximumBudget(PlanNode node, Func<MachineRecipe, int> countMachines)
        {
            if (node?.Assignments == null || node.Assignments.Count == 0)
                return 1;

            // Two shares on the same kind of machine -- duck eggs and golden duck eggs in Mayonnaise Machines --
            // compete for the same machines, so a machine type contributes no more than it has.
            int total = node.Assignments
                .GroupBy(assignment => assignment.Recipe.MachineId, StringComparer.OrdinalIgnoreCase)
                .Sum(type => Math.Min(TypeCapacity(type, countMachines), type.Sum(assignment => Ceiling(assignment, countMachines))));

            return Math.Max(MinimumBudget(node), total);
        }

        /// <summary>Divides a budget between a step's shares.</summary>
        /// <remarks>
        /// Every share takes one machine first so none is left unable to start, then spare machines go to
        /// whichever share currently finishes last. That is the standard greedy for shortening a makespan, and it
        /// means raising the budget always helps the part of the step that is holding it up.
        /// </remarks>
        /// <param name="paces">How fast each machine that could run a recipe works, fastest first, where that's known.</param>
        public static Dictionary<MachineAssignment, int> Allocate(PlanNode node, int budget, Func<MachineRecipe, int> countMachines, Func<MachineRecipe, IReadOnlyList<MachinePace>> paces = null)
        {
            Dictionary<MachineAssignment, int> allocation = new();
            if (node?.Assignments == null || node.Assignments.Count == 0)
                return allocation;

            foreach (MachineAssignment assignment in node.Assignments)
                allocation[assignment] = 0;

            int remaining = Math.Max(MinimumBudget(node), budget);

            foreach (MachineAssignment assignment in node.Assignments)
            {
                if (remaining <= 0)
                    break;

                allocation[assignment] = 1;
                remaining--;
            }

            while (remaining > 0)
            {
                MachineAssignment slowest = null;
                int slowestTime = -1;

                foreach (MachineAssignment assignment in node.Assignments)
                {
                    if (allocation[assignment] >= Ceiling(assignment, countMachines))
                        continue;

                    // Its machine type is fully booked by this step's other shares.
                    if (TypeAllocated(node, assignment, allocation) >= TypeCapacity(node, assignment, countMachines))
                        continue;

                    int time = TimeFor(assignment, allocation[assignment], null, paces);
                    if (time > slowestTime)
                    {
                        slowestTime = time;
                        slowest = assignment;
                    }
                }

                if (slowest == null)
                    break; // every share is at its own ceiling; more machines would idle

                allocation[slowest]++;
                remaining--;
            }

            return allocation;
        }

        /// <summary>How long a step takes under a given allocation.</summary>
        /// <remarks>Shares occupy different machines and run side by side, so the step is as long as its slowest.</remarks>
        /// <param name="node">The step.</param>
        /// <param name="allocation">Machines per share.</param>
        /// <param name="dusted">Runs per share that Fairy Dust will finish almost at once, if any.</param>
        /// <param name="paces">How fast each machine that could run a recipe works, fastest first, where that's known.</param>
        public static int StepMinutes(PlanNode node, IReadOnlyDictionary<MachineAssignment, int> allocation, IReadOnlyDictionary<MachineAssignment, int> dusted = null, Func<MachineRecipe, IReadOnlyList<MachinePace>> paces = null)
        {
            if (node?.Assignments == null || node.Assignments.Count == 0)
                return 0;

            int longest = 0;
            foreach (MachineAssignment assignment in node.Assignments)
            {
                int machines = allocation.TryGetValue(assignment, out int got) ? got : 1;
                int sped = dusted != null && dusted.TryGetValue(assignment, out int count) ? count : 0;
                int slow = Math.Max(0, assignment.Runs - sped);

                // Dusted runs still take a moment each; the rest take their usual time on what machines there are.
                int time = slow > 0 ? TimeFor(assignment, machines, slow, paces) : 0;
                if (sped > 0)
                    time = Math.Max(time, 10);

                longest = Math.Max(longest, time);
            }

            return longest;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>The most machines one share could use before they start idling.</summary>
        private static int Ceiling(MachineAssignment assignment, Func<MachineRecipe, int> countMachines)
        {
            int usable = Math.Max(1, countMachines?.Invoke(assignment.Recipe) ?? 1);
            return Math.Max(1, Math.Min(usable, Math.Max(1, assignment.Runs)));
        }

        /// <summary>How many machines of one type the network has for a step, across the shares using that type.</summary>
        private static int TypeCapacity(IEnumerable<MachineAssignment> sharesOfType, Func<MachineRecipe, int> countMachines)
        {
            return Math.Max(1, sharesOfType.Max(assignment => Math.Max(1, countMachines?.Invoke(assignment.Recipe) ?? 1)));
        }

        /// <summary>How many machines of an assignment's type the step's shares may use together.</summary>
        private static int TypeCapacity(PlanNode node, MachineAssignment assignment, Func<MachineRecipe, int> countMachines)
        {
            return TypeCapacity(SameType(node, assignment), countMachines);
        }

        /// <summary>How many machines of an assignment's type are already allocated to the step.</summary>
        private static int TypeAllocated(PlanNode node, MachineAssignment assignment, IReadOnlyDictionary<MachineAssignment, int> allocation)
        {
            return SameType(node, assignment).Sum(other => allocation.TryGetValue(other, out int got) ? got : 0);
        }

        /// <summary>The step's shares running on the same kind of machine as an assignment.</summary>
        private static IEnumerable<MachineAssignment> SameType(PlanNode node, MachineAssignment assignment)
        {
            return node.Assignments.Where(other => string.Equals(other.Recipe.MachineId, assignment.Recipe.MachineId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>How long a share takes on a number of machines.</summary>
        /// <remarks>
        /// With each machine's own pace known -- one may be upgraded, or several combined into one -- the fastest
        /// of them do the work, each load going to whichever machine would finish it soonest. Otherwise every
        /// machine is taken to work as the recipe's data says.
        /// </remarks>
        private static int TimeFor(MachineAssignment assignment, int machines, int? runs = null, Func<MachineRecipe, IReadOnlyList<MachinePace>> paces = null)
        {
            int count = Math.Max(1, machines);
            int total = Math.Max(1, runs ?? assignment.Runs);

            IReadOnlyList<MachinePace> known = paces?.Invoke(assignment.Recipe);
            if (known is { Count: > 0 })
                return Makespan(known.Take(count).ToList(), total);

            int waves = (int)Math.Ceiling(total / (double)count);
            return waves * assignment.MinutesPerRun;
        }

        /// <summary>How long a number of runs takes on particular machines, each load going to whichever finishes it soonest.</summary>
        private static int Makespan(IReadOnlyList<MachinePace> machines, int runs)
        {
            double[] busy = new double[machines.Count];
            int left = runs;
            while (left > 0)
            {
                int best = 0;
                for (int i = 1; i < machines.Count; i++)
                {
                    if (busy[i] + machines[i].Minutes < busy[best] + machines[best].Minutes)
                        best = i;
                }

                busy[best] += machines[best].Minutes;
                left -= Math.Max(1, machines[best].Runs);
            }

            return (int)Math.Ceiling(busy.Max());
        }
    }
}
