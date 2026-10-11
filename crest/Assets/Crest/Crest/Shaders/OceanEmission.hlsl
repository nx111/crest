// Crest Ocean System

// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

#ifndef CREST_OCEAN_EMISSION_INCLUDED
#define CREST_OCEAN_EMISSION_INCLUDED

half3 ScatterColour
(
	in const half i_surfaceOceanDepth,
	in const float i_shadow,
	in const half sss,
	in const half3 i_view,
	in const half3 i_ambientLighting,
	in const half3 i_lightDir,
	in const half3 i_lightCol,
	in const bool i_underwater
)
{
	// base colour
	float v = abs(i_view.y);
	// Previously caused rendering artifacts. See issue #1040.
	half3 col = lerp(_DiffuseGrazing, _Diffuse, v);

#if _SHADOWS_ON
	col = lerp(_DiffuseShadow, col, i_shadow);
#endif

#if _SUBSURFACESCATTERING_ON
	{
#if _SUBSURFACESHALLOWCOLOUR_ON
		float shallowness = pow(1. - saturate(i_surfaceOceanDepth / _SubSurfaceDepthMax), _SubSurfaceDepthPower);
		half3 shallowCol = _SubSurfaceShallowCol;
#if _SHADOWS_ON
		shallowCol = lerp(_SubSurfaceShallowColShadow, shallowCol, i_shadow);
#endif
		col = lerp(col, shallowCol, shallowness);
#endif

		col *= i_ambientLighting;

		// Approximate subsurface scattering - add light when surface faces viewer. Use geometry normal - don't need high freqs.
		half towardsSun = pow(max(0., dot(i_lightDir, -i_view)), _SubSurfaceSunFallOff);
		half3 subsurface = (_SubSurfaceBase + _SubSurfaceSun * towardsSun) * _SubSurfaceColour.rgb * i_lightCol * i_shadow;
		if (!i_underwater)
		{
			subsurface *= (1.0 - v * v) * sss;
		}
		col += subsurface;
	}
#endif // _SUBSURFACESCATTERING_ON

	return col;
}


#if _CAUSTICS_ON
void ApplyCaustics
(
	in const WaveHarmonic::Crest::TiledTexture i_causticsTexture,
	in const WaveHarmonic::Crest::TiledTexture i_distortionTexture,
	in const int2 i_positionSS,
	in const float3 i_scenePos,
	in const half3 i_lightDir,
	in const float i_sceneZ,
	in const bool i_underwater,
	inout half3 io_sceneColour,
	in const int i_sliceIndex,
	in const CascadeParams cascadeData
)
{
	// Caustics need incident sunlight. Keep horizon/night and invalid scales out of the
	// projection and texture queries; all samples below use explicit LODs.
	const float sunlight = smoothstep(0.0, 0.1, i_lightDir.y) * saturate(dot(_LightColor0.rgb, float3(0.2126, 0.7152, 0.0722)));
	if (sunlight <= 0.0 || _CausticsStrength <= 0.0 || i_causticsTexture._scale <= 0.0 || i_distortionTexture._scale <= 0.0)
	{
		return;
	}

	const float3 scenePosUV = WorldToUV(i_scenePos.xz, cascadeData, i_sliceIndex);

	float3 disp = 0.0;
	// this gives height at displaced position, not exactly at query position.. but it helps. i cant pass this from vert shader
	// because i dont know it at scene pos.
	SampleDisplacements(_LD_TexArray_AnimatedWaves, scenePosUV, 1.0, disp);
	float seaLevelOffset = _LD_TexArray_SeaFloorDepth.SampleLevel(LODData_linear_clamp_sampler, scenePosUV, 0.0).y;
	float waterHeight = _OceanCenterPosWorld.y + disp.y + seaLevelOffset;
	float sceneDepth = waterHeight - i_scenePos.y;
	// Smooth the first ten centimetres instead of projecting caustics onto dry geometry.
	const float submerged = smoothstep(0.0, 0.1, sceneDepth);
	if (submerged <= 0.0)
	{
		return;
	}

	// Snell's law at the mean water plane. Refraction limits the transmitted solar
	// angle, unlike the old sceneDepth / (4 * lightDir.y) projection at the horizon.
	const float refractiveRatio = _RefractiveIndexOfAir / max(_RefractiveIndexOfWater, 0.0001);
	const float2 lightInWaterXZ = refractiveRatio * i_lightDir.xz;
	const float transmittedSinSquared = dot(lightInWaterXZ, lightInWaterXZ);
	if (transmittedSinSquared >= 1.0)
	{
		return;
	}
	const float lightInWaterY = sqrt(max(1.0 - transmittedSinSquared, 0.0001));
	const float lightPath = sceneDepth / lightInWaterY;
	float2 lightProjection = lightInWaterXZ * lightPath;

	// Compute mip index manually, with bias based on sea floor depth. We compute it manually because if it is computed automatically it produces ugly patches
	// where samples are stretched/dilated. The bias is to give a focusing effect to caustics - they are sharpest at a particular depth. This doesn't work amazingly
	// well and could be replaced.
	float mipLod = log2(max(i_sceneZ, 1.0)) + abs(sceneDepth - _CausticsFocalDepth) / max(_CausticsDepthOfField, 0.001);

	float3 cuv1 = 0.0; float3 cuv2 = 0.0;
	{
		float2 surfacePosXZ = i_scenePos.xz;
		float surfacePosScale = 1.37;

#if CREST_FLOATING_ORIGIN
		// Apply tiled shifting origin offset. Always needed.
		surfacePosXZ -= i_causticsTexture.FloatingOriginOffset();
		// Scale was causing popping.
		surfacePosScale = 1.0;
#endif

		surfacePosXZ += lightProjection;

		cuv1 = float3
		(
			surfacePosXZ / i_causticsTexture._scale + float2(0.044 * _CrestTime + 17.16, -0.169 * _CrestTime),
			mipLod
		);
		cuv2 = float3
		(
			surfacePosScale * surfacePosXZ / i_causticsTexture._scale + float2(0.248 * _CrestTime, 0.117 * _CrestTime),
			mipLod
		);
	}

	// Apply distortion.
	{
		float2 surfacePosXZ = i_scenePos.xz;

#if CREST_FLOATING_ORIGIN
		// Apply tiled shifting origin offset. Always needed.
		surfacePosXZ -= i_distortionTexture.FloatingOriginOffset();
#endif

		surfacePosXZ += lightProjection;

		half2 causticN = _CausticsDistortionStrength * UnpackNormal(i_distortionTexture.SampleLevel(surfacePosXZ / i_distortionTexture._scale, mipLod)).xy;
		cuv1.xy += 1.30 * causticN;
		cuv2.xy += 1.77 * causticN;
	}

	// Attenuate the incident light on its path into the water as well as the viewing
	// path handled by the caller. Deep water should lose caustic contrast.
	half3 causticsStrength = _CausticsStrength * sunlight * submerged * exp(-max(_DepthFogDensity.xyz, 0.0) * lightPath);

#if _SHADOWS_ON
	{
		// Apply shadow maps to caustics.
		{
			// Normally, we would use SHADOW_ATTENUATION(), but SHADOWS_SCREEN and UNITY_NO_SCREENSPACE_SHADOWS are not
			// handled for the transparent pass.
			// Null texture is a small white texture which will be smaller than the screen size.
			#if defined(CREST_URP)
            causticsStrength *= CrestMainLightShadow(i_scenePos);
#else
            causticsStrength *= LOAD_TEXTURE2D_X(_CrestScreenSpaceShadowTexture,  min(i_positionSS, (int2)_CrestScreenSpaceShadowTexture_TexelSize.zw - 1)).r;
#endif
		}
	}
#endif // _SHADOWS_ON

	io_sceneColour.xyz *= max(0.0, 1.0 + causticsStrength *
	(
		0.5 * i_causticsTexture.SampleLevel(cuv1.xy, cuv1.z).xyz +
		0.5 * i_causticsTexture.SampleLevel(cuv2.xy, cuv2.z).xyz -
		_CausticsTextureAverage
	));
}
#endif // _CAUSTICS_ON


half3 OceanEmission
(
	in const half3 i_view,
	in const half3 i_n_pixel,
	in const float3 i_lightDir,
	in const float4 i_grabPos,
	in const float i_pixelZ,
	const float i_rawPixelZ,
	in const float2 i_uvDepth,
	in const int2 i_positionSS,
	in const float i_sceneZ,
	const float i_rawDepth,
	in const half3 i_bubbleCol,
	in const bool i_underwater,
	in const half3 i_scatterCol,
#if CREST_WATER_VOLUME
	in const bool i_backface,
#endif
	in const CascadeParams cascadeData0,
	in const CascadeParams cascadeData1
)
{
	half3 col = i_scatterCol;

	// underwater bubbles reflect in light
	col += i_bubbleCol;

#if _TRANSPARENCY_ON

	// View ray intersects geometry surface either above or below ocean surface

	const float2 uvBackground = i_grabPos.xy / i_grabPos.w;
	half3 sceneColour;
	half3 alpha = 0.;
	float depthFogDistance;

	float2 refractOffset = _RefractionStrength * i_n_pixel.xz;
	if (!i_underwater)
	{
		refractOffset *= saturate(0.5 * (i_sceneZ - i_pixelZ)) / max(i_sceneZ, 0.0001);
	}

	// Keep colour, depth and reconstructed receiver coordinates on the same pixel,
	// including when refraction reaches the edge of the available screen texture.
	const float2 halfTexel = 0.5 * _CameraDepthTexture_TexelSize.xy;
	float2 uvDepthRefract = clamp(i_uvDepth + refractOffset, halfTexel, 1.0 - halfTexel);
	refractOffset = uvDepthRefract - i_uvDepth;
	float rawDepth = CREST_SAMPLE_SCENE_DEPTH_X(uvDepthRefract);

	bool caustics = true;
#if CREST_WATER_VOLUME_HAS_BACKFACE
	bool backface = ApplyVolumeToOceanSurfaceRefractions(uvDepthRefract, i_rawDepth, i_underwater, rawDepth, caustics);
#endif

	// Depth fog & caustics - only if view ray starts from above water
	if (!i_underwater)
	{
		float2 uvBackgroundRefract;
		float sceneZ = i_sceneZ;

		// Compute depth fog alpha based on refracted position if it landed on an underwater surface, or on unrefracted depth otherwise
		if (CrestIsDepthBehind(rawDepth, i_rawPixelZ))
		{
			uvBackgroundRefract = uvBackground + refractOffset;
#if CREST_WATER_VOLUME_HAS_BACKFACE
			if (!backface)
#endif
			{
				rawDepth = CREST_MULTISAMPLE_SCENE_DEPTH(uvDepthRefract, rawDepth);
			}
			sceneZ = CrestLinearEyeDepth(rawDepth);
			depthFogDistance = max(sceneZ - i_pixelZ, 0.0);
		}
		else
		{
			// We have refracted onto a surface in front of the water. Cancel the refraction offset.
			uvBackgroundRefract = uvBackground;
			uvDepthRefract = i_uvDepth;
			rawDepth = i_rawDepth;
#if CREST_WATER_VOLUME_HAS_BACKFACE
			if (!i_backface)
#endif
			{
				rawDepth = CREST_MULTISAMPLE_SCENE_DEPTH(uvDepthRefract, rawDepth);
			}
			sceneZ = CrestLinearEyeDepth(rawDepth);
			// It seems that when MSAA is enabled this can sometimes be negative
			depthFogDistance = max(sceneZ - i_pixelZ, 0.0);
		}

		sceneColour = CrestSampleBackground(uvBackgroundRefract);
#if defined(CREST_URP)
		// Keep short water segments in view space: subtracting two large absolute
		// world positions would quantize shallow water thickness. The sky has no
		// receiver, so use a finite point on its ray instead of unprojecting infinity.
		const bool hasSceneDepth = CrestHasSceneDepth(rawDepth);
		const float reconstructionDepth = hasSceneDepth ? rawDepth : 0.5;
		const float3 scenePosVS = CrestReconstructViewPosition(uvDepthRefract, reconstructionDepth);
		if (hasSceneDepth)
		{
			const float3 surfacePosVS = CrestReconstructViewPosition(i_uvDepth, i_rawPixelZ);
			depthFogDistance = sceneZ > i_pixelZ ? length(scenePosVS - surfacePosVS) : 0.0;
		}
		else
		{
			const float rayLengthPerEyeDepth = unity_OrthoParams.w > 0.5 ? 1.0 : length(scenePosVS) / max(abs(scenePosVS.z), 0.0001);
			depthFogDistance = max(CrestFiniteCameraFarDepth() - i_pixelZ, 0.0) * rayLengthPerEyeDepth;
		}
#endif
#if _CAUSTICS_ON
		if (CrestHasSceneDepth(rawDepth))
#if CREST_WATER_VOLUME_HAS_BACKFACE
		if (caustics)
#endif
		{
#if defined(CREST_URP)
			// Transform the selected receiver only for world-anchored caustics.
			float3 scenePos = mul(UNITY_MATRIX_I_V, float4(scenePosVS, 1.0)).xyz;
#else
			float3 scenePos = _WorldSpaceCameraPos - i_view * sceneZ / dot(unity_CameraToWorld._m02_m12_m22, -i_view);
#endif
			ApplyCaustics(_CausticsTiledTexture, _CausticsDistortionTiledTexture, uvDepthRefract * _ScreenSize.xy, scenePos, i_lightDir, sceneZ, i_underwater, sceneColour, _LD_SliceIndex + 1, cascadeData1);
		}
#endif
		alpha = 1.0 - exp(-max(_DepthFogDensity.xyz, 0.0) * depthFogDistance);
	}
	else
	{
		const float2 uvBackgroundRefract = CrestIsDepthBehind(rawDepth, i_rawPixelZ) ? uvBackground + refractOffset : uvBackground;
		sceneColour = CrestSampleBackground(uvBackgroundRefract);
		depthFogDistance = i_pixelZ;
		// keep alpha at 0 as UnderwaterReflection shader handles the blend
		// appropriately when looking at water from below
	}

	// blend from water colour to the scene colour
	col = lerp(sceneColour, col, alpha);

#endif // _TRANSPARENCY_ON

	return col;
}

#endif // CREST_OCEAN_EMISSION_INCLUDED
