fn main() {
    cc::Build::new()
        .file("src/xdelta_bridge.c")
        .include("vendor/xdelta3")
        .define("XD3_USE_LARGEFILE64", "1")
        .define("XD3_WIN32", "1")
        .define("XD3_POSIX", "0")
        .define("XD3_STDIO", "0")
        .define("SIZEOF_SIZE_T", "8")
        .define("SIZEOF_UNSIGNED_LONG", "4")
        .define("SIZEOF_UNSIGNED_LONG_LONG", "8")
        .define("SIZEOF_UNSIGNED_INT", "4")
        .define("EXTERNAL_COMPRESSION", "0")
        .define("HAVE_CONFIG_H", "0")
        .warnings(false)
        .compile("xdelta_bridge");

    println!("cargo:rerun-if-changed=src/xdelta_bridge.c");
    println!("cargo:rerun-if-changed=vendor/xdelta3/xdelta3.c");
    println!("cargo:rerun-if-changed=vendor/xdelta3/xdelta3.h");
}
