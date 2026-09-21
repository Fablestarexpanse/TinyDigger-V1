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

        /// <summary>False until the ground exists (e.g. a game that builds its terrain in Awake); the zone waits.</summary>
        public virtual bool IsReady => true;

        public abstract void WriteHeights(in WaterSimulationDesc desc, RectInt region, float[] into);

        protected void RaiseChanged(Rect world) => Changed?.Invoke(world);
    }
}
