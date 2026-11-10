Shader "Hidden/Stochastic SSR (HDRP)"
{
	HLSLINCLUDE

	#pragma target 4.5

	#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
	#include "Packages/com.simeonradivoev.stochastic-reflections/Resources/Shaders/HDRP/StochasticSSRCommon.hlsl"

	Texture2D<half4> _SRReflectionBuffer;
	Texture2D<half4> _SRRayCast;

	float _SRIntensity;
	int _SRDebugPass;
	int _SRUseFresnel;

	struct Attributes
	{
		uint vertexID : SV_VertexID;
	};

	struct Varyings
	{
		float4 positionCS : SV_POSITION;
		float2 uv : TEXCOORD0;
	};

	Varyings Vert(Attributes input)
	{
		Varyings output;
		output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
		output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
		return output;
	}

	ENDHLSL

	SubShader
	{
		ZTest Always Cull Off ZWrite Off

		// Copies the camera color buffer into a mip mapped texture so the resolve can sample it
		// with a mip bias and so it can be read while the reflections are composited back.
		Pass
		{
			Name "COPY"

			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment FragCopy

			half4 FragCopy(Varyings input) : SV_Target
			{
				return _SRSceneColorSource.SampleLevel(sampler_linear_clamp, float3(input.uv, _SRSliceIndex), 0);
			}
			ENDHLSL
		}

		Pass
		{
			Name "COMBINE"

			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment FragCombine

			float4 FragCombine(Varyings input) : SV_Target
			{
				float2 uv = input.uv;

				half4 sceneColor = _SRSceneColor.SampleLevel(sampler_linear_clamp, uv, 0);
				half4 reflection = _SRReflectionBuffer.SampleLevel(sampler_linear_clamp, uv, 0);

				float rawDepth = SR_GetRawDepth(uv);
				float3 viewPos = SR_GetViewPosition(uv, rawDepth);

				float4 normalAndRoughness = SR_GetNormalAndRoughness(uv);
				float3 viewNormal = SR_GetViewNormal(normalAndRoughness.xyz);

				// The reflection mask is stored in the alpha channel of the resolved reflections.
				float mask = reflection.a * reflection.a * _SRIntensity;

				// HDRP does not expose the specular color, so a dielectric F0 is assumed.
				float3 V = normalize(-viewPos);
				float NdotV = saturate(dot(viewNormal, V));
				float3 fresnel = 0.04 + (1.0 - 0.04) * pow(1.0 - NdotV, 5.0);
				if (_SRUseFresnel == 0)
					fresnel = 1.0;

				float3 result = reflection.rgb * fresnel;

				if (_SRDebugPass == 0)
					sceneColor.rgb += result * mask;
				else if (_SRDebugPass == 1)
					sceneColor.rgb = result * mask;
				else if (_SRDebugPass == 2)
					sceneColor.rgb = mask.xxx;
				else if (_SRDebugPass == 3)
					sceneColor.rgb = _SRRayCast.SampleLevel(sampler_point_clamp, uv, 0).rgb;
				else if (_SRDebugPass == 4)
					sceneColor.rgb = reflection.rgb;
				else if (_SRDebugPass == 5)
					sceneColor.rgb = _SRDepthPyramid.SampleLevel(sampler_point_clamp, uv, 2).xxx;

				return sceneColor;
			}
			ENDHLSL
		}
	}

	Fallback Off
}
