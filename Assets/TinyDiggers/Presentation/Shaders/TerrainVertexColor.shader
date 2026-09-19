// Flat low-poly terrain lit by the main light's N.L plus ambient from spherical harmonics. No
// textures, no shadows — placeholder lighting for the terrain slice, not final art.
//
// Colour: the vertex colour is the top material, and UV1 carries the colour of the layer a
// steep face cuts through. Faces steeper than about 45 degrees blend from the first to the
// second, so a cut shows what is under the topsoil. A cheap stand-in for the triplanar blend a
// textured version would do.
Shader "TinyDiggers/Terrain Vertex Color"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _SteepStart ("Steep blend starts (degrees)", Range(0, 90)) = 40
        _SteepEnd ("Steep blend complete (degrees)", Range(0, 90)) = 50
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
                half4 exposed : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3 normalWS : TEXCOORD0;
                half3 color : TEXCOORD1;
                half3 exposed : TEXCOORD2;
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

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.color = ToWorkingSpace(input.color.rgb) * _Tint.rgb;
                output.exposed = ToWorkingSpace(input.exposed.rgb) * _Tint.rgb;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half3 normalWS = normalize(input.normalWS);

                // normal.y is the cosine of the slope angle: 1 flat, 0 vertical.
                half steepness = 1.0h - smoothstep(cos(radians(_SteepEnd)), cos(radians(_SteepStart)), normalWS.y);
                half3 albedo = lerp(input.color, input.exposed, steepness);

                Light mainLight = GetMainLight();
                half diffuse = saturate(dot(normalWS, mainLight.direction));
                half3 lighting = mainLight.color * diffuse + SampleSH(normalWS);
                return half4(albedo * lighting, 1);
            }
            ENDHLSL
        }

        // Lets the terrain appear in the depth texture, which URP's depth-based effects read.
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
