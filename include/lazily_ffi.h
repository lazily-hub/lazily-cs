#ifndef LAZILY_FFI_H
#define LAZILY_FFI_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint8_t *ptr;
    size_t len;
} LazilyFfiBytes;

typedef enum {
    LazilyFfiStatus_Ok = 0,
    LazilyFfiStatus_Empty = 1,
    LazilyFfiStatus_NullPointer = 2,
    LazilyFfiStatus_InvalidMessage = 3,
    LazilyFfiStatus_EncodeFailed = 4,
    LazilyFfiStatus_Panic = 5
} LazilyFfiStatus;

typedef enum {
    LazilyFfiMessageKind_Unknown = 0,
    LazilyFfiMessageKind_Snapshot = 1,
    LazilyFfiMessageKind_Delta = 2,
    LazilyFfiMessageKind_CrdtSync = 3,
    LazilyFfiMessageKind_ResyncRequest = 4,
    LazilyFfiMessageKind_OutboxAck = 5
} LazilyFfiMessageKind;

LazilyFfiStatus lazily_ffi_ipc_message_validate_json(
    const uint8_t *ptr,
    size_t len);
LazilyFfiStatus lazily_ffi_ipc_message_kind_json(
    const uint8_t *ptr,
    size_t len,
    LazilyFfiMessageKind *kind);
LazilyFfiStatus lazily_ffi_ipc_message_clone_json(
    const uint8_t *ptr,
    size_t len,
    LazilyFfiBytes *output);
void lazily_ffi_bytes_free(LazilyFfiBytes bytes);

void *lazily_ffi_channel_new(void);
LazilyFfiStatus lazily_ffi_channel_free(void *handle);
LazilyFfiStatus lazily_ffi_channel_send_json(
    void *handle,
    const uint8_t *ptr,
    size_t len);
LazilyFfiStatus lazily_ffi_channel_recv_json(
    void *handle,
    LazilyFfiBytes *output);

#ifdef __cplusplus
}
#endif

#endif
