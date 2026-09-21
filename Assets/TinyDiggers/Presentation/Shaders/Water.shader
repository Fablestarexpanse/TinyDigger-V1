// The sea and the rivers. Our own, deliberately: no bought water asset, no spline package, just
// what the tabletop needs. WaterView fills every property here from WaterSettings.
//
// What is baked into the mesh when the land changes (WaterField):
// - UV1: x metres of water over the vertex, y metres travelled down a river channel.
// - UV2: x metres through the water to the nearest shore, y how fast a river runs here (0 = sea).
// - UV3: which way the nearest shore lies for the sea, which way the water runs for a river.
//
// What is done per frame:
// - Vertex: a seeded, random mix of Gerstner waves (set as globals by WaterView), rougher and
//   calmer in patches across the map, dying away in the shallows and at the shore; plus waves
//   running in to the beach, lifting the surface a little as they come.
// - Fragment: the seabed seen through the water (the camera's opaque texture, bent by the
//   ripples), dimmed by how much water the eye looks through (the depth texture), with scattered
//   colour building up with depth; caustics on shallow seabed; the sky reflected at grazing
//   angles; the sun glinting; foam where the water meets anything, where waves break on the beach
//   and on the tallest crests.
Shader "TinyDiggers/Water"
{
    Properties
    {
        _ScatterShallow ("Scatter shallow", Color) = (0.20, 0.62, 0.62, 1)
        _ScatterDeep ("Scatter deep", Color) = (0.04, 0.20, 0.33, 1)
        _Absorption ("Absorption per metre (rgb)", Vector) = (0.45, 0.16, 0.11, 0)
        _ScatterDensity ("Scatter density", Float) = 0.12
        _Refraction ("Refraction", Range(0, 0.1)) = 0.025

        _Wind ("Wind (xz on the map)", Vector) = (0.82, 0.57, 0, 0)
        _GustSize ("Gust size (m)", Float) = 140
        _GustCalm ("Calmest gust", Range(0, 1)) = 0.3
        _DampDepth ("Swell dies under (m)", Float) = 4
        _DampDistance ("Swell dies within (m of shore)", Float) = 6
        _SwellHeight ("Tallest crest (m)", Float) = 0.8
        _Whitecaps ("Whitecaps at", Range(0, 1.5)) = 0.75

        _ShoreReach ("Shore waves reach (m)", Float) = 16
        _ShoreSpacing ("Shore wave spacing (m)", Float) = 6
        _ShoreSpeed ("Shore waves per second", Float) = 0.18
        _ShoreLift ("Shore wave lift (m)", Float) = 0.12
        _ShoreFoam ("Shore foam", Range(0, 1)) = 0.9

        [NoScaleOffset] _Detail ("Ripples (normal rg, height b)", 2D) = "bump" {}
        _RippleSize ("Ripple size (m)", Float) = 9
        _RippleDetailSize ("Ripple detail size (m)", Float) = 3.1
        _RippleStrength ("Ripple strength", Range(0, 2)) = 0.55
        _RippleSpeed ("Ripple speed", Float) = 0.35
        _RippleFade ("Ripple fade (m)", Float) = 220

        _SkyHorizon ("Sky at horizon", Color) = (0.78, 0.84, 0.88, 1)
        _SkyZenith ("Sky overhead", Color) = (0.38, 0.56, 0.78, 1)
        _Reflection ("Reflection", Range(0, 1)) = 0.8
        _FresnelPower ("Fresnel power", Range(1, 10)) = 5
        _SunGlint ("Sun glint", Range(0, 10)) = 3
        _SunSharpness ("Sun sharpness", Range(8, 2048)) = 600
        _Caustics ("Caustics", Range(0, 2)) = 0.45
        _CausticSize ("Caustic size (m)", Float) = 4

        _Foam ("Foam", Color) = (0.95, 0.97, 0.96, 1)
        _ContactFoam ("Contact foam (m)", Float) = 0.6
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            // The seabed is composited here from the opaque texture, so the water is drawn opaque
            // over it rather than blended: blending would show the seabed twice.
            Blend One Zero
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            // The same light keywords URP's own Lit shader compiles with. Our renderer is
            // Forward+ with light layers on; without these variants the sun never reached this
            // shader and the land was lit by the ambient alone.
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ScatterShallow;
                half4 _ScatterDeep;
                float4 _Absorption;
                float _ScatterDensity;
                float _Refraction;
                float4 _Wind;
                float _GustSize;
                float _GustCalm;
                float _DampDepth;
                float _DampDistance;
                float _SwellHeight;
                float _Whitecaps;
                float _ShoreReach;
                float _ShoreSpacing;
                float _ShoreSpeed;
                float _ShoreLift;
                float _ShoreFoam;
                float _RippleSize;
                float _RippleDetailSize;
                float _RippleStrength;
                float _RippleSpeed;
                float _RippleFade;
                half4 _SkyHorizon;
                half4 _SkyZenith;
                float _Reflection;
                float _FresnelPower;
                float _SunGlint;
                float _SunSharpness;
                float _Caustics;
                float _CausticSize;
                half4 _Foam;
                float _ContactFoam;
            CBUFFER_END

            // The swell, set by WaterView from WaveSet. A: direction xz, amplitude, wave number.
            // B: Gerstner steepness, speed, phase.
            int _TDWaveCount;
            float4 _TDWaveA[8];
            float4 _TDWaveB[8];

            TEXTURE2D(_Detail);
            SAMPLER(sampler_Detail);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 water : TEXCOORD1;
                float2 shore : TEXCOORD2;
                float2 direction : TEXCOORD3;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 water : TEXCOORD2;      // depth, along, shore distance, flow speed
                float4 extra : TEXCOORD3;      // direction xz, crest (0..1+), fog
            };

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash21(i);
                float b = Hash21(i + float2(1, 0));
                float c = Hash21(i + float2(0, 1));
                float d = Hash21(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // Waves running in to the shore: bands on the shore distance that move towards land,
            // broken along their length so they arrive as sets, not as rings round the island.
            float ShoreBand(float shoreDistance, float2 xz)
            {
                if (shoreDistance <= 0.0 || shoreDistance >= _ShoreReach)
                    return 0.0;
                float wobble = ValueNoise(xz / 23.0) * 0.9 + ValueNoise(xz / 7.0) * 0.25;
                float phase = shoreDistance / max(0.5, _ShoreSpacing) + _Time.y * _ShoreSpeed + wobble;
                float crest = pow(saturate(sin(phase * 6.2831853) * 0.5 + 0.5), 5.0);
                float sets = saturate(ValueNoise(xz / 31.0 + _Time.y * 0.05) * 1.6 - 0.25);
                float envelope = saturate(1.0 - shoreDistance / _ShoreReach) * saturate(shoreDistance * 0.8);
                return crest * sets * envelope;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float depth = input.water.x;
                float shoreDistance = input.shore.x;
                float flow = input.shore.y;

                // Swell: none on a river, none in the shallows, rougher and calmer in patches.
                float damping = flow > 0.0 ? 0.0 : saturate(depth / max(0.01, _DampDepth)) * saturate(shoreDistance / max(0.01, _DampDistance));
                float gust = lerp(_GustCalm, 1.0, ValueNoise(positionWS.xz / max(1.0, _GustSize) + _Time.y * 0.01));
                float scale = damping * gust;

                float3 displacement = 0;
                float3 normal = float3(0, 1, 0);
                float2 xz = positionWS.xz;
                [loop]
                for (int i = 0; i < _TDWaveCount; i++)
                {
                    float4 a = _TDWaveA[i];
                    float4 b = _TDWaveB[i];
                    float amplitude = a.z * scale;
                    float f = a.w * (dot(a.xy, xz) - b.y * _Time.y) + b.z;
                    float s, c;
                    sincos(f, s, c);
                    displacement.x += b.x * amplitude * a.x * c;
                    displacement.z += b.x * amplitude * a.y * c;
                    displacement.y += amplitude * s;
                    float wa = a.w * amplitude;
                    normal.x -= a.x * wa * c;
                    normal.z -= a.y * wa * c;
                    normal.y -= b.x * wa * s;
                }

                // Waves running in lift the surface a little ahead of breaking.
                float band = flow > 0.0 ? 0.0 : ShoreBand(shoreDistance, xz);
                displacement.y += band * _ShoreLift;

                positionWS += displacement;
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = normalize(normal);
                output.water = float4(depth, input.water.y, shoreDistance, flow);
                // How near this point is to the top of the local swell, for whitecaps.
                float crest = displacement.y / max(0.05, _SwellHeight * gust);
                output.extra = float4(input.direction, crest * damping, ComputeFogFactor(output.positionCS.z));
                return output;
            }

            float3 Ripples(float3 positionWS, float2 flowDirection, float flow, float distance, out float height)
            {
                // The sea's ripples drift down the wind; a river's run down its channel.
                float2 along = flow > 0.0 ? flowDirection : normalize(_Wind.xy + 1e-4);
                float speed = flow > 0.0 ? flow : _RippleSpeed;
                float2 across = float2(-along.y, along.x);

                float2 p = positionWS.xz;
                float2 uvA = p / _RippleSize - along * (_Time.y * speed / _RippleSize);
                // Turned against the first, so the two layers never line up into a pattern.
                float2 turned = float2(dot(p, float2(0.64, -0.77)), dot(p, float2(0.77, 0.64)));
                float2 uvB = turned / _RippleDetailSize - (along * 0.8 + across * 0.3) * (_Time.y * speed * 1.4 / _RippleDetailSize);

                half4 a = SAMPLE_TEXTURE2D(_Detail, sampler_Detail, uvA);
                half4 b = SAMPLE_TEXTURE2D(_Detail, sampler_Detail, uvB);
                height = (a.b + b.b) * 0.5;

                float2 slope = (a.rg * 2.0 - 1.0) + (b.rg * 2.0 - 1.0) * 0.6;
                float fade = lerp(0.33, 1.0, saturate(1.0 - distance / max(1.0, _RippleFade)));
                return float3(slope.x, 0, slope.y) * _RippleStrength * fade;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 positionWS = input.positionWS;
                float3 view = GetWorldSpaceNormalizeViewDir(positionWS);
                float cameraDistance = distance(_WorldSpaceCameraPos, positionWS);
                float shoreDistance = input.water.z;
                float flow = input.water.w;

                float rippleHeight;
                float3 normalWS = normalize(input.normalWS + Ripples(positionWS, input.extra.xy, flow, cameraDistance, rippleHeight));

                // --- What lies under the water ------------------------------------------------
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float surfaceEye = input.positionCS.w;
                float sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float thickness = max(0.0, sceneEye - surfaceEye);

                // Bend the seabed by the ripples, less where the water is thin so edges stay put,
                // and never onto something standing in front of the water.
                float2 bentUV = screenUV + normalWS.xz * _Refraction * saturate(thickness * 0.25);
                float bentEye = LinearEyeDepth(SampleSceneDepth(bentUV), _ZBufferParams);
                if (bentEye < surfaceEye)
                {
                    bentUV = screenUV;
                    bentEye = sceneEye;
                }

                float path = max(0.0, bentEye - surfaceEye);
                float3 seabed = SampleSceneColor(bentUV);

                Light mainLight = GetMainLight(TransformWorldToShadowCoord(positionWS));
                float shadow = mainLight.shadowAttenuation;
                float3 sun = mainLight.color * lerp(0.45, 1.0, shadow);
                float3 ambient = SampleSH(float3(0, 1, 0));

                // Caustics on shallow seabed: bright where two drifting ripple layers cross.
                float3 camForward = -UNITY_MATRIX_V[2].xyz;
                float3 ray = -view;
                float3 seabedWS = _WorldSpaceCameraPos + ray * (bentEye / max(0.05, dot(ray, camForward)));
                float2 causticUV = seabedWS.xz / _CausticSize;
                float c1 = SAMPLE_TEXTURE2D(_Detail, sampler_Detail, causticUV + _Time.y * float2(0.021, 0.013)).b;
                float c2 = SAMPLE_TEXTURE2D(_Detail, sampler_Detail, causticUV * 0.77 - _Time.y * float2(0.017, 0.024)).b;
                float caustic = saturate(1.0 - abs(c1 - c2) * 5.0);
                caustic = caustic * caustic * caustic * _Caustics * exp(-path * 0.35) * saturate(path * 2.0);
                seabed += seabed * caustic * sun * 2.0;

                // Water eats red first, then green: what comes back through it turns blue-green,
                // and the light it scatters builds up in its place.
                float3 transmittance = exp(-_Absorption.rgb * path);
                float scatterAmount = 1.0 - exp(-path * _ScatterDensity);
                float3 scatter = lerp(_ScatterShallow.rgb, _ScatterDeep.rgb, scatterAmount);
                scatter *= ambient * 0.6 + sun * saturate(mainLight.direction.y) * 0.55;
                float3 body = seabed * transmittance + scatter * (1.0 - transmittance);

                // --- The surface -------------------------------------------------------------
                float3 reflected = reflect(-view, normalWS);
                float3 sky = lerp(_SkyHorizon.rgb, _SkyZenith.rgb, saturate(reflected.y * 1.5));
                float facing = saturate(dot(normalWS, view));
                float fresnel = (0.02 + 0.98 * pow(1.0 - facing, _FresnelPower)) * _Reflection;
                float3 colour = lerp(body, sky, fresnel);

                float3 halfway = normalize(mainLight.direction + view);
                float glint = pow(saturate(dot(normalWS, halfway)), _SunSharpness) * _SunGlint;
                colour += mainLight.color * glint * shadow;

                // --- Foam ---------------------------------------------------------------------
                // Where the water meets anything standing in it: shores, banks, anything dug.
                float breakup = smoothstep(0.25, 0.75, rippleHeight);
                float contact = (1.0 - smoothstep(0.0, _ContactFoam, thickness)) * saturate(thickness * 20.0);
                contact *= lerp(0.55, 1.0, breakup);
                // Where waves run up the beach.
                float band = flow > 0.0 ? 0.0 : ShoreBand(shoreDistance, positionWS.xz);
                // Only in the breaking zone near the beach: further out a wave running in is a
                // lift of the surface, not a line of foam painted on the sea.
                float shoreFoam = band * _ShoreFoam * smoothstep(0.35, 0.6, rippleHeight + band * 0.3)
                    * saturate(1.0 - shoreDistance / max(1.0, _ShoreReach * 0.4));
                // On the tallest crests out at sea.
                float whitecap = saturate((input.extra.z - _Whitecaps) * 4.0) * smoothstep(0.65, 0.9, rippleHeight);
                // White water on a river where it runs fast.
                float rapids = flow > 0.0 ? saturate(flow / 4.0 - 0.35) * smoothstep(0.55, 0.8, rippleHeight) : 0.0;

                float foam = saturate(max(max(contact, shoreFoam), max(whitecap, rapids)));
                // Lit by how bright the sun is, not its colour: warm sunlight on white foam over
                // blue water reads as pink.
                float sunBrightness = dot(sun, float3(0.2126, 0.7152, 0.0722));
                float3 foamLit = _Foam.rgb * (Luminance(ambient) * 0.55 + sunBrightness * saturate(mainLight.direction.y) * 0.8);
                colour = lerp(colour, foamLit, foam);

                colour = MixFog(colour, input.extra.w);
                return half4(colour, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
