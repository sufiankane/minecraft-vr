using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Cubeglass.Editor
{
    /// <summary>
    /// Recomputes the committed scene SHA-256 pins recorded in
    /// <c>docs/notes/s7-gate.md</c> section 7 and compares them (TD-025). The
    /// hash is taken over the scene bytes with CRLF normalised to LF first, so
    /// a Windows checkout with <c>core.autocrlf</c> produces the same value as
    /// the committed git blob (the old recipe hashed the worktree bytes and
    /// false-mismatched on Windows).
    /// </summary>
    /// <remarks>
    /// Used by the EditMode test <c>SceneHashVerifierTests</c>, the
    /// <c>Cubeglass/Verify Scene Hashes</c> menu entry and the batch entry
    /// <see cref="VerifyAllBatch"/> (which exits non-zero on any mismatch, so a
    /// CI lane can call it with <c>-executeMethod</c>). The recipe is documented
    /// next to the pins in <c>docs/notes/s7-gate.md</c> section 7.1.
    /// </remarks>
    public static class SceneHashVerifier
    {
        /// <summary>Game scene path relative to the Unity project root.</summary>
        public const string GameScenePath = "Assets/Scenes/Game.unity";

        /// <summary>Game scene pin (s7-gate.md section 7).</summary>
        public const string GameSceneSha256 =
            "2B97305E45F6E8B5ED130B33F6DBC6F589B2DB8278C3E063B7A72DF3A0627767";

        /// <summary>Calibration scene path relative to the Unity project root.</summary>
        public const string CalibrationScenePath = "Assets/Scenes/Calibration.unity";

        /// <summary>Calibration scene pin (s7-gate.md section 7).</summary>
        public const string CalibrationSceneSha256 =
            "70F970CE7CFEEDC6A70AD27B8759CE177A496A235E59E45E37E0A243E8E490A1";

        /// <summary>One scene's pin comparison.</summary>
        public sealed class SceneHashResult
        {
            public SceneHashResult(string scenePath, string expected, string actual)
            {
                ScenePath = scenePath;
                Expected = expected;
                Actual = actual;
            }

            /// <summary>Scene path relative to the Unity project root.</summary>
            public string ScenePath { get; }

            /// <summary>The committed pin.</summary>
            public string Expected { get; }

            /// <summary>The recomputed hash (or a marker when the file is missing).</summary>
            public string Actual { get; }

            /// <summary>Whether the recomputed hash equals the pin.</summary>
            public bool Match
            {
                get { return string.Equals(Expected, Actual, StringComparison.OrdinalIgnoreCase); }
            }
        }

        /// <summary>The Unity project root (the directory containing <c>Assets</c>).</summary>
        public static string ProjectRoot
        {
            get { return Directory.GetParent(Application.dataPath).FullName; }
        }

        /// <summary>Compares both committed scenes against their pins.</summary>
        public static IReadOnlyList<SceneHashResult> VerifyAll()
        {
            return new[]
            {
                VerifyScene(GameScenePath, GameSceneSha256),
                VerifyScene(CalibrationScenePath, CalibrationSceneSha256),
            };
        }

        /// <summary>Compares one scene file against <paramref name="expectedHash"/>.</summary>
        public static SceneHashResult VerifyScene(string projectRelativePath, string expectedHash)
        {
            string fullPath = Path.Combine(ProjectRoot, projectRelativePath);
            string actual = File.Exists(fullPath)
                ? ComputeHash(File.ReadAllBytes(fullPath))
                : "<missing>";
            return new SceneHashResult(projectRelativePath, expectedHash, actual);
        }

        /// <summary>
        /// SHA-256 of scene bytes with CRLF normalised to LF, so the value is
        /// checkout-agnostic and matches the committed git blob.
        /// </summary>
        public static string ComputeHash(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            using (SHA256 sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(NormalizeLineEndings(bytes)));
            }
        }

        /// <summary>Returns a copy of <paramref name="bytes"/> with CRLF pairs collapsed to LF.</summary>
        public static byte[] NormalizeLineEndings(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            var normalized = new List<byte>(bytes.Length);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
                {
                    continue;
                }

                normalized.Add(bytes[i]);
            }

            return normalized.ToArray();
        }

        /// <summary>
        /// Menu entry: logs every comparison and a loud error for each mismatch
        /// (<c>Cubeglass/Verify Scene Hashes</c>).
        /// </summary>
        [MenuItem("Cubeglass/Verify Scene Hashes")]
        public static void VerifyFromMenu()
        {
            IReadOnlyList<SceneHashResult> results = VerifyAll();
            bool allMatch = true;
            var report = new StringBuilder("[SceneHashVerifier] scene hash pins:");
            foreach (SceneHashResult result in results)
            {
                report.Append('\n').Append("  ").Append(result.ScenePath).Append(": ");
                if (result.Match)
                {
                    report.Append("OK ").Append(result.Actual);
                }
                else
                {
                    allMatch = false;
                    report.Append("MISMATCH expected ").Append(result.Expected)
                        .Append(" actual ").Append(result.Actual);
                }
            }

            if (allMatch)
            {
                Debug.Log(report.ToString());
            }
            else
            {
                Debug.LogError(
                    report.ToString()
                        + "\nRe-pin only when a scene rebuild is intentional (recipe: docs/notes/s7-gate.md section 7.1).");
            }
        }

        /// <summary>
        /// Batch entry (<c>-executeMethod Cubeglass.Editor.SceneHashVerifier.VerifyAllBatch</c>):
        /// verifies and exits 0 on success, 1 on any mismatch or exception.
        /// </summary>
        public static void VerifyAllBatch()
        {
            try
            {
                IReadOnlyList<SceneHashResult> results = VerifyAll();
                foreach (SceneHashResult result in results)
                {
                    if (result.Match)
                    {
                        Debug.Log("[SceneHashVerifier] " + result.ScenePath + " OK " + result.Actual);
                    }
                    else
                    {
                        Debug.LogError(
                            "[SceneHashVerifier] " + result.ScenePath + " MISMATCH expected "
                                + result.Expected + " actual " + result.Actual);
                    }
                }

                bool allMatch = true;
                foreach (SceneHashResult result in results)
                {
                    allMatch &= result.Match;
                }

                EditorApplication.Exit(allMatch ? 0 : 1);
            }
            catch (Exception exception)
            {
                Debug.LogError("[SceneHashVerifier] verification failed: " + exception);
                EditorApplication.Exit(1);
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }
}