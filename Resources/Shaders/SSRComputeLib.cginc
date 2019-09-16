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
float4x4 ProjectionMatrixStereo[2];
float4x4 InverseProjectionMatrix;
float4x4 InverseProjectionMatrixStereo[2];
float4x4 WorldToCameraMatrix;
float4x4 WorldToCameraMatrixStereo[2];
float4x4 CameraToWorldMatrix;
float4x4 CameraToWorldMatrixStereo[2];
float4x4 PrevViewProjectionMatrix;
float4x4 PrevViewProjectionMatrixStereo[2];
float4x4 ViewProjectionMatrix;
float4x4 ViewProjectionMatrixStereo[2];
float4x4 InverseViewProjectionMatrix;
float4x4 InverseViewProjectionMatrixStereo[2];
float4 RayCastSize;
float4 JitterSizeAndOffset;
float4 ProjectionParams;
float4 ZBufferParams;
bool UseStereo;

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

inline int GetEyeIndex(float2 uv)
{
    return uv.x < 0.5 ? 0 : 1;
}

inline float4x4 GetCameraToWorld(float2 uv)
{
    return UseStereo ? CameraToWorldMatrixStereo[GetEyeIndex(uv)] : CameraToWorldMatrix;
}

inline float4x4 GetWorldToCamera(float2 uv)
{
    return UseStereo ? WorldToCameraMatrixStereo[GetEyeIndex(uv)] : WorldToCameraMatrix;
}

inline float4x4 GetViewProjection(float2 uv)
{
    return UseStereo ? ViewProjectionMatrixStereo[GetEyeIndex(uv)] : ViewProjectionMatrix;
}

inline float4x4 GetPrevViewProjection(float2 uv)
{
    return UseStereo ? PrevViewProjectionMatrixStereo[GetEyeIndex(uv)] : PrevViewProjectionMatrix;
}

inline float4x4 GetProjection(float2 uv)
{
    return UseStereo ? ProjectionMatrixStereo[GetEyeIndex(uv)] : ProjectionMatrix;
}

inline float4x4 GetInverseProjection(float2 uv)
{
    return UseStereo ? InverseProjectionMatrixStereo[GetEyeIndex(uv)] : InverseProjectionMatrix;
}

inline float4x4 GetInverseViewProjection(float2 uv)
{
    return UseStereo ? InverseViewProjectionMatrixStereo[GetEyeIndex(uv)] : InverseViewProjectionMatrix;
}

float3 GetViewNormal(float3 normal,float2 screenUv)
{
    float4x4 worldToCamera = GetWorldToCamera(screenUv);
    float3 viewNormal = mul((float3x3) WorldToCameraMatrix, normal.rgb);
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

float3 GetViewPos(float3 screenPos, float2 screenUv)
{
    float4x4 inverseProjection = GetInverseProjection(screenUv);
    float4 viewPos = mul(inverseProjection, float4(screenPos, 1));
    return viewPos.xyz / viewPos.w;
}

float2 GetVelocity(float2 uv)
{
    return CameraMotionVectorsTexture.SampleLevel(samplerCameraMotionVectorsTexture, uv, 0).xy;
}

float3 GetWorlPos(float3 screenPos, float2 screenUv)
{
    float4x4 inverseViewProjection = GetInverseViewProjection(screenUv);
    float4 worldPos = mul(inverseViewProjection, float4(screenPos, 1));
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
    float4x4 projectionMatrix = GetProjection(uv);
    float4 _CamScreenDir = float4(1.0 / projectionMatrix[0][0], 1.0 / projectionMatrix[1][1], 1, 1);
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
    bool useStereo = UseStereo;
    float4x4 cameraToWorld = GetCameraToWorld(inUV);
    float4x4 viewProjection = GetViewProjection(inUV);
    float4x4 prevViewProjection = GetPrevViewProjection(inUV);

    float depth = Linear01Depth(rawDepth);
    float3 ray = GetViewRay(inUV);
    float3 vPos = ray * depth;
    float4 worldPos = mul(cameraToWorld, float4(vPos, 1.0));

    float4 prevClipPos = mul(prevViewProjection, worldPos);
    float4 curClipPos = mul(viewProjection, worldPos);

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