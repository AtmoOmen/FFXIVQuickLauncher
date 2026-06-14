#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>

#define XD3_MAIN 1
#define main xdelta3_embedded_main
#include "../vendor/xdelta3/xdelta3.c"
#undef main

#define XDELTA_BRIDGE_SOURCE_BLOCK_SIZE (1U << 20)

static char xdelta_bridge_last_error_buffer[256];

static void set_last_error_message(const char* message)
{
    if (message == NULL) {
        xdelta_bridge_last_error_buffer[0] = '\0';
        return;
    }

    strncpy_s(xdelta_bridge_last_error_buffer, sizeof(xdelta_bridge_last_error_buffer), message, _TRUNCATE);
}

static wchar_t* utf8_to_wide(const char* value)
{
    int length;
    wchar_t* wide;

    if (value == NULL) {
        return NULL;
    }

    length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value, -1, NULL, 0);
    if (length <= 0) {
        return NULL;
    }

    wide = (wchar_t*)malloc((size_t)length * sizeof(wchar_t));
    if (wide == NULL) {
        return NULL;
    }

    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value, -1, wide, length) <= 0) {
        free(wide);
        return NULL;
    }

    return wide;
}

static int get_file_size64(HANDLE handle, xoff_t* size)
{
    LARGE_INTEGER file_size;

    if (!GetFileSizeEx(handle, &file_size) || file_size.QuadPart < 0) {
        return EIO;
    }

    *size = (xoff_t)file_size.QuadPart;
    return 0;
}

static int write_all(HANDLE handle, const uint8_t* data, usize_t size)
{
    usize_t written_total = 0;

    while (written_total < size) {
        DWORD chunk = (DWORD)xd3_min(size - written_total, (usize_t)0x40000000U);
        DWORD written = 0;

        if (!WriteFile(handle, data + written_total, chunk, &written, NULL)) {
            return EIO;
        }

        if (written == 0) {
            return EIO;
        }

        written_total += written;
    }

    return 0;
}

static int decode_delta_memory_to_file(const uint8_t* source_data, xoff_t source_size, const uint8_t* delta_data, usize_t delta_size, HANDLE target_handle)
{
    xd3_stream stream;
    xd3_config config;
    xd3_source source;
    usize_t input_position = 0;
    int ret;

    memset(&stream, 0, sizeof(stream));
    memset(&config, 0, sizeof(config));
    memset(&source, 0, sizeof(source));

    if ((source_data == NULL && source_size != 0) || (delta_data == NULL && delta_size != 0)) {
        return EINVAL;
    }

    ret = xd3_config_stream(&stream, &config);
    if (ret != 0) {
        set_last_error_message(stream.msg);
        return ret;
    }

    source.blksize = (usize_t)source_size;
    source.max_winsize = source_size;
    source.curblk = source_data;
    source.curblkno = 0;
    source.onblk = (usize_t)source_size;

    ret = xd3_set_source_and_size(&stream, &source, source_size);
    if (ret != 0) {
        xd3_free_stream(&stream);
        set_last_error_message(stream.msg);
        return ret;
    }

    stream.flags |= XD3_FLUSH;
    if (delta_size > 0) {
        usize_t initial_chunk = xd3_min(stream.winsize, delta_size);
        xd3_avail_input(&stream, delta_data, initial_chunk);
        input_position = initial_chunk;
    }

    for (;;) {
        switch ((ret = xd3_decode_input(&stream))) {
        case XD3_INPUT: {
            usize_t chunk_size = xd3_min(stream.winsize, delta_size - input_position);

            if (chunk_size == 0) {
                ret = xd3_close_stream(&stream);
                goto done;
            }

            xd3_avail_input(&stream, delta_data + input_position, chunk_size);
            input_position += chunk_size;
            continue;
        }
        case XD3_OUTPUT:
            ret = write_all(target_handle, stream.next_out, stream.avail_out);
            xd3_consume_output(&stream);
            if (ret != 0) {
                goto done;
            }
            continue;
        case XD3_GOTHEADER:
        case XD3_WINSTART:
        case XD3_WINFINISH:
        case XD3_GETSRCBLK:
            continue;
        case XD3_INTERNAL:
        case XD3_TOOFARBACK:
        case XD3_INVALID_INPUT:
        case XD3_UNIMPLEMENTED:
        case XD3_INVALID:
            set_last_error_message(stream.msg);
            goto done;
        case 0:
            ret = XD3_INTERNAL;
            set_last_error_message("invalid return: 0");
            goto done;
        default:
            set_last_error_message(stream.msg);
            goto done;
        }
    }

done:
    xd3_free_stream(&stream);
    return ret;
}

const char* xdelta_bridge_get_last_error(void)
{
    return xdelta_bridge_last_error_buffer[0] == '\0' ? NULL : xdelta_bridge_last_error_buffer;
}

int xdelta_decode_file(const char* source_path, const char* delta_path, const char* target_path)
{
    const char* argv[] = {
        "xdelta3",
        "-d",
        "-s",
        source_path,
        delta_path,
        target_path,
    };

    return xdelta3_embedded_main(6, (char**)argv);
}

int xdelta_decode_file_with_delta_memory(const char* source_path, const uint8_t* delta_data, size_t delta_size, const char* target_path)
{
    wchar_t* source_path_wide = NULL;
    wchar_t* target_path_wide = NULL;
    HANDLE source_handle = INVALID_HANDLE_VALUE;
    HANDLE target_handle = INVALID_HANDLE_VALUE;
    HANDLE source_mapping = NULL;
    void* source_view = NULL;
    int result;
    int ret;

    if (source_path == NULL || target_path == NULL || (delta_data == NULL && delta_size != 0) || delta_size > UINT32_MAX) {
        return EINVAL;
    }

    set_last_error_message(NULL);

    source_path_wide = utf8_to_wide(source_path);
    target_path_wide = utf8_to_wide(target_path);
    if (source_path_wide == NULL || target_path_wide == NULL) {
        ret = EINVAL;
        goto done;
    }

    source_handle = CreateFileW(source_path_wide, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_RANDOM_ACCESS, NULL);
    if (source_handle == INVALID_HANDLE_VALUE) {
        ret = EIO;
        goto done;
    }

    {
        xoff_t source_size;

        ret = get_file_size64(source_handle, &source_size);
        if (ret != 0) {
            goto done;
        }

        if (source_size > UINT32_MAX) {
            set_last_error_message("source file is too large for memory decode");
            ret = EFBIG;
            goto done;
        }

        if (source_size > 0) {
            source_mapping = CreateFileMappingW(source_handle, NULL, PAGE_READONLY, 0, 0, NULL);
            if (source_mapping == NULL) {
                ret = EIO;
                goto done;
            }

            source_view = MapViewOfFile(source_mapping, FILE_MAP_READ, 0, 0, 0);
            if (source_view == NULL) {
                ret = EIO;
                goto done;
            }
        }

        target_handle = CreateFileW(target_path_wide, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, NULL);
        if (target_handle == INVALID_HANDLE_VALUE) {
            ret = EIO;
            goto done;
        }

        result = decode_delta_memory_to_file((const uint8_t*)source_view, source_size, delta_data, (usize_t)delta_size, target_handle);
        if (result != 0) {
            ret = result;
            goto done;
        }
    }

done:
    if (source_view != NULL) {
        UnmapViewOfFile(source_view);
    }

    if (source_mapping != NULL) {
        CloseHandle(source_mapping);
    }

    if (target_handle != INVALID_HANDLE_VALUE) {
        CloseHandle(target_handle);
    }

    if (source_handle != INVALID_HANDLE_VALUE) {
        CloseHandle(source_handle);
    }

    free(target_path_wide);
    free(source_path_wide);
    return ret;
}
