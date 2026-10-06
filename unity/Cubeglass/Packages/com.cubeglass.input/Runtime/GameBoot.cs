using Cubeglass.CoreMath;
using Cubeglass.Unity.Rendering;
using UnityEngine;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// Boots the S7 game scene slice (Task 4c): creates the persistent
    /// <see cref="FileWorldStore"/>, wires it into the
    /// <see cref="StreamingRuntime"/> and the <see cref="SaveBatches"/> sink,
    /// and seeds <see cref="GameplayBridge.Player"/> from the authored
    /// <see cref="PlayerRoot"/> pose so the first bridge tick starts at the
    /// scene spawn instead of the <c>PlayerState</c> origin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="FileWorldStore"/> is a plain class and cannot be serialized
    /// into the scene, so the committed scene needs a runtime factory: this
    /// component. It runs in <c>Start</c> (after every <c>Awake</c>, so the
    /// runtime's view manager exists) and before the first <c>Update</c> of
    /// the bridge, which is pinned to -100.
    /// </para>
    /// <para>
    /// The world name defaults to <see cref="DefaultWorldName"/> and the store
    /// root to <see cref="FileWorldStore.DefaultRootDirectory"/>
    /// (<c>Application.persistentDataPath/Cubeglass/saves</c>).
    /// <see cref="WorldNameOverride"/> and <see cref="RootDirectoryOverride"/>
    /// are test seams: the PlayMode smoke points them at a unique temp world
    /// before loading the scene so it never touches the player's real save.
    /// They are process-wide and must be cleared after the test.
    /// </para>
    /// <para>
    /// The spawn seed is one-way (scene pose into <c>PlayerState</c>); the
    /// bridge owns the state afterwards and drives <see cref="PlayerRoot"/>
    /// from it, so nothing reads the transform back. If the bridge is not
    /// ready at <c>Start</c> the seed is deferred and retried every frame
    /// (with one warning), so a late initialization cannot strand the player
    /// at the internal origin (review M-1).
    /// </para>
    /// <para>
    /// S7 HIL defect (2026-10-06): the first player run left the OS cursor on
    /// another display, so the legacy mouse path (look, break, place) never
    /// reached the fullscreen window while the keyboard kept working because
    /// focus followed that window. The boot therefore locks and hides the
    /// cursor in a player (<see cref="ShouldLockCursor"/>) and re-locks it when
    /// the window regains focus; the Editor preview is never locked.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-300)]
    [DisallowMultipleComponent]
    public sealed class GameBoot : MonoBehaviour
    {
        /// <summary>The world name the committed game scene boots.</summary>
        public const string DefaultWorldName = "default";

        [SerializeField] private string worldName = DefaultWorldName;
        [SerializeField] private StreamingRuntime streaming;
        [SerializeField] private SaveBatches saves;
        [SerializeField] private GameplayBridge bridge;
        [SerializeField] private PlayerRoot playerRoot;
        [Tooltip("Lock and hide the OS cursor in the player (never in the Editor preview).")]
        [SerializeField] private bool lockCursorInPlayer = true;

        private FileWorldStore store;
        private bool seeded;
        private bool seedRetryWarned;

        /// <summary>
        /// Test seam: when non-empty, overrides the serialized world name for
        /// the next boot. The scene's serialized value is untouched.
        /// </summary>
        public static string WorldNameOverride { get; set; }

        /// <summary>
        /// Test seam: when non-null, overrides the store root directory for
        /// the next boot.
        /// </summary>
        public static string RootDirectoryOverride { get; set; }

        /// <summary>The store created at boot; null before <c>Start</c> and after destroy.</summary>
        public FileWorldStore Store
        {
            get { return store; }
        }

        /// <summary>The serialized world name; the static override applies at runtime only.</summary>
        public string WorldName
        {
            get { return worldName; }
            set { worldName = value; }
        }

        /// <summary>The streaming runtime the store is wired into.</summary>
        public StreamingRuntime Streaming
        {
            get { return streaming; }
            set { streaming = value; }
        }

        /// <summary>The batched persistence sink the store is wired into.</summary>
        public SaveBatches Saves
        {
            get { return saves; }
            set { saves = value; }
        }

        /// <summary>The bridge whose player state is seeded from the scene spawn.</summary>
        public GameplayBridge Bridge
        {
            get { return bridge; }
            set { bridge = value; }
        }

        /// <summary>The authored spawn the player state is seeded from.</summary>
        public PlayerRoot PlayerRoot
        {
            get { return playerRoot; }
            set { playerRoot = value; }
        }

        /// <summary>Whether the player locks the OS cursor at boot (the Editor preview never locks).</summary>
        public bool LockCursorInPlayer
        {
            get { return lockCursorInPlayer; }
            set { lockCursorInPlayer = value; }
        }

        /// <summary>
        /// The boot-time cursor policy: a player locks and hides the OS cursor,
        /// the Editor preview never does. See the class remarks for the S7 HIL
        /// defect this fixes.
        /// </summary>
        public static bool ShouldLockCursor(bool lockEnabled, bool isEditor)
        {
            return lockEnabled && !isEditor;
        }

        /// <summary>
        /// The shipped <c>config.json</c> values loaded at start (TD-014), or
        /// null when no file was found. <see cref="StereoRig"/> and
        /// <see cref="StreamingRuntime"/> apply the same file in their own
        /// <c>Awake</c>; boot re-reads it once so the loaded source and values
        /// are observable/diagnosable in one place.
        /// </summary>
        public GameConfigValues LoadedConfig { get; private set; }

        /// <summary>The resolved <c>config.json</c> path, or null when absent (TD-014).</summary>
        public string ConfigSourcePath { get; private set; }

        private void Start()
        {
            if (ShouldLockCursor(lockCursorInPlayer, Application.isEditor))
            {
                LockCursor();
            }

            GameConfigValues configValues;
            string configSource;
            string configError;
            if (GameConfigFile.TryLoadDefault(out configValues, out configSource, out configError))
            {
                LoadedConfig = configValues;
                ConfigSourcePath = configSource;
            }
            else if (!string.IsNullOrEmpty(configError))
            {
                Debug.LogWarning("[GameBoot] config.json was not applied: " + configError);
            }

            string name = string.IsNullOrEmpty(WorldNameOverride) ? worldName : WorldNameOverride;
            store = RootDirectoryOverride != null
                ? new FileWorldStore(name, RootDirectoryOverride)
                : new FileWorldStore(name);

            if (streaming != null)
            {
                streaming.Store = store;
                streaming.AppliedEdits = saves;
            }

            if (saves != null)
            {
                saves.Store = store;
            }

            SeedPlayerFromSpawn();
        }

        private void Update()
        {
            if (!seeded)
            {
                SeedPlayerFromSpawn();
            }
        }

        /// <summary>
        /// Re-locks the cursor when the window regains focus in a player (the
        /// OS releases the lock on focus loss, so without this one alt-tab
        /// leaves the mouse path dead again).
        /// </summary>
        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && ShouldLockCursor(lockCursorInPlayer, Application.isEditor))
            {
                LockCursor();
            }
        }

        private static void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        /// <summary>Destroys the store after draining queued writes.</summary>
        private void OnDestroy()
        {
            if (store != null)
            {
                store.Dispose();
                store = null;
            }
        }

        private void SeedPlayerFromSpawn()
        {
            if (seeded)
            {
                return;
            }

            if (bridge == null || playerRoot == null)
            {
                WarnSeedDeferred("the bridge or the player root is not wired");
                return;
            }

            if (!bridge.EnsureInitialized())
            {
                WarnSeedDeferred("the bridge is not initialized yet");
                return;
            }

            seeded = true;
            Vector3 unity = playerRoot.transform.position;
            Vec3 position = UnityConvert.ToUnity(new Vec3(unity.x, unity.y, unity.z));
            Vector3 forward = playerRoot.transform.forward;
            float yaw = -Mathf.Atan2(forward.x, forward.z);
            bridge.Player.Position = position;
            bridge.Player.YawRadians = yaw;
            playerRoot.SetPlayerPose(position, yaw);
        }

        /// <summary>
        /// Review M-1: a one-shot seed that silently no-ops leaves the player
        /// at the internal origin (far below/inside the terrain). Warn once and
        /// let <c>Update</c> retry on later frames, so a bridge that finishes
        /// initializing after <c>Start</c> still seeds the authored spawn.
        /// </summary>
        private void WarnSeedDeferred(string reason)
        {
            if (seedRetryWarned)
            {
                return;
            }

            seedRetryWarned = true;
            Debug.LogWarning(
                "GameBoot: could not seed the player from the scene spawn because " + reason
                + "; retrying every frame until the bridge is ready.");
        }
    }
}
