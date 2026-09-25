using System;

namespace TinyDiggers.Units
{
    /// <summary>The forge's working craft (Ronan, 2026-09-25): all afloat, the work comes in later slices.</summary>
    public enum VesselKind
    {
        /// <summary>Clamshell crane on a barge: digs the bed and loads a scow alongside.</summary>
        Dredge,

        /// <summary>Bucket-line dredge that floats its own pond and digs for gold.</summary>
        GoldDredge,

        /// <summary>Hopper barge that carries the dredge's spoil and bottom-dumps it.</summary>
        Scow,

        /// <summary>Push boat for the scow.</summary>
        Tug,
    }

    /// <summary>A craft's hull and handling, at game size.</summary>
    public readonly struct VesselSpec
    {
        public readonly string Name;

        /// <summary>Metres below the water, loaded.</summary>
        public readonly float Draft;

        public readonly float HalfBeam;
        public readonly float HalfLength;

        /// <summary>Metres a second.</summary>
        public readonly float Speed;

        /// <summary>Degrees a second.</summary>
        public readonly float TurnRate;

        public VesselSpec(string name, float draft, float halfBeam, float halfLength, float speed, float turnRate)
        {
            Name = name;
            Draft = draft;
            HalfBeam = halfBeam;
            HalfLength = halfLength;
            Speed = speed;
            TurnRate = turnRate;
        }
    }

    /// <summary>
    /// Hull sizes from each machine's forge <c>machine.json</c> (waterline, bounds), times the
    /// forge-to-game factor every forge machine shares (Ronan, 2026-09-25: the dredge "could fit
    /// 3-4 excavators on its deck length", which that factor keeps). Speeds and turn rates are
    /// this game's: the tug quickest, the gold dredge barely moving, as it walks on its spuds.
    /// </summary>
    public static class VesselSpecs
    {
        /// <summary>Forge size to game size: the dumper's ruled 0.79 m over its forge height (ForgeMachines).</summary>
        public const float ForgeScale = 1.41f;

        public static VesselSpec For(VesselKind kind)
        {
            switch (kind)
            {
                // Waterline 0.192, barge 3.39 x 5.95 m over its fenders.
                case VesselKind.Dredge: return Make("dredge", 0.192f, 1.696f, 2.976f, 1.2f, 20f);
                // Waterline 0.20, pontoon 3.02 m wide. Its ladder is raised to move (game_clips);
                // lowered, it digs its own pond, which is slice D's.
                case VesselKind.GoldDredge: return Make("gold dredge", 0.2f, 1.51f, 5.0f, 0.5f, 8f);
                // Loaded waterline 0.42 (0.22 light): it has to float full.
                case VesselKind.Scow: return Make("scow", 0.42f, 1.81f, 4.82f, 1.5f, 15f);
                case VesselKind.Tug: return Make("tug", 0.3f, 0.825f, 2.11f, 3f, 45f);
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        static VesselSpec Make(string name, float draft, float halfBeam, float halfLength, float speed, float turnRate) =>
            new VesselSpec(name, draft * ForgeScale, halfBeam * ForgeScale, halfLength * ForgeScale, speed, turnRate);
    }
}
