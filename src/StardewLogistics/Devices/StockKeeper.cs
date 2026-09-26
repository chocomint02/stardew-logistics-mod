using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace StardewLogistics.Devices
{
    /// <summary>Where a minimum-stock rule stands, for the terminal's Stock tab.</summary>
    internal class StockRuleStatus
    {
        /// <summary>How many storage holds that count towards the rule.</summary>
        public long Have { get; init; }

        /// <summary>How many its running jobs have still to deliver.</summary>
        public int Coming { get; init; }

        /// <summary>Why the last attempt to make more failed, or <c>null</c> if it didn't.</summary>
        public string Error { get; init; }
    }

    /// <summary>Tops storage up to each minimum-stock rule by queuing autocrafting jobs.</summary>
    /// <remarks>
    /// Checked every ten in-game minutes on the host. A rule short of its target, counting what its running jobs
    /// have still to deliver, queues a job for the difference. When the whole difference can't be made, as many
    /// as can be are; when none can, the rule says why and tries again an hour later rather than re-planning
    /// every tick.
    /// </remarks>
    internal class StockKeeper
    {
        /*********
        ** Fields
        *********/
        private readonly NetworkManager Networks;
        private readonly JobRunner Jobs;
        private readonly ITranslationHelper Translations;

        /// <summary>Every terminal in the world, or <c>null</c> if they need finding again.</summary>
        private List<(GameLocation Location, Vector2 Tile)> Terminals;

        /// <summary>Why each rule last failed, by rule key.</summary>
        private readonly Dictionary<string, string> Errors = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>When each failed rule may be tried again, as <see cref="Now"/>.</summary>
        private readonly Dictionary<string, int> RetryAt = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Rules that have already told the player they're stuck today.</summary>
        private readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);


        /*********
        ** Public methods
        *********/
        public StockKeeper(NetworkManager networks, JobRunner jobs, ITranslationHelper translations)
        {
            this.Networks = networks;
            this.Jobs = jobs;
            this.Translations = translations;
        }

        /// <summary>Forgets where the terminals are, for when one is placed or removed.</summary>
        public void Invalidate() => this.Terminals = null;

        /// <summary>Forgets everything, for when the player leaves the save.</summary>
        public void Reset()
        {
            this.Terminals = null;
            this.Errors.Clear();
            this.RetryAt.Clear();
            this.Warned.Clear();
        }

        /// <summary>A new day: whatever blocked a rule yesterday may not today.</summary>
        public void OnDayStarted()
        {
            this.Terminals = null;
            this.RetryAt.Clear();
            this.Warned.Clear();
        }

        /// <summary>Checks every rule in the world, queuing jobs where storage has dropped below one.</summary>
        public void Run()
        {
            if (!Context.IsWorldReady || !Context.IsMainPlayer)
                return;

            HashSet<StorageNetwork> seen = new();
            foreach ((GameLocation location, Vector2 tile) in this.GetTerminals())
            {
                if (!location.Objects.TryGetValue(tile, out SObject terminal) || !terminal.modData.ContainsKey(ModIds.StockRulesKey))
                    continue;

                StorageNetwork network = this.Networks.GetNetworkAt(location, tile);
                if (network != null && seen.Add(network))
                    this.Check(network);
            }
        }

        /// <summary>Checks one network's rules.</summary>
        public void Check(StorageNetwork network)
        {
            if (network == null || !Context.IsMainPlayer)
                return;

            foreach ((StockRule rule, NetworkNode _) in this.GetRules(network))
            {
                try
                {
                    this.CheckRule(rule, network);
                }
                catch (Exception ex)
                {
                    Log.Error($"Couldn't check the stock rule for {StockId.GetDisplayName(rule.ItemId)}.", ex);
                }
            }
        }

        /// <summary>Every rule on a network, with the terminal holding it.</summary>
        /// <remarks>Two terminals holding a rule for the same thing -- two networks since joined -- count once, the first found.</remarks>
        public List<(StockRule Rule, NetworkNode Terminal)> GetRules(StorageNetwork network)
        {
            List<(StockRule, NetworkNode)> rules = new();
            if (network == null)
                return rules;

            HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
            foreach (NetworkNode terminal in network.Terminals)
            {
                foreach (StockRule rule in StockRule.Read(terminal.Object))
                {
                    if (keys.Add(rule.Key))
                        rules.Add((rule, terminal));
                }
            }

            return rules;
        }

        /// <summary>Where a rule stands now.</summary>
        public StockRuleStatus GetStatus(StockRule rule, StorageNetwork network)
        {
            return new StockRuleStatus
            {
                Have = CountHeld(rule, network),
                Coming = this.CountComing(rule, network),
                Error = this.Errors.TryGetValue(rule.Key, out string error) ? error : null
            };
        }

        /// <summary>Adds a rule to a network, or changes the one it has for the same thing.</summary>
        /// <param name="network">The network.</param>
        /// <param name="home">The terminal to store a new rule on.</param>
        /// <param name="rule">The rule.</param>
        /// <param name="replacing">The key of a rule this one replaces, if it's an edit that changed the quality.</param>
        public void SetRule(StorageNetwork network, SObject home, StockRule rule, string replacing = null)
        {
            if (network == null || rule?.ItemId == null)
                return;

            if (replacing != null && !string.Equals(replacing, rule.Key, StringComparison.OrdinalIgnoreCase))
                this.RemoveRule(network, replacing);

            // Changed where it's kept, if a terminal on the network already has it; otherwise kept here.
            SObject holder = network.Terminals.Select(node => node.Object).FirstOrDefault(terminal => StockRule.Read(terminal).Any(existing => existing.Key == rule.Key)) ?? home;
            if (holder == null)
                return;

            List<StockRule> rules = StockRule.Read(holder);
            rules.RemoveAll(existing => existing.Key == rule.Key);
            rules.Add(rule);
            StockRule.Write(holder, rules);

            this.Errors.Remove(rule.Key);
            this.RetryAt.Remove(rule.Key);
            this.Warned.Remove(rule.Key);
            this.Invalidate();

            Log.Debug($"Stock rule set: keep {rule.Target}x {StockId.GetDisplayName(rule.ItemId)}{(rule.Quality > 0 ? $" ({Quality.Name(rule.Quality)})" : "")}.");
            this.Check(network);
        }

        /// <summary>Removes a rule from whichever terminal on a network holds it.</summary>
        public void RemoveRule(StorageNetwork network, string key)
        {
            if (network == null || key == null)
                return;

            foreach (SObject terminal in network.Terminals.Select(node => node.Object))
            {
                List<StockRule> rules = StockRule.Read(terminal);
                if (rules.RemoveAll(rule => rule.Key == key) > 0)
                    StockRule.Write(terminal, rules);
            }

            this.Errors.Remove(key);
            this.RetryAt.Remove(key);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Queues a job for one rule if storage has dropped below it.</summary>
        private void CheckRule(StockRule rule, StorageNetwork network)
        {
            long have = CountHeld(rule, network);
            int coming = this.CountComing(rule, network);
            long deficit = rule.Target - have - coming;
            if (deficit <= 0)
            {
                this.Errors.Remove(rule.Key);
                return;
            }

            if (this.RetryAt.TryGetValue(rule.Key, out int retry) && Now() < retry)
                return;

            int wanted = (int)Math.Min(deficit, 9999);
            int maxMachines = rule.MaxMachines > 0 ? rule.MaxMachines : int.MaxValue;

            // The whole shortfall if it can be made; otherwise the most that can, found by halving. Planning is
            // cheap next to a job, and a rule making ten of the fifty it wants is better than one making none.
            int count = wanted;
            if (!this.Jobs.CanPlan(rule.ItemId, count, network, rule.Quality, rule.FertilizerId))
            {
                int low = 0;
                int high = wanted - 1;
                while (low < high)
                {
                    int middle = (low + high + 1) / 2;
                    if (this.Jobs.CanPlan(rule.ItemId, middle, network, rule.Quality, rule.FertilizerId))
                        low = middle;
                    else
                        high = middle - 1;
                }
                count = low;
            }

            string error;
            CraftJob job = null;
            if (count > 0)
                job = this.Jobs.TryQueue(rule.ItemId, count, network, maxMachines, null, out error, rule.Quality, rule.UseFairyDust, rule.FertilizerId, ruleKey: rule.Key);
            else
                error = this.Jobs.ExplainShortfall(rule.ItemId, wanted, network, rule.Quality, rule.FertilizerId);

            if (job != null)
            {
                this.Errors.Remove(rule.Key);
                this.RetryAt.Remove(rule.Key);
                Log.Debug($"Stock rule for {job.DisplayName}: {have} stored, {coming} coming, queued {job.Id} for {count} more.");
                return;
            }

            this.Errors[rule.Key] = error;
            this.RetryAt[rule.Key] = Now() + 100;
            Log.Trace($"Stock rule for {StockId.GetDisplayName(rule.ItemId)}: {have} of {rule.Target} stored, and no more can be made ({error}). Trying again in an hour.");

            if (this.Warned.Add(rule.Key))
                Game1.addHUDMessage(new HUDMessage(this.Translations.Get("stock.failed-hud", new { name = StockId.GetDisplayName(rule.ItemId), reason = error }), HUDMessage.error_type));
        }

        /// <summary>How many storage holds that count towards a rule: at its quality or better, if it has one.</summary>
        private static long CountHeld(StockRule rule, StorageNetwork network)
        {
            if (rule.Quality <= 0)
                return network.CountById(rule.ItemId);

            return new[] { SObject.medQuality, SObject.highQuality, SObject.bestQuality }
                .Where(quality => quality >= rule.Quality)
                .Sum(quality => network.CountById(rule.ItemId, quality));
        }

        /// <summary>How many a rule's running jobs on a network have still to deliver.</summary>
        private int CountComing(StockRule rule, StorageNetwork network)
        {
            return this.Jobs.Jobs
                .Where(job => job.RuleKey == rule.Key && job.Status is not (JobStatus.Complete or JobStatus.Cancelled) && this.Jobs.IsOnNetwork(job, network))
                .Sum(job => Math.Max(0, job.TargetCount - job.Delivered));
        }

        /// <summary>The time now, as a number that only goes up: days then time of day.</summary>
        private static int Now() => (Game1.Date.TotalDays * 10000) + Game1.timeOfDay;

        /// <summary>Every terminal in the world, found once and remembered.</summary>
        private List<(GameLocation, Vector2)> GetTerminals()
        {
            if (this.Terminals != null)
                return this.Terminals;

            List<(GameLocation, Vector2)> found = new();
            Utility.ForEachLocation(location =>
            {
                foreach ((Vector2 tile, SObject obj) in location.Objects.Pairs)
                {
                    if (obj != null && NetworkNode.GetKind(obj.ItemId) is NodeKind.Terminal or NodeKind.CraftingTerminal)
                        found.Add((location, tile));
                }
                return true;
            }, includeInteriors: true, includeGenerated: false);

            return this.Terminals = found;
        }
    }
}
