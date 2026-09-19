using TinyDiggers.Units;
using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// Scene placeholder for the one crew in Vertical Slice 1. Owns a <see cref="Units.Crew"/>;
    /// holds no logic of its own. It has no body and does not move yet.
    /// </summary>
    public sealed class CrewView : MonoBehaviour
    {
        [Tooltip("Loose m³ the crew can carry.")]
        [SerializeField, Min(0.1f)] float _capacity = MaterialInventory.DefaultCapacity;

        public Crew Crew { get; private set; }

        void Awake()
        {
            Crew = new Crew(_capacity);
        }
    }
}
