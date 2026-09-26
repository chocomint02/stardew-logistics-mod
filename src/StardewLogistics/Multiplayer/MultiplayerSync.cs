using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewLogistics.Devices;
using StardewLogistics.Framework;
using StardewLogistics.Network;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Inventories;
using StardewValley.Network;

namespace StardewLogistics.Multiplayer
{
    /// <summary>Keeps every player's terminals working on the same networks, with the host in charge.</summary>
    /// <remarks>
    /// <para>
    /// Only the host runs the networks: its world is complete, its autocrafting jobs are the real ones, and it's the
    /// one machine where two players can't take the same item at once. So a farmhand's terminal asks the host to do
    /// things -- withdraw, craft, queue a job, ship -- and the host does them and says how it went.
    /// </para>
    /// <para>
    /// Items travel through two global inventories per farmhand, which the game syncs and saves: the host drops
    /// what a farmhand withdrew into their mailbox, and a farmhand drops what they deposit into their outbox, which
    /// the host empties into the network their terminal is on. Each is locked while it's touched, so neither side
    /// can overwrite the other's change.
    /// </para>
    /// <para>
    /// What only the host knows -- its jobs, why a stock rule is stuck, the ledger -- it sends to farmhands, and it
    /// tells every player whenever a network changes, so open terminals refresh straight away.
    /// </para>
    /// </remarks>
    internal class MultiplayerSync
    {
        /*********
        ** Fields
        *********/
        private readonly IModHelper Helper;
        private readonly string ModId;
        private readonly NetworkManager Networks;
        private readonly JobRunner Jobs;

        /// <summary>Items waiting to go into a player's mailbox, by player ID, until its lock is free.</summary>
        private readonly Dictionary<long, List<Item>> PendingMail = new();

        /// <summary>Items a farmhand is depositing, waiting for their outbox's lock.</summary>
        private readonly List<Item> PendingDeposits = new();

        private bool DepositLocking;

        /// <summary>Whether the host is waiting on a mailbox lock to deliver.</summary>
        private bool MailLocking;

        /// <summary>Whether a farmhand is waiting on their mailbox lock to collect.</summary>
        /// <remarks>Apart from the host's flag: in split-screen, host and farmhand share one copy of the mod.</remarks>
        private bool CollectLocking;
        private int TicksSinceSnapshot;
        private bool SnapshotDue;


        /*********
        ** Accessors
        *********/
        /// <summary>The instance, for the menus and devices that need it.</summary>
        public static MultiplayerSync Instance { get; private set; }

        /// <summary>Whether this player is a farmhand, whose terminals ask the host rather than act themselves.</summary>
        public static bool IsRemote => Context.IsMultiplayer && !Context.IsMainPlayer;

        /// <summary>Goes up whenever a network changes, so open terminals know to refresh.</summary>
        public static int Revision { get; private set; }

        /// <summary>The host's jobs, as this farmhand last heard.</summary>
        public List<CraftJob> RemoteJobs { get; private set; } = new();

        /// <summary>Why each stock rule last failed, as this farmhand last heard.</summary>
        public Dictionary<string, string> RemoteRuleErrors { get; private set; } = new();

        /// <summary>The host's ledger, as this farmhand last heard.</summary>
        public LedgerData RemoteLedger { get; private set; }


        /*********
        ** Public methods
        *********/
        public MultiplayerSync(IModHelper helper, string modId, NetworkManager networks, JobRunner jobs)
        {
            this.Helper = helper;
            this.ModId = modId;
            this.Networks = networks;
            this.Jobs = jobs;
            Instance = this;

            helper.Events.Multiplayer.ModMessageReceived += this.OnMessage;
            helper.Events.Multiplayer.PeerConnected += (_, e) =>
            {
                if (Context.IsMainPlayer)
                {
                    this.SnapshotDue = true;
                    this.SendLedger(e.Peer.PlayerID);
                }
            };
        }

        /// <summary>Forgets what was heard from the host, for when the player leaves the save.</summary>
        public void Reset()
        {
            this.RemoteJobs = new List<CraftJob>();
            this.RemoteRuleErrors = new Dictionary<string, string>();
            this.RemoteLedger = null;
            this.PendingMail.Clear();
            this.PendingDeposits.Clear();
            this.DepositLocking = false;
            this.MailLocking = false;
            this.CollectLocking = false;
        }

        /// <summary>Runs every tick: moves items in and out of the hand-off inventories, and keeps farmhands current.</summary>
        public void Update()
        {
            if (!Context.IsWorldReady || !Context.IsMultiplayer)
                return;

            if (Context.IsMainPlayer)
            {
                this.DrainOutboxes();
                this.DeliverPendingMail();

                // The host's jobs and rule statuses, every second -- sooner after a change.
                this.TicksSinceSnapshot++;
                if (Game1.otherFarmers.Count > 0 && (this.TicksSinceSnapshot >= 60 || (this.SnapshotDue && this.TicksSinceSnapshot >= 10)))
                {
                    this.BroadcastSnapshot();
                    this.TicksSinceSnapshot = 0;
                    this.SnapshotDue = false;
                }
            }
            else
            {
                this.SendPendingDeposits();
                this.CollectMail();
            }
        }

        /// <summary>Records that a network changed: open terminals refresh, and farmhands are told.</summary>
        public void NotifyChanged()
        {
            Revision++;
            if (!Context.IsMultiplayer)
                return;

            this.SnapshotDue = true;
            if (Context.IsMainPlayer)
                this.Helper.Multiplayer.SendMessage(true, MessageTypes.Changed, new[] { this.ModId });
        }

        /// <summary>Asks the host to do something, as a farmhand.</summary>
        public void Send<T>(T request, string type)
        {
            this.Helper.Multiplayer.SendMessage(request, type, new[] { this.ModId }, new[] { Game1.MasterPlayer.UniqueMultiplayerID });
        }

        /// <summary>Sends an item to the host to store, as a farmhand. The item is the host's from now on.</summary>
        public void Deposit(Item item)
        {
            if (item != null && item.Stack > 0)
                this.PendingDeposits.Add(item);
        }

        /// <summary>Tells the host which network this farmhand's open terminal is on, for their deposits.</summary>
        public static void SetActiveNetwork(string reference)
        {
            if (Game1.player == null || reference == null)
                return;

            // Only when it changes: the menu refreshes twice a second, and every write is sent to the host.
            if (!Game1.player.modData.TryGetValue(ModIds.TerminalRefKey, out string current) || current != reference)
                Game1.player.modData[ModIds.TerminalRefKey] = reference;
        }

        /// <summary>Sends the ledger to farmhands, or one of them.</summary>
        public void SendLedger(long? playerId = null)
        {
            if (!Context.IsMainPlayer || !Context.IsMultiplayer || this.Jobs.Ledger == null)
                return;

            LedgerMessage message = new() { Data = new LedgerData { Days = this.Jobs.Ledger.Days.ToList() } };
            this.Helper.Multiplayer.SendMessage(message, MessageTypes.Ledger, new[] { this.ModId }, playerId != null ? new[] { playerId.Value } : null);
        }


        /*********
        ** Private methods: messages
        *********/
        private void OnMessage(object sender, ModMessageReceivedEventArgs e)
        {
            if (e.FromModID != this.ModId)
                return;

            try
            {
                if (Context.IsMainPlayer)
                    this.HandleRequest(e);
                else
                    this.HandleUpdate(e);
            }
            catch (Exception ex)
            {
                Log.Error($"Couldn't handle a '{e.Type}' message from player {e.FromPlayerID}.", ex);
            }
        }

        /// <summary>Carries out a farmhand's request, as the host.</summary>
        private void HandleRequest(ModMessageReceivedEventArgs e)
        {
            Farmer who = Game1.getFarmer(e.FromPlayerID);
            string result = null;
            bool error = false;

            switch (e.Type)
            {
                case MessageTypes.Withdraw:
                {
                    WithdrawRequest request = e.ReadAs<WithdrawRequest>();
                    StorageNetwork network = NetworkRef.Resolve(this.Networks, request.Network);
                    ItemKey key = new(request.ItemId, request.Quality, request.Variant, request.Unique);
                    NetworkItemStack entry = network?.Aggregate().FirstOrDefault(candidate => candidate.Key.Equals(key));
                    if (entry == null)
                    {
                        (result, error) = (this.Text("error.network-empty"), true);
                        break;
                    }

                    this.Mail(who, network.ExtractMerged(entry.Key, entry.Sample, request.Count));
                    break;
                }

                case MessageTypes.Craft:
                {
                    CraftRequest request = e.ReadAs<CraftRequest>();
                    StorageNetwork network = NetworkRef.Resolve(this.Networks, request.Network);
                    if (network == null)
                    {
                        (result, error) = (this.Text("error.not-connected"), true);
                        break;
                    }

                    CraftingRecipe recipe = new(request.Recipe, request.Cooking);
                    List<Item> overflow = new();
                    int made = NetworkCrafting.Craft(network, recipe, Math.Max(1, request.Times), product =>
                    {
                        // Into the network, like a craft at a terminal; whatever won't fit goes to the farmhand.
                        network.Insert(product);
                        if (product.Stack > 0)
                            overflow.Add(product);
                    });
                    this.Mail(who, overflow);

                    if (made <= 0)
                        (result, error) = (this.Text("error.missing-ingredients"), true);
                    break;
                }

                case MessageTypes.Queue:
                {
                    QueueRequest request = e.ReadAs<QueueRequest>();
                    StorageNetwork network = NetworkRef.Resolve(this.Networks, request.Network);
                    CraftJob job = this.Jobs.TryQueue(request.TargetId, request.Count, network, request.MaxMachines, request.Preferred, out string reason, request.Quality, request.FairyDust, request.Fertilizer);
                    if (job == null)
                        (result, error) = (reason, true);
                    break;
                }

                case MessageTypes.Job:
                {
                    JobRequest request = e.ReadAs<JobRequest>();
                    CraftJob job = this.Jobs.Jobs.FirstOrDefault(candidate => candidate.Token == request.Token);
                    switch (request.Action)
                    {
                        case "cancel" when job != null:
                            this.Jobs.Cancel(job.Id);
                            break;
                        case "dismiss" when job != null:
                            this.Jobs.Dismiss(job.Id);
                            break;
                        case "dust" when job != null:
                            job.UseFairyDust = request.Value;
                            break;
                    }
                    break;
                }

                case MessageTypes.Rule:
                {
                    RuleRequest request = e.ReadAs<RuleRequest>();
                    StorageNetwork network = NetworkRef.Resolve(this.Networks, request.Network);
                    if (request.RemoveKey != null)
                        this.Jobs.Stock?.RemoveRule(network, request.RemoveKey);
                    else
                    {
                        StockRule rule = new()
                        {
                            ItemId = request.ItemId,
                            Quality = request.Quality,
                            Target = request.Target,
                            UseFairyDust = request.FairyDust,
                            FertilizerId = request.Fertilizer,
                            MaxMachines = request.MaxMachines
                        };
                        this.Jobs.Stock?.SetRule(network, null, rule, request.Replacing);
                    }
                    break;
                }

                case MessageTypes.Ship:
                {
                    ShipRequest request = e.ReadAs<ShipRequest>();
                    StorageNetwork network = NetworkRef.Resolve(this.Networks, request.Network);
                    ItemKey key = new(request.ItemId, request.Quality, request.Variant, request.Unique);
                    NetworkItemStack entry = network?.Aggregate().FirstOrDefault(candidate => candidate.Key.Equals(key));
                    int shipped = entry == null ? 0 : ShippingService.Ship(network, entry, request.Count, who);
                    if (shipped <= 0)
                        (result, error) = (this.Text("sell.bin-full"), true);
                    break;
                }

                case MessageTypes.Return:
                {
                    ShipRequest request = e.ReadAs<ShipRequest>();
                    StorageNetwork network = NetworkRef.Resolve(this.Networks, request.Network);
                    Item item = ShippingService.GetContents(network, who).FirstOrDefault(candidate => ItemKey.From(candidate).Equals(new ItemKey(request.ItemId, request.Quality, request.Variant, 0)));
                    if (item == null || ShippingService.Return(network, item, who) <= 0)
                        (result, error) = (this.Text("shipping.storage-full"), true);
                    break;
                }

                default:
                    return;
            }

            if (result != null)
                this.Helper.Multiplayer.SendMessage(new RequestResult { Message = result, IsError = error }, MessageTypes.Result, new[] { this.ModId }, new[] { e.FromPlayerID });

            this.NotifyChanged();
        }

        /// <summary>Takes in news from the host, as a farmhand.</summary>
        private void HandleUpdate(ModMessageReceivedEventArgs e)
        {
            switch (e.Type)
            {
                case MessageTypes.Result:
                {
                    RequestResult result = e.ReadAs<RequestResult>();
                    if (!string.IsNullOrEmpty(result.Message))
                        Game1.addHUDMessage(new HUDMessage(result.IsError ? this.Text("mp.request-failed", new { reason = result.Message }) : result.Message, result.IsError ? HUDMessage.error_type : HUDMessage.newQuest_type));
                    break;
                }

                case MessageTypes.Snapshot:
                {
                    HostSnapshot snapshot = e.ReadAs<HostSnapshot>();
                    this.RemoteJobs = snapshot.Jobs.Select(ToJob).ToList();
                    this.RemoteRuleErrors = snapshot.RuleErrors ?? new Dictionary<string, string>();
                    Revision++;
                    break;
                }

                case MessageTypes.Ledger:
                    this.RemoteLedger = e.ReadAs<LedgerMessage>().Data;
                    Revision++;
                    break;

                case MessageTypes.Changed:
                    Revision++;
                    break;
            }
        }

        /// <summary>Sends every farmhand the host's jobs and rule statuses.</summary>
        private void BroadcastSnapshot()
        {
            HostSnapshot snapshot = new()
            {
                Jobs = this.Jobs.Jobs.Select(ToView).ToList(),
                RuleErrors = this.Jobs.Stock?.Errors.ToDictionary(pair => pair.Key, pair => pair.Value) ?? new Dictionary<string, string>()
            };
            this.Helper.Multiplayer.SendMessage(snapshot, MessageTypes.Snapshot, new[] { this.ModId });
        }


        /*********
        ** Private methods: hand-off inventories
        *********/
        /// <summary>Queues items for a player's mailbox; the host's own go straight into its bag.</summary>
        private void Mail(Farmer who, IEnumerable<Item> items)
        {
            List<Item> list = items.Where(item => item != null && item.Stack > 0).ToList();
            if (list.Count == 0 || who == null)
                return;

            if (!this.PendingMail.TryGetValue(who.UniqueMultiplayerID, out List<Item> pending))
                this.PendingMail[who.UniqueMultiplayerID] = pending = new List<Item>();
            pending.AddRange(list);
        }

        /// <summary>Puts queued items into mailboxes, one locked mailbox at a time.</summary>
        private void DeliverPendingMail()
        {
            if (this.MailLocking)
                return;

            long playerId = this.PendingMail.Keys.FirstOrDefault(id => this.PendingMail[id].Count > 0);
            if (playerId == 0)
                return;

            string key = ModIds.MailboxPrefix + playerId;
            NetMutex mutex = Game1.player.team.GetOrCreateGlobalInventoryMutex(key);
            this.MailLocking = true;
            mutex.RequestLock(
                acquired: () =>
                {
                    IInventory mailbox = Game1.player.team.GetOrCreateGlobalInventory(key);
                    foreach (Item item in this.PendingMail[playerId])
                        mailbox.Add(item);
                    this.PendingMail[playerId].Clear();
                    mutex.ReleaseLock();
                    this.MailLocking = false;
                },
                failed: () => this.MailLocking = false
            );
        }

        /// <summary>Stores what farmhands have put in their outboxes, in the network each one's terminal is on.</summary>
        private void DrainOutboxes()
        {
            foreach (Farmer farmer in Game1.getOnlineFarmers())
            {
                if (farmer.IsMainPlayer)
                    continue;

                string key = ModIds.OutboxPrefix + farmer.UniqueMultiplayerID;
                if (!Game1.player.team.globalInventories.ContainsKey(key) || Game1.player.team.GetOrCreateGlobalInventory(key).Count == 0)
                    continue;

                NetMutex mutex = Game1.player.team.GetOrCreateGlobalInventoryMutex(key);
                if (mutex.IsLocked())
                    continue;

                mutex.RequestLock(() =>
                {
                    IInventory outbox = Game1.player.team.GetOrCreateGlobalInventory(key);
                    StorageNetwork network = farmer.modData.TryGetValue(ModIds.TerminalRefKey, out string reference) ? NetworkRef.Resolve(this.Networks, reference) : null;

                    List<Item> rejected = new();
                    foreach (Item item in outbox.Where(item => item != null).ToList())
                    {
                        network?.Insert(item);
                        if (item.Stack > 0)
                            rejected.Add(item);
                    }
                    outbox.Clear();
                    mutex.ReleaseLock();

                    // Whatever the network couldn't take goes back.
                    this.Mail(farmer, rejected);
                    this.NotifyChanged();
                });
            }
        }

        /// <summary>Puts a farmhand's deposits into their outbox, for the host to store.</summary>
        private void SendPendingDeposits()
        {
            if (this.PendingDeposits.Count == 0 || this.DepositLocking)
                return;

            string key = ModIds.OutboxPrefix + Game1.player.UniqueMultiplayerID;
            NetMutex mutex = Game1.player.team.GetOrCreateGlobalInventoryMutex(key);
            this.DepositLocking = true;
            mutex.RequestLock(
                acquired: () =>
                {
                    IInventory outbox = Game1.player.team.GetOrCreateGlobalInventory(key);
                    foreach (Item item in this.PendingDeposits)
                        outbox.Add(item);
                    this.PendingDeposits.Clear();
                    mutex.ReleaseLock();
                    this.DepositLocking = false;
                },
                failed: () => this.DepositLocking = false
            );
        }

        /// <summary>Takes what the host has put in this farmhand's mailbox into their bag, dropping what won't fit.</summary>
        private void CollectMail()
        {
            string key = ModIds.MailboxPrefix + Game1.player.UniqueMultiplayerID;
            if (this.CollectLocking || !Game1.player.team.globalInventories.ContainsKey(key) || Game1.player.team.GetOrCreateGlobalInventory(key).Count == 0)
                return;

            NetMutex mutex = Game1.player.team.GetOrCreateGlobalInventoryMutex(key);
            this.CollectLocking = true;
            mutex.RequestLock(
                acquired: () =>
                {
                    IInventory mailbox = Game1.player.team.GetOrCreateGlobalInventory(key);
                    int dropped = 0;
                    foreach (Item item in mailbox.Where(item => item != null).ToList())
                    {
                        if (!Game1.player.addItemToInventoryBool(item))
                        {
                            dropped += item.Stack;
                            Game1.createItemDebris(item, Game1.player.getStandingPosition(), Game1.player.FacingDirection);
                        }
                    }
                    mailbox.Clear();
                    mutex.ReleaseLock();
                    this.CollectLocking = false;

                    Game1.playSound("dwop");
                    if (dropped > 0)
                        Game1.addHUDMessage(new HUDMessage(this.Text("mp.inventory-full", new { count = dropped }), HUDMessage.error_type));
                    Revision++;
                },
                failed: () => this.CollectLocking = false
            );
        }


        /*********
        ** Private methods: job views
        *********/
        /// <summary>A job as sent to farmhands.</summary>
        private static JobView ToView(CraftJob job)
        {
            return new JobView
            {
                Id = job.Id,
                Token = job.Token,
                TargetId = job.TargetId,
                TargetQuality = job.TargetQuality,
                DisplayName = job.DisplayName,
                TargetCount = job.TargetCount,
                Status = (int)job.Status,
                BlockedReason = job.BlockedReason,
                Progress = job.Progress,
                EtaMinutes = job.EstimatedMinutesRemaining,
                UseFairyDust = job.UseFairyDust,
                RuleKey = job.RuleKey,
                PlannedMinutes = job.PlannedMinutes,
                Delivered = job.Delivered,
                LocationName = job.LocationName,
                AnchorX = job.AnchorTile.X,
                AnchorY = job.AnchorTile.Y,
                Reserved = job.CropReservations
                    .Select(crop => new TileView { Location = crop.Location?.NameOrUniqueName, X = crop.Tile.X, Y = crop.Tile.Y, HarvesterX = crop.HarvesterTile.X, HarvesterY = crop.HarvesterTile.Y, ItemId = crop.ItemId, Days = crop.Days })
                    .Concat(job.Plantings.Select(planting => new TileView { Location = planting.Location?.NameOrUniqueName, X = planting.Tile.X, Y = planting.Tile.Y, HarvesterX = planting.HarvesterTile.X, HarvesterY = planting.HarvesterTile.Y, ItemId = planting.ItemId, SeedId = planting.SeedId, Days = planting.Days }))
                    .ToList()
            };
        }

        /// <summary>A job as a farmhand shows it: the host's figures, with its reserved tiles for the field view.</summary>
        private static CraftJob ToJob(JobView view)
        {
            CraftJob job = new()
            {
                Id = view.Id,
                Token = view.Token,
                TargetId = view.TargetId,
                TargetQuality = view.TargetQuality,
                DisplayName = view.DisplayName,
                TargetCount = view.TargetCount,
                LocationName = view.LocationName,
                AnchorTile = new Vector2(view.AnchorX, view.AnchorY),
                RuleKey = view.RuleKey,
                Status = (JobStatus)view.Status,
                BlockedReason = view.BlockedReason,
                UseFairyDust = view.UseFairyDust,
                PlannedMinutes = view.PlannedMinutes,
                Delivered = view.Delivered,
                RemoteProgress = view.Progress,
                RemoteEta = view.EtaMinutes
            };

            foreach (TileView tile in view.Reserved)
            {
                GameLocation location = Game1.getLocationFromName(tile.Location);
                if (location == null)
                    continue;

                if (tile.SeedId != null)
                    job.Plantings.Add(new PlannedPlanting { Location = location, Tile = new Vector2(tile.X, tile.Y), HarvesterTile = new Vector2(tile.HarvesterX, tile.HarvesterY), ItemId = tile.ItemId, SeedId = tile.SeedId, Days = tile.Days });
                else
                    job.CropReservations.Add(new IncomingCrop { Location = location, Tile = new Vector2(tile.X, tile.Y), HarvesterTile = new Vector2(tile.HarvesterX, tile.HarvesterY), ItemId = tile.ItemId, Days = tile.Days });
            }

            return job;
        }

        private string Text(string key, object tokens = null) => this.Helper.Translation.Get(key, tokens);
    }
}
