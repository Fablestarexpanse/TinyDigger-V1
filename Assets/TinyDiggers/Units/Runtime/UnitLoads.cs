namespace TinyDiggers.Units
{
    /// <summary>
    /// What each unit carries, in loose cubic metres.
    ///
    /// These are one set of numbers rather than three, because what matters is the ratio between
    /// them and a ratio kept in three places drifts. Ronan's ruling (2026-09-22): three of the
    /// digger's scoops fill the dumper, and the starter robot takes six barrow loads to do the
    /// same job. Those ratios are what these numbers keep; their size is set by what the ground
    /// will actually let a unit do.
    ///
    /// On the sandbox's half-metre cells and half-metre steps, a **tip** puts down one step of a
    /// cell — 0.125 m³ of the load — and a **cut** takes one step out of the ground, which comes
    /// up bulked: a cut of dirt is 1.25 steps of load, and of rock 1.5.
    ///
    /// That mismatch is what sets the size. A unit that tips whole steps is left holding whatever
    /// did not make up a step, and it has to be able to take another cut on top of that remnant or
    /// it is stuck: too full to dig, too light to tip, standing in front of a job it could do. The
    /// remnant is under a step, and the worst cut is one and a half, so **a load has to hold at
    /// least two and a half steps**, and three is the round number above it.
    ///
    /// The old barrow was a step and a half. It deadlocked every robot on its second load — two
    /// cuts, two tips, then a remnant of half a step it could neither tip nor dig on top of — and
    /// it did it quietly, because a unit stuck that way still reads as Digging. Found in the deep
    /// pit trial, 2026-09-22, where four robots managed eight cuts between them and stopped.
    /// </summary>
    public static class UnitLoads
    {
        /// <summary>One tip: a half-metre step on a half-metre cell.</summary>
        public const float Step = 0.125f;

        /// <summary>
        /// The starter robot's barrow: three steps. Two cuts of rock, or three of sand, and always
        /// room for one more cut on top of a remnant.
        /// </summary>
        public const float Barrow = 3f * Step;

        /// <summary>The digger's scoop: two barrows — six steps — and three of them fill the dumper.</summary>
        public const float Scoop = 2f * Barrow;

        /// <summary>The dumper's bed: six barrows, or three scoops; eighteen steps, and it tips in one.</summary>
        public const float Bed = 6f * Barrow;
    }
}
