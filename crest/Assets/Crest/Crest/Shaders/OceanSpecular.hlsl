// Crest Ocean System - MIT License (see LICENSE)
#ifndef CREST_OCEAN_SPECULAR_INCLUDED
#define CREST_OCEAN_SPECULAR_INCLUDED

// Evaluate on the final, unflipped water normal, before underwater branches or
// per-light loops. Derivatives in those divergent paths are not well-defined.
float CrestSpecularNormalVariance(float3 normalWS)
{
    float3 deltaU = ddx(normalWS);
    float3 deltaV = ddy(normalWS);
    float variance = clamp(_SpecularAAVariance, 0.0, 0.25)
        * (dot(deltaU, deltaU) + dot(deltaV, deltaV));
    float threshold = saturate(_SpecularAAThreshold);
    return min(variance, 0.5 * threshold * threshold);
}

float CrestFilteredPerceptualRoughness(float perceptualRoughness, float normalVariance)
{
    // Match Unity Core NormalFiltering: perceptual roughness p maps to alpha=p^2,
    // and normal variance is added to alpha^2, not to p^2.
    float roughness = saturate(perceptualRoughness);
    roughness *= roughness;
    return sqrt(sqrt(saturate(roughness * roughness + 2.0 * normalVariance)));
}

float CrestFilteredLightHighlight(float reflectionDotLight, float fallOff, float normalVariance)
{
    // Small-angle filtering of Crest's existing Phong lobe. Reflection direction
    // changes approximately twice as fast as the normal (four times the variance).
    // The peak reduction preserves the angular lobe integral 2*pi/(fallOff+1).
    // This is not a replacement for a full energy-conserving water BRDF.
    fallOff = max(fallOff, 1.0);
    float filteredFallOff = fallOff / (1.0 + 4.0 * normalVariance * fallOff);
    float peakScale = (filteredFallOff + 1.0) / (fallOff + 1.0);
    return pow(saturate(reflectionDotLight), filteredFallOff) * peakScale;
}

#endif
