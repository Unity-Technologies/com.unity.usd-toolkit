/*
 * UsdDeflate.c - the zlib inflate the image codecs use, compiled in from the libdeflate that
 * OpenUSD 26.05 vendors (pxr/imaging/plugin/hioOpenEXR/OpenEXR/deflate, v1.18, MIT).
 *
 * UsdImageCodecs.cpp used to look a zlib up at run time (libz.so.1, libz.1.dylib, zlib1.dll) and
 * hand the host managed decoding where none opened. Linux and macOS carry one; Windows does not,
 * and neither does the Unity Editor or the OpenUSD install, so the Windows payload silently
 * decoded every EXR tile and PNG in C# at a fraction of the speed the other platforms had.
 * Building the inflater in gives every platform the same path and the same bytes, with no
 * library to find and no row to add to Native~/dependency-sources: the source is part of the
 * pinned OpenUSD tree, and Native~/CMakeLists.txt checks it file by file against the digests
 * recorded there before compiling it.
 *
 * libdeflate is written to be amalgamated: lib_common.h makes every entry point static, so the
 * library is this one translation unit and none of it is exported. That is how OpenUSD's own
 * hioOpenEXR builds it (OpenEXRCoreUnity.h); only the decompressor and what it needs is here.
 */

#include "lib/lib_common.h"
#include "lib/utils.c"
#include "lib/arm/cpu_features.c"
#include "lib/x86/cpu_features.c"
#include "lib/deflate_decompress.c"
#include "lib/adler32.c"
#include "lib/zlib_decompress.c"

/* The contract of zlib's uncompress(): 0 on success with *outLength set to the bytes written,
   non-zero otherwise. The codecs know every stream's exact decompressed size, so a stream that
   stops short or would run long is an error, as it is with zlib. The decompressor is a few tens
   of kilobytes allocated per call; against a tile's inflate that is noise, and it keeps the
   function reentrant for the worker threads that call it. */
int RUsdZlibUncompress(unsigned char* out, unsigned long* outLength, const unsigned char* in,
                       unsigned long inLength)
{
    struct libdeflate_decompressor* decompressor;
    enum libdeflate_result result;

    if (out == NULL || outLength == NULL || in == NULL)
    {
        return -1;
    }

    decompressor = libdeflate_alloc_decompressor();
    if (decompressor == NULL)
    {
        return -2;
    }

    /* No actual_out_nbytes_ret: with one, libdeflate reports SUCCESS for a stream that stops
       short and leaves the size check to every caller. Without it, anything but exactly
       *outLength bytes is LIBDEFLATE_SHORT_OUTPUT, so the contract above holds here. */
    result = libdeflate_zlib_decompress(decompressor, in, (size_t)inLength, out, (size_t)*outLength,
                                        NULL);
    libdeflate_free_decompressor(decompressor);
    if (result != LIBDEFLATE_SUCCESS)
    {
        return (int)result;
    }

    return 0;
}
