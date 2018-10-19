#define PI            3.14159265359f
#define INV_PI        0.31830988618f

half SmithJointGGXVisibilityTerm(half NdotL, half NdotV, half roughness)
{
// Approximation of the above formulation (simplify the sqrt, not mathematically correct but close enough)
    half a = roughness;
    half lambdaV = NdotL * (NdotV * (1 - a) + a);
    half lambdaL = NdotV * (NdotL * (1 - a) + a);

    return 0.5f / (lambdaV + lambdaL + 1e-5f);
}

half GGXTerm(half NdotH, half roughness)
{
    half a2 = roughness * roughness;
    half d = (NdotH * a2 - NdotH) * NdotH + 1.0f; // 2 mad
    return INV_PI * a2 / (d * d + 1e-7f); // This function is not intended to be running on Mobile,
                                            // therefore epsilon is smaller than what can be represented by half
}

float BRDF_Unity_Weight(float3 V, float3 L, float3 N, float Roughness)
{
    float3 H = normalize(L + V);

    float NdotH = saturate(dot(N, H));
    float NdotL = saturate(dot(N, L));
    float NdotV = saturate(dot(N, V));

    half G = SmithJointGGXVisibilityTerm(NdotL, NdotV, Roughness);
    half D = GGXTerm(NdotH, Roughness);

    return (D * G) * (PI / 4.0);
}

float4 TangentToWorld(float3 N, float4 H)
{
    float3 UpVector = abs(N.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
    float3 T = normalize(cross(UpVector, N));
    float3 B = cross(N, T);
				 
    return float4((T * H.x) + (B * H.y) + (N * H.z), H.w);
}

// Brian Karis, Epic Games "Real Shading in Unreal Engine 4"
float4 ImportanceSampleGGX(float2 Xi, float Roughness)
{
    float m = Roughness * Roughness;
    float m2 = m * m;
		
    float Phi = 2 * PI * Xi.x;
				 
    float CosTheta = sqrt((1.0 - Xi.y) / (1.0 + (m2 - 1.0) * Xi.y));
    float SinTheta = sqrt(max(1e-5, 1.0 - CosTheta * CosTheta));
				 
    float3 H;
    H.x = SinTheta * cos(Phi);
    H.y = SinTheta * sin(Phi);
    H.z = CosTheta;
		
    float d = (CosTheta * m2 - CosTheta) * CosTheta + 1;
    float D = m2 / (PI * d * d);
    float pdf = D * CosTheta;

    return float4(H, pdf);
}