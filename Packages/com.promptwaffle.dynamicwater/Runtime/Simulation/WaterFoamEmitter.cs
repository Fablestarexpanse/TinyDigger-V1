using System.Runtime.InteropServices;
using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    /// <summary>
    /// Something in the water that makes foam as <see cref="WaterFoamMap"/> sees it: a hull of
    /// <see cref="HalfSize"/> (half length along <see cref="Forward"/>, half width across), moving at
    /// <see cref="Velocity"/>. Foam laps round its waterline at rest, a bow wave builds ahead of it and
    /// a wake is left behind it as it moves, all growing with speed up to the map's reference speed.
    /// Laid out as the compute shader reads it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WaterFoamEmitter
    {
        public const int Stride = sizeof(float) * 12;

        /// <summary>World x, z of the hull's centre.</summary>
        public Vector2 Position;

        /// <summary>World x, z direction of the hull's length (unit).</summary>
        public Vector2 Forward;

        /// <summary>Half length, half width, metres.</summary>
        public Vector2 HalfSize;

        /// <summary>World x, z metres a second.</summary>
        public Vector2 Velocity;

        /// <summary>Foam round the waterline at rest, 0..1.</summary>
        public float Ring;

        /// <summary>Foam ahead of the hull at full speed, 0..1.</summary>
        public float Bow;

        /// <summary>Foam left behind the hull at full speed, 0..1.</summary>
        public float Wake;

        float _padding;
    }
}
