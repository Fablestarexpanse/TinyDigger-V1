using System;
using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    /// <summary>
    /// What the water flows over. A zone asks for ground heights on its own grid and listens for
    /// changes (digging, building), so any heightfield can drive it: a Unity Terrain, a game's own
    /// voxel or column data, or a procedural function.
    /// </summary>
    public interface IWaterGround
    {
        /// <summary>
        /// Writes the ground height, in world metres, under each cell of <paramref name="region"/>
        /// of a zone with <paramref name="desc"/>, row by row: <c>into[(z - region.y) * region.width + (x - region.x)]</c>.
        /// A cell water can never enter (off the map, inside a wall) is <see cref="WaterGround.Wall"/>.
        /// </summary>
        void WriteHeights(in WaterSimulationDesc desc, RectInt region, float[] into);

        /// <summary>Raised with the world-space xz rectangle whose ground changed.</summary>
        event Action<Rect> Changed;
    }

    public static class WaterGround
    {
        /// <summary>Ground height meaning "solid wall": no water enters or leaves the cell.</summary>
        public const float Wall = 1e6f;

        public static bool IsWall(float height) => height >= Wall * 0.5f;
    }
}
