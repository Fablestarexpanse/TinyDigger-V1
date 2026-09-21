// PromptWaffle Dynamic Water System: the surface of a simulated zone, for URP.
//
// Every vertex of the flat grid (WaterZoneRenderer) reads the zone's state texture: (surface
// height, depth, velocity x, velocity z) per cell. It is lifted to the surface, and sunk under the
// ground where the cell is dry so nothing shows there. The fragment:
// - normal from the surface's slope, read from the state texture at the pixel;
// - colour absorbed with depth, over the refracted scene (URP opaque texture);
// - foam carried by the flow: two scrolling noise phases along the velocity, cross-faded, so the
//   foam moves with the water without stretching; more where the water is fast or shallow;
// - Fresnel reflection of the environment and a sun highlight.
Shader "PromptWaffle/Dynamic Water Surface"
{
    Properties
    {
        _Shallow ("Shallow colour", Color) = (0.25, 0.62, 0.62, 1)
        _Deep ("Deep colour", Color) = (0.04, 0.2, 0.32, 1)
        _Absorption ("Absorption per metre", Range(0.05, 4)) = 0.7
        _AbsorptionColour ("Absorption by colour (red goes first)", Vector) = (0.45, 0.09, 0.06, 0)
        _EdgeFade ("Metres of depth over which the shore edge fades in", Range(0.001, 0.5)) = 0.06
        _Refraction ("Refraction strength", Range(0, 0.2)) = 0.04
        _Foam ("Foam colour", Color) = (0.92, 0.96, 0.98, 1)
        _FoamFromSpeed ("Foam per m/s of flow", Range(0, 2)) = 0.35
        _ShoreFoam ("Foam in thin water at the shore", Range(0, 1)) = 0.45
        _FoamScale ("Foam pattern size (m)", Float) = 2.5
        _FlowPeriod ("Flow cycle (s)", Float) = 1.6
        _NormalStrength ("Normal strength", Range(0, 4)) = 1.4
        _RippleStrength ("Small ripples on moving water", Range(0, 1)) = 0.25
        _Smoothness ("Smoothness", Range(0, 1)) = 0.92
        _DryDepth ("Depth below which a cell counts as dry (m)", Float) = 0.002
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent-10"
            "RenderPipeline" = "UniversalPipeline"
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
            #pragma target 4.5

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Shallow;
                half4 _Deep;
                half _Absorption;
                float4 _AbsorptionColour;
                half _EdgeFade;
                half _Refraction;
                half4 _Foam;
                half _FoamFromSpeed;
                half _ShoreFoam;
                float _FoamScale;
                float _FlowPeriod;
                half _NormalStrength;
                half _RippleStrength;
                half _Smoothness;
                float _DryDepth;
            CBUFFER_END

            // Set per zone by WaterZoneRenderer.
            TEXTURE2D(_WaterState); SAMPLER(sampler_WaterState);
            float4 _WaterZone;   // origin x, origin z, size x, size z (m)
            float4 _WaterTexel;  // 1/width, 1/height, cell size

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                float wet : TEXCOORD2;
                half fogFactor : TEXCOORD3;
            };

            float2 ZoneUV(float2 xz) { return (xz - _WaterZone.xy) / _WaterZone.zw; }

            float4 State(float2 uv) { return SAMPLE_TEXTURE2D_LOD(_WaterState, sampler_WaterState, uv, 0); }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                // The grid's vertices are world positions already (WaterZoneRenderer): the object
                // matrix is ignored, so the zone's transform can never skew the water.
                float3 positionWS = input.positionOS.xyz;
                float2 uv = ZoneUV(positionWS.xz);
                float4 state = State(uv);
                bool wall = state.x < -1e5;
                float wet = (!wall && state.y > _DryDepth) ? 1.0 : 0.0;
                // Dry: sink just under the ground so the triangle folds out of sight; walls far down.
                positionWS.y = wall ? -1e4 : state.x - (1.0 - wet) * 0.15;
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = uv;
                output.wet = wet;
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
                return lerp(lerp(Hash21(i), Hash21(i + float2(1, 0)), f.x),
                            lerp(Hash21(i + float2(0, 1)), Hash21(i + float2(1, 1)), f.x), f.y);
            }

            float FoamPattern(float2 p)
            {
                return ValueNoise(p) * 0.6 + ValueNoise(p * 2.7 + 13.0) * 0.4;
            }

            // Surface height at a neighbouring texel, standing in for walls and dry ground with
            // this texel's own height so the shore does not tilt the normal into the ground.
            float Neighbour(float2 uv, float here)
            {
                float4 s = State(uv);
                return (s.x < -1e5 || s.y <= _DryDepth) ? here : s.x;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float4 state = State(input.uv);
                float depth = state.y;
                float2 velocity = state.zw;
                if (input.wet < 0.5 || depth <= _DryDepth)
                    discard;

                // Normal from the slope of the surface.
                float here = state.x;
                float2 t = _WaterTexel.xy;
                float cell = _WaterTexel.z;
                float dx = Neighbour(input.uv + float2(t.x, 0), here) - Neighbour(input.uv - float2(t.x, 0), here);
                float dz = Neighbour(input.uv + float2(0, t.y), here) - Neighbour(input.uv - float2(0, t.y), here);
                float3 normal = normalize(float3(-dx * _NormalStrength / (2.0 * cell), 1.0, -dz * _NormalStrength / (2.0 * cell)));

                // Flow: two phases half a cycle apart, each advecting the pattern for one cycle,
                // cross-faded so neither is seen stretching.
                float speed = length(velocity);
                float cycle = _Time.y / max(0.05, _FlowPeriod);
                float phaseA = frac(cycle);
                float phaseB = frac(cycle + 0.5);
                float weightB = abs(1.0 - 2.0 * phaseA);
                float2 p = input.positionWS.xz / _FoamScale;
                float2 drift = velocity * _FlowPeriod / _FoamScale;
                float patternA = FoamPattern(p - drift * phaseA);
                float patternB = FoamPattern(p - drift * phaseB + 5.3);
                float pattern = lerp(patternA, patternB, weightB);

                // Ripples on moving water, from the same flowing pattern.
                float rippleA = FoamPattern(p * 3.1 - drift * 3.1 * phaseA + 31.0);
                float rippleB = FoamPattern(p * 3.1 - drift * 3.1 * phaseB + 47.0);
                float ripple = lerp(rippleA, rippleB, weightB) - 0.5;
                float moving = saturate(speed * 0.8);
                normal = normalize(normal + float3(ripple, 0, -ripple) * _RippleStrength * moving);

                float shore = 1.0 - saturate(depth / 0.35);
                float foamAmount = saturate(speed * _FoamFromSpeed + shore * _ShoreFoam);
                float foam = smoothstep(1.0 - foamAmount, 1.0 - foamAmount + 0.25, pattern) * foamAmount;

                // Refraction: the scene under the water, bent by the normal, tinted by depth.
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float2 bent = screenUV + normal.xz * _Refraction * saturate(depth);
                half3 under = SampleSceneColor(bent);
                // Light through water loses red first, then green, then blue: the seabed seen
                // through it shifts toward teal and fades, and the water's own scattered colour
                // (shallow to deep) takes its place. Multiplying the seabed by a colour instead
                // turned sand under shallow water olive.
                float absorbed = 1.0 - exp(-depth * _Absorption);
                half3 transmitted = exp(-depth * _AbsorptionColour.rgb * (_Absorption / 0.7) * 2.0);
                half3 body = lerp(_Shallow.rgb, _Deep.rgb, absorbed);
                half3 colour = under * transmitted + body * (1.0 - transmitted);

                // Light: a little diffuse on the body, a sharp sun highlight, and the sky in the
                // grazing angles.
                float3 view = GetWorldSpaceNormalizeViewDir(input.positionWS);
                Light sun = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float shadow = sun.shadowAttenuation;
                colour *= lerp(0.55, 1.0, saturate(dot(normal, sun.direction)) * shadow);
                float fresnel = pow(1.0 - saturate(dot(normal, view)), 5.0) * 0.9 + 0.02;
                half3 sky = GlossyEnvironmentReflection(reflect(-view, normal), 1.0 - _Smoothness, 1.0);
                colour = lerp(colour, sky, fresnel);
                float3 halfway = normalize(sun.direction + view);
                float specular = pow(saturate(dot(normal, halfway)), 256.0 * _Smoothness) * 4.0 * shadow;
                colour += sun.color * specular * (1.0 - foam);

                colour = lerp(colour, _Foam.rgb * lerp(0.6, 1.0, shadow), foam);
                colour = MixFog(colour, input.fogFactor);

                float alpha = saturate(depth / _EdgeFade);
                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }
}
