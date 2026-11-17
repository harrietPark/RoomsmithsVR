// F52 (2026-11-12): colours and textures the person picked from the real room, shown as they were seen.
// The user found the picked colours "dark and not the original": the swatches and the paint on made things
// used URP Lit (virtual lights, an occlusion map that darkened the grain, parallax), which dims a colour that
// already carries the room's real light. Here the side facing the viewer's fixed key light shows the colour
// exactly (albedo x 1), the far side only a little darker (_Shade), with an optional normal-map relief and a
// faint rim. Unlit pass (no shadows, no real-time lights): cheaper than URP Lit on Quest.
// F57 (2026-11-13): `_Triplanar` lays the texture in real size from three sides in the object's own space (paint
// on a made thing: no seams, no patches stretched along the model's texture layout; the relief is blended the
// same way, so it needs no tangents), `_Gloss` a soft highlight from the same key light (smooth paint, glaze,
// plastic), and `_VertexColour` multiplies the vertex colours in (a rebuilt solid carries its colours that way).
// All are uniform branches (no keywords to strip from the build).
// F70 (2026-11-14): with the local keyword _FILL (set only on a paint material's temporary copy) the copy is
// drawn over one part while paint floods it from the touched spot (`_FillMode` 1 = only within `_FillRadius` of
// `_FillCentre`, 2 = only beyond it: the eraser), with `_DepthOffset` -1 so it wins over the part it lies on.
// Without the keyword nothing changes (multi_compile_local: both variants are always in the build).
Shader "Windups/True Colour"
{
    Properties
    {
        [MainTexture] _BaseMap ("Base", 2D) = "white" {}
        [MainColor] _BaseColor ("Colour", Color) = (1, 1, 1, 1)
        [Normal] _BumpMap ("Relief (normal map)", 2D) = "bump" {}
        _BumpScale ("Relief strength", Range(0, 2)) = 0
        _LightDir ("Light (view space, toward light)", Vector) = (-.35, .65, .68, 0)
        _Shade ("Shade", Range(0, 1)) = .82
        _RimStrength ("Rim", Range(0, 1)) = .05
        [Toggle] _Triplanar ("Triplanar (real size)", Float) = 0
        _TriTile ("Triplanar tile size (m)", Float) = .06
        _Gloss ("Gloss", Range(0, 1)) = 0
        [Toggle] _VertexColour ("Vertex colours", Float) = 0
        _Glow ("Glow (light inside)", Color) = (0, 0, 0, 0)
        _FillMode ("Fill (0 off, 1 inside, 2 outside)", Float) = 0
        _FillCentre ("Fill centre (object axes, metres)", Vector) = (0, 0, 0, 0)
        _FillRadius ("Fill radius (m)", Float) = 0
        _DepthOffset ("Depth offset", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }
        Cull Off
        Offset [_DepthOffset], [_DepthOffset]
        Pass
        {
            Name "TrueColour"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_local __ _FILL
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _BumpScale;
                float4 _LightDir;
                half _Shade;
                half _RimStrength;
                half _Triplanar;
                float _TriTile;
                half _Gloss;
                half _VertexColour;
                half4 _Glow;
                half _FillMode;
                float4 _FillCentre;
                float _FillRadius;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 tangentWS : TEXCOORD2;
                float3 viewDirWS : TEXCOORD3;
                float3 triPos : TEXCOORD4;      // object position in metres of real size (object scale applied)
                float3 normalOS : TEXCOORD5;
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
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                // Not normalised here: a mesh without tangents gives zero, and then there is no relief.
                output.tangentWS = float4(TransformObjectToWorldDir(input.tangentOS.xyz, false), input.tangentOS.w);
                output.viewDirWS = GetWorldSpaceViewDir(positionWS);
                float3 scale = float3(length(GetObjectToWorldMatrix()._m00_m10_m20), length(GetObjectToWorldMatrix()._m01_m11_m21), length(GetObjectToWorldMatrix()._m02_m12_m22));
                output.triPos = input.positionOS.xyz * scale / max(_TriTile, 1e-4);
                output.normalOS = input.normalOS;
                output.color = input.color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                #ifdef _FILL
                    // The paint's spreading edge (F70): kept inside (1) or outside (2) the circle, with a thin
                    // lighter line where it is spreading. Only the fill's own copy has the keyword, so the
                    // discard never costs the ordinary draws their early depth test.
                    float inside = _FillRadius - distance(input.triPos * _TriTile, _FillCentre.xyz);
                    clip(_FillMode < 1.5 ? inside : -inside);
                    half front = saturate(1.0 - abs(inside) / 0.0025);
                #endif
                float3 view = normalize(input.viewDirWS);
                float3 normal = normalize(input.normalWS);
                float side = dot(normal, view) < 0 ? -1.0 : 1.0;
                normal *= side;
                half3 albedo;
                if (_Triplanar > 0.5)
                {
                    float3 n = normalize(input.normalOS);
                    float3 w = pow(abs(n), 4.0);
                    w /= max(w.x + w.y + w.z, 1e-5);
                    float2 uvX = input.triPos.zy, uvY = input.triPos.xz, uvZ = input.triPos.xy;
                    albedo = (SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvX).rgb * w.x
                            + SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvY).rgb * w.y
                            + SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvZ).rgb * w.z) * _BaseColor.rgb;
                    if (_BumpScale > 0.001)
                    {
                        // Whiteout blend of three tangent-space normals into object space (no tangents needed).
                        half3 tX = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvX), _BumpScale);
                        half3 tY = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvY), _BumpScale);
                        half3 tZ = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvZ), _BumpScale);
                        tX = half3(tX.xy + n.zy, abs(tX.z) * n.x);
                        tY = half3(tY.xy + n.xz, abs(tY.z) * n.y);
                        tZ = half3(tZ.xy + n.xy, abs(tZ.z) * n.z);
                        float3 bumped = normalize(tX.zyx * w.x + tY.xzy * w.y + tZ.xyz * w.z);
                        normal = normalize(TransformObjectToWorldNormal(bumped)) * side;
                    }
                }
                else
                {
                    albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb;
                    float tangentLength = length(input.tangentWS.xyz);
                    if (_BumpScale > 0.001 && tangentLength > 0.01)
                    {
                        float3 tangent = input.tangentWS.xyz / tangentLength;
                        tangent = normalize(tangent - normal * dot(normal, tangent));
                        float3 bitangent = cross(normal, tangent) * (input.tangentWS.w >= 0 ? 1.0 : -1.0) * side;
                        half3 bump = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                        normal = normalize(tangent * bump.x + bitangent * bump.y + normal * bump.z);
                    }
                }
                albedo *= lerp(half3(1, 1, 1), input.color.rgb, _VertexColour);
                float3 light = normalize(mul((float3x3)UNITY_MATRIX_I_V, normalize(_LightDir.xyz)));
                half wrap = dot(normal, light) * 0.5 + 0.5;
                half lit = smoothstep(0.25, 0.65, wrap);
                half3 color = albedo * lerp(_Shade, 1.0, lit);
                half rim = pow(1.0 - saturate(dot(normal, view)), 3.0) * _RimStrength;
                color += rim * lit;
                if (_Gloss > 0.001)
                {
                    float3 halfway = normalize(light + view);
                    half spec = pow(saturate(dot(normal, halfway)), lerp(10.0, 70.0, _Gloss));
                    color += spec * _Gloss * 0.45;
                }
                // A light inside (F59 lamp): the thin shell glows, more where it faces away from the viewer's side.
                color += _Glow.rgb * (0.55 + 0.45 * (1.0 - saturate(dot(normal, view))));
                #ifdef _FILL
                    color = lerp(color, color * 0.85 + 0.15, front * 0.6);
                #endif
                return half4(color, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
