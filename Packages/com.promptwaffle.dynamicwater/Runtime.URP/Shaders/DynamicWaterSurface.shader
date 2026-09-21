// PromptWaffle Dynamic Water System: the surface of a simulated zone, for URP.
//
// Every vertex of the flat grid (WaterZoneRenderer) reads the zone's state texture: (surface
// height, depth, velocity x, velocity z) per cell. It is lifted to the surface, and sunk under the
// ground where the cell is dry so nothing shows there. The fragment:
// - normal from the surface's slope, read from the state texture at the pixel;
// - colour absorbed with depth, over the refracted scene (URP opaque texture);
// - foam carried by the flow: two scrolling noise phases along the velocity, cross-faded, so the
//   foam moves with the water without stretching; more where the water is fast or shallow;
// - Fresnel reflection of the environment and a sun highlight;
// - the swell (WaterWaves, set per zone): a Gerstner sum on top of the simulated surface, dying
//   away in shallow water and varying in wind patches, with whitecaps at the crests. The same sum
//   as WaterWaves.Displacement, so floating objects ride what is drawn.
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
        _WindRipples ("Wind ripples everywhere", Range(0, 1)) = 0.35
        _WindRippleSize ("Wind ripple size (m)", Float) = 2.2
        _SunGlint ("Sun glint strength", Range(0, 8)) = 2.5
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
                half _WindRipples;
                float _WindRippleSize;
                half _SunGlint;
                half _Smoothness;
                float _DryDepth;
            CBUFFER_END

            // Set per zone by WaterZoneRenderer.
            TEXTURE2D(_WaterState); SAMPLER(sampler_WaterState);
            float4 _WaterZone;   // origin x, origin z, size x, size z (m)
            float4 _WaterTexel;  // 1/width, 1/height, cell size

            // The swell, set per zone by WaterZoneRenderer from WaterWaves.Pack.
            #define PW_MAX_WAVES 8
            float4 _PWWaveA[PW_MAX_WAVES];   // direction x, direction z, amplitude, wave number
            float4 _PWWaveB[PW_MAX_WAVES];   // steepness, speed, phase, 0
            int _PWWaveCount;
            float4 _PWWaveParams;            // gust size, gust calm, damp depth, whitecap threshold

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
                float2 baseXZ : TEXCOORD4;   // where the vertex was before the swell moved it
                float2 swell : TEXCOORD5;    // x: crest height as a share of the local swell, y: damping
            };

            float2 ZoneUV(float2 xz) { return (xz - _WaterZone.xy) / _WaterZone.zw; }

            float4 State(float2 uv) { return SAMPLE_TEXTURE2D_LOD(_WaterState, sampler_WaterState, uv, 0); }

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

            // Matches WaterWaves.Damping: none in the shallows, full in deep water in a rough patch.
            float SwellDamping(float depth, float2 xz)
            {
                float shallow = saturate(depth / _PWWaveParams.z);
                float gust = lerp(_PWWaveParams.y, 1.0, ValueNoise(xz / _PWWaveParams.x));
                return shallow * gust;
            }

            // Matches WaterWaves.Displacement. Also returns the slope (dh/dx, dh/dz) for the normal
            // and the total amplitude, for whitecaps.
            float3 Swell(float2 xz, float damping, out float2 slope, out float total)
            {
                float3 displacement = 0;
                slope = 0;
                total = 0;
                for (int i = 0; i < _PWWaveCount; i++)
                {
                    float4 a = _PWWaveA[i];
                    float4 b = _PWWaveB[i];
                    float amplitude = a.z * damping;
                    float f = a.w * (dot(a.xy, xz) - b.y * _Time.y) + b.z;
                    float s, c;
                    sincos(f, s, c);
                    displacement.xz += b.x * amplitude * a.xy * c;
                    displacement.y += amplitude * s;
                    slope += a.xy * a.w * amplitude * c;
                    total += amplitude;
                }
                return displacement;
            }

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
                output.baseXZ = positionWS.xz;
                output.swell = 0;
                if (wet > 0.5 && _PWWaveCount > 0)
                {
                    float damping = SwellDamping(state.y, positionWS.xz);
                    float2 slope;
                    float total;
                    float3 swell = Swell(positionWS.xz, damping, slope, total);
                    positionWS += swell;
                    output.swell = float2(swell.y / max(0.02, total), damping);
                }
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = uv;
                output.wet = wet;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            float FoamPattern(float2 p)
            {
                return ValueNoise(p) * 0.6 + ValueNoise(p * 2.7 + 13.0) * 0.4;
            }

            // Slope of a small ripple field at p (metres), drifting with the wind: finite
            // differences of two octaves of noise. It is what breaks the sun into glitter; without
            // it a smooth swell reflects the sun as a few soft blobs.
            float2 WindRippleSlope(float2 p, float2 wind)
            {
                float2 slope = 0;
                float scale = 1.0 / max(0.2, _WindRippleSize);
                float amplitude = 1.0;
                [unroll]
                for (int o = 0; o < 2; o++)
                {
                    float2 q = p * scale + wind * _Time.y * scale * (0.6 + o * 0.5) + o * 17.3;
                    float e = 0.15;
                    float n = ValueNoise(q);
                    slope += float2(ValueNoise(q + float2(e, 0)) - n, ValueNoise(q + float2(0, e)) - n) / e * amplitude;
                    scale *= 2.6;
                    amplitude *= 0.55;
                }
                return slope;
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
                float2 simSlope = float2(dx, dz) * _NormalStrength / (2.0 * cell);
                // The swell's slope, evaluated per pixel so the crests stay crisp between vertices.
                float2 waveSlope = 0;
                float waveTotal = 0;
                if (_PWWaveCount > 0)
                    Swell(input.baseXZ, input.swell.y, waveSlope, waveTotal);
                float3 normal = normalize(float3(-simSlope.x - waveSlope.x, 1.0, -simSlope.y - waveSlope.y));

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
                // Wind ripples on all water deep enough to take them, blowing with the first wave.
                float2 wind = _PWWaveCount > 0 ? _PWWaveA[0].xy * 0.8 : float2(0.6, 0.4);
                float2 windSlope = WindRippleSlope(input.positionWS.xz, wind) * _WindRipples * saturate(depth / 0.5) * 0.25;
                normal = normalize(normal - float3(windSlope.x, 0, windSlope.y));

                float shore = 1.0 - saturate(depth / 0.35);
                float foamAmount = saturate(speed * _FoamFromSpeed + shore * _ShoreFoam);
                // Whitecaps: crests above the threshold share of the local swell, in deep rough water.
                float crest = saturate((input.swell.x - _PWWaveParams.w) / max(0.05, 1.0 - _PWWaveParams.w));
                foamAmount = saturate(foamAmount + crest * input.swell.y * 0.9);
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
                // A tight glint, strongest at grazing angles as on real water.
                float specular = pow(saturate(dot(normal, halfway)), 900.0 * _Smoothness) * _SunGlint * shadow * (0.25 + fresnel * 3.0);
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
