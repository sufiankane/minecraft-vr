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
    /// and <c>targetRefresh</c>. Loading uses
    /// <see cref="JsonUtility.FromJsonOverwrite(string, object)"/>, so a field
    /// that is not present in the JSON keeps the code default. IPD and FOV are
    /// clamped to their supported ranges with a warning (dossier 5.13 fails
    /// loud; the rig clamps so a bad value cannot desynchronise the eyes).
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

        [SerializeField] private float ipdMeters = DefaultIpdMeters;
        [SerializeField] private float fovDegrees = DefaultFovDegrees;
        [SerializeField] private float near = DefaultNear;
        [SerializeField] private float far = DefaultFar;
        [SerializeField] private bool borderlessFullscreen = DefaultBorderlessFullscreen;
        [SerializeField] private int targetRefresh = DefaultTargetRefresh;

        /// <summary>Interpupillary distance in metres, clamped to [0.02, 0.09].</summary>
        public float IpdMeters
        {
            get { return ipdMeters; }
            set { ipdMeters = value; }
        }

        /// <summary>Per-eye field of view in degrees, clamped to [10, 120].</summary>
        public float FovDegrees
        {
            get { return fovDegrees; }
            set { fovDegrees = value; }
        }

        /// <summary>Near clip plane in metres.</summary>
        public float Near
        {
            get { return near; }
            set { near = value; }
        }

        /// <summary>Far clip plane in metres.</summary>
        public float Far
        {
            get { return far; }
            set { far = value; }
        }

        /// <summary>Borderless fullscreen on the configured display.</summary>
        public bool BorderlessFullscreen
        {
            get { return borderlessFullscreen; }
            set { borderlessFullscreen = value; }
        }

        /// <summary>Target display refresh rate in Hz.</summary>
        public int TargetRefresh
        {
            get { return targetRefresh; }
            set { targetRefresh = value; }
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
        /// Clamps IPD and FOV into their supported ranges and logs a warning for
        /// every field it changes. Returns true when a clamp was applied.
        /// </summary>
        public bool Validate()
        {
            bool clamped = false;

            if (ipdMeters < MinIpdMeters || ipdMeters > MaxIpdMeters)
            {
                float original = ipdMeters;
                ipdMeters = Mathf.Clamp(ipdMeters, MinIpdMeters, MaxIpdMeters);
                Debug.LogWarning(
                    "StereoRigConfig: ipdMeters " + original + " is outside [" +
                    MinIpdMeters + ", " + MaxIpdMeters + "]; clamped to " + ipdMeters + ".");
                clamped = true;
            }

            if (fovDegrees < MinFovDegrees || fovDegrees > MaxFovDegrees)
            {
                float original = fovDegrees;
                fovDegrees = Mathf.Clamp(fovDegrees, MinFovDegrees, MaxFovDegrees);
                Debug.LogWarning(
                    "StereoRigConfig: fovDegrees " + original + " is outside [" +
                    MinFovDegrees + ", " + MaxFovDegrees + "]; clamped to " + fovDegrees + ".");
                clamped = true;
            }

            return clamped;
        }
    }
}
