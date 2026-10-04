using System;
using Cubeglass.Unity.Bridge;
using UnityEngine;

namespace Cubeglass.Unity.Rendering
{
    /// <summary>
    /// Scene bootstrap that prefers the production bridge: on <c>Awake</c> it
    /// tries to map the 5.12 shared-memory region and, when one is ready, points
    /// the <see cref="LateLatchPose"/> at a <see cref="BridgePoseProvider"/>.
    /// Otherwise it falls back to the serialized synthetic provider (the
    /// calibration scene's <c>SyntheticPoseProvider</c>), so PlayMode tests and
    /// the HIL run work with no writer present.
    /// </summary>
    /// <remarks>
    /// A missing or wrong-architecture <c>cg_unity_bridge.dll</c> still falls
    /// back rather than throwing, but it is no longer silent: the selector logs
    /// an explicit warning and sets <see cref="PluginUnavailable"/>, so a
    /// packaging regression is visible in the player log, the overlay and the
    /// tests instead of looking like "no writer running". The bridge client is
    /// disposed with this component. This is a calibration/scene helper, not a
    /// per-frame path.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class PoseProviderSelector : MonoBehaviour
    {
        [SerializeField] private LateLatchPose lateLatch;
        [SerializeField] private MonoBehaviour syntheticFallback;

        private BridgeClient client;

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

        /// <summary>True when the bridge region was mapped and is supplying poses.</summary>
        public bool UsingBridge { get; private set; }

        /// <summary>
        /// True when the native plugin could not be loaded (missing, wrong
        /// architecture or stale exports). The synthetic fallback is still
        /// selected; this flag and the accompanying error-free warning make the
        /// degraded tracking explicit for the overlay and tests.
        /// </summary>
        public bool PluginUnavailable { get; private set; }

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

            if (ForceSyntheticForTests)
            {
                SelectSyntheticFallback();
                return;
            }

            if (TryOpenBridge(out BridgeClient opened, out BridgeStatus status, out bool pluginUnavailable))
            {
                client = opened;
                lateLatch.Provider = new BridgePoseProvider(client);
                UsingBridge = true;
                PluginUnavailable = false;
                return;
            }

            UsingBridge = false;
            PluginUnavailable = pluginUnavailable;
            if (pluginUnavailable)
            {
                Debug.LogWarning(
                    "PoseProviderSelector: cg_unity_bridge could not be loaded (missing, wrong architecture or stale exports); "
                        + "using the synthetic fallback — this run has no real head tracking.",
                    this);
            }
            else if (status != BridgeStatus.NotReady)
            {
                Debug.LogWarning(
                    "PoseProviderSelector: bridge open failed with " + status + "; using the synthetic fallback.",
                    this);
            }

            SelectSyntheticFallback();
        }

        private void SelectSyntheticFallback()
        {
            UsingBridge = false;
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

        private void OnDestroy()
        {
            if (client != null)
            {
                client.Dispose();
                client = null;
            }
        }

        private bool TryOpenBridge(out BridgeClient opened, out BridgeStatus status, out bool pluginUnavailable)
        {
            opened = null;
            status = BridgeStatus.NotReady;
            pluginUnavailable = false;
            try
            {
                if (ForceMissingPluginForTests)
                {
                    throw new DllNotFoundException("test: cg_unity_bridge.dll is unavailable");
                }

                return BridgeClient.TryOpen(out opened, out status);
            }
            catch (DllNotFoundException)
            {
                pluginUnavailable = true;
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                pluginUnavailable = true;
                return false;
            }
            catch (BadImageFormatException)
            {
                pluginUnavailable = true;
                return false;
            }
        }
    }
}
