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
            this.Content = new ContentInjector(helper.Translation);

            helper.Events.Content.AssetRequested += this.OnAssetRequested;
            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            helper.Events.World.ObjectListChanged += this.OnObjectListChanged;
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
                e.Edit(asset => this.Content.EditFloors(asset.GetData<List<FloorPathData>>()));
            else if (e.NameWithoutLocale.IsEquivalentTo("Data/CraftingRecipes"))
                e.Edit(asset => this.Content.EditRecipes(asset.AsDictionary<string, string>().Data));
            else if (e.NameWithoutLocale.IsEquivalentTo(ModIds.TextureAsset))
                e.LoadFromModFile<Texture2D>("assets/craftables.png", AssetLoadPriority.Medium);
            else if (e.NameWithoutLocale.IsEquivalentTo(ModIds.CableFloorTexture))
                e.LoadFromModFile<Texture2D>("assets/cable-floor.png", AssetLoadPriority.Medium);
        }


        /*********
        ** Private methods: lifecycle
        *********/
        /// <summary>Registers the config menu once every mod has loaded.</summary>
        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
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
        }

        /// <summary>Clears cached networks when a save is loaded.</summary>
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            this.Networks.InvalidateAll();
        }

        /// <summary>Rescans the world each morning and teaches the player any recipes they've earned.</summary>
        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            this.Networks.InvalidateAll();
            this.UnlockRecipes();
        }

        /// <summary>Drops world state when returning to the title screen.</summary>
        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            this.Networks.InvalidateAll();
        }

        /// <summary>Rescans a location when something is placed or broken in it.</summary>
        private void OnObjectListChanged(object sender, ObjectListChangedEventArgs e)
        {
            this.Networks.Invalidate(e.Location);
        }

        /// <summary>Services wired machines on the host.</summary>
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            // Only the host moves items. Farmhands see the results through the game's own object sync, so running
            // this everywhere would move each item once per player.
            if (!Context.IsWorldReady || !Context.IsMainPlayer)
                return;

            // Respect the game's own sense of whether time is passing, so buses pause with the world.
            if (!Game1.shouldTimePass())
                return;

            if (e.IsMultipleOf((uint)this.Config.BusIntervalTicks))
                this.Ticker.Run();
        }

        /// <summary>Opens the terminal when the player activates one.</summary>
        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady || !Context.IsPlayerFree)
                return;

            bool isAction = e.Button.IsActionButton();
            bool isHotkey = this.Config.OpenTerminalKey.JustPressed();
            if (!isAction && !isHotkey)
                return;

            Vector2 tile = e.Cursor.GrabTile;
            GameLocation location = Game1.currentLocation;

            if (!location.Objects.TryGetValue(tile, out SObject obj) || obj == null)
                return;

            NodeKind? kind = NetworkNode.GetKind(obj.ItemId);
            if (kind is not (NodeKind.Terminal or NodeKind.CraftingTerminal))
                return;

            // Don't let the player operate a terminal from across the farm.
            if (!Utility.tileWithinRadiusOfPlayer((int)tile.X, (int)tile.Y, 1, Game1.player))
                return;

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
                    canCraft: kind == NodeKind.CraftingTerminal
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


        /*********
        ** Private methods: progression
        *********/
        /// <summary>Teaches the player every logistics recipe they've qualified for.</summary>
        private void UnlockRecipes()
        {
            if (!Context.IsWorldReady)
                return;

            foreach (KeyValuePair<string, int> recipe in ContentInjector.RecipeUnlockLevels)
            {
                if (!this.Config.UnlockAllRecipes && Game1.player.MiningLevel < recipe.Value)
                    continue;
                if (Game1.player.craftingRecipes.ContainsKey(recipe.Key))
                    continue;

                Game1.player.craftingRecipes.Add(recipe.Key, 0);
                Game1.addHUDMessage(new HUDMessage(this.Helper.Translation.Get("ui.recipe-learned", new { name = recipe.Key }), HUDMessage.newQuest_type));
            }
        }
    }
}
