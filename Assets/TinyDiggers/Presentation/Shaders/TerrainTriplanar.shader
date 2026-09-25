// Terrain material detail, per TERRAIN_REFERENCE.md section 5 ("Later"): the mesh is unchanged,
// everything here is in the fragment shader.
//
// - What a fragment is made of is read from _CellMap, one texel per cell: red the material on
//   top, green the material a cut would expose. The four cells nearest the fragment are blended
//   over about half a cell, so a material edge is a soft line across the ground rather than the
//   edge of a triangle.
// - Albedo and normal come from texture arrays indexed by material, sampled triplanar from world
//   position, so cliffs and overhung cuts get the same detail as flat ground with no UVs.
// - Two scales: a fine detail normal (_DetailRepeat) over the albedo (_AlbedoRepeat), plus one
//   slow mottle (_MottleRepeat) that lifts and drops brightness so large flats do not read as
//   one flat colour.
// - Faces past _SteepStart degrees are a cut: they take the exposed material, its "cut" variant
//   where the set has one, and are darkened by _CutDarken.
// - A material can have two variant looks (lush and dry grass, 2026-09-24): the atlas gives their
//   slices in _MaterialParams z and w, and they are blended over the base in large patches drawn by
//   two noise fields _VariantScale metres across, the dry one favoured on high ground.
// - Hollows darken and cool toward _HollowColour, ridges lift a little, from _HollowMap: one byte a
//   cell of how far it lies below the ground round it (TerrainHollowMap; Ronan's references run deep
//   green in every fold, 2026-09-24).
// - The mesh's smooth normal is the base; the detail normal perturbs it.
// - Lighting is URP's own: main light, additional lights, shadows and ambient, with per-material
//   smoothness.
Shader "TinyDiggers/Terrain Triplanar"
{
    Properties
    {
        // Filled in by TerrainDetail: one texel per cell, and the material set as texture arrays.
        [NoScaleOffset] _CellMap ("Cell materials", 2D) = "black" {}
        [NoScaleOffset] _Albedos ("Material albedos", 2DArray) = "" {}
        [NoScaleOffset] _Normals ("Material normals", 2DArray) = "" {}
        [NoScaleOffset] _HollowMap ("Hollows (0.5 level)", 2D) = "grey" {}

        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _AlbedoRepeat ("Albedo repeat (m)", Range(0.1, 16)) = 0.5
        _DetailRepeat ("Detail normal repeat (m)", Range(0.05, 16)) = 0.25
        _DetailStrength ("Detail normal strength", Range(0, 2)) = 1
        _MacroRatio ("Anti-tiling: second scale (x repeat)", Range(1, 8)) = 3.3
        _MacroMix ("Anti-tiling: second scale share", Range(0, 1)) = 0.45
        _MottleRepeat ("Mottle repeat (m)", Range(2, 40)) = 11.3
        _MottleStrength ("Mottle strength", Range(0, 0.4)) = 0.1
        _BlendWidth ("Material blend width (cells)", Range(0.05, 4)) = 2
        _TriplanarSharpness ("Triplanar sharpness", Range(1, 16)) = 4
        _SteepStart ("Cut starts (degrees)", Range(0, 90)) = 40
        _SteepEnd ("Cut complete (degrees)", Range(0, 90)) = 55
        _CutDarken ("Cut face darkening", Range(0, 0.5)) = 0.15
        _Occlusion ("Slope shading", Range(0, 1)) = 0.25

        [Header(Variant looks)]
        _VariantScale ("Variant patch size (m)", Range(10, 500)) = 90
        _VariantAmount ("Variant amount", Range(0, 1)) = 1
        _VariantSoftness ("Variant edge softness", Range(0.02, 0.5)) = 0.14
        _DryHeight ("Dry from / to height (m)", Vector) = (15, 45, 0, 0)

        [Header(Hollows)]
        _HollowStrength ("Hollow strength", Range(0, 1)) = 0.8
        _HollowColour ("Hollow colour (multiplier)", Color) = (0.45, 0.62, 0.52, 1)
        _RidgeLift ("Ridge lift", Range(0, 0.4)) = 0.08

        [Header(Slope and contours)]
        _SlopeTint ("Slope tint", Range(0, 0.6)) = 0.2
        _SlopeColour ("Slope colour", Color) = (0.55, 0.6, 0.68, 1)
        _SlopeFullAt ("Slope full at (degrees)", Range(10, 80)) = 45
        _ContourStrength ("Contour strength", Range(0, 1)) = 0
        _MajorContour ("Major contour (m)", Range(1, 25)) = 5
        _MinorContour ("Minor contour (m)", Range(0.25, 10)) = 1
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
            float _AlbedoRepeat;
            float _DetailRepeat;
            half _DetailStrength;
            float _MacroRatio;
            half _MacroMix;
            float _MottleRepeat;
            half _MottleStrength;
            float _BlendWidth;
            half _TriplanarSharpness;
            half _SteepStart;
            half _SteepEnd;
            half _CutDarken;
            half _Occlusion;
            float _VariantScale;
            half _VariantAmount;
            half _VariantSoftness;
            float4 _DryHeight;
            half _HollowStrength;
            half4 _HollowColour;
            half _RidgeLift;
            half _SlopeTint;
            half4 _SlopeColour;
            half _SlopeFullAt;
            half _ContourStrength;
            float _MajorContour;
            float _MinorContour;
            float4 _MapSize;
            float4 _TerrainOrigin;
        CBUFFER_END

        TEXTURE2D(_CellMap);
        SAMPLER(sampler_CellMap);
        TEXTURE2D(_HollowMap);
        SAMPLER(sampler_HollowMap);
        TEXTURE2D_ARRAY(_Albedos);
        SAMPLER(sampler_Albedos);
        TEXTURE2D_ARRAY(_Normals);
        SAMPLER(sampler_Normals);

        // x: smoothness, y: slice to use on a cut face.
        float4 _MaterialParams[24];
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma require 2darray

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            // The same light keywords URP's own Lit shader compiles with. Our renderer is
            // Forward+ with light layers on; without these variants the sun never reached this
            // shader and the land was lit by the ambient alone.
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float fogFactor : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            // --- helpers ---------------------------------------------------------------------

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
                float a = Hash21(i);
                float b = Hash21(i + float2(1, 0));
                float c = Hash21(i + float2(0, 1));
                float d = Hash21(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            /// The three planes' texture coordinates and their screen-space derivatives.
            ///
            /// The derivatives are taken here, once, in uniform flow. The samples themselves happen
            /// inside a loop over the four nearest cells, which is branchy, and a texture read in
            /// branchy flow has no derivatives to pick a mip from: that is what turns distant ground
            /// into flat colour and makes near ground crawl with aliasing.
            struct TriplanarUV
            {
                float2 uvX, uvY, uvZ;
                float2 dxX, dyX, dxY, dyY, dxZ, dyZ;
            };

            TriplanarUV MakeTriplanarUV(float3 positionWS, float repeat)
            {
                TriplanarUV uv;
                uv.uvX = positionWS.zy / repeat;
                uv.uvY = positionWS.xz / repeat;
                uv.uvZ = positionWS.xy / repeat;
                uv.dxX = ddx(uv.uvX); uv.dyX = ddy(uv.uvX);
                uv.dxY = ddx(uv.uvY); uv.dyY = ddy(uv.uvY);
                uv.dxZ = ddx(uv.uvZ); uv.dyZ = ddy(uv.uvZ);
                return uv;
            }

            /// Triplanar sample of one slice of an array, at a mip chosen from the derivatives above.
            half4 SampleTriplanar(TEXTURE2D_ARRAY_PARAM(tex, samp), TriplanarUV uv, float3 weights, float slice)
            {
                half4 x = SAMPLE_TEXTURE2D_ARRAY_GRAD(tex, samp, uv.uvX, slice, uv.dxX, uv.dyX);
                half4 y = SAMPLE_TEXTURE2D_ARRAY_GRAD(tex, samp, uv.uvY, slice, uv.dxY, uv.dyY);
                half4 z = SAMPLE_TEXTURE2D_ARRAY_GRAD(tex, samp, uv.uvZ, slice, uv.dxZ, uv.dyZ);
                return x * weights.x + y * weights.y + z * weights.z;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 positionWS = input.positionWS;
                half3 geometryNormal = normalize(input.normalWS);

                // Which cells this fragment sits between. Cell (i, j) spans i..i+1, so the centre
                // of the cell is at +0.5, and the fragment blends toward whichever centres are
                // near it.
                // _TerrainOrigin.z is cells per metre (1 / cell size).
                float2 cell = (positionWS.xz - _TerrainOrigin.xy) * _TerrainOrigin.z;
                // The boundary is pushed about by a little noise before the blend is worked out,
                // so a material edge is a ragged line rather than a straight one between two cell
                // centres. Metres, not cells, so it does not scale with the blend width.
                cell += (float2(ValueNoise(positionWS.xz * 0.35), ValueNoise(positionWS.zx * 0.35 + 17.0)) - 0.5) * 1.6 * _TerrainOrigin.z;
                float2 centred = cell - 0.5;
                float2 baseCell = floor(centred);
                float2 f = centred - baseCell;

                // A soft transition _BlendWidth cells wide, centred on the halfway line, widened
                // when a cell is smaller than a pixel: from far away a half-cell blend is narrower
                // than the pixel it is drawn into, which is what put the sawtooth on material
                // edges. Widening it to the fragment's own footprint keeps the edge soft at every
                // distance without softening it up close.
                float2 footprint = fwidth(cell);
                float2 width = max(_BlendWidth, footprint * 1.5);
                float2 t = saturate((f - 0.5) / max(width, 0.01) + 0.5);
                t = t * t * (3.0 - 2.0 * t);
                float w00 = (1.0 - t.x) * (1.0 - t.y);
                float w10 = t.x * (1.0 - t.y);
                float w01 = (1.0 - t.x) * t.y;
                float w11 = t.x * t.y;

                float slope = degrees(acos(saturate(geometryNormal.y)));
                float cutAmount = saturate((slope - _SteepStart) / max(_SteepEnd - _SteepStart, 0.001));
                // Where the cut shows, blended over the whole slope band and pushed about by noise,
                // strongest mid-band and nothing at either end. It used to switch from the top
                // material to the cut one outright at the band's middle; the slope comes from
                // normals interpolated across triangles, so on a face not square to the grid that
                // middle runs triangle by triangle, and the hard switch drew it as a row of light
                // teeth along the foot of every dark face (2026-09-24).
                float cutNoise = (ValueNoise(positionWS.xz * 0.55) * 0.65 + ValueNoise(positionWS.xz * 2.1 + 7.0) * 0.35) - 0.5;
                float cutBlend = saturate(cutAmount + cutNoise * 1.2 * 4.0 * cutAmount * (1.0 - cutAmount));
                cutBlend = cutBlend * cutBlend * (3.0 - 2.0 * cutBlend);

                float3 axisWeights = pow(abs(geometryNormal), _TriplanarSharpness);
                axisWeights /= max(axisWeights.x + axisWeights.y + axisWeights.z, 0.0001);

                // How much of each variant look shows here: two slow noise fields, the dry one pushed
                // up on high ground and the lush one down there. The same for every cell under this
                // fragment, so the patches run across cell edges as one.
                float2 variantAt = positionWS.xz / _VariantScale;
                float lushField = ValueNoise(variantAt) * 0.6 + ValueNoise(variantAt * 0.35 + 31.0) * 0.4;
                float dryField = ValueNoise(variantAt + 57.0) * 0.6 + ValueNoise(variantAt * 0.35 + 93.0) * 0.4;
                float highGround = saturate((positionWS.y - _DryHeight.x) / max(_DryHeight.y - _DryHeight.x, 0.01));
                float dryWeight = smoothstep(0.5 - _VariantSoftness, 0.5 + _VariantSoftness, dryField + highGround * 0.35 - 0.08) * _VariantAmount;
                float lushWeight = smoothstep(0.5 - _VariantSoftness, 0.5 + _VariantSoftness, lushField - highGround * 0.3 - 0.04)
                    * (1.0 - dryWeight) * _VariantAmount;

                TriplanarUV albedoUV = MakeTriplanarUV(positionWS, _AlbedoRepeat);
                TriplanarUV detailUV = MakeTriplanarUV(positionWS, _DetailRepeat);
                // The same albedo again at a scale _MacroRatio times larger, offset so the two grids
                // never line up, and mixed in: a painted tile repeated every few metres reads as a
                // grid of the same blocks (the rock tiles did, 2026-09-24); two scales that do not
                // share a period break it up.
                TriplanarUV macroUV = MakeTriplanarUV(positionWS + float3(37.1, 11.3, 23.7), _AlbedoRepeat * _MacroRatio);

                // Stone and everything else are blended separately and then mixed along a smooth
                // line: the 0.5 contour of the blurred stone field (cell map blue), bent by noise.
                // Blending the four cells directly drew every grass-to-rock edge as the cell
                // staircase.
                half3 albedoSoil = 0, albedoStone = 0;
                half3 normalSoil = 0, normalStone = 0;
                half smoothSoil = 0, smoothStone = 0;
                float weightSoil = 0, weightStone = 0;
                float stoneField = 0;
                float weights[4] = { w00, w10, w01, w11 };
                float2 offsets[4] = { float2(0, 0), float2(1, 0), float2(0, 1), float2(1, 1) };
                // Plain bilinear weights for the field, so its contour is smooth, not stepped.
                float2 fs = f;
                float fieldWeights[4] = { (1 - fs.x) * (1 - fs.y), fs.x * (1 - fs.y), (1 - fs.x) * fs.y, fs.x * fs.y };

                [unroll]
                for (int i = 0; i < 4; i++)
                {
                    float2 lookup = (baseCell + offsets[i] + 0.5) * _MapSize.zw;
                    half4 ids = SAMPLE_TEXTURE2D_LOD(_CellMap, sampler_CellMap, lookup, 0);
                    stoneField += ids.b * fieldWeights[i];

                    float weight = weights[i] + 0.02;
                    float topSlice = floor(ids.r * 255.0 + 0.5);
                    float exposedSlice = floor(ids.g * 255.0 + 0.5);

                    // On a steep face the cut shows the layer underneath, in its cut variant
                    // where the set has one.
                    float cutSlice = _MaterialParams[(int)exposedSlice].y;

                    // The top material, the cut one, or a blend of the two across the band. Only
                    // the band samples both; the gradients are explicit, so the branch is safe.
                    float firstSlice = cutBlend >= 0.999 ? cutSlice : topSlice;
                    half4 albedoSample = SampleTriplanar(TEXTURE2D_ARRAY_ARGS(_Albedos, sampler_Albedos), albedoUV, axisWeights, firstSlice);
                    if (_MacroMix > 0.001)
                        albedoSample = lerp(albedoSample, SampleTriplanar(TEXTURE2D_ARRAY_ARGS(_Albedos, sampler_Albedos), macroUV, axisWeights, firstSlice), _MacroMix);
                    half4 normalSample = SampleTriplanar(TEXTURE2D_ARRAY_ARGS(_Normals, sampler_Normals), detailUV, axisWeights, firstSlice);
                    half smooth = _MaterialParams[(int)firstSlice].x;

                    // The top material's variant looks, where it has them.
                    float4 topParams = _MaterialParams[(int)topSlice];
                    if (firstSlice == topSlice && topParams.z > 0.5 && lushWeight > 0.01)
                    {
                        half4 lushAlbedo = SampleTriplanar(TEXTURE2D_ARRAY_ARGS(_Albedos, sampler_Albedos), albedoUV, axisWeights, topParams.z);
                        half4 lushNormal = SampleTriplanar(TEXTURE2D_ARRAY_ARGS(_Normals, sampler_Normals), detailUV, axisWeights, topParams.z);
                        albedoSample = lerp(albedoSample, lushAlbedo, lushWeight);
                        normalSample = lerp(normalSample, lushNormal, lushWeight);
                    }

                    if (firstSlice == topSlice && topParams.w > 0.5 && dryWeight > 0.01)
                    {
                        half4 dryAlbedo = SampleTriplanar(TEXTURE2D_ARRAY_ARGS(_Albedos, sampler_Albedos), albedoUV, axisWeights, topParams.w);
                        half4 dryNormal = SampleTriplanar(TEXTURE2D_ARRAY_ARGS(_Normals, sampler_Normals), detailUV, axisWeights, topParams.w);
                        albedoSample = lerp(albedoSample, dryAlbedo, dryWeight);
                        normalSample = lerp(normalSample, dryNormal, dryWeight);
                    }

                    if (cutBlend > 0.001 && cutBlend < 0.999 && cutSlice != topSlice)
                    {
                        half4 cutAlbedo = SampleTriplanar(TEXTURE2D_ARRAY_ARGS(_Albedos, sampler_Albedos), albedoUV, axisWeights, cutSlice);
                        half4 cutNormal = SampleTriplanar(TEXTURE2D_ARRAY_ARGS(_Normals, sampler_Normals), detailUV, axisWeights, cutSlice);
                        albedoSample = lerp(albedoSample, cutAlbedo, cutBlend);
                        normalSample = lerp(normalSample, cutNormal, cutBlend);
                        smooth = lerp(smooth, _MaterialParams[(int)cutSlice].x, cutBlend);
                    }
                    half3 n = normalSample.rgb * 2.0 - 1.0;

                    if (ids.a > 0.5)
                    {
                        albedoStone += albedoSample.rgb * weight;
                        normalStone += n * weight;
                        smoothStone += smooth * weight;
                        weightStone += weight;
                    }
                    else
                    {
                        albedoSoil += albedoSample.rgb * weight;
                        normalSoil += n * weight;
                        smoothSoil += smooth * weight;
                        weightSoil += weight;
                    }
                }

                // Where the edge falls: the smooth field, pushed about by two octaves of noise so
                // it wanders like a real outcrop edge, with a soft band either side.
                float edgeNoise = ValueNoise(positionWS.xz * 0.45) * 0.65 + ValueNoise(positionWS.xz * 1.7) * 0.35;
                float stoneAmount = smoothstep(0.38, 0.62, stoneField + (edgeNoise - 0.5) * 0.35);
                if (weightStone <= 0.0) stoneAmount = 0.0;
                if (weightSoil <= 0.0) stoneAmount = 1.0;
                albedoSoil /= max(weightSoil, 1e-4);
                normalSoil /= max(weightSoil, 1e-4);
                smoothSoil /= max(weightSoil, 1e-4);
                albedoStone /= max(weightStone, 1e-4);
                normalStone /= max(weightStone, 1e-4);
                smoothStone /= max(weightStone, 1e-4);

                half3 albedo = lerp(albedoSoil, albedoStone, stoneAmount);
                half3 normalTS = lerp(normalSoil, normalStone, stoneAmount);
                half smoothness = lerp(smoothSoil, smoothStone, stoneAmount);

                // One slow mottle so a big flat does not read as a single colour.
                float mottle = ValueNoise(positionWS.xz / _MottleRepeat) * 2.0 - 1.0;
                albedo *= 1.0 + mottle * _MottleStrength;
                albedo *= _Tint.rgb;

                // Hollows and ridges: the map is 0.5 on level ground, bilinear between cell centres.
                float2 hollowUV = (positionWS.xz - _TerrainOrigin.xy) * _TerrainOrigin.z * _MapSize.zw;
                half hollowValue = SAMPLE_TEXTURE2D_LOD(_HollowMap, sampler_HollowMap, hollowUV, 0).r;
                half hollow = saturate(hollowValue * 2.0 - 1.0);
                half ridge = saturate(1.0 - hollowValue * 2.0);
                albedo *= lerp(half3(1, 1, 1), _HollowColour.rgb, hollow * _HollowStrength);
                albedo *= 1.0 + ridge * _RidgeLift;
                albedo *= 1.0 - cutBlend * _CutDarken;

                // Slope tint: steep ground cools and darkens a little whatever the light is doing,
                // so a face reads as steep even on the shadowed side of a hill.
                float slopeAmount = saturate(slope / max(1.0, _SlopeFullAt));
                albedo = lerp(albedo, _SlopeColour.rgb * 0.85, slopeAmount * _SlopeTint);

                // Contours, drawn from world height rather than from geometry: a strong line every
                // _MajorContour metres and a faint one every _MinorContour.
                if (_ContourStrength > 0.001)
                {
                    float height = positionWS.y;
                    // fwidth keeps a line one pixel wide at every distance instead of aliasing
                    // into a moire when the camera pulls back.
                    float thickness = max(fwidth(height), 1e-4) * 1.2;
                    float major = abs(frac(height / _MajorContour + 0.5) - 0.5) * _MajorContour;
                    float minor = abs(frac(height / _MinorContour + 0.5) - 0.5) * _MinorContour;
                    float majorLine = 1.0 - smoothstep(0.0, thickness, major);
                    float minorLine = (1.0 - smoothstep(0.0, thickness, minor)) * 0.4;
                    // Not called "line": that is a reserved word in HLSL.
                    float contour = saturate(max(majorLine, minorLine)) * _ContourStrength;
                    albedo *= 1.0 - contour * 0.75;
                }

                // The detail normal bends the mesh's smooth normal rather than replacing it.
                float3 detail = normalize(float3(normalTS.x, normalTS.y, max(normalTS.z, 0.05)));
                float3 up = abs(geometryNormal.y) > 0.99 ? float3(1, 0, 0) : float3(0, 1, 0);
                float3 tangent = normalize(cross(up, geometryNormal));
                float3 bitangent = cross(geometryNormal, tangent);
                float3 perturbed = normalize(geometryNormal + (tangent * detail.x + bitangent * detail.y) * _DetailStrength);

                InputData inputData = (InputData)0;
                inputData.positionWS = positionWS;
                inputData.normalWS = perturbed;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.bakedGI = SampleSH(perturbed);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = saturate(albedo);
                surfaceData.metallic = 0;
                surfaceData.smoothness = smoothness;
                // Slopes sit a little in their own shade, which reads as depth on terraces.
                surfaceData.occlusion = lerp(1.0, saturate(geometryNormal.y * 0.5 + 0.5), _Occlusion);
                surfaceData.alpha = 1;
                surfaceData.normalTS = half3(0, 0, 1);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, input.fogFactor);
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            ShadowVaryings ShadowVert(ShadowAttributes input)
            {
                ShadowVaryings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                output.positionCS = positionCS;
                return output;
            }

            half4 ShadowFrag(ShadowVaryings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

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
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            DepthVaryings DepthVert(DepthAttributes input)
            {
                DepthVaryings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthFrag(DepthVaryings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
