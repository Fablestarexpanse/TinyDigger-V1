using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>The kinds of piece the dam ring is made of. See Art/Tools/td_dam.py.</summary>
    public enum DamPieceKind
    {
        /// <summary>A plain 12 m bay of wall.</summary>
        Bay,

        /// <summary>A bay with the intake tower standing off it in the sea.</summary>
        Tower,

        /// <summary>A 36 m gated spillway; its water pours off into the void.</summary>
        Spillway,

        /// <summary>A 24 m pair of bays with a landing pad on an outrigger.</summary>
        Pad,

        /// <summary>An 84 m capital-ship terminal with docking arms out into the void.</summary>
        Terminal,
    }

    /// <summary>
    /// The dials for the dam that rings the world: how wide each piece is (as modelled), and how
    /// many of each go round the ring. Terminals sit at the compass points; the gap between two
    /// terminals holds the same run of features every time, with bays filling the rest.
    /// </summary>
    [CreateAssetMenu(menuName = "TinyDiggers/Dam Settings", fileName = "DamSettings")]
    public sealed class DamSettings : ScriptableObject
    {
        [Header("Piece widths, metres, as modelled")]
        [Min(1f)] public float BayWidth = 12f;
        [Min(1f)] public float SpillwayWidth = 36f;
        [Min(1f)] public float PadWidth = 24f;
        [Min(1f)] public float TerminalWidth = 84f;

        [Header("The ring")]
        [Tooltip("Metres from the edge of the disc out to the dam's inner face. Negative tucks the face over the disc's last cells, so the water runs into the concrete: the disc edge is a staircase of square cells, and with the face 4 m out a strip of it showed between the water and the dam.")]
        public float InnerOffset = -1f;

        [Tooltip("Capital-ship terminals, evenly round the ring, the first at north.")]
        [Min(0)] public int Terminals = 4;

        [Tooltip("Spillways in each gap between two terminals.")]
        [Min(0)] public int SpillwaysPerGap = 2;

        [Tooltip("Landing pads in each gap between two terminals.")]
        [Min(0)] public int PadsPerGap = 2;

        [Tooltip("Intake towers in each gap between two terminals.")]
        [Min(0)] public int TowersPerGap = 1;

        [Tooltip("Metres below the sea of the floor that closes the world from underneath, joined to the dam's underside.")]
        public float FloorDepth = -24f;

        [Tooltip("Bays a feature may move either way from even spacing, so the ring is not a clock face.")]
        [Min(0)] public int Jitter = 1;

        public float WidthOf(DamPieceKind kind) => kind switch
        {
            DamPieceKind.Spillway => SpillwayWidth,
            DamPieceKind.Pad => PadWidth,
            DamPieceKind.Terminal => TerminalWidth,
            _ => BayWidth,
        };
    }
}
