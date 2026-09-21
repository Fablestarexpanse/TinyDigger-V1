using System.Runtime.InteropServices;
using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    public enum WaterEffectorKind
    {
        /// <summary>Adds <see cref="WaterEffector.Rate"/> m³/s of water, spread over the radius.</summary>
        Source = 0,

        /// <summary>Removes up to <see cref="WaterEffector.Rate"/> m³/s, never more than is there.</summary>
        Drain = 1,

        /// <summary>
        /// Pulls the water surface toward <see cref="WaterEffector.Level"/>: adds where it is lower,
        /// removes where it is higher, at <see cref="WaterEffector.Rate"/> per second (0..1 of the
        /// gap). A sea held at its level, or an outflow that keeps a lake from overfilling.
        /// </summary>
        Level = 2,

        /// <summary>Pushes water along <see cref="WaterEffector.Direction"/> at <see cref="WaterEffector.Rate"/> m/s².</summary>
        Force = 3,

        /// <summary>
        /// A weir: lets out only water standing above <see cref="WaterEffector.Level"/> (the crest),
        /// at the broad-crested weir rate Q = 1.7 × width × head^1.5 m³/s, where the width is
        /// <see cref="WaterEffector.Rate"/> in metres. Never adds water and never draws below the
        /// crest, so it does nothing until something lifts the water over it. A spillway.
        /// </summary>
        Overflow = 4,
    }

    /// <summary>
    /// One source, drain, level or force acting on a zone. Positions and radii are world metres
    /// (x, z). Blittable, so a zone uploads them straight to the GPU; the layout must match
    /// <c>Effector</c> in ShallowWater.compute.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WaterEffector
    {
        public int Kind;
        public Vector2 Position;
        public float Radius;
        public float Rate;
        public float Level;
        public Vector2 Direction;

        /// <summary>
        /// Set by the simulation, not by callers: 1 / (sum of the falloff weights of the cells
        /// the effector covers × cell area), so a source adds exactly its rate however its disc
        /// lands on the grid.
        /// </summary>
        public float Scale;

        public const int Stride = 36;

        public WaterEffectorKind EffectorKind => (WaterEffectorKind)Kind;

        public static WaterEffector Source(Vector2 position, float radius, float cubicMetresPerSecond) =>
            new WaterEffector { Kind = (int)WaterEffectorKind.Source, Position = position, Radius = radius, Rate = cubicMetresPerSecond };

        public static WaterEffector Drain(Vector2 position, float radius, float cubicMetresPerSecond) =>
            new WaterEffector { Kind = (int)WaterEffectorKind.Drain, Position = position, Radius = radius, Rate = cubicMetresPerSecond };

        public static WaterEffector HoldLevel(Vector2 position, float radius, float level, float response = 0.5f) =>
            new WaterEffector { Kind = (int)WaterEffectorKind.Level, Position = position, Radius = radius, Level = level, Rate = response };

        /// <summary>Broad-crested weir coefficient, SI units (m^0.5/s).</summary>
        public const float WeirCoefficient = 1.7f;

        /// <summary>
        /// A spillway <paramref name="weirWidth"/> metres wide with its crest at world height
        /// <paramref name="crestLevel"/>, drawing from the water within <paramref name="radius"/>.
        /// </summary>
        public static WaterEffector Overflow(Vector2 position, float radius, float crestLevel, float weirWidth) =>
            new WaterEffector { Kind = (int)WaterEffectorKind.Overflow, Position = position, Radius = radius, Level = crestLevel, Rate = weirWidth };

        /// <summary>The weir rate in m³/s for <paramref name="head"/> metres of water over the crest.</summary>
        public static float WeirRate(float weirWidth, float head) =>
            head > 0f ? WeirCoefficient * weirWidth * head * Mathf.Sqrt(head) : 0f;

        public static WaterEffector Force(Vector2 position, float radius, Vector2 direction, float acceleration) =>
            new WaterEffector { Kind = (int)WaterEffectorKind.Force, Position = position, Radius = radius, Direction = direction.normalized, Rate = acceleration };

        /// <summary>Falloff weight at <paramref name="distance"/> from the centre: (1 - (r/R)²)², smooth to zero at the radius.</summary>
        public static float Weight(float distance, float radius)
        {
            var q = distance / radius;
            if (q >= 1f)
                return 0f;
            var s = 1f - q * q;
            return s * s;
        }

        /// <summary>
        /// Fills in <see cref="Scale"/> for a zone, from the cells the effector actually covers.
        /// A radius under three quarters of a cell is widened to that, so the effector always covers
        /// the cell it sits in.
        /// </summary>
        public void Prepare(in WaterSimulationDesc desc)
        {
            Radius = Mathf.Max(Radius, desc.CellSize * 0.75f);
            var sum = 0f;
            var x0 = Mathf.FloorToInt((Position.x - Radius - desc.Origin.x) / desc.CellSize);
            var x1 = Mathf.CeilToInt((Position.x + Radius - desc.Origin.x) / desc.CellSize);
            var z0 = Mathf.FloorToInt((Position.y - Radius - desc.Origin.y) / desc.CellSize);
            var z1 = Mathf.CeilToInt((Position.y + Radius - desc.Origin.y) / desc.CellSize);
            for (var z = Mathf.Max(0, z0); z <= Mathf.Min(desc.Height - 1, z1); z++)
                for (var x = Mathf.Max(0, x0); x <= Mathf.Min(desc.Width - 1, x1); x++)
                    sum += Weight(Vector2.Distance(desc.CellCentre(x, z), Position), Radius);
            Scale = sum > 0f ? 1f / (sum * desc.CellArea) : 0f;
        }
    }
}
