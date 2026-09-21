// Terrain look per TERRAIN_REFERENCE.md section 5 ("Now"): no textures yet.
//
// - Colour: the vertex colour is the top material; UV1 carries the colour of the layer a steep
//   face cuts through. Faces past _SteepStart degrees blend towards it, fully by _SteepEnd.
// - Grain: three octaves of world-space value noise, finest at _GrainScale metres, scale the
//   colour by up to +-_GrainAmount so dirt and rock read as granular at RTS distance.
// - Edges: UV2 flags quad sides whose neighbour has a different top material; a faint dark line
//   is drawn along them. UV0 is the fragment's position within its cell.
// - Lighting: main light N.L plus spherical-harmonic ambient on the interpolated, smooth normal.
//   Faces darker than flat ground have _CreaseSoftening of the difference given back, so terrace
//   risers read as gentle creases. No shadows. Placeholder lighting, not final art.
Shader "TinyDiggers/Terrain Vertex Color"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _SteepStart ("Steep blend starts (degrees)", Range(0, 90)) = 40
        _SteepEnd ("Steep blend complete (degrees)", Range(0, 90)) = 50
        _GrainScale ("Grain scale, finest octave (m)", Range(0.05, 2)) = 0.25
        _GrainAmount ("Grain amount (fraction of colour)", Range(0, 0.3)) = 0.08
        _EdgeDarken ("Material edge darkening", Range(0, 0.5)) = 0.12
        _EdgeWidth ("Material edge width (cell fraction)", Range(0, 0.25)) = 0.04
        _CreaseSoftening ("Crease softening (share of shadow removed)", Range(0, 1)) = 0.5
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
            half _SteepStart;
            half _SteepEnd;
            float _GrainScale;
            half _GrainAmount;
            half _EdgeDarken;
            half _EdgeWidth;
            half _CreaseSoftening;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
                float2 local : TEXCOORD0;
                half4 exposed : TEXCOORD1;
                half4 edges : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half3 color : TEXCOORD2;
                half3 exposed : TEXCOORD3;
                float2 local : TEXCOORD4;
                nointerpolation half4 edges : TEXCOORD5;
            };

            half3 ToWorkingSpace(half3 srgb)
            {
                // Material colours are authored in sRGB, but vertex data reaches the shader
                // unconverted, so a linear-space project would otherwise wash them out.
                #if !defined(UNITY_COLORSPACE_GAMMA)
                return SRGBToLinear(srgb);
                #else
                return srgb;
                #endif
            }

            // Integer-lattice hash (Hoskins' hash13): stable at world coordinates in the hundreds,
            // unlike frac(sin(...)).
            float Hash(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.zyx + 31.32);
                return frac((p.x + p.y) * p.z);
            }

            // 3D value noise in [-1, 1]. Three-dimensional so vertical cut faces get grain too,
            // not stretched streaks.
            float ValueNoise(float3 p)
            {
                float3 cell = floor(p);
                float3 f = frac(p);
                float3 u = f * f * (3.0 - 2.0 * f);
                float n000 = Hash(cell);
                float n100 = Hash(cell + float3(1, 0, 0));
                float n010 = Hash(cell + float3(0, 1, 0));
                float n110 = Hash(cell + float3(1, 1, 0));
                float n001 = Hash(cell + float3(0, 0, 1));
                float n101 = Hash(cell + float3(1, 0, 1));
                float n011 = Hash(cell + float3(0, 1, 1));
                float n111 = Hash(cell + float3(1, 1, 1));
                float n = lerp(
                    lerp(lerp(n000, n100, u.x), lerp(n010, n110, u.x), u.y),
                    lerp(lerp(n001, n101, u.x), lerp(n011, n111, u.x), u.y),
                    u.z);
                return n * 2.0 - 1.0;
            }

            // Octaves at 4x, 2x and 1x the finest scale, weighted towards the finest so the grain
            // reads at 0.25 m while the coarser ones break up repetition. Result in [-1, 1].
            float Grain(float3 positionWS)
            {
                float3 p = positionWS / _GrainScale;
                return ValueNoise(p) * 0.5 + ValueNoise(p * 0.5 + 17.3) * 0.3 + ValueNoise(p * 0.25 + 41.7) * 0.2;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.color = ToWorkingSpace(input.color.rgb) * _Tint.rgb;
                output.exposed = ToWorkingSpace(input.exposed.rgb) * _Tint.rgb;
                output.local = input.local;
                output.edges = input.edges;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half3 normalWS = normalize(input.normalWS);

                // normal.y is the cosine of the slope angle: 1 flat, 0 vertical.
                half steepness = 1.0h - smoothstep(cos(radians(_SteepEnd)), cos(radians(_SteepStart)), normalWS.y);
                half3 albedo = lerp(input.color, input.exposed, steepness);

                albedo *= 1.0h + _GrainAmount * (half)Grain(input.positionWS);

                // Distance to each flagged side of the cell, in cell units; unflagged sides are
                // pushed far away. fwidth keeps the line at least a pixel wide and anti-aliased.
                float2 uv = input.local;
                float4 toEdge = float4(uv.x, 1.0 - uv.x, uv.y, 1.0 - uv.y) + (1.0 - input.edges) * 10.0;
                float nearest = min(min(toEdge.x, toEdge.y), min(toEdge.z, toEdge.w));
                float width = max(_EdgeWidth, fwidth(nearest));
                albedo *= 1.0h - _EdgeDarken * (half)(1.0 - smoothstep(0.0, width, nearest));

                Light mainLight = GetMainLight();
                half diffuse = saturate(dot(normalWS, mainLight.direction));
                half3 lighting = mainLight.color * diffuse + SampleSH(normalWS);

                // Terrace creases: a riser turned away from the sun is darker than flat ground.
                // Give back _CreaseSoftening of that shortfall; faces lit brighter than flat keep
                // their full light, so relief still reads.
                half3 flatLighting = mainLight.color * saturate(mainLight.direction.y) + SampleSH(half3(0, 1, 0));
                lighting = lerp(lighting, max(lighting, flatLighting), _CreaseSoftening);

                return half4(albedo * lighting, 1);
            }
            ENDHLSL
        }

        // Lets the terrain appear in the depth texture, which URP's depth-based effects read.
        // URP draws this, not DepthOnly, whenever anything asks for normals, and SSAO does. Without
        // it the terrain was missing from the depth texture: no ambient occlusion on the land, and
        // the water thought every pixel of sea was bottomless.
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
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Vert(float4 positionOS : POSITION) : SV_POSITION
            {
                return TransformObjectToHClip(positionOS.xyz);
            }

            half Frag() : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
