using UnityEngine;

namespace Cubeglass.Unity.Input
{
    /// <summary>
    /// Minimal Unity-side mirror of the <c>Cubeglass.Gameplay.InputFrame</c>
    /// fields the Unity client needs (dossier 5.11). Gameplay is a plain .NET
    /// library outside Unity, so this struct is the S7 adapter placeholder:
    /// S7 converts it to <c>InputFrame</c> (Move → <c>Vector2f</c>, the button
    /// booleans to <c>ButtonState</c> edges, HotbarDelta to -1/0/+1) and adds
    /// pointer/quality, which have no Unity counterpart yet.
    /// </summary>
    /// <remarks>
    /// A readonly value type: polling allocates nothing and the frame can be
    /// copied by value into the gameplay layer.
    /// </remarks>
    public readonly struct UnityMoveInput
    {
        /// <summary>Movement axes: x = strafe, y = forward, each in [-1, 1].</summary>
        public readonly Vector2 Move;

        /// <summary>Snap-turn amount in degrees for this frame (0 when no snap).</summary>
        public readonly float TurnSnap;

        /// <summary>Primary action (select / click).</summary>
        public readonly bool Primary;

        /// <summary>Secondary action (back / alternate).</summary>
        public readonly bool Secondary;

        /// <summary>A recentre was requested this frame.</summary>
        public readonly bool RecenterPressed;

        /// <summary>Hotbar step for this frame: -1, 0 or +1.</summary>
        public readonly int HotbarDelta;

        /// <summary>Creates a frame with every field set explicitly.</summary>
        public UnityMoveInput(
            Vector2 move,
            float turnSnap,
            bool primary,
            bool secondary,
            bool recenterPressed,
            int hotbarDelta)
        {
            Move = move;
            TurnSnap = turnSnap;
            Primary = primary;
            Secondary = secondary;
            RecenterPressed = recenterPressed;
            HotbarDelta = hotbarDelta;
        }

        /// <summary>The neutral frame: no move, no turn, no buttons, no recentre, no hotbar step.</summary>
        public static UnityMoveInput Neutral
        {
            get { return default(UnityMoveInput); }
        }
    }

    /// <summary>
    /// Maps the active Unity input backend to <see cref="UnityMoveInput"/>.
    /// With the legacy Input Manager enabled it polls the configured axes
    /// (default <c>Horizontal</c>/<c>Vertical</c>), <c>Fire1</c>/<c>Fire2</c>,
    /// <c>Space</c>/<c>F</c>, <c>Q</c>/<c>E</c> snap turns, <c>R</c> recentre and
    /// the scroll wheel hotbar step, allocation-free.
    /// </summary>
    /// <remarks>
    /// This project uses the legacy Input Manager (<c>activeInputHandler: 0</c>).
    /// When S7 adds <c>com.unity.inputsystem</c>, extend <see cref="Poll"/>
    /// behind the same contract (device-based reads for move/turn/buttons);
    /// until then a project configured for the new backend alone returns
    /// <see cref="UnityMoveInput.Neutral"/> instead of throwing.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class UnityInputProvider : MonoBehaviour
    {
        [SerializeField] private string moveAxisX = "Horizontal";
        [SerializeField] private string moveAxisY = "Vertical";
        [SerializeField] private float turnSnapDegrees = 30f;
        [SerializeField] private bool invertHotbarScroll;

        /// <summary>The last frame sampled by <see cref="Update"/>.</summary>
        public UnityMoveInput Latest { get; private set; }

        private void Update()
        {
            Latest = Poll();
        }

        /// <summary>
        /// Reads the current Unity input state into a <see cref="UnityMoveInput"/>.
        /// Allocation-free and safe to call from tests.
        /// </summary>
        public UnityMoveInput Poll()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            float moveX = Mathf.Clamp(UnityEngine.Input.GetAxisRaw(moveAxisX), -1f, 1f);
            float moveY = Mathf.Clamp(UnityEngine.Input.GetAxisRaw(moveAxisY), -1f, 1f);

            float turnSnap = 0f;
            if (UnityEngine.Input.GetKeyDown(KeyCode.Q))
            {
                turnSnap -= turnSnapDegrees;
            }

            if (UnityEngine.Input.GetKeyDown(KeyCode.E))
            {
                turnSnap += turnSnapDegrees;
            }

            bool primary = UnityEngine.Input.GetButton("Fire1") || UnityEngine.Input.GetKey(KeyCode.Space);
            bool secondary = UnityEngine.Input.GetButton("Fire2") || UnityEngine.Input.GetKey(KeyCode.F);
            bool recenter = UnityEngine.Input.GetKeyDown(KeyCode.R);

            float scroll = UnityEngine.Input.mouseScrollDelta.y;
            int hotbar = scroll > 0.01f ? 1 : (scroll < -0.01f ? -1 : 0);
            if (invertHotbarScroll)
            {
                hotbar = -hotbar;
            }

            return new UnityMoveInput(new Vector2(moveX, moveY), turnSnap, primary, secondary, recenter, hotbar);
#else
            // New-input-only project: the Input System mapping lands with S7.
            return UnityMoveInput.Neutral;
#endif
        }
    }
}
