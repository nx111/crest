// Crest Ocean System - MIT License (see LICENSE)
#ifndef CREST_URP_CORE_INCLUDED
#define CREST_URP_CORE_INCLUDED
#define CREST_URP 1
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// Compatibility names used by the shared Crest shading code. Do not include UnityCG here.
#define fixed half
#define fixed2 half2
#define fixed3 half3
#define fixed4 half4
#define UNITY_INITIALIZE_OUTPUT(type, name) name = (type)0;
#define UNITY_FOG_COORDS(index) float fogCoord : TEXCOORD##index;
#define UNITY_TRANSFER_FOG(output, position) output.fogCoord = ComputeFogFactor(position.z);
#define UNITY_APPLY_FOG(coord, color) color.rgb = MixFog(color.rgb, coord);
#define UNITY_DECLARE_SCREENSPACE_TEXTURE(name) TEXTURE2D_X(name); SAMPLER(sampler##name);
#define UNITY_SAMPLE_SCREENSPACE_TEXTURE(name, uv) SAMPLE_TEXTURE2D_X(name, sampler##name, UnityStereoTransformScreenSpaceTex(uv))
#define _LightColor0 _MainLightColor.rgb
#define unity_CameraToWorld UNITY_MATRIX_I_V
#define _ScreenSize float4(_ScaledScreenParams.xy, rcp(_ScaledScreenParams.xy))

float4 UnityObjectToClipPos(float3 positionOS) { return TransformObjectToHClip(positionOS); }
float4 UnityObjectToClipPos(float4 positionOS) { return TransformObjectToHClip(positionOS.xyz); }
float LinearEyeDepth(float depth) { return LinearEyeDepth(depth, _ZBufferParams); }
float Linear01Depth(float depth) { return Linear01Depth(depth, _ZBufferParams); }
#endif
