	//The MIT License(MIT)

//Copyright(c) 2016 Charles Greivelding Thomas

//Permission is hereby granted, free of charge, to any person obtaining a copy
//of this software and associated documentation files (the "Software"), to deal
//in the Software without restriction, including without limitation the rights
//to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//copies of the Software, and to permit persons to whom the Software is
//furnished to do so, subject to the following conditions:

//The above copyright notice and this permission notice shall be included in all
//copies or substantial portions of the Software.

//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//SOFTWARE.
	
#include "UnityStandardBRDF.cginc"

#define PI 3.141592

uniform sampler2D _MipMapInput;
uniform sampler2D _BlitInput;
uniform sampler2D _DepthPyramidInput;
uniform sampler2D _MainTex;
uniform sampler2D _ReflectionBuffer;
uniform sampler2D _PreviousBuffer;
uniform sampler2D _RayCast;
uniform sampler2D _RayCastMask;
uniform sampler2D _MinZDepth;
uniform sampler2D _CameraGBufferTexture0;
uniform sampler2D _CameraGBufferTexture1;
uniform sampler2D _CameraGBufferTexture2;
uniform sampler2D _CameraReflectionsTexture;
uniform sampler2D _CostMap;
uniform sampler2D _MinDepth;
	
uniform sampler2D	_CameraDepthTexture; // Unity depth
uniform sampler2D_half _CameraMotionVectorsTexture;

uniform float4		_MainTex_TexelSize;
uniform float4      _CameraDepthTexture_TexelSize;
uniform float4      _VisibilityMask_TexelSize;
uniform float4		_ReflectionBuffer_TexelSize;
uniform float4		_ScreenSize;
uniform float4		_GaussianDir;
uniform float2		_MinZStride;
uniform float		_Intensity;

uniform int			_MipMapCount;
uniform float       _MipLevel;
uniform bool        _MaskBlur;

uniform float4x4	_InverseViewProjectionMatrix;
uniform float4x4	_WorldToCameraMatrix;

//Debug Options
uniform float		_UseFresnel;
uniform int			_DebugPass;

float sqr(float x)
{
	return x*x;
}
	
float fract(float x)
{
	return x - floor( x );
}

float4 GetSampleColor (sampler2D tex, float2 uv) { return tex2D(tex, uv); }
float4 GetCubeMap (float2 uv) { return tex2D(_CameraReflectionsTexture, uv); }
float4 GetAlbedo (float2 uv) { return tex2D(_CameraGBufferTexture0, uv); }
float4 GetSpecular (float2 uv,float type) 
{
    float4 spec = tex2D(_CameraGBufferTexture1, uv);
    spec.gb = lerp(spec.gb, spec.rr, 1-type);
    return spec;
}
float4 GetNormal (float2 uv) 
{ 
	float4 gbuffer2 = tex2D(_CameraGBufferTexture2, uv);

    return float4(gbuffer2.rgb * 2 - 1, gbuffer2.a);
}

float4 GetVelocity(float2 uv)    { return tex2D(_CameraMotionVectorsTexture, uv); }
float4 GetReflection(float2 uv)    { return tex2D(_ReflectionBuffer, uv); }

float ComputeDepth(float4 clippos)
{
#if defined(SHADER_TARGET_GLSL) || defined(SHADER_API_GLES) || defined(SHADER_API_GLES3)
	return (clippos.z / clippos.w) * 0.5 + 0.5;
#else
	return clippos.z / clippos.w;
#endif
}

float3 GetViewNormal (float3 normal)
{
	float3 viewNormal =  mul((float3x3)_WorldToCameraMatrix, normal.rgb);
	return normalize(viewNormal);
}

float GetDepth (sampler2D tex, float2 uv)
{
    return tex2Dlod(tex, float4(uv, 0, 0)).r;
}

float GetDepth (sampler2D tex, float2 uv, float mip)
{
	return tex2Dlod(tex, float4(uv, 0, mip));
}

float3 GetScreenPos (float2 uv, float depth)
{
	return float3(uv * 2 - 1, depth);
}

float3 GetWorlPos (float3 screenPos)
{
	float4 worldPos = mul(_InverseViewProjectionMatrix, float4(screenPos, 1));
	return worldPos.xyz / worldPos.w;
}

	
float3 GetViewDir (float3 worldPos)
{
	return normalize(worldPos - _WorldSpaceCameraPos);
}

// Deprecated since 5.4
/*float2 ReprojectUV(float3 clipPosition)
{
	float4 previousClipPosition = mul(_PrevInverseViewProjectionMatrix, float4(clipPosition, 1.0f));
	previousClipPosition.xyz /= previousClipPosition.w;

	return float2(previousClipPosition.xy * 0.5f + 0.5f);
}*/

static const int2 offset[9] =
{
    float2(0, 0),
	float2(1, -1),
	float2(-1, -1),
	float2(0, 1),
    float2(1, 0),
    float2(1, 1),
	float2(0, -1),
	float2(-1, 0),
	float2(-1, 1)
};

float RayAttenBorder (float2 pos, float value)
{
	float borderDist = min(1.0 - max(pos.x, pos.y), min(pos.x, pos.y));
	return saturate(borderDist > value ? 1.0 : borderDist / value);
}