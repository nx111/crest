// Crest Ocean System - MIT License (see LICENSE)
#ifndef CREST_OCEAN_ADDITIONAL_LIGHTS_INCLUDED
#define CREST_OCEAN_ADDITIONAL_LIGHTS_INCLUDED

#if _FOAM_ON && _FOAM3DLIGHTING_ON
half3 CrestFoamAdditionalLight(half3 normalWS, half3 view, half3 lightDirection, half3 radiance, float normalVariance)
{
    half ndl = saturate(dot(normalWS, lightDirection));
    half3 colour = _FoamWhiteColor.rgb * _WaveFoamLightScale * radiance * ndl;
    colour += CrestFilteredLightHighlight(dot(reflect(-view, normalWS), lightDirection), _WaveFoamSpecularFallOff, normalVariance)
        * _WaveFoamSpecularBoost * radiance * ndl;
    return colour;
}
#endif

void CrestAccumulateAdditionalLight(Light light, half3 normalWS, half3 view,
    half3 scatterTint, half sss, bool underwater, float normalVariance,
    CrestFoamLightingData foamLighting,
    inout half3 scatter, inout half3 specular, inout half3 foam)
{
#if defined(_LIGHT_LAYERS)
    if (!IsMatchingLightLayer(light.layerMask, GetMeshRenderingLayer())) return;
#endif
    half3 radiance = light.color * (light.distanceAttenuation * light.shadowAttenuation * _AdditionalLightIntensity);
    half ndl = saturate(dot(normalWS, light.direction));
    scatter += scatterTint * radiance * ndl;
#if _SUBSURFACESCATTERING_ON
    half towardsLight = pow(saturate(dot(light.direction, -view)), _SubSurfaceSunFallOff);
    half scatteringWeight = underwater ? 1.0h : (1.0h - view.y * view.y) * sss;
    scatter += (_SubSurfaceBase + _SubSurfaceSun * towardsLight) * _SubSurfaceColour.rgb * radiance * scatteringWeight;
#endif
    // Retain the material's Crest highlight shape, with URP's range, cone, cookie and shadow attenuation.
    specular += CrestFilteredLightHighlight(dot(reflect(-view, normalWS), light.direction), _AdditionalLightFallOff, normalVariance)
        * _AdditionalLightBoost * radiance * ndl;
#if _FOAM_ON
#if _FOAM3DLIGHTING_ON
    // Reuse each phase's sun normal and derivative variance. All texture samples
    // and derivatives ran before this light loop; no additional light fetch is needed.
    if (foamLighting.weights.x > 0.0)
    {
        foam += foamLighting.weights.x * CrestFoamAdditionalLight(foamLighting.normal0, view, light.direction, radiance, foamLighting.variance0);
    }
#if _FLOW_ON
    if (foamLighting.weights.y > 0.0)
    {
        foam += foamLighting.weights.y * CrestFoamAdditionalLight(foamLighting.normal1, view, light.direction, radiance, foamLighting.variance1);
    }
#endif
#else
    foam += _FoamWhiteColor.rgb * _WaveFoamLightScale * radiance * ndl;
#endif
#endif
}

void CrestAdditionalLights(float3 positionWS, half3 normalWS, half3 view, float2 screenUV,
    half seaDepth, half sss, bool underwater, float normalVariance,
    CrestFoamLightingData foamLighting,
    out half3 scatter, out half3 specular, out half3 foam)
{
    scatter = specular = foam = 0.0h;
#if defined(_ADDITIONAL_LIGHTS) || defined(_ADDITIONAL_LIGHTS_VERTEX) || USE_CLUSTER_LIGHT_LOOP
    UNITY_BRANCH
    if (_AdditionalLightIntensity <= 0.0h) return;
    half3 scatterTint = lerp(_DiffuseGrazing, _Diffuse, abs(view.y));
#if _SUBSURFACESHALLOWCOLOUR_ON && _SUBSURFACESCATTERING_ON
    half shallowness = pow(1.0h - saturate(seaDepth / _SubSurfaceDepthMax), _SubSurfaceDepthPower);
    scatterTint = lerp(scatterTint, _SubSurfaceShallowCol, shallowness);
#endif
    // LIGHT_LOOP_BEGIN consumes inputData in URP's clustered path.
    InputData inputData = (InputData)0;
    inputData.positionWS = positionWS;
    inputData.normalizedScreenSpaceUV = screenUV;
#if USE_CLUSTER_LIGHT_LOOP
    UNITY_LOOP for (uint lightIndex = 0; lightIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); ++lightIndex)
    {
        CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK
        Light light = GetAdditionalLight(lightIndex, positionWS, half4(1, 1, 1, 1));
        CrestAccumulateAdditionalLight(light, normalWS, view, scatterTint, sss, underwater, normalVariance, foamLighting, scatter, specular, foam);
    }
#endif
    // Evaluate per pixel even for a per-vertex URP asset: large ocean tiles otherwise lose local highlights.
    uint lightCount = GetAdditionalLightsCount();
    LIGHT_LOOP_BEGIN(lightCount)
        Light light = GetAdditionalLight(lightIndex, positionWS, half4(1, 1, 1, 1));
        CrestAccumulateAdditionalLight(light, normalWS, view, scatterTint, sss, underwater, normalVariance, foamLighting, scatter, specular, foam);
    LIGHT_LOOP_END
#endif
}
#endif
