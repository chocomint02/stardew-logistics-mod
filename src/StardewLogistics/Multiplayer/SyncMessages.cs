using System.Collections.Generic;
using StardewLogistics.Devices;

namespace StardewLogistics.Multiplayer
{
    /// <summary>Message types, as sent between players.</summary>
    internal static class MessageTypes
    {
        public const string Withdraw = "Withdraw";
        public const string Craft = "Craft";
        public const string Queue = "Queue";
        public const string Job = "Job";
        public const string Rule = "Rule";
        public const string Ship = "Ship";
        public const string Return = "Return";
        public const string Result = "Result";
        public const string Snapshot = "Snapshot";
        public const string Ledger = "Ledger";
        public const string Changed = "Changed";
    }

    /// <summary>A farmhand asking the host to take items out of a network for them.</summary>
    internal class WithdrawRequest
    {
        public string Network { get; set; }
        public string ItemId { get; set; }
        public int Quality { get; set; }
        public string Variant { get; set; }
        public int Unique { get; set; }
        public int Count { get; set; }
    }

    /// <summary>A farmhand asking the host to craft from a network.</summary>
    internal class CraftRequest
    {
        public string Network { get; set; }
        public string Recipe { get; set; }
        public bool Cooking { get; set; }
        public int Times { get; set; }
    }

    /// <summary>A farmhand asking the host to queue an autocrafting job.</summary>
    internal class QueueRequest
    {
        public string Network { get; set; }
        public string TargetId { get; set; }
        public int Count { get; set; }
        public int MaxMachines { get; set; }
        public Dictionary<string, string> Preferred { get; set; } = new();
        public int Quality { get; set; }
        public bool FairyDust { get; set; }
        public string Fertilizer { get; set; }
    }

    /// <summary>A farmhand acting on a job: cancel, dismiss, clear finished, or Fairy Dust on or off.</summary>
    internal class JobRequest
    {
        public string Token { get; set; }
        public string Action { get; set; }
        public bool Value { get; set; }
    }

    /// <summary>A farmhand setting or removing a minimum-stock rule.</summary>
    internal class RuleRequest
    {
        public string Network { get; set; }
        public string ItemId { get; set; }
        public int Quality { get; set; }
        public int Target { get; set; }
        public bool FairyDust { get; set; }
        public string Fertilizer { get; set; }
        public int MaxMachines { get; set; }
        public string Replacing { get; set; }

        /// <summary>The key of a rule to remove instead, if this is a removal.</summary>
        public string RemoveKey { get; set; }
    }

    /// <summary>A farmhand shipping stored items, or taking one back out of the bin.</summary>
    internal class ShipRequest
    {
        public string Network { get; set; }
        public string ItemId { get; set; }
        public int Quality { get; set; }
        public string Variant { get; set; }
        public int Unique { get; set; }
        public int Count { get; set; }
    }

    /// <summary>What the host made of a request, shown to the farmhand.</summary>
    internal class RequestResult
    {
        public string Message { get; set; }
        public bool IsError { get; set; }
    }

    /// <summary>A job as a farmhand sees it.</summary>
    internal class JobView
    {
        public string Id { get; set; }
        public string Token { get; set; }
        public string TargetId { get; set; }
        public int TargetQuality { get; set; }
        public string DisplayName { get; set; }
        public int TargetCount { get; set; }
        public int Status { get; set; }
        public string BlockedReason { get; set; }
        public double Progress { get; set; }
        public int EtaMinutes { get; set; }
        public bool UseFairyDust { get; set; }
        public string RuleKey { get; set; }
        public int PlannedMinutes { get; set; }
        public int Delivered { get; set; }
        public string LocationName { get; set; }
        public float AnchorX { get; set; }
        public float AnchorY { get; set; }
        public List<TileView> Reserved { get; set; } = new();
    }

    /// <summary>A crop or planting tile a job holds.</summary>
    internal class TileView
    {
        public string Location { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float HarvesterX { get; set; }
        public float HarvesterY { get; set; }
        public string ItemId { get; set; }
        public string SeedId { get; set; }
        public int Days { get; set; }
    }

    /// <summary>Everything a farmhand's terminal needs that only the host knows.</summary>
    internal class HostSnapshot
    {
        public List<JobView> Jobs { get; set; } = new();
        public Dictionary<string, string> RuleErrors { get; set; } = new();
    }

    /// <summary>The ledger, sent to farmhands when it changes.</summary>
    internal class LedgerMessage
    {
        public LedgerData Data { get; set; }
    }
}
