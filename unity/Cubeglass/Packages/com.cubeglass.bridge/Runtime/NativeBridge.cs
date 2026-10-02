using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Cubeglass.Unity.Bridge.Tests")]

namespace Cubeglass.Unity.Bridge
{
    /// <summary>
    /// Raw P/Invoke imports for <c>cg_unity_bridge.dll</c> (5.12 C ABI).
    /// Production code uses <see cref="BridgeClient"/>; the <c>cg_test_writer_*</c>
    /// imports are the test-only native writer documented in
    /// <c>cpp/bridge/include/cg/bridge/test_writer.h</c> and must never be called
    /// from runtime code.
    /// </summary>
    internal static class NativeBridge
    {
        private const string Library = "cg_unity_bridge";

        // Production bridge (contracts/cg_unity_bridge.h).

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BridgeStatus cg_bridge_open(out IntPtr outHandle);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BridgeStatus cg_bridge_read_head(IntPtr handle, out BridgeHeadSample sample);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BridgeStatus cg_bridge_read_hands(IntPtr handle, out BridgeHandFrame frame);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BridgeStatus cg_bridge_send_command(IntPtr handle, uint command);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void cg_bridge_close(IntPtr handle);

        // Test-only writer (cpp/bridge/include/cg/bridge/test_writer.h).

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BridgeStatus cg_test_writer_create();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BridgeStatus cg_test_writer_open();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern void cg_test_writer_close();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BridgeStatus cg_test_writer_publish_head(ref BridgeHeadSample sample);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BridgeStatus cg_test_writer_publish_hands(ref BridgeHandFrame frame);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BridgeStatus cg_test_writer_set_heartbeat(long heartbeatNs);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BridgeStatus cg_test_writer_publish_head_raw(ulong seqA, ulong seqB, ref BridgeHeadSample sample);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BridgeStatus cg_test_writer_read_command(out uint command, out uint ack);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BridgeStatus cg_test_writer_ack_command(uint ack);
    }

    /// <summary>
    /// Reader-side client for the 5.12 bridge over <c>Local\cubeglass.v1.state</c>.
    /// Create one instance at startup with <see cref="TryOpen"/> and reuse it for
    /// the whole session; the read path writes into caller-owned blittable
    /// structs and allocates nothing, so the client must not be allocated per
    /// rendered frame.
    /// </summary>
    /// <remarks>
    /// Thread safety: an instance is not thread-safe because <see cref="LastStatus"/>
    /// is mutable instance state. The native reads are wait-free seqlock reads
    /// that are safe against a concurrent writer, but callers sharing one
    /// instance across threads must synchronize or use one client per thread.
    /// <see cref="Dispose"/> is idempotent.
    /// </remarks>
    public sealed class BridgeClient : IDisposable
    {
        private IntPtr _handle;

        private BridgeClient(IntPtr handle)
        {
            _handle = handle;
        }

        /// <summary>
        /// Status of the most recent call. <see cref="BridgeStatus.NotReady"/> and
        /// <see cref="BridgeStatus.Timeout"/> are the expected transient outcomes
        /// (no writer yet, or a publish in flight); both map to a <c>false</c>
        /// return from the <c>Try*</c> methods.
        /// </summary>
        public BridgeStatus LastStatus { get; private set; } = BridgeStatus.Ok;

        /// <summary>True while the shared-memory mapping is open.</summary>
        public bool IsOpen
        {
            get { return _handle != IntPtr.Zero; }
        }

        /// <summary>
        /// Maps the shared-memory region. Returns false with a null
        /// <paramref name="client"/> when the region is not ready yet, is
        /// foreign or incompatible, or cannot be mapped; use the overload with
        /// an out status to tell those cases apart.
        /// </summary>
        public static bool TryOpen(out BridgeClient client)
        {
            return TryOpen(out client, out _);
        }

        /// <summary>
        /// Maps the shared-memory region and reports the outcome. On success
        /// <paramref name="status"/> is <see cref="BridgeStatus.Ok"/> and
        /// <paramref name="client"/> is non-null. On failure
        /// <paramref name="client"/> is null and <paramref name="status"/> is
        /// <see cref="BridgeStatus.NotReady"/> (the writer has not created or
        /// initialised the region), <see cref="BridgeStatus.Unsupported"/>
        /// (foreign, incompatible or too-small region) or
        /// <see cref="BridgeStatus.Internal"/> (the mapping failed). A failed
        /// open has no client to carry a <see cref="LastStatus"/>, so this
        /// overload is the only place the failure status survives.
        /// </summary>
        public static bool TryOpen(out BridgeClient client, out BridgeStatus status)
        {
            status = NativeBridge.cg_bridge_open(out IntPtr handle);
            if (status == BridgeStatus.Ok && handle != IntPtr.Zero)
            {
                client = new BridgeClient(handle) { LastStatus = BridgeStatus.Ok };
                return true;
            }

            client = null;
            return false;
        }

        /// <summary>
        /// Reads the newest head sample. False with
        /// <see cref="LastStatus"/> = <see cref="BridgeStatus.NotReady"/> (nothing
        /// published yet) or <see cref="BridgeStatus.Timeout"/> (a publish was in
        /// flight on every attempt). A stale head still returns true with
        /// <see cref="BridgeHeadSample.State"/> forced to
        /// <see cref="TrackState.Lost"/>.
        /// </summary>
        public bool TryReadHead(out BridgeHeadSample sample)
        {
            if (_handle == IntPtr.Zero)
            {
                sample = default;
                LastStatus = BridgeStatus.InvalidArg;
                return false;
            }

            LastStatus = NativeBridge.cg_bridge_read_head(_handle, out sample);
            return LastStatus == BridgeStatus.Ok;
        }

        /// <summary>
        /// Reads the newest hand frame. False with
        /// <see cref="LastStatus"/> = <see cref="BridgeStatus.NotReady"/> (nothing
        /// published yet, or the frame is stale because the writer heartbeat is
        /// older than 250 ms — stale hands are withheld, not returned) or
        /// <see cref="BridgeStatus.Timeout"/> (a publish was in flight on every
        /// attempt).
        /// </summary>
        public bool TryReadHands(out BridgeHandFrame frame)
        {
            if (_handle == IntPtr.Zero)
            {
                frame = default;
                LastStatus = BridgeStatus.InvalidArg;
                return false;
            }

            LastStatus = NativeBridge.cg_bridge_read_hands(_handle, out frame);
            return LastStatus == BridgeStatus.Ok;
        }

        /// <summary>
        /// Writes a command word (recenter, start/stop service). Zero and
        /// <see cref="uint.MaxValue"/> are rejected natively as
        /// <see cref="BridgeStatus.InvalidArg"/>.
        /// </summary>
        public bool SendCommand(uint command)
        {
            if (_handle == IntPtr.Zero)
            {
                LastStatus = BridgeStatus.InvalidArg;
                return false;
            }

            LastStatus = NativeBridge.cg_bridge_send_command(_handle, command);
            return LastStatus == BridgeStatus.Ok;
        }

        /// <summary>Unmaps the region. Safe to call more than once.</summary>
        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                NativeBridge.cg_bridge_close(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
