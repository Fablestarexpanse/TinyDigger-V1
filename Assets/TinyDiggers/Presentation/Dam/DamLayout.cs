using System;
using System.Collections.Generic;
using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>One piece of the ring: what it is, and where along the inner face it sits.</summary>
    public readonly struct DamSlot
    {
        public DamSlot(DamPieceKind kind, float start, float width, float stretch)
        {
            Kind = kind;
            Start = start;
            Width = width;
            Stretch = stretch;
        }

        public DamPieceKind Kind { get; }

        /// <summary>Metres along the inner face, clockwise from north, where the piece begins.</summary>
        public float Start { get; }

        /// <summary>Metres of inner face the piece covers, after <see cref="Stretch"/>.</summary>
        public float Width { get; }

        /// <summary>How much the piece is stretched along the wall so the ring closes exactly (about 1).</summary>
        public float Stretch { get; }

        public float Centre => Start + Width * 0.5f;
    }

    /// <summary>
    /// Lays the dam out round a circle. Terminals are centred on the compass points (for four);
    /// each gap between two terminals holds its pads, spillways and towers spread evenly, in a
    /// fixed order (pad, spillway, tower, spillway, pad for the defaults), moved up to
    /// <see cref="DamSettings.Jitter"/> bays by the seed, with plain bays between. A gap never
    /// comes out a whole number of bays, so every piece in it is stretched or squeezed by the same
    /// small factor (within half a bay over the gap) and the ring closes with no seam.
    /// </summary>
    public static class DamLayout
    {
        public static List<DamSlot> Build(DamSettings settings, float innerRadius, int seed)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (!(innerRadius > 0f))
                throw new ArgumentOutOfRangeException(nameof(innerRadius));

            var circumference = 2f * Mathf.PI * innerRadius;
            var slots = new List<DamSlot>();
            var random = new System.Random(seed);
            var terminals = Mathf.Max(0, settings.Terminals);

            if (terminals == 0)
            {
                FillGap(slots, settings, 0f, circumference, random);
                return slots;
            }

            var sector = circumference / terminals;
            var terminal = settings.TerminalWidth;
            if (terminal > sector)
                throw new ArgumentException($"{terminals} terminals of {terminal} m do not fit round {circumference:0} m.");

            // Start half a terminal before north, so the first terminal is centred on it.
            var cursor = -terminal * 0.5f;
            for (var t = 0; t < terminals; t++)
            {
                slots.Add(new DamSlot(DamPieceKind.Terminal, Wrap(cursor, circumference), terminal, 1f));
                cursor += terminal;
                FillGap(slots, settings, cursor, sector - terminal, random, circumference);
                cursor += sector - terminal;
            }

            return slots;
        }

        /// <summary>The features of one gap, in order, with bays between, stretched to fill it exactly.</summary>
        static void FillGap(List<DamSlot> slots, DamSettings settings, float start, float length, System.Random random,
            float circumference = float.PositiveInfinity)
        {
            var features = GapFeatures(settings);
            var featureWidth = 0f;
            foreach (var kind in features)
                featureWidth += settings.WidthOf(kind);

            // The nearest whole number of bays, so the stretch is within half a bay either way.
            var bays = Mathf.Max(0, Mathf.RoundToInt((length - featureWidth) / settings.BayWidth));
            var natural = featureWidth + bays * settings.BayWidth;
            if (natural <= 0f)
                return;
            var stretch = length / natural;

            // Bays before each feature, evenly spread, then jittered and kept in range.
            var runs = new int[features.Count + 1];
            for (var i = 0; i < runs.Length; i++)
                runs[i] = bays * (i + 1) / runs.Length - bays * i / runs.Length;
            for (var i = 0; i + 1 < runs.Length && settings.Jitter > 0; i++)
            {
                var move = random.Next(-settings.Jitter, settings.Jitter + 1);
                // Never down to no bays: two features side by side read as one.
                move = Mathf.Clamp(move, Mathf.Min(0, 1 - runs[i]), Mathf.Max(0, runs[i + 1] - 1));
                runs[i] += move;
                runs[i + 1] -= move;
            }

            var cursor = start;
            void Place(DamPieceKind kind)
            {
                var width = settings.WidthOf(kind) * stretch;
                slots.Add(new DamSlot(kind, Wrap(cursor, circumference), width, stretch));
                cursor += width;
            }

            for (var i = 0; i < runs.Length; i++)
            {
                for (var b = 0; b < runs[i]; b++)
                    Place(DamPieceKind.Bay);
                if (i < features.Count)
                    Place(features[i]);
            }
        }

        /// <summary>Pads at the ends, spillways inside them, towers in the middle.</summary>
        static List<DamPieceKind> GapFeatures(DamSettings settings)
        {
            var middle = new List<DamPieceKind>();
            for (var i = 0; i < settings.TowersPerGap; i++)
                middle.Add(DamPieceKind.Tower);
            var list = new List<DamPieceKind>(middle);
            for (var i = 0; i < settings.SpillwaysPerGap; i++)
            {
                if (i % 2 == 0)
                    list.Insert(0, DamPieceKind.Spillway);
                else
                    list.Add(DamPieceKind.Spillway);
            }

            for (var i = 0; i < settings.PadsPerGap; i++)
            {
                if (i % 2 == 0)
                    list.Insert(0, DamPieceKind.Pad);
                else
                    list.Add(DamPieceKind.Pad);
            }

            return list;
        }

        static float Wrap(float metres, float circumference) =>
            float.IsInfinity(circumference) ? metres : (metres % circumference + circumference) % circumference;
    }

    /// <summary>
    /// Bends a straight kit piece onto the circle. The kit is modelled in Blender with x along
    /// the wall, y outward and z up; Unity's FBX import turns that into (-x, z, -y), so in the
    /// imported mesh "along" is -x, "out" is -z and "up" is y.
    ///
    /// A point <c>along</c> metres from the piece's centre and <c>out</c> metres from the inner
    /// face lands at angle <c>centre + along × stretch / radius</c> and radius
    /// <c>radius + out</c>. Both ends of every piece are cuts at ±width/2, so neighbours' ends
    /// land on the same radial line and the outer face, which is longer than the inner one,
    /// closes with no gap. Angles run clockwise from north (+z), as seen from above.
    /// </summary>
    public static class DamBend
    {
        public static Vector3 Point(Vector3 kit, float centreAngle, float radius, float stretch)
        {
            var along = -kit.x;
            var outward = -kit.z;
            var angle = centreAngle + along * stretch / radius;
            var r = radius + outward;
            return new Vector3(Mathf.Sin(angle) * r, kit.y, Mathf.Cos(angle) * r);
        }

        public static void Bend(Vector3[] source, Vector3[] into, float centreAngle, float radius, float stretch)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (into == null || into.Length < source.Length)
                throw new ArgumentException("The output must be at least as long as the source.", nameof(into));
            for (var i = 0; i < source.Length; i++)
                into[i] = Point(source[i], centreAngle, radius, stretch);
        }
    }
}
