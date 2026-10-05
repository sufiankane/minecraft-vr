using System;
using System.IO;
using NUnit.Framework;

namespace Cubeglass.Editor.Tests
{
    /// <summary>
    /// TD-060 M-9: the canonical root ordering must not depend on the
    /// pseudo-random local file ids Unity assigns, because two saves of the
    /// same content produce different ids and the reviewer found the old
    /// body-based tiebreak could order structural twins by those ids.
    /// </summary>
    public sealed class SceneCanonicalizerTests
    {
        [Test]
        public void StableBodyKeyScrubsFileIdsButKeepsContent()
        {
            string first = "  m_GameObject: {fileID: 100}\n  m_LocalPosition: {x: 1, y: 2, z: 3}";
            string second = "  m_GameObject: {fileID: 987654321}\n  m_LocalPosition: {x: 1, y: 2, z: 3}";

            Assert.AreEqual(
                SceneCanonicalizer.StableBodyKey(first),
                SceneCanonicalizer.StableBodyKey(second),
                "the ids must be scrubbed");

            string moved = "  m_GameObject: {fileID: 100}\n  m_LocalPosition: {x: 9, y: 2, z: 3}";
            Assert.AreNotEqual(
                SceneCanonicalizer.StableBodyKey(first),
                SceneCanonicalizer.StableBodyKey(moved),
                "content outside ids must still be part of the key");
        }

        [Test]
        public void CanonicalizeOrdersSameNamedRootsByContentNotOldIds()
        {
            string rootA = Path.Combine(Path.GetTempPath(), "cg-canon-a-" + Guid.NewGuid().ToString("N"));
            string rootB = Path.Combine(Path.GetTempPath(), "cg-canon-b-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootA);
            Directory.CreateDirectory(rootB);
            string sceneA = Path.Combine(rootA, "A.unity");
            string sceneB = Path.Combine(rootB, "B.unity");
            try
            {
                // Both scenes hold two roots named "Alpha" that differ only in
                // m_Layer, listed in the same order. Scene B assigns the old
                // ids so that the *numeric* id order is the opposite of the
                // content order: the old body-based tiebreak (which saw the
                // raw fileIDs) would order them differently, the stable key
                // must not.
                File.WriteAllText(sceneA, Scene(
                    rootList: "  - {fileID: 200}\n  - {fileID: 100}",
                    layer5Id: 200, layer5TransformId: 201,
                    layer6Id: 100, layer6TransformId: 101));
                File.WriteAllText(sceneB, Scene(
                    rootList: "  - {fileID: 7}\n  - {fileID: 9}",
                    layer5Id: 7, layer5TransformId: 8,
                    layer6Id: 9, layer6TransformId: 10));

                SceneCanonicalizer.Canonicalize(sceneA);
                SceneCanonicalizer.Canonicalize(sceneB);

                Assert.AreEqual(
                    File.ReadAllText(sceneA),
                    File.ReadAllText(sceneB),
                    "canonicalization must be independent of the old ids and list order");
            }
            finally
            {
                Directory.Delete(rootA, true);
                Directory.Delete(rootB, true);
            }
        }

        [Test]
        public void CanonicalizeRefusesStructurallyIdenticalSameNamedRoots()
        {
            string root = Path.Combine(Path.GetTempPath(), "cg-canon-twin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string scene = Path.Combine(root, "Twin.unity");
            try
            {
                File.WriteAllText(scene, Scene(
                    rootList: "  - {fileID: 100}\n  - {fileID: 200}",
                    layer5Id: 100, layer5TransformId: 101,
                    layer6Id: 200, layer6TransformId: 201,
                    layer5: 5,
                    layer6: 5));

                Assert.Throws<InvalidDataException>(
                    () => SceneCanonicalizer.Canonicalize(scene),
                    "two roots that are identical modulo ids have no renumber-independent order");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static string Scene(
            string rootList,
            long layer5Id,
            long layer5TransformId,
            long layer6Id,
            long layer6TransformId,
            int layer5 = 5,
            int layer6 = 6)
        {
            return "%YAML 1.1\n"
                + "%TAG !u! tag:unity3d.com,2011:\n"
                + "--- !u!1660057539 &9223372036854775807\n"
                + "SceneRoots:\n"
                + "  m_ObjectHideFlags: 0\n"
                + "  m_Roots:\n"
                + rootList + "\n"
                + "--- !u!1 &" + layer5Id + "\n"
                + "GameObject:\n"
                + "  m_ObjectHideFlags: 0\n"
                + "  m_Name: Alpha\n"
                + "  m_Layer: " + layer5 + "\n"
                + "  m_Component:\n"
                + "  - component: {fileID: " + layer5TransformId + "}\n"
                + "--- !u!4 &" + layer5TransformId + "\n"
                + "Transform:\n"
                + "  m_ObjectHideFlags: 0\n"
                + "  m_GameObject: {fileID: " + layer5Id + "}\n"
                + "  m_LocalPosition: {x: 0, y: 0, z: 0}\n"
                + "--- !u!1 &" + layer6Id + "\n"
                + "GameObject:\n"
                + "  m_ObjectHideFlags: 0\n"
                + "  m_Name: Alpha\n"
                + "  m_Layer: " + layer6 + "\n"
                + "  m_Component:\n"
                + "  - component: {fileID: " + layer6TransformId + "}\n"
                + "--- !u!4 &" + layer6TransformId + "\n"
                + "Transform:\n"
                + "  m_ObjectHideFlags: 0\n"
                + "  m_GameObject: {fileID: " + layer6Id + "}\n"
                + "  m_LocalPosition: {x: 0, y: 0, z: 0}\n";
        }
    }
}