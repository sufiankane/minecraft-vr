using System;
using UnityEngine;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// Stereo presentation settings consumed by <see cref="StereoRig"/>: the
    /// `config.json` (dossier 5.13) subset for the rig. Defaults live in code
    /// (ADR-0010) and are used for every field absent from the JSON.
    /// </summary>
    /// <remarks>
    /// The JSON keys are the serialized field names: <c>ipdMeters</c>,
    /// <c>fovDegrees</c>, <c>near</c>, <c>far</c>, <c>borderlessFullscreen</c>
    /// and <c>targetRefresh</c>. <c>fovDegrees</c> is the per-eye
    /// <b>horizontal</b> FOV (ADR-0010); <see cref="StereoRig.ApplyEyeLayout"/>
    /// converts it to Unity's vertical <see cref="Camera.fieldOfView"/> at the
    /// per-eye viewport aspect. Loading uses
    /// <see cref="JsonUtility.FromJsonOverwrite(string, object)"/>, so a field
    /// that is not present in the JSON keeps the code default.
    /// <para>
    /// Every numeric field is guarded against non-finite values before any
    /// comparison (I-3): a <c>NaN</c> is never greater or less than any bound,
    /// so a NaN loaded from a corrupted scene/JSON used to slip through
    /// clamping and poison the camera projection. Programmatic setters fall
    /// back to the ADR-0010 default for non-finite input; <see cref="Validate"/>
    /// does the same for values that bypass the setters (JSON overwrite,
    /// serialized scene data) and additionally enforces
    /// <c>0 &lt; near &lt; far</c> and a sane refresh range, logging one
    /// warning per corrected field.
    /// </para>
    /// </remarks>
    [Serializable]
    public class StereoRigConfig
    {
        public const float DefaultIpdMeters = 0.064f;
        public const float DefaultFovDegrees = 45f;
        public const float DefaultNear = 0.05f;
        public const float DefaultFar = 500f;
        public const bool DefaultBorderlessFullscreen = true;
        public const int DefaultTargetRefresh = 90;

        public const float MinIpdMeters = 0.02f;
        public const float MaxIpdMeters = 0.09f;
        public const float MinFovDegrees = 10f;
        public const float MaxFovDegrees = 120f;

        /// <summary>Lowest refresh a display request may ask for (a "1 Hz" request would strobe, review M-10).</summary>
        public const int MinTargetRefresh = 24;

        /// <summary>Highest refresh a display request may ask for.</summary>
        public const int MaxTargetRefresh = 240;

        [SerializeField] private float ipdMeters = DefaultIpdMeters;
        [SerializeField] private float fovDegrees = DefaultFovDegrees;
        [SerializeField] private float near = DefaultNear;
        [SerializeField] private float far = DefaultFar;
        [SerializeField] private bool borderlessFullscreen = DefaultBorderlessFullscreen;
        [SerializeField] private int targetRefresh = DefaultTargetRefresh;

        /// <summary>
        /// Interpupillary distance in metres; programmatic sets are clamped to
        /// [0.02, 0.09], and non-finite input falls back to
        /// <see cref="DefaultIpdMeters"/>. JSON-loaded values are validated by
        /// <see cref="Validate"/>.
        /// </summary>
        public float IpdMeters
        {
            get { return ipdMeters; }
            set { ipdMeters = float.IsFinite(value) ? Mathf.Clamp(value, MinIpdMeters, MaxIpdMeters) : DefaultIpdMeters; }
        }

        /// <summary>
        /// Per-eye <b>horizontal</b> field of view in degrees (ADR-0010);
        /// <see cref="StereoRig"/> converts it to the vertical
        /// <see cref="UnityEngine.Camera.fieldOfView"/> at the per-eye viewport
        /// aspect. Programmatic sets are clamped to [10, 120], and non-finite
        /// input falls back to <see cref="DefaultFovDegrees"/>. JSON-loaded
        /// values are validated by <see cref="Validate"/>.
        /// </summary>
        public float FovDegrees
        {
            get { return fovDegrees; }
            set { fovDegrees = float.IsFinite(value) ? Mathf.Clamp(value, MinFovDegrees, MaxFovDegrees) : DefaultFovDegrees; }
        }

        /// <summary>Near clip plane in metres; non-finite or non-positive sets fall back to <see cref="DefaultNear"/>.</summary>
        public float Near
        {
            get { return near; }
            set { near = float.IsFinite(value) && value > 0f ? value : DefaultNear; }
        }

        /// <summary>Far clip plane in metres; non-finite or non-positive sets fall back to <see cref="DefaultFar"/>.</summary>
        public float Far
        {
            get { return far; }
            set { far = float.IsFinite(value) && value > 0f ? value : DefaultFar; }
        }

        /// <summary>Borderless fullscreen on the configured display.</summary>
        public bool BorderlessFullscreen
        {
            get { return borderlessFullscreen; }
            set { borderlessFullscreen = value; }
        }

        /// <summary>Target display refresh rate in Hz, clamped to [24, 240] on set (review M-10).</summary>
        public int TargetRefresh
        {
            get { return targetRefresh; }
            set { targetRefresh = SanitizeTargetRefresh(value); }
        }

        /// <summary>
        /// Resolves a requested refresh rate: a non-positive value is a
        /// corrupt/legacy request (for example the 1 Hz a default-initialised
        /// scene can carry) and maps to <see cref="DefaultTargetRefresh"/>;
        /// anything else is clamped into
        /// [<see cref="MinTargetRefresh"/>, <see cref="MaxTargetRefresh"/>].
        /// Used by the setter, by <see cref="Validate"/> and by the window
        /// manager at the point of use (review R-1).
        /// </summary>
        public static int SanitizeTargetRefresh(int value)
        {
            if (value <= 0)
            {
                return DefaultTargetRefresh;
            }

            return Mathf.Clamp(value, MinTargetRefresh, MaxTargetRefresh);
        }

        /// <summary>
        /// Applies the JSON on top of the current values: present fields
        /// override, absent fields keep their current value (the code defaults
        /// for a fresh instance). A null or blank document restores defaults.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="json"/> is not a JSON object this config can read.
        /// </exception>
        public void LoadFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                ResetToDefaults();
                return;
            }

            JsonUtility.FromJsonOverwrite(json, this);
            Validate();
        }

        /// <summary>Restores every field to its ADR-0010 default.</summary>
        public void ResetToDefaults()
        {
            ipdMeters = DefaultIpdMeters;
            fovDegrees = DefaultFovDegrees;
            near = DefaultNear;
            far = DefaultFar;
            borderlessFullscreen = DefaultBorderlessFullscreen;
            targetRefresh = DefaultTargetRefresh;
        }

        /// <summary>
        /// Corrects every numeric field into a usable range and logs a warning
        /// for every field it changes. Non-finite values fall back to the
        /// ADR-0010 default (a NaN can never pass a comparison, so it must be
        /// rejected explicitly); <c>near</c> must be positive and <c>far</c>
        /// greater than <c>near</c> (a near too large to pair with a finite far
        /// repairs both, review R-5); refresh is resolved by
        /// <see cref="SanitizeTargetRefresh"/> (non-positive to the default,
        /// otherwise clamped). Returns true when a correction was applied.
        /// </summary>
        public bool Validate()
        {
            bool changed = false;

            if (!float.IsFinite(ipdMeters) || ipdMeters < MinIpdMeters || ipdMeters > MaxIpdMeters)
            {
                float original = ipdMeters;
                ipdMeters = float.IsFinite(original)
                    ? Mathf.Clamp(original, MinIpdMeters, MaxIpdMeters)
                    : DefaultIpdMeters;
                WarnCorrection("ipdMeters", original, ipdMeters);
                changed = true;
            }

            if (!float.IsFinite(fovDegrees) || fovDegrees < MinFovDegrees || fovDegrees > MaxFovDegrees)
            {
                float original = fovDegrees;
                fovDegrees = float.IsFinite(original)
                    ? Mathf.Clamp(original, MinFovDegrees, MaxFovDegrees)
                    : DefaultFovDegrees;
                WarnCorrection("fovDegrees", original, fovDegrees);
                changed = true;
            }

            if (!float.IsFinite(near) || near <= 0f)
            {
                float original = near;
                near = DefaultNear;
                WarnCorrection("near", original, near);
                changed = true;
            }

            if (!float.IsFinite(far) || far <= near)
            {
                float originalFar = far;
                float repairedFar = near * 2f;

                // near can be a finite-but-huge double-to-float survivor; its
                // doubling then overflows to +infinity, which must not pass
                // the finite guarantee through the repair path (review R-5).
                if (!float.IsFinite(repairedFar) || !(repairedFar > near))
                {
                    float originalNear = near;
                    near = DefaultNear;
                    far = DefaultFar;
                    WarnCorrection("near", originalNear, near);
                    WarnCorrection("far", originalFar, far);
                }
                else
                {
                    far = Mathf.Max(DefaultFar, repairedFar);
                    WarnCorrection("far", originalFar, far);
                }

                changed = true;
            }

            int refresh = SanitizeTargetRefresh(targetRefresh);
            if (refresh != targetRefresh)
            {
                int original = targetRefresh;
                targetRefresh = refresh;
                Debug.LogWarning(
                    "StereoRigConfig: targetRefresh " + original + " is outside [" +
                    MinTargetRefresh + ", " + MaxTargetRefresh + "]; repaired to " + refresh + ".");
                changed = true;
            }

            return changed;
        }

        private static void WarnCorrection(string field, float original, float fallback)
        {
            Debug.LogWarning(
                "StereoRigConfig: " + field + " " + original + " is not a usable value; using " + fallback + ".");
        }
    }
}
