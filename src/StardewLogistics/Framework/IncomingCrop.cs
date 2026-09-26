using Microsoft.Xna.Framework;
using StardewValley;

namespace StardewLogistics.Framework
{
    /// <summary>A crop growing under an auto-harvester, counted as stock that hasn't arrived yet.</summary>
    /// <remarks>
    /// Autocrafting can plan around these: Starfruit ready in five days is Starfruit a job can wait for. Only the
    /// harvest a crop is guaranteed to give counts -- a chance at extra isn't something to plan on.
    /// </remarks>
    internal class IncomingCrop
    {
        /// <summary>The location the crop is growing in.</summary>
        public GameLocation Location { get; init; }

        /// <summary>The crop's tile.</summary>
        public Vector2 Tile { get; init; }

        /// <summary>The tile of the auto-harvester that tends it.</summary>
        public Vector2 HarvesterTile { get; init; }

        /// <summary>The stock ID of what it yields.</summary>
        public string ItemId { get; init; }

        /// <summary>How many it's guaranteed to yield.</summary>
        public int Count { get; init; }

        /// <summary>Days until it's ready: zero if it is now.</summary>
        public int Days { get; init; }
    }
}
