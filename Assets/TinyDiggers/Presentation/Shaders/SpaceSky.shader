// Space behind the floating world (Ronan, 2026-09-21: "add a starfield space backdrop").
// Drawn as the skybox, unlit, all procedural from the view direction, so nothing tiles or seams:
// - three layers of stars, from a thin scatter of bright ones to a dust of faint ones, each with
//   its own colour temperature, the faint ones twinkling slowly;
// - a galaxy band on a tilted great circle: a soft glow, denser stars, dark dust lanes;
// - faint nebula clouds, strongest near the band.
// The ambient light does not come from this sky (LightingView sets trilight colours), so a dark
// sky does not darken the island.
Shader "TinyDiggers/Space Sky"
{
    Properties
    {
        _Base ("Deep space", Color) = (0.008, 0.01, 0.02, 1)
        _StarBrightness ("Star brightness", Range(0, 8)) = 2.2
        _StarDensity ("Star density", Range(0, 2)) = 1
        _Twinkle ("Twinkle", Range(0, 1)) = 0.35
        _BandNormal ("Galaxy band (plane normal)", Vector) = (0.35, 0.8, 0.48, 0)
        _BandWidth ("Galaxy band width", Range(0.02, 0.6)) = 0.22
        _BandColour ("Galaxy band colour", Color) = (0.55, 0.5, 0.62, 1)
        _BandBrightness ("Galaxy band brightness", Range(0, 1)) = 0.16
        _NebulaA ("Nebula colour A", Color) = (0.32, 0.14, 0.42, 1)
        _NebulaB ("Nebula colour B", Color) = (0.08, 0.3, 0.38, 1)
        _NebulaBrightness ("Nebula brightness", Range(0, 1)) = 0.12
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            Name "SpaceSky"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Base;
                half _StarBrightness;
                half _StarDensity;
                half _Twinkle;
                float4 _BandNormal;
                half _BandWidth;
                half4 _BandColour;
                half _BandBrightness;
                half4 _NebulaA;
                half4 _NebulaB;
                half _NebulaBrightness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 direction : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.direction = input.positionOS.xyz;
                return output;
            }

            float3 Hash33(float3 p)
            {
                p = frac(p * float3(0.1031, 0.1030, 0.0973));
                p += dot(p, p.yxz + 33.33);
                return frac((p.xxy + p.yxx) * p.zyx);
            }

            float Noise3(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float n000 = Hash33(i).x, n100 = Hash33(i + float3(1, 0, 0)).x;
                float n010 = Hash33(i + float3(0, 1, 0)).x, n110 = Hash33(i + float3(1, 1, 0)).x;
                float n001 = Hash33(i + float3(0, 0, 1)).x, n101 = Hash33(i + float3(1, 0, 1)).x;
                float n011 = Hash33(i + float3(0, 1, 1)).x, n111 = Hash33(i + float3(1, 1, 1)).x;
                return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                            lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
            }

            float Fbm(float3 p)
            {
                float sum = 0.0, amplitude = 0.5;
                for (int i = 0; i < 5; i++)
                {
                    sum += Noise3(p) * amplitude;
                    p = p * 2.03 + 17.1;
                    amplitude *= 0.5;
                }
                return sum;
            }

            // One layer of stars: a jittered point in each 3D cell the sky sphere passes through,
            // kept with probability `chance`, sized in cells, brightness skewed so most are faint.
            half3 Stars(float3 direction, float scale, float chance, float size, float twinkle)
            {
                float3 p = direction * scale;
                float3 cell = floor(p);
                float3 h = Hash33(cell);
                if (h.z > chance)
                    return 0;
                float3 centre = cell + 0.2 + h * 0.6;
                float distanceToStar = length(p - centre);
                // Keep a star at least about a pixel wide at any resolution, so it never flickers
                // in and out as the camera turns.
                float footprint = fwidth(p.x) + fwidth(p.y) + fwidth(p.z);
                float radius = max(size, footprint * 0.7);
                float core = saturate(1.0 - distanceToStar / radius);
                core *= core;
                float brightness = pow(h.x, 6.0) * 0.9 + 0.1;
                // Colour temperature: most white, some blue-white, some warm.
                half3 tint = lerp(half3(1.0, 0.82, 0.62), half3(0.72, 0.84, 1.0), h.y);
                tint = lerp(tint, half3(1, 1, 1), 0.45);
                float flicker = 1.0 - twinkle * (0.5 + 0.5 * sin(_Time.y * (1.3 + h.y * 2.7) + h.x * 40.0));
                return tint * core * brightness * flicker * (size / radius);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 direction = normalize(input.direction);
                half3 colour = _Base.rgb;

                // The galaxy band: distance from a tilted great circle.
                float3 bandNormal = normalize(_BandNormal.xyz);
                float offBand = dot(direction, bandNormal);
                float band = exp(-offBand * offBand / (_BandWidth * _BandWidth));
                float clouds = Fbm(direction * 4.0 + 3.0);
                float lanes = smoothstep(0.45, 0.62, Fbm(direction * 9.0 + 11.0));
                colour += _BandColour.rgb * band * _BandBrightness * (0.5 + clouds) * (1.0 - lanes * 0.75 * band);

                // Nebulae: two colours of slow cloud, mostly near the band.
                float nebula = saturate(Fbm(direction * 2.2 + 41.0) * 1.6 - 0.55);
                float mix = Fbm(direction * 1.7 + 7.0);
                colour += lerp(_NebulaA.rgb, _NebulaB.rgb, mix) * nebula * _NebulaBrightness * (0.35 + band);

                // Stars: bright and rare, medium, and a dust of faint ones thickest in the band.
                half3 stars = Stars(direction, 60.0, 0.08 * _StarDensity, 0.09, 0.0) * 3.0
                            + Stars(direction, 170.0, 0.22 * _StarDensity, 0.08, _Twinkle) * 1.2
                            + Stars(direction, 420.0, (0.18 + band * 0.5) * _StarDensity, 0.07, _Twinkle) * 0.7;
                colour += stars * _StarBrightness * (1.0 - lanes * band * 0.6);

                return half4(colour, 1);
            }
            ENDHLSL
        }
    }
}
