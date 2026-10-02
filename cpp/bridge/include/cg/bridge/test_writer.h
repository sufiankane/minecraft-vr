#ifndef CG_BRIDGE_TEST_WRITER_H
#define CG_BRIDGE_TEST_WRITER_H

// TEST-ONLY native writer for the dossier 5.6 shared-memory region
// (`Local\cubeglass.v1.state`). NOT part of the production path: the production
// writer is `cg-handservice` (S12) and owns the VITURE device. The bridge DLL
// exports these symbols so the C++ bridge tests in `cpp/tests/bridge/` and the
// C# P/Invoke EditMode tests (S6 Task 2) can script samples through the real
// layout with no hardware. No production bridge function calls into this file.
//
// Single writer only, exactly like the real service: create/open once, publish
// from one thread, close once. All publishing uses the same odd/even seqlock
// protocol and word-wise atomic payload copies as the production writer spec,
// except `cg_test_writer_publish_head_raw`, which exists only to inject the
// torn states a reader must reject.

#include <stdint.h>

#include "cg_types.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Creates the region (or reuses and reinitializes a stale one) and zeroes the
 * header and both slots. One writer per process. */
cg_status cg_test_writer_create(void);

/* Opens an existing region without creating one; CG_ERR_NOT_READY when
 * absent. Does not reinitialize it. */
cg_status cg_test_writer_open(void);

/* Unmaps and releases (POSIX: also unlinks) the region. Null-safe and safe to
 * call when no writer is open. */
void cg_test_writer_close(void);

/* Publishes one head sample with the 5.6 seqlock protocol. */
cg_status cg_test_writer_publish_head(const cg_head_sample *sample);

/* Publishes one hand frame with the same protocol on the hand slot. */
cg_status cg_test_writer_publish_hands(const cg_hand_frame *frame);

/* Stores the writer heartbeat (`ShmHeader::heartbeat_ns`). */
cg_status cg_test_writer_set_heartbeat(cg_time_ns heartbeat_ns);

/* Writes `sample` and the two counters exactly as given, with no protocol:
 * an odd `seq_a` or `seq_b != seq_a` injects the torn-read states a reader
 * must reject. Test-only; the production writer never emits these. */
cg_status cg_test_writer_publish_head_raw(uint64_t seq_a, uint64_t seq_b, const cg_head_sample *sample);

/* Reads the command and ack words a reader-side bridge wrote. Either output
 * may be null to skip it; both null is CG_ERR_INVALID_ARG. 5.12 has no
 * read-ack function, so this writer-side view is the only ack observability. */
cg_status cg_test_writer_read_command(uint32_t *out_command, uint32_t *out_ack);

/* Simulates the service acking the command word (writer-side test seam). */
cg_status cg_test_writer_ack_command(uint32_t ack);

#ifdef __cplusplus
}
#endif

#endif /* CG_BRIDGE_TEST_WRITER_H */
