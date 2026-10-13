// The image codecs' inflate, on this platform's payload (2026-10-11). RUsd_ImageBuild decodes a
// PNG and a ZIP-compressed OpenEXR, both zlib streams, so this is what proves the inflater is
// compiled in: a payload without one reports "zlib not available" and the host decodes in C#,
// which is what the Windows payload did while Linux and macOS found a system zlib. The fixtures
// are tiny and their pixels are known, so the bytes are checked, not just the return code.
//
// Build and run like import_uv_test (REBUILD_WINDOWS_LINUX.md Gate 6), with
// image_codec_fixture.png and image_codec_fixture.exr next to it:
//   image_codec_test image_codec_fixture.png image_codec_fixture.exr

#include "unity_usd_toolkit_native.h"

#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <string>
#include <vector>

namespace
{

int failures = 0;

void Check(bool condition, const std::string& what)
{
    std::printf("  %s %s\n", condition ? "ok  " : "FAIL", what.c_str());
    if (!condition)
    {
        ++failures;
    }
}

std::vector<unsigned char> ReadFile(const char* path)
{
    std::ifstream in(path, std::ios::binary);
    return std::vector<unsigned char>((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
}

bool Near(int a, int b, int tolerance = 1)
{
    return std::abs(a - b) <= tolerance;
}

// Decodes to RGBA8 (mode 0) and checks every pixel against `expected(x, y, channel)`.
template <typename Expected>
void Decode(const char* label, const std::vector<unsigned char>& bytes, Expected expected)
{
    std::printf("%s\n", label);
    char reason[512] = {};
    void* image = RUsd_ImageBuild(bytes.data(), static_cast<int>(bytes.size()), 0, 0, 0, reason, sizeof(reason));
    Check(image != nullptr, std::string("decodes natively") + (image ? "" : std::string(": ") + reason));
    if (image == nullptr)
    {
        return;
    }

    int width = 0, height = 0, mode = -1, mips = 0, hasAlpha = 0, columns = 0, rows = 0, minU = 0, minV = 0, tiles = 0;
    long long byteCount = 0;
    RUsd_ImageInfo(image, &width, &height, &mode, &byteCount, &mips, &hasAlpha, &columns, &rows, &minU, &minV, &tiles);
    Check(width == 4 && height == 4, "4 x 4");
    Check(mode == 0 && byteCount == 4 * 4 * 4, "RGBA8, 64 bytes");

    const unsigned char* data = RUsd_ImageData(image);
    int wrong = 0;
    for (int y = 0; y < 4 && data != nullptr && byteCount == 64; ++y)
    {
        for (int x = 0; x < 4; ++x)
        {
            for (int c = 0; c < 4; ++c)
            {
                const int actual = data[(y * 4 + x) * 4 + c];
                if (!Near(actual, expected(x, y, c)))
                {
                    if (wrong < 4)
                    {
                        std::printf("       (%d,%d)[%d] = %d, expected %d\n", x, y, c, actual, expected(x, y, c));
                    }
                    ++wrong;
                }
            }
        }
    }
    Check(data != nullptr && wrong == 0, "every pixel matches");
    RUsd_ImageRelease(image);
}

}  // namespace

int main(int argc, char** argv)
{
    if (argc < 3)
    {
        std::printf("usage: image_codec_test <image_codec_fixture.png> <image_codec_fixture.exr>\n");
        return 2;
    }

    std::printf("API %d\n", RUsd_GetApiVersion());
    const std::vector<unsigned char> png = ReadFile(argv[1]);
    const std::vector<unsigned char> exr = ReadFile(argv[2]);
    Check(!png.empty() && !exr.empty(), "fixtures read");

    // Both decoders hand rows bottom-up, as the texture wants them, so row y of the file is row
    // 3 - y of the result.
    // PNG: (x, y) = (x * 60, y * 60, 200, 255 - x * 10) in file rows.
    Decode("PNG (zlib IDAT)", png, [](int x, int y, int c) {
        const int fileRow = 3 - y;
        const int v[4] = {x * 60, fileRow * 60, 200, 255 - x * 10};
        return v[c];
    });

    // EXR: R = x / 4, G = y / 4, B = 1, no alpha, linear values quantised to 8 bits.
    Decode("OpenEXR (ZIP scanlines)", exr, [](int x, int y, int c) {
        const int fileRow = 3 - y;
        const int v[4] = {x * 255 / 4, fileRow * 255 / 4, 255, 255};
        return v[c];
    });

    if (failures == 0)
    {
        std::printf("PASS\n");
        return 0;
    }

    std::printf("%d check(s) failed\n", failures);
    return 1;
}
