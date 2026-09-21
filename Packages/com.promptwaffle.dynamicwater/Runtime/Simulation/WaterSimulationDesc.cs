using System;
using UnityEngine;

namespace PromptWaffle.DynamicWater
{
    /// <summary>
    /// The shape and tuning of one simulation zone. The zone is a grid of <see cref="Width"/> by
    /// <see cref="Height"/> square cells, <see cref="CellSize"/> metres across, whose cell (0, 0)
    /// starts at <see cref="Origin"/> (world x, z) and runs toward +x and +z.
    /// </summary>
    [Serializable]
    public struct WaterSimulationDesc
    {
        [Min(2)] public int Width;
        [Min(2)] public int Height;

        [Tooltip("Metres across one cell.")]
        [Min(0.01f)] public float CellSize;

        [Tooltip("World x and z of the corner of cell (0, 0).")]
        public Vector2 Origin;

        [Tooltip("m/s².")]
        public float Gravity;

        [Tooltip("Seconds per simulation step. Smaller is more stable and more expensive.")]
        [Min(0.001f)] public float FixedStep;

        [Tooltip("Most steps run in one frame; time beyond that is dropped rather than letting a slow frame snowball.")]
        [Min(1)] public int MaxStepsPerFrame;

        [Tooltip("Fraction of each pipe's flow kept from one step to the next. Lower settles faster and damps waves.")]
        [Range(0.9f, 1f)] public float FlowRetention;

        [Tooltip("Metres of water below which a cell counts as dry.")]
        [Min(0f)] public float DryDepth;

        [Tooltip("Speed limit, m/s, so a numerical spike can never run away.")]
        [Min(0.1f)] public float MaxSpeed;

        public static WaterSimulationDesc Default(int width, int height, float cellSize, Vector2 origin) => new WaterSimulationDesc
        {
            Width = width,
            Height = height,
            CellSize = cellSize,
            Origin = origin,
            Gravity = 9.81f,
            FixedStep = 1f / 60f,
            MaxStepsPerFrame = 4,
            FlowRetention = 0.995f,
            DryDepth = 0.001f,
            MaxSpeed = 20f,
        };

        public float CellArea => CellSize * CellSize;

        public int CellCount => Width * Height;

        /// <summary>World x, z of the centre of cell (x, z).</summary>
        public Vector2 CellCentre(int x, int z) => Origin + new Vector2((x + 0.5f) * CellSize, (z + 0.5f) * CellSize);

        /// <summary>
        /// Largest step the pipe model is stable at for these cells: a step that moves water
        /// across a pipe faster than the height difference driving it would overshoot and ring.
        /// </summary>
        public float StableStep => Mathf.Sqrt(CellSize / (4f * Mathf.Max(0.01f, Gravity)));

        public void Validate()
        {
            if (Width < 2 || Height < 2)
                throw new ArgumentException("A water zone needs at least 2 x 2 cells.");
            if (!(CellSize > 0f))
                throw new ArgumentException("CellSize must be positive.");
            if (!(FixedStep > 0f) || FixedStep > StableStep)
                throw new ArgumentException($"FixedStep {FixedStep:0.####} s is unstable for {CellSize} m cells; the limit is {StableStep:0.####} s.");
        }
    }
}
