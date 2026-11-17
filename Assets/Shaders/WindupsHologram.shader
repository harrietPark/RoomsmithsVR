// F40 (2026-11-12): the see-through twin of a made thing (the v2 draft) and Fixer's print-check marks.
// Unlit and transparent: a faint fill of the tint with a little of the made thing's own texture, a bright
// rim where the surface turns away from the eye, and thin scan lines drifting up. A depth pass first
// (SRPDefaultUnlit, no colour) so only the front surface draws: no inside faces show through. The alpha is
// blended so it adds up correctly over passthrough (the eye buffer's alpha decides what is virtual).
Shader "Windups/Hologram"
{
    Properties
    {
        [MainColor] _BaseColor ("Tint", Color) = (.42, .74, 1, 1)
        [MainTexture] _BaseMap ("Texture (faint)", 2D) = "white" {}
        _Fill ("Fill alpha", Range(0, 1)) = .16
        _TexAmount ("Texture amount", Range(0, 1)) = .3
        _Rim ("Rim strength", Range(0, 3)) = 1.2
        _RimPower ("Rim power", Range(.5, 8)) = 2.4
        _Lines ("Scan lines per metre", Float) = 240
        _LineAlpha ("Scan line alpha", Range(0, 1)) = .2
        _Speed ("Scan speed (m/s)", Float) = .03
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Fill;
            half _TexAmount;
            half _Rim;
            half _RimPower;
            float _Lines;
            half _LineAlpha;
            float _Speed;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "HologramDepth"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            ZWrite On
            ColorMask 0
            Cull Back
            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings DepthVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "Hologram"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite Off
            ZTest LEqual
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 viewDirWS : TEXCOORD2;
                float heightWS : TEXCOORD3;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.viewDirWS = GetWorldSpaceViewDir(positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.heightWS = positionWS.y;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 normal = normalize(input.normalWS);
                float3 view = normalize(input.viewDirWS);
                half rim = pow(saturate(1.0 - abs(dot(normal, view))), _RimPower) * _Rim;
                half3 texel = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb;
                half3 color = lerp(_BaseColor.rgb, _BaseColor.rgb * (0.55 + 0.9 * texel), _TexAmount);
                float phase = frac(input.heightWS * _Lines - _Time.y * _Speed * _Lines);
                half line_ = smoothstep(0.0, 0.18, phase) * (1.0 - smoothstep(0.32, 0.5, phase)) * _LineAlpha;
                half alpha = saturate(_Fill + rim * 0.6 + line_) * _BaseColor.a;
                color += rim * 0.45;
                return half4(saturate(color), alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
