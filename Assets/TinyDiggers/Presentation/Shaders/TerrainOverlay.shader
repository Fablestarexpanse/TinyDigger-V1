// Debug and designation overlays drawn over the terrain: unlit, vertex-coloured, alpha-blended,
// with a small depth bias so tiles lying on the surface do not z-fight with it.
Shader "TinyDiggers/Terrain Overlay"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)

        // Depth test, as a number so a material can choose. 4 is LEqual, which is what a tile
        // lying on the ground wants. 8 is Always, for the road ghost: a road cut through a hill
        // is *inside* the hill, and a ghost you cannot see is no use for deciding how deep to cut.
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Depth test", Float) = 4
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
            Name "Overlay"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest [_ZTest]
            Cull Off
            Offset -1, -1

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                half4 color = input.color * _Tint;
                #if !defined(UNITY_COLORSPACE_GAMMA)
                color.rgb = SRGBToLinear(color.rgb);
                #endif
                output.color = color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return input.color;
            }
            ENDHLSL
        }
    }
}
