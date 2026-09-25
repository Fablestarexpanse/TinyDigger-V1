using TinyDiggers.Units;
using UnityEngine;

namespace TinyDiggers.Interaction
{
    /// <summary>
    /// The working craft's prefabs (2026-09-25), found at runtime as Resources/FleetCatalog.
    /// <c>TinyDiggers/Build Forge Machines</c> fills it in as it builds them, so a rebuild never
    /// leaves the game pointing at nothing.
    /// </summary>
    [CreateAssetMenu(menuName = "TinyDiggers/Fleet Catalog")]
    public sealed class FleetCatalog : ScriptableObject
    {
        public const string ResourcePath = "FleetCatalog";

        public GameObject Dredge;
        public GameObject GoldDredge;
        public GameObject Scow;
        public GameObject Tug;

        public GameObject For(VesselKind kind)
        {
            switch (kind)
            {
                case VesselKind.Dredge: return Dredge;
                case VesselKind.GoldDredge: return GoldDredge;
                case VesselKind.Scow: return Scow;
                case VesselKind.Tug: return Tug;
                default: return null;
            }
        }
    }
}
