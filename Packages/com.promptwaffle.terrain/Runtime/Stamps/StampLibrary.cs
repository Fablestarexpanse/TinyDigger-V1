using System.Collections.Generic;
using UnityEngine;

namespace PromptWaffle.Terrain
{
    /// <summary>
    /// Every height stamp there is, one list for the generator and the terraform tool (Ronan,
    /// 2026-09-24). Lives at Resources/StampLibrary so either can find it without wiring; built and
    /// kept up to date by TinyDiggers/Import Stamps.
    /// </summary>
    [CreateAssetMenu(menuName = "TinyDiggers/Stamp Library")]
    public sealed class StampLibrary : ScriptableObject
    {
        public const string ResourcePath = "StampLibrary";

        public List<HeightStamp> Stamps = new List<HeightStamp>();

        static StampLibrary _loaded;

        /// <summary>The library in Resources, or null when there is none yet.</summary>
        public static StampLibrary Load()
        {
            if (_loaded == null)
                _loaded = Resources.Load<StampLibrary>(ResourcePath);
            return _loaded;
        }

        /// <summary>The stamps offered to <paramref name="use"/>, in library order.</summary>
        public IEnumerable<HeightStamp> For(StampUse use)
        {
            foreach (var stamp in Stamps)
                if (stamp != null && (stamp.Use & use) != 0)
                    yield return stamp;
        }
    }
}
