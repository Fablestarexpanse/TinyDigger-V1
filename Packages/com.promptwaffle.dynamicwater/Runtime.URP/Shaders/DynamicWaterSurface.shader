// PromptWaffle Dynamic Water System: the surface of a simulated zone, for URP.
//
// Every vertex of the flat grid (WaterZoneRenderer) reads the zone's state texture: (surface
// height, depth, velocity x, velocity z) per cell. It is lifted to the surface. A dry vertex beside
// water is held at the lowest neighbouring water level, so the surface runs on flat past its edge and
// the ground cuts the shoreline; a dry vertex with no water beside it is sunk under the ground. The
// shore fade and shore foam read the real water depth from the scene depth texture, so the edge
// follows the ground's own contour rather than the simulation's cells. Needs URP's depth texture as
// well as its opaque texture. The fragment:
// - normal from the surface's slope, read from the state texture at the pixel;
// - colour absorbed with depth, over the refracted scene (URP opaque texture);
// - surf: bands of foam laid out by distance from the shore (WaterShoreDistance) that roll in,
//   widen and tear as they break, and come in sets, so a beach has waves washing onto it rather
//   than a still white rim; only on water open enough for waves to build, so rivers stay calm;
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
        _FoamSpeedCap ("Most foam from speed alone", Range(0, 1)) = 0.5
        _Surf ("Surf: breaking waves at the shore", Range(0, 1)) = 0.8
        _SurfReach ("Surf reaches this far out from the shore (m)", Range(1, 30)) = 9
        _SurfSpacing ("Metres between surf waves", Range(0.5, 10)) = 3
        _SurfSpeed ("Surf waves reaching the shore per second", Range(0, 1)) = 0.25
        _SurfWidth ("Surf foam trail, share of the gap between waves", Range(0.1, 0.9)) = 0.45
        _SurfFetch ("Open water needed out to sea for surf (m)", Range(2, 30)) = 14
        _CascadeDrop ("Water higher beside a vertex than this is a cascade (m)", Float) = 0.25
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

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
                half _FoamSpeedCap;
                half _Surf;
                float _SurfReach;
                float _SurfSpacing;
                float _SurfSpeed;
                half _SurfWidth;
                float _SurfFetch;
                float _CascadeDrop;
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
            float4 _WaterTexel;  // 1/width, 1/height, cell size, cells per vertex

            // Metres to the nearest dry ground (WaterShoreDistance), set per zone by WaterZoneRenderer.
            TEXTURE2D(_PWShoreDistance); SAMPLER(sampler_PWShoreDistance);
            float4 _PWShoreTexel;   // 1/width, 1/height, metres covered across, metres covered down
            float4 _PWShoreParams;  // reach (m), 1 when the field is there

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

            // Height a vertex in a cascade needs so the mesh edge to each wet neighbour clears the
            // ground half way along it. The mesh has one vertex every few cells; on a steep reach
            // the ground between two of them bulges over the straight edge joining them, and a
            // film a few centimetres thick went under it a whole quad at a time (2026-09-24).
            float CascadeClearance(float2 uv)
            {
                float2 step = _WaterTexel.xy * max(1.0, _WaterTexel.w);
                float need = -1e6;
                [unroll]
                for (int j = -1; j <= 1; j++)
                {
                    [unroll]
                    for (int i = -1; i <= 1; i++)
                    {
                        if (i == 0 && j == 0)
                            continue;
                        float2 d = float2(i * step.x, j * step.y);
                        float4 far = State(uv + d);
                        float4 mid = State(uv + d * 0.5);
                        if (far.x > -1e5 && far.y > _DryDepth && mid.x > -1e5)
                            need = max(need, 2.0 * (mid.x - mid.y) - far.x + 0.03);
                    }
                }
                return need;
            }

            // Whether water beside this point stands higher than the given height: water is coming
            // down over it, as on a riser or a steep tread, rather than lying level in a pool.
            float HighestWetNeighbour(float2 uv)
            {
                float2 step = _WaterTexel.xy * max(1.0, _WaterTexel.w);
                float highest = -1e6;
                [unroll]
                for (int j = -1; j <= 1; j++)
                {
                    [unroll]
                    for (int i = -1; i <= 1; i++)
                    {
                        float4 s = State(uv + float2(i * step.x, j * step.y));
                        if ((i != 0 || j != 0) && s.x > -1e5 && s.y > _DryDepth)
                            highest = max(highest, s.x);
                    }
                }
                return highest;
            }

            // Height for a dry vertex: the lowest wet surface one vertex step away, a hair under it,
            // so the water runs on flat past its edge and the ground cuts the shoreline. With no
            // water beside it the vertex is sunk under its own ground so nothing shows there.
            //
            // It used to sink every dry vertex. A triangle from a wet vertex down to a sunk one is
            // a slanted skirt, the fragment cut it where the interpolated wet flag crossed a half,
            // and what stood above the bank was paper-thin water the shore foam painted solid
            // white: a stepped white wall down every river bank, one vertex step to a tooth.
            //
            // Except where the water beside it stands higher than this vertex's own ground: then the
            // vertex is a riser inside a cascade, with water coming over it from above, and it is
            // draped with a film at its ground. Held at the lowest water it went under the ground,
            // and a steep reach, pooled on every tread and dry on every riser, came apart into a
            // checker of water and holes (2026-09-24). A bank has water only below it, so a bank
            // still gets the flat surface and the ground still cuts the shoreline.
            float DryStandIn(float2 uv, float ground, out float lifted)
            {
                float2 step = _WaterTexel.xy * max(1.0, _WaterTexel.w);
                float lowest = 1e6;
                float highest = -1e6;
                [unroll]
                for (int j = -1; j <= 1; j++)
                {
                    [unroll]
                    for (int i = -1; i <= 1; i++)
                    {
                        if (i == 0 && j == 0)
                            continue;
                        float4 s = State(uv + float2(i * step.x, j * step.y));
                        if (s.x > -1e5 && s.y > _DryDepth)
                        {
                            lowest = min(lowest, s.x);
                            highest = max(highest, s.x);
                        }
                    }
                }

                lifted = lowest < 1e5 ? 1.0 : 0.0;
                if (lifted < 0.5)
                    return ground - 0.15;
                return highest > ground + 0.02 ? max(ground + 0.02, CascadeClearance(uv)) : lowest - 0.02;
            }

            // Height for a vertex over a wall: the highest non-wall surface one vertex step away,
            // or the mesh's own height when it is walled in on every side.
            float WallStandIn(float2 uv, float fallback)
            {
                float2 step = _WaterTexel.xy * max(1.0, _WaterTexel.w);
                float best = -1e6;
                float4 s;
                s = State(uv + float2(step.x, 0)); if (s.x > -1e5) best = max(best, s.x);
                s = State(uv - float2(step.x, 0)); if (s.x > -1e5) best = max(best, s.x);
                s = State(uv + float2(0, step.y)); if (s.x > -1e5) best = max(best, s.x);
                s = State(uv - float2(0, step.y)); if (s.x > -1e5) best = max(best, s.x);
                return best > -1e5 ? best : fallback;
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
                // Dry: held at the water beside it, or sunk under the ground (DryStandIn). A wall
                // vertex (the void round the world) takes the highest open neighbour instead: sent
                // far down, the triangles along the rim stretched into curtains under the map.
                float surface = state.x;
                float lifted = 0.0;
                if (wall)
                    surface = WallStandIn(uv, input.positionOS.y) - 0.15;
                else if (wet < 0.5)
                    surface = DryStandIn(uv, state.x, lifted);
                else if (HighestWetNeighbour(uv) > state.x + _CascadeDrop)
                    surface = max(surface, CascadeClearance(uv));
                positionWS.y = surface;
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
                // A vertex held at the water beside it draws, as a wet one does; the ground in
                // front of it decides where the water stops.
                output.wet = max(wet, lifted);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            float FoamPattern(float2 p)
            {
                return ValueNoise(p) * 0.6 + ValueNoise(p * 2.7 + 13.0) * 0.4;
            }

            float ShoreDistance(float2 xz)
            {
                float2 uv = (xz - _WaterZone.xy) / _PWShoreTexel.zw;
                return SAMPLE_TEXTURE2D_LOD(_PWShoreDistance, sampler_PWShoreDistance, uv, 0).r;
            }

            /// How much surf breaks here, 0 to 1.
            float Surf(float2 xz)
            {
                float distance = ShoreDistance(xz);
                if (distance >= _SurfReach)
                    return 0;
                // Open water only: look out to sea, down the field's slope, by the fetch. Across a
                // river or a pond that lands on the far bank, which is near the shore again.
                float texel = _PWShoreTexel.z * _PWShoreTexel.x;
                float2 slope = float2(ShoreDistance(xz + float2(texel, 0)) - ShoreDistance(xz - float2(texel, 0)),
                                      ShoreDistance(xz + float2(0, texel)) - ShoreDistance(xz - float2(0, texel)));
                float2 seaward = slope / max(length(slope), 1e-4);
                float outThere = ShoreDistance(xz + seaward * _SurfFetch);
                float open = smoothstep(_SurfFetch * 0.45, _SurfFetch * 0.9, outThere);
                if (open <= 0.001)
                    return 0;

                float nearShore = 1.0 - distance / _SurfReach;
                // Three scales of wobble so a front never traces the shore it is heading for.
                float wobble = FoamPattern(xz * 0.03) * 1.1 + FoamPattern(xz * 0.075 + 5.0) * 0.8 + FoamPattern(xz * 0.2 + 17.0) * 0.12;
                float band = frac(distance / _SurfSpacing + _Time.y * _SurfSpeed + wobble);
                float width = lerp(_SurfWidth * 0.45, _SurfWidth, nearShore);
                float crestLine = smoothstep(0.0, 0.04, band) * (1.0 - smoothstep(0.06, 0.2, band));
                float trail = smoothstep(0.0, 0.04, band) * (1.0 - smoothstep(0.04, width, band));
                // Each wave breaks along some of its length at a time, and in sets.
                float wave = floor(distance / _SurfSpacing + _Time.y * _SurfSpeed + wobble);
                float along = smoothstep(0.38, 0.6, FoamPattern(xz * 0.09 + wave * 3.7));
                float sets = smoothstep(0.2, 0.7, FoamPattern(xz * 0.015 + _Time.y * 0.04));
                // Foam that tears into holes: the crest mostly whole, the trail a lace of patches.
                float torn = FoamPattern(xz * 0.9 + wave * 1.3) * 0.6 + FoamPattern(xz * 2.6 + 9.0) * 0.4;
                float crestFoam = crestLine * smoothstep(0.36, 0.48, torn);
                float trailFoam = sqrt(trail) * smoothstep(0.47, 0.58, torn) * 0.85;
                // Builds as it comes in: a swell line far out, breaking white near the beach.
                float build = smoothstep(0.1, 0.75, nearShore);
                return saturate(max(crestFoam, trailFoam) * build * lerp(0.08, 1.0, along) * (0.5 + 0.5 * sets) * open * 1.3);
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
                if (input.wet < 0.5)
                    discard;

                // How deep the water really is under this pixel, down to whatever the scene drew
                // behind it: the ray's length through the water, turned into a vertical depth. It
                // follows the ground's own contour, where the simulation's depth steps from cell to
                // cell. It is what fades the edge and foams the shore.
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float sceneEye = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float surfaceEye = -TransformWorldToView(input.positionWS).z;
                float3 ray = normalize(input.positionWS - _WorldSpaceCameraPos);
                float3 camForward = -UNITY_MATRIX_V[2].xyz;
                float throughWater = max(0.0, sceneEye - surfaceEye) / max(0.05, dot(ray, camForward));
                float realDepth = throughWater * saturate(-ray.y);
                // Out in the open the simulation's depth is the truer one; at the edge, the ground's.
                float edgeDepth = lerp(realDepth, depth, smoothstep(0.3, 0.8, depth));

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
                // Not turned into the flow's own frame: a world position hundreds of metres out,
                // dotted with a direction that changes from cell to cell, moved the pattern by metres
                // between neighbouring pixels and drew contour lines all over fast water (2026-09-24).
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

                float shore = 1.0 - saturate(edgeDepth / 0.35);
                // Speed alone never whites the water out: uncapped, a thin sheet racing down a steep
                // reach went solid white on every triangle (2026-09-24).
                // Nor do speed and shore stack: a thin fast sheet is all shore by depth, and the two
                // together whited it out again.
                float foamAmount = max(min(speed * _FoamFromSpeed, _FoamSpeedCap), shore * _ShoreFoam);
                // Whitecaps: crests above the threshold share of the local swell, in deep rough water.
                float crest = saturate((input.swell.x - _PWWaveParams.w) / max(0.05, 1.0 - _PWWaveParams.w));
                foamAmount = saturate(foamAmount + crest * input.swell.y * 0.9);
                // Surf, laid out by metres from the shore (WaterShoreDistance): a band of constant
                // distance + time moves inshore, so each wave rolls in and breaks on the beach. Laid
                // out by depth instead, every wave on a steep beach crowded into a strip a metre wide
                // and read as contour lines (2026-09-24). Sharp on its shoreward front, trailing off
                // and tearing into holes behind, wider as it breaks, bent by slow noise so a front does
                // not trace the shore, and coming in sets so the beach does not pulse in step. Its own
                // lace, not the flow foam's threshold, which kept only the peaks of a weak band.
                float surf = 0;
                if (_Surf > 0.001 && _PWShoreParams.y > 0.5)
                    surf = Surf(input.positionWS.xz) * _Surf;

                float foam = smoothstep(1.0 - foamAmount, 1.0 - foamAmount + 0.25, pattern) * foamAmount;
                foam = max(foam, surf);

                // Refraction: the scene under the water, bent by the normal, tinted by depth.
                float2 bent = screenUV + normal.xz * _Refraction * saturate(edgeDepth);
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

                // Foam is opaque however thin the water under it: a film draped over a riser shows
                // as foam over wet rock rather than vanishing.
                float alpha = saturate(max(edgeDepth / _EdgeFade, foam));
                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }
}
