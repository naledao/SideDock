//
// Copyright (C) Microsoft Corporation. All rights reserved.
//
#include "pch.h"

namespace
{
    constexpr wchar_t kLocalMapName[] = L"Local\\SideDockCameraPreviewFrame";
    constexpr wchar_t kGlobalMapName[] = L"Global\\SideDockCameraPreviewFrame";
    constexpr int kHeaderSize = 128;
    constexpr int kMagic = 0x46434453; // SDCF
    constexpr int kVersion = 1;
    constexpr int kFormatBgra32 = 1;
    constexpr int kMaxFrameBytes = 2560 * 1440 * 4;
    constexpr int kMappingBytes = kHeaderSize + kMaxFrameBytes;

    struct SharedFrame
    {
        long long Sequence = 0;
        int Width = 0;
        int Height = 0;
        int Stride = 0;
        long long WrittenAtUnixMs = 0;
        std::wstring MapName;
        std::vector<BYTE> Bgra;
    };

    long long ReadInt64(const BYTE* base, int offset)
    {
        long long value = 0;
        CopyMemory(&value, base + offset, sizeof(value));
        return value;
    }

    int ReadInt32(const BYTE* base, int offset)
    {
        int value = 0;
        CopyMemory(&value, base + offset, sizeof(value));
        return value;
    }

    long long UnixTimeMilliseconds()
    {
        FILETIME fileTime{};
        GetSystemTimeAsFileTime(&fileTime);

        ULARGE_INTEGER value{};
        value.LowPart = fileTime.dwLowDateTime;
        value.HighPart = fileTime.dwHighDateTime;

        constexpr unsigned long long windowsToUnixEpoch100ns = 116444736000000000ULL;
        return static_cast<long long>((value.QuadPart - windowsToUnixEpoch100ns) / 10000ULL);
    }

    std::string WideToUtf8(const std::wstring& value)
    {
        if (value.empty())
        {
            return {};
        }

        const int size = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
        if (size <= 1)
        {
            return {};
        }

        std::string result(static_cast<size_t>(size - 1), '\0');
        WideCharToMultiByte(CP_UTF8, 0, value.c_str(), -1, result.data(), size, nullptr, nullptr);
        return result;
    }

    std::string EscapeJson(const std::wstring& value)
    {
        std::string utf8 = WideToUtf8(value);
        std::string escaped;
        escaped.reserve(utf8.size());
        for (char ch : utf8)
        {
            switch (ch)
            {
            case '\\':
                escaped += "\\\\";
                break;
            case '"':
                escaped += "\\\"";
                break;
            case '\n':
                escaped += "\\n";
                break;
            case '\r':
                escaped += "\\r";
                break;
            case '\t':
                escaped += "\\t";
                break;
            default:
                escaped += ch;
                break;
            }
        }

        return escaped;
    }

    std::wstring StatusPath()
    {
        wchar_t base[MAX_PATH] = {};
        DWORD length = GetEnvironmentVariableW(L"ProgramData", base, ARRAYSIZE(base));
        std::wstring root;
        if (length > 0 && length < ARRAYSIZE(base))
        {
            root.assign(base, length);
        }
        else
        {
            wchar_t temp[MAX_PATH] = {};
            DWORD tempLength = GetTempPathW(ARRAYSIZE(temp), temp);
            root.assign(temp, tempLength);
        }

        if (!root.empty() && root.back() != L'\\')
        {
            root += L'\\';
        }

        root += L"SideDock";
        CreateDirectoryW(root.c_str(), nullptr);
        return root + L"\\virtual-camera-status.json";
    }

    void WriteStatus(
        const char* frameKind,
        unsigned long long servedFrames,
        const SharedFrame* frame,
        const std::wstring& lastError)
    {
        const std::wstring path = StatusPath();
        const std::wstring tempPath = path + L".tmp";

        char buffer[2048] = {};
        const long long sourceSeq = frame ? frame->Sequence : 0;
        const int sourceWidth = frame ? frame->Width : 0;
        const int sourceHeight = frame ? frame->Height : 0;
        const long long sourceWrittenAt = frame ? frame->WrittenAtUnixMs : 0;
        const std::string mapName = EscapeJson(frame ? frame->MapName : L"Local\\SideDockCameraPreviewFrame; Global\\SideDockCameraPreviewFrame");
        const std::string escapedError = EscapeJson(lastError);

        const int written = sprintf_s(
            buffer,
            "{\n"
            "  \"version\": 1,\n"
            "  \"friendlyName\": \"SideDock Camera\",\n"
            "  \"previewMapName\": \"%s\",\n"
            "  \"servedFrames\": %llu,\n"
            "  \"servedAtUnixMs\": %lld,\n"
            "  \"frameKind\": \"%s\",\n"
            "  \"sourceFrameSequence\": %lld,\n"
            "  \"sourceWidth\": %d,\n"
            "  \"sourceHeight\": %d,\n"
            "  \"sourceFrameWrittenAtUnixMs\": %lld,\n"
            "  \"lastError\": \"%s\"\n"
            "}\n",
            mapName.c_str(),
            servedFrames,
            UnixTimeMilliseconds(),
            frameKind,
            sourceSeq,
            sourceWidth,
            sourceHeight,
            sourceWrittenAt,
            escapedError.c_str());

        if (written <= 0)
        {
            return;
        }

        FILE* file = nullptr;
        if (_wfopen_s(&file, tempPath.c_str(), L"wb") != 0 || file == nullptr)
        {
            return;
        }

        fwrite(buffer, 1, static_cast<size_t>(written), file);
        fclose(file);
        MoveFileExW(tempPath.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
    }

    class SharedFrameReader
    {
    public:
        ~SharedFrameReader()
        {
            if (m_view)
            {
                UnmapViewOfFile(m_view);
            }

            if (m_mapping)
            {
                CloseHandle(m_mapping);
            }
        }

        bool TryRead(SharedFrame& frame, std::wstring& error)
        {
            if (!EnsureOpen(error))
            {
                return false;
            }

            auto* base = static_cast<const BYTE*>(m_view);
            const long long sequenceBefore = ReadInt64(base, 32);
            if (sequenceBefore <= 0 || (sequenceBefore & 1LL) != 0)
            {
                error = L"preview frame not published yet";
                return false;
            }

            MemoryBarrier();

            const int magic = ReadInt32(base, 0);
            const int version = ReadInt32(base, 4);
            const int headerSize = ReadInt32(base, 8);
            const int width = ReadInt32(base, 12);
            const int height = ReadInt32(base, 16);
            const int stride = ReadInt32(base, 20);
            const int format = ReadInt32(base, 24);
            const int frameBytes = ReadInt32(base, 28);
            const long long writtenAtUnixMs = ReadInt64(base, 48);

            if (magic != kMagic
                || version != kVersion
                || headerSize != kHeaderSize
                || format != kFormatBgra32
                || width <= 0
                || height <= 0
                || stride < width * 4
                || frameBytes <= 0
                || frameBytes > kMaxFrameBytes
                || frameBytes != stride * height)
            {
                error = L"preview frame header is invalid";
                return false;
            }

            frame.Bgra.resize(static_cast<size_t>(frameBytes));
            CopyMemory(frame.Bgra.data(), base + kHeaderSize, static_cast<SIZE_T>(frameBytes));

            MemoryBarrier();

            const long long sequenceAfter = ReadInt64(base, 32);
            if (sequenceAfter != sequenceBefore || (sequenceAfter & 1LL) != 0)
            {
                error = L"preview frame changed while reading";
                return false;
            }

            frame.Sequence = sequenceAfter / 2;
            frame.Width = width;
            frame.Height = height;
            frame.Stride = stride;
            frame.WrittenAtUnixMs = writtenAtUnixMs;
            frame.MapName = m_activeMapName;
            error.clear();
            return true;
        }

    private:
        bool EnsureOpen(std::wstring& error)
        {
            if (m_view)
            {
                return true;
            }

            for (const auto* candidate : { kLocalMapName, kGlobalMapName })
            {
                m_mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, candidate);
                if (!m_mapping)
                {
                    continue;
                }

                m_view = MapViewOfFile(m_mapping, FILE_MAP_READ, 0, 0, kMappingBytes);
                if (m_view)
                {
                    m_activeMapName = candidate;
                    return true;
                }

                CloseHandle(m_mapping);
                m_mapping = nullptr;
            }

            error = L"Local\\SideDockCameraPreviewFrame and Global\\SideDockCameraPreviewFrame are not available";
            return false;
        }

        HANDLE m_mapping = nullptr;
        void* m_view = nullptr;
        std::wstring m_activeMapName;
    };

    std::mutex g_frameReaderLock;
    SharedFrameReader g_frameReader;
    std::atomic<unsigned long long> g_servedFrames = 0;
}

HRESULT SimpleFrameGenerator::Initialize(_In_ IMFMediaType* pMediaType)
{
    RETURN_HR_IF_NULL(E_INVALIDARG, pMediaType);

    RETURN_IF_FAILED(pMediaType->GetGUID(MF_MT_SUBTYPE, &m_subType));
    if (m_subType != MFVideoFormat_RGB32 && m_subType != MFVideoFormat_NV12)
    {
        RETURN_HR_MSG(MF_E_UNSUPPORTED_FORMAT, "Unsupported format: %s", winrt::to_hstring(m_subType).data());
    }
    MFGetAttributeSize(pMediaType, MF_MT_FRAME_SIZE, &m_width, &m_height);

    return S_OK;
}

/*:
   Writes to a buffer representing a 2D image.
   Writes a different constant to each line based on row number and current time.
   Assumes top down image, no negative stride and pBuf points to the begnning of the buffer of length len.
   Param:
   pBuf - pointer to beginning of buffer
   pitch - line length in bytes
   len - length of buffer in bytes
*/
HRESULT SimpleFrameGenerator::CreateFrame(
    _Inout_updates_bytes_(len) BYTE* pBuf,
    _In_ DWORD len,
    _In_ LONG pitch)
{
    if (m_subType == MFVideoFormat_RGB32)
    {
        DEBUG_MSG(L"RGB32 frames %s\n", winrt::to_hstring(MFVideoFormat_RGB32).data());

        RETURN_IF_FAILED(_CreateRGB32Frame(pBuf, len, pitch, m_width, m_height));
    }
    else if(m_subType == MFVideoFormat_NV12)
    {
        DEBUG_MSG(L"NV12 frames %s \n", winrt::to_hstring(MFVideoFormat_NV12).data());

        DWORD frameBuffLen = m_width * m_height * 4;
        wil::unique_cotaskmem_ptr<BYTE[]> spBuff = wil::make_unique_cotaskmem_nothrow<BYTE[]>(frameBuffLen);
        RETURN_IF_NULL_ALLOC(spBuff.get());

        RETURN_IF_FAILED(_CreateRGB32Frame(spBuff.get(), frameBuffLen, m_width * 4, m_width, m_height));
        RETURN_IF_FAILED(RGB32ToNV12Frame(spBuff.get(), frameBuffLen, m_width * 4, m_width, m_height, pBuf, len, pitch));
    }
    else
    {
        return MF_E_UNSUPPORTED_FORMAT;
    }

    return S_OK;
}

//////////////////////////////////////////////////
// private

HRESULT SimpleFrameGenerator::_CreateRGB32Frame(
    _Inout_updates_bytes_(len) BYTE* pBuf,
    _In_ DWORD len,
    _In_ LONG pitch,
    _In_ DWORD width,
    _In_ DWORD height)
{
    RETURN_HR_IF_NULL(E_INVALIDARG, pBuf);
    const auto absPitch = static_cast<DWORD>(abs(pitch));
    const DWORD rowBytes = width * 4;
    if (absPitch < rowBytes || len < (absPitch * height))
    {
        return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    }

    SharedFrame frame;
    std::wstring error;
    bool hasFrame = false;
    {
        std::lock_guard<std::mutex> lock(g_frameReaderLock);
        hasFrame = g_frameReader.TryRead(frame, error);
    }

    if (hasFrame
        && frame.Width == static_cast<int>(width)
        && frame.Height == static_cast<int>(height))
    {
        for (DWORD y = 0; y < height; y++)
        {
            BYTE* dst = pitch >= 0
                ? pBuf + (y * pitch)
                : pBuf + ((height - 1 - y) * absPitch);
            const BYTE* src = frame.Bgra.data() + y * frame.Stride;
            CopyMemory(dst, src, rowBytes);
        }

        const auto servedFrames = ++g_servedFrames;
        WriteStatus("shared", servedFrames, &frame, error);
        return S_OK;
    }

    for (DWORD y = 0; y < height; y++)
    {
        BYTE* dst = pitch >= 0
            ? pBuf + (y * pitch)
            : pBuf + ((height - 1 - y) * absPitch);

        if (!hasFrame)
        {
            for (DWORD x = 0; x < width; x++)
            {
                dst[x * 4 + 0] = 0;
                dst[x * 4 + 1] = 0;
                dst[x * 4 + 2] = 0;
                dst[x * 4 + 3] = 0xFF;
            }

            continue;
        }

        const int sourceY = static_cast<int>((static_cast<unsigned long long>(y) * frame.Height) / height);
        const BYTE* srcRow = frame.Bgra.data() + sourceY * frame.Stride;
        for (DWORD x = 0; x < width; x++)
        {
            const int sourceX = static_cast<int>((static_cast<unsigned long long>(x) * frame.Width) / width);
            const BYTE* src = srcRow + sourceX * 4;
            dst[x * 4 + 0] = src[0];
            dst[x * 4 + 1] = src[1];
            dst[x * 4 + 2] = src[2];
            dst[x * 4 + 3] = 0xFF;
        }
    }

    const auto servedFrames = ++g_servedFrames;
    WriteStatus(hasFrame ? "shared" : "black", servedFrames, hasFrame ? &frame : nullptr, error);

    return S_OK;
}

//////////////////////////////////////////////////
// pixelFormatConverter

void SimpleFrameGenerator::RGB24ToYUY2(int R, int G, int B, BYTE* pY, BYTE* pU, BYTE* pV)
{
    *pY = ((66 * R + 129 * G + 25 * B + 128) >> 8) + 16;
    *pU = ((-38 * R - 74 * G + 112 * B + 128) >> 8) + 128;
    *pV = ((112 * R - 94 * G - 18 * B + 128) >> 8) + 128;
}

void SimpleFrameGenerator::RGB24ToY(int R, int G, int B, BYTE* pY)
{
    *pY = ((66 * R + 129 * G + 25 * B + 128) >> 8) + 16;
}

void SimpleFrameGenerator::RGB32ToNV12(BYTE RGB1[8], BYTE RGB2[8], BYTE* pY1, BYTE* pY2, BYTE* pUV)
{
    RGB24ToYUY2(RGB1[2], RGB1[1], RGB1[0], pY1, pUV, pUV + 1);
    RGB24ToY(RGB1[6], RGB1[5], RGB1[4], pY1 + 1);
    RGB24ToYUY2(RGB2[2], RGB2[1], RGB2[0], pY2, pUV, pUV + 1);
    RGB24ToY(RGB2[6], RGB2[5], RGB2[4], pY2 + 1);
};

//////////////////////////////////////////////////
// FrameFormatConverter

HRESULT SimpleFrameGenerator::RGB32ToNV12Frame(_Inout_updates_bytes_(len) BYTE* pbBuff, ULONG cbBuff, long stride, UINT width, UINT height, BYTE* pbBuffOut, ULONG cbBuffOut, long strideOut)
{
    do
    {
        RETURN_HR_IF(E_UNEXPECTED, width * 4 * height > cbBuff);
        RETURN_HR_IF(E_UNEXPECTED, width * 1.5 * height > cbBuffOut);
        RETURN_HR_IF_NULL(E_INVALIDARG, pbBuff);

        RETURN_HR_IF_NULL(E_INVALIDARG, pbBuffOut);
        for (DWORD h = 0; h < height - 1; h += 2)
        {
            BYTE* pRGB1 = h * stride + pbBuff;
            BYTE* pRGB2 = (h + 1) * stride + pbBuff;
            BYTE* pY1 = h * strideOut + pbBuffOut;
            BYTE* pY2 = (h + 1) * strideOut + pbBuffOut;
            BYTE* pUV = (h / 2 + height) * strideOut + pbBuffOut;

            for (DWORD w = 0; w < width; w += 2)
            {
                RGB32ToNV12(pRGB1, pRGB2, pY1, pY2, pUV);
                pRGB1 += 8;
                pRGB2 += 8;
                pY1 += 2;
                pY2 += 2;
                pUV += 2;
            }
        }
    } while (FALSE);

    return S_OK;
}
