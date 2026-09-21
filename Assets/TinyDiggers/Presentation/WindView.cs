using UnityEngine;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Puts <see cref="WindSettings"/> into the shader globals the TinyDiggers/Foliage shader reads,
    /// every frame, so a tweak in the inspector shows at once.
    /// </summary>
    [ExecuteAlways]
    public sealed class WindView : MonoBehaviour
    {
        static readonly int WindId = Shader.PropertyToID("_TDWind");
        static readonly int GustId = Shader.PropertyToID("_TDGust");

        [SerializeField] WindSettings _settings;

        public WindSettings Settings => _settings;

        void OnEnable() => Apply();

        void Update() => Apply();

        public void Apply()
        {
            var s = _settings;
            if (s == null)
            {
                Shader.SetGlobalVector(WindId, new Vector4(0.82f, 0.57f, 0.35f, 1.6f));
                Shader.SetGlobalVector(GustId, new Vector4(30f, 0.7f, 6f, 0f));
                return;
            }

            var radians = s.Degrees * Mathf.Deg2Rad;
            Shader.SetGlobalVector(WindId, new Vector4(Mathf.Cos(radians), Mathf.Sin(radians), s.Strength, s.Speed));
            Shader.SetGlobalVector(GustId, new Vector4(s.GustSize, s.GustStrength, s.GustSpeed, 0f));
        }
    }
}
