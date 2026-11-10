using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Trive.Rendering.EditorTools
{
	/// <summary>
	/// Small helper that creates the scene objects required by the HDRP version of the stochastic
	/// screen space reflections and validates the HDRP asset settings the pass depends on.
	/// </summary>
	static class StochasticReflectionsMenu
	{
		const string kProfilePath = "Assets/Stochastic Reflections Volume Profile.asset";
		const string kMenuPath = "GameObject/Rendering/Stochastic Reflections (HDRP)";

		[MenuItem(kMenuPath, false, 10)]
		static void CreateSceneSetup()
		{
			CustomPassVolume passVolume = CreateCustomPassVolume();
			Volume volume = CreateGlobalVolume();

			Selection.objects = new Object[] { passVolume.gameObject, volume.gameObject };

			Debug.Log("Stochastic Reflections: created a Custom Pass Volume (Before Transparent) and a global Volume. " +
			          "Make sure the HDRP asset uses the Deferred Only lit shader mode, MSAA is disabled on the camera, " +
			          "and HDRP screen space reflections are disabled.");
		}

		[MenuItem("Tools/Rendering/Validate Stochastic Reflections (HDRP) Setup", false, 100)]
		static void ValidateSetup()
		{
			HDRenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
			if (asset == null)
			{
				Debug.LogError("Stochastic Reflections: the active render pipeline is not HDRP.");
				return;
			}

			RenderPipelineSettings settings = asset.currentPlatformRenderPipelineSettings;
			bool valid = true;

			if (settings.supportedLitShaderMode == RenderPipelineSettings.SupportedLitShaderMode.ForwardOnly)
			{
				Debug.LogError("Stochastic Reflections: the HDRP asset only supports the Forward Only lit shader mode. " +
				               "The pass needs the deferred normal buffer.");
				valid = false;
			}

			if (settings.msaaSampleCount != MSAASamples.None)
			{
				Debug.LogWarning("Stochastic Reflections: MSAA is enabled on the HDRP asset. " +
				                 "The pass is skipped on cameras that render with MSAA.");
			}

			if (settings.supportSSR)
			{
				Debug.LogWarning("Stochastic Reflections: HDRP screen space reflections are enabled. " +
				                 "They will be added on top of the stochastic reflections. Disable them for best results.");
			}

			int volumes = Object.FindObjectsByType<CustomPassVolume>().Length;
			if (volumes == 0)
			{
				Debug.LogError("Stochastic Reflections: no Custom Pass Volume found in the scene. " +
				               "Use GameObject > Rendering > Stochastic Reflections (HDRP) to create one.");
				valid = false;
			}

			if (valid)
				Debug.Log("Stochastic Reflections: setup looks valid.");
		}

		static CustomPassVolume CreateCustomPassVolume()
		{
			GameObject gameObject = new GameObject("Stochastic Reflections Custom Pass");

			CustomPassVolume passVolume = gameObject.AddComponent<CustomPassVolume>();
			passVolume.isGlobal = true;
			passVolume.injectionPoint = CustomPassInjectionPoint.BeforeTransparent;
			passVolume.customPasses.Add(new StochasticReflectionsPass());

			Undo.RegisterCreatedObjectUndo(gameObject, "Create Stochastic Reflections Custom Pass");

			return passVolume;
		}

		static Volume CreateGlobalVolume()
		{
			GameObject gameObject = new GameObject("Stochastic Reflections Volume");

			Volume volume = gameObject.AddComponent<Volume>();
			volume.isGlobal = true;
			volume.priority = 1f;
			volume.sharedProfile = GetOrCreateProfile();

			Undo.RegisterCreatedObjectUndo(gameObject, "Create Stochastic Reflections Volume");

			return volume;
		}

		static VolumeProfile GetOrCreateProfile()
		{
			VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(kProfilePath);
			if (profile != null)
			{
				if (!profile.TryGet(out StochasticReflectionsVolume component))
					component = profile.Add<StochasticReflectionsVolume>();

				EnableComponent(component);
				return profile;
			}

			profile = ScriptableObject.CreateInstance<VolumeProfile>();
			StochasticReflectionsVolume created = profile.Add<StochasticReflectionsVolume>();
			EnableComponent(created);

			AssetDatabase.CreateAsset(profile, kProfilePath);
			AssetDatabase.SaveAssets();

			return profile;
		}

		/// <summary>
		/// The effect is off by default, so a freshly created profile has to explicitly switch it on.
		/// </summary>
		static void EnableComponent(StochasticReflectionsVolume component)
		{
			component.enabled.overrideState = true;
			component.enabled.value = true;
			EditorUtility.SetDirty(component);
		}
	}
}
