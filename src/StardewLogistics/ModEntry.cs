using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewLogistics.Devices;
using StardewLogistics.Framework;
using StardewLogistics.Integrations;
using StardewLogistics.Menus;
using StardewLogistics.Network;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.BigCraftables;
using StardewValley.GameData.FloorsAndPaths;
using StardewValley.GameData.Objects;
using StardewValley.TerrainFeatures;
using System.Linq;
using SObject = StardewValley.Object;

namespace StardewLogistics
{
    /// <summary>The mod entry point.</summary>
    public class ModEntry : Mod
    {
        /*********
        ** Fields
        *********/
        private ModConfig Config;
        private NetworkManager Networks;
        private NetworkTicker Ticker;
        private ContentInjector Content;
        private MachineRecipeIndex MachineRecipes;
        private RecipeIndex CraftingRecipes;
        private JobRunner Jobs;
        private HarvesterRunner Harvesters;
        private StockKeeper Stock;
        private ShippingLedger Ledger;

        /// <summary>Saves autocrafting jobs with the game, and restores them on load.</summary>
        private JobStore JobStore;
        private Multiplayer.MultiplayerSync Sync;


        /*********
        ** Public methods
        *********/
        /// <inheritdoc />
        public override void Entry(IModHelper helper)
        {
            this.Config = helper.ReadConfig<ModConfig>();
            this.Config.Normalise();

            Log.Initialise(this.Monitor);
            ItemSource.Initialise(helper.ModRegistry);

            this.Networks = new NetworkManager(this.Config);
            this.Ticker = new NetworkTicker(this.Networks, this.Config);
            this.MachineRecipes = new MachineRecipeIndex();
            this.CraftingRecipes = new RecipeIndex();

            this.Jobs = new JobRunner(this.Networks, this.MachineRecipes, this.CraftingRecipes, this.Config);
            this.Ticker.IsLiveClaim = this.Jobs.IsLiveClaim;

            HarmonyLib.Harmony harmony = new(this.ModManifest.UniqueID);
            CaskPatches.Apply(harmony, this.Jobs.ReclaimFromCask);
            this.Harvesters = new HarvesterRunner(this.Networks, helper.Translation);
            SoilPatches.Apply(harmony, this.Harvesters.IsProtected);
            AccessorySlot.Apply(harmony, helper.Translation, () => this.Config.OpenWirelessTerminalKey.ToString());

            // Growing crops feed autocrafting, and reserved ones go to their job when harvested.
            this.Jobs.Forecast = this.Harvesters.Forecast;
            this.Jobs.HarvestersOn = network => this.Harvesters.GetHarvestersOn(network);
            this.Harvesters.ClaimHarvest = this.Jobs.ClaimHarvest;

            // Automation tiles: jobs plant crops they need there, and the harvester does the planting.
            this.Jobs.FreeTilesOn = this.Harvesters.FreeTiles;
            this.Jobs.PlantNow = () =>
            {
                if (Context.IsMainPlayer)
                    this.Harvesters.Run();
            };
            this.Harvesters.PlantingFor = this.Jobs.PlantingFor;
            this.Harvesters.PlantingDone = this.Jobs.PlantingDone;

            // Minimum-stock rules, which queue jobs of their own.
            this.Stock = new StockKeeper(this.Networks, this.Jobs, helper.Translation);
            this.Jobs.Stock = this.Stock;

            // The history of what each day earned.
            this.Ledger = new ShippingLedger(helper.Data);
            this.Jobs.Ledger = this.Ledger;

            // Farmhands' terminals act through the host, and see what only the host knows.
            this.Sync = new Multiplayer.MultiplayerSync(helper, this.ModManifest.UniqueID, this.Networks, this.Jobs);
            helper.Events.GameLoop.Saving += (_, _) => this.Ledger.CloseDay();

            // Jobs carry on across a reload: written as the game saves, read back when it loads.
            this.JobStore = new JobStore(helper.Data, this.Jobs, this.MachineRecipes);
            helper.Events.GameLoop.Saving += (_, _) => this.JobStore.Save();

            new ConsoleCommands(this.MachineRecipes, this.CraftingRecipes, this.Networks, this.Config, this.Jobs)
                .Register(helper.ConsoleCommands);
            this.Content = new ContentInjector(helper.Translation);

            helper.Events.Content.AssetRequested += this.OnAssetRequested;
            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.GameLoop.DayEnding += this.OnDayEnding;
            helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            helper.Events.World.ObjectListChanged += this.OnObjectListChanged;
            helper.Events.World.TerrainFeatureListChanged += this.OnTerrainFeatureListChanged;
            helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
            helper.Events.GameLoop.TimeChanged += this.OnTimeChanged;
        }


        /*********
        ** Private methods: content
        *********/
        /// <summary>Injects the mod's craftables, recipes and spritesheet into the game's assets.</summary>
        private void OnAssetRequested(object sender, AssetRequestedEventArgs e)
        {
            if (e.NameWithoutLocale.IsEquivalentTo("Data/BigCraftables"))
                e.Edit(asset => this.Content.EditBigCraftables(asset.AsDictionary<string, BigCraftableData>().Data));
            else if (e.NameWithoutLocale.IsEquivalentTo("Data/Objects"))
                e.Edit(asset => this.Content.EditObjects(asset.AsDictionary<string, ObjectData>().Data));
            else if (e.NameWithoutLocale.IsEquivalentTo("Data/FloorsAndPaths"))
                e.Edit(asset => this.Content.EditFloors(asset.AsDictionary<string, FloorPathData>().Data));
            else if (e.NameWithoutLocale.IsEquivalentTo("Data/CraftingRecipes"))
                e.Edit(asset => this.Content.EditRecipes(asset.AsDictionary<string, string>().Data));
            else if (e.NameWithoutLocale.IsEquivalentTo(ModIds.TextureAsset))
                e.LoadFromModFile<Texture2D>("assets/craftables.png", AssetLoadPriority.Medium);
            else if (e.NameWithoutLocale.IsEquivalentTo(ModIds.CableFloorTexture))
                e.LoadFromModFile<Texture2D>("assets/cable-floor.png", AssetLoadPriority.Medium);
            else if (e.NameWithoutLocale.IsEquivalentTo(ModIds.UiIconsTexture))
                e.LoadFromModFile<Texture2D>("assets/ui-icons.png", AssetLoadPriority.Medium);
            else if (e.NameWithoutLocale.IsEquivalentTo(ModIds.ItemsTexture))
                e.LoadFromModFile<Texture2D>("assets/items.png", AssetLoadPriority.Medium);
        }


        /*********
        ** Private methods: lifecycle
        *********/
        /// <summary>Registers the config menu once every mod has loaded.</summary>
        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            this.WarnAboutCompetingAutomation();

            var api = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (api == null)
                return;

            var i18n = this.Helper.Translation;
            api.Register(
                mod: this.ModManifest,
                reset: () =>
                {
                    this.Config = new ModConfig();
                    this.Config.Normalise();
                },
                save: () =>
                {
                    this.Config.Normalise();
                    this.Helper.WriteConfig(this.Config);
                    this.Networks.InvalidateAll();
                }
            );

            api.AddSectionTitle(this.ModManifest, () => i18n.Get("config.section.automation"));
            api.AddNumberOption(this.ModManifest, () => this.Config.BusIntervalTicks, value => this.Config.BusIntervalTicks = value, () => i18n.Get("config.bus-interval.name"), () => i18n.Get("config.bus-interval.tooltip"), 6, 600);
            api.AddNumberOption(this.ModManifest, () => this.Config.BusItemsPerRun, value => this.Config.BusItemsPerRun = value, () => i18n.Get("config.bus-rate.name"), () => i18n.Get("config.bus-rate.tooltip"), 1, 999);
            api.AddBoolOption(this.ModManifest, () => this.Config.EnableMachineAutomation, value => this.Config.EnableMachineAutomation = value, () => i18n.Get("config.machines.name"), () => i18n.Get("config.machines.tooltip"));

            api.AddSectionTitle(this.ModManifest, () => i18n.Get("config.section.general"));
            api.AddBoolOption(this.ModManifest, () => this.Config.UnlockAllRecipes, value => this.Config.UnlockAllRecipes = value, () => i18n.Get("config.recipes.name"), () => i18n.Get("config.recipes.tooltip"));
            api.AddKeybindList(this.ModManifest, () => this.Config.OpenTerminalKey, value => this.Config.OpenTerminalKey = value, () => i18n.Get("config.terminal-key.name"), () => i18n.Get("config.terminal-key.tooltip"));
            api.AddKeybindList(this.ModManifest, () => this.Config.OpenWirelessTerminalKey, value => this.Config.OpenWirelessTerminalKey = value, () => i18n.Get("config.wireless-key.name"), () => i18n.Get("config.wireless-key.tooltip"));
        }

        /// <summary>Clears cached networks when a save is loaded.</summary>
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            this.Networks.InvalidateAll();

            // Machine data is only readable once content is loaded, so the processing recipes are derived here
            // rather than at startup.
            this.MachineRecipes.Rebuild();
            this.Ledger.Load();

            // Before the scheduler's first pass, or it would take the jobs' buffers for leftovers and empty them.
            this.JobStore.Load();
        }

        /// <summary>Rescans the world each morning and teaches the player any recipes they've earned.</summary>
        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            this.Networks.InvalidateAll();
            this.UnlockRecipes();

            // Crops grew overnight: harvest what's ready and plant for the new day first thing. Then top up
            // stock, with whatever the harvest brought in.
            this.Harvesters.Invalidate();
            this.Stock.OnDayStarted();
            // Normally closed as the game saved overnight; this catches a night that didn't save.
            this.Ledger.CloseDay();

            if (Context.IsMainPlayer)
            {
                this.Harvesters.RestoreSoil();
                this.Harvesters.Run();
                this.Stock.Run();
            }
        }

        /// <summary>Notes the soil under auto-harvesters before the night, so it stays tilled through it.</summary>
        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            if (Context.IsMainPlayer)
                this.Harvesters.BeforeNight();

            // What's in the shipping bins, before the night sells it.
            this.Ledger.BeforeNight();
        }

        /// <summary>Drops world state when returning to the title screen.</summary>
        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            this.Networks.InvalidateAll();

            // Jobs belong to the save that queued them; carried into the next save they'd drive its machines.
            this.Jobs.Reset();
            this.Stock.Reset();
            this.Ledger.Reset();
            this.Sync.Reset();
        }

        /// <summary>Lets auto-harvesters work through the day, so a new plan starts within ten minutes, and tops up stock.</summary>
        private void OnTimeChanged(object sender, TimeChangedEventArgs e)
        {
            if (!Context.IsMainPlayer)
                return;

            this.Harvesters.Run();
            this.Stock.Run();
        }

        /// <summary>Rescans a location when something is placed or broken in it.</summary>
        private void OnObjectListChanged(object sender, ObjectListChangedEventArgs e)
        {
            // Before the location is rescanned, while the network still knows what the removed machines were
            // wired to. Only the host moves items, as everywhere else.
            if (Context.IsMainPlayer)
            {
                foreach (KeyValuePair<Vector2, SObject> removed in e.Removed)
                {
                    if (this.Jobs.HandleMachineRemoved(e.Location, removed.Key, removed.Value))
                        continue;

                    MachineIO.RefundRemoved(removed.Value, this.Networks.GetNetworkTouching(e.Location, removed.Key), e.Location, removed.Key);
                }
            }

            this.Networks.Invalidate(e.Location);

            // Where the wireless devices are is worked out across the whole world, so only placing or removing
            // one of them makes that search run again.
            if (e.Added.Any(pair => NetworkManager.IsWirelessDevice(pair.Value)) || e.Removed.Any(pair => NetworkManager.IsWirelessDevice(pair.Value)))
                this.Networks.InvalidateWireless();

            if (e.Added.Any(pair => pair.Value?.ItemId == ModIds.AutoHarvester) || e.Removed.Any(pair => pair.Value?.ItemId == ModIds.AutoHarvester))
                this.Harvesters.Invalidate();

            static bool IsTerminal(SObject obj) => obj != null && NetworkNode.GetKind(obj.ItemId) is NodeKind.Terminal or NodeKind.CraftingTerminal;
            if (e.Added.Any(pair => IsTerminal(pair.Value)) || e.Removed.Any(pair => IsTerminal(pair.Value)))
                this.Stock.Invalidate();
        }

        /// <summary>Rescans a location when cable is laid or lifted there.</summary>
        /// <remarks>
        /// Cable is floor, so laying it doesn't change the object list. Without this, a run of new cable only
        /// joined the network once something else in the location happened to trigger a rescan.
        /// </remarks>
        private void OnTerrainFeatureListChanged(object sender, TerrainFeatureListChangedEventArgs e)
        {
            static bool IsCable(TerrainFeature feature) => feature is Flooring floor && floor.whichFloor.Value == ModIds.CableFloorId;

            if (e.Added.Any(pair => IsCable(pair.Value)) || e.Removed.Any(pair => IsCable(pair.Value)))
                this.Networks.Invalidate(e.Location);
        }

        /// <summary>Shows each nearby wireless device's channel above it.</summary>
        /// <remarks>
        /// Two receivers look identical, so without this the only way to tell what one is tuned to is to open it.
        /// Only devices near the player are labelled, to keep a busy farm readable.
        /// </remarks>
        private void OnRenderedWorld(object sender, RenderedWorldEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.currentLocation == null || Game1.eventUp)
                return;

            const int radius = 6;
            Vector2 player = Game1.player.Tile;

            foreach ((Vector2 tile, SObject obj) in Game1.currentLocation.Objects.Pairs)
            {
                if (obj?.ItemId == ModIds.AutoHarvester)
                {
                    this.DrawHarvesterArea(e.SpriteBatch, tile, obj);
                    continue;
                }

                if (!NetworkManager.IsWirelessDevice(obj) || Vector2.Distance(tile, player) > radius)
                    continue;

                string label = NetworkNode.GetChannel(obj).ToString();
                Vector2 size = Game1.smallFont.MeasureString(label) * 0.6f;

                // A big craftable stands two tiles tall from its tile; the label sits just above its top.
                Vector2 top = Game1.GlobalToLocal(Game1.viewport, new Vector2((tile.X * Game1.tileSize) + (Game1.tileSize / 2f), (tile.Y - 1) * Game1.tileSize));
                Rectangle plate = new((int)(top.X - (size.X / 2) - 6), (int)(top.Y - size.Y - 10), (int)size.X + 12, (int)size.Y + 4);

                e.SpriteBatch.Draw(Game1.staminaRect, plate, new Color(26, 22, 32) * 0.8f);
                e.SpriteBatch.DrawString(Game1.smallFont, label, new Vector2(plate.X + 6, plate.Y + 2), Color.White, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 1f);
            }
        }

        /// <summary>Shades an auto-harvester's area green, when it's set to show or its menu is open.</summary>
        private void DrawHarvesterArea(SpriteBatch b, Vector2 machineTile, SObject machine)
        {
            bool editing = Game1.activeClickableMenu is HarvesterMenu menu && menu.Machine == machine;
            HarvesterSettings settings = HarvesterSettings.ReadCached(machine);
            if (!settings.ShowPreview && !editing)
                return;

            Rectangle area = settings.GetArea(machineTile);
            Color fill = Color.LimeGreen * (editing ? 0.35f : 0.2f);
            for (int y = area.Top; y < area.Bottom; y++)
            {
                for (int x = area.Left; x < area.Right; x++)
                {
                    Vector2 local = Game1.GlobalToLocal(Game1.viewport, new Vector2(x * Game1.tileSize, y * Game1.tileSize));
                    b.Draw(Game1.staminaRect, new Rectangle((int)local.X, (int)local.Y, Game1.tileSize, Game1.tileSize), fill);
                }
            }

            // An outline, so the edge reads even over green grass.
            Vector2 corner = Game1.GlobalToLocal(Game1.viewport, new Vector2(area.X * Game1.tileSize, area.Y * Game1.tileSize));
            int w = area.Width * Game1.tileSize;
            int h = area.Height * Game1.tileSize;
            Color edge = Color.LimeGreen * 0.9f;
            b.Draw(Game1.staminaRect, new Rectangle((int)corner.X, (int)corner.Y, w, 3), edge);
            b.Draw(Game1.staminaRect, new Rectangle((int)corner.X, (int)corner.Y + h - 3, w, 3), edge);
            b.Draw(Game1.staminaRect, new Rectangle((int)corner.X, (int)corner.Y, 3, h), edge);
            b.Draw(Game1.staminaRect, new Rectangle((int)corner.X + w - 3, (int)corner.Y, 3, h), edge);
        }

        /// <summary>Services wired machines on the host.</summary>
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            // Only the host moves items. Farmhands see the results through the game's own object sync, so running
            // this everywhere would move each item once per player.
            if (!Context.IsWorldReady)
                return;

            // Every player: items in and out of the hand-off inventories, and news between host and farmhands.
            this.Sync.Update();

            if (!Context.IsMainPlayer)
                return;

            if (!e.IsMultipleOf((uint)this.Config.BusIntervalTicks))
                return;

            // Jobs first: the job collector must get its claimed machines before the general ticker sweeps them,
            // otherwise a job's output is swept into storage as ordinary machine output and the job only learns
            // about it second-hand.
            //
            // Jobs run even while time is stopped. In single player time stops whenever a menu is open, and
            // gating jobs on it meant an order placed from the terminal sat untouched until the terminal closed.
            // Loading a machine doesn't need the clock; the machine only counts down once time moves again.
            this.Jobs.Run();

            // Buses and machine collection do respect it, so they pause with the world.
            if (Game1.shouldTimePass())
                this.Ticker.Run();
        }

        /// <summary>Opens the terminal when the player activates one.</summary>
        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady || !Context.IsPlayerFree)
                return;

            bool isAction = e.Button.IsActionButton();
            // The Wireless Terminal opens from anywhere, if one is equipped.
            if (this.Config.OpenWirelessTerminalKey.JustPressed())
            {
                this.Helper.Input.SuppressActiveKeybinds(this.Config.OpenWirelessTerminalKey);
                this.OpenWirelessTerminal();
                return;
            }

            bool isHotkey = this.Config.OpenTerminalKey.JustPressed();
            if (!isAction && !isHotkey)
                return;

            Vector2 tile = e.Cursor.GrabTile;
            GameLocation location = Game1.currentLocation;

            if (!location.Objects.TryGetValue(tile, out SObject obj) || obj == null)
                return;

            NodeKind? kind = NetworkNode.GetKind(obj.ItemId);
            if (kind is not (NodeKind.Terminal or NodeKind.CraftingTerminal or NodeKind.WirelessTransmitter or NodeKind.WirelessReceiver or NodeKind.Harvester))
                return;

            // Don't let the player operate a device from across the farm.
            if (!Utility.tileWithinRadiusOfPlayer((int)tile.X, (int)tile.Y, 1, Game1.player))
                return;

            if (kind == NodeKind.Harvester)
            {
                if (!isAction)
                    return;

                this.Helper.Input.Suppress(e.Button);
                Game1.playSound("bigSelect");
                Game1.activeClickableMenu = new HarvesterMenu(this.Networks, this.Helper.Translation, location, tile, obj, onPlanSaved: (where, at, machine) =>
                {
                    // Start on a new plan straight away rather than at the next ten-minute tick.
                    StorageNetwork network = this.Networks.GetNetworkAt(where, at);
                    if (Context.IsMainPlayer && network != null)
                        this.Harvesters.Work(where, at, machine, network);
                }, this.Jobs.GetReservation);
                return;
            }

            if (kind is NodeKind.WirelessTransmitter or NodeKind.WirelessReceiver)
            {
                // Only the action button tunes a device; the terminal hotkey is for terminals.
                if (!isAction)
                    return;

                this.Helper.Input.Suppress(e.Button);
                Game1.playSound("bigSelect");
                Game1.activeClickableMenu = new WirelessMenu(this.Networks, this.Helper.Translation, location, tile, obj);
                return;
            }

            this.Helper.Input.Suppress(e.Button);

            // Build the menu defensively: a failure here is recoverable, and swallowing it into SMAPI's generic
            // event-handler catch would lose the context that makes it diagnosable.
            try
            {
                TerminalMenu menu = new(
                    this.Networks,
                    this.Helper.Translation,
                    location,
                    tile,
                    canCraft: kind == NodeKind.CraftingTerminal,
                    this.MachineRecipes,
                    this.Jobs,
                    this.Config
                );

                Game1.playSound("bigSelect");
                Game1.activeClickableMenu = menu;
            }
            catch (Exception ex)
            {
                Log.Error($"Couldn't open the terminal at {location.NameOrUniqueName} ({tile.X}, {tile.Y}).", ex);
                Game1.addHUDMessage(new HUDMessage(this.Helper.Translation.Get("error.terminal-failed"), HUDMessage.error_type));
            }
        }


        /// <summary>Opens the equipped Wireless Terminal, or says there isn't one.</summary>
        private void OpenWirelessTerminal()
        {
            Item terminal = AccessorySlot.GetTerminal(Game1.player);
            if (terminal == null)
            {
                Game1.showRedMessage(this.Helper.Translation.Get("wireless-terminal.none"));
                return;
            }

            try
            {
                Game1.playSound("bigSelect");
                Game1.activeClickableMenu = new TerminalMenu(this.Networks, this.Helper.Translation, Game1.currentLocation, Game1.player.Tile, canCraft: true, this.MachineRecipes, this.Jobs, this.Config, wirelessTerminal: terminal);
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't open the Wireless Terminal.", ex);
                Game1.addHUDMessage(new HUDMessage(this.Helper.Translation.Get("error.terminal-failed"), HUDMessage.error_type));
            }
        }


        /*********
        ** Private methods: progression
        *********/
        /// <summary>Warns when another mod automates the same machines this one does.</summary>
        /// <remarks>
        /// Automate feeds and empties machines from adjacent chests. Both mods working the same furnace is not
        /// harmful -- autocrafting counts a run whose output was collected by something else -- but it does mean
        /// machines get loaded without an autocrafting job asking for it, which looks like a bug if you don't
        /// know the other mod is doing it.
        /// </remarks>
        private void WarnAboutCompetingAutomation()
        {
            if (!this.Helper.ModRegistry.IsLoaded("Pathoschild.Automate"))
                return;

            this.Monitor.Log(
                "Automate is installed. If it can see the same machines as a logistics network, it will load and"
                + " empty them on its own, which can look like autocrafting misbehaving. Keep the two on separate"
                + " machines, or don't put a plain chest next to a machine you want autocrafting to drive.",
                LogLevel.Info
            );
        }

        /// <summary>Teaches the player every logistics recipe they've qualified for.</summary>
        private void UnlockRecipes()
        {
            if (!Context.IsWorldReady)
                return;

            foreach (KeyValuePair<string, (int Skill, int Level)> recipe in ContentInjector.RecipeUnlockLevels)
            {
                if (!this.Config.UnlockAllRecipes && Game1.player.GetSkillLevel(recipe.Value.Skill) < recipe.Value.Level)
                    continue;
                if (Game1.player.craftingRecipes.ContainsKey(recipe.Key))
                    continue;

                Game1.player.craftingRecipes.Add(recipe.Key, 0);
                Game1.addHUDMessage(new HUDMessage(this.Helper.Translation.Get("ui.recipe-learned", new { name = recipe.Key }), HUDMessage.newQuest_type));
            }
        }
    }
}
