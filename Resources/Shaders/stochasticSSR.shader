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

Shader "Hidden/Stochastic SSR" 
{
	CGINCLUDE
	
	#include "UnityCG.cginc"
	#include "UnityPBSLighting.cginc"
    #include "UnityStandardBRDF.cginc"
    #include "UnityStandardUtils.cginc"

	#define REDUCE_FIREFLIES

	#include "SSRLib.cginc"
	#include "SSRBlur.cginc"

	#include "BRDFLib.cginc"

	struct VertexInput 
	{
		float4 vertex : POSITION;
		float2 texcoord : TEXCOORD0;
	};

	struct VertexOutput
	{
		float2 uv : TEXCOORD0;
		float4 vertex : SV_POSITION;
	};

	VertexOutput vert( VertexInput v ) 
	{
		VertexOutput o;
		o.vertex = UnityObjectToClipPos(v.vertex);
		o.uv = v.texcoord;
		return o;
	}

	VertexOutput vertTriangle( VertexInput v ) 
	{
		VertexOutput o;
		o.vertex = float4(v.vertex.xy, 0.0, 1.0);
		o.uv = (v.vertex.xy + 1.0) * 0.5;
		#if UNITY_UV_STARTS_AT_TOP
            o.uv.y = 1.0 - o.uv.y;
		#endif
		return o;
	}

	void costBuild(VertexOutput i,out float2 cost : SV_Target)
	{
		float2 uv = i.uv;
		float depth = GetDepth(_CameraDepthTexture, uv);
		float4 diffuse =  GetAlbedo(uv);
		float4 worldNormal = GetNormal (uv);
		float3 screenPos = GetScreenPos(uv, depth);
		float3 worldPos = GetWorlPos(screenPos);
		float4 specular = GetSpecular (uv,worldNormal.a);
		float smoothness = specular.a;
		float occlusion = diffuse.a;
		float skybox = (depth > 0.0001);
		float3 viewDir = GetViewDir(worldPos);
		float NdotV = saturate(dot(worldNormal, -viewDir));
		float3 reflDir = normalize( reflect( -viewDir, worldNormal ) );
		float fade = 1-saturate(dot(viewDir, reflDir) + 0.2);

		cost.r = (((1-smoothness) * occlusion * skybox) > 0.5) * fade;
		cost.g = occlusion * skybox * fade;
	}

	half4 blit(VertexOutput i) : SV_Target
	{
		return tex2Dlod(_BlitInput,float4(i.uv,0,_MipLevel));
	}

	half4 copyDepth(VertexOutput i) : SV_Target
	{
		return tex2Dlod(_CameraDepthTexture,float4(i.uv,0,0));
	}

	float4 removeCubemap( VertexOutput i ) : SV_Target
	{	 
		float2 uv = i.uv;

		float4 cubemap = GetCubeMap (uv);

		float4 sceneColor = tex2D(_MainTex,  uv);
		sceneColor.rgb = max(1e-5, sceneColor.rgb - cubemap.rgb);

		return sceneColor;
	}

	float4 recursive( VertexOutput i ) : SV_Target
	{	 
		float2 uv = i.uv;

		float depth = GetDepth(_CameraDepthTexture, uv);
		float3 screenPos = GetScreenPos(uv, depth);
		float2 velocity = GetVelocity(uv); // 5.4 motion vector

		float2 prevUV = uv - velocity;

		float4 cubemap = GetCubeMap (uv);

		float4 sceneColor = tex2D(_MainTex,  prevUV);

		return sceneColor;
	}

	float4 combine( VertexOutput i ) : SV_Target
	{	 
		float2 uv = i.uv;

		float depth = GetDepth(_CameraDepthTexture, uv);
		float3 screenPos = GetScreenPos(uv, depth);
		float3 worldPos = GetWorlPos(screenPos);

		float3 cubemap = GetCubeMap (uv);
		float4 worldNormal = GetNormal (uv);

		float4 diffuse =  GetAlbedo(uv);
		float occlusion = diffuse.a;
		float4 specular = GetSpecular (uv,worldNormal.a);
		float smoothness = specular.a;

		float4 sceneColor = tex2D(_MainTex,  uv);
		sceneColor.rgb = max(1e-5, sceneColor.rgb - (cubemap.rgb * occlusion));

		float4 reflection = GetSampleColor(_ReflectionBuffer, uv);

		float3 viewDir = GetViewDir(worldPos);
		float NdotV = saturate(dot(worldNormal, -viewDir));

		float3 reflDir = normalize( reflect( -viewDir, worldNormal ) );
		float fade = saturate(dot(-viewDir, reflDir) * 2.0);
		float mask = sqr(reflection.a) /* fade */;

		float oneMinusReflectivity;
		diffuse.rgb = EnergyConservationBetweenDiffuseAndSpecular(diffuse, specular.rgb, oneMinusReflectivity);

        UnityLight light;
        light.color = 0;
        light.dir = 0;
        light.ndotl = 0;

        UnityIndirect ind;
        ind.diffuse = 0;
        ind.specular = reflection;

		if(_UseFresnel == 1)													
			reflection.rgb = UNITY_BRDF_PBS (diffuse.rgb, specular.rgb, oneMinusReflectivity, smoothness, worldNormal, -viewDir, light, ind).rgb;

		reflection.rgb *= occlusion;

		if(_DebugPass == 0)
			sceneColor.rgb += lerp(cubemap.rgb * occlusion, reflection.rgb, mask * _Intensity); // Combine reflection and cubemap and add it to the scene color 
		else if(_DebugPass == 1)
			sceneColor.rgb = reflection.rgb * mask;
		else if(_DebugPass == 2)
			sceneColor.rgb = cubemap;
		else if(_DebugPass == 3)
			sceneColor.rgb = lerp(cubemap.rgb, reflection.rgb, mask);
		else if(_DebugPass == 4)
			sceneColor = mask;
		else if(_DebugPass == 5)
			sceneColor.rgb += lerp(0.0, reflection.rgb, mask);
		else if(_DebugPass == 6)
			sceneColor.rgb = GetSampleColor(_RayCast, uv);
		else if(_DebugPass == 7)
			sceneColor.rgb = GetSampleColor(_CostMap, uv).r;
		else if(_DebugPass == 8)
			sceneColor.rgb = tex2Dlod(_MinDepth, float4(uv,0,2)).r;
		else if(_DebugPass == 9)
			sceneColor.rgb = reflection.rgb;

		return sceneColor;
	}

	ENDCG 
	
	SubShader 
	{
		ZTest Always Cull Off ZWrite Off

		Pass 
		{
			Name "REMOVE_CUBEMAP"
			CGPROGRAM
			#pragma target 3.0

			#ifdef SHADER_API_OPENGL
       			#pragma glsl
    		#endif

			#pragma exclude_renderers nomrt xbox360 ps3 xbox360 ps3

			#pragma vertex vertTriangle
			#pragma fragment removeCubemap
			ENDCG
		}
		Pass 
		{
			Name "COMBINE"

			CGPROGRAM
			#pragma target 3.0

			#ifdef SHADER_API_OPENGL
       			#pragma glsl
    		#endif

			#pragma exclude_renderers nomrt xbox360 ps3 xbox360 ps3

			#pragma vertex vertTriangle
			#pragma fragment combine
			ENDCG
		}
		Pass 
		{
			Name "RECURSIVE"
			CGPROGRAM
			#pragma target 3.0

			#ifdef SHADER_API_OPENGL
       			#pragma glsl
    		#endif

			#pragma exclude_renderers nomrt xbox360 ps3 xbox360 ps3

			#pragma vertex vertTriangle
			#pragma fragment recursive
			ENDCG
		}
		Pass 
		{
			Name "BLIT"
			CGPROGRAM
			#pragma target 3.0

			#ifdef SHADER_API_OPENGL
       			#pragma glsl
    		#endif

			#pragma exclude_renderers nomrt xbox360 ps3 xbox360 ps3

			#pragma vertex vertTriangle
			#pragma fragment blit
			ENDCG
		}
		Pass 
		{
			Name "COPY_DEPTH"
			CGPROGRAM
			#pragma target 3.0

			#ifdef SHADER_API_OPENGL
       			#pragma glsl
    		#endif

			#pragma exclude_renderers nomrt xbox360 ps3 xbox360 ps3

			#pragma vertex vertTriangle
			#pragma fragment copyDepth
			ENDCG
		}
		Pass 
		{
			Name "COST_MAP"
			CGPROGRAM
			#pragma target 3.0

			#ifdef SHADER_API_OPENGL
       			#pragma glsl
    		#endif

			#pragma exclude_renderers nomrt xbox360 ps3 xbox360 ps3

			#pragma vertex vertTriangle
			#pragma fragment costBuild
			ENDCG
		}
	}
	Fallback Off
}
