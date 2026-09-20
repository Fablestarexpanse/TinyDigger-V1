// A flat two-colour backdrop, so the map reads as a model on a table rather than a landscape
// under a sky. Unlit, no fog, drawn as the skybox.
Shader "TinyDiggers/Gradient Sky"
{
    Properties
    {
        _Top ("Top", Color) = (0.88, 0.87, 0.85, 1)
        _Bottom ("Bottom", Color) = (0.62, 0.61, 0.60, 1)
        _Horizon ("Horizon", Range(-1, 1)) = -0.15
        _Softness ("Softness", Range(0.01, 2)) = 0.9
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            Name "GradientSky"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Top;
                half4 _Bottom;
                half _Horizon;
                half _Softness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 directionWS : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.directionWS = input.positionOS.xyz;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 direction = normalize(input.directionWS);
                half t = saturate((direction.y - _Horizon) / max(_Softness, 0.01) * 0.5 + 0.5);
                t = t * t * (3.0 - 2.0 * t);
                return lerp(_Bottom, _Top, t);
            }
            ENDHLSL
        }
    }
}
