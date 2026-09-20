// The sea and the rivers. Our own, deliberately: a bought water asset wants a depth texture, a
// spline package and a render feature, and all we need is a flat sheet that reads as water at RTS
// distance.
//
// Everything expensive is baked into the mesh instead of sampled at runtime:
// - The depth of water over each vertex comes from the terrain when the sheet is built, in UV1.x,
//   so the shallows are pale and the channel is dark with no scene-depth read at all.
// - Shoreline foam is the same number: a band where the water is only ankle deep.
// - Flow, for a river, is baked into UV1.y as distance along the channel, so the surface texture
//   scrolls downstream rather than in one world direction.
//
// The surface itself is two crossing sine ripples perturbing the normal, which at this distance
// does the job of a normal map without one.
Shader "TinyDiggers/Water"
{
    Properties
    {
        _Shallow ("Shallow", Color) = (0.42, 0.72, 0.72, 0.62)
        _Deep ("Deep", Color) = (0.06, 0.24, 0.36, 0.92)
        _DepthRange ("Metres to full depth colour", Range(0.5, 40)) = 9
        _FoamColor ("Foam", Color) = (0.92, 0.96, 0.96, 1)
        _FoamDepth ("Foam up to this depth (m)", Range(0, 4)) = 0.7
        _FoamWidth ("Foam softness", Range(0.01, 2)) = 0.45
        _RippleScale ("Ripple size (m)", Range(0.5, 20)) = 5
        _RippleStrength ("Ripple strength", Range(0, 1)) = 0.18
        _RippleSpeed ("Ripple speed", Range(0, 4)) = 0.7
        _FlowSpeed ("Flow speed (rivers)", Range(0, 4)) = 0.9
        _Smoothness ("Smoothness", Range(0, 1)) = 0.92
        _Specular ("Specular", Range(0, 1)) = 0.5
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

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Shallow;
                half4 _Deep;
                float _DepthRange;
                half4 _FoamColor;
                float _FoamDepth;
                float _FoamWidth;
                float _RippleScale;
                half _RippleStrength;
                float _RippleSpeed;
                float _FlowSpeed;
                half _Smoothness;
                half _Specular;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                // x: metres of water over this vertex. y: metres travelled down the channel.
                float2 water : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 water : TEXCOORD1;
                float fogFactor : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.water = input.water;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float depth = max(0.0, input.water.x);
                float along = input.water.y;

                // Two crossing ripples, carried downstream on a river and standing still at sea.
                float time = _Time.y * _RippleSpeed;
                float drift = along * 0.35 - _Time.y * _FlowSpeed;
                float2 p = input.positionWS.xz / max(0.5, _RippleScale);
                float waveA = sin(p.x * 2.1 + p.y * 1.3 + time + drift);
                float waveB = sin(p.x * -1.4 + p.y * 2.3 + time * 1.31 + drift * 1.7);
                float3 normalWS = normalize(float3(waveA * _RippleStrength, 1.0, waveB * _RippleStrength));

                half4 water = lerp(_Shallow, _Deep, saturate(depth / max(0.5, _DepthRange)));

                // Foam where the water runs out: a soft band along every shore and bank.
                float foam = 1.0 - smoothstep(_FoamDepth, _FoamDepth + _FoamWidth, depth);
                foam *= 0.5 + 0.5 * sin(along * 1.7 + input.positionWS.x * 0.6 + input.positionWS.z * 0.5 + _Time.y * 1.3);
                half3 albedo = lerp(water.rgb, _FoamColor.rgb, saturate(foam));
                half alpha = lerp(water.a, 1.0, saturate(foam * 0.8));

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.bakedGI = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.specular = _Specular.xxx;
                surfaceData.smoothness = _Smoothness;
                surfaceData.occlusion = 1;
                surfaceData.alpha = alpha;
                surfaceData.normalTS = half3(0, 0, 1);

                half4 color = UniversalFragmentBlinnPhong(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, input.fogFactor);
                color.a = alpha;
                return color;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
