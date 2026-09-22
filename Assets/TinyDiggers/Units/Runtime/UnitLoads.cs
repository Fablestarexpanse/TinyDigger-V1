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
        /// <summary>The starter robot's barrow.</summary>
        public const float Barrow = 0.19f;

        /// <summary>The digger's scoop: two barrows, so three of them fill the dumper.</summary>
        public const float Scoop = 2f * Barrow;

        /// <summary>The dumper's bed: six barrows, or three scoops.</summary>
        public const float Bed = 6f * Barrow;
    }
}
