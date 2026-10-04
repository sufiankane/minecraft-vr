using System.Text.RegularExpressions;
using Cubeglass.Unity.Bridge;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// Pins the pose-source selection contract (S7 review fix): a missing or
    /// unloadable <c>cg_unity_bridge.dll</c> still falls back to the scripted
    /// provider, but loudly and observably through
    /// <see cref="PoseProviderSelector.PluginUnavailable"/> — never as a silent
    /// fake tracking path.
    /// </summary>
    public class PoseProviderSelectorTests
    {
        private GameObject root;

        [SetUp]
        public void CreateSelector()
        {
            root = new GameObject("PoseProviderSelector");
            PoseProviderSelector.ForceSyntheticForTests = false;
            PoseProviderSelector.ForceMissingPluginForTests = false;
        }

        [TearDown]
        public void DestroySelector()
        {
            PoseProviderSelector.ForceSyntheticForTests = false;
            PoseProviderSelector.ForceMissingPluginForTests = false;
            Object.DestroyImmediate(root);
            root = null;
        }

        [Test]
        public void MissingPluginWarnsAndExposesTheFallbackInsteadOfStayingSilent()
        {
            LateLatchPose latch = root.AddComponent<LateLatchPose>();
            FakePoseProvider fallback = root.AddComponent<FakePoseProvider>();
            PoseProviderSelector selector = root.AddComponent<PoseProviderSelector>();
            selector.LateLatch = latch;
            selector.SyntheticFallback = fallback;

            PoseProviderSelector.ForceMissingPluginForTests = true;
            LogAssert.Expect(LogType.Warning, new Regex("cg_unity_bridge could not be loaded"));
            selector.SelectProvider();

            Assert.IsFalse(selector.UsingBridge, "no bridge is available");
            Assert.IsTrue(selector.PluginUnavailable, "the missing plugin must be exposed to the overlay/tests");
            Assert.AreSame(fallback, latch.Provider, "the synthetic fallback is still selected");
        }

        [Test]
        public void ForcedSyntheticSelectionIsNotAPluginFailure()
        {
            LateLatchPose latch = root.AddComponent<LateLatchPose>();
            FakePoseProvider fallback = root.AddComponent<FakePoseProvider>();
            PoseProviderSelector selector = root.AddComponent<PoseProviderSelector>();
            selector.LateLatch = latch;
            selector.SyntheticFallback = fallback;

            PoseProviderSelector.ForceSyntheticForTests = true;
            selector.SelectProvider();

            Assert.IsFalse(selector.UsingBridge);
            Assert.IsFalse(selector.PluginUnavailable, "a deliberate test override is not a plugin failure");
            Assert.AreSame(fallback, latch.Provider);
        }

        private sealed class FakePoseProvider : MonoBehaviour, IPoseProvider
        {
            public bool TryGetLatest(out BridgeHeadSample sample)
            {
                sample = new BridgeHeadSample { State = TrackState.Stable };
                return true;
            }
        }
    }
}
