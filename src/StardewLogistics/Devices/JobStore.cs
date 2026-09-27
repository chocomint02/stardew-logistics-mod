using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewLogistics.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Inventories;

namespace StardewLogistics.Devices
{
    /// <summary>Writes autocrafting jobs into the save, and reads them back when it's loaded.</summary>
    /// <remarks>
    /// Most of what a job needs to carry on is already in the world: its ingredients are in its buffer, a global
    /// inventory the game saves, and the machines it's using carry its claim in their <c>modData</c>. What's left
    /// is the job itself -- its steps, what's running where, the crops it's waiting on -- which goes into the
    /// mod's save data as plain records.
    ///
    /// A few items don't fit plain records: the exact inputs of each run in flight, kept so cancelling can give
    /// them back, and each recipe's output sample, which carries a wine's name, colour and price. Those go into a
    /// global inventory of the job's own, written as the game saves and emptied again once they're read back.
    /// They aren't items in the world -- the machine has the real ones -- so one left behind is deleted, never
    /// returned to storage.
    /// </remarks>
    internal class JobStore
    {
        /*********
        ** Fields
        *********/
        /// <summary>The save data key.</summary>
        private const string SaveKey = "jobs";

        /// <summary>The prefix of the global inventories holding a job's saved items.</summary>
        private const string ItemsPrefix = ModIds.ModId + "/job-items/";

        private readonly IDataHelper Data;
        private readonly JobRunner Jobs;
        private readonly MachineRecipeIndex MachineRecipes;


        /*********
        ** Public methods
        *********/
        public JobStore(IDataHelper data, JobRunner jobs, MachineRecipeIndex machineRecipes)
        {
            this.Data = data;
            this.Jobs = jobs;
            this.MachineRecipes = machineRecipes;
        }

        /// <summary>Writes every job into the save. Call as the game saves.</summary>
        public void Save()
        {
            if (!Context.IsMainPlayer)
                return;

            RemoveItemInventories();

            JobSaveData data = new();
            foreach (CraftJob job in this.Jobs.LocalJobs.Where(job => job.Status != JobStatus.Cancelled))
            {
                try
                {
                    data.Jobs.Add(SaveJob(job));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Couldn't save autocrafting job {job.Id} ({job.DisplayName}); its materials will return to storage when the save is loaded. {ex.Message}");
                }
            }

            try
            {
                this.Data.WriteSaveData(SaveKey, data.Jobs.Count > 0 ? data : null);
                if (data.Jobs.Count > 0)
                    Log.Trace($"Saved {data.Jobs.Count} autocrafting jobs.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't save autocrafting jobs; their materials will return to storage when the save is loaded. {ex.Message}");
            }
        }

        /// <summary>Reads the save's jobs back and hands them to the runner. Call once the save is loaded.</summary>
        public void Load()
        {
            if (!Context.IsMainPlayer)
                return;

            JobSaveData data;
            try
            {
                data = this.Data.ReadSaveData<JobSaveData>(SaveKey);
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't read the saved autocrafting jobs; their materials will return to storage. {ex.Message}");
                data = null;
            }

            List<CraftJob> restored = new();
            foreach (JobData saved in data?.Jobs ?? new List<JobData>())
            {
                try
                {
                    restored.Add(this.LoadJob(saved));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Couldn't restore the autocrafting job for {saved.DisplayName}; its materials will return to storage. {ex.Message}");
                }
            }

            // The saved items are all read now; the next save writes them afresh.
            RemoveItemInventories();

            this.Jobs.Restore(restored);
            if (restored.Count > 0)
                Log.Debug($"Restored {restored.Count} autocrafting jobs: {string.Join(", ", restored.Select(job => $"{job.TargetCount}x {job.DisplayName}"))}.");
        }


        /*********
        ** Private methods: saving
        *********/
        /// <summary>Records one job.</summary>
        private static JobData SaveJob(CraftJob job)
        {
            List<Item> items = new();

            JobData data = new()
            {
                Token = job.Token,
                TargetId = job.TargetId,
                DisplayName = job.DisplayName,
                TargetCount = job.TargetCount,
                TargetQuality = job.TargetQuality,
                LocationName = job.LocationName,
                AnchorX = (int)job.AnchorTile.X,
                AnchorY = (int)job.AnchorTile.Y,
                Status = job.Status,
                BlockedReason = job.BlockedReason,
                BufferKey = job.Buffer?.Key,
                FertilizerId = job.FertilizerId,
                UseFairyDust = job.UseFairyDust,
                RuleKey = job.RuleKey,
                PlannedMinutes = job.PlannedMinutes,
                Delivered = job.Delivered,
                ExcludedDay = job.ExcludedDay,
                Excluded = job.Excluded.Select(place => new PlaceData(place.Location, place.Tile)).ToList(),
                Crops = job.CropReservations.Select(crop => new CropData
                {
                    LocationName = crop.Location?.NameOrUniqueName,
                    X = (int)crop.Tile.X,
                    Y = (int)crop.Tile.Y,
                    HarvesterX = (int)crop.HarvesterTile.X,
                    HarvesterY = (int)crop.HarvesterTile.Y,
                    ItemId = crop.ItemId,
                    Count = crop.Count,
                    Days = crop.Days
                }).ToList(),
                Plantings = job.Plantings.Select(planting => new PlantingData
                {
                    LocationName = planting.Location?.NameOrUniqueName,
                    X = (int)planting.Tile.X,
                    Y = (int)planting.Tile.Y,
                    HarvesterX = (int)planting.HarvesterTile.X,
                    HarvesterY = (int)planting.HarvesterTile.Y,
                    SeedId = planting.SeedId,
                    FertilizerId = planting.FertilizerId,
                    ItemId = planting.ItemId,
                    Count = planting.Count,
                    Days = planting.Days
                }).ToList(),
                Steps = job.Steps.Select(step => new StepData
                {
                    Kind = step.Kind,
                    OutputId = step.OutputId,
                    DisplayName = step.DisplayName,
                    CraftRecipeName = step.CraftRecipe?.name,
                    MachineRecipe = step.MachineRecipe != null ? SaveRecipe(step.MachineRecipe, items) : null,
                    RemainingBatches = step.RemainingBatches,
                    CompletedBatches = step.CompletedBatches,
                    TotalBatches = step.TotalBatches,
                    DeliversToStorage = step.DeliversToStorage,
                    MaxMachines = step.MaxMachines,
                    Reserved = step.Reserved.Select(place => new PlaceData(place.Location, place.Tile)).ToList(),
                    InFlight = step.InFlight.Select(batch => new BatchData
                    {
                        LocationName = batch.LocationName,
                        X = (int)batch.Tile.X,
                        Y = (int)batch.Tile.Y,
                        ExpectedMinutes = batch.ExpectedMinutes,
                        MinutesLeft = batch.MinutesLeft,
                        Yield = batch.Yield,
                        Runs = batch.Runs,
                        Dusted = batch.Dusted,
                        Inputs = batch.Inputs.Select(input => Store(input, items)).Where(index => index >= 0).ToList()
                    }).ToList()
                }).ToList()
            };

            if (items.Count > 0)
            {
                Inventory inventory = Game1.player.team.GetOrCreateGlobalInventory(ItemsPrefix + job.Token);
                inventory.Clear();
                foreach (Item item in items)
                    inventory.Add(item);
            }

            return data;
        }

        /// <summary>Records a machine recipe.</summary>
        private static RecipeData SaveRecipe(MachineRecipe recipe, List<Item> items)
        {
            return new RecipeData
            {
                MachineId = recipe.MachineId,
                MachineName = recipe.MachineName,
                InputId = recipe.InputId,
                InputTags = recipe.InputTags.ToList(),
                InputCount = recipe.InputCount,
                InputQuality = recipe.InputQuality,
                ExtraInputs = recipe.ExtraInputs.Select(extra => new CostData { ItemId = extra.ItemId, Count = extra.Count, Quality = extra.RequiredQuality }).ToList(),
                OutputId = recipe.OutputId,
                OutputSample = Store(recipe.OutputSample, items),
                IsAging = recipe.IsAging,
                TargetQuality = recipe.TargetQuality,
                AgingRate = recipe.AgingRate,
                FromStock = recipe.FromStock,
                OutputCount = recipe.OutputCount,
                MaxOutputCount = recipe.MaxOutputCount,
                Minutes = recipe.BaseMinutes,
                Days = recipe.BaseDays,
                RecipeExtras = recipe.RecipeExtras
            };
        }

        /// <summary>Adds a copy of an item to the job's saved items.</summary>
        /// <returns>Its index there, or -1 if there's no item.</returns>
        /// <remarks>A copy, so the same item can't end up in two lists, and nothing live is touched.</remarks>
        private static int Store(Item item, List<Item> items)
        {
            if (item == null)
                return -1;

            Item copy = item.getOne();
            copy.Stack = Math.Max(1, item.Stack);
            items.Add(copy);
            return items.Count - 1;
        }


        /*********
        ** Private methods: loading
        *********/
        /// <summary>Rebuilds one job.</summary>
        private CraftJob LoadJob(JobData data)
        {
            if (string.IsNullOrEmpty(data.Token) || string.IsNullOrEmpty(data.BufferKey))
                throw new InvalidOperationException("the saved job has no token.");

            string itemsKey = ItemsPrefix + data.Token;
            IList<Item> items = Game1.player.team.globalInventories.ContainsKey(itemsKey)
                ? Game1.player.team.GetOrCreateGlobalInventory(itemsKey).ToList()
                : new List<Item>();

            Item Read(int index)
            {
                Item saved = index >= 0 && index < items.Count ? items[index] : null;
                if (saved == null)
                    return null;

                Item copy = saved.getOne();
                copy.Stack = Math.Max(1, saved.Stack);
                return copy;
            }

            JobBuffer buffer = JobBuffer.FromKey(data.BufferKey);
            CraftJob job = new()
            {
                Id = "?",
                Token = data.Token,
                Buffer = buffer,
                TargetId = data.TargetId,
                TargetQuality = data.TargetQuality,
                DisplayName = StockId.GetDisplayName(data.TargetId) ?? data.DisplayName,
                TargetCount = data.TargetCount,
                LocationName = data.LocationName,
                AnchorTile = new Vector2(data.AnchorX, data.AnchorY),
                FertilizerId = data.FertilizerId,
                RuleKey = data.RuleKey,
                Status = data.Status,
                BlockedReason = data.BlockedReason,
                UseFairyDust = data.UseFairyDust,
                PlannedMinutes = data.PlannedMinutes,
                Delivered = data.Delivered,
                ExcludedDay = data.ExcludedDay,
                Steps = data.Steps.Select(step => this.LoadStep(step, Read)).ToList()
            };

            job.Excluded.AddRange(data.Excluded.Select(place => (place.LocationName, new Vector2(place.X, place.Y))));

            foreach (CropData crop in data.Crops)
            {
                GameLocation location = Game1.getLocationFromName(crop.LocationName);
                if (location == null)
                    continue;

                job.CropReservations.Add(new IncomingCrop
                {
                    Location = location,
                    Tile = new Vector2(crop.X, crop.Y),
                    HarvesterTile = new Vector2(crop.HarvesterX, crop.HarvesterY),
                    ItemId = crop.ItemId,
                    Count = crop.Count,
                    Days = crop.Days
                });
            }

            foreach (PlantingData planting in data.Plantings)
            {
                GameLocation location = Game1.getLocationFromName(planting.LocationName);
                if (location == null)
                    continue;

                job.Plantings.Add(new PlannedPlanting
                {
                    Location = location,
                    Tile = new Vector2(planting.X, planting.Y),
                    HarvesterTile = new Vector2(planting.HarvesterX, planting.HarvesterY),
                    SeedId = planting.SeedId,
                    FertilizerId = planting.FertilizerId,
                    ItemId = planting.ItemId,
                    Count = planting.Count,
                    Days = planting.Days,
                    Buffer = buffer
                });
            }

            return job;
        }

        /// <summary>Rebuilds one step.</summary>
        private JobStep LoadStep(StepData data, Func<int, Item> read)
        {
            CraftingRecipe craft = null;
            if (data.Kind == PlanStepKind.Craft)
            {
                craft = !string.IsNullOrEmpty(data.CraftRecipeName) && CraftingRecipe.craftingRecipes.ContainsKey(data.CraftRecipeName)
                    ? new CraftingRecipe(data.CraftRecipeName, isCookingRecipe: false)
                    : throw new InvalidOperationException($"the crafting recipe '{data.CraftRecipeName}' no longer exists.");
            }

            JobStep step = new()
            {
                Kind = data.Kind,
                OutputId = data.OutputId,
                DisplayName = StockId.GetDisplayName(data.OutputId) ?? data.DisplayName,
                CraftRecipe = craft,
                MachineRecipe = data.MachineRecipe != null ? this.LoadRecipe(data.MachineRecipe, read) : null,
                RemainingBatches = data.RemainingBatches,
                CompletedBatches = data.CompletedBatches,
                TotalBatches = data.TotalBatches,
                DeliversToStorage = data.DeliversToStorage,
                MaxMachines = data.MaxMachines
            };

            step.Reserved.AddRange(data.Reserved.Select(place => (place.LocationName, new Vector2(place.X, place.Y))));
            foreach (BatchData batch in data.InFlight)
            {
                step.InFlight.Add(new RunningBatch
                {
                    LocationName = batch.LocationName,
                    Tile = new Vector2(batch.X, batch.Y),
                    ExpectedMinutes = batch.ExpectedMinutes,
                    MinutesLeft = batch.MinutesLeft,
                    Yield = batch.Yield,
                    Runs = Math.Max(1, batch.Runs),
                    Dusted = batch.Dusted,
                    Inputs = batch.Inputs.Select(read).Where(item => item != null).ToList()
                });
            }

            return step;
        }

        /// <summary>Rebuilds a machine recipe.</summary>
        /// <remarks>The index's own copy if it still has one, so the recipe is the same object the planner uses; otherwise as saved.</remarks>
        private MachineRecipe LoadRecipe(RecipeData data, Func<int, Item> read)
        {
            MachineRecipe recipe = new()
            {
                MachineId = data.MachineId,
                MachineName = data.MachineName,
                InputId = data.InputId,
                InputTags = data.InputTags ?? new List<string>(),
                InputCount = data.InputCount,
                InputQuality = data.InputQuality,
                ExtraInputs = (data.ExtraInputs ?? new List<CostData>()).Select(extra => new ItemCost(extra.ItemId, extra.Count, extra.Quality)).ToList(),
                OutputId = data.OutputId,
                OutputSample = read(data.OutputSample),
                IsAging = data.IsAging,
                TargetQuality = data.TargetQuality,
                AgingRate = data.AgingRate,
                FromStock = data.FromStock,
                OutputCount = data.OutputCount,
                MaxOutputCount = data.MaxOutputCount,
                Minutes = data.Minutes,
                Days = data.Days,
                RecipeExtras = data.RecipeExtras
            };

            return this.MachineRecipes.All.FirstOrDefault(known => known.Key == recipe.Key) ?? recipe;
        }

        /// <summary>Deletes every job's saved items.</summary>
        private static void RemoveItemInventories()
        {
            foreach (string key in Game1.player.team.globalInventories.Keys.Where(key => key.StartsWith(ItemsPrefix, StringComparison.Ordinal)).ToList())
                Game1.player.team.globalInventories.Remove(key);
        }


        /*********
        ** Save models
        *********/
        internal class JobSaveData
        {
            public int Version { get; set; } = 1;
            public List<JobData> Jobs { get; set; } = new();
        }

        internal class JobData
        {
            public string Token { get; set; }
            public string TargetId { get; set; }
            public string DisplayName { get; set; }
            public int TargetCount { get; set; }
            public int TargetQuality { get; set; } = Quality.Any;
            public string LocationName { get; set; }
            public int AnchorX { get; set; }
            public int AnchorY { get; set; }
            public JobStatus Status { get; set; }
            public string BlockedReason { get; set; }
            public string BufferKey { get; set; }
            public string FertilizerId { get; set; }
            public bool UseFairyDust { get; set; }
            public string RuleKey { get; set; }
            public int PlannedMinutes { get; set; } = -1;
            public int Delivered { get; set; }
            public int ExcludedDay { get; set; }
            public List<PlaceData> Excluded { get; set; } = new();
            public List<CropData> Crops { get; set; } = new();
            public List<PlantingData> Plantings { get; set; } = new();
            public List<StepData> Steps { get; set; } = new();
        }

        internal class StepData
        {
            public PlanStepKind Kind { get; set; }
            public string OutputId { get; set; }
            public string DisplayName { get; set; }
            public string CraftRecipeName { get; set; }
            public RecipeData MachineRecipe { get; set; }
            public int RemainingBatches { get; set; }
            public int CompletedBatches { get; set; }
            public int TotalBatches { get; set; }
            public bool DeliversToStorage { get; set; }
            public int MaxMachines { get; set; }
            public List<PlaceData> Reserved { get; set; } = new();
            public List<BatchData> InFlight { get; set; } = new();
        }

        internal class BatchData
        {
            public string LocationName { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int ExpectedMinutes { get; set; }
            public int MinutesLeft { get; set; }
            public int Yield { get; set; }
            public int Runs { get; set; } = 1;
            public bool Dusted { get; set; }

            /// <summary>Indexes into the job's saved items.</summary>
            public List<int> Inputs { get; set; } = new();
        }

        internal class RecipeData
        {
            public string MachineId { get; set; }
            public string MachineName { get; set; }
            public string InputId { get; set; }
            public List<string> InputTags { get; set; } = new();
            public int InputCount { get; set; }
            public int InputQuality { get; set; } = Quality.Any;
            public List<CostData> ExtraInputs { get; set; } = new();
            public string OutputId { get; set; }

            /// <summary>An index into the job's saved items, or -1.</summary>
            public int OutputSample { get; set; } = -1;
            public bool IsAging { get; set; }
            public int TargetQuality { get; set; } = Quality.Any;
            public float AgingRate { get; set; } = 1f;
            public bool FromStock { get; set; }
            public int OutputCount { get; set; }
            public int MaxOutputCount { get; set; }
            public int Minutes { get; set; }
            public int Days { get; set; }
            public int RecipeExtras { get; set; }
        }

        internal class CostData
        {
            public string ItemId { get; set; }
            public int Count { get; set; }
            public int Quality { get; set; } = Framework.Quality.Any;
        }

        internal class PlaceData
        {
            public string LocationName { get; set; }
            public int X { get; set; }
            public int Y { get; set; }

            public PlaceData() { }

            public PlaceData(string locationName, Vector2 tile)
            {
                this.LocationName = locationName;
                this.X = (int)tile.X;
                this.Y = (int)tile.Y;
            }
        }

        internal class CropData
        {
            public string LocationName { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public int HarvesterX { get; set; }
            public int HarvesterY { get; set; }
            public string ItemId { get; set; }
            public int Count { get; set; }
            public int Days { get; set; }
        }

        internal class PlantingData : CropData
        {
            public string SeedId { get; set; }
            public string FertilizerId { get; set; }
        }
    }
}
