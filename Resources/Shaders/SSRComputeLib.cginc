#ifdef UNITY_COLORSPACE_GAMMA
#define unity_ColorSpaceLuminance half4(0.22, 0.707, 0.071, 0.0) // Legacy: alpha is set to 0.0 to specify gamma mode
#else // Linear values
#define unity_ColorSpaceLuminance half4(0.0396819152, 0.458021790, 0.00609653955, 1.0) // Legacy: alpha is set to 1.0 to specify linear mode
#endif

Texture2D<half4> ScreenInput;
SamplerState samplerScreenInput;
Texture2D<float> MinDepth;
SamplerState samplerMinDepth;
Texture2D<float4> CameraGBufferTexture2;
SamplerState samplerCameraGBufferTexture2;
Texture2D<float4> CameraGBufferTexture1;
SamplerState samplerCameraGBufferTexture1;
Texture2D<float4> CameraMotionVectorsTexture;
SamplerState samplerCameraMotionVectorsTexture;
Texture2D<float> CameraDepthTexture;
SamplerState samplerCameraDepthTexture;
Texture2D<float4> ReflectionBuffer;
SamplerState samplerReflectionBuffer;
Texture2D<float2> CostMap;
SamplerState samplerCostMap;
float4 ScreenSize;
float4 ResolveSize;
float3 WorldSpaceCameraPos;
float4x4 ProjectionMatrix;
float4x4 InverseProjectionMatrix;
float4x4 WorldToCameraMatrix;
float4x4 CameraToWorldMatrix;
float4x4 PrevViewProjectionMatrix;
float4x4 ViewProjectionMatrix;
float4x4 InverseViewProjectionMatrix;
float4 RayCastSize;
float4 JitterSizeAndOffset;
float4 ProjectionParams;
float4 ZBufferParams;

cbuffer VS_PROPERTIES_BUFFER
{
    Texture2D<float4> Noise;
    SamplerState samplerNoise;
    float SmoothnessRange;
    float4 NoiseSize;
    float BRDFBias;
    float TResponseMin;
    float TResponseMax;
    float TScale;
    float EdgeFactor;
    float Thickness;
    int NumSteps;
    int MaxMipMap;
    int Normalization;
}

float3 GetViewNormal(float3 normal)
{
    float3 viewNormal = mul((float3x3)WorldToCameraMatrix, normal.rgb);
    return normalize(viewNormal);
}

float4 GetNormal(float2 uv)
{
    float4 gbuffer2 = CameraGBufferTexture2.SampleLevel(samplerCameraGBufferTexture2,uv,0);
    return float4(gbuffer2.rgb * 2 - 1, gbuffer2.a);
}

float4 GetSpecular(float2 uv, float type)
{
    float4 spec = CameraGBufferTexture1.SampleLevel(samplerCameraGBufferTexture1, uv,0);
    return spec;
}

float GetRoughness(float smoothness)
{
    return max(min(SmoothnessRange, 1 - smoothness), 0.05f);
}

float GetDepth(float2 uv, float mip)
{
    return MinDepth.SampleLevel(samplerMinDepth,uv,mip);
}

float GetDepth(float2 uv)
{
    return CameraDepthTexture.SampleLevel(samplerCameraDepthTexture, uv, 0);
}

float3 GetScreenPos(float2 uv, float depth)
{
    return float3(uv * 2 - 1, depth);
}

float3 GetViewPos(float3 screenPos)
{
    float4 viewPos = mul(InverseProjectionMatrix, float4(screenPos, 1));
    return viewPos.xyz / viewPos.w;
}

float2 GetVelocity(float2 uv)
{
    return CameraMotionVectorsTexture.SampleLevel(samplerCameraMotionVectorsTexture, uv, 0).xy;
}

float3 GetWorlPos(float3 screenPos)
{
    float4 worldPos = mul(InverseViewProjectionMatrix, float4(screenPos, 1));
    return worldPos.xyz / worldPos.w;
}

float3 GetViewDir(float3 worldPos)
{
    return normalize(worldPos - WorldSpaceCameraPos);
}

float RayAttenBorder(float2 pos, float value)
{
    float borderDist = min(1.0 - max(pos.x, pos.y), min(pos.x, pos.y));
    return saturate(borderDist > value ? 1.0 : borderDist / value);
}

// Converts color to luminance (grayscale)
inline half Luminance(half3 rgb)
{
    return dot(rgb, unity_ColorSpaceLuminance.rgb);
}

float3 GetViewRay(float2 uv)
{
    float4 _CamScreenDir = float4(1.0 / ProjectionMatrix[0][0], 1.0 / ProjectionMatrix[1][1], 1, 1);
    float3 ray = float3(uv.x * 2 - 1, uv.y * 2 - 1, 1);
    ray *= _CamScreenDir.xyz;
    ray = ray * (ProjectionParams.z / ray.z);
    return ray;
}

float Linear01Depth(float z)
{
    return 1.0 / (ZBufferParams.x * z + ZBufferParams.y);
}

// Z buffer to linear depth
float LinearEyeDepth(float z)
{
    return 1.0 / (ZBufferParams.z * z + ZBufferParams.w);
}

half2 CalculateMotion(float rawDepth, float2 inUV)
{
    float depth = Linear01Depth(rawDepth);
    float3 ray = GetViewRay(inUV);
    float3 vPos = ray * depth;
    float4 worldPos = mul(CameraToWorldMatrix, float4(vPos, 1.0));

    float4 prevClipPos = mul(PrevViewProjectionMatrix, worldPos);
    float4 curClipPos = mul(ViewProjectionMatrix, worldPos);

    float2 prevHPos = prevClipPos.xy / prevClipPos.w;
    float2 curHPos = curClipPos.xy / curClipPos.w;

            // V is the viewport position at this pixel in the range 0 to 1.
    float2 vPosPrev = (prevHPos.xy + 1.0f) / 2.0f;
    float2 vPosCur = (curHPos.xy + 1.0f) / 2.0f;
    return vPosCur - vPosPrev;
}

static const int2 offset[9] =
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