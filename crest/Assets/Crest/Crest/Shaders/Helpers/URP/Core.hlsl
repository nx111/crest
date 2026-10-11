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
#define _LightColor0 (_MainLightColor.rgb * GetMainLight().distanceAttenuation)
#define _ScreenSize float4(_ScaledScreenParams.xy, rcp(_ScaledScreenParams.xy))

// World-space queries also run before URP's screen-space shadow texture exists.
// Its screen keyword hides the cascade keyword, so use the camera's actual count.
int _CrestMainLightShadowCascadeCount;
int _CrestRefractionPass;

float4 CrestWorldToMainLightShadowCoord(float3 positionWS)
{
    half cascadeIndex = 0;
#if defined(_MAIN_LIGHT_SHADOWS_CASCADE)
    cascadeIndex = ComputeCascadeIndex(positionWS);
#elif defined(_MAIN_LIGHT_SHADOWS_SCREEN)
    if (_CrestMainLightShadowCascadeCount > 1)
        cascadeIndex = ComputeCascadeIndex(positionWS);
#endif
    return float4(mul(_MainLightWorldToShadow[cascadeIndex], float4(positionWS, 1.0)).xyz, 0.0);
}

half CrestMainLightShadow(float3 positionWS, bool applyFade = true)
{
#if defined(MAIN_LIGHT_CALCULATE_SHADOWS)
#if defined(CREST_SAMPLE_SHADOW_HARD)
    // Crest creates softness by temporal world-space jitter. Keep its hard channel sharp even when URP PCF is on.
    float4 shadowCoord = CrestWorldToMainLightShadowCoord(positionWS);
    half shadow = LerpWhiteTo(SAMPLE_TEXTURE2D_SHADOW(_MainLightShadowmapTexture, sampler_LinearClampCompare, shadowCoord.xyz), GetMainLightShadowParams().x);
    shadow = BEYOND_SHADOW_FAR(shadowCoord) ? 1.0h : shadow;
#else
    half shadow = SampleShadowmap(TEXTURE2D_ARGS(_MainLightShadowmapTexture, sampler_LinearClampCompare),
        CrestWorldToMainLightShadowCoord(positionWS), GetMainLightShadowSamplingData(), GetMainLightShadowParams(), false);
#endif
    return applyFade ? lerp(shadow, 1.0h, GetMainLightShadowFade(positionWS)) : shadow;
#else
    return 1.0h;
#endif
}

float4 UnityObjectToClipPos(float3 positionOS) { return TransformObjectToHClip(positionOS); }
float4 UnityObjectToClipPos(float4 positionOS) { return TransformObjectToHClip(positionOS.xyz); }
float LinearEyeDepth(float depth) { return LinearEyeDepth(depth, _ZBufferParams); }
float Linear01Depth(float depth) { return Linear01Depth(depth, _ZBufferParams); }
#endif
