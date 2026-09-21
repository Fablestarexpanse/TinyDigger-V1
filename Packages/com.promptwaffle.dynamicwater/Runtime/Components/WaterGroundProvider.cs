using System;
using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    /// <summary>
    /// A component that gives a <see cref="WaterZone"/> its ground. Subclass it to connect any
    /// heightfield; <see cref="TerrainWaterGround"/> covers Unity's own Terrain. Call
    /// <see cref="RaiseChanged"/> with the world rectangle whenever the ground there changes.
    /// </summary>
    public abstract class WaterGroundProvider : MonoBehaviour, IWaterGround
    {
        public event Action<Rect> Changed;

        public abstract void WriteHeights(in WaterSimulationDesc desc, RectInt region, float[] into);

        protected void RaiseChanged(Rect world) => Changed?.Invoke(world);
    }
}
