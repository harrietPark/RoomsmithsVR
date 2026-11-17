// 2026-11-15 (the user's pick A of Docs/Design/Concepts/Windups_PartHighlight_2026-11-15): a coloured line round a made
// thing's part. The part's triangles are drawn again pushed out along their normals by _Width metres (world space) and
// seen from inside (front faces culled), so only a rim shows past the part's own surface. Unlit, one pass, no textures.
Shader "Windups/Part Outline"
{
    Properties
    {
        _BaseColor ("Line colour", Color) = (1, .45, .36, 1)
        _Width ("Width (m)", Float) = .0016
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry+10" }
        Cull Front
        ZWrite On
        ZTest LEqual
        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                float _Width;
            CBUFFER_END
            struct A { float4 p : POSITION; float3 n : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 p : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };
            V Vert(A i)
            {
                V o = (V)0;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 w = TransformObjectToWorld(i.p.xyz);
                float3 n = normalize(TransformObjectToWorldNormal(i.n));
                o.p = TransformWorldToHClip(w + n * _Width);
                return o;
            }
            half4 Frag(V i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return half4(_BaseColor.rgb, 1);
            }
            ENDHLSL
        }
    }
}
