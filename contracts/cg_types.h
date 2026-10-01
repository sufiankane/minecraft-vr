#ifndef CG_TYPES_H
#define CG_TYPES_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>
#define CG_ABI_VERSION 1

typedef int64_t cg_time_ns;            /* HostTime, monotonic nanoseconds */

typedef struct { float x, y, z; }         cg_vec3;
typedef struct { float w, x, y, z; }      cg_quat;        /* unit quaternion */
typedef struct { cg_vec3 p; cg_quat q; }  cg_pose;        /* position metres, rotation */

typedef enum {
  CG_OK = 0,
  CG_ERR_INVALID_ARG = 1,
  CG_ERR_NOT_READY = 2,
  CG_ERR_DEVICE = 3,
  CG_ERR_TIMEOUT = 4,
  CG_ERR_UNSUPPORTED = 5,
  CG_ERR_INTERNAL = 6
} cg_status;

typedef enum { CG_TRACK_STABLE = 0, CG_TRACK_UNSTABLE = 1, CG_TRACK_LOST = 2 } cg_track_state;

typedef struct {
  cg_time_ns  host_time;     /* time the sample refers to */
  cg_pose     pose;          /* head pose; in 3DoF mode p is (0,0,0) */
  cg_track_state state;
  uint32_t    sequence;
} cg_head_sample;

#ifdef __cplusplus
}
#endif

#endif /* CG_TYPES_H */
