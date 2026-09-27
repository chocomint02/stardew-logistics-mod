using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using SObject = StardewValley.Object;

namespace StardewLogistics.Framework
{
    /// <summary>What one tile of an auto-harvester's area should grow.</summary>
    internal class TilePlan
    {
        /// <summary>The qualified item ID of the seed to plant, or <c>null</c> for none.</summary>
        public string SeedId { get; set; }

        /// <summary>The qualified item ID of the fertilizer to lay before planting, or <c>null</c> for none.</summary>
        public string FertilizerId { get; set; }

        /// <summary>Whether this tile has been planted since its seed was chosen.</summary>
        /// <remarks>The first planting always happens; after that, a crop is only planted again if it's set to replant.</remarks>
        public bool Planted { get; set; }

        /// <summary>Whether the tile is set aside for autocrafting, which plants on it when a job needs a crop.</summary>
        /// <remarks>An automation tile has no seed or fertilizer of its own; the job brings both.</remarks>
        public bool Automation { get; set; }

        /// <summary>Whether the tile has nothing planned on it.</summary>
        public bool IsEmpty => this.SeedId == null && this.FertilizerId == null && !this.Automation;
    }

    /// <summary>An auto-harvester's configuration: the area it works, what to grow where, and what to replant.</summary>
    /// <remarks>
    /// Kept in the machine's own <c>modData</c>, like every other device's settings, so it saves with the machine,
    /// travels with it to farmhands, and needs no save-data plumbing. The format is a few lines of plain text: item
    /// IDs never contain tabs or line breaks, which is what separates the fields.
    /// </remarks>
    internal class HarvesterSettings
    {
        /*********
        ** Fields
        *********/
        /// <summary>The smallest and largest side of the working area.</summary>
        public const int MinSize = 1;
        public const int MaxSize = 50;

        /// <summary>How far the area can be moved from the machine in any direction.</summary>
        public const int MaxOffset = 50;

        private const string Version = "1";


        /*********
        ** Accessors
        *********/
        /// <summary>The area's width, in tiles.</summary>
        public int Width { get; set; } = 5;

        /// <summary>The area's height, in tiles.</summary>
        public int Height { get; set; } = 5;

        /// <summary>How far the area is moved east of centred on the machine; negative is west.</summary>
        public int OffsetX { get; set; }

        /// <summary>How far the area is moved south of centred on the machine; negative is north.</summary>
        public int OffsetY { get; set; }

        /// <summary>Whether the area is outlined in the world.</summary>
        public bool ShowPreview { get; set; } = true;

        /// <summary>The plan for each tile, by position within the area (0,0 is the top-left).</summary>
        public Dictionary<Point, TilePlan> Tiles { get; } = new();

        /// <summary>The player's replant choice per seed, where they've made one.</summary>
        /// <remarks>Without a choice, a crop that regrows is replanted and a one-harvest crop isn't.</remarks>
        public Dictionary<string, bool> Replant { get; } = new(StringComparer.OrdinalIgnoreCase);


        /*********
        ** Public methods
        *********/
        /// <summary>The tiles the harvester works, for a machine on a tile.</summary>
        /// <remarks>Centred on the machine, then moved by the offset; an even side leans towards the top-left.</remarks>
        public Rectangle GetArea(Vector2 machineTile)
        {
            return new Rectangle(
                (int)machineTile.X - ((this.Width - 1) / 2) + this.OffsetX,
                (int)machineTile.Y - ((this.Height - 1) / 2) + this.OffsetY,
                this.Width,
                this.Height
            );
        }

        /// <summary>Changes the area's size, dropping plans for tiles that fall outside it.</summary>
        public void Resize(int width, int height)
        {
            this.Width = Math.Clamp(width, MinSize, MaxSize);
            this.Height = Math.Clamp(height, MinSize, MaxSize);

            foreach (Point outside in this.Tiles.Keys.Where(point => point.X >= this.Width || point.Y >= this.Height).ToList())
                this.Tiles.Remove(outside);
        }

        /// <summary>Whether a world tile is one of this harvester's automation tiles.</summary>
        public bool IsAutomationTile(Vector2 machineTile, Vector2 tile)
        {
            Rectangle area = this.GetArea(machineTile);
            Point point = new((int)tile.X - area.X, (int)tile.Y - area.Y);
            return this.Tiles.TryGetValue(point, out TilePlan plan) && plan.Automation;
        }

        /// <summary>Whether a seed will be replanted after its harvest.</summary>
        public bool ShouldReplant(string seedId, bool regrows)
        {
            return seedId != null && this.Replant.TryGetValue(seedId, out bool choice) ? choice : regrows;
        }

        /// <summary>Reads a machine's settings, or the defaults if it has none yet.</summary>
        public static HarvesterSettings Read(SObject machine)
        {
            HarvesterSettings settings = new();
            if (machine == null || !machine.modData.TryGetValue(ModIds.HarvesterKey, out string raw) || string.IsNullOrEmpty(raw))
                return settings;

            try
            {
                string[] lines = raw.Split('\n');
                string[] head = lines[0].Split('\t');
                if (head.Length < 6 || head[0] != Version)
                    return settings;

                settings.Width = Math.Clamp(Int(head[1], 5), MinSize, MaxSize);
                settings.Height = Math.Clamp(Int(head[2], 5), MinSize, MaxSize);
                settings.OffsetX = Math.Clamp(Int(head[3], 0), -MaxOffset, MaxOffset);
                settings.OffsetY = Math.Clamp(Int(head[4], 0), -MaxOffset, MaxOffset);
                settings.ShowPreview = head[5] == "1";

                foreach (string line in lines.Skip(1))
                {
                    string[] fields = line.Split('\t');
                    if (fields[0] == "R" && fields.Length >= 3)
                        settings.Replant[fields[1]] = fields[2] == "1";
                    else if (fields[0] == "T" && fields.Length >= 6)
                    {
                        Point point = new(Int(fields[1], -1), Int(fields[2], -1));
                        if (point.X < 0 || point.Y < 0 || point.X >= settings.Width || point.Y >= settings.Height)
                            continue;

                        settings.Tiles[point] = new TilePlan
                        {
                            SeedId = Nullable(fields[3]),
                            FertilizerId = Nullable(fields[4]),
                            Planted = fields[5] == "1",
                            Automation = fields.Length >= 7 && fields[6] == "1"
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"An auto-harvester's settings couldn't be read, so it's back to defaults: {ex.Message}");
                return new HarvesterSettings();
            }

            return settings;
        }

        /// <summary>Reads a machine's settings, reusing the last result while they haven't changed.</summary>
        /// <remarks>For drawing every frame: parsing a full 50x50 plan sixty times a second would be wasteful.</remarks>
        public static HarvesterSettings ReadCached(SObject machine)
        {
            string raw = machine != null && machine.modData.TryGetValue(ModIds.HarvesterKey, out string value) ? value : "";
            if (Cache.TryGetValue(machine, out CachedRead cached) && ReferenceEquals(cached.Raw, raw))
                return cached.Settings;

            HarvesterSettings settings = Read(machine);
            Cache.AddOrUpdate(machine, new CachedRead(raw, settings));
            return settings;
        }

        /// <summary>Parsed settings per machine, forgotten with the machine.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SObject, CachedRead> Cache = new();

        /// <summary>The text a machine's settings were parsed from, and the result.</summary>
        private record CachedRead(string Raw, HarvesterSettings Settings);

        /// <summary>Stores these settings on a machine.</summary>
        public void Write(SObject machine)
        {
            if (machine == null)
                return;

            StringBuilder text = new();
            text.Append(string.Join("\t", Version, this.Width, this.Height, this.OffsetX, this.OffsetY, this.ShowPreview ? "1" : "0"));

            foreach ((string seed, bool replant) in this.Replant)
                text.Append('\n').Append(string.Join("\t", "R", seed, replant ? "1" : "0"));

            foreach ((Point point, TilePlan plan) in this.Tiles.Where(pair => !pair.Value.IsEmpty))
                text.Append('\n').Append(string.Join("\t", "T", point.X, point.Y, plan.SeedId ?? "", plan.FertilizerId ?? "", plan.Planted ? "1" : "0", plan.Automation ? "1" : "0"));

            machine.modData[ModIds.HarvesterKey] = text.ToString();
        }


        /*********
        ** Private methods
        *********/
        private static int Int(string text, int fallback) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

        private static string Nullable(string text) => string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
