using System;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;

namespace PromptWaffle.DynamicWater.Tests
{
    /// <summary>A heightfield in a plain array, for tests.</summary>
    sealed class ArrayGround : IWaterGround
    {
        public readonly float[] Heights;
        readonly int _width;

        public ArrayGround(int width, int height, float fill = 0f)
        {
            _width = width;
            Heights = new float[width * height];
            for (var i = 0; i < Heights.Length; i++)
                Heights[i] = fill;
        }

        public event Action<Rect> Changed;

        public float this[int x, int z]
        {
            get => Heights[z * _width + x];
            set => Heights[z * _width + x] = value;
        }

        public void WriteHeights(in WaterSimulationDesc desc, RectInt region, float[] into)
        {
            var i = 0;
            for (var z = region.yMin; z < region.yMax; z++)
                for (var x = region.xMin; x < region.xMax; x++)
                    into[i++] = Heights[z * _width + x];
        }

        public void RaiseChanged(Rect world) => Changed?.Invoke(world);
    }

    /// <summary>
    /// The shallow-water model: water is only ever moved, never made or lost; still water stays
    /// still; water finds its level over a sill; sources, drains, levels and forces do what they
    /// say; walls hold; and the GPU runs the same model as the CPU reference.
    /// </summary>
    public class ShallowWaterTests
    {
        const float Cell = 0.5f;

        static WaterSimulationDesc Desc(int size) => WaterSimulationDesc.Default(size, size, Cell, Vector2.zero);

        static CpuShallowWater Cpu(int size, ArrayGround ground) => new CpuShallowWater(Desc(size), ground.Heights);

        /// <summary>A dry flat floor with a 2 m column of water in the middle: a dam break.</summary>
        static void DamBreak(CpuShallowWater water, int size)
        {
            for (var z = size / 2 - 4; z < size / 2 + 4; z++)
                for (var x = size / 2 - 4; x < size / 2 + 4; x++)
                    water.SetDepth(x, z, 2f);
        }

        // --- the CPU reference ----------------------------------------------------------------

        [Test]
        public void AnUnstableStepIsRefused()
        {
            var desc = Desc(8);
            desc.FixedStep = 1f;
            Assert.Throws<ArgumentException>(() => desc.Validate());
        }

        [Test]
        public void StillWaterStaysStill()
        {
            var ground = new ArrayGround(32, 32);
            var water = Cpu(32, ground);
            water.FillTo(1f);

            for (var i = 0; i < 300; i++)
                water.Step();

            for (var z = 0; z < 32; z++)
                for (var x = 0; x < 32; x++)
                {
                    Assert.That(water.DepthAt(x, z), Is.EqualTo(1f).Within(1e-4f));
                    Assert.That(water.VelocityAt(x, z).magnitude, Is.LessThan(1e-4f));
                }
        }

        [Test]
        public void ADamBreakSpreadsAndKeepsEveryDrop()
        {
            var ground = new ArrayGround(48, 48);
            var water = Cpu(48, ground);
            DamBreak(water, 48);
            var before = water.TotalVolume();

            for (var i = 0; i < 600; i++)
                water.Step();

            Assert.That(water.TotalVolume(), Is.EqualTo(before).Within(before * 1e-4));
            Assert.That(water.DepthAt(24, 24), Is.LessThan(0.5f), "the column fell");
            Assert.That(water.DepthAt(4, 24), Is.GreaterThan(0.01f), "and spread to the edge");
        }

        [Test]
        public void WaterFindsItsLevelOverASill()
        {
            // Two basins split by a wall 1.2 m high with a sill at 0.4 m in it. The left starts
            // 1 m deep; the right is dry and 2 m deeper, big enough to take everything that
            // comes over, so the left drains down to the sill and no further. (With equal basins
            // both would level out above the sill, at half the water each.)
            var ground = new ArrayGround(40, 20);
            for (var z = 0; z < 20; z++)
            {
                ground[20, z] = z >= 8 && z < 12 ? 0.4f : 1.2f;
                for (var x = 21; x < 40; x++)
                    ground[x, z] = -2f;
            }
            var desc = WaterSimulationDesc.Default(40, 20, Cell, Vector2.zero);
            var water = new CpuShallowWater(desc, ground.Heights);
            for (var z = 0; z < 20; z++)
                for (var x = 0; x < 20; x++)
                    water.SetDepth(x, z, 1f);
            var before = water.TotalVolume();

            for (var i = 0; i < 6000; i++)
                water.Step();

            // The flow's momentum carries a few millimetres over after the level reaches the sill.
            Assert.That(water.SurfaceAt(5, 10), Is.InRange(0.37f, 0.47f), "the left settles at the sill");
            Assert.That(water.DepthAt(35, 10), Is.GreaterThan(0.05f), "the right filled");
            Assert.That(water.TotalVolume(), Is.EqualTo(before).Within(before * 1e-4));
        }

        [Test]
        public void WallsHoldWater()
        {
            var ground = new ArrayGround(20, 20);
            for (var z = 0; z < 20; z++)
                ground[10, z] = WaterGround.Wall;
            var water = Cpu(20, ground);
            for (var z = 0; z < 20; z++)
                for (var x = 0; x < 10; x++)
                    water.SetDepth(x, z, 1f);

            for (var i = 0; i < 600; i++)
                water.Step();

            for (var z = 0; z < 20; z++)
                for (var x = 11; x < 20; x++)
                    Assert.That(water.DepthAt(x, z), Is.Zero, $"({x}, {z}) got wet through the wall");
        }

        [Test]
        public void ASourceAddsItsRate()
        {
            var ground = new ArrayGround(40, 40);
            var water = Cpu(40, ground);
            water.SetEffectors(new[] { WaterEffector.Source(new Vector2(10f, 10f), 2f, 3f) });
            var steps = Mathf.RoundToInt(2f / water.Desc.FixedStep);
            for (var i = 0; i < steps; i++)
                water.Step();

            Assert.That(water.TotalVolume(), Is.EqualTo(6f).Within(6f * 0.01f), "3 m³/s for 2 s");
        }

        [Test]
        public void ADrainInAFullPoolTakesItsRate()
        {
            // Deep enough that the drain never runs short: it takes exactly what it asks for.
            var ground = new ArrayGround(40, 40);
            var water = Cpu(40, ground);
            water.FillTo(1f);
            var before = water.TotalVolume();
            water.SetEffectors(new[] { WaterEffector.Drain(new Vector2(10f, 10f), 2f, 1f) });
            var steps = Mathf.RoundToInt(2f / water.Desc.FixedStep);
            for (var i = 0; i < steps; i++)
                water.Step();

            Assert.That(before - water.TotalVolume(), Is.EqualTo(2f).Within(2f * 0.01f), "1 m³/s out for 2 s");
        }

        [Test]
        public void ADrainNeverTakesMoreThanIsThere()
        {
            var ground = new ArrayGround(20, 20);
            var water = Cpu(20, ground);
            water.SetDepth(10, 10, 0.1f);
            water.SetEffectors(new[] { WaterEffector.Drain(new Vector2(5f, 5f), 3f, 100f) });

            for (var i = 0; i < 120; i++)
                water.Step();

            for (var z = 0; z < 20; z++)
                for (var x = 0; x < 20; x++)
                    Assert.That(water.DepthAt(x, z), Is.GreaterThanOrEqualTo(0f));
            Assert.That(water.TotalVolume(), Is.LessThan(1e-4));
        }

        [Test]
        public void ALevelEffectorHoldsTheSurface()
        {
            var ground = new ArrayGround(30, 30);
            var water = Cpu(30, ground);
            water.SetEffectors(new[] { WaterEffector.HoldLevel(new Vector2(7.5f, 7.5f), 30f, 0.8f, 2f) });

            for (var i = 0; i < 1800; i++)
                water.Step();

            Assert.That(water.SurfaceAt(15, 15), Is.EqualTo(0.8f).Within(0.02f));
        }

        [Test]
        public void AForcePushesTheWaterAlong()
        {
            var ground = new ArrayGround(40, 20);
            var desc = WaterSimulationDesc.Default(40, 20, Cell, Vector2.zero);
            var water = new CpuShallowWater(desc, ground.Heights);
            water.FillTo(0.5f);
            water.SetEffectors(new[] { WaterEffector.Force(new Vector2(10f, 5f), 3f, Vector2.right, 4f) });

            for (var i = 0; i < 90; i++)
                water.Step();

            // Waves travel about 2.2 m/s in half a metre of water, so after 1.5 s the surge has
            // reached about 3 m beyond the 3 m radius on either side.
            Assert.That(water.VelocityAt(20, 10).x, Is.GreaterThan(0.05f), "flowing +x at the force");
            Assert.That(water.SurfaceAt(30, 10), Is.GreaterThan(water.SurfaceAt(10, 10) + 0.005f), "piling up downstream");
        }

        // --- the GPU ---------------------------------------------------------------------------

        [Test]
        public void TheGpuRunsTheSameModelAsTheCpu()
        {
            const int size = 48;
            var ground = new ArrayGround(size, size);
            for (var z = 0; z < size; z++)
                for (var x = 0; x < size; x++)
                    ground[x, z] = Mathf.Sin(x * 0.3f) * 0.3f + z * 0.01f;
            ground[30, 30] = WaterGround.Wall;
            var cpu = Cpu(size, ground);
            DamBreak(cpu, size);
            var effectors = new[] { WaterEffector.Source(new Vector2(6f, 6f), 1.5f, 2f), WaterEffector.Drain(new Vector2(18f, 6f), 1.5f, 1f) };
            cpu.SetEffectors(effectors);

            using var gpu = new WaterSimulation(Desc(size), ground);
            var start = new float[size * size];
            for (var z = 0; z < size; z++)
                for (var x = 0; x < size; x++)
                    start[z * size + x] = cpu.DepthAt(x, z);
            gpu.SetDepths(start);
            gpu.SetEffectors(effectors);

            for (var i = 0; i < 240; i++)
                cpu.Step();
            gpu.StepExactly(240);

            var depths = gpu.ReadDepthsImmediate();
            var worst = 0f;
            for (var z = 0; z < size; z++)
                for (var x = 0; x < size; x++)
                    worst = Mathf.Max(worst, Mathf.Abs(depths[z * size + x] - cpu.DepthAt(x, z)));
            Assert.That(worst, Is.LessThan(2e-3f), $"largest depth difference {worst} m");
        }

        [Test]
        public void TheGpuKeepsEveryDropAtFullSize()
        {
            const int size = 1024;
            var ground = new ArrayGround(size, size);
            using var gpu = new WaterSimulation(Desc(size), ground);
            var depths = new float[size * size];
            for (var z = 400; z < 624; z++)
                for (var x = 400; x < 624; x++)
                    depths[z * size + x] = 2f;
            gpu.SetDepths(depths);
            var before = gpu.TotalVolumeImmediate();

            gpu.StepExactly(600);

            Assert.That(gpu.TotalVolumeImmediate(), Is.EqualTo(before).Within(before * 1e-4));
        }

        [Test]
        public void DiggingTheGroundLetsWaterIn()
        {
            // A dry shelf at 1 m beside a pool 0.5 m deep: dig a trench through the shelf and
            // the pool runs into it.
            const int size = 32;
            var ground = new ArrayGround(size, size, 1f);
            for (var z = 0; z < size; z++)
                for (var x = 0; x < 8; x++)
                    ground[x, z] = 0f;
            using var gpu = new WaterSimulation(Desc(size), ground);
            gpu.FillTo(0.5f);
            gpu.StepExactly(60);
            Assert.That(gpu.ReadDepthsImmediate()[16 * size + 20], Is.Zero, "dry before the dig");

            for (var x = 8; x < 24; x++)
                ground[x, 16] = 0f;
            ground.RaiseChanged(new Rect(4f, 8f, 8f, 0.5f));
            gpu.StepExactly(600);

            Assert.That(gpu.ReadDepthsImmediate()[16 * size + 20], Is.GreaterThan(0.1f), "the trench flooded");
        }

        [Test]
        public void AFullSizeZoneStepsWellInsideAFrame()
        {
            // 1024² half-metre cells, the size of TinyDiggers' disc. 60 steps is one second of
            // simulation at the default rate.
            const int size = 1024;
            var ground = new ArrayGround(size, size);
            using var gpu = new WaterSimulation(Desc(size), ground);
            gpu.FillTo(0.5f);
            gpu.StepExactly(10);
            gpu.ReadDepthsImmediate();

            var clock = Stopwatch.StartNew();
            gpu.StepExactly(60);
            gpu.ReadDepthsImmediate();
            var ms = clock.Elapsed.TotalMilliseconds;

            UnityEngine.Debug.Log($"PromptWaffle water: 60 steps of 1024² in {ms:0.0} ms ({ms / 60.0:0.00} ms a step, with one readback).");
            Assert.That(ms, Is.LessThan(250.0), $"60 steps took {ms:0} ms");
        }
    }
}
