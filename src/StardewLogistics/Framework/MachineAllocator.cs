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
        public static int MaximumBudget(PlanNode node, Func<string, int> countMachines)
        {
            if (node?.Assignments == null || node.Assignments.Count == 0)
                return 1;

            return Math.Max(MinimumBudget(node), node.Assignments.Sum(assignment => Ceiling(assignment, countMachines)));
        }

        /// <summary>Divides a budget between a step's shares.</summary>
        /// <remarks>
        /// Every share takes one machine first so none is left unable to start, then spare machines go to
        /// whichever share currently finishes last. That is the standard greedy for shortening a makespan, and it
        /// means raising the budget always helps the part of the step that is holding it up.
        /// </remarks>
        public static Dictionary<MachineAssignment, int> Allocate(PlanNode node, int budget, Func<string, int> countMachines)
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

                    int time = TimeFor(assignment, allocation[assignment]);
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
        public static int StepMinutes(PlanNode node, IReadOnlyDictionary<MachineAssignment, int> allocation)
        {
            if (node?.Assignments == null || node.Assignments.Count == 0)
                return 0;

            int longest = 0;
            foreach (MachineAssignment assignment in node.Assignments)
            {
                int machines = allocation.TryGetValue(assignment, out int got) ? got : 1;
                longest = Math.Max(longest, TimeFor(assignment, machines));
            }

            return longest;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>The most machines one share could use before they start idling.</summary>
        private static int Ceiling(MachineAssignment assignment, Func<string, int> countMachines)
        {
            int owned = Math.Max(1, countMachines?.Invoke(assignment.Recipe?.MachineId) ?? 1);
            return Math.Max(1, Math.Min(owned, Math.Max(1, assignment.Runs)));
        }

        /// <summary>How long a share takes on a number of machines.</summary>
        private static int TimeFor(MachineAssignment assignment, int machines)
        {
            int count = Math.Max(1, machines);
            int waves = (int)Math.Ceiling(Math.Max(1, assignment.Runs) / (double)count);
            return waves * assignment.MinutesPerRun;
        }
    }
}
