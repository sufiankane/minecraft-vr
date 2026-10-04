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
            selector.UpdateRetriesAt(0.0);
            selector.UpdateRetriesAt(0.0);

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
            selector.RetryIntervalSeconds = 1f;
            selector.WarningIntervalSeconds = 3600f;
            double t0 = Time.realtimeSinceStartupAsDouble;
            selector.UpdateRetriesAt(t0 + 2.0);

            Assert.IsTrue(selector.UsingBridge, "the retry must adopt the bridge once it opens");
            Assert.AreEqual(PoseFallbackReason.None, selector.FallbackReason);
            Assert.IsFalse(selector.PluginUnavailable);
            Assert.AreSame(bridgeProvider, latch.Provider, "the latch must point at the bridge provider");
            Assert.AreEqual(2, selector.ProbeAttempts);

            // Once the bridge is selected the timer is a no-op.
            selector.UpdateRetriesAt(t0 + 200.0);
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

            double t0 = Time.realtimeSinceStartupAsDouble;
            selector.RetryIntervalSeconds = 3600f;
            selector.WarningIntervalSeconds = 3600f;
            selector.UpdateRetriesAt(t0 + 1.0);
            Assert.AreEqual(
                1,
                selector.FallbackWarnings,
                "the recurring warning must respect the warning interval");

            selector.WarningIntervalSeconds = 0.5f;
            LogAssert.Expect(LogType.Warning, new Regex("still on the synthetic pose fallback.*bridge not ready"));
            selector.UpdateRetriesAt(t0 + 20.0);
            Assert.AreEqual(2, selector.FallbackWarnings, "the elapsed interval emits the recurring warning");
        }

        [Test]
        public void FirstUpdateAfterSelectionDoesNotProbeTwice()
        {
            PoseProviderSelector selector = CreateComponent(out _, out _);
            PoseProviderSelector.BridgeProbeOverrideForTests =
                () => PoseBridgeProbe.NotReady(BridgeStatus.NotReady);

            LogAssert.Expect(LogType.Warning, new Regex("bridge not ready"));
            selector.SelectProvider();
            Assert.AreEqual(1, selector.ProbeAttempts, "the selection probe ran once");

            selector.UpdateRetriesAt(0.0);
            Assert.AreEqual(
                1,
                selector.ProbeAttempts,
                "the first Update must not pay a second immediate native probe (review R-2)");
        }

        /// <summary>
        /// R-2: a NaN/negative/zero interval used to make <see cref="Mathf.Max(float, float)"/>
        /// collapse to 0 and probe through the native open on every frame. The
        /// setter and the point of use both bound it.
        /// </summary>
        [Test]
        public void HostileRetryIntervalCannotProbeEveryFrame()
        {
            PoseProviderSelector selector = CreateComponent(out _, out _);
            PoseProviderSelector.BridgeProbeOverrideForTests =
                () => PoseBridgeProbe.NotReady(BridgeStatus.NotReady);

            selector.RetryIntervalSeconds = float.NaN;
            Assert.IsTrue(float.IsFinite(selector.RetryIntervalSeconds), "a NaN setter value is sanitized");
            Assert.AreEqual(
                PoseProviderSelector.DefaultRetryIntervalSeconds,
                selector.RetryIntervalSeconds,
                1e-6f,
                "NaN resolves to the default interval");
            selector.RetryIntervalSeconds = -5f;
            Assert.AreEqual(
                PoseProviderSelector.MinRetryIntervalSeconds,
                selector.RetryIntervalSeconds,
                1e-6f,
                "a negative interval clamps to the minimum");
            selector.RetryIntervalSeconds = 1e9f;
            Assert.AreEqual(
                PoseProviderSelector.MaxRetryIntervalSeconds,
                selector.RetryIntervalSeconds,
                1e-6f,
                "an absurd interval clamps to the maximum");

            // Select with the sane default so the initial schedule is 2 s.
            selector.RetryIntervalSeconds = PoseProviderSelector.DefaultRetryIntervalSeconds;
            LogAssert.Expect(LogType.Warning, new Regex("bridge not ready"));
            selector.SelectProvider();
            int afterSelection = selector.ProbeAttempts;
            Assert.AreEqual(1, afterSelection, "the selection probe");

            selector.WarningIntervalSeconds = PoseProviderSelector.MaxWarningIntervalSeconds;
            double t0 = Time.realtimeSinceStartupAsDouble;
            for (int frame = 0; frame < 600; frame++)
            {
                selector.UpdateRetriesAt(t0 + (frame * 0.001));
            }

            Assert.AreEqual(
                afterSelection,
                selector.ProbeAttempts,
                "600 frames inside the interval must not probe once (review R-2)");

            selector.UpdateRetriesAt(t0 + PoseProviderSelector.DefaultRetryIntervalSeconds + 0.5);
            Assert.AreEqual(
                afterSelection + 1,
                selector.ProbeAttempts,
                "exactly one probe once the interval elapses");

            // A deserialized scene writes the raw field without the setter; the
            // use site must sanitize that too.
            typeof(PoseProviderSelector)
                .GetField(
                    "retryIntervalSeconds",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(selector, float.NaN);
            selector.UpdateRetriesAt(t0 + 200.0);
            Assert.AreEqual(
                afterSelection + 2,
                selector.ProbeAttempts,
                "the use-site sanitize bounds a poisoned serialized field");
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
            selector.RetryIntervalSeconds = 1f;
            selector.WarningIntervalSeconds = 3600f;
            selector.UpdateRetriesAt(Time.realtimeSinceStartupAsDouble + 2.0);
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

            selector.RetryIntervalSeconds = 1f;
            selector.WarningIntervalSeconds = 3600f;
            double t0 = Time.realtimeSinceStartupAsDouble;
            selector.UpdateRetriesAt(t0 + 2.0);
            selector.UpdateRetriesAt(t0 + 4.0);

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
