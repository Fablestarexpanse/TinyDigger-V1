// The dam's cast concrete (Ronan, 2026-09-21: "a very good looking cement texture").
//
// Two scales:
// - Close up, a tileable detail set (Editor/DamTextureGenerator.cs) projected triplanar in world
//   space every _Repeat metres: paste blotches, sand grain, aggregate, air voids.
// - At the scale of the structure, everything drawn from position rather than from a texture, so
//   none of it repeats:
//   - formwork panels on walls, _PanelSize metres, with joints, tie holes and a tone per panel;
//   - rain streaks down vertical faces;
//   - grime on ledges and a darker, sheltered underside;
//   - a wet, green-dark band at the waterline, and algae below it;
//   - a slow mottle across the whole ring, and a tone per piece (uv0.w, set by DamView).
//
// The formwork follows the wall: uv0.x is metres along the ring and uv0.z metres outward from the
// inner face (both set by DamView), so panels run true round the curve. On faces that look along
// the ring (fin and pier sides) the panels run outward instead.
Shader "TinyDiggers/Concrete"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        [NoScaleOffset] _Albedo ("Detail albedo", 2D) = "grey" {}
        [NoScaleOffset] _Normal ("Detail normal (RGB, green up)", 2D) = "bump" {}
        [NoScaleOffset] _Mask ("Detail mask (R cavity, G void, B roughness)", 2D) = "white" {}
        _Repeat ("Metres per detail tile", Float) = 4
        _DetailStrength ("Detail normal strength", Range(0, 2)) = 0.8
        _Smoothness ("Smoothness", Range(0, 1)) = 0.18

        _PanelSize ("Formwork panel (width, height, m)", Vector) = (2.4, 1.2, 0, 0)
        _JointWidth ("Joint width (m)", Float) = 0.03
        _JointDepth ("Joint darkness", Range(0, 1)) = 0.25
        _TieHoles ("Tie hole darkness", Range(0, 1)) = 0.45
        _FormworkFade ("Formwork fades out past this many m per pixel", Float) = 0.035
        _PanelTone ("Tone variation per panel", Range(0, 0.2)) = 0.045
        _PieceTone ("Tone variation per piece", Range(0, 0.2)) = 0.05
        _Mottle ("Slow mottle across the ring", Range(0, 0.3)) = 0.08

        _Streaks ("Rain streaks", Range(0, 1)) = 0.35
        _Grime ("Grime on ledges", Range(0, 1)) = 0.3
        _Underside ("Sheltered underside", Range(0, 1)) = 0.3
        _Waterline ("Waterline band", Range(0, 1)) = 0.6
        _GrimeColour ("Grime colour", Color) = (0.36, 0.35, 0.3, 1)
        _AlgaeColour ("Algae colour", Color) = (0.22, 0.29, 0.24, 1)
        _AmbientSaturation ("Ambient light saturation", Range(0, 1)) = 0.25
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _Tint;
            float _Repeat;
            half _DetailStrength;
            half _Smoothness;
            float4 _PanelSize;
            float _JointWidth;
            half _JointDepth;
            half _TieHoles;
            float _FormworkFade;
            half _PanelTone;
            half _PieceTone;
            half _Mottle;
            half _Streaks;
            half _Grime;
            half _Underside;
            half _Waterline;
            half4 _GrimeColour;
            half4 _AlgaeColour;
            half _AmbientSaturation;
        CBUFFER_END

        // Set once by DamView for the whole ring: the middle of the disc, in world space.
        float4 _DamCentre;
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            // The light keywords URP's Lit compiles with: Forward+ with light layers, so without
            // them the sun never reaches a custom shader (see TerrainTriplanar).
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_Albedo); SAMPLER(sampler_Albedo);
            TEXTURE2D(_Normal); SAMPLER(sampler_Normal);
            TEXTURE2D(_Mask); SAMPLER(sampler_Mask);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 wall : TEXCOORD0;    // x along the ring (m), y height (m), z out (m), w piece tone
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 wall : TEXCOORD2;
                half fogFactor : TEXCOORD3;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.wall = input.wall;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

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

            // How far a coordinate is from the nearest multiple of size, in the same units.
            float DistanceToLine(float coordinate, float size)
            {
                return abs(frac(coordinate / size + 0.5) - 0.5) * size;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 positionWS = input.positionWS;
                float3 n = normalize(input.normalWS);

                // --- close-up detail, triplanar ---------------------------------------------
                float3 weights = pow(abs(n), 4.0);
                weights /= max(weights.x + weights.y + weights.z, 1e-4);
                float2 uvX = positionWS.zy / _Repeat;
                float2 uvY = positionWS.xz / _Repeat;
                float2 uvZ = positionWS.xy / _Repeat;
                half3 albedo = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, uvX).rgb * weights.x
                    + SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, uvY).rgb * weights.y
                    + SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, uvZ).rgb * weights.z;
                half3 mask = SAMPLE_TEXTURE2D(_Mask, sampler_Mask, uvX).rgb * weights.x
                    + SAMPLE_TEXTURE2D(_Mask, sampler_Mask, uvY).rgb * weights.y
                    + SAMPLE_TEXTURE2D(_Mask, sampler_Mask, uvZ).rgb * weights.z;
                half3 nx = SAMPLE_TEXTURE2D(_Normal, sampler_Normal, uvX).rgb * 2.0 - 1.0;
                half3 ny = SAMPLE_TEXTURE2D(_Normal, sampler_Normal, uvY).rgb * 2.0 - 1.0;
                half3 nz = SAMPLE_TEXTURE2D(_Normal, sampler_Normal, uvZ).rgb * 2.0 - 1.0;
                // Whiteout-style: each projection's tangent-space bend turned into world axes.
                float3 bend = float3(0, nx.y, nx.x) * weights.x * sign(n.x)
                    + float3(ny.x, 0, ny.y) * weights.y
                    + float3(nz.x, nz.y, 0) * weights.z * sign(n.z);

                // --- which way the face looks, in the dam's frame ----------------------------
                float2 radial = positionWS.xz - _DamCentre.xz;
                radial = radial / max(length(radial), 1e-3);
                float facesRing = abs(dot(n.xz, radial));            // inner and outer faces
                float vertical = saturate(1.0 - abs(n.y) * 1.6);      // walls rather than decks
                float along = facesRing >= abs(dot(n.xz, float2(radial.y, -radial.x)))
                    ? input.wall.x : input.wall.z;
                float up = positionWS.y;

                // --- formwork: panels, joints and tie holes, on walls only ---------------------
                float2 panelSize = _PanelSize.xy;
                float2 panel = float2(along, up) / panelSize;
                float2 cell = floor(panel);
                float jointX = DistanceToLine(along, panelSize.x);
                float jointY = DistanceToLine(up, panelSize.y);
                float aa = max(fwidth(along), fwidth(up)) + 1e-4;
                float joint = 1.0 - smoothstep(_JointWidth, _JointWidth + aa, min(jointX, jointY));
                // Four tie holes per panel, at the quarter points.
                float2 inPanel = frac(panel) * panelSize;
                float2 tie = float2(DistanceToLine(inPanel.x - panelSize.x * 0.25, panelSize.x * 0.5),
                                    DistanceToLine(inPanel.y - panelSize.y * 0.25, panelSize.y * 0.5));
                float hole = 1.0 - smoothstep(0.035, 0.035 + aa, length(tie));
                float panelTone = (Hash21(cell + 17.0) - 0.5) * 2.0 * _PanelTone;
                // Joints and tie holes are a close-up detail: past a few centimetres a pixel they
                // alias into a tiled grid (the first pass read as a bathroom wall from 100 m), so
                // they fade out and leave only the panel tones.
                float nearness = 1.0 - smoothstep(_FormworkFade * 0.4, _FormworkFade, aa);
                joint *= nearness;
                hole *= nearness;
                float formwork = vertical;

                half3 colour = albedo * _Tint.rgb;
                colour *= 1.0 + panelTone * formwork;
                colour *= 1.0 - joint * _JointDepth * formwork;
                colour *= 1.0 - hole * _TieHoles * formwork;

                // --- the structure's own weathering ------------------------------------------
                float mottle = ValueNoise(positionWS.xz / 18.0 + up / 9.0) - 0.5;
                colour *= 1.0 + mottle * 2.0 * _Mottle + (input.wall.w - 0.5) * 2.0 * _PieceTone;

                // Rain streaks: long, thin, down the walls, heavier just under the top of a face.
                float streak = ValueNoise(float2(along * 2.3, up * 0.07)) * ValueNoise(float2(along * 0.6 + 11.0, up * 0.2));
                streak = smoothstep(0.18, 0.55, streak) * vertical;
                colour = lerp(colour, colour * _GrimeColour.rgb * 1.6, streak * _Streaks);

                // Grime settles on ledges; undersides sit in their own dirt and shade.
                float ledge = saturate((n.y - 0.5) * 2.0);
                float ledgeNoise = ValueNoise(positionWS.xz * 0.7) * 0.6 + 0.4;
                colour = lerp(colour, colour * _GrimeColour.rgb * 1.5, ledge * ledgeNoise * _Grime);
                float underside = saturate(-n.y * 1.5);
                colour *= 1.0 - underside * _Underside;

                // Waterline: wet and dark just above the sea, algae below it. Only on the inner
                // face (the first metre out from it): outside the dam there is no sea, only void.
                float innerFace = 1.0 - saturate(input.wall.z - 1.0);
                float wetBand = smoothstep(0.6, -0.2, up) * smoothstep(-2.5, -0.6, up) * innerFace;
                float submerged = smoothstep(-0.6, -2.0, up) * innerFace;
                colour = lerp(colour, colour * _AlgaeColour.rgb * 1.9, saturate(wetBand * 0.7 + submerged) * _Waterline);
                half smoothness = _Smoothness * mask.b * 1.6 + wetBand * 0.35 * _Waterline;

                // Voids sit in shadow: the mask's cavity darkens and occludes.
                colour *= lerp(0.55, 1.0, mask.r);

                float3 normal = normalize(n + bend * _DetailStrength * (1.0 - joint * 0.5));
                // Joints are grooves: tilt the normal across them so they catch the light.
                normal = normalize(normal - n * joint * 0.3 * formwork);

                InputData inputData = (InputData)0;
                inputData.positionWS = positionWS;
                inputData.normalWS = normal;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(positionWS);
                inputData.fogCoord = input.fogFactor;
                // The navy sky's ambient, through the scene's saturation grade, turned every face
                // out of the sun olive-green. Concrete in shade should read grey, so the ambient is
                // mostly desaturated here (brightness kept).
                half3 ambient = SampleSH(normal);
                inputData.bakedGI = lerp(Luminance(ambient).xxx, ambient, _AmbientSaturation);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = saturate(colour);
                surfaceData.metallic = 0;
                surfaceData.smoothness = saturate(smoothness);
                surfaceData.occlusion = lerp(0.6, 1.0, mask.r) * (1.0 - joint * 0.3 * formwork) * (1.0 - hole * 0.5 * formwork);
                surfaceData.alpha = 1;
                surfaceData.normalTS = half3(0, 0, 1);

                half4 result = UniversalFragmentPBR(inputData, surfaceData);
                result.rgb = MixFog(result.rgb, input.fogFactor);
                return result;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            ShadowVaryings ShadowVert(ShadowAttributes input)
            {
                ShadowVaryings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                output.positionCS = positionCS;
                return output;
            }

            half4 ShadowFrag(ShadowVaryings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // SSAO reads depth and normals from this pass; without it the dam would be missing from
        // the depth texture (see TerrainTriplanar).
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On

            HLSLPROGRAM
            #pragma vertex DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            struct DepthNormalsAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct DepthNormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
            };

            DepthNormalsVaryings DepthNormalsVert(DepthNormalsAttributes input)
            {
                DepthNormalsVaryings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 DepthNormalsFrag(DepthNormalsVaryings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 octahedral = PackNormalOctQuadEncode(normalWS);
                    return half4(PackFloat2To888(saturate(octahedral * 0.5 + 0.5)), 0.0);
                #else
                    return half4(normalWS, 0.0);
                #endif
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            DepthVaryings DepthVert(DepthAttributes input)
            {
                DepthVaryings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthFrag(DepthVaryings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
