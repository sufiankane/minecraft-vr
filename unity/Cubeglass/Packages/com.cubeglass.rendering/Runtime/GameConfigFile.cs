using System;
using System.Globalization;
using System.IO;
using Cubeglass.Streaming;
using UnityEngine;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// The validated subset of the shipped <c>config.json</c> (TD-014): the
    /// streaming tunables of <see cref="StreamingConfig"/> plus the stereo-rig
    /// fields of <see cref="StereoRigConfig"/>. A null property means the key
    /// was absent and the caller keeps its current (inspector) value.
    /// </summary>
    public sealed class GameConfigValues
    {
        public int? ViewDistanceChunks { get; internal set; }

        public int? UnloadHysteresis { get; internal set; }

        public int? MaxLoadsPerFrame { get; internal set; }

        public int? MaxUnloadsPerFrame { get; internal set; }

        public int? MaxMeshUploadsPerFrame { get; internal set; }

        public float? VerticalRadiusChunks { get; internal set; }

        public float? IpdMeters { get; internal set; }

        public float? FovDegrees { get; internal set; }

        public float? Near { get; internal set; }

        public float? Far { get; internal set; }

        public int? TargetRefresh { get; internal set; }
    }

    /// <summary>
    /// Loads the shipped <c>config.json</c> (TD-014) and applies its keys on top
    /// of the serialized inspector values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Precedence (ADR-0011 addendum).</b> built-in code defaults &lt;
    /// serialized inspector values &lt; <c>config.json</c> keys: a key present
    /// in the file wins over the scene value; a key absent keeps the scene
    /// value. This makes the file the operator-facing tuning surface shipped
    /// next to the player while an author can still bake values into a scene by
    /// omitting the key from the file.
    /// </para>
    /// <para>
    /// <b>Location.</b> In a player the file is expected at
    /// <c>&lt;StreamingAssets&gt;/Cubeglass/config.json</c> (the build
    /// post-processor copies the committed <c>Assets/config.json</c> there); in
    /// the editor and in tests it is read from <c>&lt;Assets&gt;/config.json</c>.
    /// A missing file is a no-op with a debug log; a malformed or oversized
    /// file logs one warning and applies nothing, so a bad drop-in cannot brick
    /// boot.
    /// </para>
    /// <para>
    /// <b>Validation.</b> Each numeric key is validated against the same bounds
    /// the runtime enforces: out-of-range finite values are clamped with a
    /// warning, non-finite values are ignored, and cross-field constraints
    /// (<c>near &lt; far</c>, refresh) are handled by the destination's own
    /// <see cref="StereoRigConfig.Validate"/>/scheduler construction. Unknown
    /// JSON keys are ignored.
    /// </para>
    /// </remarks>
    public static class GameConfigFile
    {
        /// <summary>File name of the shipped configuration.</summary>
        public const string FileName = "config.json";

        /// <summary>Subfolder under StreamingAssets that carries the file in a player.</summary>
        public const string StreamingAssetsSubfolder = "Cubeglass";

        /// <summary>Largest accepted config file, in bytes (256 KiB); larger files are rejected.</summary>
        public const int MaxFileBytes = 256 * 1024;

        /// <summary>
        /// The resolved path: the StreamingAssets copy when present, otherwise
        /// the editor <c>Assets/config.json</c>, otherwise null. (Window-mode
        /// keys such as <c>borderlessFullscreen</c> stay inspector-driven; the
        /// loader owns the streaming and rig tuning keys.)
        /// </summary>
        public static string ResolvePath()
        {
            string streaming = Path.Combine(
                Application.streamingAssetsPath, StreamingAssetsSubfolder, FileName);
            if (File.Exists(streaming))
            {
                return streaming;
            }

            string editor = Path.Combine(Application.dataPath, FileName);
            return File.Exists(editor) ? editor : null;
        }

        /// <summary>
        /// Resolves the shipped path and loads it. Returns false with the file
        /// absent (a no-op, no error) or invalid (an error is logged and
        /// <paramref name="error"/> set).
        /// </summary>
        public static bool TryLoadDefault(out GameConfigValues values, out string sourcePath, out string error)
        {
            string path = ResolvePath();
            if (path == null)
            {
                values = null;
                sourcePath = null;
                error = null;
                return false;
            }

            sourcePath = path;
            return TryLoad(path, out values, out error);
        }

        /// <summary>
        /// Loads and validates <paramref name="path"/>; false with a non-null
        /// <paramref name="error"/> when the file cannot be read or is not a
        /// JSON object this loader accepts.
        /// </summary>
        public static bool TryLoad(string path, out GameConfigValues values, out string error)
        {
            values = null;
            error = null;
            if (path == null)
            {
                error = "config path is null";
                return false;
            }

            string json;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxFileBytes)
                {
                    error = "config file is too large (" + info.Length + " bytes, cap " + MaxFileBytes + ")";
                    Debug.LogWarning("[GameConfigFile] " + error + ": '" + path + "'");
                    return false;
                }

                json = File.ReadAllText(path);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                Debug.LogWarning("[GameConfigFile] could not read '" + path + "': " + exception.Message);
                return false;
            }

            return TryParse(json, out values, out error);
        }

        /// <summary>
        /// Parses and validates a config document; false with a non-null
        /// <paramref name="error"/> when it is not valid JSON for this loader.
        /// </summary>
        public static bool TryParse(string json, out GameConfigValues values, out string error)
        {
            values = null;
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "config document is empty";
                return false;
            }

            GameConfigJson parsed;
            try
            {
                parsed = JsonUtility.FromJson<GameConfigJson>(json);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                Debug.LogWarning("[GameConfigFile] malformed config JSON: " + exception.Message);
                return false;
            }

            if (parsed == null)
            {
                error = "config JSON did not contain an object";
                return false;
            }

            values = new GameConfigValues
            {
                ViewDistanceChunks = ValidateInt(parsed.viewDistanceChunks, StreamingConfig.MinViewDistanceChunks, StreamingConfig.MaxViewDistanceChunks, "viewDistanceChunks"),
                UnloadHysteresis = ValidateInt(parsed.unloadHysteresis, 0, StreamingConfig.MaxUnloadHysteresis, "unloadHysteresis"),
                MaxLoadsPerFrame = ValidateInt(parsed.maxLoadsPerFrame, 0, StreamingConfig.MaxActionsPerFrame, "maxLoadsPerFrame"),
                MaxUnloadsPerFrame = ValidateInt(parsed.maxUnloadsPerFrame, 0, StreamingConfig.MaxActionsPerFrame, "maxUnloadsPerFrame"),
                MaxMeshUploadsPerFrame = ValidateInt(parsed.maxMeshUploadsPerFrame, 0, StreamingConfig.MaxActionsPerFrame, "maxMeshUploadsPerFrame"),
                VerticalRadiusChunks = ValidateFloat(parsed.verticalRadiusChunks, 0f, StreamingConfig.MaxVerticalRadiusChunks, "verticalRadiusChunks"),
                IpdMeters = ValidateFloat(parsed.ipdMeters, StereoRigConfig.MinIpdMeters, StereoRigConfig.MaxIpdMeters, "ipdMeters"),
                FovDegrees = ValidateFloat(parsed.fovDegrees, StereoRigConfig.MinFovDegrees, StereoRigConfig.MaxFovDegrees, "fovDegrees"),
                Near = ValidateFloat(parsed.near, 1e-4f, float.MaxValue, "near"),
                Far = ValidateFloat(parsed.far, 1e-4f, float.MaxValue, "far"),
                TargetRefresh = ValidateInt(parsed.targetRefresh, StereoRigConfig.MinTargetRefresh, StereoRigConfig.MaxTargetRefresh, "targetRefresh"),
            };
            return true;
        }

        /// <summary>
        /// Applies every present key to <paramref name="config"/> (file wins);
        /// values were validated on parse, so the setters cannot throw.
        /// </summary>
        public static void ApplyTo(GameConfigValues values, StereoRigConfig config)
        {
            if (values == null || config == null)
            {
                return;
            }

            if (values.IpdMeters.HasValue)
            {
                config.IpdMeters = values.IpdMeters.Value;
            }

            if (values.FovDegrees.HasValue)
            {
                config.FovDegrees = values.FovDegrees.Value;
            }

            if (values.Near.HasValue)
            {
                config.Near = values.Near.Value;
            }

            if (values.Far.HasValue)
            {
                config.Far = values.Far.Value;
            }

            if (values.TargetRefresh.HasValue)
            {
                config.TargetRefresh = values.TargetRefresh.Value;
            }
        }

        private static int? ValidateInt(int value, int min, int max, string name)
        {
            if (value == int.MinValue)
            {
                return null;
            }

            if (value < min || value > max)
            {
                int clamped = value < min ? min : max;
                Debug.LogWarning(
                    "[GameConfigFile] " + name + " " + value.ToString(CultureInfo.InvariantCulture)
                        + " is outside [" + min + ", " + max + "]; clamped to " + clamped + ".");
                return clamped;
            }

            return value;
        }

        private static float? ValidateFloat(float value, float min, float max, string name)
        {
            if (float.IsNaN(value))
            {
                return null;
            }

            if (float.IsNegativeInfinity(value) || float.IsPositiveInfinity(value))
            {
                Debug.LogWarning("[GameConfigFile] ignoring non-finite " + name + ".");
                return null;
            }

            if (value < min || value > max)
            {
                float clamped = value < min ? min : max;
                Debug.LogWarning(
                    "[GameConfigFile] " + name + " " + value.ToString(CultureInfo.InvariantCulture)
                        + " is outside [" + min.ToString(CultureInfo.InvariantCulture) + ", "
                        + max.ToString(CultureInfo.InvariantCulture) + "]; clamped to "
                        + clamped.ToString(CultureInfo.InvariantCulture) + ".");
                return clamped;
            }

            return value;
        }

        /// <summary>
        /// The JSON DTO. Fields absent from the document keep their sentinel
        /// (<see cref="int.MinValue"/> / NaN) and are reported as absent.
        /// </summary>
        [Serializable]
        private sealed class GameConfigJson
        {
            public int viewDistanceChunks = int.MinValue;
            public int unloadHysteresis = int.MinValue;
            public int maxLoadsPerFrame = int.MinValue;
            public int maxUnloadsPerFrame = int.MinValue;
            public int maxMeshUploadsPerFrame = int.MinValue;
            public float verticalRadiusChunks = float.NaN;
            public float ipdMeters = float.NaN;
            public float fovDegrees = float.NaN;
            public float near = float.NaN;
            public float far = float.NaN;
            public int targetRefresh = int.MinValue;
        }
    }
}
