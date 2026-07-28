#include "lazily_ffi.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int require(int condition, const char *message)
{
    if (condition) {
        return 1;
    }

    fprintf(stderr, "ffi smoke failure: %s\n", message);
    return 0;
}

int main(void)
{
    static const char snapshot[] =
        "{\"Snapshot\":{\"epoch\":1,\"nodes\":[{\"node\":1,\"type_tag\":\"u8\","
        "\"state\":{\"Payload\":[1]}}],\"edges\":[],\"roots\":[1]}}";
    const uint8_t *frame = (const uint8_t *)snapshot;
    const size_t frame_len = strlen(snapshot);

    if (!require(
            lazily_ffi_ipc_message_validate_json(frame, frame_len)
                == LazilyFfiStatus_Ok,
            "snapshot validation")) {
        return EXIT_FAILURE;
    }

    LazilyFfiMessageKind kind = LazilyFfiMessageKind_Unknown;
    if (!require(
            lazily_ffi_ipc_message_kind_json(frame, frame_len, &kind)
                == LazilyFfiStatus_Ok
                && kind == LazilyFfiMessageKind_Snapshot,
            "snapshot classification")) {
        return EXIT_FAILURE;
    }

    LazilyFfiBytes clone = {0};
    if (!require(
            lazily_ffi_ipc_message_clone_json(frame, frame_len, &clone)
                == LazilyFfiStatus_Ok
                && clone.len == frame_len
                && memcmp(clone.ptr, frame, frame_len) == 0,
            "canonical clone")) {
        return EXIT_FAILURE;
    }
    lazily_ffi_bytes_free(clone);

    void *channel = lazily_ffi_channel_new();
    if (!require(channel != NULL, "channel allocation")) {
        return EXIT_FAILURE;
    }

    LazilyFfiBytes received = {0};
    if (!require(
            lazily_ffi_channel_recv_json(channel, &received)
                == LazilyFfiStatus_Empty,
            "empty receive")) {
        return EXIT_FAILURE;
    }
    if (!require(
            lazily_ffi_channel_send_json(channel, frame, frame_len)
                == LazilyFfiStatus_Ok,
            "channel send")) {
        return EXIT_FAILURE;
    }
    if (!require(
            lazily_ffi_channel_recv_json(channel, &received)
                == LazilyFfiStatus_Ok
                && received.len == frame_len
                && memcmp(received.ptr, frame, frame_len) == 0,
            "channel receive")) {
        return EXIT_FAILURE;
    }
    lazily_ffi_bytes_free(received);

    static const char invalid[] = "{\"Unknown\":{}}";
    if (!require(
            lazily_ffi_ipc_message_validate_json(
                (const uint8_t *)invalid,
                strlen(invalid))
                == LazilyFfiStatus_InvalidMessage,
            "invalid frame rejection")) {
        return EXIT_FAILURE;
    }

    if (!require(
            lazily_ffi_channel_free(channel) == LazilyFfiStatus_Ok,
            "channel free")) {
        return EXIT_FAILURE;
    }

    return EXIT_SUCCESS;
}
