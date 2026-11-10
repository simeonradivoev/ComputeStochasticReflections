using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Trive.Rendering
{
	/// <summary>
	/// Debug views available on the HDRP volume override.
	/// </summary>
	public enum StochasticReflectionsDebugPass
	{
		Combine = 0,
		Reflection = 1,
		SSRMask = 2,
		RayCast = 3,
		Resolve = 4,
		Depth = 5,
	}

	[Serializable]
	public sealed class StochasticReflectionsDebugPassParameter : VolumeParameter<StochasticReflectionsDebugPass>
	{
		public StochasticReflectionsDebugPassParameter(StochasticReflectionsDebugPass value, bool overrideState = false)
			: base(value, overrideState)
		{
		}
	}

	/// <summary>
	/// HDRP volume settings for the stochastic screen space reflections custom pass.
	/// Somewhere in the scene a <see cref="Volume"/> (global) has to hold an override of this component
	/// for the pass to do anything.
	/// </summary>
	[Serializable]
	[VolumeComponentMenu("Post-processing/Stochastic Screen Space Reflections (HDRP)")]
	public sealed class StochasticReflectionsVolume : VolumeComponent
	{
		[Tooltip("Turns the pass on. The effect is off until a volume overrides this to true.")]
		public BoolParameter enabled = new BoolParameter(false);

		[Tooltip("Weight of the reflection that gets added on top of the scene color.")]
		public ClampedFloatParameter intensity = new ClampedFloatParameter(1f, 0f, 1f);

		[Tooltip("Run the raycast at half resolution.")]
		public BoolParameter raycastDownsample = new BoolParameter(false);

		[Tooltip("Run the resolve at half resolution.")]
		public BoolParameter resolveDownsample = new BoolParameter(false);

		[Tooltip("Number of hierarchical Z steps. Higher values trace longer rays but cost more.")]
		public ClampedIntParameter rayDistance = new ClampedIntParameter(70, 1, 256);

		[Tooltip("Depth difference in view space unit that still counts as an intersection.")]
		public FloatParameter thickness = new FloatParameter(0.2f);

		[Tooltip("Fades the reflections out towards the screen borders. 0 disables the fade.")]
		public ClampedFloatParameter screenFadeSize = new ClampedFloatParameter(0.25f, 0f, 1f);

		[Tooltip("Apply a Fresnel term to the reflection. HDRP does not expose the specular color, so a dielectric F0 of 0.04 is used.")]
		public BoolParameter useFresnel = new BoolParameter(true);

		[Tooltip("GGX importance sampling bias. 0 is fully random, 1 is the mirror direction.")]
		public ClampedFloatParameter brdfBias = new ClampedFloatParameter(0.7f, 0f, 1f);

		[Header("Resolve")]
		[Tooltip("Build a color mip chain so rough reflections get elongated.")]
		public BoolParameter useMipMap = new BoolParameter(true);

		[Tooltip("Divide by the BRDF weight while resolving.")]
		public BoolParameter normalization = new BoolParameter(true);

		[Tooltip("Run a median blur over the resolved reflections.")]
		public BoolParameter blurring = new BoolParameter(true);

		[Tooltip("Use the 5x5 median kernel instead of the 3x3 one.")]
		public BoolParameter highQualityBlur = new BoolParameter(true);

		[Header("Temporal (play mode only)")]
		public BoolParameter useTemporal = new BoolParameter(true);

		[Tooltip("Lower bound of the temporal accumulation weight.")]
		public ClampedFloatParameter temporalResponseMin = new ClampedFloatParameter(0.85f, 0f, 1f);

		[Tooltip("Upper bound of the temporal accumulation weight.")]
		public ClampedFloatParameter temporalResponseMax = new ClampedFloatParameter(1f, 0f, 1f);

		[Header("Debug")]
		[Tooltip("Maximum roughness that is allowed to produce a reflection.")]
		public ClampedFloatParameter smoothnessRange = new ClampedFloatParameter(1f, 0f, 1f);

		public StochasticReflectionsDebugPassParameter debugPass = new StochasticReflectionsDebugPassParameter(StochasticReflectionsDebugPass.Combine);

		/// <summary>
		/// The pass calls this to know whether it has to run at all.
		/// </summary>
		/// <remarks>
		/// <see cref="VolumeComponent.active"/> is not used here: it is not a <see cref="VolumeParameter"/>,
		/// so the volume manager never copies it into the stack and unchecking "Active" in a profile would
		/// leave the pass running with default values. <see cref="enabled"/> is blended like any other
		/// parameter, which makes it a real on/off switch.
		/// </remarks>
		public bool IsActive()
		{
			return enabled.value
			       && intensity.value > 0f
			       && rayDistance.value > 0;
		}
	}
}
