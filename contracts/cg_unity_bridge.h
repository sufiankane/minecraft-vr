#ifndef CG_UNITY_BRIDGE_H
#define CG_UNITY_BRIDGE_H

#include "cg_types.h"

#ifdef __cplusplus
extern "C" {
#endif

cg_status cg_bridge_open(void** out_handle);                  /* maps shared memory */
cg_status cg_bridge_read_head(void* h, cg_head_sample* out);  /* wait-free */
cg_status cg_bridge_read_hands(void* h, cg_hand_frame* out);  /* seqlock read */
cg_status cg_bridge_send_command(void* h, uint32_t cmd);      /* recenter, start/stop service */
void      cg_bridge_close(void* h);

#ifdef __cplusplus
}
#endif

#endif /* CG_UNITY_BRIDGE_H */
