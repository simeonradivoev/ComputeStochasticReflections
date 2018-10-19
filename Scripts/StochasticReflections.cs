using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using Object = UnityEngine.Object;

namespace Trive.Rendering
{
	[System.Serializable]
	public enum SSRDebugPass
	{
		Combine,
		Reflection,
		Cubemap,
		ReflectionAndCubemap,
		SSRMask,
		CombineNoCubemap,
		RayCast,
		CostMap,
		Depth,
		Resolve
	}

	[Serializable]
	public class DebugModeParameter : ParameterOverride<SSRDebugPass>
	{
	}

	[Serializable]
	[PostProcess(typeof(StochasticReflectionsRenderer), PostProcessEvent.BeforeTransparent, "Custom/Stochastic Screen Space Reflections")]
	public class StochasticReflections : PostProcessEffectSettings
	{
		[Range(0,1)] public FloatParameter intensity = new FloatParameter(){value = 0};
		public BoolParameter raycastDownsample = new BoolParameter(){value = false};
		public BoolParameter resolveDownsample = new BoolParameter(){value = false};
		[Range(0, 100)] public IntParameter rayDistance = new IntParameter() {value = 70};
		public FloatParameter thickness = new FloatParameter() {value = 0.2f};
		public FloatParameter screenFadeSize = new FloatParameter() {value = 0.25f};
		public BoolParameter useFresnel = new BoolParameter() {value = true};
		[Range(0, 1)] public FloatParameter BRDFBias = new FloatParameter() {value = 0.7f};
		[Header("Resolve")] public BoolParameter useMipMap = new BoolParameter() {value = true};
		public BoolParameter normalization = new BoolParameter() {value = true};
		public BoolParameter blurring = new BoolParameter() {value = true};
		public BoolParameter highQualityBlur = new BoolParameter() {value = true};
		[Header("Temporal (play mode only)")] public BoolParameter useTemporal = new BoolParameter() {value = true};
		public BoolParameter multipleBounces = new BoolParameter() {value = true};
		[Range(0.0f, 1.0f)] public FloatParameter temporalResponseMin = new FloatParameter() {value = 0.85f};
		[Range(0.0f, 1.0f)] public FloatParameter temporalResponseMax = new FloatParameter() {value = 1f};
		[Header("Debug"), Range(0, 1)] public FloatParameter smoothnessRange = new FloatParameter() {value = 1};
		public DebugModeParameter debugPass = new DebugModeParameter() {value = SSRDebugPass.Combine};

		public override bool IsEnabledAndSupported(PostProcessRenderContext context)
		{
			return enabled
				   && intensity.value > 0
				   && rayDistance.value > 0 
			       && screenFadeSize.value < 1 
			       && context.camera.actualRenderingPath == RenderingPath.DeferredShading
			       && SystemInfo.supportsMotionVectors
			       && SystemInfo.supportsComputeShaders
			       && SystemInfo.copyTextureSupport > CopyTextureSupport.None;
		}
	}

	public class StochasticReflectionsRenderer : PostProcessEffectRenderer<StochasticReflections>
	{
		public const int MAX_MIN_Z_LEVELS = 7;
		public const int KERNEL_SIZE = 16;

		private static class ComputeUniforms
		{
			public static int Noise = Shader.PropertyToID("Noise");
			public static int NoiseSize = Shader.PropertyToID("NoiseSize");
			public static int BDRFBias = Shader.PropertyToID("BRDFBias");
			public static int NumSteps = Shader.PropertyToID("NumSteps");
			public static int Thickness = Shader.PropertyToID("Thickness");
			public static int JitterSizeAndOffset = Shader.PropertyToID("JitterSizeAndOffset");
			public static int RayCastSize = Shader.PropertyToID("RayCastSize");
			public static int SmoothnessRange = Shader.PropertyToID("SmoothnessRange");
			public static int WorldToCameraMatrix = Shader.PropertyToID("WorldToCameraMatrix");
			public static int InverseProjectionMatrix = Shader.PropertyToID("InverseProjectionMatrix");
			public static int ProjectionMatrix = Shader.PropertyToID("ProjectionMatrix");
			public static int ScreenSize = Shader.PropertyToID("ScreenSize");
			public static int ResolveSize = Shader.PropertyToID("ResolveSize");
			public static int MinDepth = Shader.PropertyToID("MinDepth");
			public static int CameraGBufferTexture2 = Shader.PropertyToID("CameraGBufferTexture2");
			public static int CameraGBufferTexture1 = Shader.PropertyToID("CameraGBufferTexture1");
			public static int RaycastResult = Shader.PropertyToID("RaycastResult");
			public static int MaskResult = Shader.PropertyToID("MaskResult");
			public static int WorldSpaceCameraPos = Shader.PropertyToID("WorldSpaceCameraPos");
			public static int CameraMotionVectorsTexture = Shader.PropertyToID("CameraMotionVectorsTexture");
			public static int ResolveResult = Shader.PropertyToID("ResolveResult");
			public static int MaxMipMap = Shader.PropertyToID("MaxMipMap");
			public static int EdgeFactor = Shader.PropertyToID("EdgeFactor");
			public static int ScreenInput = Shader.PropertyToID("ScreenInput");
			public static int RaycastInput = Shader.PropertyToID("RaycastInput");
			public static int MaskInput = Shader.PropertyToID("MaskInput");
			public static int PrevViewProjectionMatrix = Shader.PropertyToID("PrevViewProjectionMatrix");
			public static int TResponseMin = Shader.PropertyToID("TResponseMin");
			public static int TResponseMax = Shader.PropertyToID("TResponseMax");
			public static int CameraToWorldMatrix = Shader.PropertyToID("CameraToWorldMatrix");
			public static int ViewProjectionMatrix = Shader.PropertyToID("ViewProjectionMatrix");
			public static int ProjectionParams = Shader.PropertyToID("ProjectionParams");
			public static int ZBufferParams = Shader.PropertyToID("ZBufferParams");
			public static int PreviousTemporalInput = Shader.PropertyToID("PreviousTemporalInput");
			public static int TemporalResult = Shader.PropertyToID("TemporalResult");
			public static int CameraDepthTexture = Shader.PropertyToID("CameraDepthTexture");
			public static int InverseViewProjectionMatrix = Shader.PropertyToID("InverseViewProjectionMatrix");
			public static int Source = Shader.PropertyToID("_Source");
			public static int Result = Shader.PropertyToID("_Result");
			public static int Size = Shader.PropertyToID("_Size");
			public static int TemporalBuffet0 = Shader.PropertyToID("temporalBuffer0");
			public static int BlurResult = Shader.PropertyToID("Result");
			public static int CostMap = Shader.PropertyToID("CostMap");
			public static int Normalization = Shader.PropertyToID("Normalization");
		}

		public static class Uniforms
		{
			public static int UseFresnel = Shader.PropertyToID("_UseFresnel");
			public static int DebugPass = Shader.PropertyToID("_DebugPass");
			public static int InverseViewProjectionMatrix = Shader.PropertyToID("_InverseViewProjectionMatrix");
			public static int WorldToCameraMatrix = Shader.PropertyToID("_WorldToCameraMatrix");
			public static int JitterSizeAndOffset = Shader.PropertyToID("_JitterSizeAndOffset");
			public static int ScreenSize = Shader.PropertyToID("_ScreenSize");
			public static int Raycast = Shader.PropertyToID("_RayCast");
			public static int RaycastMask = Shader.PropertyToID("_RayCastMask");
			public static int ReflectionBuffer = Shader.PropertyToID("_ReflectionBuffer");
			public static int PreviousBuffer = Shader.PropertyToID("_PreviousBuffer");
			public static int MinZStride = Shader.PropertyToID("_MinZStride");
			public static int MainTex = Shader.PropertyToID("_MainTex");
			public static int MipMapInput = Shader.PropertyToID("_MipMapInput");
			public static int BlitInput = Shader.PropertyToID("_BlitInput");
			public static int DepthPyramidInput = Shader.PropertyToID("_DepthPyramidInput");
			public static int MipLevel = Shader.PropertyToID("_MipLevel");
			public static int MipMapCount = Shader.PropertyToID("_MipMapCount");
			public static int GaussianDir = Shader.PropertyToID("_GaussianDir");
			public static int MaskBlur = Shader.PropertyToID("_MaskBlur");
			public static int CameraGBufferTexture2 = Shader.PropertyToID("_CameraGBufferTexture2");
			public static int CameraGBufferTexture1 = Shader.PropertyToID("_CameraGBufferTexture1");
			public static int CameraMotionVectorsTexture = Shader.PropertyToID("_CameraMotionVectorsTexture");
			public static int CameraDepthTexture = Shader.PropertyToID("_CameraDepthTexture");
			public static int UnityCameraToWorld = Shader.PropertyToID("unity_CameraToWorld");
			public static int ZBufferParams = Shader.PropertyToID("_ZBufferParams");
			public static int ProjectionParams = Shader.PropertyToID("_ProjectionParams");
			public static int Intensity = Shader.PropertyToID("_Intensity");
			public static int CostMap = Shader.PropertyToID("_CostMap");
			public static int MinDepth = Shader.PropertyToID("_MinDepth");
		}

		private RenderTexture recursiveTex;
		private RenderTexture mainBuffer;
		private RenderTexture temporalBuffer;
		private Matrix4x4 prevViewProjectionMatrix;
		private ComputeShader computeShader;
		private ComputeShader blurShader;
		private ComputeShader depthPyramidShader;
		private Texture2D noise;
		private int combinePass;
		private int blitPass;
		private int recusrsivePass;
		private int copyDepthPass;
		private int removeCubemapPass;
		private int costMapPass;
		private int raycastKernel;
		private int resolveKernel;
		private int temporalKernel;
		private int[] mipIDs;
		private int[] depthIds;

		public override void Init()
		{
			noise = Resources.Load("tex_BlueNoise_1024x1024_UNI") as Texture2D;
			computeShader = Resources.Load<ComputeShader>("Shaders/RayTraceCompute");
			blurShader = Resources.Load<ComputeShader>("Shaders/MedianBlur");
			depthPyramidShader = Resources.Load<ComputeShader>("Shaders/DepthPyramid");
			raycastKernel = computeShader.FindKernel("CSRaycast");
			resolveKernel = computeShader.FindKernel("CSResolve");
			temporalKernel = computeShader.FindKernel("CSTemporal");
			FindPasses();

			// Pre-cache mipmaps ids
			if (mipIDs == null || mipIDs.Length == 0)
			{
				mipIDs = new int[12];

				for (int i = 0; i < 12; i++)
					mipIDs[i] = Shader.PropertyToID("_SSSRGaussianMip" + i);
			}

			// Pre-cache depth ids
			if (depthIds == null || depthIds.Length == 0)
			{
				depthIds = new int[12];

				for (int i = 0; i < 12; i++)
					depthIds[i] = Shader.PropertyToID("_SSSRDepthMip" + i);
			}
		}

		private void FindPasses()
		{
			Material material = new Material(Shader.Find("Hidden/Stochastic SSR"));
			combinePass = material.FindPass("COMBINE");
			blitPass = material.FindPass("BLIT");
			recusrsivePass = material.FindPass("RECURSIVE");
			copyDepthPass = material.FindPass("COPY_DEPTH");
			removeCubemapPass = material.FindPass("REMOVE_CUBEMAP");
			costMapPass = material.FindPass("COST_MAP");
			Object.DestroyImmediate(material);
		}

		private void UpdateParameters(PostProcessRenderContext context,PropertySheet sheet)
		{
			context.command.SetComputeTextureParam(computeShader, raycastKernel, ComputeUniforms.Noise, noise);
			context.command.SetComputeTextureParam(computeShader, resolveKernel, ComputeUniforms.Noise, noise);
			context.command.SetComputeVectorParam(computeShader, ComputeUniforms.NoiseSize, new Vector4(noise.width, noise.height, 1.0f / noise.width, 1.0f / noise.height));
			context.command.SetComputeFloatParam(computeShader, ComputeUniforms.SmoothnessRange, settings.smoothnessRange);
			context.command.SetComputeIntParam(computeShader, ComputeUniforms.NumSteps, settings.rayDistance);
			context.command.SetComputeFloatParam(computeShader, ComputeUniforms.Thickness, settings.thickness);
			context.command.SetComputeFloatParam(computeShader, ComputeUniforms.BDRFBias, settings.BRDFBias);
			context.command.SetComputeFloatParam(computeShader, ComputeUniforms.EdgeFactor, settings.screenFadeSize);
			context.command.SetComputeFloatParam(computeShader, ComputeUniforms.EdgeFactor, settings.screenFadeSize);
			context.command.SetComputeFloatParam(computeShader, ComputeUniforms.TResponseMin, settings.temporalResponseMin);
			context.command.SetComputeFloatParam(computeShader, ComputeUniforms.TResponseMax, settings.temporalResponseMax);
			context.command.SetComputeIntParam(computeShader,ComputeUniforms.Normalization,settings.normalization ? 1 : 0);

			sheet.properties.SetInt(Uniforms.UseFresnel, settings.useFresnel ? 1 : 0);
			sheet.properties.SetInt(Uniforms.MaskBlur, settings.blurring ? 1 : 0);
			sheet.properties.SetInt(Uniforms.DebugPass, (int) settings.debugPass.value);
			sheet.properties.SetFloat(Uniforms.Intensity, settings.intensity.value);
		}

		private Matrix4x4 UpdateMatrices(PostProcessRenderContext context, PropertySheet sheet)
		{
			var worldToCameraMatrix = context.camera.worldToCameraMatrix;
			var cameraToWorldMatrix = context.camera.cameraToWorldMatrix;

			cameraToWorldMatrix.m02 *= -1;
			cameraToWorldMatrix.m12 *= -1;
			cameraToWorldMatrix.m22 *= -1;

			var projectionMatrix = GL.GetGPUProjectionMatrix(context.camera.projectionMatrix, false);

			var viewProjectionMatrix = projectionMatrix * worldToCameraMatrix;
			var inverseViewProjectionMatrix = Matrix4x4.Inverse(viewProjectionMatrix);

			sheet.properties.SetMatrix(Uniforms.WorldToCameraMatrix, worldToCameraMatrix);
			sheet.properties.SetMatrix(Uniforms.InverseViewProjectionMatrix, inverseViewProjectionMatrix);
			sheet.properties.SetVector(Uniforms.ScreenSize, new Vector4(context.width, context.height, 1.0f / context.width, 1.0f / context.height));

			context.command.SetComputeMatrixParam(computeShader, ComputeUniforms.WorldToCameraMatrix, worldToCameraMatrix);
			context.command.SetComputeMatrixParam(computeShader, ComputeUniforms.ProjectionMatrix, projectionMatrix);
			context.command.SetComputeMatrixParam(computeShader, ComputeUniforms.InverseProjectionMatrix, Matrix4x4.Inverse(projectionMatrix));
			context.command.SetComputeMatrixParam(computeShader, ComputeUniforms.InverseViewProjectionMatrix, inverseViewProjectionMatrix);
			context.command.SetComputeVectorParam(computeShader, ComputeUniforms.WorldSpaceCameraPos, context.camera.transform.position);
			context.command.SetComputeMatrixParam(computeShader, ComputeUniforms.CameraToWorldMatrix, cameraToWorldMatrix);
			context.command.SetComputeMatrixParam(computeShader, ComputeUniforms.ViewProjectionMatrix, viewProjectionMatrix);
			context.command.SetComputeMatrixParam(computeShader, ComputeUniforms.PrevViewProjectionMatrix, prevViewProjectionMatrix);

			return viewProjectionMatrix;
		}

		private void CreateTextures(PostProcessRenderContext context)
		{
			if (recursiveTex == null || context.width != recursiveTex.width || context.height != recursiveTex.height)
			{
				if (recursiveTex != null) Object.DestroyImmediate(recursiveTex);
				recursiveTex = new RenderTexture(context.width, context.height, 0, RenderTextureFormat.DefaultHDR);
				recursiveTex.Create();
			}

			int powerOfTwoSize = Mathf.ClosestPowerOfTwo(Mathf.Min(context.width, context.height));
			if (mainBuffer == null || mainBuffer.width != context.width || mainBuffer.height != context.height)
			{
				if (mainBuffer != null) Object.DestroyImmediate(mainBuffer);
				mainBuffer = new RenderTexture(context.width, context.height, 0, RenderTextureFormat.DefaultHDR)
				{
					useMipMap = true,
					autoGenerateMips = false
				};
				mainBuffer.Create();
			}
			Vector2Int resolveSize = new Vector2Int(context.width,context.height);
			if (settings.resolveDownsample)
			{
				resolveSize.x /= 2;
				resolveSize.y /= 2;
			}
			if (temporalBuffer == null || temporalBuffer.width != resolveSize.x || temporalBuffer.height != resolveSize.y)
			{
				if (temporalBuffer != null) Object.DestroyImmediate(temporalBuffer);
				temporalBuffer = new RenderTexture(new RenderTextureDescriptor(resolveSize.x, resolveSize.y, RenderTextureFormat.DefaultHDR, 0) {enableRandomWrite = true});
				temporalBuffer.Create();
			}
		}

		private void CleanTextures()
		{
			if (recursiveTex != null) Object.DestroyImmediate(recursiveTex);
			if (mainBuffer != null) Object.DestroyImmediate(mainBuffer);
			if (temporalBuffer != null) Object.DestroyImmediate(temporalBuffer);
		}

		public override void Render(PostProcessRenderContext context)
		{
			context.command.BeginSample("Stochastic Reflection");

			var sheet = context.propertySheets.Get("Hidden/Stochastic SSR");

			UpdateParameters(context,sheet);
			var viewProjectionMatrix = UpdateMatrices(context, sheet);
			CreateTextures(context);

			Vector2Int costMapSize = new Vector2Int(context.width,context.height);
			Vector2Int raycastSize = new Vector2Int(context.width, context.height);
			Vector2Int resolveSize = new Vector2Int(context.width,context.height);
			if (settings.raycastDownsample)
			{
				raycastSize.x /= 2;
				raycastSize.y /= 2;
			}

			if (settings.resolveDownsample)
			{
				resolveSize.x /= 2;
				resolveSize.y /= 2;
			}

			context.command.SetComputeVectorParam(computeShader, ComputeUniforms.ProjectionParams, CalculateProjectionParams(context.camera));
			context.command.SetComputeVectorParam(computeShader, ComputeUniforms.ZBufferParams, CalculateZBufferParams(context.camera));
			context.command.SetComputeVectorParam(computeShader, ComputeUniforms.ScreenSize, new Vector4(context.width, context.height, 1.0f / context.width, 1.0f / context.height));
			context.command.SetComputeVectorParam(computeShader, ComputeUniforms.RayCastSize, new Vector4(raycastSize.x, raycastSize.y, 1.0f / raycastSize.x, 1.0f / raycastSize.y));
			context.command.SetComputeVectorParam(computeShader, ComputeUniforms.ResolveSize, new Vector4(resolveSize.x, resolveSize.y, 1.0f / resolveSize.x, 1.0f / resolveSize.y));

			Vector2 jitterSample = GenerateRandomOffset();
			context.command.SetComputeVectorParam(computeShader, ComputeUniforms.JitterSizeAndOffset, new Vector4
			(
				(float) context.width / (float) noise.width,
				(float) context.height / (float) noise.height,
				jitterSample.x,
				jitterSample.y
			));

			var costMap = ComputeUniforms.CostMap;
			var costMapDesc = new RenderTextureDescriptor(costMapSize.x, costMapSize.y,RenderTextureFormat.RG16,0);

			context.command.BeginSample("Stochastic Reflection Cost Map");
			context.command.GetTemporaryRT(costMap,costMapDesc);
			context.command.BlitFullscreenTriangle(context.source,costMap, sheet, costMapPass);
			context.command.SetGlobalTexture(Uniforms.CostMap,costMap);
			context.command.EndSample("Stochastic Reflection Cost Map");

			//var visibilityTex0 = Shader.PropertyToID("DepthPyramid0");
			var visibilityTex1 = Shader.PropertyToID("DepthPyramid1");
			var visiblityDesc = new RenderTextureDescriptor(context.width, context.height, RenderTextureFormat.RHalf, 0)
			{
				useMipMap = true,
				autoGenerateMips = false
			};

			context.command.BeginSample("Stochastic Reflection Depth Pyramid");
			context.command.GetTemporaryRT(visibilityTex1, visiblityDesc, FilterMode.Point);
			//copy depth inf first mip level
			context.command.BlitFullscreenTriangle(BuiltinRenderTextureType.None, visibilityTex1, sheet, copyDepthPass);
			var lastDepthPyramid = new RenderTargetIdentifier(visibilityTex1);
			Vector2Int DepthPyramidSize = new Vector2Int(visiblityDesc.width, visiblityDesc.height);
			Vector2Int LastDepthPyramidSize = new Vector2Int(visiblityDesc.width, visiblityDesc.height);

			for (int i = 0; i < MAX_MIN_Z_LEVELS; i++)
			{
				DepthPyramidSize.x /= 2;
				DepthPyramidSize.y /= 2;

				context.command.GetTemporaryRT(depthIds[i], DepthPyramidSize.x, DepthPyramidSize.y, 0, FilterMode.Point, RenderTextureFormat.RHalf, RenderTextureReadWrite.Default, 1, true);
				context.command.SetComputeTextureParam(depthPyramidShader, 0, ComputeUniforms.Source, lastDepthPyramid);
				context.command.SetComputeTextureParam(depthPyramidShader, 0, ComputeUniforms.Result, depthIds[i]);
				context.command.SetComputeVectorParam(depthPyramidShader, ComputeUniforms.Size, new Vector4(1f / DepthPyramidSize.x,1f / DepthPyramidSize.y,1f / LastDepthPyramidSize.x, 1f / LastDepthPyramidSize.y));
				context.command.DispatchCompute(depthPyramidShader, 0, Mathf.CeilToInt(DepthPyramidSize.x / 8f), Mathf.CeilToInt(DepthPyramidSize.y / 8f), 1);
				context.command.CopyTexture(depthIds[i], 0, 0, visibilityTex1, 0, i + 1);

				lastDepthPyramid = depthIds[i];
				LastDepthPyramidSize = DepthPyramidSize;
			}

			for (int i = 0; i < MAX_MIN_Z_LEVELS; i++)
				context.command.ReleaseTemporaryRT(mipIDs[i]);

			context.command.SetGlobalTexture(Uniforms.MinDepth, visibilityTex1);

			//context.command.ReleaseTemporaryRT(visibilityTex0);
			context.command.EndSample("Stochastic Reflection Depth Pyramid");

			switch (settings.debugPass.value)
			{
				case SSRDebugPass.Reflection:
				case SSRDebugPass.Cubemap:
				case SSRDebugPass.CombineNoCubemap:
				case SSRDebugPass.RayCast:
				case SSRDebugPass.ReflectionAndCubemap:
				case SSRDebugPass.SSRMask:
				case SSRDebugPass.CostMap:
				case SSRDebugPass.Depth:
				case SSRDebugPass.Resolve:
					context.command.BlitFullscreenTriangle(context.source, mainBuffer, sheet, removeCubemapPass);
					break;
				case SSRDebugPass.Combine:
					if (Application.isPlaying && settings.multipleBounces && !context.isSceneView)
					{
						context.command.BlitFullscreenTriangle(mainBuffer, recursiveTex, sheet, recusrsivePass);
						context.command.BlitFullscreenTriangle(recursiveTex, mainBuffer);
					}
					else
					{
						context.command.BlitFullscreenTriangle(context.source, mainBuffer);
					}

					break;
			}

			context.command.BeginSample("Stochastic Reflection Raycasting");
			var raycastTex = Uniforms.Raycast;
			var raycastDesc = new RenderTextureDescriptor(raycastSize.x, raycastSize.y, RenderTextureFormat.ARGBHalf, 0)
			{
				enableRandomWrite = true
			};
			var raycastMaskTex = Uniforms.RaycastMask;
			var raycastMaskDesc = new RenderTextureDescriptor(raycastSize.x, raycastSize.y, RenderTextureFormat.RHalf, 0)
			{
				enableRandomWrite = true
			};
			context.command.GetTemporaryRT(raycastTex, raycastDesc,FilterMode.Point);
			context.command.GetTemporaryRT(raycastMaskTex, raycastMaskDesc, FilterMode.Point);

			context.command.SetGlobalTexture(Uniforms.Raycast, raycastTex);
			context.command.SetGlobalTexture(Uniforms.RaycastMask, raycastMaskTex);

			context.command.SetComputeTextureParam(computeShader, raycastKernel, ComputeUniforms.MinDepth, visibilityTex1);
			context.command.SetComputeTextureParam(computeShader, raycastKernel, ComputeUniforms.CostMap, costMap);
			context.command.SetComputeTextureParam(computeShader, raycastKernel, ComputeUniforms.CameraGBufferTexture1, BuiltinRenderTextureType.GBuffer1);
			context.command.SetComputeTextureParam(computeShader, raycastKernel, ComputeUniforms.CameraGBufferTexture2, BuiltinRenderTextureType.GBuffer2);
			context.command.SetComputeTextureParam(computeShader, raycastKernel, ComputeUniforms.MaskResult, raycastMaskTex);
			context.command.SetComputeTextureParam(computeShader, raycastKernel, ComputeUniforms.RaycastResult, raycastTex);
			context.command.DispatchCompute(computeShader, raycastKernel, Mathf.CeilToInt((float)raycastSize.x / KERNEL_SIZE),Mathf.CeilToInt((float)raycastSize.y / KERNEL_SIZE), 1);
			context.command.EndSample("Stochastic Reflection Raycasting");

			const int kMaxLods = 12;
			int lodCount = Mathf.FloorToInt(Mathf.Log(mainBuffer.width, 2f) - 3f);
			lodCount = Mathf.Min(lodCount, kMaxLods);

			if (settings.useMipMap)
			{
				context.command.BeginSample("Stochastic Reflection Color Pyramid");
				var compute = context.resources.computeShaders.gaussianDownsample;
				int kernel = compute.FindKernel("KMain");

				var last = new RenderTargetIdentifier(mainBuffer);
				Vector2Int mipSize = new Vector2Int(mainBuffer.width, mainBuffer.height);

				for (int i = 0; i < lodCount; i++)
				{
					mipSize.x >>= 1;
					mipSize.y >>= 1;

					context.command.GetTemporaryRT(mipIDs[i], mipSize.x, mipSize.y, 0, FilterMode.Bilinear, context.sourceFormat, RenderTextureReadWrite.Default, 1, true);
					context.command.SetComputeTextureParam(compute, kernel, ComputeUniforms.Source, last);
					context.command.SetComputeTextureParam(compute, kernel, ComputeUniforms.Result, mipIDs[i]);
					context.command.SetComputeVectorParam(compute,ComputeUniforms.Size, new Vector4(mipSize.x, mipSize.y, 1f / mipSize.x, 1f / mipSize.y));
					context.command.DispatchCompute(compute, kernel, Mathf.CeilToInt(mipSize.x / 8f), Mathf.CeilToInt(mipSize.y / 8f), 1);
					context.command.CopyTexture(mipIDs[i], 0, 0, mainBuffer, 0, i + 1);

					last = mipIDs[i];
				}

				for (int i = 0; i < lodCount; i++)
					context.command.ReleaseTemporaryRT(mipIDs[i]);


				context.command.EndSample("Stochastic Reflection Color Pyramid");
			}

			context.command.BeginSample("Stochastic Reflection Resolve");

			//resolve
			var resolvePassTex = ComputeUniforms.ResolveResult;
			var resolveTexDesc = new RenderTextureDescriptor(resolveSize.x, resolveSize.y, RenderTextureFormat.DefaultHDR, 0)
			{
				enableRandomWrite = true
			};
			context.command.GetTemporaryRT(resolvePassTex, resolveTexDesc);
			context.command.SetGlobalTexture(Uniforms.ReflectionBuffer, resolvePassTex);

			context.command.SetComputeIntParam(computeShader, ComputeUniforms.MaxMipMap, settings.useMipMap ? lodCount : 1);
			context.command.SetComputeTextureParam(computeShader, resolveKernel, ComputeUniforms.CameraGBufferTexture1, BuiltinRenderTextureType.GBuffer1);
			context.command.SetComputeTextureParam(computeShader, resolveKernel, ComputeUniforms.CameraGBufferTexture2, BuiltinRenderTextureType.GBuffer2);
			context.command.SetComputeTextureParam(computeShader, resolveKernel, ComputeUniforms.CameraMotionVectorsTexture, BuiltinRenderTextureType.MotionVectors);
			context.command.SetComputeTextureParam(computeShader, resolveKernel, ComputeUniforms.MinDepth, visibilityTex1);
			context.command.SetComputeTextureParam(computeShader, resolveKernel, ComputeUniforms.ResolveResult, resolvePassTex);
			context.command.SetComputeTextureParam(computeShader, resolveKernel, ComputeUniforms.ScreenInput, mainBuffer);
			context.command.SetComputeTextureParam(computeShader, resolveKernel, ComputeUniforms.RaycastInput, raycastTex);
			context.command.SetComputeTextureParam(computeShader, resolveKernel, ComputeUniforms.MaskInput, raycastMaskTex);
			context.command.SetComputeTextureParam(computeShader, resolveKernel, ComputeUniforms.CostMap, costMap);
			context.command.DispatchCompute(computeShader, resolveKernel, Mathf.CeilToInt((float)resolveSize.x / KERNEL_SIZE), Mathf.CeilToInt((float)resolveSize.y / KERNEL_SIZE), 1);

			context.command.EndSample("Stochastic Reflection Resolve");

			var finalResolve = new RenderTargetIdentifier(resolvePassTex);

			if (settings.useTemporal && !context.isSceneView)
			{
				context.command.BeginSample("Stochastic Reflection Temporal");

				var temporalBuffer0 = ComputeUniforms.TemporalBuffet0;
				var temporalBufferDesc = new RenderTextureDescriptor(resolveSize.x, resolveSize.y, RenderTextureFormat.DefaultHDR, 0) {enableRandomWrite = true};
				context.command.GetTemporaryRT(temporalBuffer0, temporalBufferDesc);

				context.command.SetComputeTextureParam(computeShader, temporalKernel, ComputeUniforms.MinDepth, visibilityTex1);
				context.command.SetComputeTextureParam(computeShader, temporalKernel, ComputeUniforms.RaycastInput, raycastTex);
				context.command.SetComputeTextureParam(computeShader, temporalKernel, ComputeUniforms.MaskInput, raycastMaskTex);
				context.command.SetComputeTextureParam(computeShader, temporalKernel, ComputeUniforms.PreviousTemporalInput, temporalBuffer);
				context.command.SetComputeTextureParam(computeShader, temporalKernel, ComputeUniforms.ScreenInput, resolvePassTex);
				context.command.SetComputeTextureParam(computeShader, temporalKernel, ComputeUniforms.CameraMotionVectorsTexture, BuiltinRenderTextureType.MotionVectors);
				context.command.SetComputeTextureParam(computeShader, temporalKernel, ComputeUniforms.CameraDepthTexture, BuiltinRenderTextureType.ResolvedDepth);

				context.command.SetComputeTextureParam(computeShader, temporalKernel, ComputeUniforms.TemporalResult, temporalBuffer0);
				context.command.DispatchCompute(computeShader, temporalKernel, Mathf.CeilToInt((float)resolveSize.x / KERNEL_SIZE), Mathf.CeilToInt((float)resolveSize.y / KERNEL_SIZE), 1);

				context.command.Blit(temporalBuffer0, temporalBuffer);
				context.command.ReleaseTemporaryRT(temporalBuffer0);

				context.command.SetGlobalTexture(Uniforms.ReflectionBuffer, temporalBuffer);
				finalResolve = temporalBuffer;

				context.command.EndSample("Stochastic Reflection Temporal");
			}

			context.command.BeginSample("Stochastic Reflection Blur");

			if (settings.blurring)
			{
				context.command.SetComputeTextureParam(blurShader, settings.highQualityBlur ? 1 : 0, ComputeUniforms.BlurResult, finalResolve);
				context.command.DispatchCompute(blurShader, settings.highQualityBlur ? 1 : 0, Mathf.CeilToInt((float)resolveSize.x / KERNEL_SIZE), Mathf.CeilToInt((float)resolveSize.y / KERNEL_SIZE), 1);
			}

			context.command.EndSample("Stochastic Reflection Blur");

			context.command.BeginSample("Stochastic Reflection Combine");

			switch (settings.debugPass.value)
			{
				case SSRDebugPass.Reflection:
				case SSRDebugPass.Cubemap:
				case SSRDebugPass.CombineNoCubemap:
				case SSRDebugPass.RayCast:
				case SSRDebugPass.ReflectionAndCubemap:
				case SSRDebugPass.SSRMask:
				case SSRDebugPass.CostMap:
				case SSRDebugPass.Depth:
				case SSRDebugPass.Resolve:
					context.command.BlitFullscreenTriangle(context.source, context.destination, sheet, combinePass);
					break;
				case SSRDebugPass.Combine:
					if (Application.isPlaying && settings.multipleBounces && !context.isSceneView)
					{
						context.command.BlitFullscreenTriangle(context.source, mainBuffer, sheet, combinePass);
						context.command.BlitFullscreenTriangle(mainBuffer, context.destination);
					}
					else
						context.command.BlitFullscreenTriangle(context.source, context.destination, sheet, combinePass);

					break;
			}

			context.command.EndSample("Stochastic Reflection Combine");

			context.command.ReleaseTemporaryRT(resolvePassTex);
			context.command.ReleaseTemporaryRT(raycastTex);
			context.command.ReleaseTemporaryRT(raycastMaskTex);
			context.command.ReleaseTemporaryRT(visibilityTex1);
			context.command.ReleaseTemporaryRT(costMap);

			prevViewProjectionMatrix = viewProjectionMatrix;

			context.command.EndSample("Stochastic Reflection");
		}

		// From Unity TAA
		private int m_SampleIndex = 0;
		private const int k_SampleCount = 64;

		private float GetHaltonValue(int index, int radix)
		{
			float result = 0f;
			float fraction = 1f / (float) radix;

			while (index > 0)
			{
				result += (float) (index % radix) * fraction;

				index /= radix;
				fraction /= (float) radix;
			}

			return result;
		}

		private Vector2 GenerateRandomOffset()
		{
			var offset = new Vector2(
				GetHaltonValue(m_SampleIndex & 1023, 2),
				GetHaltonValue(m_SampleIndex & 1023, 3));

			if (++m_SampleIndex >= k_SampleCount)
				m_SampleIndex = 0;

			return offset;
		}

		private Vector4 CalculateZBufferParams(Camera camera)
		{
			float fpn = camera.farClipPlane / camera.nearClipPlane;

			if (SystemInfo.usesReversedZBuffer)
				return new Vector4(fpn - 1f, 1f, (fpn - 1f) / camera.farClipPlane, 1f / camera.farClipPlane);

			return new Vector4(1f - fpn, fpn, (1f - fpn) / camera.farClipPlane, 1f / camera.farClipPlane);
		}

		private Vector4 CalculateProjectionParams(Camera camera)
		{
			return new Vector4(1, camera.nearClipPlane, camera.farClipPlane, 1f / camera.farClipPlane);
		}

		public static void BlitFullscreenTriangle(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier destination, Material material, int pass, bool clear = false)
		{
			cmd.SetGlobalTexture(Uniforms.MainTex, source);
			cmd.SetRenderTarget(destination);

			if (clear)
				cmd.ClearRenderTarget(true, true, Color.clear);

			cmd.DrawMesh(RuntimeUtilities.fullscreenTriangle, Matrix4x4.identity, material, 0, pass);
		}

		public override void Release()
		{
			CleanTextures();
			base.Release();
		}
	}
}