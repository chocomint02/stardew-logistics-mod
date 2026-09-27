using Microsoft.Xna.Framework;
using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>A tile under an auto-harvester that's set aside for automation and has nothing growing on it.</summary>
    /// <remarks>Autocrafting can plant on these when a job needs a crop that isn't in storage or already growing.</remarks>
    internal class FreeTile
    {
        /// <summary>The location the tile is in.</summary>
        public GameLocation Location { get; init; }

        /// <summary>The tile.</summary>
        public Vector2 Tile { get; init; }

        /// <summary>The tile of the auto-harvester that tends it.</summary>
        public Vector2 HarvesterTile { get; init; }

        /// <summary>The fertilizer already in the soil, or <c>null</c> for none.</summary>
        /// <remarks>Soil takes one fertilizer, so a tile that has one can't be given Speed-Gro -- but keeps what it has.</remarks>
        public string Fertilizer { get; init; }
    }

    /// <summary>A seed an autocrafting job will have planted on an automation tile.</summary>
    /// <remarks>
    /// Planned with the job, and carried out by the tile's harvester on its next pass -- which is at once, since
    /// queuing a job with plantings sets the harvesters working. Once planted it becomes an ordinary reserved
    /// crop, whose harvest goes to the job.
    /// </remarks>
    internal class PlannedPlanting
    {
        /// <summary>The location the tile is in.</summary>
        public GameLocation Location { get; init; }

        /// <summary>The tile.</summary>
        public Vector2 Tile { get; init; }

        /// <summary>The tile of the auto-harvester that tends it.</summary>
        public Vector2 HarvesterTile { get; init; }

        /// <summary>The qualified item ID of the seed to plant.</summary>
        public string SeedId { get; init; }

        /// <summary>The qualified item ID of the fertilizer to lay first, or <c>null</c> for none.</summary>
        public string FertilizerId { get; init; }

        /// <summary>The qualified item ID of what the crop yields.</summary>
        public string ItemId { get; init; }

        /// <summary>How many it's guaranteed to yield.</summary>
        public int Count { get; init; }

        /// <summary>Days from planting until it's ready.</summary>
        public int Days { get; init; }

        /// <summary>The buffer of the job it's for, which holds its seed and fertilizer.</summary>
        public Devices.JobBuffer Buffer { get; set; }
    }
}
