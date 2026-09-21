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
        /// Digging, tipping or handing a load over is Work. Otherwise a loaded unit holds its load
        /// up (Carry), moving or not; an empty one on the move is Move, and anything else is Idle.
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

            if (loaded)
                return Carry;
            return state == CrewUnitState.Moving ? Move : Idle;
        }
    }
}
