// Crest Ocean System

// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

#if _PROCEDURALSKY_ON
half3 SkyProceduralDP(in const half3 i_refl, in const half3 i_lightDir)
{
	half dp = dot(i_refl, i_lightDir);

	if (dp > _SkyDirectionality)
	{
		dp = (dp - _SkyDirectionality) / (1. - _SkyDirectionality);
		return lerp(_SkyBase, _SkyTowardsSun, dp);
	}

	dp = (dp - -1.0) / (_SkyDirectionality - -1.0);
	return lerp(_SkyAwayFromSun, _SkyBase, dp);
}
#endif

#if _PLANARREFLECTIONS_ON
#if defined(CREST_URP)
float CrestPlanarReflectionMip(float2 textureSize, float mipCount, float perceptualRoughness)
{
	// A screen-space box-mip approximation, not an angular GGX prefilter. Express
	// the roughness footprint in UV units so changing RT resolution does not change
	// the intended blur radius. One sixteenth of the image at maximum roughness.
	float roughness = saturate(perceptualRoughness);
	float roughnessFootprint = max(textureSize.x, textureSize.y) * 0.0625
		* roughness * roughness * clamp(_PlanarReflectionRoughnessScale, 0.0, 2.0);
	float2 pixelFootprint = textureSize / max(_ScaledScreenParams.xy, 1.0);
	float footprint = max(1.0, max(roughnessFootprint, max(pixelFootprint.x, pixelFootprint.y)));
	return min(log2(footprint), max(mipCount - 1.0, 0.0));
}

float CrestPlanarReflectionEdgeWeight(float2 uv, float2 textureSize, float mipLevel)
{
	float2 distanceToEdge = min(uv, 1.0 - uv);
	float edgeDistance = min(distanceToEdge.x, distanceToEdge.y);
	// Keep at least one filtered half-texel of coverage falloff even when the
	// optional wider edge fade is zero. Outside the capture, use the environment.
	float edgeWidth = max(_PlanarReflectionEdgeFade, 0.5 * exp2(mipLevel) / min(textureSize.x, textureSize.y));
	return smoothstep(0.0, edgeWidth, edgeDistance);
}
#endif

void PlanarReflection(in const float4 i_screenPos, in const half3 i_n_pixel, in const float i_pixelZ, in const float i_normalVariance, inout half3 io_colour)
{
#if defined(CREST_URP)
	// Homogeneous screen coordinates can be much larger than the render target.
	// Half precision quantizes or overflows them at distant water pixels.
	float4 screenPos = i_screenPos;
	screenPos.xy += max(1.0, i_pixelZ * _PlanarReflectionDistanceFactor) * _PlanarReflectionNormalsStrength * i_n_pixel.xz;
	float2 uv = screenPos.xy / max(screenPos.w, 0.0001);

	uint width, height;
	_ReflectionTex.GetDimensions(width, height);
	// Unity gates mip-count queries on backends which implement them. The Core
	// fallback returns zero, for which the helper safely selects LOD 0.
	uint mipCount = GetMipCount(TEXTURE2D_ARGS(_ReflectionTex, sampler_ReflectionTex));
	float2 textureSize = max(float2(width, height), 1.0);
	float perceptualRoughness = CrestFilteredPerceptualRoughness(_Roughness, i_normalVariance);
	float mipLevel = CrestPlanarReflectionMip(textureSize, mipCount, perceptualRoughness);
	float edgeWeight = CrestPlanarReflectionEdgeWeight(uv, textureSize, mipLevel);
	float2 halfTexel = 0.5 / textureSize;
	half4 refl = SAMPLE_TEXTURE2D_LOD(_ReflectionTex, sampler_ReflectionTex, clamp(uv, halfTexel, 1.0 - halfTexel), mipLevel);

	// Transparent-black clear + filtered covered pixels gives premultiplied
	// coverage. Multiplying RGB by alpha again creates dark fringes in the mips.
	// Alpha <= 0 must contribute no RGB (including terrain/material alpha errors).
	half coverage = saturate(refl.a);
	float intensity = saturate(_PlanarReflectionIntensity) * edgeWeight;
	if (coverage > 0.0)
	{
		io_colour = io_colour * (1.0 - intensity * coverage) + refl.rgb * intensity;
	}
#else
	// Keep the existing Built-in diagnostic path and its sampler semantics.
	half4 screenPos = i_screenPos;
	screenPos.xy += max(1.0, i_pixelZ * _PlanarReflectionDistanceFactor) * _PlanarReflectionNormalsStrength * i_n_pixel.xz;
	half4 refl = SAMPLE_TEXTURE2D(_ReflectionTex, sampler_ReflectionTex, screenPos.xy / screenPos.w);
	// If more than four layers are used on terrain, they will appear black if HDR is enabled on the planar reflection
	// camera. Reflection alpha is probably a negative value.
	io_colour = lerp(io_colour, refl.rgb, _PlanarReflectionIntensity * saturate(refl.a));
#endif
}
#endif // _PLANARREFLECTIONS_ON

float CalculateFresnelReflectionCoefficient(float cosTheta)
{
	// Fresnel calculated using Schlick's approximation
	// See: http://www.cs.virginia.edu/~jdl/bib/appearance/analytic%20models/schlick94b.pdf
	// reflectance at facing angle
	float R_0 = (_RefractiveIndexOfAir - _RefractiveIndexOfWater) / (_RefractiveIndexOfAir + _RefractiveIndexOfWater); R_0 *= R_0;
	const float R_theta = R_0 + (1.0 - R_0) * pow(max(0.,1.0 - cosTheta), _FresnelPower);
	return R_theta;
}

void ApplyReflectionSky
(
	in const half3 i_view,
	in const half3 i_n_pixel,
	in const float3 i_positionWS,
	in const half3 i_lightDir,
	in const half i_shadow,
	in const float4 i_screenPos,
	in const float i_pixelZ,
	in const half i_weight,
	in const float i_normalVariance,
	inout half3 io_col
)
{
	// Reflection
	half3 refl = reflect(-i_view, i_n_pixel);
	// Dont reflect below horizon
	refl.y = max(refl.y, 0.0);

	half3 skyColour;


#if _PROCEDURALSKY_ON
	// procedural sky cubemap
	skyColour = SkyProceduralDP(refl, i_lightDir);
#else

	// sample sky cubemap
#if _OVERRIDEREFLECTIONCUBEMAP_ON
	// User-provided cubemap
	half4 val = SAMPLE_TEXTURECUBE(_ReflectionCubemapOverride, sampler_ReflectionCubemapOverride, refl);
	skyColour = val.rgb;
#else
#if defined(CREST_URP)
    float perceptualRoughness = CrestFilteredPerceptualRoughness(_Roughness, i_normalVariance);
    skyColour = GlossyEnvironmentReflection(refl, i_positionWS, perceptualRoughness, 1.0h, i_screenPos.xy / i_screenPos.w);
#else
	Unity_GlossyEnvironmentData envData;
	envData.roughness = _Roughness;
	envData.reflUVW = refl;
	float3 probe0 = Unity_GlossyEnvironment(UNITY_PASS_TEXCUBE(unity_SpecCube0), unity_SpecCube0_HDR, envData);
	#if UNITY_SPECCUBE_BLENDING
	float interpolator = unity_SpecCube0_BoxMin.w;
	// Branch optimization recommended by: https://catlikecoding.com/unity/tutorials/rendering/part-8/
	UNITY_BRANCH
	if (interpolator < 0.99999)
	{
		float3 probe1 = Unity_GlossyEnvironment(UNITY_PASS_TEXCUBE_SAMPLER(unity_SpecCube1, unity_SpecCube0), unity_SpecCube1_HDR, envData);
		skyColour = lerp(probe1, probe0, interpolator);
	}
	else
	{
		skyColour = probe0;
	}
	#else
	skyColour = probe0;
	#endif
#endif
#endif

#endif

	// Override with anything in the planar reflections
#if _PLANARREFLECTIONS_ON
	PlanarReflection(i_screenPos, i_n_pixel, i_pixelZ, i_normalVariance, skyColour);
#endif

	// Add primary light
#if _COMPUTEDIRECTIONALLIGHT_ON
#if _DIRECTIONALLIGHTVARYROUGHNESS_ON
	half fallOffAlpha = saturate(i_pixelZ / max(_DirectionalLightFarDistance, 0.001));
	fallOffAlpha = sqrt(fallOffAlpha);
	half fallOff = lerp(_DirectionalLightFallOff, _DirectionalLightFallOffFar, fallOffAlpha);
#else
	half fallOff = _DirectionalLightFallOff;
#endif

	skyColour += CrestFilteredLightHighlight(dot(refl, i_lightDir), fallOff, i_normalVariance)
		* _DirectionalLightBoost * _LightColor0 * i_shadow;
#endif

	// Fresnel
	float R_theta = CalculateFresnelReflectionCoefficient(max(dot(i_n_pixel, i_view), 0.0));
	io_col = lerp(io_col, skyColour, R_theta * _Specular * i_weight);
}

#if _UNDERWATER_ON
void ApplyReflectionUnderwater
(
	in const half3 i_view,
	in const half3 i_n_pixel,
	in const half3 i_lightDir,
	in const half i_shadow,
	in const float4 i_screenPos,
	half3 scatterCol,
	in const half i_weight,
	inout half3 io_col
)
{
	const half3 underwaterColor = scatterCol;
	// The the angle of outgoing light from water's surface
	// (whether refracted form outside or internally reflected)
	const float cosOutgoingAngle = saturate(dot(i_n_pixel, i_view));
	const float refractiveRatio = _RefractiveIndexOfWater / max(_RefractiveIndexOfAir, 0.0001);
	const float sinIncomingSquared = refractiveRatio * refractiveRatio * max(1.0 - cosOutgoingAngle * cosOutgoingAngle, 0.0);
	const float cosIncomingAngle = sqrt(max(1.0 - sinIncomingSquared, 0.0));
	// Use one bounded coefficient for both sides of the blend. At the critical
	// angle it tends continuously to one, and beyond it all light is reflected.
	// This retains the material's existing Schlick/Fresnel Power control.
	const float reflectionCoefficient = (sinIncomingSquared >= 1.0 ? 1.0 : CalculateFresnelReflectionCoefficient(cosIncomingAngle)) * i_weight;
	io_col = max(io_col, 0.0) * (1.0 - reflectionCoefficient) + underwaterColor * reflectionCoefficient;
}
#endif
