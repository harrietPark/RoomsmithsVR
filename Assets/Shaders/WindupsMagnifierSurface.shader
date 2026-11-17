Shader "Windups/Magnifier Surface"
{
    Properties {
        _BaseColor ("Colour",Color)=(.7,.48,.18,1)
        _Specular ("Soft sheen",Range(0,1))=.5
        _Gloss ("Highlight width",Range(4,128))=48
        _Grain ("Fine surface grain",Range(0,.15))=.025
    }
    SubShader {
        Tags {"RenderPipeline"="UniversalPipeline" "RenderType"="Opaque"}
        Cull Off
        Pass {
            Tags {"LightMode"="SRPDefaultUnlit"}
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor; half _Specular,_Gloss,_Grain;
            CBUFFER_END
            struct A {float4 p:POSITION;float3 n:NORMAL;UNITY_VERTEX_INPUT_INSTANCE_ID};
            struct V {float4 p:SV_POSITION;float3 n:TEXCOORD0;float3 view:TEXCOORD1;float3 local:TEXCOORD2;UNITY_VERTEX_OUTPUT_STEREO};
            V Vert(A i) {
                V o=(V)0;UNITY_SETUP_INSTANCE_ID(i);UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 w=TransformObjectToWorld(i.p.xyz);o.p=TransformWorldToHClip(w);
                o.n=TransformObjectToWorldNormal(i.n);o.view=GetWorldSpaceViewDir(w);o.local=i.p.xyz;return o;
            }
            half4 Frag(V i):SV_Target {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 n=normalize(i.n),v=normalize(i.view);n=dot(n,v)<0?-n:n;
                float3 light=normalize(mul((float3x3)UNITY_MATRIX_I_V,float3(-.4,.65,.7)));
                half lit=saturate(dot(n,light)*.5+.5);
                half grain=sin(i.local.y*2500+sin(i.local.x*900)*2)*_Grain;
                half sheen=pow(saturate(dot(n,normalize(light+v))),_Gloss)*_Specular;
                half rim=pow(1-saturate(dot(n,v)),3)*.10;
                half3 colour=_BaseColor.rgb*(.60+.40*lit+grain)+half3(1,.94,.78)*(sheen+rim);
                return half4(colour,1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
