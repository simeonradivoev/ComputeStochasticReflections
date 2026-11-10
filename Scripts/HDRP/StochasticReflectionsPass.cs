using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Trive.Rendering
{
	/// <summary>
	/// HDRP custom pass that renders the stochastic screen space reflections.
	///
	/// The pass has to be injected at <see cref="CustomPassInjectionPoint.BeforeTransparent"/> on a
	/// camera that renders with the deferred lit shader mode and with MSAA disabled, because it needs
	/// the normal buffer, the motion vectors and a non multisampled color buffer.
	///
	/// Unlike the post processing v2 version that removed the cubemap reflection from the scene color,
	/// HDRP has no separable reflection buffer, so the reflections are added on top of the scene color.
	/// </summary>
	[Serializable]
	public sealed class StochasticReflectionsPass : CustomPass
	{
		const int kKernelSize = 16;
		const int kMaxMinZLevels = 7;
		const int kMaxLods = 12;
		const int kCopyPass = 0;
		const int kCombinePass = 1;

		static class IDs
		{
			// Textures
			public static readonly int SceneColor = Shader.PropertyToID("_SRSceneColor");
			public static readonly int SceneColorSource = Shader.PropertyToID("_SRSceneColorSource");
			public static readonly int NormalBuffer = Shader.PropertyToID("_SRNormalBuffer");
			public static readonly int DepthTexture = Shader.PropertyToID("_SRDepthTexture");
			public static readonly int MotionVectors = Shader.PropertyToID("_SRMotionVectors");
			public static readonly int DepthPyramid = Shader.PropertyToID("_SRDepthPyramid");
			public static readonly int Noise = Shader.PropertyToID("_SRNoise");
			public static readonly int PreviousTemporal = Shader.PropertyToID("_SRPreviousTemporal");
			public static readonly int RayCast = Shader.PropertyToID("_SRRayCast");
			public static readonly int RayCastMask = Shader.PropertyToID("_SRRayCastMask");
			public static readonly int RayCastInput = Shader.PropertyToID("_SRRayCastInput");
			public static readonly int RayCastMaskInput = Shader.PropertyToID("_SRRayCastMaskInput");
			public static readonly int ResolveResult = Shader.PropertyToID("_SRResolveResult");
			public static readonly int ResolveInput = Shader.PropertyToID("_SRResolveInput");
			public static readonly int TemporalResult = Shader.PropertyToID("_SRTemporalResult");
			public static readonly int BlurSource = Shader.PropertyToID("_SRBlurSource");
			public static readonly int BlurDestination = Shader.PropertyToID("_SRBlurDestination");
			public static readonly int ReflectionBuffer = Shader.PropertyToID("_SRReflectionBuffer");
			public static readonly int DepthDestination = Shader.PropertyToID("_SRDepthDestination");
			public static readonly int ColorSource = Shader.PropertyToID("_SRColorSource");
			public static readonly int ColorDestination = Shader.PropertyToID("_SRColorDestination");

			// Sizes and matrices
			public static readonly int ScreenSize = Shader.PropertyToID("_SR_ScreenSize");
			public static readonly int RayCastSize = Shader.PropertyToID("_SR_RayCastSize");
			public static readonly int ResolveSize = Shader.PropertyToID("_SR_ResolveSize");
			public static readonly int NoiseSize = Shader.PropertyToID("_SR_NoiseSize");
			public static readonly int JitterSizeAndOffset = Shader.PropertyToID("_SRJitterSizeAndOffset");
			public static readonly int ZBufferParams = Shader.PropertyToID("_SRZBufferParams");
			public static readonly int DepthSize = Shader.PropertyToID("_SRDepthSize");
			public static readonly int ColorSize = Shader.PropertyToID("_SRColorSize");
			public static readonly int ProjMatrix = Shader.PropertyToID("_SRProjMatrix");
			public static readonly int InvProjMatrix = Shader.PropertyToID("_SRInvProjMatrix");
			public static readonly int ViewMatrix = Shader.PropertyToID("_SRViewMatrix");

			// Settings
			public static readonly int BRDFBias = Shader.PropertyToID("_SRBRDFBias");
			public static readonly int Thickness = Shader.PropertyToID("_SRThickness");
			public static readonly int EdgeFactor = Shader.PropertyToID("_SREdgeFactor");
			public static readonly int SmoothnessRange = Shader.PropertyToID("_SRSmoothnessRange");
			public static readonly int TResponseMin = Shader.PropertyToID("_SRTResponseMin");
			public static readonly int TResponseMax = Shader.PropertyToID("_SRTResponseMax");
			public static readonly int NumSteps = Shader.PropertyToID("_SRNumSteps");
			public static readonly int MaxMipMap = Shader.PropertyToID("_SRMaxMipMap");
			public static readonly int Normalization = Shader.PropertyToID("_SRNormalization");
			public static readonly int SourceMip = Shader.PropertyToID("_SRSourceMip");
			public static readonly int ColorSourceMip = Shader.PropertyToID("_SRColorSourceMip");
			public static readonly int Intensity = Shader.PropertyToID("_SRIntensity");
			public static readonly int DebugPass = Shader.PropertyToID("_SRDebugPass");
			public static readonly int UseFresnel = Shader.PropertyToID("_SRUseFresnel");
			public static readonly int SliceIndex = Shader.PropertyToID("_SRSliceIndex");
		}

		ComputeShader m_RayTraceShader;
		ComputeShader m_DepthPyramidShader;
		ComputeShader m_ColorPyramidShader;
		ComputeShader m_MedianBlurShader;
		Material m_Material;
		Texture2D m_Noise;

		int m_RaycastKernel;
		int m_ResolveKernel;
		int m_TemporalKernel;
		int m_DepthCopyKernel;
		int m_DepthDownsampleKernel;

		RTHandle m_SceneColor;
		RTHandle m_DepthPyramid;
		RTHandle m_DepthPing;
		RTHandle m_ColorPing;
		RTHandle m_RayCast;
		RTHandle m_RayCastMask;
		RTHandle m_Resolve;
		RTHandle m_Temporal;
		RTHandle m_Blur;
		RTHandle m_History;

		Camera m_HistoryCamera;
		bool m_HistoryValid;
		bool m_Initialized;

		// From Unity TAA
		int m_SampleIndex;
		const int kSampleCount = 64;

		static readonly ProfilingSampler s_ProfilingSampler = new ProfilingSampler("Stochastic Reflections");

		public StochasticReflectionsPass()
		{
			name = "Stochastic Reflections";
			targetColorBuffer = TargetBuffer.None;
			targetDepthBuffer = TargetBuffer.None;
			clearFlags = ClearFlag.None;
		}

		protected override bool executeInSceneView => true;

		bool Initialize()
		{
			if (m_Initialized)
				return true;

			m_RayTraceShader = Resources.Load<ComputeShader>("Shaders/HDRP/StochasticRayTrace");
			m_DepthPyramidShader = Resources.Load<ComputeShader>("Shaders/HDRP/StochasticDepthPyramid");
			m_ColorPyramidShader = Resources.Load<ComputeShader>("Shaders/HDRP/StochasticColorPyramid");
			m_MedianBlurShader = Resources.Load<ComputeShader>("Shaders/HDRP/StochasticMedianBlur");
			m_Noise = Resources.Load<Texture2D>("tex_BlueNoise_1024x1024_UNI");

			Shader shader = Resources.Load<Shader>("Shaders/HDRP/StochasticSSR");
			if (shader == null)
				shader = Shader.Find("Hidden/Stochastic SSR (HDRP)");

			if (shader != null)
				m_Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

			if (m_RayTraceShader == null || m_DepthPyramidShader == null || m_ColorPyramidShader == null ||
			    m_MedianBlurShader == null || m_Noise == null || m_Material == null)
			{
				Debug.LogError("Stochastic Reflections: missing resources. Make sure the package was imported completely.");
				return false;
			}

			m_RaycastKernel = m_RayTraceShader.FindKernel("CSRaycast");
			m_ResolveKernel = m_RayTraceShader.FindKernel("CSResolve");
			m_TemporalKernel = m_RayTraceShader.FindKernel("CSTemporal");
			m_DepthCopyKernel = m_DepthPyramidShader.FindKernel("CSCopyDepth");
			m_DepthDownsampleKernel = m_DepthPyramidShader.FindKernel("CSDownsample");

			m_Initialized = true;
			return true;
		}

		protected override void Execute(CustomPassContext ctx)
		{
			if (!Initialize())
				return;

			HDCamera hdCamera = ctx.hdCamera;
			VolumeStack stack = hdCamera.volumeStack;
			if (stack == null)
				return;

			StochasticReflectionsVolume settings = stack.GetComponent<StochasticReflectionsVolume>();
			if (settings == null || !settings.IsActive())
				return;

			if (!SystemInfo.supportsComputeShaders || SystemInfo.copyTextureSupport == CopyTextureSupport.None)
				return;

			// The pass needs the deferred normal buffer, the motion vectors and an unmultisampled color buffer.
			if (ctx.cameraNormalBuffer == null || ctx.cameraMotionVectorsBuffer == null || hdCamera.msaaEnabled)
				return;

			int width = hdCamera.actualWidth;
			int height = hdCamera.actualHeight;
			if (width <= 0 || height <= 0)
				return;

			Camera camera = hdCamera.camera;
			bool isSceneView = camera.cameraType == CameraType.SceneView
			                   || camera.cameraType == CameraType.Preview
			                   || camera.cameraType == CameraType.Reflection;

			Vector2Int raycastSize = new Vector2Int(width, height);
			Vector2Int resolveSize = new Vector2Int(width, height);

			if (settings.raycastDownsample.value)
			{
				raycastSize.x = Mathf.Max(1, raycastSize.x / 2);
				raycastSize.y = Mathf.Max(1, raycastSize.y / 2);
			}

			if (settings.resolveDownsample.value)
			{
				resolveSize.x = Mathf.Max(1, resolveSize.x / 2);
				resolveSize.y = Mathf.Max(1, resolveSize.y / 2);
			}

			int lodCount = Mathf.FloorToInt(Mathf.Log(width, 2f) - 3f);
			lodCount = Mathf.Clamp(lodCount, 1, kMaxLods);

			AllocateTextures(width, height, raycastSize, resolveSize);

			CommandBuffer cmd = ctx.cmd;
			using (new ProfilingScope(cmd, s_ProfilingSampler))
			{
				HDCamera.ViewConstants viewConstants = hdCamera.mainViewConstants;
				Matrix4x4 projMatrix = viewConstants.projMatrix;
				Matrix4x4 invProjMatrix = projMatrix.inverse;
				Matrix4x4 viewMatrix = viewConstants.viewMatrix;

				Vector4 screenSize = new Vector4(width, height, 1f / width, 1f / height);
				Vector4 rayCastSize = new Vector4(raycastSize.x, raycastSize.y, 1f / raycastSize.x, 1f / raycastSize.y);
				Vector4 resolveSizeVec = new Vector4(resolveSize.x, resolveSize.y, 1f / resolveSize.x, 1f / resolveSize.y);
				Vector4 noiseSize = new Vector4(m_Noise.width, m_Noise.height, 1f / m_Noise.width, 1f / m_Noise.height);
				Vector4 zBufferParams = CalculateZBufferParams(camera);

				Vector2 jitter = GenerateRandomOffset();
				Vector4 jitterSizeAndOffset = new Vector4(
					(float)width / m_Noise.width,
					(float)height / m_Noise.height,
					jitter.x,
					jitter.y);

				// Setup the compute shader constants.
				cmd.SetComputeVectorParam(m_RayTraceShader, IDs.ScreenSize, screenSize);
				cmd.SetComputeVectorParam(m_RayTraceShader, IDs.RayCastSize, rayCastSize);
				cmd.SetComputeVectorParam(m_RayTraceShader, IDs.ResolveSize, resolveSizeVec);
				cmd.SetComputeVectorParam(m_RayTraceShader, IDs.NoiseSize, noiseSize);
				cmd.SetComputeVectorParam(m_RayTraceShader, IDs.JitterSizeAndOffset, jitterSizeAndOffset);
				cmd.SetComputeVectorParam(m_RayTraceShader, IDs.ZBufferParams, zBufferParams);
				cmd.SetComputeMatrixParam(m_RayTraceShader, IDs.ProjMatrix, projMatrix);
				cmd.SetComputeMatrixParam(m_RayTraceShader, IDs.InvProjMatrix, invProjMatrix);
				cmd.SetComputeMatrixParam(m_RayTraceShader, IDs.ViewMatrix, viewMatrix);
				cmd.SetComputeFloatParam(m_RayTraceShader, IDs.BRDFBias, settings.brdfBias.value);
				cmd.SetComputeFloatParam(m_RayTraceShader, IDs.Thickness, settings.thickness.value);
				cmd.SetComputeFloatParam(m_RayTraceShader, IDs.EdgeFactor, settings.screenFadeSize.value);
				cmd.SetComputeFloatParam(m_RayTraceShader, IDs.SmoothnessRange, settings.smoothnessRange.value);
				cmd.SetComputeFloatParam(m_RayTraceShader, IDs.TResponseMin, settings.temporalResponseMin.value);
				cmd.SetComputeFloatParam(m_RayTraceShader, IDs.TResponseMax, settings.temporalResponseMax.value);
				cmd.SetComputeIntParam(m_RayTraceShader, IDs.NumSteps, settings.rayDistance.value);
				cmd.SetComputeIntParam(m_RayTraceShader, IDs.MaxMipMap, settings.useMipMap.value ? lodCount : 1);
				cmd.SetComputeIntParam(m_RayTraceShader, IDs.Normalization, settings.normalization.value ? 1 : 0);

				// HDRP allocates the camera buffers as texture arrays even outside of XR, so the shaders
				// have to select a slice explicitly. The pass does not support single pass instanced XR.
				int sliceIndex = 0;
				cmd.SetComputeIntParam(m_RayTraceShader, IDs.SliceIndex, sliceIndex);
				cmd.SetComputeIntParam(m_DepthPyramidShader, IDs.SliceIndex, sliceIndex);

				// Copy the scene color so it can be sampled while the reflections are composited back.
				cmd.SetGlobalTexture(IDs.SceneColorSource, (RenderTargetIdentifier)ctx.cameraColorBuffer);
				CoreUtils.DrawFullScreen(cmd, m_Material, (RenderTargetIdentifier)m_SceneColor, null, kCopyPass);

				BuildDepthPyramid(cmd, ctx, width, height);

				// Raycast
				cmd.SetComputeTextureParam(m_RayTraceShader, m_RaycastKernel, IDs.DepthPyramid, (RenderTargetIdentifier)m_DepthPyramid);
				cmd.SetComputeTextureParam(m_RayTraceShader, m_RaycastKernel, IDs.NormalBuffer, (RenderTargetIdentifier)ctx.cameraNormalBuffer);
				cmd.SetComputeTextureParam(m_RayTraceShader, m_RaycastKernel, IDs.DepthTexture, (RenderTargetIdentifier)ctx.cameraDepthBuffer);
				cmd.SetComputeTextureParam(m_RayTraceShader, m_RaycastKernel, IDs.Noise, m_Noise);
				cmd.SetComputeTextureParam(m_RayTraceShader, m_RaycastKernel, IDs.RayCast, (RenderTargetIdentifier)m_RayCast);
				cmd.SetComputeTextureParam(m_RayTraceShader, m_RaycastKernel, IDs.RayCastMask, (RenderTargetIdentifier)m_RayCastMask);
				cmd.DispatchCompute(m_RayTraceShader, m_RaycastKernel, DivRoundUp(raycastSize.x, kKernelSize), DivRoundUp(raycastSize.y, kKernelSize), 1);

				if (settings.useMipMap.value)
					BuildColorPyramid(cmd, width, height, lodCount);

				// Resolve
				cmd.SetComputeTextureParam(m_RayTraceShader, m_ResolveKernel, IDs.RayCastInput, (RenderTargetIdentifier)m_RayCast);
				cmd.SetComputeTextureParam(m_RayTraceShader, m_ResolveKernel, IDs.RayCastMaskInput, (RenderTargetIdentifier)m_RayCastMask);
				cmd.SetComputeTextureParam(m_RayTraceShader, m_ResolveKernel, IDs.SceneColor, (RenderTargetIdentifier)m_SceneColor);
				cmd.SetComputeTextureParam(m_RayTraceShader, m_ResolveKernel, IDs.NormalBuffer, (RenderTargetIdentifier)ctx.cameraNormalBuffer);
				cmd.SetComputeTextureParam(m_RayTraceShader, m_ResolveKernel, IDs.DepthTexture, (RenderTargetIdentifier)ctx.cameraDepthBuffer);
				cmd.SetComputeTextureParam(m_RayTraceShader, m_ResolveKernel, IDs.ResolveResult, (RenderTargetIdentifier)m_Resolve);
				cmd.DispatchCompute(m_RayTraceShader, m_ResolveKernel, DivRoundUp(resolveSize.x, kKernelSize), DivRoundUp(resolveSize.y, kKernelSize), 1);

				RTHandle finalResult = m_Resolve;

				bool useTemporal = settings.useTemporal.value && !isSceneView;
				if (useTemporal)
				{
					if (m_HistoryCamera != camera)
					{
						m_HistoryCamera = camera;
						m_HistoryValid = false;
					}

					if (m_HistoryValid)
					{
						cmd.SetComputeTextureParam(m_RayTraceShader, m_TemporalKernel, IDs.ResolveInput, (RenderTargetIdentifier)m_Resolve);
						cmd.SetComputeTextureParam(m_RayTraceShader, m_TemporalKernel, IDs.PreviousTemporal, (RenderTargetIdentifier)m_History);
						cmd.SetComputeTextureParam(m_RayTraceShader, m_TemporalKernel, IDs.RayCastInput, (RenderTargetIdentifier)m_RayCast);
						cmd.SetComputeTextureParam(m_RayTraceShader, m_TemporalKernel, IDs.DepthTexture, (RenderTargetIdentifier)ctx.cameraDepthBuffer);
						cmd.SetComputeTextureParam(m_RayTraceShader, m_TemporalKernel, IDs.MotionVectors, (RenderTargetIdentifier)ctx.cameraMotionVectorsBuffer);
						cmd.SetComputeTextureParam(m_RayTraceShader, m_TemporalKernel, IDs.TemporalResult, (RenderTargetIdentifier)m_Temporal);
						cmd.DispatchCompute(m_RayTraceShader, m_TemporalKernel, DivRoundUp(resolveSize.x, kKernelSize), DivRoundUp(resolveSize.y, kKernelSize), 1);

						cmd.CopyTexture((RenderTargetIdentifier)m_Temporal, 0, 0, 0, 0, resolveSize.x, resolveSize.y,
							(RenderTargetIdentifier)m_History, 0, 0, 0, 0);

						finalResult = m_Temporal;
					}
					else
					{
						cmd.CopyTexture((RenderTargetIdentifier)m_Resolve, 0, 0, 0, 0, resolveSize.x, resolveSize.y,
							(RenderTargetIdentifier)m_History, 0, 0, 0, 0);
						m_HistoryValid = true;
					}
				}
				else
				{
					m_HistoryValid = false;
				}

				// Median blur into a separate texture: writing in place races with the groups that still
				// read their neighbourhood and produces block sized artifacts.
				if (settings.blurring.value)
				{
					int blurKernel = settings.highQualityBlur.value ? 1 : 0;
					cmd.SetComputeTextureParam(m_MedianBlurShader, blurKernel, IDs.BlurSource, (RenderTargetIdentifier)finalResult);
					cmd.SetComputeTextureParam(m_MedianBlurShader, blurKernel, IDs.BlurDestination, (RenderTargetIdentifier)m_Blur);
					cmd.DispatchCompute(m_MedianBlurShader, blurKernel, DivRoundUp(resolveSize.x, kKernelSize), DivRoundUp(resolveSize.y, kKernelSize), 1);

					finalResult = m_Blur;
				}

				// Combine with the scene color.
				cmd.SetGlobalTexture(IDs.SceneColor, (RenderTargetIdentifier)m_SceneColor);
				cmd.SetGlobalTexture(IDs.ReflectionBuffer, (RenderTargetIdentifier)finalResult);
				cmd.SetGlobalTexture(IDs.DepthTexture, (RenderTargetIdentifier)ctx.cameraDepthBuffer);
				cmd.SetGlobalTexture(IDs.NormalBuffer, (RenderTargetIdentifier)ctx.cameraNormalBuffer);
				cmd.SetGlobalTexture(IDs.DepthPyramid, (RenderTargetIdentifier)m_DepthPyramid);
				cmd.SetGlobalTexture(IDs.RayCast, (RenderTargetIdentifier)m_RayCast);
				cmd.SetGlobalMatrix(IDs.ProjMatrix, projMatrix);
				cmd.SetGlobalMatrix(IDs.InvProjMatrix, invProjMatrix);
				cmd.SetGlobalMatrix(IDs.ViewMatrix, viewMatrix);
				cmd.SetGlobalVector(IDs.ZBufferParams, zBufferParams);
				cmd.SetGlobalVector(IDs.ScreenSize, screenSize);
				cmd.SetGlobalFloat(IDs.SmoothnessRange, settings.smoothnessRange.value);
				cmd.SetGlobalFloat(IDs.Intensity, settings.intensity.value);
				cmd.SetGlobalInt(IDs.DebugPass, (int)settings.debugPass.value);
				cmd.SetGlobalInt(IDs.UseFresnel, settings.useFresnel.value ? 1 : 0);
				cmd.SetGlobalInt(IDs.SliceIndex, sliceIndex);

				CoreUtils.DrawFullScreen(cmd, m_Material, (RenderTargetIdentifier)ctx.cameraColorBuffer, null, kCombinePass);
			}
		}

		void BuildDepthPyramid(CommandBuffer cmd, CustomPassContext ctx, int width, int height)
		{
			// Mip 0 is a plain copy of the raw depth buffer.
			cmd.SetComputeTextureParam(m_DepthPyramidShader, m_DepthCopyKernel, IDs.DepthTexture, (RenderTargetIdentifier)ctx.cameraDepthBuffer);
			cmd.SetComputeTextureParam(m_DepthPyramidShader, m_DepthCopyKernel, IDs.DepthDestination, (RenderTargetIdentifier)m_DepthPyramid, 0);
			cmd.SetComputeVectorParam(m_DepthPyramidShader, IDs.DepthSize, new Vector4(1f / width, 1f / height, 1f / width, 1f / height));
			cmd.DispatchCompute(m_DepthPyramidShader, m_DepthCopyKernel, DivRoundUp(width, 8), DivRoundUp(height, 8), 1);

			cmd.SetComputeTextureParam(m_DepthPyramidShader, m_DepthDownsampleKernel, IDs.DepthPyramid, (RenderTargetIdentifier)m_DepthPyramid);

			for (int i = 1; i <= kMaxMinZLevels; i++)
			{
				int sourceWidth = Mathf.Max(1, width >> (i - 1));
				int sourceHeight = Mathf.Max(1, height >> (i - 1));
				int destinationWidth = Mathf.Max(1, width >> i);
				int destinationHeight = Mathf.Max(1, height >> i);

				cmd.SetComputeVectorParam(m_DepthPyramidShader, IDs.DepthSize,
					new Vector4(1f / destinationWidth, 1f / destinationHeight, 1f / sourceWidth, 1f / sourceHeight));
				cmd.SetComputeIntParam(m_DepthPyramidShader, IDs.SourceMip, i - 1);
				cmd.SetComputeTextureParam(m_DepthPyramidShader, m_DepthDownsampleKernel, IDs.DepthDestination, (RenderTargetIdentifier)m_DepthPing);
				cmd.DispatchCompute(m_DepthPyramidShader, m_DepthDownsampleKernel, DivRoundUp(destinationWidth, 8), DivRoundUp(destinationHeight, 8), 1);

				cmd.CopyTexture((RenderTargetIdentifier)m_DepthPing, 0, 0, 0, 0, destinationWidth, destinationHeight,
					(RenderTargetIdentifier)m_DepthPyramid, 0, i, 0, 0);
			}
		}

		void BuildColorPyramid(CommandBuffer cmd, int width, int height, int lodCount)
		{
			cmd.SetComputeTextureParam(m_ColorPyramidShader, 0, IDs.ColorSource, (RenderTargetIdentifier)m_SceneColor);

			for (int i = 0; i < lodCount; i++)
			{
				int sourceWidth = Mathf.Max(1, width >> i);
				int sourceHeight = Mathf.Max(1, height >> i);
				int destinationWidth = Mathf.Max(1, width >> (i + 1));
				int destinationHeight = Mathf.Max(1, height >> (i + 1));

				cmd.SetComputeVectorParam(m_ColorPyramidShader, IDs.ColorSize,
					new Vector4(1f / destinationWidth, 1f / destinationHeight, 1f / sourceWidth, 1f / sourceHeight));
				cmd.SetComputeIntParam(m_ColorPyramidShader, IDs.ColorSourceMip, i);
				cmd.SetComputeTextureParam(m_ColorPyramidShader, 0, IDs.ColorDestination, (RenderTargetIdentifier)m_ColorPing);
				cmd.DispatchCompute(m_ColorPyramidShader, 0, DivRoundUp(destinationWidth, 8), DivRoundUp(destinationHeight, 8), 1);

				cmd.CopyTexture((RenderTargetIdentifier)m_ColorPing, 0, 0, 0, 0, destinationWidth, destinationHeight,
					(RenderTargetIdentifier)m_SceneColor, 0, i + 1, 0, 0);
			}
		}

		void AllocateTextures(int width, int height, Vector2Int raycastSize, Vector2Int resolveSize)
		{
			Realloc(ref m_SceneColor, width, height, GraphicsFormat.R16G16B16A16_SFloat, FilterMode.Bilinear, false, true,
				"StochasticReflectionsSceneColor");
			Realloc(ref m_DepthPyramid, width, height, GraphicsFormat.R32_SFloat, FilterMode.Point, true, true,
				"StochasticReflectionsDepthPyramid");
			Realloc(ref m_DepthPing, Mathf.Max(1, width / 2), Mathf.Max(1, height / 2), GraphicsFormat.R32_SFloat, FilterMode.Point, true, false,
				"StochasticReflectionsDepthPing");
			Realloc(ref m_ColorPing, Mathf.Max(1, width / 2), Mathf.Max(1, height / 2), GraphicsFormat.R16G16B16A16_SFloat, FilterMode.Bilinear, true, false,
				"StochasticReflectionsColorPing");
			Realloc(ref m_RayCast, raycastSize.x, raycastSize.y, GraphicsFormat.R16G16B16A16_SFloat, FilterMode.Point, true, false,
				"StochasticReflectionsRayCast");
			Realloc(ref m_RayCastMask, raycastSize.x, raycastSize.y, GraphicsFormat.R16_SFloat, FilterMode.Point, true, false,
				"StochasticReflectionsRayCastMask");
			Realloc(ref m_Resolve, resolveSize.x, resolveSize.y, GraphicsFormat.R16G16B16A16_SFloat, FilterMode.Bilinear, true, false,
				"StochasticReflectionsResolve");
			Realloc(ref m_Temporal, resolveSize.x, resolveSize.y, GraphicsFormat.R16G16B16A16_SFloat, FilterMode.Bilinear, true, false,
				"StochasticReflectionsTemporal");
			Realloc(ref m_Blur, resolveSize.x, resolveSize.y, GraphicsFormat.R16G16B16A16_SFloat, FilterMode.Bilinear, true, false,
				"StochasticReflectionsBlur");

			if (m_History == null || m_History.rt == null || m_History.rt.width != resolveSize.x || m_History.rt.height != resolveSize.y)
			{
				if (m_History != null)
					RTHandles.Release(m_History);

				m_History = RTHandles.Alloc(
					width: resolveSize.x,
					height: resolveSize.y,
					colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
					filterMode: FilterMode.Bilinear,
					wrapMode: TextureWrapMode.Clamp,
					dimension: TextureDimension.Tex2D,
					enableRandomWrite: false,
					useMipMap: false,
					autoGenerateMips: false,
					name: "StochasticReflectionsHistory");

				m_HistoryValid = false;
			}
		}

		static void Realloc(ref RTHandle handle, int width, int height, GraphicsFormat format, FilterMode filterMode,
			bool enableRandomWrite, bool mipMapped, string name)
		{
			if (handle != null && handle.rt != null && handle.rt.width == width && handle.rt.height == height)
				return;

			if (handle != null)
				RTHandles.Release(handle);

			handle = RTHandles.Alloc(
				width: width,
				height: height,
				colorFormat: format,
				filterMode: filterMode,
				wrapMode: TextureWrapMode.Clamp,
				dimension: TextureDimension.Tex2D,
				enableRandomWrite: enableRandomWrite,
				useMipMap: mipMapped,
				autoGenerateMips: false,
				name: name);
		}

		static int DivRoundUp(int value, int divisor)
		{
			return (value + divisor - 1) / divisor;
		}

		// From Unity TAA
		static float GetHaltonValue(int index, int radix)
		{
			float result = 0f;
			float fraction = 1f / radix;

			while (index > 0)
			{
				result += (index % radix) * fraction;

				index /= radix;
				fraction /= radix;
			}

			return result;
		}

		Vector2 GenerateRandomOffset()
		{
			Vector2 offset = new Vector2(
				GetHaltonValue(m_SampleIndex & 1023, 2),
				GetHaltonValue(m_SampleIndex & 1023, 3));

			if (++m_SampleIndex >= kSampleCount)
				m_SampleIndex = 0;

			return offset;
		}

		static Vector4 CalculateZBufferParams(Camera camera)
		{
			float farNearRatio = camera.farClipPlane / camera.nearClipPlane;

			if (SystemInfo.usesReversedZBuffer)
				return new Vector4(farNearRatio - 1f, 1f, (farNearRatio - 1f) / camera.farClipPlane, 1f / camera.farClipPlane);

			return new Vector4(1f - farNearRatio, farNearRatio, (1f - farNearRatio) / camera.farClipPlane, 1f / camera.farClipPlane);
		}

		protected override void Cleanup()
		{
			ReleaseTextures();

			if (m_Material != null)
			{
				CoreUtils.Destroy(m_Material);
				m_Material = null;
			}

			m_Initialized = false;
		}

		void ReleaseTextures()
		{
			ReleaseHandle(ref m_SceneColor);
			ReleaseHandle(ref m_DepthPyramid);
			ReleaseHandle(ref m_DepthPing);
			ReleaseHandle(ref m_ColorPing);
			ReleaseHandle(ref m_RayCast);
			ReleaseHandle(ref m_RayCastMask);
			ReleaseHandle(ref m_Resolve);
			ReleaseHandle(ref m_Temporal);
			ReleaseHandle(ref m_Blur);
			ReleaseHandle(ref m_History);

			m_HistoryCamera = null;
			m_HistoryValid = false;
		}

		static void ReleaseHandle(ref RTHandle handle)
		{
			if (handle == null)
				return;

			RTHandles.Release(handle);
			handle = null;
		}
	}
}
