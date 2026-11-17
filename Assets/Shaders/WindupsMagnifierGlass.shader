// A small transparent convex lens: no opaque-texture copy, refraction pass or extra camera.
Shader "Windups/Magnifier Glass"
{
    Properties {_BaseColor ("Glass tint",Color)=(.48,.85,.83,.16)}
    SubShader {
        Tags {"RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent"}
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back
        Pass {
            Tags {"LightMode"="SRPDefaultUnlit"}
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END
            struct A {float4 p:POSITION;float3 n:NORMAL;float2 uv:TEXCOORD0;UNITY_VERTEX_INPUT_INSTANCE_ID};
            struct V {float4 p:SV_POSITION;float3 n:TEXCOORD0;float3 view:TEXCOORD1;float2 uv:TEXCOORD2;UNITY_VERTEX_OUTPUT_STEREO};
            V Vert(A i){
                V o=(V)0;UNITY_SETUP_INSTANCE_ID(i);UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 w=TransformObjectToWorld(i.p.xyz);o.p=TransformWorldToHClip(w);
                o.n=TransformObjectToWorldNormal(i.n);o.view=GetWorldSpaceViewDir(w);o.uv=i.uv;return o;
            }
            half4 Frag(V i):SV_Target {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 p=i.uv*2-1;float r=length(p);
                half edge=smoothstep(.78,1,r);
                half rim=pow(1-abs(dot(normalize(i.n),normalize(i.view))),3);
                half arc=(1-smoothstep(.025,.08,abs(r-.78)))*smoothstep(.10,.45,p.y-p.x);
                half streak=(1-smoothstep(.015,.07,abs(p.x+p.y*.5+.28)))*(1-smoothstep(.65,.82,r))*.35;
                half glint=saturate(arc*.72+streak);
                return half4(lerp(_BaseColor.rgb,half3(.96,1,1),glint),saturate(_BaseColor.a+edge*.2+rim*.3+glint*.55));
            }
            ENDHLSL
        }
    }
    Fallback Off
}
