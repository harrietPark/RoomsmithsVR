// Soft storybook toon for the Windups crew (F31). Matte flat colour, a warm key light fixed relative to the
// viewer (over the user's left shoulder, so the face the user looks at is lit), a lavender shade instead of
// grey, a faint warm rim and a gentle bottom-to-top lift so each character reads lighter at the top.
// Unlit pass (no real-time lights, no shadows, no reflections): cheaper than URP Lit, single-pass-instanced
// stereo friendly. _BaseMap is the main texture so WindupBody's face-sheet offset keeps working.
// _EmissionColor (black by default, so the crew is unchanged) is added on top: the toy mic's light and icon
// glow with it while recording (2026-11-12, the mic uses this shader too).
// 2026-11-13 (the user: "give every object the characters' shader"): _VertexColor 1 multiplies the albedo by the
// mesh's vertex colour (named as in Toy Surface, so a Toy Surface material keeps its setting when switched here),
// read as sRGB (ToyMeshes colours are written as sRGB values) and turned linear per vertex; _VertexSRGB 0 takes it
// as it is, for meshes whose code already made it linear (the topic ring, the display stand);
// _TwoSided 1 draws both sides with the normal turned toward the viewer (thin toy props), set with _Cull 0.
// Both default to off, so the crew and the mic are unchanged. Resources/ToyCharacterProps.mat uses them, and since the
// same day the toy props' materials too (AgentScripts/ConfigureToyProps.cs All: ToyCard, ToyStickyNote, PostIt_Toy,
// ToyMicrophone, MakerMagnifierBrass/Grip), with the crew's light, shade and rim (copied from Body_Decisions).
Shader "Windups/Toy Character"
{
    Properties
    {
        [MainTexture] _BaseMap ("Base map", 2D) = "white" {}
        [MainColor] _BaseColor ("Base colour", Color) = (1, 1, 1, 1)
        _LightColor ("Light colour", Color) = (.98, .95, .9, 1)
        _ShadeColor ("Shade tint", Color) = (.7, .66, .9, 1)
        _LightDir ("Light (view space, toward light)", Vector) = (-.55, .75, .55, 0)
        _Softness ("Shade softness", Range(.01, .6)) = .2
        _RimColor ("Rim colour", Color) = (1, .88, .7, 1)
        _RimStrength ("Rim strength", Range(0, 1)) = .15
        _RimPower ("Rim power", Range(1, 8)) = 3
        _GradientHeight ("Gradient height (m, object scale)", Float) = .16
        _GradientAmount ("Gradient amount", Range(0, .5)) = .14
        [HDR] _EmissionColor ("Glow (added)", Color) = (0, 0, 0, 1)
        _VertexColor ("Use vertex colour", Range(0, 1)) = 0
        _VertexSRGB ("Vertex colour is sRGB", Range(0, 1)) = 1
        _TwoSided ("Normal toward the viewer", Range(0, 1)) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _LightColor;
            half4 _ShadeColor;
            float4 _LightDir;
            half _Softness;
            half4 _RimColor;
            half _RimStrength;
            half _RimPower;
            float _GradientHeight;
            half _GradientAmount;
            half4 _EmissionColor;
            half _VertexColor;
            half _VertexSRGB;
            half _TwoSided;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ToyCharacter"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull [_Cull]
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
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 viewDirWS : TEXCOORD2;
                half height : TEXCOORD3;
                half3 tint : TEXCOORD4;
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
                // Height above the object's origin (the character's feet), in units of its own scale.
                float4x4 m = GetObjectToWorldMatrix();
                float scale = max(1e-4, length(float3(m._m00, m._m10, m._m20)));
                output.height = (half)saturate((positionWS.y - m._m13) / (_GradientHeight * scale));
                // sRGB vertex colour to linear (Unity's fitted curve), only for props that ask for it.
                half3 c = input.color.rgb;
                half3 linearColour = c * (c * (c * 0.305306011 + 0.682171111) + 0.012522878);
                output.tint = lerp(half3(1, 1, 1), lerp(c, linearColour, _VertexSRGB), _VertexColor);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 normal = normalize(input.normalWS);
                float3 view = normalize(input.viewDirWS);
                // Two-sided props: the visible side faces the viewer (as Toy Surface does).
                normal = (_TwoSided > 0.5 && dot(normal, view) < 0) ? -normal : normal;
                float3 light = normalize(mul((float3x3)UNITY_MATRIX_I_V, normalize(_LightDir.xyz)));
                half lit = smoothstep(-_Softness, _Softness, dot(normal, light));
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb * input.tint;
                albedo *= lerp(1.0 - _GradientAmount, 1.0 + _GradientAmount * 0.6, input.height);
                half3 color = lerp(albedo * _ShadeColor.rgb, albedo * _LightColor.rgb, lit);
                half rim = pow(saturate(1.0 - dot(normal, view)), _RimPower) * _RimStrength;
                color += _RimColor.rgb * rim * (0.35 + 0.65 * lit);
                color += _EmissionColor.rgb;
                return half4(color, 1);
            }
            ENDHLSL
        }

        // Depth for passes that need a depth prepass or the camera depth texture.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull [_Cull]
            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half DepthFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
