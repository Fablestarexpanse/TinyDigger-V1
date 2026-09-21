using TinyDiggers.Terrain;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TinyDiggers.Presentation
{
    /// <summary>
    /// Puts the <see cref="LightingSettings"/> onto the scene: the sun, the ambient, the shadow
    /// settings on the pipeline asset, and the slope tint and contour lines on the terrain
    /// material. F4 turns the contours on and off.
    ///
    /// The sun is placed relative to whatever the camera is looking along, so the light always
    /// comes over the player's shoulder from one side however they turn the map. That is what makes
    /// one face of every terrace bright and the other dark, which is what makes slope readable.
    /// </summary>
    [ExecuteAlways]
    public sealed class LightingView : MonoBehaviour
    {
        static readonly int SlopeTintId = Shader.PropertyToID("_SlopeTint");
        static readonly int SlopeColourId = Shader.PropertyToID("_SlopeColour");
        static readonly int SlopeFullAtId = Shader.PropertyToID("_SlopeFullAt");
        static readonly int ContourStrengthId = Shader.PropertyToID("_ContourStrength");
        static readonly int MajorContourId = Shader.PropertyToID("_MajorContour");
        static readonly int MinorContourId = Shader.PropertyToID("_MinorContour");

        [SerializeField] TerrainView _terrain;
        [SerializeField] Light _sun;
        [SerializeField] LightingSettings _settings;

        [Tooltip("Degrees the sun sits from the camera's heading. Taken from the settings when there is one.")]
        [SerializeField] Transform _camera;

        bool _appliedOnce;

        /// <summary>The settings this view is applying. Never null once it has started.</summary>
        public LightingSettings Settings => _settings;

        void OnEnable() => Apply();

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f4Key.wasPressedThisFrame && _settings != null)
            {
                _settings.Contours = !_settings.Contours;
                PushToMaterial();
            }

            // Cheap, and it has to follow the camera's heading anyway.
            PlaceSun();
        }

        void OnValidate() => Apply();

        /// <summary>Applies everything: the sun, the ambient, the pipeline's shadows, the material.</summary>
        public void Apply()
        {
            if (_settings == null)
                return;

            PlaceSun();
            ApplyAmbient();
            ApplyPipeline();
            PushToMaterial();
            _appliedOnce = true;
        }

        void PlaceSun()
        {
            if (_sun == null || _settings == null)
                return;

            var heading = _camera != null ? _camera.eulerAngles.y : 0f;
            _sun.transform.rotation = Quaternion.Euler(_settings.Elevation, heading + _settings.AzimuthFromCamera, 0f);
            _sun.type = LightType.Directional;
            _sun.color = Mathf.CorrelatedColorTemperatureToRGB(_settings.Temperature);
            _sun.intensity = _settings.Intensity;
            _sun.shadows = _settings.SoftShadows ? LightShadows.Soft : LightShadows.Hard;
            _sun.shadowStrength = Mathf.Clamp01(_settings.ShadowStrength);
            // Biased generously on purpose: a one-metre-stepped heightfield seen from far out is
            // the worst case for shadow acne, and acne everywhere reads as a dark, dirty island.
            _sun.shadowBias = _settings.ShadowBias;
            _sun.shadowNormalBias = _settings.ShadowNormalBias;
        }

        void ApplyAmbient()
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = _settings.SkyColour * _settings.AmbientIntensity;
            RenderSettings.ambientEquatorColor = _settings.EquatorColour * _settings.AmbientIntensity;
            RenderSettings.ambientGroundColor = _settings.GroundColour * _settings.AmbientIntensity;
        }

        /// <summary>
        /// Shadow distance, cascades and resolution live on the pipeline asset rather than on the
        /// light, so they are set here — and ambient occlusion is a renderer feature, which is
        /// found on the renderer rather than on the asset.
        /// </summary>
        void ApplyPipeline()
        {
            if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset pipeline)
                return;

            pipeline.shadowDistance = _settings.ShadowDistance;
            pipeline.shadowCascadeCount = Mathf.Clamp(_settings.ShadowCascades, 1, 4);
            pipeline.mainLightShadowmapResolution = _settings.ShadowResolution;
            // Whether soft shadows are supported at all is a build-time setting on the asset and
            // is read-only here; the light's own shadow mode is what we can choose.
        }

        void PushToMaterial()
        {
            if (_terrain == null || _settings == null)
                return;
            var material = _terrain.DetailMaterial;
            if (material == null)
                return;

            material.SetFloat(SlopeTintId, _settings.SlopeTint);
            material.SetColor(SlopeColourId, _settings.SlopeColour);
            material.SetFloat(SlopeFullAtId, _settings.SlopeFullAt);
            material.SetFloat(ContourStrengthId, _settings.Contours ? _settings.ContourStrength : 0f);
            material.SetFloat(MajorContourId, _settings.MajorContour);
            material.SetFloat(MinorContourId, _settings.MinorContour);
        }

        void LateUpdate()
        {
            // The terrain builds its material in Awake, so the first push may have had nothing to
            // push to. Once it exists, push again and then leave it alone.
            if (_appliedOnce && _terrain != null && _terrain.DetailMaterial != null)
                PushToMaterial();
        }
    }
}
