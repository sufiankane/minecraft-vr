using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Cubeglass.Editor
{
    /// <summary>
    /// Rewrites a just-saved Unity scene into a canonical form so rebuilding a
    /// scene with a deterministic builder produces byte-stable output: Unity
    /// assigns pseudo-random local file ids and emits documents in internal
    /// order, so two saves of the same scene differ byte-for-byte. This pass
    /// walks the scene graph deterministically (settings documents by class id,
    /// the <c>SceneRoots</c> roots by GameObject name, then every referenced
    /// document in stored order) and renumbers every document sequentially,
    /// remapping all <c>{fileID: n}</c> references.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="CalibrationSceneBuilder"/> and
    /// <see cref="GameSceneBuilder"/> (S7 Task 4c); the pass is not part of
    /// Unity, so it lives here rather than in a runtime assembly.
    /// </remarks>
    internal static class SceneCanonicalizer
    {
        private const int GameObjectClassId = 1;
        private const int SceneRootsClassId = 1660057539;
        private const long SceneRootsFileId = 9223372036854775807L;

        private static readonly int[] SettingsClassIds = { 29, 104, 157, 196 };
        private static readonly Regex DocumentHeaderPattern = new Regex(@"^--- !u!(\d+) &(\d+)$", RegexOptions.Compiled);
        private static readonly Regex FileIdPattern = new Regex(@"fileID: (\d+)\}", RegexOptions.Compiled);
        private static readonly Regex NamePattern = new Regex(@"^  m_Name: (.*)$", RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>
        /// Canonicalises the scene file at <paramref name="scenePath"/> in
        /// place; see the type remarks for the ordering contract.
        /// </summary>
        /// <exception cref="InvalidDataException">
        /// The file is not a scene document stream, has no <c>SceneRoots</c>
        /// document, or canonicalisation did not reach every document.
        /// </exception>
        public static void Canonicalize(string scenePath)
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
