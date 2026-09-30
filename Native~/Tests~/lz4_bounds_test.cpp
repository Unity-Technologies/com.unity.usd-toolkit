// Security regression coverage for SECURITY-282834, "Bundled OpenUSD dependency parsers as
// vulnerable/unscanned components (LZ4 1.9.2)" (CWE-1104, CVE-2021-3520).
//
// Every .usdc crate section OpenUSD reads -- tokens, fields, integer arrays -- is decompressed by
// TfFastCompression::DecompressFromBuffer with sizes taken from the file. Before
// Native~/patches/openusd-26.05-lz4-1.10.0.patch:
//   - a 64-bit output size above INT_MAX reached LZ4 as a negative int, which is the
//     CVE-2021-3520 trigger in LZ4 1.9.2 (fixed upstream in 1.9.4);
//   - a chunk size was not checked against the buffer, so LZ4 read past its end;
//   - the chunk count was read as a signed char, so a first byte >= 0x80 gave a negative count
//     and the loop walked off the buffer.
// This drives DecompressFromBuffer in the shipped libusd_ms with each of those inputs. Each
// hostile buffer ends exactly at an inaccessible guard page, so a read past its end faults
// instead of quietly landing on neighbouring memory, and on POSIX each case runs in its own
// child process so a fault is reported as that case failing. On the patched library every case
// passes; on the unpatched one the oversized capacity is mishandled and the out-of-bounds cases
// fault.
//
// Build and run (macOS; adjust the payload directory and library name for Windows/Linux). The
// headers must come from the OpenUSD install the payload was built from:
//   PLUGINS=../../Runtime/Plugins/macOS
//   clang++ -std=c++17 -Wl,-headerpad_max_install_names lz4_bounds_test.cpp \
//       -I <openusd-root>/include "$PLUGINS/libusd_ms.dylib" -o lz4_bounds_test
//   install_name_tool -change "@loader_path/libusd_ms.dylib" \
//       "$(cd "$PLUGINS" && pwd)/libusd_ms.dylib" lz4_bounds_test
//   install_name_tool -change "@loader_path/libtbb.dylib" \
//       "$(cd "$PLUGINS" && pwd)/libtbb.dylib" lz4_bounds_test
//   ./lz4_bounds_test                        # must print PASS
//
// Linux (tests the shipped payload; libusd_ms.so finds libtbb.so.2 through its own $ORIGIN):
//   PLUGINS=$(cd ../../Runtime/Plugins/x86_64/Linux && pwd)
//   g++ -std=c++17 -pthread lz4_bounds_test.cpp -I <openusd-root>/include \
//       "$PLUGINS/lib/libusd_ms.so" -Wl,-rpath,"$PLUGINS/lib" -o lz4_bounds_test
//   ./lz4_bounds_test
//
// Windows (x64 Native Tools prompt). The payload's usd_rt.dll has no import library -- it is
// usd_ms.dll renamed, with its own name string patched -- so link and run against the OpenUSD
// install the payload was copied from, which is the same build:
//   cl /std:c++17 /EHsc /MD /DNOMINMAX lz4_bounds_test.cpp /I <openusd-root>\include ^
//       /link <openusd-root>\lib\usd_ms.lib
//   set PATH=<openusd-root>\lib;<openusd-root>\bin;%PATH%
//   lz4_bounds_test.exe
// There is no fork() on Windows, so the cases run in one process there: a fault ends the run,
// which is still a failure, just without the per-case report.
//
// OpenUSD reports each refused buffer with TF_RUNTIME_ERROR, so expect "Failed to decompress
// data" lines on stderr; they are the refusals this test is checking for.

#include "pxr/pxr.h"
#include "pxr/base/tf/fastCompression.h"

#include <climits>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <functional>
#include <string>
#include <vector>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <sys/mman.h>
#include <sys/wait.h>
#include <unistd.h>
#endif

PXR_NAMESPACE_USING_DIRECTIVE

namespace
{

int failures = 0;

// Runs one case and records it. On POSIX the case runs in a child, so a fault inside libusd_ms
// fails that case rather than ending the test.
void Case(const std::string& what, const std::function<bool()>& body)
{
    bool ok = false;
    std::string note;
#if defined(_WIN32)
    ok = body();
#else
    std::fflush(stdout);
    pid_t child = fork();
    if (child == 0)
    {
        _exit(body() ? 0 : 1);
    }
    int status = 0;
    waitpid(child, &status, 0);
    ok = WIFEXITED(status) && WEXITSTATUS(status) == 0;
    if (WIFSIGNALED(status))
    {
        note = " (crashed: signal " + std::to_string(WTERMSIG(status)) + ")";
    }
#endif
    std::printf("  %s %s%s\n", ok ? "ok  " : "FAIL", what.c_str(), note.c_str());
    if (!ok)
    {
        ++failures;
    }
}

// `bytes` copied so that its last byte is the last readable byte before a guard page.
class Guarded
{
public:
    explicit Guarded(const std::vector<char>& bytes)
    {
#if defined(_WIN32)
        SYSTEM_INFO info;
        GetSystemInfo(&info);
        const size_t page = info.dwPageSize;
#else
        const size_t page = static_cast<size_t>(sysconf(_SC_PAGESIZE));
#endif
        const size_t pages = (bytes.size() + page - 1) / page + 1;
        _length = (pages + 1) * page;
#if defined(_WIN32)
        _base = static_cast<char*>(
            VirtualAlloc(nullptr, _length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
        DWORD old = 0;
        VirtualProtect(_base + pages * page, page, PAGE_NOACCESS, &old);
#else
        _base = static_cast<char*>(
            mmap(nullptr, _length, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON, -1, 0));
        mprotect(_base + pages * page, page, PROT_NONE);
#endif
        _data = _base + pages * page - bytes.size();
        if (!bytes.empty())
        {
            std::memcpy(_data, bytes.data(), bytes.size());
        }
    }

    ~Guarded()
    {
#if defined(_WIN32)
        VirtualFree(_base, 0, MEM_RELEASE);
#else
        munmap(_base, _length);
#endif
    }

    const char* data() const { return _data; }

private:
    char* _base = nullptr;
    char* _data = nullptr;
    size_t _length = 0;
};

std::vector<char> Compress(const std::vector<char>& input)
{
    std::vector<char> out(TfFastCompression::GetCompressedBufferSize(input.size()));
    out.resize(TfFastCompression::CompressToBuffer(input.data(), out.data(), input.size()));
    return out;
}

// Decompresses `bytes` from a guarded copy into a guarded-size output buffer.
size_t Decompress(const std::vector<char>& bytes, size_t claimedCapacity, std::vector<char>* out)
{
    Guarded input(bytes);
    return TfFastCompression::DecompressFromBuffer(input.data(), out->data(), bytes.size(),
                                                   claimedCapacity);
}

}  // namespace

int main()
{
    std::printf("TfFastCompression bounds (SECURITY-282834, CVE-2021-3520)\n");

    std::vector<char> original(4096);
    for (size_t i = 0; i < original.size(); ++i)
    {
        original[i] = static_cast<char>((i * 31) ^ (i >> 3));
    }
    const std::vector<char> compressed = Compress(original);

    // A single LZ4 block without the leading count byte, to build multi-chunk buffers from.
    const std::vector<char> block(compressed.begin() + 1, compressed.end());

    Case("valid data compresses to one chunk and round-trips", [&] {
        std::vector<char> out(original.size());
        return !compressed.empty() && compressed[0] == 0 &&
               Decompress(compressed, out.size(), &out) == original.size() && out == original;
    });

    Case("valid multi-chunk data round-trips", [&] {
        // Two chunks, each the same block, in the layout CompressToBuffer writes for large input.
        std::vector<char> buffer = {2};
        for (int i = 0; i < 2; ++i)
        {
            int32_t size = static_cast<int32_t>(block.size());
            buffer.insert(buffer.end(), reinterpret_cast<char*>(&size),
                          reinterpret_cast<char*>(&size) + sizeof(size));
            buffer.insert(buffer.end(), block.begin(), block.end());
        }
        std::vector<char> out(2 * original.size());
        std::vector<char> expected = original;
        expected.insert(expected.end(), original.begin(), original.end());
        return Decompress(buffer, out.size(), &out) == expected.size() && out == expected;
    });

    Case("capacity above INT_MAX is not handed to LZ4 as a negative int", [&] {
        // What the crate reader passes for a tokens section whose uncompressedSize is 2 GiB.
        std::vector<char> out(original.size());
        return Decompress(compressed, static_cast<size_t>(INT_MAX) + 1, &out) ==
                   original.size() && out == original;
    });

    Case("an empty buffer is not read", [&] {
        std::vector<char> out(64);
        return Decompress({}, out.size(), &out) == 0;
    });

    Case("a chunk count byte >= 0x80 is refused before any chunk is read", [&] {
        // 0x80 read as a signed char is -128, a count the old loop never reached.
        std::vector<char> buffer = {static_cast<char>(0x81)};
        int32_t size = static_cast<int32_t>(block.size());
        buffer.insert(buffer.end(), reinterpret_cast<char*>(&size),
                      reinterpret_cast<char*>(&size) + sizeof(size));
        buffer.insert(buffer.end(), block.begin(), block.end());
        std::vector<char> out(4 * original.size());
        return Decompress(buffer, out.size(), &out) == 0;
    });

    Case("a negative chunk size is refused", [&] {
        std::vector<char> buffer = {1};
        int32_t size = -5;
        buffer.insert(buffer.end(), reinterpret_cast<char*>(&size),
                      reinterpret_cast<char*>(&size) + sizeof(size));
        buffer.insert(buffer.end(), block.begin(), block.end());
        std::vector<char> out(original.size());
        return Decompress(buffer, out.size(), &out) == 0;
    });

    Case("a chunk larger than the rest of the buffer is not read past its end", [&] {
        std::vector<char> buffer = {1};
        int32_t size = static_cast<int32_t>(block.size()) + 4096;
        buffer.insert(buffer.end(), reinterpret_cast<char*>(&size),
                      reinterpret_cast<char*>(&size) + sizeof(size));
        buffer.insert(buffer.end(), block.begin(), block.end());
        std::vector<char> out(original.size());
        return Decompress(buffer, out.size(), &out) == 0;
    });

    Case("a buffer truncated inside a chunk size is not read past its end", [&] {
        std::vector<char> out(64);
        return Decompress({2, 0, 0}, out.size(), &out) == 0;
    });

    std::printf("%s (%d failure%s)\n", failures == 0 ? "PASS" : "FAIL", failures,
                failures == 1 ? "" : "s");
    return failures == 0 ? 0 : 1;
}
