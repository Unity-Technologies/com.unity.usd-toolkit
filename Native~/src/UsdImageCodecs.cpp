// Image codecs the import runs on worker threads: an OpenEXR reader and a BC1/BC3 encoder with
// its mip chain. Both exist in C# as well (UsdExrDecoder, UsdBlockCompressor), and these are
// ports of exactly those -- same block layout, same palette and inset rules, same 8-bit
// quantisation of EXR -- so a texture built here is the texture the managed path would have
// built, only sooner: on ALab the managed inflate ran at 145 MB/s a core and the managed
// encoder at 16 Mpx/s, which was 66 of a 70-second import. The managed versions stay as the
// fallback for a platform whose payload is older than API 11 or has no zlib to open.
//
// Every entry point is a pure function of its arguments and touches no shared state, so the
// host may call them from as many threads as it likes.

#include "unity_usd_toolkit_native.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <exception>
#include <memory>
#include <new>
#include <string>
#include <thread>
#include <vector>

#include <sys/stat.h>

// The inflate, with zlib's uncompress() contract, compiled into this library from the libdeflate
// OpenUSD vendors (UsdDeflate.c). It used to be a zlib looked up at run time, which Linux and
// macOS have and Windows does not, so the Windows payload decoded in C# while the others did not.
extern "C" int RUsdZlibUncompress(unsigned char* out, unsigned long* outLength, const unsigned char* in,
                                  unsigned long inLength);

namespace
{

// ---- zlib-format inflate ------------------------------------------------------------------
// Every platform decodes with the same built-in inflater now; the "not available" branches the
// callers keep are what an API 11 payload without one reported, and cannot be taken here.

typedef int (*UncompressFn)(unsigned char*, unsigned long*, const unsigned char*, unsigned long);

UncompressFn LoadUncompress()
{
    return &RUsdZlibUncompress;
}

void SetReason(char* buffer, int capacity, const std::string& text)
{
    if (buffer == nullptr || capacity <= 0)
    {
        return;
    }

    size_t count = std::min(text.size(), static_cast<size_t>(capacity - 1));
    std::memcpy(buffer, text.data(), count);
    buffer[count] = '\0';
}

// ---- half -> byte tables ------------------------------------------------------------------

float HalfToFloat(uint16_t half)
{
    int sign = (half >> 15) & 1;
    int exponent = (half >> 10) & 0x1F;
    int mantissa = half & 0x3FF;
    float value;
    if (exponent == 0)
    {
        value = mantissa * (1.0f / 1024.0f) * (1.0f / 16384.0f);
    }
    else if (exponent == 31)
    {
        value = mantissa == 0 ? INFINITY : NAN;
    }
    else
    {
        value = (1.0f + mantissa * (1.0f / 1024.0f)) * std::ldexp(1.0f, exponent - 15);
    }

    return sign != 0 ? -value : value;
}

uint8_t ToByte(float value, bool srgb)
{
    if (std::isnan(value) || value <= 0.0f)
    {
        return 0;
    }

    if (value >= 1.0f)
    {
        return 255;
    }

    if (srgb)
    {
        value = value <= 0.0031308f ? value * 12.92f : 1.055f * std::pow(value, 1.0f / 2.4f) - 0.055f;
    }

    return static_cast<uint8_t>(std::lround(value * 255.0f));
}

struct HalfTables
{
    uint8_t linear[65536];
    uint8_t srgb[65536];
    HalfTables()
    {
        for (int bits = 0; bits < 65536; ++bits)
        {
            float value = HalfToFloat(static_cast<uint16_t>(bits));
            linear[bits] = ToByte(value, false);
            srgb[bits] = ToByte(value, true);
        }
    }
};

const HalfTables& Halves()
{
    static const HalfTables tables;
    return tables;
}

// ---- OpenEXR -------------------------------------------------------------------------------

enum
{
    kCompressionNone = 0,
    kCompressionRle = 1,
    kCompressionZips = 2,
    kCompressionZip = 3
};

enum
{
    kPixelUint = 0,
    kPixelHalf = 1,
    kPixelFloat = 2
};

struct Channel
{
    int pixelType;
    int bytesPerSample;
    int target; // 0 R, 1 G, 2 B, 3 A, -1 unused
};

struct ExrHeader
{
    std::vector<Channel> channels;
    int compression = -1;
    int xMin = 0, yMin = 0, xMax = -1, yMax = -1;
    bool haveDataWindow = false;
    bool tiled = false;
    int tileWidth = 0, tileHeight = 0, levelMode = 0;
    // Extents in 64-bit: xMax - xMin + 1 overflows int for a window like [INT_MAX, INT_MIN+98],
    // which wrapped to a plausible width. ParseHeader checks these; width()/height() are only used
    // after it has.
    long long width64() const { return static_cast<long long>(xMax) - xMin + 1; }
    long long height64() const { return static_cast<long long>(yMax) - yMin + 1; }
    int width() const { return static_cast<int>(width64()); }
    int height() const { return static_cast<int>(height64()); }
};

const int kMaxDimension = 16384;
const long long kMaxPixelCount = 64LL * 1024 * 1024;
// RGBA plus a handful of auxiliary layers is all a texture carries; a header with thousands of
// channels exists only to make every row, and so every scratch buffer, enormous.
const size_t kMaxExrChannels = 64;

// The most bytes `compressed` bytes can expand to. Deflate cannot exceed about 1032:1, and an EXR
// RLE run is two bytes for at most 128. A chunk, or a whole image, that claims more than its bytes
// can hold is refused before anything is allocated for it: otherwise a 200-byte file declaring a
// 16384x16384 tile or a thousand channels had gigabytes allocated before any data was read.
long long MaxExpansion(int compression, long long compressed)
{
    switch (compression)
    {
        case kCompressionNone: return compressed;
        case kCompressionRle: return compressed * 64 + 128;
        default: return compressed * 1032 + 1032;
    }
}

int32_t ReadInt32(const uint8_t* d, size_t o)
{
    return static_cast<int32_t>(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (static_cast<uint32_t>(d[o + 3]) << 24));
}

int64_t ReadInt64(const uint8_t* d, size_t o)
{
    // Assembled unsigned: shifting a negative int64 left is undefined.
    const uint64_t low = static_cast<uint32_t>(ReadInt32(d, o));
    const uint64_t high = static_cast<uint32_t>(ReadInt32(d, o + 4));
    return static_cast<int64_t>(low | (high << 32));
}

bool ReadString(const uint8_t* data, size_t length, size_t& pos, size_t maxLength, std::string& value)
{
    size_t start = pos;
    while (pos < length && data[pos] != 0)
    {
        if (pos - start > maxLength)
        {
            return false;
        }

        ++pos;
    }

    if (pos >= length)
    {
        return false;
    }

    value.assign(reinterpret_cast<const char*>(data + start), pos - start);
    ++pos;
    return true;
}

bool ReadChannels(const uint8_t* data, size_t pos, size_t size, size_t maxName, std::vector<Channel>& channels)
{
    size_t end = pos + size;
    channels.clear();
    while (pos < end)
    {
        std::string name;
        if (!ReadString(data, end, pos, maxName + 1, name))
        {
            return false;
        }

        if (name.empty())
        {
            break;
        }

        if (pos + 16 > end)
        {
            return false;
        }

        int pixelType = ReadInt32(data, pos);
        int xSampling = ReadInt32(data, pos + 8);
        int ySampling = ReadInt32(data, pos + 12);
        pos += 16;
        if (pixelType < kPixelUint || pixelType > kPixelFloat || xSampling != 1 || ySampling != 1)
        {
            return false;
        }

        std::string leaf = name;
        size_t dot = name.rfind('.');
        if (dot != std::string::npos && dot + 1 < name.size())
        {
            leaf = name.substr(dot + 1);
        }

        for (char& c : leaf)
        {
            c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
        }

        int target = -1;
        if (leaf == "R" || leaf == "Y") target = 0;
        else if (leaf == "G") target = 1;
        else if (leaf == "B") target = 2;
        else if (leaf == "A") target = 3;

        channels.push_back({pixelType, pixelType == kPixelHalf ? 2 : 4, target});
    }

    if (channels.empty())
    {
        return false;
    }

    bool anyMapped = false;
    for (const Channel& channel : channels)
    {
        anyMapped = anyMapped || channel.target >= 0;
    }

    if (!anyMapped && channels.size() == 1)
    {
        channels[0].target = 0;
    }

    return true;
}

bool ParseHeader(const uint8_t* data, size_t length, size_t& pos, ExrHeader& header, std::string& reason)
{
    if (length < 8 || data[0] != 0x76 || data[1] != 0x2f || data[2] != 0x31 || data[3] != 0x01)
    {
        reason = "not an OpenEXR file";
        return false;
    }

    int version = data[4];
    int flags = data[5];
    if (version != 2)
    {
        reason = "OpenEXR version " + std::to_string(version) + " is not supported";
        return false;
    }

    bool tiledFlag = (flags & 0x02) != 0;
    bool longNames = (flags & 0x04) != 0;
    if ((flags & 0x08) != 0)
    {
        reason = "deep OpenEXR images are not supported";
        return false;
    }

    if ((flags & 0x10) != 0)
    {
        reason = "multi-part OpenEXR files are not supported";
        return false;
    }

    size_t maxName = longNames ? 255 : 31;
    pos = 8;
    while (true)
    {
        std::string name;
        if (!ReadString(data, length, pos, maxName + 1, name))
        {
            reason = "malformed OpenEXR header";
            return false;
        }

        if (name.empty())
        {
            break;
        }

        std::string type;
        if (!ReadString(data, length, pos, maxName + 1, type) || pos + 4 > length)
        {
            reason = "malformed OpenEXR header";
            return false;
        }

        int32_t size = ReadInt32(data, pos);
        pos += 4;
        if (size < 0 || pos + static_cast<size_t>(size) > length)
        {
            reason = "malformed OpenEXR header";
            return false;
        }

        if (name == "channels")
        {
            if (!ReadChannels(data, pos, static_cast<size_t>(size), maxName, header.channels))
            {
                reason = "malformed OpenEXR channel list";
                return false;
            }
        }
        else if (name == "compression")
        {
            header.compression = size >= 1 ? data[pos] : -1;
        }
        else if (name == "dataWindow")
        {
            if (size != 16)
            {
                reason = "malformed OpenEXR dataWindow";
                return false;
            }

            header.xMin = ReadInt32(data, pos);
            header.yMin = ReadInt32(data, pos + 4);
            header.xMax = ReadInt32(data, pos + 8);
            header.yMax = ReadInt32(data, pos + 12);
            header.haveDataWindow = true;
        }
        else if (name == "tiles")
        {
            if (size != 9)
            {
                reason = "malformed OpenEXR tiles attribute";
                return false;
            }

            header.tiled = true;
            header.tileWidth = ReadInt32(data, pos);
            header.tileHeight = ReadInt32(data, pos + 4);
            header.levelMode = data[pos + 8] & 0x0F;
        }

        pos += static_cast<size_t>(size);
    }

    if (header.channels.empty() || !header.haveDataWindow || header.compression < 0)
    {
        reason = "OpenEXR header lacks channels, dataWindow or compression";
        return false;
    }

    if (tiledFlag != header.tiled)
    {
        reason = "OpenEXR tiled flag and tiles attribute disagree";
        return false;
    }

    if (header.tiled && (header.tileWidth < 1 || header.tileHeight < 1 || header.tileWidth > kMaxDimension ||
                         header.tileHeight > kMaxDimension || header.levelMode < 0 || header.levelMode > 2))
    {
        reason = "malformed OpenEXR tiles attribute";
        return false;
    }

    long long w = header.width64();
    long long h = header.height64();
    if (w < 1 || h < 1 || w > kMaxDimension || h > kMaxDimension || w * h > kMaxPixelCount)
    {
        reason = "OpenEXR dataWindow claims " + std::to_string(w) + "x" + std::to_string(h) +
                 ", outside the limits of 1-16384px a side and 67108864 pixels";
        return false;
    }

    switch (header.compression)
    {
        case kCompressionNone:
        case kCompressionRle:
        case kCompressionZips:
        case kCompressionZip:
            break;
        default:
            reason = "OpenEXR compression " + std::to_string(header.compression) +
                     " is not supported (NONE, RLE, ZIPS and ZIP are)";
            return false;
    }

    if (header.channels.size() > kMaxExrChannels)
    {
        reason = "OpenEXR has " + std::to_string(header.channels.size()) + " channels, over the limit of " +
                 std::to_string(kMaxExrChannels);
        return false;
    }

    // The whole image has to fit in what is left of the file once expanded. Checked here, in the
    // header, so RUsd_ExrProbe refuses the file before the host allocates its output either.
    long long pixelBytes = 0;
    for (const Channel& channel : header.channels)
    {
        pixelBytes += channel.bytesPerSample;
    }

    const long long remaining = static_cast<long long>(length) - static_cast<long long>(pos);
    if (pixelBytes * w * h > MaxExpansion(header.compression, remaining))
    {
        reason = "OpenEXR claims " + std::to_string(w) + "x" + std::to_string(h) +
                 " pixels, more than its " + std::to_string(remaining) + " bytes of data can hold";
        return false;
    }

    return true;
}

long long BytesPerRow(const std::vector<Channel>& channels, int samples)
{
    long long bytes = 0;
    for (const Channel& channel : channels)
    {
        bytes += static_cast<long long>(channel.bytesPerSample) * samples;
    }

    return bytes;
}

int DecodeRle(const uint8_t* data, size_t at, size_t size, uint8_t* output, int expected)
{
    size_t end = at + size;
    int written = 0;
    while (at < end)
    {
        int count = static_cast<int8_t>(data[at++]);
        if (count < 0)
        {
            int literal = -count;
            if (at + static_cast<size_t>(literal) > end || written + literal > expected)
            {
                return -1;
            }

            std::memcpy(output + written, data + at, static_cast<size_t>(literal));
            at += static_cast<size_t>(literal);
            written += literal;
        }
        else
        {
            int run = count + 1;
            if (at >= end || written + run > expected)
            {
                return -1;
            }

            std::memset(output + written, data[at++], static_cast<size_t>(run));
            written += run;
        }
    }

    return written;
}

void Reconstruct(uint8_t* source, int count, uint8_t* output)
{
    for (int i = 1; i < count; ++i)
    {
        source[i] = static_cast<uint8_t>(source[i - 1] + source[i] - 128);
    }

    int half = (count + 1) / 2;
    int first = 0, second = half, at = 0;
    while (at < count)
    {
        output[at++] = source[first++];
        if (at < count)
        {
            output[at++] = source[second++];
        }
    }
}

bool Unpack(const uint8_t* data, size_t at, int dataSize, int expected, int compression,
            uint8_t* chunk, uint8_t* scratch, UncompressFn inflate, std::string& reason)
{
    if (compression == kCompressionNone || dataSize == expected)
    {
        if (dataSize != expected)
        {
            reason = "OpenEXR chunk size does not match its pixels";
            return false;
        }

        std::memcpy(chunk, data + at, static_cast<size_t>(expected));
        return true;
    }

    int produced;
    if (compression == kCompressionRle)
    {
        produced = DecodeRle(data, at, static_cast<size_t>(dataSize), scratch, expected);
    }
    else
    {
        if (inflate == nullptr)
        {
            reason = "zlib not available";
            return false;
        }

        unsigned long outLength = static_cast<unsigned long>(expected);
        int result = inflate(scratch, &outLength, data + at, static_cast<unsigned long>(dataSize));
        produced = result == 0 ? static_cast<int>(outLength) : -1;
    }

    if (produced != expected)
    {
        reason = "OpenEXR chunk did not decompress to its pixels";
        return false;
    }

    Reconstruct(scratch, expected, chunk);
    return true;
}

// Rows run bottom-up in the output, as Unity lays textures out.
void UnpackRows(const uint8_t* pixels, const ExrHeader& header, bool srgb, uint8_t* rgba,
                int x0, int y0, int rowWidth, int rows)
{
    const uint8_t* halfTable = srgb ? Halves().srgb : Halves().linear;
    int width = header.width(), height = header.height();
    size_t lineAt = 0;
    for (int row = 0; row < rows; ++row)
    {
        size_t rowBase = (static_cast<size_t>(height - 1 - (y0 + row)) * width + x0) * 4;
        for (const Channel& channel : header.channels)
        {
            if (channel.target < 0)
            {
                lineAt += static_cast<size_t>(channel.bytesPerSample) * rowWidth;
                continue;
            }

            size_t outAt = rowBase + channel.target;
            switch (channel.pixelType)
            {
                case kPixelHalf:
                    for (int x = 0; x < rowWidth; ++x, outAt += 4, lineAt += 2)
                    {
                        rgba[outAt] = halfTable[pixels[lineAt] | (pixels[lineAt + 1] << 8)];
                    }
                    break;
                case kPixelFloat:
                    for (int x = 0; x < rowWidth; ++x, outAt += 4, lineAt += 4)
                    {
                        float value;
                        std::memcpy(&value, pixels + lineAt, 4);
                        rgba[outAt] = ToByte(value, srgb);
                    }
                    break;
                default:
                    for (int x = 0; x < rowWidth; ++x, outAt += 4, lineAt += 4)
                    {
                        uint32_t value;
                        std::memcpy(&value, pixels + lineAt, 4);
                        rgba[outAt] = static_cast<uint8_t>(value >> 24);
                    }
                    break;
            }
        }
    }
}

// Refuses a chunk whose bytes cannot expand to the pixels it claims, then grows the two working
// buffers to hold it.
bool ChunkFits(int compression, int dataSize, int expected, std::vector<uint8_t>& chunk,
               std::vector<uint8_t>& scratch, std::string& reason)
{
    if (expected < 0 || (dataSize != expected && expected > MaxExpansion(compression, dataSize)))
    {
        reason = "OpenEXR chunk claims more pixels than its data can hold";
        return false;
    }

    if (chunk.size() < static_cast<size_t>(expected))
    {
        chunk.resize(static_cast<size_t>(expected));
        scratch.resize(static_cast<size_t>(expected));
    }

    return true;
}

bool ReadOffsets(const uint8_t* data, size_t length, size_t pos, long long count, std::vector<int64_t>& offsets)
{
    if (count < 0 || pos + static_cast<size_t>(count) * 8 > length)
    {
        return false;
    }

    offsets.resize(static_cast<size_t>(count));
    for (long long i = 0; i < count; ++i)
    {
        offsets[static_cast<size_t>(i)] = ReadInt64(data, pos + static_cast<size_t>(i) * 8);
        if (offsets[static_cast<size_t>(i)] < 0 || static_cast<size_t>(offsets[static_cast<size_t>(i)]) + 8 > length)
        {
            return false;
        }
    }

    return true;
}

bool DecodeScanlines(const uint8_t* data, size_t length, size_t pos, const ExrHeader& header, bool srgb,
                     uint8_t* rgba, UncompressFn inflate, std::string& reason)
{
    int width = header.width(), height = header.height();
    int linesPerChunk = header.compression == kCompressionZip ? 16 : 1;
    long long bytesPerLine = BytesPerRow(header.channels, width);
    if (bytesPerLine <= 0 || bytesPerLine * std::min(linesPerChunk, height) > INT32_MAX)
    {
        reason = "OpenEXR scanline too large";
        return false;
    }

    int chunkCount = (height + linesPerChunk - 1) / linesPerChunk;
    std::vector<int64_t> offsets;
    if (!ReadOffsets(data, length, pos, chunkCount, offsets))
    {
        reason = "OpenEXR offset table runs past the end of the file";
        return false;
    }

    // Grown to the largest chunk that passes ChunkFits, not sized from the header up front.
    std::vector<uint8_t> chunk;
    std::vector<uint8_t> scratch;
    for (int c = 0; c < chunkCount; ++c)
    {
        size_t at = static_cast<size_t>(offsets[static_cast<size_t>(c)]);
        int y = ReadInt32(data, at);
        int dataSize = ReadInt32(data, at + 4);
        at += 8;
        if (y < header.yMin || y > header.yMax || dataSize < 0 || at + static_cast<size_t>(dataSize) > length)
        {
            reason = "malformed OpenEXR chunk";
            return false;
        }

        int lines = std::min(linesPerChunk, header.yMax - y + 1);
        int expected = static_cast<int>(bytesPerLine * lines);
        if (!ChunkFits(header.compression, dataSize, expected, chunk, scratch, reason))
        {
            return false;
        }

        if (!Unpack(data, at, dataSize, expected, header.compression, chunk.data(), scratch.data(), inflate, reason))
        {
            return false;
        }

        UnpackRows(chunk.data(), header, srgb, rgba, 0, y - header.yMin, width, lines);
    }

    return true;
}

bool DecodeTiles(const uint8_t* data, size_t length, size_t pos, const ExrHeader& header, bool srgb,
                 uint8_t* rgba, UncompressFn inflate, std::string& reason)
{
    int width = header.width(), height = header.height();
    int tilesX = (width + header.tileWidth - 1) / header.tileWidth;
    int tilesY = (height + header.tileHeight - 1) / header.tileHeight;
    long long tileCount = static_cast<long long>(tilesX) * tilesY;
    std::vector<int64_t> offsets;
    if (!ReadOffsets(data, length, pos, tileCount, offsets))
    {
        reason = "OpenEXR offset table runs past the end of the file";
        return false;
    }

    // A tile never holds more than the image: a 1x1 image may declare 16384x16384 tiles.
    long long bytesPerTile = BytesPerRow(header.channels, std::min(header.tileWidth, width)) *
                             std::min(header.tileHeight, height);
    if (bytesPerTile <= 0 || bytesPerTile > INT32_MAX)
    {
        reason = "OpenEXR tile too large";
        return false;
    }

    std::vector<uint8_t> chunk;
    std::vector<uint8_t> scratch;
    for (long long c = 0; c < tileCount; ++c)
    {
        size_t at = static_cast<size_t>(offsets[static_cast<size_t>(c)]);
        if (at + 20 > length)
        {
            reason = "malformed OpenEXR tile";
            return false;
        }

        int dx = ReadInt32(data, at);
        int dy = ReadInt32(data, at + 4);
        int lx = ReadInt32(data, at + 8);
        int ly = ReadInt32(data, at + 12);
        int dataSize = ReadInt32(data, at + 16);
        at += 20;
        if (lx != 0 || ly != 0)
        {
            continue;
        }

        if (dx < 0 || dy < 0 || dx >= tilesX || dy >= tilesY || dataSize < 0 || at + static_cast<size_t>(dataSize) > length)
        {
            reason = "malformed OpenEXR tile";
            return false;
        }

        int x0 = dx * header.tileWidth;
        int y0 = dy * header.tileHeight;
        int tileW = std::min(header.tileWidth, width - x0);
        int tileH = std::min(header.tileHeight, height - y0);
        int expected = static_cast<int>(BytesPerRow(header.channels, tileW) * tileH);
        if (!ChunkFits(header.compression, dataSize, expected, chunk, scratch, reason))
        {
            return false;
        }

        if (!Unpack(data, at, dataSize, expected, header.compression, chunk.data(), scratch.data(), inflate, reason))
        {
            return false;
        }

        UnpackRows(chunk.data(), header, srgb, rgba, x0, y0, tileW, tileH);
    }

    return true;
}

// ---- BC1 / BC3 -----------------------------------------------------------------------------

const int kBc1BlockBytes = 8;
const int kBc3BlockBytes = 16;

int Blocks(int size)
{
    return std::max(1, (size + 3) >> 2);
}

struct SrgbTables
{
    float toLinear[256];
    uint8_t toSrgb[4096];
    SrgbTables()
    {
        for (int i = 0; i < 256; ++i)
        {
            float value = i / 255.0f;
            toLinear[i] = value <= 0.04045f ? value / 12.92f : std::pow((value + 0.055f) / 1.055f, 2.4f);
        }

        for (int i = 0; i < 4096; ++i)
        {
            double value = i / 4095.0;
            double encoded = value <= 0.0031308 ? value * 12.92 : (1.055 * std::pow(value, 1.0 / 2.4)) - 0.055;
            toSrgb[i] = static_cast<uint8_t>(std::lround(std::max(0.0, std::min(1.0, encoded)) * 255.0));
        }
    }
};

const SrgbTables& Srgb()
{
    static const SrgbTables tables;
    return tables;
}

uint8_t LinearToSrgb(float linear)
{
    int index = static_cast<int>((linear * 4095.0f) + 0.5f);
    index = std::max(0, std::min(4095, index));
    return Srgb().toSrgb[index];
}

int Pack565(int r, int g, int b)
{
    return ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
}

void Unpack565(int packed, int& r, int& g, int& b)
{
    int r5 = (packed >> 11) & 0x1F;
    int g6 = (packed >> 5) & 0x3F;
    int b5 = packed & 0x1F;
    r = (r5 << 3) | (r5 >> 2);
    g = (g6 << 2) | (g6 >> 4);
    b = (b5 << 3) | (b5 >> 2);
}

void EncodeColourBlock(const uint8_t* block, uint8_t* output)
{
    int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
    for (int i = 0; i < 64; i += 4)
    {
        int r = block[i], g = block[i + 1], b = block[i + 2];
        minR = std::min(minR, r); minG = std::min(minG, g); minB = std::min(minB, b);
        maxR = std::max(maxR, r); maxG = std::max(maxG, g); maxB = std::max(maxB, b);
    }

    int insetR = (maxR - minR) >> 4, insetG = (maxG - minG) >> 4, insetB = (maxB - minB) >> 4;
    minR = std::min(minR + insetR, 255); minG = std::min(minG + insetG, 255); minB = std::min(minB + insetB, 255);
    maxR = std::max(maxR - insetR, 0); maxG = std::max(maxG - insetG, 0); maxB = std::max(maxB - insetB, 0);

    int high = Pack565(maxR, maxG, maxB);
    int low = Pack565(minR, minG, minB);
    if (high < low)
    {
        std::swap(high, low);
    }

    uint32_t indices = 0;
    if (high != low)
    {
        int hr, hg, hb, lr, lg, lb;
        Unpack565(high, hr, hg, hb);
        Unpack565(low, lr, lg, lb);
        const int pr[4] = {hr, lr, ((2 * hr) + lr) / 3, (hr + (2 * lr)) / 3};
        const int pg[4] = {hg, lg, ((2 * hg) + lg) / 3, (hg + (2 * lg)) / 3};
        const int pb[4] = {hb, lb, ((2 * hb) + lb) / 3, (hb + (2 * lb)) / 3};
        for (int i = 0, texel = 0; i < 64; i += 4, ++texel)
        {
            int r = block[i], g = block[i + 1], b = block[i + 2];
            int best = 0, bestDistance = INT32_MAX;
            for (int c = 0; c < 4; ++c)
            {
                int dr = r - pr[c], dg = g - pg[c], db = b - pb[c];
                int distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = c;
                }
            }

            indices |= static_cast<uint32_t>(best) << (texel << 1);
        }
    }

    output[0] = static_cast<uint8_t>(high & 0xFF);
    output[1] = static_cast<uint8_t>(high >> 8);
    output[2] = static_cast<uint8_t>(low & 0xFF);
    output[3] = static_cast<uint8_t>(low >> 8);
    output[4] = static_cast<uint8_t>(indices & 0xFF);
    output[5] = static_cast<uint8_t>((indices >> 8) & 0xFF);
    output[6] = static_cast<uint8_t>((indices >> 16) & 0xFF);
    output[7] = static_cast<uint8_t>((indices >> 24) & 0xFF);
}

void EncodeAlphaBlock(const uint8_t* block, uint8_t* output)
{
    int minA = 255, maxA = 0;
    for (int i = 3; i < 64; i += 4)
    {
        minA = std::min(minA, static_cast<int>(block[i]));
        maxA = std::max(maxA, static_cast<int>(block[i]));
    }

    output[0] = static_cast<uint8_t>(maxA);
    output[1] = static_cast<uint8_t>(minA);
    if (maxA == minA)
    {
        std::memset(output + 2, 0, 6);
        return;
    }

    uint64_t indices = 0;
    int range = maxA - minA;
    for (int i = 3, texel = 0; i < 64; i += 4, ++texel)
    {
        int step = ((block[i] - minA) * 7 + (range >> 1)) / range;
        int index = step == 7 ? 0 : (step == 0 ? 1 : 8 - step);
        indices |= static_cast<uint64_t>(static_cast<uint32_t>(index)) << (texel * 3);
    }

    for (int byteIndex = 0; byteIndex < 6; ++byteIndex)
    {
        output[2 + byteIndex] = static_cast<uint8_t>((indices >> (byteIndex * 8)) & 0xFF);
    }
}

#if defined(__SSE2__) || defined(_M_X64) || (defined(_M_IX86_FP) && _M_IX86_FP >= 2)
#define RUSD_HAVE_SSE2 1
#include <emmintrin.h>

// The same encoder as EncodeColourBlock, four texels at a time: the bounding box from
// packed min/max, the index search as 16-bit differences squared and summed by madd. The
// bytes it writes are the ones the scalar version writes -- same inset, same palette,
// same nearest-colour rule with ties going to the lower index -- so a platform without SSE2
// (an ARM build) produces the same texture, only slower.
void EncodeColourBlockSse2(const uint8_t* block, uint8_t* output)
{
    const __m128i row0 = _mm_loadu_si128(reinterpret_cast<const __m128i*>(block));
    const __m128i row1 = _mm_loadu_si128(reinterpret_cast<const __m128i*>(block + 16));
    const __m128i row2 = _mm_loadu_si128(reinterpret_cast<const __m128i*>(block + 32));
    const __m128i row3 = _mm_loadu_si128(reinterpret_cast<const __m128i*>(block + 48));

    __m128i mn = _mm_min_epu8(_mm_min_epu8(row0, row1), _mm_min_epu8(row2, row3));
    __m128i mx = _mm_max_epu8(_mm_max_epu8(row0, row1), _mm_max_epu8(row2, row3));
    mn = _mm_min_epu8(mn, _mm_srli_si128(mn, 8));
    mn = _mm_min_epu8(mn, _mm_srli_si128(mn, 4));
    mx = _mm_max_epu8(mx, _mm_srli_si128(mx, 8));
    mx = _mm_max_epu8(mx, _mm_srli_si128(mx, 4));
    const uint32_t minPacked = static_cast<uint32_t>(_mm_cvtsi128_si32(mn));
    const uint32_t maxPacked = static_cast<uint32_t>(_mm_cvtsi128_si32(mx));
    int minR = minPacked & 255, minG = (minPacked >> 8) & 255, minB = (minPacked >> 16) & 255;
    int maxR = maxPacked & 255, maxG = (maxPacked >> 8) & 255, maxB = (maxPacked >> 16) & 255;

    int insetR = (maxR - minR) >> 4, insetG = (maxG - minG) >> 4, insetB = (maxB - minB) >> 4;
    minR = std::min(minR + insetR, 255); minG = std::min(minG + insetG, 255); minB = std::min(minB + insetB, 255);
    maxR = std::max(maxR - insetR, 0); maxG = std::max(maxG - insetG, 0); maxB = std::max(maxB - insetB, 0);

    int high = Pack565(maxR, maxG, maxB);
    int low = Pack565(minR, minG, minB);
    if (high < low)
    {
        std::swap(high, low);
    }

    uint32_t indices = 0;
    if (high != low)
    {
        int hr, hg, hb, lr, lg, lb;
        Unpack565(high, hr, hg, hb);
        Unpack565(low, lr, lg, lb);
        const int pr[4] = {hr, lr, ((2 * hr) + lr) / 3, (hr + (2 * lr)) / 3};
        const int pg[4] = {hg, lg, ((2 * hg) + lg) / 3, (hg + (2 * lg)) / 3};
        const int pb[4] = {hb, lb, ((2 * hb) + lb) / 3, (hb + (2 * lb)) / 3};

        // Palette entries as 16-bit lanes, two texels wide; the alpha lane is zero and the
        // texel's alpha is masked off, so alpha never enters the distance.
        __m128i palette[4];
        for (int c = 0; c < 4; ++c)
        {
            palette[c] = _mm_set_epi16(0, static_cast<short>(pb[c]), static_cast<short>(pg[c]), static_cast<short>(pr[c]),
                                       0, static_cast<short>(pb[c]), static_cast<short>(pg[c]), static_cast<short>(pr[c]));
        }

        const __m128i zero = _mm_setzero_si128();
        const __m128i rgbMask = _mm_set_epi16(0, -1, -1, -1, 0, -1, -1, -1);
        const __m128i rows[4] = {row0, row1, row2, row3};
        int texel = 0;
        for (int row = 0; row < 4; ++row)
        {
            const __m128i halves[2] = {_mm_and_si128(_mm_unpacklo_epi8(rows[row], zero), rgbMask),
                                       _mm_and_si128(_mm_unpackhi_epi8(rows[row], zero), rgbMask)};
            for (int half = 0; half < 2; ++half)
            {
                int distance[4][2];
                for (int c = 0; c < 4; ++c)
                {
                    __m128i diff = _mm_sub_epi16(halves[half], palette[c]);
                    __m128i squared = _mm_madd_epi16(diff, diff);                  // r²+g², b²+0 per texel
                    __m128i summed = _mm_add_epi32(squared, _mm_srli_epi64(squared, 32)); // lanes 0 and 2
                    distance[c][0] = _mm_cvtsi128_si32(summed);
                    distance[c][1] = _mm_cvtsi128_si32(_mm_srli_si128(summed, 8));
                }

                for (int t = 0; t < 2; ++t, ++texel)
                {
                    int best = 0, bestDistance = distance[0][t];
                    for (int c = 1; c < 4; ++c)
                    {
                        if (distance[c][t] < bestDistance)
                        {
                            bestDistance = distance[c][t];
                            best = c;
                        }
                    }

                    indices |= static_cast<uint32_t>(best) << (texel << 1);
                }
            }
        }
    }

    output[0] = static_cast<uint8_t>(high & 0xFF);
    output[1] = static_cast<uint8_t>(high >> 8);
    output[2] = static_cast<uint8_t>(low & 0xFF);
    output[3] = static_cast<uint8_t>(low >> 8);
    output[4] = static_cast<uint8_t>(indices & 0xFF);
    output[5] = static_cast<uint8_t>((indices >> 8) & 0xFF);
    output[6] = static_cast<uint8_t>((indices >> 16) & 0xFF);
    output[7] = static_cast<uint8_t>((indices >> 24) & 0xFF);
}
#endif

inline void EncodeColourBlockFast(const uint8_t* block, uint8_t* output)
{
#if defined(RUSD_HAVE_SSE2)
    EncodeColourBlockSse2(block, output);
#else
    EncodeColourBlock(block, output);
#endif
}

size_t EncodeRows(const uint8_t* rgba, int width, int height, bool alpha, uint8_t* output, int blockRowStart, int blockRowEnd)
{
    int blocksX = Blocks(width);
    int blockBytes = alpha ? kBc3BlockBytes : kBc1BlockBytes;
    uint8_t block[64];
    for (int by = blockRowStart; by < blockRowEnd; ++by)
    {
        for (int bx = 0; bx < blocksX; ++bx)
        {
            if ((bx << 2) + 4 <= width && (by << 2) + 4 <= height)
            {
                size_t rowBytes = static_cast<size_t>(width) << 2;
                size_t from = ((static_cast<size_t>(by << 2) * width) + (bx << 2)) << 2;
                for (int row = 0; row < 4; ++row, from += rowBytes)
                {
                    std::memcpy(block + (row << 4), rgba + from, 16);
                }
            }
            else
            {
                for (int row = 0; row < 4; ++row)
                {
                    int y = std::min((by << 2) + row, height - 1);
                    for (int column = 0; column < 4; ++column)
                    {
                        int x = std::min((bx << 2) + column, width - 1);
                        std::memcpy(block + (row << 4) + (column << 2), rgba + ((static_cast<size_t>(y) * width + x) << 2), 4);
                    }
                }
            }

            uint8_t* at = output + (static_cast<size_t>(by) * blocksX + bx) * blockBytes;
            if (alpha)
            {
                EncodeAlphaBlock(block, at);
                EncodeColourBlockFast(block, at + 8);
            }
            else
            {
                EncodeColourBlockFast(block, at);
            }
        }
    }

    return static_cast<size_t>(blocksX) * (blockRowEnd - blockRowStart) * blockBytes;
}

// A level's blocks; an image of 4 Mpx or more is split into bands across a few threads.
// The host already runs one image per core, so this only matters for the large single
// images -- an 8K map, a 12-column atlas -- that would otherwise be the tail of the import.
size_t EncodeLevel(const uint8_t* rgba, int width, int height, bool alpha, uint8_t* output)
{
    int blocksY = Blocks(height);
    long long pixels = static_cast<long long>(width) * height;
    unsigned int threads = std::min(8u, std::max(1u, std::thread::hardware_concurrency() / 3));
    if (pixels < 4LL * 1024 * 1024 || threads < 2 || blocksY < static_cast<int>(threads) * 4)
    {
        return EncodeRows(rgba, width, height, alpha, output, 0, blocksY);
    }

    std::vector<std::thread> workers;
    int band = (blocksY + static_cast<int>(threads) - 1) / static_cast<int>(threads);
    for (unsigned int t = 0; t < threads; ++t)
    {
        int start = static_cast<int>(t) * band, end = std::min(blocksY, start + band);
        if (start >= end)
        {
            break;
        }

        // A thread that cannot be started (std::system_error) must not leave the ones already
        // running joinable when `workers` is destroyed -- that is std::terminate. Its band is
        // encoded here instead.
        try
        {
            workers.emplace_back([=]() { EncodeRows(rgba, width, height, alpha, output, start, end); });
        }
        catch (...)
        {
            EncodeRows(rgba, width, height, alpha, output, start, end);
        }
    }

    for (std::thread& worker : workers)
    {
        worker.join();
    }

    return static_cast<size_t>(Blocks(width)) * blocksY * (alpha ? kBc3BlockBytes : kBc1BlockBytes);
}

std::vector<uint8_t> Downsample(const uint8_t* rgba, int width, int height, bool srgb)
{
    int halfWidth = std::max(1, width >> 1), halfHeight = std::max(1, height >> 1);
    std::vector<uint8_t> output(static_cast<size_t>(halfWidth) * halfHeight * 4);
    const SrgbTables& tables = Srgb();
    for (int y = 0; y < halfHeight; ++y)
    {
        int y0 = std::min(y << 1, height - 1), y1 = std::min((y << 1) + 1, height - 1);
        for (int x = 0; x < halfWidth; ++x)
        {
            int x0 = std::min(x << 1, width - 1), x1 = std::min((x << 1) + 1, width - 1);
            size_t a = (static_cast<size_t>(y0) * width + x0) << 2;
            size_t b = (static_cast<size_t>(y0) * width + x1) << 2;
            size_t c = (static_cast<size_t>(y1) * width + x0) << 2;
            size_t d = (static_cast<size_t>(y1) * width + x1) << 2;
            size_t to = (static_cast<size_t>(y) * halfWidth + x) << 2;
            for (int channel = 0; channel < 3; ++channel)
            {
                output[to + channel] = srgb
                    ? LinearToSrgb((tables.toLinear[rgba[a + channel]] + tables.toLinear[rgba[b + channel]] +
                                    tables.toLinear[rgba[c + channel]] + tables.toLinear[rgba[d + channel]]) * 0.25f)
                    : static_cast<uint8_t>((rgba[a + channel] + rgba[b + channel] + rgba[c + channel] + rgba[d + channel] + 2) >> 2);
            }

            output[to + 3] = static_cast<uint8_t>((rgba[a + 3] + rgba[b + 3] + rgba[c + 3] + rgba[d + 3] + 2) >> 2);
        }
    }

    return output;
}

// ---- PNG ------------------------------------------------------------------------------------
// 8-bit, non-interlaced, colour types 0/2/3/4/6: the same subset the managed UsdPngDecoder
// takes, so the two agree on what is theirs and anything else goes to Texture2D.LoadImage.

uint32_t BigEndian32(const uint8_t* d)
{
    return (static_cast<uint32_t>(d[0]) << 24) | (d[1] << 16) | (d[2] << 8) | d[3];
}

uint8_t Paeth(int a, int b, int c)
{
    int p = a + b - c;
    int pa = std::abs(p - a), pb = std::abs(p - b), pc = std::abs(p - c);
    return static_cast<uint8_t>(pa <= pb && pa <= pc ? a : (pb <= pc ? b : c));
}

bool DecodePng(const uint8_t* data, size_t length, UncompressFn inflate, std::vector<uint8_t>& rgba,
               int& width, int& height, std::string& reason)
{
    static const uint8_t signature[8] = {137, 80, 78, 71, 13, 10, 26, 10};
    if (length < 33 || std::memcmp(data, signature, 8) != 0)
    {
        reason = "not a PNG";
        return false;
    }

    if (inflate == nullptr)
    {
        reason = "zlib not available";
        return false;
    }

    size_t pos = 8;
    width = height = 0;
    int bitDepth = 0, colourType = 0, interlace = 0;
    std::vector<uint8_t> idat;
    std::vector<uint8_t> palette;
    std::vector<uint8_t> transparency;
    bool sawHeader = false;
    while (pos + 8 <= length)
    {
        uint32_t chunkLength = BigEndian32(data + pos);
        const uint8_t* type = data + pos + 4;
        size_t body = pos + 8;
        if (chunkLength > length || body + chunkLength + 4 > length)
        {
            reason = "malformed PNG";
            return false;
        }

        if (std::memcmp(type, "IHDR", 4) == 0 && chunkLength == 13)
        {
            width = static_cast<int>(BigEndian32(data + body));
            height = static_cast<int>(BigEndian32(data + body + 4));
            bitDepth = data[body + 8];
            colourType = data[body + 9];
            interlace = data[body + 12];
            sawHeader = true;
        }
        else if (std::memcmp(type, "PLTE", 4) == 0)
        {
            palette.assign(data + body, data + body + chunkLength);
        }
        else if (std::memcmp(type, "tRNS", 4) == 0)
        {
            transparency.assign(data + body, data + body + chunkLength);
        }
        else if (std::memcmp(type, "IDAT", 4) == 0)
        {
            idat.insert(idat.end(), data + body, data + body + chunkLength);
        }
        else if (std::memcmp(type, "IEND", 4) == 0)
        {
            break;
        }

        pos = body + chunkLength + 4;
    }

    if (!sawHeader || width <= 0 || height <= 0 || width > kMaxDimension || height > kMaxDimension ||
        static_cast<long long>(width) * height > kMaxPixelCount)
    {
        reason = "PNG size outside the limits";
        return false;
    }

    int channels;
    switch (colourType)
    {
        case 0: channels = 1; break;
        case 2: channels = 3; break;
        case 3: channels = 1; break;
        case 4: channels = 2; break;
        case 6: channels = 4; break;
        default: reason = "PNG colour type not supported"; return false;
    }

    if (bitDepth != 8 || interlace != 0 || (colourType == 3 && palette.empty()))
    {
        reason = "PNG bit depth or interlacing not supported";
        return false;
    }

    size_t stride = static_cast<size_t>(width) * channels;
    size_t expected = (stride + 1) * static_cast<size_t>(height);
    // Same rule as an EXR chunk: a 68-byte PNG whose header claims 16384x4096 had 270 MB allocated
    // before its 10-byte IDAT failed to inflate.
    if (static_cast<long long>(expected) > MaxExpansion(kCompressionZip, static_cast<long long>(idat.size())))
    {
        reason = "PNG claims more pixels than its data can hold";
        return false;
    }

    std::vector<uint8_t> raw(expected);
    unsigned long produced = static_cast<unsigned long>(expected);
    if (inflate(raw.data(), &produced, idat.data(), static_cast<unsigned long>(idat.size())) != 0 || produced != expected)
    {
        reason = "PNG data did not inflate to its size";
        return false;
    }

    // Unfilter in place (each row keeps its filter byte in front). One loop per filter type
    // rather than a switch per byte; the previous row is the row above in the same buffer.
    const size_t bpp = static_cast<size_t>(channels);
    for (int y = 0; y < height; ++y)
    {
        uint8_t* row = raw.data() + static_cast<size_t>(y) * (stride + 1);
        int filter = row[0];
        uint8_t* line = row + 1;
        const uint8_t* above = y > 0 ? line - (stride + 1) : nullptr;
        switch (filter)
        {
            case 1:
                for (size_t i = bpp; i < stride; ++i) line[i] = static_cast<uint8_t>(line[i] + line[i - bpp]);
                break;
            case 2:
                if (above) for (size_t i = 0; i < stride; ++i) line[i] = static_cast<uint8_t>(line[i] + above[i]);
                break;
            case 3:
                if (above)
                {
                    for (size_t i = 0; i < bpp; ++i) line[i] = static_cast<uint8_t>(line[i] + (above[i] >> 1));
                    for (size_t i = bpp; i < stride; ++i) line[i] = static_cast<uint8_t>(line[i] + ((line[i - bpp] + above[i]) >> 1));
                }
                else
                {
                    for (size_t i = bpp; i < stride; ++i) line[i] = static_cast<uint8_t>(line[i] + (line[i - bpp] >> 1));
                }
                break;
            case 4:
                if (above)
                {
                    for (size_t i = 0; i < bpp; ++i) line[i] = static_cast<uint8_t>(line[i] + above[i]); // Paeth(0, b, 0) = b
                    for (size_t i = bpp; i < stride; ++i)
                        line[i] = static_cast<uint8_t>(line[i] + Paeth(line[i - bpp], above[i], above[i - bpp]));
                }
                else
                {
                    for (size_t i = bpp; i < stride; ++i) line[i] = static_cast<uint8_t>(line[i] + line[i - bpp]); // Paeth(a, 0, 0) = a
                }
                break;
            default:
                break;
        }
    }

    rgba.resize(static_cast<size_t>(width) * height * 4);
    for (int y = 0; y < height; ++y)
    {
        const uint8_t* line = raw.data() + static_cast<size_t>(y) * (stride + 1) + 1;
        uint8_t* out = rgba.data() + static_cast<size_t>(height - 1 - y) * width * 4; // bottom-up
        if (colourType == 6)
        {
            std::memcpy(out, line, static_cast<size_t>(width) * 4);
            continue;
        }

        if (colourType == 2)
        {
            for (int x = 0; x < width; ++x, out += 4)
            {
                out[0] = line[x * 3]; out[1] = line[x * 3 + 1]; out[2] = line[x * 3 + 2]; out[3] = 255;
            }
            continue;
        }

        for (int x = 0; x < width; ++x, out += 4)
        {
            out[3] = 255;
            switch (colourType)
            {
                case 0: out[0] = out[1] = out[2] = line[x]; break;
                case 2: out[0] = line[x * 3]; out[1] = line[x * 3 + 1]; out[2] = line[x * 3 + 2]; break;
                case 3:
                {
                    size_t index = line[x];
                    out[0] = out[1] = out[2] = 255;
                    if (index * 3 + 2 < palette.size())
                    {
                        out[0] = palette[index * 3]; out[1] = palette[index * 3 + 1]; out[2] = palette[index * 3 + 2];
                    }
                    if (index < transparency.size()) out[3] = transparency[index];
                    break;
                }
                case 4: out[0] = out[1] = out[2] = line[x * 2]; out[3] = line[x * 2 + 1]; break;
                default: std::memcpy(out, line + x * 4, 4); break;
            }
        }
    }

    return true;
}

bool DecodeExrTo(const uint8_t* data, size_t length, bool srgb, std::vector<uint8_t>& rgba, int& width, int& height,
                 std::string& reason)
{
    ExrHeader header;
    size_t pos = 0;
    if (!ParseHeader(data, length, pos, header, reason))
    {
        return false;
    }

    UncompressFn inflate = LoadUncompress();
    if (inflate == nullptr && header.compression != kCompressionNone && header.compression != kCompressionRle)
    {
        reason = "zlib not available";
        return false;
    }

    width = header.width();
    height = header.height();
    rgba.assign(static_cast<size_t>(width) * height * 4, 0);
    bool hasAlpha = false;
    int colour = 0;
    for (const Channel& channel : header.channels)
    {
        hasAlpha = hasAlpha || channel.target == 3;
        if (channel.target >= 0 && channel.target < 3) ++colour;
    }

    if (!hasAlpha)
    {
        for (size_t i = 3; i < rgba.size(); i += 4) rgba[i] = 255;
    }

    bool ok = header.tiled ? DecodeTiles(data, length, pos, header, srgb, rgba.data(), inflate, reason)
                           : DecodeScanlines(data, length, pos, header, srgb, rgba.data(), inflate, reason);
    if (!ok)
    {
        return false;
    }

    if (colour == 1)
    {
        for (size_t i = 0; i < rgba.size(); i += 4)
        {
            rgba[i + 1] = rgba[i];
            rgba[i + 2] = rgba[i];
        }
    }

    return true;
}

// PNG or EXR, whichever the bytes are.
bool DecodeAny(const uint8_t* data, size_t length, bool srgb, std::vector<uint8_t>& rgba, int& width, int& height,
               std::string& reason)
{
    if (length >= 8 && data[0] == 137 && data[1] == 80 && data[2] == 78 && data[3] == 71)
    {
        return DecodePng(data, length, LoadUncompress(), rgba, width, height, reason);
    }

    if (length >= 4 && data[0] == 0x76 && data[1] == 0x2f && data[2] == 0x31 && data[3] == 0x01)
    {
        return DecodeExrTo(data, length, srgb, rgba, width, height, reason);
    }

    reason = "not a PNG or OpenEXR";
    return false;
}

// ---- one finished image: pixels, or its compressed chain ----------------------------------

enum
{
    kModeRgba = 0,
    kModeBlocks = 1,
    kModeGray = 2
};

struct BuiltImage
{
    int width = 0, height = 0, mode = kModeRgba, mips = 0;
    bool hasAlpha = false;
    int columns = 1, rows = 1, minU = 0, minV = 0, tilesUsed = 0;
    std::vector<uint8_t> bytes; // rgba, gray or blocks, by mode
    std::string notes;          // per-tile failures, newline separated
};

void RepackOrm(std::vector<uint8_t>& rgba)
{
    for (size_t i = 0; i + 3 < rgba.size(); i += 4)
    {
        uint8_t occlusion = rgba[i], roughness = rgba[i + 1], metallic = rgba[i + 2];
        rgba[i] = metallic;
        rgba[i + 1] = occlusion;
        rgba[i + 2] = 0;
        rgba[i + 3] = static_cast<uint8_t>(255 - roughness);
    }
}

bool IsOpaque(const std::vector<uint8_t>& rgba)
{
    for (size_t i = 3; i < rgba.size(); i += 4)
    {
        if (rgba[i] != 255) return false;
    }

    return true;
}

// Turns decoded pixels into the requested form. Falls back to rgba when the size cannot be
// block-compressed, which the host reads from `mode`.
void Finish(BuiltImage& image, std::vector<uint8_t>& rgba, int mode, bool srgb, bool ormRepack)
{
    if (ormRepack)
    {
        RepackOrm(rgba);
    }

    if (mode == kModeGray)
    {
        image.mode = kModeGray;
        image.bytes.resize(rgba.size() / 4);
        for (size_t i = 0; i < image.bytes.size(); ++i) image.bytes[i] = rgba[i * 4];
        return;
    }

    if (mode == kModeBlocks)
    {
        bool alpha = !IsOpaque(rgba);
        int mips = 0;
        long long size = RUsd_BlockCompressedSize(image.width, image.height, alpha ? 1 : 0, &mips);
        if (size > 0)
        {
            image.bytes.resize(static_cast<size_t>(size));
            if (RUsd_BlockCompress(rgba.data(), image.width, image.height, alpha ? 1 : 0, srgb ? 1 : 0,
                                   image.bytes.data(), size) == 0)
            {
                image.mode = kModeBlocks;
                image.mips = mips;
                image.hasAlpha = alpha;
                return;
            }
        }
    }

    image.mode = kModeRgba;
    image.bytes.swap(rgba);
}

bool IsRegularFile(const std::string& path)
{
#if defined(_WIN32)
    struct _stat64 info;
    return _stat64(path.c_str(), &info) == 0 && (info.st_mode & _S_IFMT) == _S_IFREG;
#else
    struct stat info;
    return stat(path.c_str(), &info) == 0 && S_ISREG(info.st_mode);
#endif
}

bool ReadFile(const std::string& path, std::vector<uint8_t>& bytes, std::string& reason)
{
    // A FIFO or a device named as a texture would block fopen, or read without end; only a
    // regular file is a texture. The host confines the path; this is the native half.
    if (!IsRegularFile(path))
    {
        reason = "not a regular file: " + path;
        return false;
    }

    FILE* file = std::fopen(path.c_str(), "rb");
    if (file == nullptr)
    {
        reason = "cannot open " + path;
        return false;
    }

    std::fseek(file, 0, SEEK_END);
    long size = std::ftell(file);
    std::fseek(file, 0, SEEK_SET);
    if (size < 0 || size > kMaxPixelCount * 8)
    {
        std::fclose(file);
        reason = "file size outside the limits: " + path;
        return false;
    }

    bytes.resize(static_cast<size_t>(size));
    size_t read = size > 0 ? std::fread(bytes.data(), 1, static_cast<size_t>(size), file) : 0;
    std::fclose(file);
    if (read != static_cast<size_t>(size))
    {
        reason = "short read: " + path;
        return false;
    }

    return true;
}

// Box-averages or copies `tile` into the atlas cell at (x0, y0). Rows bottom-up in both.
void BlitTile(const std::vector<uint8_t>& tile, int tileW, int tileH, std::vector<uint8_t>& atlas, int atlasW,
              int x0, int y0, int cellW, int cellH)
{
    bool same = tileW == cellW && tileH == cellH;
    for (int y = 0; y < cellH; ++y)
    {
        size_t targetRow = (static_cast<size_t>(y0 + y) * atlasW + x0) * 4;
        if (same)
        {
            std::memcpy(atlas.data() + targetRow, tile.data() + static_cast<size_t>(y) * tileW * 4, static_cast<size_t>(cellW) * 4);
            continue;
        }

        int sy0 = static_cast<int>(static_cast<long long>(y) * tileH / cellH);
        int sy1 = std::max(sy0 + 1, static_cast<int>(static_cast<long long>(y + 1) * tileH / cellH));
        for (int x = 0; x < cellW; ++x)
        {
            int sx0 = static_cast<int>(static_cast<long long>(x) * tileW / cellW);
            int sx1 = std::max(sx0 + 1, static_cast<int>(static_cast<long long>(x + 1) * tileW / cellW));
            int r = 0, g = 0, b = 0, a = 0, n = 0;
            for (int sy = sy0; sy < sy1; ++sy)
            {
                for (int sx = sx0; sx < sx1; ++sx)
                {
                    size_t at = (static_cast<size_t>(sy) * tileW + sx) * 4;
                    r += tile[at]; g += tile[at + 1]; b += tile[at + 2]; a += tile[at + 3];
                    ++n;
                }
            }

            size_t to = targetRow + static_cast<size_t>(x) * 4;
            atlas[to] = static_cast<uint8_t>(r / n);
            atlas[to + 1] = static_cast<uint8_t>(g / n);
            atlas[to + 2] = static_cast<uint8_t>(b / n);
            atlas[to + 3] = static_cast<uint8_t>(a / n);
        }
    }
}

} // namespace

extern "C" {

static void* RUsd_ImageBuildUnguarded(const unsigned char* data, int length, int srgb, int mode, int ormRepack,
                      char* reasonBuffer, int reasonCapacity)
{
    if (data == nullptr || length <= 0)
    {
        SetReason(reasonBuffer, reasonCapacity, "empty buffer");
        return nullptr;
    }

    std::vector<uint8_t> rgba;
    std::string reason;
    std::unique_ptr<BuiltImage> image(new BuiltImage());
    if (!DecodeAny(data, static_cast<size_t>(length), srgb != 0, rgba, image->width, image->height, reason))
    {
        SetReason(reasonBuffer, reasonCapacity, reason);
        return nullptr;
    }

    Finish(*image, rgba, mode, srgb != 0, ormRepack != 0);
    return image.release();
}

// Builds one atlas from the tiles of a UDIM set: `paths` is newline-separated, `tiles` the UDIM
// number of each. The grid comes from the tile numbers (1001 + u + 10 v); the first tile that
// decodes sets the cell size, halved until the atlas fits `maxSide` and the pixel limit. A tile
// that fails to read or decode is left out and named in the notes; a set with no usable tile
// returns NULL with a reason.
static void* RUsd_UdimAtlasBuildUnguarded(const char* paths, const int* tiles, int count, int srgb, int mode, int ormRepack,
                          int maxSide, char* reasonBuffer, int reasonCapacity)
{
    if (paths == nullptr || tiles == nullptr || count <= 0)
    {
        SetReason(reasonBuffer, reasonCapacity, "no tiles");
        return nullptr;
    }

    std::vector<std::string> pathList;
    {
        std::string all(paths);
        size_t start = 0;
        while (start <= all.size() && static_cast<int>(pathList.size()) < count)
        {
            size_t end = all.find('\n', start);
            if (end == std::string::npos) end = all.size();
            pathList.push_back(all.substr(start, end - start));
            start = end + 1;
        }
    }

    if (static_cast<int>(pathList.size()) != count)
    {
        SetReason(reasonBuffer, reasonCapacity, "path count does not match tile count");
        return nullptr;
    }

    // The host sends four-digit UDIMs (1001-1999, u 0-9, v 0-99), each once. Checked here too:
    // the grid arithmetic below is int, and a far-off tile number wrapped it into a heap overflow
    // in BlitTile, and a repeated one had two threads writing the same cell.
    std::vector<bool> seen(1000, false);
    for (int i = 0; i < count; ++i)
    {
        if (tiles[i] < 1001 || tiles[i] > 1999 || seen[static_cast<size_t>(tiles[i] - 1001)])
        {
            SetReason(reasonBuffer, reasonCapacity, "UDIM tile numbers must be distinct and within 1001-1999");
            return nullptr;
        }

        seen[static_cast<size_t>(tiles[i] - 1001)] = true;
    }

    int minU = INT32_MAX, maxU = INT32_MIN, minV = INT32_MAX, maxV = INT32_MIN;
    for (int i = 0; i < count; ++i)
    {
        int square = tiles[i] - 1001;
        minU = std::min(minU, square % 10); maxU = std::max(maxU, square % 10);
        minV = std::min(minV, square / 10); maxV = std::max(maxV, square / 10);
    }

    int columns = maxU - minU + 1, rows = maxV - minV + 1;
    int side = maxSide > 0 ? std::min(maxSide, kMaxDimension) : kMaxDimension;

    std::unique_ptr<BuiltImage> image(new BuiltImage());
    image->columns = columns; image->rows = rows; image->minU = minU; image->minV = minV;
    std::vector<uint8_t> atlas;
    int cellW = 0, cellH = 0;
    // The first tile that decodes sets the cell size; the rest are read, decoded and blitted
    // by a few threads at once -- each writes its own cell, so no two touch the same bytes.
    // The host runs one set per core already; this shortens the tail that the sets with 20
    // and 34 tiles would otherwise be.
    std::vector<uint8_t> bytes, tile;
    int first = 0;
    for (; first < count; ++first)
    {
        std::string reason;
        int w = 0, h = 0;
        if (!ReadFile(pathList[static_cast<size_t>(first)], bytes, reason) ||
            !DecodeAny(bytes.data(), bytes.size(), srgb != 0, tile, w, h, reason))
        {
            image->notes += pathList[static_cast<size_t>(first)] + ": " + reason + "\n";
            continue;
        }

        cellW = w; cellH = h;
        auto tooLarge = [&]() {
            return static_cast<long long>(columns) * cellW > side || static_cast<long long>(rows) * cellH > side ||
                   static_cast<long long>(columns) * cellW * rows * cellH > kMaxPixelCount;
        };
        while (tooLarge() && cellW >= 8 && cellH >= 8)
        {
            cellW /= 2; cellH /= 2;
        }

        // A thin tile (7x16384) stopped the loop above on its short side while the long one still
        // overflowed: the atlas came out 70x1,638,400. Each side is now halved on its own until it
        // fits, and an atlas that still does not is refused rather than built.
        while (static_cast<long long>(columns) * cellW > side && cellW > 1) cellW /= 2;
        while (static_cast<long long>(rows) * cellH > side && cellH > 1) cellH /= 2;
        while (static_cast<long long>(columns) * cellW * rows * cellH > kMaxPixelCount && (cellW > 1 || cellH > 1))
        {
            if (cellW >= cellH) cellW /= 2; else cellH /= 2;
        }

        if (tooLarge())
        {
            image->notes += "a " + std::to_string(columns) + "x" + std::to_string(rows) +
                            " tile grid does not fit the atlas limits\n";
            break;
        }

        image->width = columns * cellW;
        image->height = rows * cellH;
        atlas.assign(static_cast<size_t>(image->width) * image->height * 4, 0);
        for (size_t p = 3; p < atlas.size(); p += 4) atlas[p] = 255;

        int square = tiles[first] - 1001;
        BlitTile(tile, w, h, atlas, image->width, (square % 10 - minU) * cellW, (square / 10 - minV) * cellH, cellW, cellH);
        image->tilesUsed++;
        ++first;
        break;
    }

    if (!atlas.empty() && first < count)
    {
        int remaining = count - first;
        unsigned int threads = static_cast<unsigned int>(std::min(remaining, 4));
        std::vector<std::string> notes(threads);
        std::vector<int> used(threads, 0);
        std::vector<std::thread> workers;
        for (unsigned int t = 0; t < threads; ++t)
        {
            auto work = [&, t]() {
                std::vector<uint8_t> localBytes, localTile;
                for (int i = first + static_cast<int>(t); i < count; i += static_cast<int>(threads))
                {
                    std::string reason;
                    int w = 0, h = 0;
                    // An exception escaping a std::thread is std::terminate: a tile that runs the
                    // process out of memory is left out, not fatal.
                    bool decoded = false;
                    try
                    {
                        decoded = ReadFile(pathList[static_cast<size_t>(i)], localBytes, reason) &&
                                  DecodeAny(localBytes.data(), localBytes.size(), srgb != 0, localTile, w, h, reason);
                    }
                    catch (const std::exception& exception)
                    {
                        reason = exception.what();
                    }
                    catch (...)
                    {
                        reason = "decode failed";
                    }

                    if (!decoded)
                    {
                        notes[t] += pathList[static_cast<size_t>(i)] + ": " + reason + "\n";
                        continue;
                    }

                    int square = tiles[i] - 1001;
                    BlitTile(localTile, w, h, atlas, image->width, (square % 10 - minU) * cellW,
                             (square / 10 - minV) * cellH, cellW, cellH);
                    used[t]++;
                }
            };

            // As in EncodeLevel: a worker that cannot be started runs here instead.
            try
            {
                workers.emplace_back(work);
            }
            catch (...)
            {
                work();
            }
        }

        for (std::thread& worker : workers)
        {
            worker.join();
        }

        for (unsigned int t = 0; t < threads; ++t)
        {
            image->notes += notes[t];
            image->tilesUsed += used[t];
        }
    }

    if (atlas.empty())
    {
        std::string notes = image->notes;
        SetReason(reasonBuffer, reasonCapacity, notes.empty() ? "no tile decoded" : notes);
        return nullptr;
    }

    Finish(*image, atlas, mode, srgb != 0, ormRepack != 0);
    return image.release();
}

int RUsd_ImageInfo(void* handle, int* width, int* height, int* mode, long long* byteCount, int* mips, int* hasAlpha,
                   int* columns, int* rows, int* minU, int* minV, int* tilesUsed)
{
    if (handle == nullptr) return 1;
    const BuiltImage& image = *static_cast<BuiltImage*>(handle);
    if (width) *width = image.width;
    if (height) *height = image.height;
    if (mode) *mode = image.mode;
    if (byteCount) *byteCount = static_cast<long long>(image.bytes.size());
    if (mips) *mips = image.mips;
    if (hasAlpha) *hasAlpha = image.hasAlpha ? 1 : 0;
    if (columns) *columns = image.columns;
    if (rows) *rows = image.rows;
    if (minU) *minU = image.minU;
    if (minV) *minV = image.minV;
    if (tilesUsed) *tilesUsed = image.tilesUsed;
    return 0;
}

const unsigned char* RUsd_ImageData(void* handle)
{
    return handle != nullptr ? static_cast<BuiltImage*>(handle)->bytes.data() : nullptr;
}

static int RUsd_ImageNotesUnguarded(void* handle, char* buffer, int capacity)
{
    if (handle == nullptr) return 1;
    SetReason(buffer, capacity, static_cast<BuiltImage*>(handle)->notes);
    return 0;
}

void RUsd_ImageRelease(void* handle)
{
    delete static_cast<BuiltImage*>(handle);
}

// The red channel of an image (gray: one byte a pixel; rgba: four) at (x, y) of a target of
// the given size, point-sampled when the sizes differ.
static inline uint8_t SampleRed(const unsigned char* pixels, int width, int height, int stride, int x, int y,
                                int targetWidth, int targetHeight)
{
    int sx = width == targetWidth ? x : static_cast<int>(static_cast<long long>(x) * width / targetWidth);
    int sy = height == targetHeight ? y : static_cast<int>(static_cast<long long>(y) * height / targetHeight);
    return pixels[(static_cast<size_t>(sy) * width + sx) * stride];
}

// Unity's packed metallic map from a plain metallic map and/or a roughness map: metallic in
// red, 1 - roughness in alpha, a constant standing in for whichever map is absent. The
// larger map sets the size. Finished like RUsd_ImageBuild (mode 1 for the compressed chain).
static void* RUsd_PackMetallicRoughnessUnguarded(const unsigned char* metallic, int metallicWidth, int metallicHeight, int metallicStride,
                                 const unsigned char* roughness, int roughnessWidth, int roughnessHeight, int roughnessStride,
                                 int metallicConstant, int smoothnessConstant, int mode)
{
    bool hasMetallic = metallic != nullptr && metallicWidth > 0 && metallicHeight > 0;
    bool hasRoughness = roughness != nullptr && roughnessWidth > 0 && roughnessHeight > 0;
    if (!hasMetallic && !hasRoughness)
    {
        return nullptr;
    }

    bool useRoughnessSize = hasRoughness && (!hasMetallic || static_cast<long long>(roughnessWidth) * roughnessHeight >=
                                                             static_cast<long long>(metallicWidth) * metallicHeight);
    int width = useRoughnessSize ? roughnessWidth : metallicWidth;
    int height = useRoughnessSize ? roughnessHeight : metallicHeight;
    std::vector<uint8_t> rgba(static_cast<size_t>(width) * height * 4);
    uint8_t metallicByte = static_cast<uint8_t>(std::max(0, std::min(255, metallicConstant)));
    uint8_t smoothnessByte = static_cast<uint8_t>(std::max(0, std::min(255, smoothnessConstant)));
    for (int y = 0; y < height; ++y)
    {
        for (int x = 0; x < width; ++x)
        {
            size_t at = (static_cast<size_t>(y) * width + x) * 4;
            rgba[at] = hasMetallic ? SampleRed(metallic, metallicWidth, metallicHeight, metallicStride, x, y, width, height) : metallicByte;
            rgba[at + 1] = 0;
            rgba[at + 2] = 0;
            rgba[at + 3] = hasRoughness
                ? static_cast<uint8_t>(255 - SampleRed(roughness, roughnessWidth, roughnessHeight, roughnessStride, x, y, width, height))
                : smoothnessByte;
        }
    }

    std::unique_ptr<BuiltImage> image(new BuiltImage());
    image->width = width;
    image->height = height;
    Finish(*image, rgba, mode, false, false);
    return image.release();
}

// An albedo with an opacity map folded into its alpha; a missing albedo becomes white.
static void* RUsd_PackAlbedoOpacityUnguarded(const unsigned char* albedo, int albedoWidth, int albedoHeight,
                             const unsigned char* opacity, int opacityWidth, int opacityHeight, int opacityStride,
                             int srgb, int mode)
{
    bool hasAlbedo = albedo != nullptr && albedoWidth > 0 && albedoHeight > 0;
    if (opacity == nullptr || opacityWidth <= 0 || opacityHeight <= 0)
    {
        return nullptr;
    }

    bool useAlbedoSize = hasAlbedo && static_cast<long long>(albedoWidth) * albedoHeight >=
                                      static_cast<long long>(opacityWidth) * opacityHeight;
    int width = useAlbedoSize ? albedoWidth : opacityWidth;
    int height = useAlbedoSize ? albedoHeight : opacityHeight;
    std::vector<uint8_t> rgba(static_cast<size_t>(width) * height * 4);
    for (int y = 0; y < height; ++y)
    {
        for (int x = 0; x < width; ++x)
        {
            size_t at = (static_cast<size_t>(y) * width + x) * 4;
            if (hasAlbedo)
            {
                int sx = albedoWidth == width ? x : static_cast<int>(static_cast<long long>(x) * albedoWidth / width);
                int sy = albedoHeight == height ? y : static_cast<int>(static_cast<long long>(y) * albedoHeight / height);
                const uint8_t* from = albedo + (static_cast<size_t>(sy) * albedoWidth + sx) * 4;
                rgba[at] = from[0];
                rgba[at + 1] = from[1];
                rgba[at + 2] = from[2];
            }
            else
            {
                rgba[at] = rgba[at + 1] = rgba[at + 2] = 255;
            }

            rgba[at + 3] = SampleRed(opacity, opacityWidth, opacityHeight, opacityStride, x, y, width, height);
        }
    }

    std::unique_ptr<BuiltImage> image(new BuiltImage());
    image->width = width;
    image->height = height;
    Finish(*image, rgba, mode, srgb != 0, false);
    return image.release();
}



static int RUsd_ExrProbeUnguarded(const unsigned char* data, int length, int* width, int* height, char* reasonBuffer, int reasonCapacity)
{
    if (data == nullptr || length <= 0)
    {
        SetReason(reasonBuffer, reasonCapacity, "empty buffer");
        return 1;
    }

    ExrHeader header;
    std::string reason;
    size_t pos = 0;
    if (!ParseHeader(data, static_cast<size_t>(length), pos, header, reason))
    {
        SetReason(reasonBuffer, reasonCapacity, reason);
        return 1;
    }

    if (width != nullptr) *width = header.width();
    if (height != nullptr) *height = header.height();
    return 0;
}

static int RUsd_ExrDecodeUnguarded(const unsigned char* data, int length, int toSrgb, unsigned char* rgba, long long rgbaCapacity,
                   char* reasonBuffer, int reasonCapacity)
{
    if (data == nullptr || length <= 0 || rgba == nullptr)
    {
        SetReason(reasonBuffer, reasonCapacity, "empty buffer");
        return 1;
    }

    ExrHeader header;
    std::string reason;
    size_t pos = 0;
    if (!ParseHeader(data, static_cast<size_t>(length), pos, header, reason))
    {
        SetReason(reasonBuffer, reasonCapacity, reason);
        return 1;
    }

    long long needed = static_cast<long long>(header.width()) * header.height() * 4;
    if (rgbaCapacity < needed)
    {
        SetReason(reasonBuffer, reasonCapacity, "output buffer too small");
        return 1;
    }

    UncompressFn inflate = LoadUncompress();
    if (inflate == nullptr && header.compression != kCompressionNone && header.compression != kCompressionRle)
    {
        SetReason(reasonBuffer, reasonCapacity, "zlib not available");
        return 2; // the host decodes in managed code
    }

    bool hasAlpha = false;
    for (const Channel& channel : header.channels)
    {
        hasAlpha = hasAlpha || channel.target == 3;
    }

    if (!hasAlpha)
    {
        for (long long i = 3; i < needed; i += 4)
        {
            rgba[i] = 255;
        }
    }

    bool ok = header.tiled
        ? DecodeTiles(data, static_cast<size_t>(length), pos, header, toSrgb != 0, rgba, inflate, reason)
        : DecodeScanlines(data, static_cast<size_t>(length), pos, header, toSrgb != 0, rgba, inflate, reason);
    if (!ok)
    {
        SetReason(reasonBuffer, reasonCapacity, reason);
        return 1;
    }

    int colour = 0;
    for (const Channel& channel : header.channels)
    {
        if (channel.target >= 0 && channel.target < 3)
        {
            ++colour;
        }
    }

    if (colour == 1)
    {
        for (long long i = 0; i < needed; i += 4)
        {
            rgba[i + 1] = rgba[i];
            rgba[i + 2] = rgba[i];
        }
    }

    return 0;
}

long long RUsd_BlockCompressedSize(int width, int height, int alpha, int* mipCount)
{
    if (mipCount != nullptr) *mipCount = 0;
    if (width < 4 || height < 4 || (width & 3) != 0 || (height & 3) != 0)
    {
        return 0;
    }

    int blockBytes = alpha != 0 ? kBc3BlockBytes : kBc1BlockBytes;
    int levels = 1;
    for (int w = width, h = height; w > 1 || h > 1; w = std::max(1, w >> 1), h = std::max(1, h >> 1))
    {
        ++levels;
    }

    long long total = 0;
    for (int level = 0, w = width, h = height; level < levels; ++level, w = std::max(1, w >> 1), h = std::max(1, h >> 1))
    {
        total += static_cast<long long>(Blocks(w)) * Blocks(h) * blockBytes;
    }

    if (mipCount != nullptr) *mipCount = levels;
    return total;
}

static int RUsd_BlockCompressUnguarded(const unsigned char* rgba, int width, int height, int alpha, int srgb,
                       unsigned char* output, long long outputCapacity)
{
    int levels = 0;
    long long total = RUsd_BlockCompressedSize(width, height, alpha, &levels);
    if (rgba == nullptr || output == nullptr || total <= 0 || outputCapacity < total)
    {
        return 1;
    }

    std::vector<uint8_t> level;
    const uint8_t* source = rgba;
    int sourceWidth = width, sourceHeight = height;
    size_t cursor = 0;
    for (int index = 0; index < levels; ++index)
    {
        if (index > 0)
        {
            level = Downsample(source, sourceWidth, sourceHeight, srgb != 0);
            source = level.data();
            sourceWidth = std::max(1, sourceWidth >> 1);
            sourceHeight = std::max(1, sourceHeight >> 1);
        }

        cursor += EncodeLevel(source, sourceWidth, sourceHeight, alpha != 0, output + cursor);
    }

    return 0;
}

// Exception barriers. Every entry point above allocates (std::vector, std::string, std::thread),
// and a std::bad_alloc or std::system_error unwinding out of an extern "C" function into Mono or
// IL2CPP frames is undefined -- in practice the process ends. Each one is called through here, and
// a failure becomes the entry point's ordinary failure result with a reason.
} // extern "C"

namespace
{

template <typename Result, typename Body>
Result Guarded(Result failure, char* reasonBuffer, int reasonCapacity, Body body)
{
    try
    {
        return body();
    }
    catch (const std::bad_alloc&)
    {
        SetReason(reasonBuffer, reasonCapacity, "out of memory");
    }
    catch (const std::exception& exception)
    {
        SetReason(reasonBuffer, reasonCapacity, exception.what());
    }
    catch (...)
    {
        SetReason(reasonBuffer, reasonCapacity, "native image code failed");
    }

    return failure;
}

} // namespace

extern "C" {

void* RUsd_ImageBuild(const unsigned char* data, int length, int srgb, int mode, int ormRepack,
                      char* reasonBuffer, int reasonCapacity)
{
    return Guarded<void*>(nullptr, reasonBuffer, reasonCapacity, [&]() {
        return RUsd_ImageBuildUnguarded(data, length, srgb, mode, ormRepack, reasonBuffer, reasonCapacity);
    });
}

void* RUsd_UdimAtlasBuild(const char* paths, const int* tiles, int count, int srgb, int mode, int ormRepack,
                          int maxSide, char* reasonBuffer, int reasonCapacity)
{
    return Guarded<void*>(nullptr, reasonBuffer, reasonCapacity, [&]() {
        return RUsd_UdimAtlasBuildUnguarded(paths, tiles, count, srgb, mode, ormRepack, maxSide,
                                            reasonBuffer, reasonCapacity);
    });
}

void* RUsd_PackMetallicRoughness(const unsigned char* metallic, int metallicWidth, int metallicHeight, int metallicStride,
                                 const unsigned char* roughness, int roughnessWidth, int roughnessHeight, int roughnessStride,
                                 int metallicConstant, int smoothnessConstant, int mode)
{
    return Guarded<void*>(nullptr, nullptr, 0, [&]() {
        return RUsd_PackMetallicRoughnessUnguarded(metallic, metallicWidth, metallicHeight, metallicStride,
            roughness, roughnessWidth, roughnessHeight, roughnessStride, metallicConstant, smoothnessConstant, mode);
    });
}

void* RUsd_PackAlbedoOpacity(const unsigned char* albedo, int albedoWidth, int albedoHeight,
                             const unsigned char* opacity, int opacityWidth, int opacityHeight, int opacityStride,
                             int srgb, int mode)
{
    return Guarded<void*>(nullptr, nullptr, 0, [&]() {
        return RUsd_PackAlbedoOpacityUnguarded(albedo, albedoWidth, albedoHeight,
            opacity, opacityWidth, opacityHeight, opacityStride, srgb, mode);
    });
}

int RUsd_ExrProbe(const unsigned char* data, int length, int* width, int* height, char* reasonBuffer, int reasonCapacity)
{
    return Guarded<int>(1, reasonBuffer, reasonCapacity, [&]() {
        return RUsd_ExrProbeUnguarded(data, length, width, height, reasonBuffer, reasonCapacity);
    });
}

int RUsd_ExrDecode(const unsigned char* data, int length, int toSrgb, unsigned char* rgba, long long rgbaCapacity,
                   char* reasonBuffer, int reasonCapacity)
{
    return Guarded<int>(1, reasonBuffer, reasonCapacity, [&]() {
        return RUsd_ExrDecodeUnguarded(data, length, toSrgb, rgba, rgbaCapacity, reasonBuffer, reasonCapacity);
    });
}

int RUsd_BlockCompress(const unsigned char* rgba, int width, int height, int alpha, int srgb,
                       unsigned char* output, long long outputCapacity)
{
    return Guarded<int>(1, nullptr, 0, [&]() {
        return RUsd_BlockCompressUnguarded(rgba, width, height, alpha, srgb, output, outputCapacity);
    });
}

int RUsd_ImageNotes(void* handle, char* buffer, int capacity)
{
    return Guarded<int>(1, nullptr, 0, [&]() { return RUsd_ImageNotesUnguarded(handle, buffer, capacity); });
}

} // extern "C"
