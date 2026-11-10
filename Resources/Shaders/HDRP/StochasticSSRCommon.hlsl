#ifndef STOCHASTIC_SSR_HDRP_COMMON_INCLUDED
#define STOCHASTIC_SSR_HDRP_COMMON_INCLUDED

// Shared helpers for the HDRP port of the stochastic screen space reflections.
//
// HDRP conventions used here:
//  * UV coordinates have their origin in the top left corner of the screen (v = 0 is the first row of the texture),
//    exactly like HDRP's PositionInputs.positionNDC.
//  * The view space is the regular right handed Unity view space (the camera looks down -Z).
//  * Depth is always the raw device depth, so it works with and without reversed Z.

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

// ---------------------------------------------------------------------------------------------
// Textures
// ---------------------------------------------------------------------------------------------
Texture2D<half4>  _SRSceneColor;        // Scene color copy, mip chain potentially filled
Texture2DArray<half4> _SRSceneColorSource; // Camera color buffer, only used by the copy pass
Texture2DArray<float4> _SRNormalBuffer;   // HDRP normal buffer: octahedral normal (888) + perceptual roughness in alpha
Texture2DArray<float>  _SRDepthTexture;   // Raw device depth
Texture2DArray<float2> _SRMotionVectors;  // HDRP motion vectors (uv space delta)
Texture2D<float>  _SRDepthPyramid;      // Hierarchical Z buffer
Texture2D<float4> _SRNoise;             // Blue noise
Texture2D<half4>  _SRPreviousTemporal;  // Temporal history

int _SRSliceIndex; // Slice of the camera buffers, 0 outside of XR

SamplerState sampler_point_clamp;
SamplerState sampler_point_repeat;
SamplerState sampler_linear_clamp;
SamplerState sampler_linear_repeat;

// ---------------------------------------------------------------------------------------------
// Uniforms
// ---------------------------------------------------------------------------------------------
float4 _SR_ScreenSize;          // width, height, 1 / width, 1 / height (full resolution)
float4 _SR_RayCastSize;         // width, height, 1 / width, 1 / height (raycast resolution)
float4 _SR_ResolveSize;         // width, height, 1 / width, 1 / height (resolve resolution)
float4 _SR_NoiseSize;           // width, height, 1 / width, 1 / height (blue noise)
float4 _SRJitterSizeAndOffset;  // screenSize / noiseSize in xy, blue noise offset in zw
float4 _SRZBufferParams;        // Unity layout, valid for regular and reversed Z
float4x4 _SRProjMatrix;         // Platform corrected projection matrix of the current frame
float4x4 _SRInvProjMatrix;
float4x4 _SRViewMatrix;

float _SRBRDFBias;
float _SRThickness;
float _SREdgeFactor;
float _SRSmoothnessRange;
float _SRTResponseMin;
float _SRTResponseMax;

int _SRNumSteps;
int _SRMaxMipMap;
int _SRNormalization;

// ---------------------------------------------------------------------------------------------
// Screen space helpers
// ---------------------------------------------------------------------------------------------
float4 SR_GetClipSpacePosition(float2 uv, float deviceDepth)
{
	float4 positionCS = float4(uv * 2.0 - 1.0, deviceDepth, 1.0);

#if UNITY_UV_STARTS_AT_TOP
	// Unity's clip space is flipped upside down compared to NDC and screen space.
	positionCS.y = -positionCS.y;
#endif

	return positionCS;
}

float3 SR_GetViewPosition(float2 uv, float deviceDepth)
{
	float4 positionVS = mul(_SRInvProjMatrix, SR_GetClipSpacePosition(uv, deviceDepth));
	return positionVS.xyz / positionVS.w;
}

float3 SR_GetUVAndDeviceDepthFromViewPosition(float3 positionVS)
{
	float4 positionCS = mul(_SRProjMatrix, float4(positionVS, 1.0));
	float3 ndc = positionCS.xyz / positionCS.w;

	float2 uv = ndc.xy * 0.5 + 0.5;

#if UNITY_UV_STARTS_AT_TOP
	uv.y = 1.0 - uv.y;
#endif

	return float3(uv, ndc.z);
}

// ---------------------------------------------------------------------------------------------
// G-Buffer access
// ---------------------------------------------------------------------------------------------
float SR_GetRoughness(float perceptualRoughness)
{
	return max(min(_SRSmoothnessRange, perceptualRoughness), 0.05);
}

// Returns the world space normal in xyz and the roughness in w.
float4 SR_GetNormalAndRoughness(float2 uv)
{
	float4 normalBuffer = _SRNormalBuffer.SampleLevel(sampler_linear_clamp, float3(uv, (float)_SRSliceIndex), 0.0);
	float2 octNormalWS = Unpack888ToFloat2(normalBuffer.rgb);
	float3 normalWS = UnpackNormalOctQuadEncode(octNormalWS * 2.0 - 1.0);

	return float4(normalWS, SR_GetRoughness(normalBuffer.a));
}

float3 SR_GetViewNormal(float3 normalWS)
{
	return normalize(mul((float3x3)_SRViewMatrix, normalWS));
}

float SR_GetHZDepth(float2 uv, float mip)
{
	return _SRDepthPyramid.SampleLevel(sampler_point_clamp, uv, mip);
}

float SR_GetRawDepth(float2 uv)
{
	return _SRDepthTexture.SampleLevel(sampler_point_clamp, float3(uv, (float)_SRSliceIndex), 0.0);
}

// HDRP stores (currentPositionCS - previousPositionCS) * 0.5, which is directly usable as a
// uv space delta. Values above one mark pixels that have no valid motion information.
float2 SR_GetMotionVector(float2 uv)
{
	float2 motionVector = _SRMotionVectors.SampleLevel(sampler_point_clamp, float3(uv, (float)_SRSliceIndex), 0.0).xy;

	return motionVector.x > 1.0 ? float2(0.0, 0.0) : motionVector;
}

half SR_Luminance(half3 rgb)
{
	return dot(rgb, half3(0.0396819152, 0.458021790, 0.00609653955));
}

float SR_RayAttenBorder(float2 pos, float edge)
{
	float borderDist = min(1.0 - max(pos.x, pos.y), min(pos.x, pos.y));

	return saturate(borderDist > edge ? 1.0 : borderDist / max(edge, 1e-5));
}

static const int2 SR_Offset[9] =
{
	int2(0, 0),
	int2(0, 1),
	int2(1, -1),
	int2(-1, -1),
	int2(-1, 0),
	int2(0, -1),
	int2(1, 0),
	int2(-1, 1),
	int2(1, 1)
};

#endif // STOCHASTIC_SSR_HDRP_COMMON_INCLUDED
