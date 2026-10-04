using System;
using Cubeglass.Unity.Bridge;
using UnityEngine;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// Why the synthetic pose fallback is active (I-1 of the 2026-10-04 Unity
    /// review): the overlay, the tests and the recurring warning use one enum
    /// instead of inferring the cause from a pair of booleans.
    /// </summary>
    public enum PoseFallbackReason
    {
        /// <summary>The bridge supplies poses; no fallback is active.</summary>
        None = 0,

        /// <summary>The region does not exist yet (no writer): <c>NotReady</c>; retried.</summary>
        NotReady = 1,

        /// <summary>The native plugin could not be loaded (missing/wrong arch/stale exports).</summary>
        PluginUnavailable = 2,

        /// <summary>The region exists but is foreign/incompatible or mapping failed.</summary>
        BridgeFailed = 3,

        /// <summary><see cref="PoseProviderSelector.ForceSyntheticForTests"/> chose the fallback deliberately.</summary>
        ForcedSynthetic = 4,
    }

    /// <summary>
    /// One bridge probe outcome. Production builds it from
    /// <see cref="BridgeClient.TryOpen"/>; tests inject their own through
    /// <see cref="PoseProviderSelector.BridgeProbeOverrideForTests"/> so the
    /// retry loop can be exercised in EditMode.
    /// </summary>
    public readonly struct PoseBridgeProbe
    {
        private PoseBridgeProbe(
            bool ready, IPoseProvider provider, IDisposable lease, BridgeStatus status, bool pluginUnavailable)
        {
            Ready = ready;
            Provider = provider;
            Lease = lease;
            Status = status;
            PluginUnavailable = pluginUnavailable;
        }

        /// <summary>True when the probe opened the bridge and carries a provider.</summary>
        public bool Ready { get; }

        /// <summary>The provider to point the late latch at; null when not ready.</summary>
        public IPoseProvider Provider { get; }

        /// <summary>Resource to dispose when the selector goes away; may be null.</summary>
        public IDisposable Lease { get; }

        /// <summary>The failure status when not ready.</summary>
        public BridgeStatus Status { get; }

        /// <summary>True when the native plugin itself is unavailable.</summary>
        public bool PluginUnavailable { get; }

        /// <summary>Builds a successful probe; <paramref name="lease"/> may be null.</summary>
        public static PoseBridgeProbe Opened(IPoseProvider provider, IDisposable lease)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            return new PoseBridgeProbe(true, provider, lease, BridgeStatus.Ok, false);
        }

        /// <summary>Builds a not-ready probe with the native failure status.</summary>
        public static PoseBridgeProbe NotReady(BridgeStatus status)
        {
            return new PoseBridgeProbe(false, null, null, status, false);
        }

        /// <summary>Builds a probe for an unloadable native plugin.</summary>
        public static PoseBridgeProbe PluginMissing()
        {
            return new PoseBridgeProbe(false, null, null, BridgeStatus.NotReady, true);
        }
    }

    /// <summary>
    /// Scene bootstrap that prefers the production bridge: it tries to map the
    /// 5.12 shared-memory region and, when one is ready, points the
    /// <see cref="LateLatchPose"/> at a <see cref="BridgePoseProvider"/>.
    /// Otherwise it falls back to the serialized synthetic provider (the
    /// calibration scene's <c>SyntheticPoseProvider</c>), so PlayMode tests and
    /// the HIL run work with no writer present.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The probe is no longer one-shot (I-1).</b> While the fallback is
    /// active the selector re-probes on <see cref="RetryIntervalSeconds"/>
    /// (default 2 s, up to <see cref="MaxRetryAttempts"/> when positive,
    /// otherwise until success) from <see cref="UpdateRetries"/>, which the
    /// <c>Update</c> hook calls once per frame and which allocates nothing on
    /// the common path. A writer that appears after boot is therefore picked
    /// up instead of leaving a static forward view forever.
    /// </para>
    /// <para>
    /// <b>The fallback is loud.</b> Entering a fallback reason logs one
    /// warning and, while it stays active, <see cref="UpdateRetries"/> repeats
    /// a rate-limited warning (every <see cref="WarningIntervalSeconds"/>,
    /// default 10 s) naming the reason: a missing plugin
    /// (<see cref="PoseFallbackReason.PluginUnavailable"/>) is distinct from a
    /// writer that has not created the region yet
    /// (<see cref="PoseFallbackReason.NotReady"/>) and from a
    /// foreign/incompatible region (<see cref="PoseFallbackReason.BridgeFailed"/>).
    /// <see cref="UsingBridge"/> and <see cref="FallbackReason"/> are public
    /// state, and <see cref="StateChanged"/> fires on every transition, so the
    /// overlay and tests can surface the degraded tracking.
    /// </para>
    /// <para>
    /// A missing or wrong-architecture <c>cg_unity_bridge.dll</c> still falls
    /// back rather than throwing, but it is never silent: the selector logs an
    /// explicit warning and sets <see cref="PluginUnavailable"/>, so a
    /// packaging regression is visible in the player log, the overlay and the
    /// tests instead of looking like "no writer running". The bridge client is
    /// disposed with this component. This is a calibration/scene helper, not a
    /// per-frame path.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class PoseProviderSelector : MonoBehaviour
    {
        /// <summary>Default seconds between bridge probes while the fallback is active.</summary>
        public const float DefaultRetryIntervalSeconds = 2f;

        /// <summary>Default seconds between recurring fallback warnings.</summary>
        public const float DefaultWarningIntervalSeconds = 10f;

        [SerializeField] private LateLatchPose lateLatch;
        [SerializeField] private MonoBehaviour syntheticFallback;
        [SerializeField] private float retryIntervalSeconds = DefaultRetryIntervalSeconds;
        [SerializeField] private float warningIntervalSeconds = DefaultWarningIntervalSeconds;

        private IDisposable bridgeLease;
        private double nextProbeTime;
        private double lastFallbackWarningTime;
        private int probeAttempts;
        private int fallbackWarnings;

        /// <summary>
        /// Test seam: when true, <c>Awake</c> skips the bridge probe and
        /// selects <see cref="SyntheticFallback"/> directly, so a live shared
        /// region on the machine cannot take the pose path away from a test
        /// that scripts the synthetic provider. Process-wide; callers must
        /// clear it.
        /// </summary>
        public static bool ForceSyntheticForTests { get; set; }

        /// <summary>
        /// Test seam: when true, the bridge probe behaves as if
        /// <c>cg_unity_bridge.dll</c> were missing (a
        /// <see cref="DllNotFoundException"/>), so tests can pin the explicit
        /// plugin-unavailable warning and fallback without a broken plugin
        /// installation. Process-wide; callers must clear it.
        /// </summary>
        public static bool ForceMissingPluginForTests { get; set; }

        /// <summary>
        /// Test seam: when set, the bridge probe calls this instead of
        /// <see cref="BridgeClient.TryOpen"/>, so EditMode tests can script
        /// <c>NotReady → Ok</c> transitions. The override owns its provider and
        /// lease. Process-wide; callers must clear it.
        /// </summary>
        public static Func<PoseBridgeProbe> BridgeProbeOverrideForTests { get; set; }

        /// <summary>True when the bridge region was mapped and is supplying poses.</summary>
        public bool UsingBridge { get; private set; }

        /// <summary>Why the synthetic fallback is active; <see cref="PoseFallbackReason.None"/> while on the bridge.</summary>
        public PoseFallbackReason FallbackReason { get; private set; } = PoseFallbackReason.None;

        /// <summary>
        /// True when the native plugin could not be loaded (missing, wrong
        /// architecture or stale exports). The synthetic fallback is still
        /// selected; this flag and the accompanying warning make the degraded
        /// tracking explicit for the overlay and tests.
        /// </summary>
        public bool PluginUnavailable { get; private set; }

        /// <summary>Probes performed since <see cref="SelectProvider"/> (0 before the first).</summary>
        public int ProbeAttempts
        {
            get { return probeAttempts; }
        }

        /// <summary>Seconds between retry probes while the fallback is active.</summary>
        public float RetryIntervalSeconds
        {
            get { return retryIntervalSeconds; }
            set { retryIntervalSeconds = value; }
        }

        /// <summary>Seconds between recurring fallback warnings.</summary>
        public float WarningIntervalSeconds
        {
            get { return warningIntervalSeconds; }
            set { warningIntervalSeconds = value; }
        }

        /// <summary>Maximum retry probes; 0 (default) keeps retrying until the bridge opens.</summary>
        public int MaxRetryAttempts { get; set; }

        /// <summary>Fallback warnings emitted (entry plus rate-limited recurring); test/diagnostic.</summary>
        public int FallbackWarnings
        {
            get { return fallbackWarnings; }
        }

        /// <summary>Raised on every <see cref="UsingBridge"/>/<see cref="FallbackReason"/> transition.</summary>
        public event Action StateChanged;

        /// <summary>The late latch whose provider is selected; defaults to the sibling.</summary>
        public LateLatchPose LateLatch
        {
            get { return lateLatch; }
            set { lateLatch = value; }
        }

        /// <summary>The fallback provider used when the bridge is absent; must implement <see cref="IPoseProvider"/>.</summary>
        public MonoBehaviour SyntheticFallback
        {
            get { return syntheticFallback; }
            set { syntheticFallback = value; }
        }

        private void Awake()
        {
            SelectProvider();
        }

        private void Update()
        {
            UpdateRetries();
        }

        /// <summary>
        /// Selects the pose source: bridge first, synthetic fallback otherwise.
        /// Public so EditMode tests can drive the selection without relying on
        /// <c>Awake</c> (which does not run for a component added in edit mode).
        /// </summary>
        public void SelectProvider()
        {
            if (lateLatch == null)
            {
                lateLatch = GetComponent<LateLatchPose>();
            }

            if (lateLatch == null)
            {
                Debug.LogError("PoseProviderSelector: no LateLatchPose to configure.", this);
                enabled = false;
                return;
            }

            probeAttempts = 0;
            nextProbeTime = 0.0;
            lastFallbackWarningTime = Now();

            if (ForceSyntheticForTests)
            {
                ApplyFallback(PoseFallbackReason.ForcedSynthetic, pluginUnavailable: false);
                return;
            }

            ProbeBridgeNow();
        }

        /// <summary>
        /// Performs one bridge probe immediately and updates the selection.
        /// Returns true when the bridge is now supplying poses. Public so
        /// EditMode tests can drive the retry without a frame loop; the
        /// per-frame hook is <see cref="UpdateRetries"/>.
        /// </summary>
        public bool ProbeBridgeNow()
        {
            if (UsingBridge)
            {
                return true;
            }

            if (ForceSyntheticForTests)
            {
                ApplyFallback(PoseFallbackReason.ForcedSynthetic, pluginUnavailable: false);
                return false;
            }

            probeAttempts++;
            Func<PoseBridgeProbe> probeOverride = BridgeProbeOverrideForTests;
            PoseBridgeProbe probe = probeOverride != null ? probeOverride() : ProbeBridge();
            if (probe.Ready)
            {
                DisposeBridgeLease();
                bridgeLease = probe.Lease;
                lateLatch.Provider = probe.Provider;
                PluginUnavailable = false;
                SetState(usingBridge: true, PoseFallbackReason.None);
                return true;
            }

            PoseFallbackReason reason = probe.PluginUnavailable
                ? PoseFallbackReason.PluginUnavailable
                : (probe.Status == BridgeStatus.NotReady
                    ? PoseFallbackReason.NotReady
                    : PoseFallbackReason.BridgeFailed);
            ApplyFallback(reason, probe.PluginUnavailable);
            return false;
        }

        /// <summary>
        /// One timer step: while a fallback is active, emits the rate-limited
        /// recurring warning and re-probes when the retry interval has elapsed
        /// and the attempt budget allows. Called from <c>Update</c>; a no-op
        /// once the bridge is selected, so it allocates nothing per frame.
        /// </summary>
        public void UpdateRetries()
        {
            if (UsingBridge || lateLatch == null || FallbackReason == PoseFallbackReason.None)
            {
                return;
            }

            double now = Now();
            if (FallbackReason != PoseFallbackReason.ForcedSynthetic
                && now - lastFallbackWarningTime >= warningIntervalSeconds)
            {
                lastFallbackWarningTime = now;
                WarnFallback(recurring: true);
            }

            if (ForceSyntheticForTests)
            {
                return;
            }

            if (MaxRetryAttempts > 0 && probeAttempts >= MaxRetryAttempts)
            {
                return;
            }

            if (now < nextProbeTime)
            {
                return;
            }

            nextProbeTime = now + Math.Max(0.0, (double)retryIntervalSeconds);
            ProbeBridgeNow();
        }

        private static PoseBridgeProbe ProbeBridge()
        {
            try
            {
                if (ForceMissingPluginForTests)
                {
                    throw new DllNotFoundException("test: cg_unity_bridge.dll is unavailable");
                }

                BridgeClient opened;
                BridgeStatus status;
                if (BridgeClient.TryOpen(out opened, out status))
                {
                    return PoseBridgeProbe.Opened(new BridgePoseProvider(opened), opened);
                }

                return PoseBridgeProbe.NotReady(status);
            }
            catch (DllNotFoundException)
            {
                return PoseBridgeProbe.PluginMissing();
            }
            catch (EntryPointNotFoundException)
            {
                return PoseBridgeProbe.PluginMissing();
            }
            catch (BadImageFormatException)
            {
                return PoseBridgeProbe.PluginMissing();
            }
        }

        private void ApplyFallback(PoseFallbackReason reason, bool pluginUnavailable)
        {
            SelectSyntheticFallback();
            PluginUnavailable = pluginUnavailable;
            bool changed = SetState(usingBridge: false, reason);
            if (changed)
            {
                lastFallbackWarningTime = Now();
                if (reason != PoseFallbackReason.ForcedSynthetic)
                {
                    WarnFallback(recurring: false);
                }
            }
        }

        private bool SetState(bool usingBridge, PoseFallbackReason reason)
        {
            if (UsingBridge == usingBridge && FallbackReason == reason)
            {
                return false;
            }

            UsingBridge = usingBridge;
            FallbackReason = reason;
            Action handler = StateChanged;
            if (handler != null)
            {
                handler();
            }

            return true;
        }

        private void SelectSyntheticFallback()
        {
            if (syntheticFallback is IPoseProvider fallback)
            {
                lateLatch.Provider = fallback;
            }
            else
            {
                Debug.LogError(
                    "PoseProviderSelector: no bridge and no synthetic fallback; the rig keeps its start pose.",
                    this);
            }
        }

        private void WarnFallback(bool recurring)
        {
            fallbackWarnings++;
            string prefix = recurring
                ? "PoseProviderSelector: still on the synthetic pose fallback ("
                : "PoseProviderSelector: using the synthetic pose fallback (";
            switch (FallbackReason)
            {
                case PoseFallbackReason.PluginUnavailable:
                    Debug.LogWarning(
                        prefix
                            + "cg_unity_bridge could not be loaded: missing, wrong architecture or stale exports"
                            + "); this run has no real head tracking.",
                        this);
                    break;
                case PoseFallbackReason.NotReady:
                    Debug.LogWarning(
                        prefix
                            + "bridge not ready: no writer has created the region yet; retrying every "
                            + retryIntervalSeconds + " s); this run has no real head tracking until the writer appears.",
                        this);
                    break;
                case PoseFallbackReason.BridgeFailed:
                    Debug.LogWarning(
                        prefix
                            + "bridge open failed with a foreign or incompatible region); this run has no real head tracking.",
                        this);
                    break;
            }
        }

        private void DisposeBridgeLease()
        {
            IDisposable lease = bridgeLease;
            bridgeLease = null;
            if (lease != null)
            {
                lease.Dispose();
            }
        }

        private static double Now()
        {
            return Time.realtimeSinceStartupAsDouble;
        }

        private void OnDestroy()
        {
            DisposeBridgeLease();
        }
    }
}
