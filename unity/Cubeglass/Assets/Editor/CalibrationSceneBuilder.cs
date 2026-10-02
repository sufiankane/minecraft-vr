using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Cubeglass.Unity.Input;
using Cubeglass.Unity.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Cubeglass.Editor
{
    /// <summary>
    /// Deterministic builder for the S6 calibration scene: a floor grid, the
    /// coloured +X/+Y/+Z axis cubes, a far wall with yaw/pitch reference
    /// markers and a horizon strip, plus the stereo rig (config, late latch,
    /// bridge-first/synthetic-fallback pose selection) and the debug overlay.
    /// </summary>
    /// <remarks>
    /// Run from the menu (<c>Cubeglass/Build Calibration Scene</c>) or headless:
    /// <c>unity run unity/Cubeglass -- -executeMethod
    /// Cubeglass.Editor.CalibrationSceneBuilder.Build -quit</c>. The scene is
    /// committed; rebuilding on the same Unity version must produce byte-stable
    /// output, so every object is created in a fixed order with fixed names and
    /// values (no timestamps, no random ids).
    /// </remarks>
    public static class CalibrationSceneBuilder
    {
        /// <summary>The committed calibration scene path.</summary>
        public const string CalibrationScenePath = "Assets/Scenes/Calibration.unity";

        private const string ScenesFolder = "Assets/Scenes";
        private const float EyeHeight = 1.6f;
        private const float RigZ = -2f;
        private const float WallZ = 10f;
        private const int GridHalfExtent = 10;

        private const int GameObjectClassId = 1;
        private const int TransformClassId = 4;
        private const int SceneRootsClassId = 1660057539;
        private const long SceneRootsFileId = 9223372036854775807L;

        private static readonly int[] SettingsClassIds = { 29, 104, 157, 196 };
        private static readonly Regex DocumentHeaderPattern = new Regex(@"^--- !u!(\d+) &(\d+)$", RegexOptions.Compiled);
        private static readonly Regex FileIdPattern = new Regex(@"fileID: (\d+)\}", RegexOptions.Compiled);
        private static readonly Regex NamePattern = new Regex(@"^  m_Name: (.*)$", RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Color FloorColor = new Color(0.16f, 0.16f, 0.18f, 1f);
        private static readonly Color GridColor = new Color(0.35f, 0.35f, 0.4f, 1f);
        private static readonly Color WallColor = new Color(0.08f, 0.08f, 0.1f, 1f);
        private static readonly Color Red = new Color(1f, 0f, 0f, 1f);
        private static readonly Color Green = new Color(0f, 1f, 0f, 1f);
        private static readonly Color Blue = new Color(0f, 0f, 1f, 1f);
        private static readonly Color Cyan = new Color(0f, 1f, 1f, 1f);
        private static readonly Color Magenta = new Color(1f, 0f, 1f, 1f);
        private static readonly Color Yellow = new Color(1f, 1f, 0f, 1f);
        private static readonly Color Orange = new Color(1f, 0.5f, 0f, 1f);
        private static readonly Color White = new Color(1f, 1f, 1f, 1f);
        private static readonly Color Black = new Color(0f, 0f, 0f, 1f);

        /// <summary>
        /// Menu entry: rebuilds <see cref="CalibrationScenePath"/> in place.
        /// </summary>
        [MenuItem("Cubeglass/Build Calibration Scene")]
        public static void Build()
        {
            try
            {
                BuildSceneAt(CalibrationScenePath);
                Debug.Log("CalibrationSceneBuilder: wrote " + CalibrationScenePath);
            }
            catch (Exception exception)
            {
                Debug.LogError("CalibrationSceneBuilder: failed to build the calibration scene: " + exception);
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(1);
                }

                throw;
            }
        }

        /// <summary>
        /// Builds and saves the calibration scene at <paramref name="scenePath"/>.
        /// </summary>
        public static void BuildSceneAt(string scenePath)
        {
            EnsureFolder(ScenesFolder);
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var materials = new Dictionary<Color, Material>();

            CreatePrimitive(
                "Floor", null, PrimitiveType.Plane,
                new Vector3(0f, 0f, 0f), Quaternion.identity, new Vector3(4f, 1f, 4f),
                FloorColor, materials);

            BuildGrid(materials);
            BuildOrientationMarkers(materials);
            BuildReferenceWall(materials);
            LateLatchPose latch = BuildRig();
            BuildOverlay(latch);

            if (!EditorSceneManager.SaveScene(scene, scenePath))
            {
                throw new InvalidOperationException("EditorSceneManager.SaveScene returned false for " + scenePath);
            }

            CanonicalizeScene(scenePath);
            AssetDatabase.SaveAssets();
        }

        private static void BuildGrid(Dictionary<Color, Material> materials)
        {
            var grid = new GameObject("Grid");
            for (int i = -GridHalfExtent; i <= GridHalfExtent; i++)
            {
                string suffix = i.ToString(CultureInfo.InvariantCulture);
                CreatePrimitive(
                    "GridLineX" + suffix, grid.transform, PrimitiveType.Cube,
                    new Vector3(i, 0.006f, 0f), Quaternion.identity,
                    new Vector3(0.02f, 0.012f, GridHalfExtent * 2f),
                    GridColor, materials);
                CreatePrimitive(
                    "GridLineZ" + suffix, grid.transform, PrimitiveType.Cube,
                    new Vector3(0f, 0.006f, i), Quaternion.identity,
                    new Vector3(GridHalfExtent * 2f, 0.012f, 0.02f),
                    GridColor, materials);
            }
        }

        private static void BuildOrientationMarkers(Dictionary<Color, Material> materials)
        {
            var orientation = new GameObject("Orientation");
            CreatePrimitive(
                "AxisPlusX", orientation.transform, PrimitiveType.Cube,
                new Vector3(2f, 0.25f, 0f), Quaternion.identity, Vector3.one * 0.5f,
                Red, materials);
            CreatePrimitive(
                "AxisPlusY", orientation.transform, PrimitiveType.Cube,
                new Vector3(0f, 2.5f, 0f), Quaternion.identity, Vector3.one * 0.5f,
                Green, materials);
            CreatePrimitive(
                "AxisPlusZ", orientation.transform, PrimitiveType.Cube,
                new Vector3(0f, 0.25f, 2f), Quaternion.identity, Vector3.one * 0.5f,
                Blue, materials);
            CreatePrimitive(
                "Origin", orientation.transform, PrimitiveType.Cube,
                new Vector3(0f, 0.15f, 0f), Quaternion.identity, Vector3.one * 0.3f,
                White, materials);
        }

        private static void BuildReferenceWall(Dictionary<Color, Material> materials)
        {
            CreatePrimitive(
                "Wall", null, PrimitiveType.Plane,
                new Vector3(0f, 8f, WallZ), Quaternion.Euler(-90f, 0f, 0f), new Vector3(5f, 1f, 3f),
                WallColor, materials);
            CreatePrimitive(
                "Horizon", null, PrimitiveType.Cube,
                new Vector3(0f, EyeHeight, WallZ - 0.15f), Quaternion.identity, new Vector3(24f, 0.06f, 0.06f),
                White, materials);

            var markers = new GameObject("Markers");
            CreatePrimitive(
                "MarkerYawLeft", markers.transform, PrimitiveType.Cube,
                new Vector3(-4f, EyeHeight, WallZ - 0.15f), Quaternion.identity, Vector3.one * 0.5f,
                Cyan, materials);
            CreatePrimitive(
                "MarkerYawRight", markers.transform, PrimitiveType.Cube,
                new Vector3(4f, EyeHeight, WallZ - 0.15f), Quaternion.identity, Vector3.one * 0.5f,
                Magenta, materials);
            CreatePrimitive(
                "MarkerPitchUp", markers.transform, PrimitiveType.Cube,
                new Vector3(0f, 3.6f, WallZ - 0.15f), Quaternion.identity, Vector3.one * 0.5f,
                Yellow, materials);
            CreatePrimitive(
                "MarkerPitchDown", markers.transform, PrimitiveType.Cube,
                new Vector3(0f, 0.6f, WallZ - 0.15f), Quaternion.identity, Vector3.one * 0.5f,
                Orange, materials);
            CreatePrimitive(
                "MarkerCenter", markers.transform, PrimitiveType.Cube,
                new Vector3(0f, EyeHeight, WallZ - 0.25f), Quaternion.identity, Vector3.one * 0.2f,
                Black, materials);
        }

        private static LateLatchPose BuildRig()
        {
            var rigObject = new GameObject("StereoRig");
            rigObject.transform.localPosition = new Vector3(0f, EyeHeight, RigZ);

            var rig = rigObject.AddComponent<StereoRig>();
            StereoRigConfig config = rig.Config;
            config.IpdMeters = StereoRigConfig.DefaultIpdMeters;
            config.FovDegrees = StereoRigConfig.DefaultFovDegrees;
            config.Near = StereoRigConfig.DefaultNear;
            config.Far = StereoRigConfig.DefaultFar;
            config.BorderlessFullscreen = StereoRigConfig.DefaultBorderlessFullscreen;
            config.TargetRefresh = StereoRigConfig.DefaultTargetRefresh;
            // Pin the nominal side-by-side target so the baked camera FOVs are
            // deterministic instead of depending on the batch-mode screen.
            rig.ApplyEyeLayout(StereoRig.NominalScreenWidth, StereoRig.NominalScreenHeight);

            var latch = rigObject.AddComponent<LateLatchPose>();
            latch.Rig = rig;

            var providerObject = new GameObject("SyntheticPoseProvider");
            providerObject.transform.SetParent(rigObject.transform, false);
            var provider = providerObject.AddComponent<SyntheticPoseProvider>();
            provider.SetPose(0f, 0f, 0f, Vector3.zero);
            provider.SetRates(0f, 0f, 0f);
            var input = providerObject.AddComponent<UnityInputProvider>();
            var drive = providerObject.AddComponent<SyntheticPoseDrive>();
            drive.Input = input;
            drive.Provider = provider;

            var selector = rigObject.AddComponent<PoseProviderSelector>();
            selector.LateLatch = latch;
            selector.SyntheticFallback = provider;

            var window = rigObject.AddComponent<WindowManager>();
            window.Config = config;
            window.TargetDisplayIndex = 0;
            window.ApplyInEditor = false;
            return latch;
        }

        private static void BuildOverlay(LateLatchPose latch)
        {
            var overlayObject = new GameObject("DebugOverlay");
            var overlay = overlayObject.AddComponent<DebugOverlay>();
            overlay.LateLatch = latch;
            overlay.Visible = true;
        }

        private static GameObject CreatePrimitive(
            string name,
            Transform parent,
            PrimitiveType type,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            Color color,
            Dictionary<Color, Material> materials)
        {
            GameObject gameObject = GameObject.CreatePrimitive(type);
            gameObject.name = name;
            if (parent != null)
            {
                gameObject.transform.SetParent(parent, false);
            }

            gameObject.transform.localPosition = position;
            gameObject.transform.localRotation = rotation;
            gameObject.transform.localScale = scale;

            MeshRenderer meshRenderer = gameObject.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = MaterialFor(color, materials);

            Collider collider = gameObject.GetComponent<Collider>();
            if (collider != null)
            {
                Object.DestroyImmediate(collider);
            }

            return gameObject;
        }

        private static Material MaterialFor(Color color, Dictionary<Color, Material> materials)
        {
            if (materials.TryGetValue(color, out Material material))
            {
                return material;
            }

            Shader shader = Shader.Find("Unlit/Color");
            if (shader == null)
            {
                throw new InvalidOperationException("Built-in shader 'Unlit/Color' was not found.");
            }

            material = new Material(shader)
            {
                name = "Calibration " + ColorUtility.ToHtmlStringRGB(color),
                color = color,
            };
            materials.Add(color, material);
            return material;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            int separator = folder.LastIndexOf('/');
            if (separator <= 0)
            {
                throw new InvalidOperationException("Cannot create the asset folder '" + folder + "'.");
            }

            AssetDatabase.CreateFolder(folder.Substring(0, separator), folder.Substring(separator + 1));
        }

        /// <summary>
        /// Rewrites a just-saved scene into a canonical form: Unity assigns
        /// pseudo-random local file ids and emits documents in internal order,
        /// so two saves of the same scene differ byte-for-byte. This pass walks
        /// the scene graph deterministically (settings documents by class id,
        /// the <c>SceneRoots</c> roots by GameObject name, then every referenced
        /// document in stored order) and renumbers every document sequentially,
        /// remapping all <c>{fileID: n}</c> references. The result is byte-stable
        /// across rebuilds on the same Unity version.
        /// </summary>
        private static void CanonicalizeScene(string scenePath)
        {
            string[] lines = File.ReadAllText(scenePath).Replace("\r\n", "\n").Split('\n');
            int firstDocument = 0;
            while (firstDocument < lines.Length &&
                   !lines[firstDocument].StartsWith("--- !u!", StringComparison.Ordinal))
            {
                firstDocument++;
            }

            var documents = new List<SceneDocument>();
            var byOldId = new Dictionary<long, SceneDocument>();
            int index = firstDocument;
            while (index < lines.Length)
            {
                Match header = DocumentHeaderPattern.Match(lines[index]);
                if (!header.Success)
                {
                    throw new InvalidDataException(
                        "Unexpected scene document header at line " + (index + 1) + " of " + scenePath + ".");
                }

                var document = new SceneDocument
                {
                    ClassId = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture),
                    OldId = long.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture),
                };
                index++;
                int bodyStart = index;
                while (index < lines.Length &&
                       !lines[index].StartsWith("--- !u!", StringComparison.Ordinal))
                {
                    index++;
                }

                document.Body = string.Join("\n", lines, bodyStart, index - bodyStart);
                foreach (Match reference in FileIdPattern.Matches(document.Body))
                {
                    document.References.Add(long.Parse(reference.Groups[1].Value, CultureInfo.InvariantCulture));
                }

                documents.Add(document);
                byOldId.Add(document.OldId, document);
            }

            if (documents.Count == 0)
            {
                throw new InvalidDataException("No scene documents found in " + scenePath + ".");
            }

            var ordered = new List<SceneDocument>();
            var visited = new HashSet<long>();
            foreach (SceneDocument settings in documents
                         .Where(document => Array.IndexOf(SettingsClassIds, document.ClassId) >= 0)
                         .OrderBy(document => document.ClassId))
            {
                VisitDocument(settings, ordered, visited, byOldId);
            }

            SceneDocument sceneRoots = documents.FirstOrDefault(document => document.ClassId == SceneRootsClassId);
            if (sceneRoots == null)
            {
                throw new InvalidDataException("The scene " + scenePath + " has no SceneRoots document.");
            }

            VisitDocument(sceneRoots, ordered, visited, byOldId);

            if (ordered.Count != documents.Count)
            {
                string unreachable = string.Join(
                    ", ",
                    documents
                        .Where(document => !visited.Contains(document.OldId))
                        .Select(document => "class " + document.ClassId + " id " + document.OldId));
                throw new InvalidDataException(
                    "Canonicalisation did not reach every document in " + scenePath + ": " + unreachable);
            }

            long nextId = 1;
            foreach (SceneDocument document in ordered)
            {
                document.NewId = document.ClassId == SceneRootsClassId ? SceneRootsFileId : nextId++;
            }

            var newIds = ordered.ToDictionary(document => document.OldId, document => document.NewId);
            var builder = new StringBuilder();
            for (int header = 0; header < firstDocument; header++)
            {
                builder.Append(lines[header]).Append('\n');
            }

            foreach (SceneDocument document in ordered)
            {
                builder
                    .Append("--- !u!")
                    .Append(document.ClassId.ToString(CultureInfo.InvariantCulture))
                    .Append(" &")
                    .Append(document.NewId.ToString(CultureInfo.InvariantCulture))
                    .Append('\n');
                builder.Append(RemapReferences(document.Body, newIds));
                if (!document.Body.EndsWith("\n", StringComparison.Ordinal))
                {
                    builder.Append('\n');
                }
            }

            File.WriteAllText(scenePath, builder.ToString(), new UTF8Encoding(false));
        }

        private static void VisitDocument(
            SceneDocument document,
            List<SceneDocument> ordered,
            HashSet<long> visited,
            Dictionary<long, SceneDocument> byOldId)
        {
            if (!visited.Add(document.OldId))
            {
                return;
            }

            ordered.Add(document);
            IEnumerable<long> references = document.ClassId == SceneRootsClassId
                ? OrderSceneRoots(document, byOldId)
                : document.References;

            foreach (long reference in references)
            {
                if (byOldId.TryGetValue(reference, out SceneDocument referenced))
                {
                    VisitDocument(referenced, ordered, visited, byOldId);
                }
            }
        }

        private static IEnumerable<long> OrderSceneRoots(
            SceneDocument sceneRoots, Dictionary<long, SceneDocument> byOldId)
        {
            return sceneRoots.References
                .Where(reference => byOldId.ContainsKey(reference))
                .OrderBy(
                    reference => GameObjectName(byOldId[reference], byOldId),
                    StringComparer.Ordinal)
                .ThenBy(reference => byOldId[reference].Body, StringComparer.Ordinal)
                .ToList();
        }

        private static string GameObjectName(
            SceneDocument document, Dictionary<long, SceneDocument> byOldId)
        {
            SceneDocument current = document;
            for (int hop = 0; hop < 4 && current != null && current.ClassId != GameObjectClassId; hop++)
            {
                long next = current.References.FirstOrDefault(reference => byOldId.ContainsKey(reference));
                current = next != 0 && byOldId.TryGetValue(next, out SceneDocument referenced)
                    ? referenced
                    : null;
            }

            if (current == null || current.ClassId != GameObjectClassId)
            {
                return string.Empty;
            }

            Match name = NamePattern.Match(current.Body);
            return name.Success ? name.Groups[1].Value : string.Empty;
        }

        private static string RemapReferences(string body, Dictionary<long, long> newIds)
        {
            return FileIdPattern.Replace(
                body,
                match =>
                {
                    long oldId = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    return newIds.TryGetValue(oldId, out long newId)
                        ? "fileID: " + newId.ToString(CultureInfo.InvariantCulture) + "}"
                        : match.Value;
                });
        }

        private sealed class SceneDocument
        {
            public int ClassId;
            public long OldId;
            public long NewId;
            public string Body;
            public readonly List<long> References = new List<long>();
        }
    }
}
