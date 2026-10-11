// Crest Ocean System

// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

#ifndef CREST_OCEAN_NORMAL_MAPPING_INCLUDED
#define CREST_OCEAN_NORMAL_MAPPING_INCLUDED

#if _APPLYNORMALMAPPING_ON

float3 CrestReorientNormal(in const float3 i_baseNormalWS, in const float2 i_detailXZ)
{
	// Rotate the effective flat-water detail normal (detail.x, 1, detail.y)
	// from world up onto the normalised wave/water-level normal. This is RNM
	// with Y as the reference axis: https://blog.selfshadow.com/publications/blending-in-detail/
	// Keeping the existing detail XZ definition preserves the flat-water strength.
	// The upward macro normal makes t.y >= 1, so no antiparallel singularity occurs.
	const float3 t = i_baseNormalWS + float3(0.0, 1.0, 0.0);
	const float3 u = float3(-i_detailXZ.x, 1.0, -i_detailXZ.y);
	// Division by t.y cancels in the final normalisation. The result stays in
	// the macro normal's hemisphere even when an individual ripple has Y < 0.
	return normalize(t * dot(t, u) - u * t.y);
}

half2 SampleNormalMaps
(
	in const WaveHarmonic::Crest::TiledTexture i_texture,
	in const float2 i_worldXZUndisplaced,
	in const float2 offset,
	in const float lodAlpha,
	in const CascadeParams cascadeData,
	in const PerCascadeInstanceData instanceData
)
{
	float2 normalScrollSpeeds = instanceData._normalScrollSpeeds;

	const float2 v0 = float2(0.94, 0.34), v1 = float2(-0.85, -0.53);

	float2 worldXZUndisplaced = i_worldXZUndisplaced;

#if CREST_FLOATING_ORIGIN
	// Apply tiled shifting origin offset. Always needed.
	worldXZUndisplaced -= i_texture.FloatingOriginOffset(cascadeData);
#endif

	worldXZUndisplaced -= offset;

	float nstretch = i_texture._scale * cascadeData._scale / 100.0; // normals scaled with geometry
	// Include spatially varying flow in the footprint before entering the LOD blend
	// branch. Implicit derivatives in that branch can be undefined across a pixel quad.
	// Scroll offsets are uniform within a draw, so both scroll directions share these gradients.
	const float2 normalUV = worldXZUndisplaced / nstretch;
	const float2 normalUVdx = ddx(normalUV);
	const float2 normalUVdy = ddy(normalUV);
	const float spdmulL = normalScrollSpeeds[0];
	half2 norm =
		UnpackNormal(i_texture.SampleGrad((v0 * _CrestTime * spdmulL + worldXZUndisplaced) / nstretch, normalUVdx, normalUVdy)).xy +
		UnpackNormal(i_texture.SampleGrad((v1 * _CrestTime * spdmulL + worldXZUndisplaced) / nstretch, normalUVdx, normalUVdy)).xy;

	// blend in next higher scale of normals to obtain continuity
	const float farNormalsWeight = instanceData._farNormalsWeight;
	const half nblend = lodAlpha * farNormalsWeight;
	if (nblend > 0.001)
	{
		// next lod level
		nstretch *= 2.;
		const float spdmulH = normalScrollSpeeds[1];
		norm = lerp(norm,
			UnpackNormal(i_texture.SampleGrad((v0 * _CrestTime * spdmulH + worldXZUndisplaced) / nstretch, normalUVdx * 0.5, normalUVdy * 0.5)).xy +
			UnpackNormal(i_texture.SampleGrad((v1 * _CrestTime * spdmulH + worldXZUndisplaced) / nstretch, normalUVdx * 0.5, normalUVdy * 0.5)).xy,
			nblend);
	}

	// Preserve the existing effective flat-water perturbation. The caller blends
	// the flow phases before reorienting this detail onto the macro normal.
	return _NormalsStrength * norm;
}

void ApplyNormalMapsWithFlow
(
	in const WaveHarmonic::Crest::TiledTexture i_texture,
	in const float2 worldXZUndisplaced,
	in const float2 flow,
	in const float lodAlpha,
	in const CascadeParams cascadeData,
	in const PerCascadeInstanceData instanceData,
	inout float3 io_n
)
{
	const float half_period = 1;
	const float period = half_period * 2;
	float sample1_offset = fmod(_CrestTime, period);
	float sample1_weight = sample1_offset / half_period;
	if (sample1_weight > 1.0) sample1_weight = 2.0 - sample1_weight;
	float sample2_offset = fmod(_CrestTime + half_period, period);
	float sample2_weight = 1.0 - sample1_weight;
	sample1_offset -= 0.5 * period;
	sample2_offset -= 0.5 * period;

	// In order to prevent flow from distorting the UVs too much,
	// we fade between two samples of normal maps so that for each
	// sample the UVs can be reset
	half2 io_n_1 = SampleNormalMaps(i_texture, worldXZUndisplaced, flow * sample1_offset, lodAlpha, cascadeData, instanceData);
	half2 io_n_2 = SampleNormalMaps(i_texture, worldXZUndisplaced, flow * sample2_offset, lodAlpha, cascadeData, instanceData);
	io_n.xz += sample1_weight * io_n_1;
	io_n.xz += sample2_weight * io_n_2;
}

#endif // _APPLYNORMALMAPPING_ON

#endif // CREST_OCEAN_NORMAL_MAPPING_INCLUDED
