use std::os::raw::{c_char, c_int, c_uchar};

unsafe extern "C" {
    fn xdelta_decode_file(
        source_path: *const c_char,
        delta_path: *const c_char,
        target_path: *const c_char,
    ) -> c_int;

    fn xdelta_decode_file_with_delta_memory(
        source_path: *const c_char,
        delta_data: *const c_uchar,
        delta_size: usize,
        target_path: *const c_char,
    ) -> c_int;

    fn xdelta_bridge_get_last_error() -> *const c_char;
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn xdelta_decode_file_utf8(
    source_path: *const c_char,
    delta_path: *const c_char,
    target_path: *const c_char,
) -> c_int {
    if source_path.is_null() || delta_path.is_null() || target_path.is_null() {
        return 2;
    }

    unsafe { xdelta_decode_file(source_path, delta_path, target_path) }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn xdelta_decode_file_with_delta_memory_utf8(
    source_path: *const c_char,
    delta_data: *const c_uchar,
    delta_size: usize,
    target_path: *const c_char,
) -> c_int {
    if source_path.is_null() || target_path.is_null() || (delta_data.is_null() && delta_size != 0) {
        return 2;
    }

    unsafe { xdelta_decode_file_with_delta_memory(source_path, delta_data, delta_size, target_path) }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn xdelta_bridge_get_last_error_utf8() -> *const c_char {
    unsafe { xdelta_bridge_get_last_error() }
}
