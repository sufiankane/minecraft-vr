using System.Text.RegularExpressions;
using Cubeglass.Unity.Bridge;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Cubeglass.Unity.Rendering.Tests
{
    /// <summary>
    /// Pins the pose-source selection contract (S7 review fix + I-1 of the
    /// 2026-10-04 review): a missing or unloadable <c>cg_unity_bridge.dll</c>
    /// still falls back to the scripted provider, but loudly and observably
    /// through <see cref="PoseProviderSelector.PluginUnavailable"/> — and a
    /// <c>NotReady</c> region is retried on a timer instead of leaving the
    /// one-shot synthetic fallback in place forever.
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
            PoseProviderSelector.BridgeProbeOverrideForTests = null;
        }

        [TearDown]
        public void DestroySelector()
        {
            PoseProviderSelector.ForceSyntheticForTests = false;
            PoseProviderSelector.ForceMissingPluginForTests = false;
            PoseProviderSelector.BridgeProbeOverrideForTests = null;
            Object.DestroyImmediate(root);
            root = null;
        }

        private PoseProviderSelector CreateComponent(out LateLatchPose latch, out FakePoseProvider fallback)
        {
            latch = root.AddComponent<LateLatchPose>();
            fallback = root.AddComponent<FakePoseProvider>();
            PoseProviderSelector selector = root.AddComponent<PoseProviderSelector>();
            selector.LateLatch = latch;
            selector.SyntheticFallback = fallback;
            return selector;
        }

        [Test]
        public void MissingPluginWarnsAndExposesTheFallbackInsteadOfStayingSilent()
        {
            PoseProviderSelector selector = CreateComponent(out LateLatchPose latch, out FakePoseProvider fallback);

            PoseProviderSelector.ForceMissingPluginForTests = true;
            LogAssert.Expect(LogType.Warning, new Regex("cg_unity_bridge could not be loaded"));
            selector.SelectProvider();

            Assert.IsFalse(selector.UsingBridge, "no bridge is available");
            Assert.IsTrue(selector.PluginUnavailable, "the missing plugin must be exposed to the overlay/tests");
            Assert.AreEqual(PoseFallbackReason.PluginUnavailable, selector.FallbackReason);
            Assert.AreSame(fallback, latch.Provider, "the synthetic fallback is still selected");
        }

        [Test]
        public void ForcedSyntheticSelectionIsNotAPluginFailure()
        {
            PoseProviderSelector selector = CreateComponent(out LateLatchPose latch, out FakePoseProvider fallback);

            PoseProviderSelector.ForceSyntheticForTests = true;
            selector.SelectProvider();

            Assert.IsFalse(selector.UsingBridge);
            Assert.IsFalse(selector.PluginUnavailable, "a deliberate test override is not a plugin failure");
            Assert.AreEqual(PoseFallbackReason.ForcedSynthetic, selector.FallbackReason);
            Assert.AreSame(fallback, latch.Provider);
        }

        [Test]
        public void ForcedSyntheticDoesNotProbeOrWarnWhenTheTimerTicks()
        {
            PoseProviderSelector selector = CreateComponent(out _, out FakePoseProvider fallback);
            var bridgeProvider = root.AddComponent<FakePoseProvider>();
            PoseProviderSelector.ForceSyntheticForTests = true;
            PoseProviderSelector.BridgeProbeOverrideForTests = () => PoseBridgeProbe.Opened(bridgeProvider, null);

            selector.SelectProvider();
            selector.RetryIntervalSeconds = 0f;
            selector.UpdateRetries();
            selector.UpdateRetries();

            Assert.IsFalse(selector.UsingBridge, "the deliberate override must not be retried away");
            Assert.AreEqual(0, selector.ProbeAttempts, "no probe may run under ForceSyntheticForTests");
            Assert.AreSame(fallback, selector.LateLatch.Provider);
        }

        [Test]
        public void NotReadyFallbackRetriesUntilTheBridgeAppears()
        {
            PoseProviderSelector selector = CreateComponent(out LateLatchPose latch, out FakePoseProvider fallback);
            var bridgeProvider = root.AddComponent<FakePoseProvider>();
            bool ready = false;
            PoseProviderSelector.BridgeProbeOverrideForTests = () => ready
                ? PoseBridgeProbe.Opened(bridgeProvider, null)
                : PoseBridgeProbe.NotReady(BridgeStatus.NotReady);

            LogAssert.Expect(LogType.Warning, new Regex("bridge not ready"));
            selector.SelectProvider();

            Assert.IsFalse(selector.UsingBridge, "the first probe is NotReady");
            Assert.AreEqual(PoseFallbackReason.NotReady, selector.FallbackReason);
            Assert.AreSame(fallback, latch.Provider);
            Assert.AreEqual(1, selector.ProbeAttempts);

            // The old one-shot behaviour stops here; the timer must probe again
            // and switch the latch to the bridge provider when it opens.
            ready = true;
            selector.RetryIntervalSeconds = 0f;
            selector.WarningIntervalSeconds = 3600f;
            selector.UpdateRetries();

            Assert.IsTrue(selector.UsingBridge, "the retry must adopt the bridge once it opens");
            Assert.AreEqual(PoseFallbackReason.None, selector.FallbackReason);
            Assert.IsFalse(selector.PluginUnavailable);
            Assert.AreSame(bridgeProvider, latch.Provider, "the latch must point at the bridge provider");
            Assert.AreEqual(2, selector.ProbeAttempts);

            // Once the bridge is selected the timer is a no-op.
            selector.UpdateRetries();
            Assert.AreEqual(2, selector.ProbeAttempts, "no further probes after a successful switch");
        }

        [Test]
        public void RecurringWarningNamesTheFallbackReasonAndIsRateLimited()
        {
            PoseProviderSelector selector = CreateComponent(out _, out _);
            PoseProviderSelector.BridgeProbeOverrideForTests =
                () => PoseBridgeProbe.NotReady(BridgeStatus.NotReady);

            LogAssert.Expect(LogType.Warning, new Regex("bridge not ready"));
            selector.SelectProvider();
            Assert.AreEqual(1, selector.FallbackWarnings, "the entry warning is the only one so far");

            selector.RetryIntervalSeconds = 3600f;
            selector.WarningIntervalSeconds = 3600f;
            selector.UpdateRetries();
            Assert.AreEqual(
                1,
                selector.FallbackWarnings,
                "the recurring warning must respect the warning interval");

            selector.WarningIntervalSeconds = 0f;
            LogAssert.Expect(LogType.Warning, new Regex("still on the synthetic pose fallback.*bridge not ready"));
            selector.UpdateRetries();
            Assert.AreEqual(2, selector.FallbackWarnings, "the elapsed interval emits the recurring warning");
        }

        [Test]
        public void StateChangedFiresOnEveryTransition()
        {
            PoseProviderSelector selector = CreateComponent(out _, out _);
            var bridgeProvider = root.AddComponent<FakePoseProvider>();
            bool ready = false;
            PoseProviderSelector.BridgeProbeOverrideForTests = () => ready
                ? PoseBridgeProbe.Opened(bridgeProvider, null)
                : PoseBridgeProbe.NotReady(BridgeStatus.NotReady);
            int transitions = 0;
            selector.StateChanged += () => transitions++;

            LogAssert.Expect(LogType.Warning, new Regex("bridge not ready"));
            selector.SelectProvider();
            Assert.AreEqual(1, transitions, "None -> NotReady is one transition");

            ready = true;
            selector.RetryIntervalSeconds = 0f;
            selector.WarningIntervalSeconds = 3600f;
            selector.UpdateRetries();
            Assert.AreEqual(2, transitions, "NotReady -> bridge is one transition");
        }

        [Test]
        public void MissingPluginReasonsAreRetriedButKeepTheReason()
        {
            PoseProviderSelector selector = CreateComponent(out LateLatchPose latch, out FakePoseProvider fallback);
            int probes = 0;
            PoseProviderSelector.BridgeProbeOverrideForTests = () =>
            {
                probes++;
                return PoseBridgeProbe.PluginMissing();
            };

            LogAssert.Expect(LogType.Warning, new Regex("cg_unity_bridge could not be loaded"));
            selector.SelectProvider();

            selector.RetryIntervalSeconds = 0f;
            selector.WarningIntervalSeconds = 3600f;
            selector.UpdateRetries();
            selector.UpdateRetries();

            Assert.AreEqual(3, probes, "the plugin case is retried too");
            Assert.IsTrue(selector.PluginUnavailable);
            Assert.AreEqual(PoseFallbackReason.PluginUnavailable, selector.FallbackReason);
            Assert.AreSame(fallback, latch.Provider, "the fallback stays selected while the plugin is missing");
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
