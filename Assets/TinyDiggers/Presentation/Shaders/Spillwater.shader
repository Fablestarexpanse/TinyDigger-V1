// The water the spillways pour into the void (the disc floats in space; the dam holds its sea).
//
// The sheet (Art/Tools/td_dam.py) runs out under each gate, down the stepped chute and off the
// lip, then falls as a curtain. DamView gives each vertex uv0 = (metres along the ring, height,
// metres out from the inner face, piece tone); the kit gives vertex colour red = how far into the
// sheet from its edge (0 at the edges, 1 in the middle) and alpha = the fade below the footing.
//
// - Downstream is "out minus height": it grows both down the chute and down the fall, so one
//   coordinate scrolls the whole sheet.
// - On the chute: fast water, torn white by the steps, with streaks running with the flow.
// - Off the lip: the curtain speeds up as it falls, wobbles, whitens, and tears into strands and
//   spray the further it drops, until it is gone.
Shader "TinyDiggers/Spillwater"
{
    Properties
    {
        _Deep ("Water colour", Color) = (0.16, 0.34, 0.45, 1)
        _Foam ("Foam colour", Color) = (0.92, 0.96, 1.0, 1)
        _Speed ("Flow speed down the chute (m/s)", Float) = 7
        _Lip ("Metres out from the inner face where the chute ends", Float) = 24.5
        _LipHeight ("Height of the lip (m)", Float) = -16.5
        _Break ("How soon the curtain tears apart", Range(0, 2)) = 1
        _Wobble ("Curtain wobble (m)", Float) = 0.35
        _Glow ("Foam self-lighting, so it reads against space", Range(0, 1)) = 0.08
        _StepPitch ("Metres between the chute's step noses", Float) = 2
        _FirstStep ("Metres out of the first step nose", Float) = 2.5
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
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Deep;
                half4 _Foam;
                float _Speed;
                float _Lip;
                float _LipHeight;
                half _Break;
                float _Wobble;
                half _Glow;
                float _StepPitch;
                float _FirstStep;
            CBUFFER_END

            // Set by DamView: the middle of the disc, in world space.
            float4 _DamCentre;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 colour : COLOR;
                float4 wall : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 wall : TEXCOORD2;
                float4 colour : TEXCOORD3;
                half fogFactor : TEXCOORD4;
            };

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

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);

                // The curtain wobbles outward and back, more the further it has fallen.
                float fall = max(0.0, _LipHeight - input.wall.y);
                float curtain = saturate((input.wall.z - _Lip) / 2.0);
                float2 radial = positionWS.xz - _DamCentre.xz;
                radial /= max(length(radial), 1e-3);
                float sway = sin(_Time.y * 2.3 + input.wall.x * 0.45 + input.wall.y * 0.35)
                           + 0.5 * sin(_Time.y * 3.7 - input.wall.x * 0.9);
                positionWS.xz += radial * sway * _Wobble * curtain * saturate(fall / 6.0);

                output.positionWS = positionWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.wall = input.wall;
                output.colour = input.colour;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input, bool frontFace : SV_IsFrontFace) : SV_Target
            {
                float along = input.wall.x;
                float height = input.wall.y;
                float outward = input.wall.z;
                float downstream = outward - height;
                float fall = max(0.0, _LipHeight - height);
                float curtain = saturate((outward - _Lip) / 2.0);

                // Faster as it falls: free fall on top of the chute speed.
                float speed = _Speed + sqrt(2.0 * 9.8 * fall) * 0.6;
                float t = _Time.y;
                float flow = downstream - t * speed;

                // Streaks running with the flow: long along it, narrow across it. Sharpened, so
                // they read as ropes of white on blue water rather than as a white wash.
                float streaks = ValueNoise(float2(along * 2.2, flow * 0.12)) * 0.6
                              + ValueNoise(float2(along * 6.0 + 3.0, flow * 0.3)) * 0.4;
                streaks = smoothstep(0.45, 0.8, streaks);
                // Each step nose throws the water up white just below it, then it settles.
                float sinceNose = frac((outward - _FirstStep) / _StepPitch);
                float nose = exp(-sinceNose * 5.0) * (0.6 + 0.4 * ValueNoise(float2(along * 3.0, flow * 0.8)));
                float chuteFoam = 0.12 + streaks * 0.45 + nose * 0.55;
                // The curtain leaves the lip glassy and whitens as it falls and aerates.
                float curtainFoam = 0.2 + saturate(fall / 12.0) * 0.55 + streaks * 0.3;
                float foam = lerp(chuteFoam, curtainFoam, curtain);

                // Edges: the sheet thins toward its sides, raggedly.
                float edge = input.colour.r;
                float ragged = ValueNoise(float2(flow * 0.3, along * 0.2)) * 0.25;
                half alpha = smoothstep(0.0, 0.3 + ragged, edge);

                // Tearing: past the lip the curtain breaks into strands, then spray, then nothing.
                float tear = saturate(fall / 30.0) * _Break;
                float strands = ValueNoise(float2(along * 1.3, flow * 0.25 + 11.0)) * 0.7
                              + ValueNoise(float2(along * 5.0, flow * 1.1)) * 0.3;
                alpha *= lerp(1.0, smoothstep(tear * 0.9 - 0.1, tear * 0.9 + 0.12, strands), curtain);
                alpha *= lerp(0.88, 0.8, curtain) * input.colour.a;

                half3 normal = normalize(input.normalWS) * (frontFace ? 1.0 : -1.0);
                Light mainLight = GetMainLight();
                half diffuse = saturate(dot(normal, mainLight.direction)) * 0.5 + 0.35;
                half3 ambient = SampleSH(normal);
                half3 base = lerp(_Deep.rgb, _Foam.rgb, saturate(foam));
                half3 colour = base * (mainLight.color * diffuse * 0.8 + ambient) + _Foam.rgb * foam * _Glow;

                colour = MixFog(colour, input.fogFactor);
                return half4(colour, saturate(alpha));
            }
            ENDHLSL
        }
    }
}
