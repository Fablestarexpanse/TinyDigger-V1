namespace TinyDiggers.Units
{
    /// <summary>
    /// What each unit carries, in loose cubic metres.
    ///
    /// These are one set of numbers rather than three, because what matters is the ratio between
    /// them and a ratio kept in three places drifts. Ronan's ruling (2026-09-22): three of the
    /// digger's scoops fill the dumper, and the starter robot takes six barrow loads to do the
    /// same job.
    ///
    /// On the sandbox's half-metre cells and half-metre steps one cut is 0.125 m³, so a barrow is
    /// about a cut and a half, a scoop three cuts, and a dumper load nine.
    /// </summary>
    public static class UnitLoads
    {
        /// <summary>
        /// The starter robot's barrow: a step and a half of ground on the sandbox's half-metre
        /// cells (0.125 m³ a step).
        ///
        /// It is a step and a half rather than a round 0.19 so that the scoop and the bed come to
        /// **whole steps** — three and nine. The ground only takes material a whole step at a
        /// time, so a load that is not a whole number of them cannot be put down: a bed of 9.12
        /// steps tips nine and keeps the remainder for ever.
        /// </summary>
        public const float Barrow = 0.1875f;

        /// <summary>The digger's scoop: two barrows — three steps — and three of them fill the dumper.</summary>
        public const float Scoop = 2f * Barrow;

        /// <summary>The dumper's bed: six barrows, or three scoops; nine steps, and it tips in one.</summary>
        public const float Bed = 6f * Barrow;
    }
}
