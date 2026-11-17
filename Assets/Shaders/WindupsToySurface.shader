// Matte "toy diorama" surface for Windups UI objects (speech cards, sticky note, microphone).
// Soft two-tone toon shading from a light fixed relative to the viewer, so faces that point
// at the user always read bright while edges and sides fall into a calm tinted shade.
// Unlit pass: no shadows, no real-time light cost, single-pass-instanced stereo friendly.
Shader "Windups/Toy Surface"
{
    Properties
    {
        _BaseColor ("Base", Color) = (1, 1, 1, 1)
        _ShadeColor ("Shade", Color) = (.84, .85, .91, 1)
        _LightDir ("Light (view space, toward light)", Vector) = (-.35, .65, .68, 0)
        _ShadeStep ("Shade step", Range(0, 1)) = .42
        _ShadeSoftness ("Shade softness", Range(.01, .5)) = .2
        _RimColor ("Rim", Color) = (1, 1, 1, 1)
        _RimStrength ("Rim strength", Range(0, 1)) = .12
        _RimPower ("Rim power", Range(1, 8)) = 3
        _VertexColor ("Use vertex colour", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }
        Cull Off
        Pass
        {
            Name "ToySurface"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _ShadeColor;
                float4 _LightDir;
                half _ShadeStep;
                half _ShadeSoftness;
                half4 _RimColor;
                half _RimStrength;
                half _RimPower;
                half _VertexColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewDirWS : TEXCOORD1;
                half4 color : COLOR;
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
                output.color = input.color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 normal = normalize(input.normalWS);
                float3 view = normalize(input.viewDirWS);
                // Visible faces of these closed/thin shapes face the viewer. Orienting by view
                // instead of VFACE stays correct when URP renders through a flipped target.
                normal = dot(normal, view) < 0 ? -normal : normal;
                // A soft key light that stays fixed relative to the viewer.
                float3 light = normalize(mul((float3x3)UNITY_MATRIX_I_V, normalize(_LightDir.xyz)));
                half wrap = dot(normal, light) * 0.5 + 0.5;
                half lit = smoothstep(_ShadeStep - _ShadeSoftness, _ShadeStep + _ShadeSoftness, wrap);
                // Imported models without vertex colours set _VertexColor to 0.
                half3 albedo = _BaseColor.rgb * lerp(half3(1, 1, 1), input.color.rgb, _VertexColor);
                half3 shade = albedo * _ShadeColor.rgb;
                half3 color = lerp(shade, albedo, lit);
                half rim = pow(1.0 - saturate(dot(normal, view)), _RimPower) * _RimStrength;
                color = lerp(color, _RimColor.rgb, rim * lit);
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
