// Short turf drawn from the GPU grass buffer (GrassSpawn.compute, GpuGrass): one clump mesh, one
// indirect draw, each instance read back here by its instance id (Ronan, 2026-09-24).
//
// - Placed, turned and sized from the buffer; coloured from the ground it grew out of, darker at the
//   root and lighter at the tip, which is most of what makes turf read as turf from above.
// - The wind is the trees' wind (_TDWind, _TDGust from WindView), so everything moves together.
// - Lit by URP: main light with its shadows, ambient; soft wrap so a blade edge-on to the sun is not
//   black. Compiled with URP's light keywords, or Forward+ with light layers leaves it unlit.
Shader "TinyDiggers/Grass Blades"
{
    Properties
    {
        _Brightness ("Brightness", Range(0.3, 1.5)) = 0.8
        _RootDarken ("Root darkening", Range(0, 1)) = 0.62
        _TipLighten ("Tip lightening", Range(0, 1)) = 0.08
        _WindScale ("Wind scale", Range(0, 4)) = 1
        _Translucency ("Translucency", Range(0, 1)) = 0.15
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct GrassInstance
        {
            float3 position;
            float rotation;
            float3 colour;
            float scale;
        };

        StructuredBuffer<GrassInstance> _Instances;

        CBUFFER_START(UnityPerMaterial)
            half _Brightness;
            half _RootDarken;
            half _TipLighten;
            half _WindScale;
            half _Translucency;
        CBUFFER_END

        float4 _TDWind;
        float4 _TDGust;

        float GrassHash(float2 p)
        {
            p = frac(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return frac(p.x * p.y);
        }

        float GrassNoise(float2 p)
        {
            float2 i = floor(p);
            float2 f = frac(p);
            f = f * f * (3.0 - 2.0 * f);
            return lerp(lerp(GrassHash(i), GrassHash(i + float2(1, 0)), f.x), lerp(GrassHash(i + float2(0, 1)), GrassHash(i + float2(1, 1)), f.x), f.y);
        }

        /// Where a blade vertex ends up: turned and scaled about the clump's root, then bent down the
        /// wind by weight² (roots stay put), with a gust that rolls across the map and a sway per clump.
        float3 PlaceVertex(float3 positionOS, float weight, GrassInstance instance, out float gustOut)
        {
            float s, c;
            sincos(instance.rotation, s, c);
            float3 local = float3(positionOS.x * c - positionOS.z * s, positionOS.y, positionOS.x * s + positionOS.z * c) * instance.scale;
            float3 world = instance.position + local;

            float2 direction = _TDWind.xy;
            float time = _Time.y;
            float2 gustUV = instance.position.xz / max(1.0, _TDGust.x) - direction * time * _TDGust.z / max(1.0, _TDGust.x);
            float gust = lerp(1.0, GrassNoise(gustUV) * 1.6, _TDGust.y);
            float phase = GrassHash(instance.position.xz * 0.37) * 6.2831853;
            float sway = 0.55 + 0.45 * sin(time * _TDWind.w * 1.3 + phase) + 0.2 * sin(time * _TDWind.w * 3.1 + phase * 1.7);
            // Short blades bend by their own height, not by metres: a 20 cm blade leans as far,
            // for its size, as a tree's twig does.
            float bend = weight * weight * _TDWind.z * _WindScale * gust * instance.scale * 0.9;
            world.xz += direction * bend * sway;
            world.y -= bend * 0.35 * saturate(sway);
            gustOut = gust;
            return world;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half3 colour : TEXCOORD2;
                half height : TEXCOORD3;
                half fog : TEXCOORD4;
            };

            Varyings Vert(Attributes input)
            {
                GrassInstance instance = _Instances[input.instanceID];
                float gust;
                float3 positionWS = PlaceVertex(input.positionOS, input.uv.y, instance, gust);

                Varyings output;
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                // Mostly up: turf lights like the ground it covers, with a little of the blade's own
                // facing so a clump is not one flat colour.
                float s, c;
                sincos(instance.rotation, s, c);
                float3 bladeNormal = float3(input.normalOS.x * c - input.normalOS.z * s, input.normalOS.y, input.normalOS.x * s + input.normalOS.z * c);
                output.normalWS = normalize(lerp(float3(0, 1, 0), bladeNormal, 0.35));
                // Gusts show: a blade bent over by a gust catches more light.
                output.colour = instance.colour * (1.0 + (gust - 1.0) * 0.12) * (0.94 + 0.12 * input.uv.x);
                output.height = input.uv.y;
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input, bool frontFace : SV_IsFrontFace) : SV_Target
            {
                // Dark at the root, where the blades shade each other, is what gives the carpet depth.
                half ramp = input.height * input.height;
                half3 albedo = input.colour * _Brightness * lerp(1.0 - _RootDarken, 1.0 + _TipLighten, ramp);
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light light = GetMainLight(shadowCoord);
                half3 normal = normalize(input.normalWS);
                half wrap = saturate((dot(normal, light.direction) + 0.35) / 1.35);
                half3 lit = albedo * light.color * (wrap * light.shadowAttenuation * light.distanceAttenuation);
                lit += albedo * light.color * _Translucency * 0.25 * light.shadowAttenuation * input.height;
                lit += albedo * SampleSH(normal);
                lit = MixFog(lit, input.fog);
                return half4(lit, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma target 4.5

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };

            float4 DepthVert(Attributes input) : SV_POSITION
            {
                GrassInstance instance = _Instances[input.instanceID];
                float gust;
                return TransformWorldToHClip(PlaceVertex(input.positionOS, input.uv.y, instance, gust));
            }

            half DepthFrag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
