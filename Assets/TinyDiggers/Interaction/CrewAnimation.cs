using TinyDiggers.Units;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// Which clip of the crew robot's Animator a unit plays. The states are the ones in
    /// <c>crew_unit.controller</c>: Idle, Move, Work and Carry.
    /// </summary>
    public static class CrewAnimation
    {
        public const string Idle = "Idle";
        public const string Move = "Move";
        public const string Work = "Work";
        public const string Carry = "Carry";

        /// <summary>
        /// Digging, tipping or handing a load over is Work. A unit on the move plays Carry with a
        /// load and Move without one. Anything standing still plays Idle.
        ///
        /// Carry used to mean "loaded, moving or not", which is true of the crew robot — its Carry
        /// is a standing pose with the load held up. It is not true of the machines: the digger's
        /// Carry is its walk cycle and the dumper's is walk_loaded, so a loaded machine standing
        /// still walked on the spot (Ronan, 2026-09-23: "when they are standing still their legs
        /// are still walking"). Carry is carrying *along*, not holding, and only a unit that is
        /// actually going somewhere should play it.
        /// </summary>
        public static string StateFor(CrewUnitState state, bool loaded)
        {
            switch (state)
            {
                case CrewUnitState.Digging:
                case CrewUnitState.Tipping:
                case CrewUnitState.Transferring:
                    return Work;
            }

            if (state != CrewUnitState.Moving)
                return Idle;
            return loaded ? Carry : Move;
        }
    }
}
