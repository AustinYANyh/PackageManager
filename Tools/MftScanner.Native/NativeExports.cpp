#define NOMINMAX
#include <Windows.h>
#include <winioctl.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <cstdlib>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cwchar>
#include <cwctype>
#include <filesystem>
#include <fstream>
#include <future>
#include <limits>
#include <memory>
#include <mutex>
#include <regex>
#include <shared_mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace
{
    using NativeJsonCallback = void(__cdecl*)(void*, const char*);

    enum class MatchMode
    {
        Contains,
        Prefix,
        Suffix,
        Regex,
        Wildcard
    };

    enum class SearchFilter
    {
        All,
        Launchable,
        Folder,
        Script,
        Log,
        Config
    };

    enum class ChangeKind
    {
        Create,
        Delete,
        Rename
    };

    struct Record
    {
        std::wstring lowerName;
        std::wstring originalName;
        std::uint64_t parentFrn = 0;
        std::uint64_t frn = 0;
        wchar_t driveLetter = L'\0';
        bool isDirectory = false;
    };

    struct FrnEntry
    {
        std::wstring name;
        std::uint64_t parentFrn = 0;
        bool isDirectory = false;
    };

    struct VolumeState
    {
        wchar_t driveLetter = L'\0';
        std::int64_t nextUsn = 0;
        std::uint64_t journalId = 0;
        std::uint32_t recordCount = 0;
        std::uint64_t maxFrn = 0;
        bool enumerationComplete = true;
        std::unordered_map<std::uint64_t, FrnEntry> frnMap;
        std::unordered_map<std::uint64_t, std::wstring> pathCache;
        std::unordered_map<std::uint64_t, std::pair<std::wstring, std::uint64_t>> pendingRenameOldByFrn;
    };

    struct SearchResultRow
    {
        std::wstring fullPath;
        std::wstring fileName;
        bool isDirectory = false;
    };

    struct SearchBinaryPayload
    {
        int totalIndexedCount = 0;
        int totalMatchedCount = 0;
        int physicalMatchedCount = 0;
        int uniqueMatchedCount = 0;
        int duplicatePathCount = 0;
        bool isTruncated = false;
        std::vector<SearchResultRow> rows;
    };

    struct NativeChange
    {
        ChangeKind kind = ChangeKind::Create;
        std::uint64_t frn = 0;
        std::uint64_t parentFrn = 0;
        std::uint64_t oldParentFrn = 0;
        wchar_t driveLetter = L'\0';
        bool isDirectory = false;
        std::wstring lowerName;
        std::wstring originalName;
        std::wstring oldLowerName;
    };

    struct UsnReadState
    {
        wchar_t driveLetter = L'\0';
        std::int64_t nextUsn = 0;
        std::uint64_t journalId = 0;
        std::unordered_map<std::uint64_t, std::pair<std::wstring, std::uint64_t>> pendingRenameOldByFrn;
    };

    struct VolumeCatchUpResult
    {
        wchar_t driveLetter = L'\0';
        UsnReadState state;
        std::vector<NativeChange> changes;
        bool journalReset = false;
        bool success = false;
    };

    struct NativeV2BucketEntry
    {
        std::uint64_t key = 0;
        std::uint32_t offset = 0;
        std::uint32_t count = 0;
        std::uint32_t byteCount = 0;
    };

    struct NativeV2ShortBucketEntry
    {
        std::uint32_t offset = 0;
        std::uint32_t count = 0;
        std::uint32_t byteCount = 0;
    };

    struct NativeV2ChildBucketEntry
    {
        std::uint64_t key = 0;
        std::uint32_t offset = 0;
        std::uint32_t count = 0;
    };

    struct FrnRecordIndexEntry
    {
        std::uint64_t key = 0;
        std::uint32_t recordId = 0;
    };

    struct NativeV2CompactRecord
    {
        std::uint64_t frn = 0;
        std::uint64_t parentFrn = 0;
        std::uint32_t originalOffset = 0;
        std::uint32_t lowerOffset = 0;
        std::uint16_t originalLength = 0;
        std::uint16_t lowerLength = 0;
        std::uint8_t drive = 0;
        std::uint8_t flags = 0;
        std::uint16_t reserved = 0;
    };

    class MappedFile
    {
    public:
        MappedFile() = default;

        MappedFile(const MappedFile&) = delete;
        MappedFile& operator=(const MappedFile&) = delete;

        MappedFile(MappedFile&& other) noexcept
        {
            MoveFrom(std::move(other));
        }

        MappedFile& operator=(MappedFile&& other) noexcept
        {
            if (this != &other)
            {
                Close();
                MoveFrom(std::move(other));
            }

            return *this;
        }

        ~MappedFile()
        {
            Close();
        }

        bool Open(const std::filesystem::path& path)
        {
            Close();
            std::error_code sizeError;
            const auto fileBytes = std::filesystem::file_size(path, sizeError);
            if (sizeError || fileBytes == 0 || fileBytes > static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max()))
            {
                return false;
            }

            file_ = CreateFileW(
                path.c_str(),
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                nullptr,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                nullptr);
            if (file_ == INVALID_HANDLE_VALUE)
            {
                file_ = nullptr;
                return false;
            }

            mapping_ = CreateFileMappingW(file_, nullptr, PAGE_READONLY, 0, 0, nullptr);
            if (mapping_ == nullptr)
            {
                Close();
                return false;
            }

            view_ = static_cast<const std::uint8_t*>(MapViewOfFile(mapping_, FILE_MAP_READ, 0, 0, 0));
            if (view_ == nullptr)
            {
                Close();
                return false;
            }

            size_ = static_cast<std::size_t>(fileBytes);
            return true;
        }

        void Close()
        {
            if (view_ != nullptr)
            {
                UnmapViewOfFile(view_);
                view_ = nullptr;
            }

            if (mapping_ != nullptr)
            {
                CloseHandle(mapping_);
                mapping_ = nullptr;
            }

            if (file_ != nullptr && file_ != INVALID_HANDLE_VALUE)
            {
                CloseHandle(file_);
                file_ = nullptr;
            }

            size_ = 0;
        }

        const std::uint8_t* Data() const noexcept
        {
            return view_;
        }

        std::size_t Size() const noexcept
        {
            return size_;
        }

        bool IsOpen() const noexcept
        {
            return view_ != nullptr && size_ > 0;
        }

    private:
        void MoveFrom(MappedFile&& other) noexcept
        {
            file_ = other.file_;
            mapping_ = other.mapping_;
            view_ = other.view_;
            size_ = other.size_;
            other.file_ = nullptr;
            other.mapping_ = nullptr;
            other.view_ = nullptr;
            other.size_ = 0;
        }

        HANDLE file_ = nullptr;
        HANDLE mapping_ = nullptr;
        const std::uint8_t* view_ = nullptr;
        std::size_t size_ = 0;
    };

    struct NativeV2MappedRecords
    {
        MappedFile file;
        const NativeV2CompactRecord* records = nullptr;
        const wchar_t* stringPool = nullptr;
        std::uint32_t recordCount = 0;
        std::uint32_t volumeCount = 0;
        std::uint32_t stringPoolChars = 0;

        void Clear()
        {
            file.Close();
            records = nullptr;
            stringPool = nullptr;
            recordCount = 0;
            volumeCount = 0;
            stringPoolChars = 0;
        }

        bool Ready() const
        {
            return records != nullptr && stringPool != nullptr && recordCount > 0;
        }
    };

    struct NativeV2CandidateResult
    {
        std::vector<std::uint32_t> ids;
        const char* mode = "none";
        bool accelerated = false;
    };

    enum class QueryPlanKind
    {
        Contains,
        Prefix,
        Suffix,
        ExtensionWildcard,
        WildcardLiteral,
        RegexLiteral,
        RegexScan
    };

    struct QueryPlan
    {
        QueryPlanKind kind = QueryPlanKind::Contains;
        MatchMode mode = MatchMode::Contains;
        std::wstring query;
        std::wstring pattern;
        std::wstring requiredLiteral;
        std::wstring extension;
        const char* name = "contains";
    };

    struct OverlaySearchIndex
    {
        std::array<std::vector<std::uint32_t>, 128> asciiCharPostings;
        std::array<std::vector<std::uint32_t>, 128 * 128> asciiBigramPostings;
        std::unordered_map<std::uint64_t, std::vector<std::uint32_t>> nonAsciiShortPostings;
        std::unordered_map<std::uint64_t, std::vector<std::uint32_t>> trigramPostings;
        std::unordered_map<std::wstring, std::vector<std::uint32_t>> extensionPostings;
        std::unordered_map<std::uint64_t, std::vector<std::uint32_t>> parentFrnPostings;
        bool ready = false;

        void Clear()
        {
            for (auto& posting : asciiCharPostings)
            {
                posting.clear();
                posting.shrink_to_fit();
            }

            for (auto& posting : asciiBigramPostings)
            {
                posting.clear();
                posting.shrink_to_fit();
            }

            nonAsciiShortPostings.clear();
            trigramPostings.clear();
            extensionPostings.clear();
            parentFrnPostings.clear();
            ready = false;
        }

        void ResetKeepCapacity()
        {
            for (auto& posting : asciiCharPostings)
            {
                posting.clear();
            }

            for (auto& posting : asciiBigramPostings)
            {
                posting.clear();
            }

            nonAsciiShortPostings.clear();
            trigramPostings.clear();
            extensionPostings.clear();
            parentFrnPostings.clear();
            ready = false;
        }
    };

    struct OverlaySegmentSnapshotData
    {
        std::uint64_t generation = 0;
        std::uint64_t stableRecordCount = 0;
        std::unordered_map<wchar_t, VolumeState> volumes;
        std::vector<Record> overlayRecords;
        std::unordered_map<std::uint64_t, std::uint32_t> overlayIndexByKey;
        std::unordered_set<std::uint64_t> overlayDeletedKeys;
        std::unordered_set<std::uint64_t> compactedKeys;
        std::unordered_set<std::uint64_t> compactedDeletedKeys;
    };

    struct NativeV2Accelerator
    {
        std::vector<NativeV2BucketEntry> trigramEntries;
        std::vector<std::uint8_t> trigramPostings;
        std::vector<NativeV2BucketEntry> prefix4Entries;
        std::vector<std::uint8_t> prefix4Postings;
        std::array<std::vector<std::uint32_t>, 128> asciiCharPostings;
        std::array<std::vector<std::uint32_t>, 128 * 128> asciiBigramPostings;
        std::unordered_map<std::uint64_t, std::vector<std::uint32_t>> nonAsciiShortPostings;
        std::array<NativeV2ShortBucketEntry, 128> asciiCharEntries{};
        std::array<NativeV2ShortBucketEntry, 128 * 128> asciiBigramEntries{};
        std::vector<std::array<std::uint32_t, 26>> asciiCharDriveCounts;
        std::vector<std::array<std::uint32_t, 26>> asciiBigramDriveCounts;
        std::vector<std::array<std::vector<std::uint32_t>, 26>> asciiCharDrivePages;
        std::vector<std::array<std::vector<std::uint32_t>, 26>> asciiBigramDrivePages;
        std::unordered_map<std::uint64_t, NativeV2ShortBucketEntry> nonAsciiShortEntries;
        std::vector<std::uint8_t> asciiCharCompressedPostings;
        std::vector<std::uint8_t> asciiBigramCompressedPostings;
        std::vector<std::uint8_t> nonAsciiShortCompressedPostings;
        std::vector<std::uint32_t> parentOrder;
        std::vector<NativeV2ChildBucketEntry> childEntries;
        std::vector<std::uint32_t> childRecordIds;
        std::vector<std::uint32_t> subtreeEnter;
        std::vector<std::uint32_t> subtreeExit;
        std::vector<std::uint32_t> subtreeRecordIds;
        bool ready = false;
        std::uint64_t buildMs = 0;
        std::uint64_t postingsBytes = 0;

        void Clear()
        {
            trigramEntries.clear();
            trigramPostings.clear();
            trigramPostings.shrink_to_fit();
            prefix4Entries.clear();
            prefix4Postings.clear();
            prefix4Postings.shrink_to_fit();
            for (auto& posting : asciiCharPostings)
            {
                posting.clear();
                posting.shrink_to_fit();
            }

            for (auto& posting : asciiBigramPostings)
            {
                posting.clear();
                posting.shrink_to_fit();
            }

            nonAsciiShortPostings.clear();
            asciiCharEntries = {};
            asciiBigramEntries = {};
            asciiCharDriveCounts.clear();
            asciiBigramDriveCounts.clear();
            asciiCharDrivePages.clear();
            asciiBigramDrivePages.clear();
            nonAsciiShortEntries.clear();
            asciiCharCompressedPostings.clear();
            asciiCharCompressedPostings.shrink_to_fit();
            asciiBigramCompressedPostings.clear();
            asciiBigramCompressedPostings.shrink_to_fit();
            nonAsciiShortCompressedPostings.clear();
            nonAsciiShortCompressedPostings.shrink_to_fit();
            parentOrder.clear();
            childEntries.clear();
            childRecordIds.clear();
            subtreeEnter.clear();
            subtreeExit.clear();
            subtreeRecordIds.clear();
            ready = false;
            buildMs = 0;
            postingsBytes = 0;
        }
    };

    struct NativeV2ShortQueryLocal
    {
        std::array<std::vector<std::uint32_t>, 128> chars;
        std::array<std::vector<std::uint32_t>, 128 * 128> bigrams;
        std::unordered_map<std::uint64_t, std::vector<std::uint32_t>> nonAscii;
    };

    struct NativeV2BuildLocal
    {
        std::unordered_map<std::uint64_t, std::vector<std::uint32_t>> trigrams;
        std::unordered_map<std::uint64_t, std::vector<std::uint32_t>> prefix4;
        NativeV2ShortQueryLocal shortQuery;
    };

    struct WStringViewHash
    {
        std::size_t operator()(std::wstring_view value) const noexcept
        {
            return std::hash<std::wstring_view>{}(value);
        }
    };

    struct WStringViewEqual
    {
        bool operator()(std::wstring_view left, std::wstring_view right) const noexcept
        {
            return left == right;
        }
    };

    struct NativeLogSettingsState
    {
        std::mutex mutex;
        std::chrono::steady_clock::time_point lastCheck = std::chrono::steady_clock::time_point::min();
        std::filesystem::file_time_type settingsLastWrite = std::filesystem::file_time_type::min();
        bool enabled = false;
    };

    NativeLogSettingsState& GetNativeLogSettingsState()
    {
        static NativeLogSettingsState state;
        return state;
    }

    std::wstring GetEnvironmentValue(const wchar_t* name)
    {
        if (name == nullptr || *name == L'\0')
        {
            return {};
        }

        wchar_t* buffer = nullptr;
        std::size_t length = 0;
        if (_wdupenv_s(&buffer, &length, name) != 0 || buffer == nullptr || length == 0)
        {
            if (buffer != nullptr)
            {
                std::free(buffer);
            }

            return {};
        }

        std::wstring value(buffer);
        std::free(buffer);
        return value;
    }

    std::filesystem::path GetNativeLogSettingsPath()
    {
        const auto appData = GetEnvironmentValue(L"APPDATA");
        if (appData.empty())
        {
            return {};
        }

        return std::filesystem::path(appData) / L"PackageManager" / L"settings.json";
    }

    std::filesystem::path GetNativeLogDirectoryPath()
    {
        const auto localAppData = GetEnvironmentValue(L"LOCALAPPDATA");
        if (localAppData.empty())
        {
            return {};
        }

        return std::filesystem::path(localAppData) / L"PackageManager" / L"logs" / L"index-service-diagnostics";
    }

    bool ParseNativeLoggingEnabled(const std::filesystem::path& settingsPath)
    {
        std::ifstream input(settingsPath, std::ios::binary);
        if (!input)
        {
            return false;
        }

        const std::string json(
            (std::istreambuf_iterator<char>(input)),
            std::istreambuf_iterator<char>());

        constexpr char key[] = "\"EnableIndexServicePerformanceAnalysis\"";
        const auto keyPos = json.find(key);
        if (keyPos == std::string::npos)
        {
            return false;
        }

        const auto colonPos = json.find(':', keyPos + sizeof(key) - 1);
        if (colonPos == std::string::npos)
        {
            return false;
        }

        auto valuePos = colonPos + 1;
        while (valuePos < json.length() && std::isspace(static_cast<unsigned char>(json[valuePos])))
        {
            ++valuePos;
        }

        return json.compare(valuePos, 4, "true") == 0;
    }

    bool NativeLoggingEnabled()
    {
        constexpr auto refreshInterval = std::chrono::seconds(2);
        auto& state = GetNativeLogSettingsState();
        std::lock_guard<std::mutex> guard(state.mutex);

        const auto now = std::chrono::steady_clock::now();
        if (state.lastCheck != std::chrono::steady_clock::time_point::min()
            && now - state.lastCheck < refreshInterval)
        {
            return state.enabled;
        }

        state.lastCheck = now;
        const auto settingsPath = GetNativeLogSettingsPath();
        if (settingsPath.empty() || !std::filesystem::exists(settingsPath))
        {
            state.enabled = false;
            state.settingsLastWrite = std::filesystem::file_time_type::min();
            return state.enabled;
        }

        std::error_code error;
        const auto lastWrite = std::filesystem::last_write_time(settingsPath, error);
        if (error)
        {
            state.enabled = false;
            state.settingsLastWrite = std::filesystem::file_time_type::min();
            return state.enabled;
        }

        if (lastWrite == state.settingsLastWrite)
        {
            return state.enabled;
        }

        state.enabled = ParseNativeLoggingEnabled(settingsPath);
        state.settingsLastWrite = lastWrite;
        return state.enabled;
    }

    std::string FormatLogValue(const std::string& value, std::size_t maxLength = 160)
    {
        if (value.empty())
        {
            return "<empty>";
        }

        std::string normalized;
        normalized.reserve(value.size());
        for (const auto ch : value)
        {
            if (ch == '\r' || ch == '\n')
            {
                normalized.push_back(' ');
            }
            else
            {
                normalized.push_back(ch);
            }
        }

        const auto first = normalized.find_first_not_of(' ');
        if (first == std::string::npos)
        {
            return "<empty>";
        }

        const auto last = normalized.find_last_not_of(' ');
        normalized = normalized.substr(first, last - first + 1);
        if (normalized.length() <= maxLength)
        {
            return normalized;
        }

        return normalized.substr(0, maxLength) + "...";
    }

    void NativeLogWrite(const std::string& message)
    {
        if (!NativeLoggingEnabled())
        {
            return;
        }

        const auto logDirectory = GetNativeLogDirectoryPath();
        if (logDirectory.empty())
        {
            return;
        }

        std::error_code error;
        std::filesystem::create_directories(logDirectory, error);
        if (error)
        {
            return;
        }

        SYSTEMTIME now{};
        GetLocalTime(&now);

        char fileName[16]{};
        std::snprintf(
            fileName,
            sizeof(fileName),
            "%04u%02u%02u.log",
            static_cast<unsigned>(now.wYear),
            static_cast<unsigned>(now.wMonth),
            static_cast<unsigned>(now.wDay));

        char line[64]{};
        std::snprintf(
            line,
            sizeof(line),
            "%04u-%02u-%02u %02u:%02u:%02u.%03u",
            static_cast<unsigned>(now.wYear),
            static_cast<unsigned>(now.wMonth),
            static_cast<unsigned>(now.wDay),
            static_cast<unsigned>(now.wHour),
            static_cast<unsigned>(now.wMinute),
            static_cast<unsigned>(now.wSecond),
            static_cast<unsigned>(now.wMilliseconds));

        static std::mutex writeMutex;
        std::lock_guard<std::mutex> guard(writeMutex);
        std::ofstream output(logDirectory / std::filesystem::path(fileName), std::ios::app | std::ios::binary);
        if (!output)
        {
            return;
        }

        output << line << " [INDEX] " << message << "\r\n";
    }

    std::string JoinDriveLetters(const std::vector<wchar_t>& drives)
    {
        std::string result;
        result.reserve(drives.size() * 2);
        for (std::size_t i = 0; i < drives.size(); ++i)
        {
            if (i > 0)
            {
                result.push_back(',');
            }

            result.push_back(static_cast<char>(drives[i]));
        }

        return result;
    }

    std::wstring JoinDriveLettersWide(const std::vector<wchar_t>& drives)
    {
        std::wstring result;
        result.reserve(drives.size() * 3);
        for (std::size_t i = 0; i < drives.size(); ++i)
        {
            if (i > 0)
            {
                result.append(L",");
            }

            result.push_back(static_cast<wchar_t>(towupper(drives[i])));
            result.push_back(L':');
        }

        return result;
    }

    std::size_t CountFrnEntries(const std::unordered_map<wchar_t, VolumeState>& volumes)
    {
        std::size_t total = 0;
        for (const auto& pair : volumes)
        {
            total += pair.second.frnMap.size();
        }

        return total;
    }

    std::size_t CountLiveFrnEntries(
        const std::unordered_map<wchar_t, VolumeState>& volumes,
        std::size_t recordCount)
    {
        const auto frnMapCount = CountFrnEntries(volumes);
        return frnMapCount == 0 ? recordCount : frnMapCount;
    }

    std::uint32_t CountRecordsForVolume(const std::vector<Record>& records, wchar_t driveLetter)
    {
        std::uint32_t count = 0;
        for (const auto& record : records)
        {
            if (record.driveLetter == driveLetter && count < std::numeric_limits<std::uint32_t>::max())
            {
                ++count;
            }
        }

        return count;
    }

    std::uint64_t MaxFrnForVolume(const std::vector<Record>& records, wchar_t driveLetter)
    {
        std::uint64_t maxFrn = 0;
        for (const auto& record : records)
        {
            if (record.driveLetter == driveLetter && record.frn > maxFrn)
            {
                maxFrn = record.frn;
            }
        }

        return maxFrn;
    }

    const char* MatchModeToString(MatchMode mode)
    {
        switch (mode)
        {
        case MatchMode::Prefix:
            return "Prefix";
        case MatchMode::Suffix:
            return "Suffix";
        case MatchMode::Regex:
            return "Regex";
        case MatchMode::Wildcard:
            return "Wildcard";
        default:
            return "Contains";
        }
    }

    const char* SearchFilterToString(SearchFilter filter)
    {
        switch (filter)
        {
        case SearchFilter::Launchable:
            return "Launchable";
        case SearchFilter::Folder:
            return "Folder";
        case SearchFilter::Script:
            return "Script";
        case SearchFilter::Log:
            return "Log";
        case SearchFilter::Config:
            return "Config";
        default:
            return "All";
        }
    }

    class NativeIndex
    {
        struct BuildProgressState
        {
            std::wstring stage;
            double percent = -1.0;
            std::wstring message;
            std::size_t indexedCount = 0;
            wchar_t currentDrive = L'\0';
            long long elapsedMs = 0;
        };

        struct EnumeratedVolume
        {
            wchar_t driveLetter = L'\0';
            bool success = false;
            std::vector<Record> records;
            VolumeState volume;
            std::string error;
        };

    public:
        NativeIndex(NativeJsonCallback statusCallback, NativeJsonCallback changeCallback, void* userData)
            : statusCallback_(statusCallback),
              changeCallback_(changeCallback),
              userData_(userData),
              watcherStopToken_(std::make_shared<std::atomic_bool>(false)),
              backgroundCatchUpStopToken_(std::make_shared<std::atomic_bool>(false))
        {
            StartSnapshotWorker();
        }

        ~NativeIndex()
        {
            Shutdown();
        }

        std::string Build(bool forceRebuild)
        {
            const auto totalStart = std::chrono::steady_clock::now();
            const auto stopCatchUpStart = std::chrono::steady_clock::now();
            StopBackgroundCatchUp();
            const auto stopBackgroundCatchUpMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - stopCatchUpStart).count();
            const auto stopWatchersStart = std::chrono::steady_clock::now();
            StopWatchers();
            const auto stopWatchersMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - stopWatchersStart).count();

            std::unique_lock<std::shared_mutex> guard(mutex_);
            hasShutdown_ = false;
            ClearBuildProgress_NoLock();

            if (!forceRebuild)
            {
                SetBuildProgress_NoLock(L"restore", 5.0, L"正在恢复索引快照", GetRecordCount_NoLock(), L'\0', totalStart);
            }

            if (!forceRebuild && TryLoadSnapshot())
            {
                SetBuildProgress_NoLock(L"restore", 100.0, L"索引快照已恢复", GetRecordCount_NoLock(), L'\0', totalStart);
                currentStatusMessage_ = L"已从 native 快照恢复索引，可立即搜索；后台正在追平 USN...";
                isBackgroundCatchUpInProgress_ = true;
                PublishStatus(false);
                StartBackgroundCatchUp_NoLock();
                NativeLogWrite(
                    "[NATIVE BUILD] mode=snapshot-restore totalMs="
                    + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - totalStart).count())
                    + " stopBackgroundCatchUpMs=" + std::to_string(stopBackgroundCatchUpMs)
                    + " stopWatchersMs=" + std::to_string(stopWatchersMs)
                    + " indexedCount=" + std::to_string(GetRecordCount_NoLock())
                    + " isBackgroundCatchUpInProgress=true");
                return BuildStateJson(false);
            }

            const auto mftBuildStart = std::chrono::steady_clock::now();
            BuildFromMft();
            const auto mftBuildMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - mftBuildStart).count();
            const auto recordsRemapStart = std::chrono::steady_clock::now();
            const bool recordsRemapped = WriteAndMapCurrentRecords_NoLock();
            const auto recordsRemapMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - recordsRemapStart).count();
            if (!recordsRemapped)
            {
                NativeLogWrite(
                    "[NATIVE BUILD] cold-records-remap-failed recordsRemapMs="
                    + std::to_string(recordsRemapMs)
                    + " indexedCount=" + std::to_string(GetRecordCount_NoLock()));
            }
            const auto sidecarSaveStart = std::chrono::steady_clock::now();
            bool sidecarsSaved = false;
            const auto sidecarRecordCount = GetRecordCount_NoLock();
            if (sidecarRecordCount > 0 && v2_.ready && allIds_.size() == sidecarRecordCount)
            {
                try
                {
                    WriteV2PostingsSnapshotFile(sidecarRecordCount, v2_);
                    WriteV2RuntimeSnapshotFile(sidecarRecordCount, v2_);
                    WriteV2TypeBucketsSnapshotFile(sidecarRecordCount, allIds_, directoryIds_, launchableIds_, scriptIds_, logIds_, configIds_, extensionIds_);
                    sidecarsSaved = true;
                }
                catch (const std::exception& ex)
                {
                    NativeLogWrite(
                        "[NATIVE BUILD] cold-sidecar-save-failed reason="
                        + FormatLogValue(ex.what())
                        + " records=" + std::to_string(sidecarRecordCount)
                        + " all=" + std::to_string(allIds_.size()));
                }
                catch (...)
                {
                    NativeLogWrite(
                        "[NATIVE BUILD] cold-sidecar-save-failed reason=unknown"
                        " records=" + std::to_string(sidecarRecordCount)
                        + " all=" + std::to_string(allIds_.size()));
                }
            }
            else
            {
                NativeLogWrite(
                    "[NATIVE BUILD] cold-sidecar-save-skipped records="
                    + std::to_string(sidecarRecordCount)
                    + " v2Ready=" + std::string(v2_.ready ? "true" : "false")
                    + " all=" + std::to_string(allIds_.size()));
            }
            const auto sidecarSaveMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - sidecarSaveStart).count();
            const auto watcherStart = std::chrono::steady_clock::now();
            currentStatusMessage_ = L"索引可搜索，正在后台保存快照";
            isBackgroundCatchUpInProgress_ = false;
            StartWatchers_NoLock();
            const auto watcherStartMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - watcherStart).count();
            lastSearchCompletedTickMs_.store(SteadyClockMilliseconds(), std::memory_order_release);
            forceFullSnapshotSavePending_.store(!sidecarsSaved, std::memory_order_release);
            QueueSnapshotSave_NoLock();
            SetBuildProgress_NoLock(L"ready", 100.0, L"索引可搜索，正在后台保存快照", GetRecordCount_NoLock(), L'\0', totalStart, true);
            PublishStatus(true);
            NativeLogWrite(
                "[NATIVE BUILD] mode=full totalMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - totalStart).count())
                + " stopBackgroundCatchUpMs=" + std::to_string(stopBackgroundCatchUpMs)
                + " stopWatchersMs=" + std::to_string(stopWatchersMs)
                + " mftBuildMs=" + std::to_string(mftBuildMs)
                + " recordsRemapMs=" + std::to_string(recordsRemapMs)
                + " recordsRemapped=" + std::string(recordsRemapped ? "true" : "false")
                + " sidecarSaveMs=" + std::to_string(sidecarSaveMs)
                + " sidecarsSaved=" + std::string(sidecarsSaved ? "true" : "false")
                + " generationRemapMs=0"
                + " watcherStartMs=" + std::to_string(watcherStartMs)
                + " indexedCount=" + std::to_string(GetRecordCount_NoLock())
                + " isBackgroundCatchUpInProgress=false");
            return BuildStateJson(true);
        }

        std::string Search(const std::string& requestJson)
        {
            std::string keywordUtf8;
            std::string filterText;
            int maxResults = 0;
            int offset = 0;
            TryReadJsonStringField(requestJson, "keyword", keywordUtf8);
            TryReadJsonStringField(requestJson, "filter", filterText);
            TryReadJsonIntField(requestJson, "maxResults", maxResults);
            TryReadJsonIntField(requestJson, "offset", offset);
            return SearchCore(
                keywordUtf8,
                ParseFilter(filterText),
                maxResults,
                offset,
                nullptr,
                true);
        }

        bool SearchBinary(
            const char* keywordUtf8,
            int maxResults,
            int offset,
            int filterValue,
            std::vector<std::uint8_t>& payload,
            std::string& error)
        {
            SearchBinaryPayload result;
            const auto json = SearchCore(
                keywordUtf8 == nullptr ? std::string() : std::string(keywordUtf8),
                ParseFilterValue(filterValue),
                maxResults,
                offset,
                &result,
                false);
            if (TryExtractErrorJson(json, error))
            {
                return false;
            }

            payload = BuildSearchBinaryPayload(result);
            return true;
        }

        std::string SearchCore(
            const std::string& keywordUtf8,
            SearchFilter filter,
            int maxResults,
            int offset,
            SearchBinaryPayload* binaryPayload,
            bool buildJson)
        {
            const auto totalStart = std::chrono::steady_clock::now();
            activeSearchCount_.fetch_add(1, std::memory_order_acq_rel);
            struct SearchActivityScope
            {
                const NativeIndex* owner;
                ~SearchActivityScope()
                {
                    owner->lastSearchCompletedTickMs_.store(SteadyClockMilliseconds(), std::memory_order_release);
                    owner->activeSearchCount_.fetch_sub(1, std::memory_order_acq_rel);
                }
            } activity{ this };

            const auto lockWaitStart = std::chrono::steady_clock::now();
            std::shared_lock<std::shared_mutex> guard(mutex_);
            const auto lockWaitMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - lockWaitStart).count();
            if (derivedDirty_ || !v2_.ready)
            {
                return BuildErrorJson("native v2 search accelerator is not ready");
            }

            const auto parseStart = std::chrono::steady_clock::now();
            const auto keyword = Utf8ToWide(keywordUtf8);
            const int safeOffset = std::max(offset, 0);
            const int safeMaxResults = std::max(maxResults, 0);
            const auto parseMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - parseStart).count();

            if (keyword.empty())
            {
                NativeLogWrite("[NATIVE SEARCH] outcome=empty-keyword totalMs=0 filter="
                    + std::string(SearchFilterToString(filter))
                    + " offset=" + std::to_string(offset)
                    + " maxResults=" + std::to_string(maxResults));
                return BuildSearchResponse(0, false, {}, binaryPayload, buildJson);
            }

            std::wstring pathPrefix;
            std::wstring searchTerm;
            ParsePathScope(keyword, pathPrefix, searchTerm);
            if (searchTerm.empty())
            {
                NativeLogWrite("[NATIVE SEARCH] outcome=path-only totalMs=0 filter="
                    + std::string(SearchFilterToString(filter))
                    + " offset=" + std::to_string(offset)
                    + " maxResults=" + std::to_string(maxResults)
                    + " keyword=" + FormatLogValue(keywordUtf8));
                return BuildSearchResponse(0, false, {}, binaryPayload, buildJson);
            }

            MatchMode mode = MatchMode::Contains;
            const auto normalizedQuery = DetectMatchMode(searchTerm, mode);
            const auto* source = GetCandidateSource(filter);
            if (source == nullptr || source->empty())
            {
                NativeLogWrite("[NATIVE SEARCH] outcome=no-candidates totalMs=0 filter="
                    + std::string(SearchFilterToString(filter))
                    + " mode=" + MatchModeToString(mode)
                    + " offset=" + std::to_string(offset)
                    + " maxResults=" + std::to_string(maxResults)
                    + " candidateCount=0 keyword=" + FormatLogValue(keywordUtf8)
                    + " searchTerm=" + FormatLogValue(WideToUtf8(searchTerm))
                    + " normalized=" + FormatLogValue(WideToUtf8(normalizedQuery)));
                return BuildSearchResponse(0, false, {}, binaryPayload, buildJson);
            }

            const bool pathScoped = !pathPrefix.empty();
            const auto candidateCount = source->size();

            std::vector<std::uint32_t> matchedPage;
            int totalMatched = 0;
            const char* acceleratorMode = "none";
            QueryPlan plan;
            std::string unsupportedReason;
            if (!TryBuildQueryPlan(mode, normalizedQuery, plan, unsupportedReason))
            {
                NativeLogWrite(
                    "[NATIVE SEARCH] outcome=unsupported-pattern totalMs="
                    + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - totalStart).count())
                    + " lockWaitMs=" + std::to_string(lockWaitMs)
                    + " filter=" + SearchFilterToString(filter)
                    + " mode=" + MatchModeToString(mode)
                    + " reason=" + unsupportedReason
                    + " keyword=" + FormatLogValue(keywordUtf8)
                    + " normalized=" + FormatLogValue(WideToUtf8(normalizedQuery)));
                return BuildErrorJson("unsupported-pattern:" + unsupportedReason);
            }

            const auto matchStart = std::chrono::steady_clock::now();
            if (!ExecuteQueryPlan_NoLock(
                plan,
                filter,
                pathPrefix,
                safeOffset,
                safeMaxResults,
                matchedPage,
                totalMatched,
                acceleratorMode))
            {
                NativeLogWrite(
                    "[NATIVE SEARCH] outcome=unsupported-pattern totalMs="
                    + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - totalStart).count())
                    + " lockWaitMs=" + std::to_string(lockWaitMs)
                    + " filter=" + SearchFilterToString(filter)
                    + " mode=" + MatchModeToString(plan.mode)
                    + " plan=" + plan.name
                    + " reason=no-index-entry"
                    + " keyword=" + FormatLogValue(keywordUtf8)
                    + " normalized=" + FormatLogValue(WideToUtf8(plan.query)));
                return BuildErrorJson("unsupported-pattern:no-index-entry");
            }
            AppendOverlayMatches_NoLock(
                plan,
                filter,
                pathPrefix,
                safeOffset,
                safeMaxResults,
                matchedPage,
                totalMatched);
            const auto matchMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - matchStart).count();

            std::vector<SearchResultRow> rows;
            rows.reserve(matchedPage.size());
            const auto resolveStart = std::chrono::steady_clock::now();

            for (const auto recordId : matchedPage)
            {
                rows.push_back({
                    ResolveFullPathById_NoLock(recordId),
                    std::wstring(GetOriginalNameView_NoLock(recordId)),
                    IsDirectoryRecord_NoLock(recordId)
                });
            }
            const auto duplicatePathCount = DeduplicateSearchRowsByPath(rows);
            const auto physicalMatched = totalMatched;
            const auto uniqueMatched = std::max(0, totalMatched - duplicatePathCount);
            const auto resolveMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - resolveStart).count();

            const bool isTruncated = uniqueMatched > safeOffset + static_cast<int>(rows.size());
            const auto jsonStart = std::chrono::steady_clock::now();
            auto json = BuildSearchResponse(
                uniqueMatched,
                isTruncated,
                rows,
                binaryPayload,
                buildJson,
                physicalMatched,
                uniqueMatched,
                0);
            const auto jsonMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - jsonStart).count();
            const auto totalMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - totalStart).count();
            NativeLogWrite(
                "[NATIVE SEARCH] outcome=success totalMs="
                + std::to_string(totalMs)
                + " lockWaitMs=" + std::to_string(lockWaitMs)
                + " parseMs=" + std::to_string(parseMs)
                + " matchMs=" + std::to_string(matchMs)
                + " resolveMs=" + std::to_string(resolveMs)
                + " jsonMs=" + std::to_string(jsonMs)
                + " filter=" + SearchFilterToString(filter)
                + " mode=" + MatchModeToString(mode)
                + " plan=" + plan.name
                + " offset=" + std::to_string(safeOffset)
                + " maxResults=" + std::to_string(safeMaxResults)
                + " candidateCount=" + std::to_string(candidateCount)
                + " matched=" + std::to_string(totalMatched)
                + " returned=" + std::to_string(rows.size())
                + " truncated=" + std::string(isTruncated ? "true" : "false")
                + " indexed=" + std::to_string(GetRecordCount_NoLock())
                + " accelerator=" + acceleratorMode
                + " pathScoped=" + std::string(pathScoped ? "true" : "false")
                + " keyword=" + FormatLogValue(keywordUtf8)
                + " searchTerm=" + FormatLogValue(WideToUtf8(searchTerm))
                + " normalized=" + FormatLogValue(WideToUtf8(normalizedQuery))
                + " pathPrefix=" + FormatLogValue(WideToUtf8(pathPrefix)));
            return json;
        }

        std::string GetState() const
        {
            std::shared_lock<std::shared_mutex> guard(mutex_);
            return BuildStateJson(false);
        }

        std::string TestControl(const std::string& requestJson)
        {
            if (!IsTestHooksEnabled())
            {
                return BuildErrorJson("test-hooks-disabled");
            }

            std::string action;
            TryReadJsonStringField(requestJson, "action", action);
            if (action == "injectSyntheticUsnBacklog")
            {
                int count = 0;
                int seed = 0;
                TryReadJsonIntField(requestJson, "count", count);
                TryReadJsonIntField(requestJson, "seed", seed);
                return InjectSyntheticUsnBacklog(count, seed);
            }

            if (action == "waitForDeltaIndexed" || action == "getDeltaState")
            {
                return BuildDeltaStateJson(true, 0);
            }

            return BuildErrorJson("unknown-test-action");
        }

        void Shutdown()
        {
            {
                std::unique_lock<std::shared_mutex> guard(mutex_);
                if (hasShutdown_)
                {
                    return;
                }
            }

            StopBackgroundCatchUp();
            StopWatchers();
            StopSnapshotWorker(true);

            std::unique_lock<std::shared_mutex> guard(mutex_);
            hasShutdown_ = true;
            currentStatusMessage_ = L"native 索引已关闭";
            isBackgroundCatchUpInProgress_ = false;
        }

    private:
        static constexpr std::int32_t SnapshotVersion3 = 3;
        static constexpr std::int32_t SnapshotVersion4 = 4;
        static constexpr std::int32_t V2RecordsSnapshotVersion1 = 1;
        static constexpr std::int32_t V2RecordsSnapshotVersion2 = 2;
        static constexpr std::int32_t V2PostingsSnapshotVersion1 = 1;
        static constexpr std::int32_t V2RuntimeSnapshotVersion7 = 7;
        static constexpr std::int32_t V2TypeBucketsSnapshotVersion1 = 1;
        static constexpr std::int32_t V2OverlaySnapshotVersion1 = 1;
        static constexpr std::size_t ShortDrivePageSize = 1024;
        static constexpr DWORD LiveWatcherBufferSize = 256 * 1024;
        static constexpr DWORD CatchUpBufferSize = 1024 * 1024;
        static constexpr DWORD MftEnumBufferSize = 8 * 1024 * 1024;
        static constexpr DWORD MftEnumFallbackBufferSize = 1024 * 1024;
        static constexpr int MftEnumMaxResumeAttempts = 3;
        static constexpr unsigned long long LiveWaitMilliseconds = 1000;
        static constexpr DWORD ErrorJournalEntryDeleted = 1181;
        static constexpr DWORD ErrorHandleEof = 38;
        static constexpr std::uint64_t NtfsRootFileReferenceNumber = 5;
        static constexpr wchar_t SnapshotWriteMutexName[] = L"PackageManager.MftScannerNativeIndex.WriteLock";
        static constexpr DWORD SnapshotWriteLockTimeoutMilliseconds = 200;
        static constexpr auto SnapshotSaveDebounceMilliseconds = std::chrono::milliseconds(1000);
        static constexpr auto SnapshotSaveStartupDelayMilliseconds = std::chrono::milliseconds(8000);
        static constexpr auto SnapshotCompactionIdleMilliseconds = std::chrono::milliseconds(5000);
        static constexpr auto ForceFullSnapshotIdleMilliseconds = std::chrono::milliseconds(30000);
        static constexpr auto OverlaySegmentSaveDebounceMilliseconds = std::chrono::milliseconds(1000);
        static constexpr std::uint32_t OverlayRecordIdMask = 0x80000000u;
        static constexpr std::size_t OverlayCompactionThreshold = 65536;
        static constexpr std::size_t OverlayImmediateRebuildThreshold = 4096;
        static constexpr std::size_t OverlayPathScopeAncestorCacheLimit = 4096;
        static constexpr std::size_t OverlayStaleRebuildThreshold = 32768;
        static constexpr std::size_t OverlayStaleRebuildRatioDivisor = 4;
        static constexpr std::size_t MaxPathCacheEntriesPerVolume = 32768;
        static constexpr std::size_t MaxPathScopeRecordIdsCacheEntries = 32;

        mutable std::shared_mutex mutex_;
        std::vector<Record> records_;
        std::vector<Record> overlayRecords_;
        std::unordered_map<std::uint64_t, std::uint32_t> overlayIndexByKey_;
        std::unordered_set<std::uint64_t> overlayDeletedKeys_;
        std::vector<std::uint8_t> overlaySuppressedRecordIds_;
        OverlaySearchIndex overlaySearch_;
        std::size_t overlayStalePostingEstimate_ = 0;
        std::chrono::steady_clock::time_point lastOverlayRebuildAttempt_{};
        std::uint64_t overlaySegmentBaseRecordCountOverride_ = 0;
        std::unordered_set<std::uint64_t> overlayCompactedKeys_;
        std::unordered_set<std::uint64_t> overlayCompactedDeletedKeys_;
        mutable std::mutex overlaySegmentSaveMutex_;
        mutable std::atomic<std::uint64_t> lastOverlaySegmentSaveSignature_{ 0 };
        std::unordered_map<std::uint64_t, std::size_t> recordIndexByKey_;
        std::vector<FrnRecordIndexEntry> frnRecordIndex_;
        std::vector<std::uint32_t> allIds_;
        std::vector<std::uint32_t> directoryIds_;
        std::vector<std::uint32_t> launchableIds_;
        std::vector<std::uint32_t> scriptIds_;
        std::vector<std::uint32_t> logIds_;
        std::vector<std::uint32_t> configIds_;
        std::array<std::vector<std::uint32_t>, 26> driveIds_;
        std::unordered_map<std::wstring, std::vector<std::uint32_t>> extensionIds_;
        std::unordered_map<std::wstring_view, std::vector<std::uint32_t>, WStringViewHash, WStringViewEqual> exactNameIds_;
        mutable std::unordered_map<std::uint64_t, std::vector<std::uint32_t>> pathScopeRecordIdsCache_;
        mutable std::uint64_t pathScopeRecordIdsCacheGeneration_ = 0;
        NativeV2Accelerator v2_;
        NativeV2MappedRecords mappedRecords_;
        std::unordered_map<wchar_t, VolumeState> volumes_;
        std::wstring currentStatusMessage_;
        BuildProgressState buildProgress_;
        bool isBackgroundCatchUpInProgress_ = false;
        bool derivedDirty_ = false;
        bool hasShutdown_ = false;
        NativeJsonCallback statusCallback_ = nullptr;
        NativeJsonCallback changeCallback_ = nullptr;
        void* userData_ = nullptr;
        std::vector<std::thread> watcherThreads_;
        std::shared_ptr<std::atomic_bool> watcherStopToken_;
        std::thread backgroundCatchUpThread_;
        std::shared_ptr<std::atomic_bool> backgroundCatchUpStopToken_;
        std::atomic_bool journalResetRebuildScheduled_{ false };
        mutable std::mutex snapshotRequestMutex_;
        mutable std::condition_variable snapshotRequestCv_;
        mutable std::thread snapshotWorkerThread_;
        mutable bool snapshotSavePending_ = false;
        mutable bool snapshotWorkerStop_ = false;
        mutable std::atomic<std::uint64_t> generationId_{ 0 };
        mutable std::atomic<int> activeSearchCount_{ 0 };
        mutable std::atomic<long long> lastSearchCompletedTickMs_{ 0 };
        mutable std::atomic<long long> lastOverlayMutationTickMs_{ 0 };
        mutable std::atomic<long long> lastOverlaySegmentSaveTickMs_{ 0 };
        mutable std::atomic_bool forceFullSnapshotSavePending_{ false };
        mutable std::mutex watcherLifecycleMutex_;

        void StartSnapshotWorker()
        {
            std::lock_guard<std::mutex> guard(snapshotRequestMutex_);
            if (snapshotWorkerThread_.joinable())
            {
                return;
            }

            snapshotSavePending_ = false;
            snapshotWorkerStop_ = false;
            snapshotWorkerThread_ = std::thread([this]()
            {
                SnapshotWorkerLoop();
            });
        }

        void StopSnapshotWorker(bool flushPending)
        {
            std::thread workerToJoin;
            {
                std::lock_guard<std::mutex> guard(snapshotRequestMutex_);
                if (!snapshotWorkerThread_.joinable())
                {
                    snapshotSavePending_ = false;
                    snapshotWorkerStop_ = false;
                    return;
                }

                if (flushPending)
                {
                    snapshotSavePending_ = true;
                }

                snapshotWorkerStop_ = true;
                workerToJoin = std::move(snapshotWorkerThread_);
            }

            snapshotRequestCv_.notify_all();

            if (workerToJoin.joinable())
            {
                workerToJoin.join();
            }

            std::lock_guard<std::mutex> guard(snapshotRequestMutex_);
            snapshotSavePending_ = false;
            snapshotWorkerStop_ = false;
        }

        void SnapshotWorkerLoop()
        {
            for (;;)
            {
                bool shouldSave = false;
                bool shouldStop = false;

                {
                    std::unique_lock<std::mutex> lock(snapshotRequestMutex_);
                    snapshotRequestCv_.wait(lock, [this]()
                    {
                        return snapshotSavePending_ || snapshotWorkerStop_;
                    });

                    if (!snapshotSavePending_ && snapshotWorkerStop_)
                    {
                        break;
                    }

                    shouldSave = snapshotSavePending_;
                    snapshotSavePending_ = false;

                    while (shouldSave && !snapshotWorkerStop_)
                    {
                        const auto wokeForNewRequest = snapshotRequestCv_.wait_for(
                            lock,
                            SnapshotSaveDebounceMilliseconds,
                            [this]()
                            {
                                return snapshotSavePending_ || snapshotWorkerStop_;
                            });

                        if (!wokeForNewRequest)
                        {
                            break;
                        }

                        if (!snapshotWorkerStop_)
                        {
                            snapshotSavePending_ = false;
                        }
                    }

                    shouldStop = snapshotWorkerStop_;
                }

                if (shouldSave)
                {
                    if (!SaveSnapshot(shouldStop))
                    {
                        std::lock_guard<std::mutex> guard(snapshotRequestMutex_);
                        if (!snapshotWorkerStop_)
                        {
                            snapshotSavePending_ = true;
                        }
                    }
                }

                if (shouldStop)
                {
                    break;
                }
            }
        }

        void StopWatchers()
        {
            std::vector<std::thread> threadsToJoin;
            std::shared_ptr<std::atomic_bool> oldToken;

            {
                std::lock_guard<std::mutex> guard(watcherLifecycleMutex_);
                oldToken = watcherStopToken_;
                if (oldToken != nullptr)
                {
                    oldToken->store(true);
                }

                threadsToJoin.swap(watcherThreads_);
                watcherStopToken_ = std::make_shared<std::atomic_bool>(false);
            }

            const auto joinStart = std::chrono::steady_clock::now();
            int joinedCount = 0;
            int cancelRequestedCount = 0;
            for (auto& thread : threadsToJoin)
            {
                if (thread.joinable())
                {
                    if (CancelSynchronousIo(static_cast<HANDLE>(thread.native_handle())) != FALSE)
                    {
                        ++cancelRequestedCount;
                    }
                }
            }

            for (auto& thread : threadsToJoin)
            {
                if (thread.joinable())
                {
                    thread.join();
                    ++joinedCount;
                }
            }

            const auto joinMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - joinStart).count();
            if (joinedCount > 0 || joinMs > 0)
            {
                NativeLogWrite(
                    "[NATIVE WATCHERS STOP] joined=" + std::to_string(joinedCount)
                    + " cancelRequested=" + std::to_string(cancelRequestedCount)
                    + " elapsedMs=" + std::to_string(joinMs)
                    + (joinMs > 2000 ? " slow=true" : " slow=false"));
            }
        }

        void StopBackgroundCatchUp()
        {
            auto token = backgroundCatchUpStopToken_;
            if (token != nullptr)
            {
                token->store(true);
            }

            if (backgroundCatchUpThread_.joinable())
            {
                const auto joinStart = std::chrono::steady_clock::now();
                backgroundCatchUpThread_.join();
                const auto joinMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - joinStart).count();
                NativeLogWrite(
                    "[NATIVE CATCHUP STOP] elapsedMs=" + std::to_string(joinMs)
                    + (joinMs > 2000 ? " slow=true" : " slow=false"));
            }

            backgroundCatchUpStopToken_ = std::make_shared<std::atomic_bool>(false);
        }

        void StartWatchers_NoLock()
        {
            const auto token = watcherStopToken_;
            std::lock_guard<std::mutex> guard(watcherLifecycleMutex_);
            for (const auto& pair : volumes_)
            {
                watcherThreads_.emplace_back([this, driveLetter = pair.first, token]()
                {
                    WatchLoop(driveLetter, token);
                });
            }
        }

        void StartBackgroundCatchUp_NoLock()
        {
            const auto token = backgroundCatchUpStopToken_;
            backgroundCatchUpThread_ = std::thread([this, token]()
            {
                try
                {
                    const auto catchUpStart = std::chrono::steady_clock::now();
                    std::vector<UsnReadState> catchUpStates;
                    std::vector<std::string> payloadsToPublish;
                    {
                        std::shared_lock<std::shared_mutex> guard(mutex_);
                        if (token->load() || hasShutdown_)
                        {
                            return;
                        }

                        catchUpStates.reserve(volumes_.size());
                        for (const auto& pair : volumes_)
                        {
                            catchUpStates.push_back(CreateUsnReadState(pair.second));
                        }
                    }

                    std::vector<VolumeCatchUpResult> catchUpResults;
                    catchUpResults.reserve(catchUpStates.size());
                    bool journalReset = false;
                    bool needsDeferredOverlayRebuild = false;
                    bool needsForcedOverlaySegmentSave = false;
                    bool needsSnapshotSave = false;
                    std::uint64_t catchUpGeneration = 0;
                    for (auto& state : catchUpStates)
                    {
                        if (token->load())
                        {
                            return;
                        }

                        const auto volumeCatchUpStart = std::chrono::steady_clock::now();
                        const auto startUsn = state.nextUsn;
                        const auto startJournalId = state.journalId;
                        VolumeCatchUpResult result;
                        result.driveLetter = state.driveLetter;
                        if (token->load())
                        {
                            break;
                        }

                        result.journalReset = CatchUpVolume(state, token.get(), result.changes);
                        result.state = std::move(state);
                        result.success = true;
                        catchUpResults.push_back(std::move(result));

                        const auto elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                            std::chrono::steady_clock::now() - volumeCatchUpStart).count();
                        if (catchUpResults.back().journalReset)
                        {
                            NativeLogWrite(
                                "[NATIVE SNAPSHOT CATCHUP] drive="
                                + std::string(1, static_cast<char>(catchUpResults.back().driveLetter))
                                + " expired elapsedMs=" + std::to_string(elapsedMs)
                                + " startUsn=" + std::to_string(startUsn)
                                + " journalId=" + std::to_string(startJournalId));
                        }
                        else
                        {
                            NativeLogWrite(
                                "[NATIVE SNAPSHOT CATCHUP] drive="
                                + std::string(1, static_cast<char>(catchUpResults.back().driveLetter))
                                + " elapsedMs=" + std::to_string(elapsedMs)
                                + " startUsn=" + std::to_string(startUsn)
                                + " nextUsn=" + std::to_string(catchUpResults.back().state.nextUsn)
                                + " journalId=" + std::to_string(catchUpResults.back().state.journalId)
                                + " changes=" + std::to_string(catchUpResults.back().changes.size()));
                        }

                        if (journalReset = catchUpResults.back().journalReset)
                        {
                            break;
                        }
                    }

                    bool publishRefresh = false;
                    bool appliedChanges = false;
                    {
                        std::unique_lock<std::shared_mutex> guard(mutex_);
                        if (token->load() || hasShutdown_)
                        {
                            return;
                        }

                        if (token->load() || hasShutdown_)
                        {
                            return;
                        }

                        if (journalReset)
                        {
                            currentStatusMessage_ = L"native 快照需要后台重建；当前快照继续提供搜索";
                        }
                        else
                        {
                            std::size_t totalChangeCount = 0;
                            for (auto& result : catchUpResults)
                            {
                                if (!result.success)
                                {
                                    continue;
                                }

                                totalChangeCount += result.changes.size();
                                const auto volumeIt = volumes_.find(result.driveLetter);
                                if (volumeIt != volumes_.end())
                                {
                                    ApplyUsnReadState(volumeIt->second, std::move(result.state));
                                }
                            }

                            if (totalChangeCount > 0)
                            {
                                std::vector<std::string> appliedPayloads;
                                appliedPayloads.reserve(totalChangeCount);
                                for (auto& result : catchUpResults)
                                {
                                    if (!result.success || result.changes.empty())
                                    {
                                        continue;
                                    }

                                    ApplyCollectedChanges_NoLock(result.changes, &appliedPayloads, true, true);
                                }

                                payloadsToPublish.insert(
                                    payloadsToPublish.end(),
                                    std::make_move_iterator(appliedPayloads.begin()),
                                    std::make_move_iterator(appliedPayloads.end()));
                                appliedChanges = true;
                                needsDeferredOverlayRebuild = true;
                                catchUpGeneration = generationId_.fetch_add(1, std::memory_order_acq_rel) + 1;
                                publishRefresh = true;
                                NativeLogWrite(
                                    "[NATIVE SNAPSHOT CATCHUP OVERLAY] changes="
                                    + std::to_string(totalChangeCount)
                                    + " overlayRecords=" + std::to_string(overlayRecords_.size())
                                    + " overlayDeletes=" + std::to_string(overlayDeletedKeys_.size()));
                            }

                            currentStatusMessage_ = L"已索引 " + std::to_wstring(GetRecordCount_NoLock()) + L" 个对象";
                        }

                        isBackgroundCatchUpInProgress_ = false;
                        const auto watcherStart = std::chrono::steady_clock::now();
                        if (!journalReset)
                        {
                            StartWatchers_NoLock();
                        }
                        const auto watcherStartMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                            std::chrono::steady_clock::now() - watcherStart).count();
                        if (!journalReset && appliedChanges)
                        {
                            needsForcedOverlaySegmentSave = true;
                            needsSnapshotSave = true;
                        }
                        publishRefresh = true;
                        NativeLogWrite(
                            "[NATIVE SNAPSHOT CATCHUP TOTAL] catchUpMs="
                            + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                                std::chrono::steady_clock::now() - catchUpStart).count())
                            + " watcherStartMs=" + std::to_string(watcherStartMs)
                            + " indexedCount=" + std::to_string(GetRecordCount_NoLock()));
                    }

                    if (needsDeferredOverlayRebuild && catchUpGeneration != 0)
                    {
                        RebuildOverlaySearchIndexOutsideExclusiveLock(catchUpGeneration);
                    }

                    if (needsForcedOverlaySegmentSave)
                    {
                        if (!SaveOverlaySegmentSnapshotOutsideExclusiveLock(true))
                        {
                            NativeLogWrite("[NATIVE SNAPSHOT CATCHUP OVERLAY] overlay-segment-save-failed");
                        }
                    }

                    if (needsSnapshotSave)
                    {
                        QueueSnapshotSave();
                    }

                    if (publishRefresh)
                    {
                        PublishStatus(true);
                    }

                    if (journalReset)
                    {
                        ScheduleJournalResetRebuild();
                    }

                    for (const auto& payload : payloadsToPublish)
                    {
                        PublishChangePayload(payload);
                    }
                }
                catch (const std::exception& ex)
                {
                    {
                        std::unique_lock<std::shared_mutex> guard(mutex_);
                        currentStatusMessage_ = L"native 后台追平失败: " + Utf8ToWide(ex.what());
                        isBackgroundCatchUpInProgress_ = false;
                    }

                    PublishStatus(false);
                }
                catch (...)
                {
                    {
                        std::unique_lock<std::shared_mutex> guard(mutex_);
                        currentStatusMessage_ = L"native 后台追平失败";
                        isBackgroundCatchUpInProgress_ = false;
                    }

                    PublishStatus(false);
                }
            });
        }

        static UsnReadState CreateUsnReadState(const VolumeState& volume)
        {
            UsnReadState state;
            state.driveLetter = volume.driveLetter;
            state.nextUsn = volume.nextUsn;
            state.journalId = volume.journalId;
            state.pendingRenameOldByFrn = volume.pendingRenameOldByFrn;
            return state;
        }

        static void ApplyUsnReadState(VolumeState& volume, UsnReadState&& state)
        {
            volume.nextUsn = state.nextUsn;
            volume.journalId = state.journalId;
            volume.pendingRenameOldByFrn = std::move(state.pendingRenameOldByFrn);
        }

        void SetBuildProgress_NoLock(
            const wchar_t* stage,
            double percent,
            const wchar_t* message,
            std::size_t indexedCount,
            wchar_t currentDrive,
            const std::chrono::steady_clock::time_point& start,
            bool requireSearchRefresh = false)
        {
            buildProgress_.stage = stage == nullptr ? L"" : stage;
            buildProgress_.percent = percent;
            buildProgress_.message = message == nullptr ? L"" : message;
            buildProgress_.indexedCount = indexedCount;
            buildProgress_.currentDrive = currentDrive;
            buildProgress_.elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - start).count();
            currentStatusMessage_ = buildProgress_.message;
            PublishStatus(requireSearchRefresh);
        }

        void PublishEnumerationProgress_NoLock(
            const std::vector<EnumeratedVolume>& results,
            const std::vector<bool>& completed,
            const std::vector<wchar_t>& fixedDrives,
            std::size_t completedCount,
            const std::chrono::steady_clock::time_point& start,
            const wchar_t* detail)
        {
            std::size_t enumeratedRecords = 0;
            wchar_t currentDrive = L'\0';
            std::wstring pendingDrives;
            for (std::size_t i = 0; i < results.size(); ++i)
            {
                if (completed[i] && results[i].success)
                {
                    enumeratedRecords += results[i].records.size();
                }
                else if (!completed[i])
                {
                    if (currentDrive == L'\0')
                    {
                        currentDrive = results[i].driveLetter;
                    }

                    if (!pendingDrives.empty())
                    {
                        pendingDrives.append(L",");
                    }

                    pendingDrives.push_back(static_cast<wchar_t>(towupper(results[i].driveLetter)));
                    pendingDrives.push_back(L':');
                }
            }

            const auto total = std::max<std::size_t>(fixedDrives.size(), 1);
            const auto percent = 1.0 + (static_cast<double>(completedCount) / static_cast<double>(total)) * 54.0;
            std::wstring message = detail == nullptr || detail[0] == L'\0'
                ? L"正在枚举 MFT 记录"
                : detail;
            message.append(L"（");
            message.append(std::to_wstring(completedCount));
            message.push_back(L'/');
            message.append(std::to_wstring(fixedDrives.size()));
            message.append(L" 个固定盘");
            if (!pendingDrives.empty())
            {
                message.append(L"，等待 ");
                message.append(pendingDrives);
            }
            message.push_back(L'）');

            SetBuildProgress_NoLock(
                L"enumerate",
                percent,
                message.c_str(),
                enumeratedRecords,
                currentDrive,
                start);
        }

        void ClearBuildProgress_NoLock()
        {
            buildProgress_ = BuildProgressState();
        }

        void BuildFromMft()
        {
            const auto totalStart = std::chrono::steady_clock::now();
            records_.clear();
            mappedRecords_.Clear();
            overlayRecords_.clear();
            overlayIndexByKey_.clear();
            overlayDeletedKeys_.clear();
            overlaySuppressedRecordIds_.clear();
            overlaySearch_.Clear();
            overlaySegmentBaseRecordCountOverride_ = 0;
            overlayCompactedKeys_.clear();
            overlayCompactedDeletedKeys_.clear();
            recordIndexByKey_.clear();
            frnRecordIndex_.clear();
            volumes_.clear();
            derivedDirty_ = false;
            ClearBuildProgress_NoLock();
            SetBuildProgress_NoLock(L"enumerate", 0.0, L"正在准备枚举固定磁盘", 0, L'\0', totalStart);

            const auto drivesMask = GetLogicalDrives();
            std::vector<wchar_t> fixedDrives;
            fixedDrives.reserve(8);
            for (int i = 0; i < 26; ++i)
            {
                if ((drivesMask & (1u << i)) == 0)
                {
                    continue;
                }

                wchar_t root[] = { static_cast<wchar_t>(L'A' + i), L':', L'\\', L'\0' };
                if (GetDriveTypeW(root) != DRIVE_FIXED)
                {
                    continue;
                }

                fixedDrives.push_back(root[0]);
            }

            NativeLogWrite("[NATIVE MFT BUILD] start drives=" + JoinDriveLetters(fixedDrives));
            std::wstring enumerateStartMessage = L"正在枚举 MFT 记录";
            if (!fixedDrives.empty())
            {
                enumerateStartMessage.append(L"（固定盘 ");
                enumerateStartMessage.append(JoinDriveLettersWide(fixedDrives));
                enumerateStartMessage.append(L"）");
            }
            SetBuildProgress_NoLock(L"enumerate", 1.0, enumerateStartMessage.c_str(), 0, fixedDrives.empty() ? L'\0' : fixedDrives.front(), totalStart);

            const auto enumerateStart = std::chrono::steady_clock::now();
            std::vector<EnumeratedVolume> results(fixedDrives.size());
            std::vector<std::future<void>> tasks;
            tasks.reserve(fixedDrives.size());

            for (std::size_t i = 0; i < fixedDrives.size(); ++i)
            {
                results[i].driveLetter = fixedDrives[i];
                tasks.emplace_back(std::async(std::launch::async, [this, &results, i]()
                {
                    const auto volumeStart = std::chrono::steady_clock::now();
                    try
                    {
                        results[i].success = EnumerateVolume_NoLock(
                            results[i].driveLetter,
                            results[i].records,
                            results[i].volume);
                        if (results[i].success)
                        {
                            results[i].volume.recordCount = static_cast<std::uint32_t>(std::min<std::size_t>(
                                results[i].records.size(),
                                std::numeric_limits<std::uint32_t>::max()));
                            NativeLogWrite(
                                "[NATIVE MFT ENUM] drive=" + std::string(1, static_cast<char>(results[i].driveLetter))
                                + " success elapsedMs=" + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                                    std::chrono::steady_clock::now() - volumeStart).count())
                                + " records=" + std::to_string(results[i].records.size())
                                + " maxFrn=" + std::to_string(results[i].volume.maxFrn)
                                + " complete=" + std::string(results[i].volume.enumerationComplete ? "true" : "false")
                                + " nextUsn=" + std::to_string(results[i].volume.nextUsn)
                                + " journalId=" + std::to_string(results[i].volume.journalId));
                        }
                        else
                        {
                            NativeLogWrite(
                                "[NATIVE MFT ENUM] drive=" + std::string(1, static_cast<char>(results[i].driveLetter))
                                + " fail elapsedMs=" + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                                    std::chrono::steady_clock::now() - volumeStart).count())
                                + " error=<empty>");
                        }
                    }
                    catch (const std::exception& ex)
                    {
                        results[i].error = ex.what();
                        NativeLogWrite(
                            "[NATIVE MFT ENUM] drive=" + std::string(1, static_cast<char>(results[i].driveLetter))
                            + " fail elapsedMs=" + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                                std::chrono::steady_clock::now() - volumeStart).count())
                            + " error=" + FormatLogValue(ex.what()));
                    }
                    catch (...)
                    {
                        results[i].error = "unknown native enumeration failure";
                        NativeLogWrite(
                            "[NATIVE MFT ENUM] drive=" + std::string(1, static_cast<char>(results[i].driveLetter))
                            + " fail elapsedMs=" + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                                std::chrono::steady_clock::now() - volumeStart).count())
                            + " error=unknown native enumeration failure");
                    }
                }));
            }

            std::vector<bool> completed(tasks.size(), false);
            std::size_t completedCount = 0;
            auto lastPublish = std::chrono::steady_clock::now();
            while (completedCount < tasks.size())
            {
                bool madeProgress = false;
                for (std::size_t i = 0; i < tasks.size(); ++i)
                {
                    if (completed[i])
                    {
                        continue;
                    }

                    if (tasks[i].wait_for(std::chrono::milliseconds(0)) != std::future_status::ready)
                    {
                        continue;
                    }

                    tasks[i].get();
                    completed[i] = true;
                    ++completedCount;
                    madeProgress = true;

                    std::wstring message = L"已完成 ";
                    message.push_back(static_cast<wchar_t>(towupper(results[i].driveLetter)));
                    message.append(L": 盘枚举");
                    PublishEnumerationProgress_NoLock(
                        results,
                        completed,
                        fixedDrives,
                        completedCount,
                        totalStart,
                        message.c_str());
                    lastPublish = std::chrono::steady_clock::now();
                }

                if (!madeProgress)
                {
                    const auto now = std::chrono::steady_clock::now();
                    if (now - lastPublish >= std::chrono::milliseconds(500))
                    {
                        PublishEnumerationProgress_NoLock(
                            results,
                            completed,
                            fixedDrives,
                            completedCount,
                            totalStart,
                            L"正在枚举 MFT 记录");
                        lastPublish = now;
                    }

                    std::this_thread::sleep_for(std::chrono::milliseconds(25));
                }
            }

            std::size_t totalRecordCount = 0;
            int enumeratedVolumeCount = 0;
            std::string firstError;
            for (const auto& result : results)
            {
                if (!result.error.empty() && firstError.empty())
                {
                    firstError = result.error;
                }

                if (!result.success)
                {
                    throw std::runtime_error(
                        result.error.empty()
                            ? "native MFT enumeration failed for fixed drive"
                            : result.error);
                }

                ++enumeratedVolumeCount;
                totalRecordCount += result.records.size();
            }

            if (!fixedDrives.empty() && enumeratedVolumeCount == 0)
            {
                throw std::runtime_error(
                    firstError.empty()
                        ? "native MFT enumeration failed for all fixed drives"
                        : firstError);
            }

            const auto enumerateMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - enumerateStart).count();
            const auto mergeStart = std::chrono::steady_clock::now();
            SetBuildProgress_NoLock(L"merge", 55.0, L"正在合并各卷索引记录", totalRecordCount, L'\0', totalStart);
            records_.reserve(totalRecordCount);
            for (auto& result : results)
            {
                if (!result.success)
                {
                    continue;
                }

                if (!result.records.empty())
                {
                    records_.insert(
                        records_.end(),
                        std::make_move_iterator(result.records.begin()),
                        std::make_move_iterator(result.records.end()));
                }

                volumes_[result.driveLetter] = std::move(result.volume);
            }
            PopulateVolumeRecordMetrics_NoLock(records_, volumes_);

            const auto mergeMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - mergeStart).count();
            const auto buildStart = std::chrono::steady_clock::now();
            SetBuildProgress_NoLock(L"derived", 60.0, L"正在构建搜索加速结构", records_.size(), L'\0', totalStart);
            RebuildDerivedData_NoLock("mft-build", false);
            const auto buildMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - buildStart).count();
            SetBuildProgress_NoLock(L"derived", 92.0, L"搜索加速结构已构建，正在收尾", records_.size(), L'\0', totalStart);
            NativeLogWrite(
                "[NATIVE MFT BUILD] success totalMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - totalStart).count())
                + " enumerateMs=" + std::to_string(enumerateMs)
                + " mergeMs=" + std::to_string(mergeMs)
                + " buildMs=" + std::to_string(buildMs)
                + " indexedCount=" + std::to_string(records_.size())
                + " successfulDrives=" + std::to_string(enumeratedVolumeCount)
                + " failedDrives=" + std::to_string(static_cast<int>(fixedDrives.size()) - enumeratedVolumeCount));
        }

        bool EnumerateVolume_NoLock(wchar_t driveLetter, std::vector<Record>& outRecords, VolumeState& outVolume)
        {
            std::string error;
            if (EnumerateVolumeEntries_NoLock(driveLetter, outRecords, outVolume, MftEnumBufferSize, error))
            {
                return true;
            }

            NativeLogWrite(
                "[NATIVE MFT ENUM] drive=" + std::string(1, static_cast<char>(driveLetter))
                + " retry reason=" + FormatLogValue(error)
                + " bufferBytes=" + std::to_string(MftEnumFallbackBufferSize));
            return EnumerateVolumeEntries_NoLock(driveLetter, outRecords, outVolume, MftEnumFallbackBufferSize, error);
        }

        bool EnumerateVolumeEntries_NoLock(
            wchar_t driveLetter,
            std::vector<Record>& outRecords,
            VolumeState& outVolume,
            DWORD bufferSize,
            std::string& error)
        {
            const auto volumePath = BuildVolumePath(driveLetter);
            HANDLE handle = OpenVolumeHandle(volumePath);
            if (handle == INVALID_HANDLE_VALUE)
            {
                error = "open-volume-failed:" + std::to_string(GetLastError());
                return false;
            }

            auto* buffer = static_cast<std::byte*>(std::malloc(bufferSize));
            if (buffer == nullptr)
            {
                CloseHandle(handle);
                throw std::bad_alloc();
            }

            struct MftEnumDataV0Native
            {
                DWORDLONG StartFileReferenceNumber;
                USN LowUsn;
                USN HighUsn;
            };

            outRecords.clear();
            outRecords.reserve(300000);
            std::uint64_t lastFrn = std::numeric_limits<std::uint64_t>::max();
            std::uint32_t lastRecordIndex = std::numeric_limits<std::uint32_t>::max();

            MftEnumDataV0Native enumData{};
            enumData.StartFileReferenceNumber = 0;
            enumData.LowUsn = 0;
            enumData.HighUsn = MAXLONGLONG;

            DWORD bytesReturned = 0;
            DWORD lastError = ERROR_SUCCESS;
            std::uint64_t batchCount = 0;
            std::uint64_t maxFrn = 0;
            int resumeAttempts = 0;
            while (true)
            {
                bytesReturned = 0;
                if (!DeviceIoControl(
                    handle,
                    FSCTL_ENUM_USN_DATA,
                    &enumData,
                    sizeof(enumData),
                    buffer,
                    bufferSize,
                    &bytesReturned,
                    nullptr))
                {
                    lastError = GetLastError();
                    if (lastError != ErrorHandleEof && resumeAttempts < MftEnumMaxResumeAttempts)
                    {
                        ++resumeAttempts;
                        NativeLogWrite(
                            "[NATIVE MFT ENUM] drive=" + std::string(1, static_cast<char>(driveLetter))
                            + " resume attempt=" + std::to_string(resumeAttempts)
                            + " lastError=" + std::to_string(lastError)
                            + " startFrn=" + std::to_string(enumData.StartFileReferenceNumber)
                            + " records=" + std::to_string(outRecords.size())
                            + " bufferBytes=" + std::to_string(bufferSize));
                        CloseHandle(handle);
                        handle = OpenVolumeHandle(volumePath);
                        if (handle != INVALID_HANDLE_VALUE)
                        {
                            continue;
                        }

                        lastError = GetLastError();
                    }

                    break;
                }

                lastError = ERROR_SUCCESS;
                if (bytesReturned <= sizeof(USN))
                {
                    continue;
                }

                ++batchCount;
                enumData.StartFileReferenceNumber = *reinterpret_cast<DWORDLONG*>(buffer);
                DWORD offset = sizeof(USN);
                while (offset + 60 < bytesReturned)
                {
                    const auto* recordHeader = reinterpret_cast<const USN_RECORD_V2*>(buffer + offset);
                    if (recordHeader->RecordLength == 0
                        || offset + recordHeader->RecordLength > bytesReturned)
                    {
                        break;
                    }

                    const auto frn = static_cast<std::uint64_t>(recordHeader->FileReferenceNumber & 0x0000FFFFFFFFFFFFull);
                    const auto parentFrn = static_cast<std::uint64_t>(recordHeader->ParentFileReferenceNumber & 0x0000FFFFFFFFFFFFull);
                    const auto* fileNamePtr = reinterpret_cast<const wchar_t*>(reinterpret_cast<const std::byte*>(recordHeader) + recordHeader->FileNameOffset);
                    const auto fileNameLength = recordHeader->FileNameLength / sizeof(wchar_t);
                    const bool isDirectory = (recordHeader->FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;

                    if (frn != lastFrn)
                    {
                        Record record;
                        record.frn = frn;
                        record.parentFrn = parentFrn;
                        record.driveLetter = driveLetter;
                        record.originalName.assign(fileNamePtr, fileNamePtr + fileNameLength);
                        record.lowerName = ToLower(record.originalName);
                        record.isDirectory = isDirectory;
                        lastFrn = frn;
                        lastRecordIndex = static_cast<std::uint32_t>(outRecords.size());
                        if (frn > maxFrn)
                        {
                            maxFrn = frn;
                        }
                        outRecords.push_back(std::move(record));
                    }
                    else if (lastRecordIndex < outRecords.size())
                    {
                        auto& record = outRecords[lastRecordIndex];
                        if (fileNameLength > record.originalName.length())
                        {
                            record.parentFrn = parentFrn;
                            record.originalName.assign(fileNamePtr, fileNamePtr + fileNameLength);
                            record.lowerName = ToLower(record.originalName);
                            record.isDirectory = isDirectory;
                        }
                    }

                    offset += recordHeader->RecordLength;
                }
            }

            if (lastError != ErrorHandleEof)
            {
                error = "enum-incomplete:lastError=" + std::to_string(lastError)
                    + ";startFrn=" + std::to_string(enumData.StartFileReferenceNumber)
                    + ";records=" + std::to_string(outRecords.size())
                    + ";batches=" + std::to_string(batchCount)
                    + ";bytesReturned=" + std::to_string(bytesReturned)
                    + ";resumeAttempts=" + std::to_string(resumeAttempts);
                std::free(buffer);
                if (handle != INVALID_HANDLE_VALUE)
                {
                    CloseHandle(handle);
                }
                return false;
            }

            VolumeState volume;
            volume.driveLetter = driveLetter;
            volume.recordCount = static_cast<std::uint32_t>(std::min<std::size_t>(outRecords.size(), std::numeric_limits<std::uint32_t>::max()));
            volume.maxFrn = maxFrn;
            volume.enumerationComplete = true;

            USN_JOURNAL_DATA_V0 journalData{};
            if (QueryJournal_NoLock(handle, journalData))
            {
                volume.nextUsn = journalData.NextUsn;
                volume.journalId = journalData.UsnJournalID;
            }

            outVolume = std::move(volume);

            std::free(buffer);
            if (handle != INVALID_HANDLE_VALUE)
            {
                CloseHandle(handle);
            }
            error.clear();
            return true;
        }

        void RebuildDerivedData_NoLock(const char* reason = nullptr, bool recordsAlreadySorted = false, bool buildV2 = true)
        {
            const auto totalStart = std::chrono::steady_clock::now();
            long long sortMs = 0;
            (void)recordsAlreadySorted;

            recordIndexByKey_.clear();
            frnRecordIndex_.clear();
            allIds_.clear();
            directoryIds_.clear();
            launchableIds_.clear();
            scriptIds_.clear();
            logIds_.clear();
            configIds_.clear();
            ClearDriveIds_NoLock();
            extensionIds_.clear();
            v2_.Clear();
            ClearPathScopeRecordIdsCache_NoLock();

            frnRecordIndex_.reserve(records_.size());
            allIds_.reserve(records_.size());
            directoryIds_.reserve(records_.size() / 8);
            launchableIds_.reserve(records_.size() / 8);
            extensionIds_.reserve(records_.size() / 8);

            const auto bucketStart = std::chrono::steady_clock::now();
            const auto baseBucketStart = bucketStart;
            for (std::uint32_t i = 0; i < records_.size(); ++i)
            {
                const auto& record = records_[i];
                const auto key = MakeRecordKey(record.driveLetter, record.frn);
                frnRecordIndex_.push_back(FrnRecordIndexEntry{ key, i });
                allIds_.push_back(i);
                AddDriveId_NoLock(record.driveLetter, i);
                if (record.isDirectory)
                {
                    directoryIds_.push_back(i);
                    continue;
                }

                const auto extension = GetExtension(record.lowerName);
                if (!extension.empty())
                {
                    extensionIds_[extension].push_back(i);
                }
                if (IsLaunchableExtension(extension))
                {
                    launchableIds_.push_back(i);
                }
                if (IsScriptExtension(extension))
                {
                    scriptIds_.push_back(i);
                }
                if (IsLogExtension(extension))
                {
                    logIds_.push_back(i);
                }
                if (IsConfigExtension(extension))
                {
                    configIds_.push_back(i);
                }
            }
            const auto baseBucketMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - baseBucketStart).count();

            const auto frnSortStart = std::chrono::steady_clock::now();
            SortFrnRecordIndex_NoLock();
            ReleaseDuplicateFrnNameStorage_NoLock();
            const auto frnSortMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - frnSortStart).count();

            long long v2Ms = 0;
            if (buildV2)
            {
                const auto v2Start = std::chrono::steady_clock::now();
                BuildV2Accelerator_NoLock();
                v2Ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - v2Start).count();
            }

            derivedDirty_ = false;
            generationId_.fetch_add(1, std::memory_order_relaxed);
            if (reason != nullptr)
            {
                const auto bucketMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - bucketStart).count();
                NativeLogWrite(
                    "[NATIVE DERIVED BUILD] reason=" + std::string(reason)
                    + " totalMs=" + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - totalStart).count())
                    + " sortMs=" + std::to_string(sortMs)
                    + " sortSkipped=true"
                    + " bucketMs=" + std::to_string(bucketMs)
                    + " baseBucketMs=" + std::to_string(baseBucketMs)
                    + " frnSortMs=" + std::to_string(frnSortMs)
                    + " v2Ms=" + std::to_string(v2Ms)
                    + " v2Ready=" + std::string(v2_.ready ? "true" : "false")
                    + " v2TrigramBuckets=" + std::to_string(v2_.trigramEntries.size())
                    + " v2TrigramBytes=" + std::to_string(v2_.trigramPostings.size())
                    + " v2Bytes=" + std::to_string(v2_.postingsBytes)
                    + " indexedCount=" + std::to_string(records_.size()));
            }
        }

        void BuildBaseBuckets_NoLock(const char* reason = nullptr, bool recordsAlreadySorted = true)
        {
            const auto totalStart = std::chrono::steady_clock::now();
            long long sortMs = 0;
            if (!recordsAlreadySorted)
            {
                const auto sortStart = totalStart;
                std::sort(records_.begin(), records_.end(), [](const Record& left, const Record& right)
                {
                    if (left.lowerName != right.lowerName)
                    {
                        return left.lowerName < right.lowerName;
                    }

                    if (left.driveLetter != right.driveLetter)
                    {
                        return left.driveLetter < right.driveLetter;
                    }

                    return left.frn < right.frn;
                });
                sortMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - sortStart).count();
            }

            recordIndexByKey_.clear();
            frnRecordIndex_.clear();
            allIds_.clear();
            directoryIds_.clear();
            launchableIds_.clear();
            scriptIds_.clear();
            logIds_.clear();
            configIds_.clear();
            ClearDriveIds_NoLock();
            exactNameIds_.clear();
            extensionIds_.clear();
            ClearPathScopeRecordIdsCache_NoLock();

            frnRecordIndex_.reserve(records_.size());
            allIds_.reserve(records_.size());
            directoryIds_.reserve(records_.size() / 8);
            launchableIds_.reserve(records_.size() / 8);
            extensionIds_.reserve(records_.size() / 8);

            const auto bucketStart = std::chrono::steady_clock::now();
            for (std::uint32_t i = 0; i < records_.size(); ++i)
            {
                const auto& record = records_[i];
                const auto key = MakeRecordKey(record.driveLetter, record.frn);
                frnRecordIndex_.push_back(FrnRecordIndexEntry{ key, i });
                allIds_.push_back(i);
                AddDriveId_NoLock(record.driveLetter, i);
                if (record.isDirectory)
                {
                    directoryIds_.push_back(i);
                    continue;
                }

                const auto extension = GetExtension(record.lowerName);
                if (!extension.empty())
                {
                    extensionIds_[extension].push_back(i);
                }
                if (IsLaunchableExtension(extension))
                {
                    launchableIds_.push_back(i);
                }
                if (IsScriptExtension(extension))
                {
                    scriptIds_.push_back(i);
                }
                if (IsLogExtension(extension))
                {
                    logIds_.push_back(i);
                }
                if (IsConfigExtension(extension))
                {
                    configIds_.push_back(i);
                }
            }

            SortFrnRecordIndex_NoLock();
            ReleaseDuplicateFrnNameStorage_NoLock();

            derivedDirty_ = false;
            generationId_.fetch_add(1, std::memory_order_relaxed);
            if (reason != nullptr)
            {
                const auto bucketMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - bucketStart).count();
                NativeLogWrite(
                    "[NATIVE BASE BUCKET BUILD] reason=" + std::string(reason)
                    + " totalMs=" + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - totalStart).count())
                    + " sortMs=" + std::to_string(sortMs)
                    + " sortSkipped=" + std::string(recordsAlreadySorted ? "true" : "false")
                    + " bucketMs=" + std::to_string(bucketMs)
                    + " indexedCount=" + std::to_string(records_.size()));
            }
        }

        void EnsureDerivedData_NoLock()
        {
            if (derivedDirty_)
            {
                RebuildDerivedData_NoLock();
            }
        }

        void BuildV2Accelerator_NoLock()
        {
            const auto totalStart = std::chrono::steady_clock::now();
            long long emitMs = 0;
            long long entryBuildMs = 0;
            long long encodeMs = 0;
            long long shortCompressMs = 0;
            long long parentMs = 0;
            const auto workerCount = GetV2WorkerCount(records_.size());
            auto ranges = BuildV2WorkerRanges(records_.size(), workerCount);
            std::vector<std::unique_ptr<NativeV2BuildLocal>> locals(workerCount);
            std::vector<std::future<void>> emitTasks;
            emitTasks.reserve(workerCount);
            const auto emitStart = std::chrono::steady_clock::now();
            for (std::size_t worker = 0; worker < workerCount; ++worker)
            {
                locals[worker] = std::make_unique<NativeV2BuildLocal>();
                emitTasks.emplace_back(std::async(std::launch::async, [this, &ranges, &locals, worker]()
                {
                    auto& local = *locals[worker];
                    local.trigrams.reserve(32768);
                    std::vector<std::uint64_t> uniqueKeys;
                    for (std::uint32_t recordId = ranges[worker].first; recordId < ranges[worker].second; ++recordId)
                    {
                        const auto& name = records_[recordId].lowerName;
                        BuildUniqueTrigramKeys_NoLock(name, uniqueKeys);
                        for (const auto key : uniqueKeys)
                        {
                            local.trigrams[key].push_back(recordId);
                        }

                        if (name.length() >= 4)
                        {
                            local.prefix4[PackPrefix4(name)].push_back(recordId);
                        }

                        AddShortQueryTokensToLocal_NoLock(name, recordId, local.shortQuery);
                    }
                }));
            }

            for (auto& task : emitTasks)
            {
                task.get();
            }
            emitMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - emitStart).count();

            const auto entryBuildStart = std::chrono::steady_clock::now();
            std::unordered_map<std::uint64_t, std::uint32_t> trigramCounts;
            trigramCounts.reserve(131072);
            std::unordered_map<std::uint64_t, std::uint32_t> prefix4Counts;
            prefix4Counts.reserve(65536);
            for (const auto& local : locals)
            {
                for (const auto& pair : local->trigrams)
                {
                    trigramCounts[pair.first] += static_cast<std::uint32_t>(pair.second.size());
                }

                for (const auto& pair : local->prefix4)
                {
                    prefix4Counts[pair.first] += static_cast<std::uint32_t>(pair.second.size());
                }
            }

            v2_.trigramEntries.clear();
            v2_.trigramPostings.clear();
            v2_.prefix4Entries.clear();
            v2_.prefix4Postings.clear();
            v2_.trigramEntries.reserve(trigramCounts.size());
            v2_.prefix4Entries.reserve(prefix4Counts.size());
            std::uint64_t totalPostings = 0;
            for (const auto& pair : trigramCounts)
            {
                totalPostings += pair.second;
                v2_.trigramEntries.push_back(NativeV2BucketEntry{ pair.first, static_cast<std::uint32_t>(totalPostings - pair.second), pair.second, 0 });
            }

            std::sort(v2_.trigramEntries.begin(), v2_.trigramEntries.end(), [](const auto& left, const auto& right)
            {
                return left.key < right.key;
            });
            entryBuildMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - entryBuildStart).count();

            const auto encodeStart = std::chrono::steady_clock::now();
            v2_.trigramPostings.reserve(static_cast<std::size_t>(std::min<std::uint64_t>(
                totalPostings * 2,
                static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max()))));
            for (auto& entry : v2_.trigramEntries)
            {
                const auto byteOffset = static_cast<std::uint32_t>(v2_.trigramPostings.size());
                EncodeMergedLocalTrigramPosting_NoLock(entry.key, locals, v2_.trigramPostings);
                entry.offset = byteOffset;
                entry.byteCount = static_cast<std::uint32_t>(v2_.trigramPostings.size() - byteOffset);
            }
            v2_.trigramPostings.shrink_to_fit();
            EncodePrefix4PostingsFromLocals_NoLock(prefix4Counts, locals, v2_);
            encodeMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - encodeStart).count();

            const auto shortCompressStart = std::chrono::steady_clock::now();
            MergeShortQueryLocals_NoLock(locals);
            CompressShortPostings_NoLock();
            shortCompressMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - shortCompressStart).count();

            const auto parentStart = std::chrono::steady_clock::now();
            BuildParentOrder_NoLock();
            parentMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - parentStart).count();
            v2_.buildMs = static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - totalStart).count());
            v2_.postingsBytes =
                (static_cast<std::uint64_t>(v2_.trigramEntries.size()) * sizeof(NativeV2BucketEntry))
                + static_cast<std::uint64_t>(v2_.trigramPostings.size())
                + (static_cast<std::uint64_t>(v2_.prefix4Entries.size()) * sizeof(NativeV2BucketEntry))
                + static_cast<std::uint64_t>(v2_.prefix4Postings.size())
                + EstimateShortPostingBytes_NoLock()
                + (static_cast<std::uint64_t>(v2_.parentOrder.size()) * sizeof(std::uint32_t));
            v2_.ready = true;
        }

        static bool BuildV2AcceleratorForSnapshotRecords(
            const std::vector<Record>& records,
            const std::unordered_map<wchar_t, VolumeState>& volumes,
            NativeV2Accelerator& v2)
        {
            const auto totalStart = std::chrono::steady_clock::now();
            v2.Clear();
            if (records.empty())
            {
                v2.ready = true;
                return true;
            }

            const auto workerCount = GetV2WorkerCount(records.size());
            auto ranges = BuildV2WorkerRanges(records.size(), workerCount);
            std::vector<std::unique_ptr<NativeV2BuildLocal>> locals(workerCount);
            std::vector<std::future<void>> emitTasks;
            emitTasks.reserve(workerCount);
            for (std::size_t worker = 0; worker < workerCount; ++worker)
            {
                locals[worker] = std::make_unique<NativeV2BuildLocal>();
                emitTasks.emplace_back(std::async(std::launch::async, [&records, &ranges, &locals, worker]()
                {
                    auto& local = *locals[worker];
                    local.trigrams.reserve(32768);
                    std::vector<std::uint64_t> uniqueKeys;
                    for (std::uint32_t recordId = ranges[worker].first; recordId < ranges[worker].second; ++recordId)
                    {
                        const auto& name = records[recordId].lowerName;
                        BuildUniqueTrigramKeys_NoLock(name, uniqueKeys);
                        for (const auto key : uniqueKeys)
                        {
                            local.trigrams[key].push_back(recordId);
                        }

                        if (name.length() >= 4)
                        {
                            local.prefix4[PackPrefix4(name)].push_back(recordId);
                        }

                        AddShortQueryTokensToLocal_NoLock(name, recordId, local.shortQuery);
                    }
                }));
            }

            for (auto& task : emitTasks)
            {
                task.get();
            }

            std::unordered_map<std::uint64_t, std::uint32_t> trigramCounts;
            trigramCounts.reserve(131072);
            std::unordered_map<std::uint64_t, std::uint32_t> prefix4Counts;
            prefix4Counts.reserve(65536);
            for (const auto& local : locals)
            {
                for (const auto& pair : local->trigrams)
                {
                    trigramCounts[pair.first] += static_cast<std::uint32_t>(pair.second.size());
                }

                for (const auto& pair : local->prefix4)
                {
                    prefix4Counts[pair.first] += static_cast<std::uint32_t>(pair.second.size());
                }
            }

            v2.trigramEntries.reserve(trigramCounts.size());
            v2.prefix4Entries.reserve(prefix4Counts.size());
            std::uint64_t totalPostings = 0;
            for (const auto& pair : trigramCounts)
            {
                totalPostings += pair.second;
                v2.trigramEntries.push_back(NativeV2BucketEntry{ pair.first, static_cast<std::uint32_t>(totalPostings - pair.second), pair.second, 0 });
            }

            std::sort(v2.trigramEntries.begin(), v2.trigramEntries.end(), [](const auto& left, const auto& right)
            {
                return left.key < right.key;
            });

            v2.trigramPostings.reserve(static_cast<std::size_t>(std::min<std::uint64_t>(
                totalPostings * 2,
                static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max()))));
            for (auto& entry : v2.trigramEntries)
            {
                const auto byteOffset = static_cast<std::uint32_t>(v2.trigramPostings.size());
                EncodeMergedLocalTrigramPosting_NoLock(entry.key, locals, v2.trigramPostings);
                entry.offset = byteOffset;
                entry.byteCount = static_cast<std::uint32_t>(v2.trigramPostings.size() - byteOffset);
            }
            v2.trigramPostings.shrink_to_fit();
            EncodePrefix4PostingsFromLocals_NoLock(prefix4Counts, locals, v2);

            MergeShortQueryLocalsIntoV2(locals, v2);
            CompressShortPostingsForSnapshotRecords(records, v2);
            BuildParentOrderForSnapshotRecords(records, volumes, v2);
            v2.buildMs = static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - totalStart).count());
            v2.postingsBytes =
                (static_cast<std::uint64_t>(v2.trigramEntries.size()) * sizeof(NativeV2BucketEntry))
                + static_cast<std::uint64_t>(v2.trigramPostings.size())
                + (static_cast<std::uint64_t>(v2.prefix4Entries.size()) * sizeof(NativeV2BucketEntry))
                + static_cast<std::uint64_t>(v2.prefix4Postings.size())
                + EstimateShortPostingBytesForV2(v2)
                + (static_cast<std::uint64_t>(v2.parentOrder.size()) * sizeof(std::uint32_t));
            v2.ready = v2.parentOrder.size() == records.size();
            return v2.ready;
        }

        static void MergeShortQueryLocalsIntoV2(
            const std::vector<std::unique_ptr<NativeV2BuildLocal>>& locals,
            NativeV2Accelerator& v2)
        {
            for (const auto& local : locals)
            {
                for (std::size_t i = 0; i < v2.asciiCharPostings.size(); ++i)
                {
                    auto& target = v2.asciiCharPostings[i];
                    const auto& source = local->shortQuery.chars[i];
                    target.insert(target.end(), source.begin(), source.end());
                }

                for (std::size_t i = 0; i < v2.asciiBigramPostings.size(); ++i)
                {
                    auto& target = v2.asciiBigramPostings[i];
                    const auto& source = local->shortQuery.bigrams[i];
                    target.insert(target.end(), source.begin(), source.end());
                }

                for (const auto& pair : local->shortQuery.nonAscii)
                {
                    auto& target = v2.nonAsciiShortPostings[pair.first];
                    target.insert(target.end(), pair.second.begin(), pair.second.end());
                }
            }
        }

        static int GetDriveIndexForSnapshotRecords(const std::vector<Record>& records, std::uint32_t recordId)
        {
            if (recordId >= records.size())
            {
                return -1;
            }

            const auto index = static_cast<int>(towupper(records[recordId].driveLetter) - L'A');
            return index >= 0 && index < 26 ? index : -1;
        }

        static void BuildShortDriveCountsForSnapshotRecords(
            const std::vector<Record>& records,
            NativeV2Accelerator& v2)
        {
            v2.asciiCharDriveCounts.assign(v2.asciiCharPostings.size(), {});
            v2.asciiBigramDriveCounts.assign(v2.asciiBigramPostings.size(), {});
            v2.asciiCharDrivePages.assign(v2.asciiCharPostings.size(), {});
            v2.asciiBigramDrivePages.assign(v2.asciiBigramPostings.size(), {});

            for (std::size_t token = 0; token < v2.asciiCharPostings.size(); ++token)
            {
                auto& counts = v2.asciiCharDriveCounts[token];
                auto& pages = v2.asciiCharDrivePages[token];
                for (const auto recordId : v2.asciiCharPostings[token])
                {
                    const auto driveIndex = GetDriveIndexForSnapshotRecords(records, recordId);
                    if (driveIndex >= 0)
                    {
                        const auto index = static_cast<std::size_t>(driveIndex);
                        ++counts[index];
                        if (pages[index].size() < ShortDrivePageSize)
                        {
                            pages[index].push_back(recordId);
                        }
                    }
                }
            }

            for (std::size_t token = 0; token < v2.asciiBigramPostings.size(); ++token)
            {
                auto& counts = v2.asciiBigramDriveCounts[token];
                auto& pages = v2.asciiBigramDrivePages[token];
                for (const auto recordId : v2.asciiBigramPostings[token])
                {
                    const auto driveIndex = GetDriveIndexForSnapshotRecords(records, recordId);
                    if (driveIndex >= 0)
                    {
                        const auto index = static_cast<std::size_t>(driveIndex);
                        ++counts[index];
                        if (pages[index].size() < ShortDrivePageSize)
                        {
                            pages[index].push_back(recordId);
                        }
                    }
                }
            }
        }

        static void CompressShortPostingsForSnapshotRecords(
            const std::vector<Record>& records,
            NativeV2Accelerator& v2)
        {
            v2.asciiCharEntries = {};
            v2.asciiBigramEntries = {};
            v2.nonAsciiShortEntries.clear();
            v2.asciiCharCompressedPostings.clear();
            v2.asciiBigramCompressedPostings.clear();
            v2.nonAsciiShortCompressedPostings.clear();

            BuildShortDriveCountsForSnapshotRecords(records, v2);
            for (std::size_t i = 0; i < v2.asciiCharPostings.size(); ++i)
            {
                EncodeShortPosting_NoLock(
                    v2.asciiCharPostings[i],
                    v2.asciiCharEntries[i],
                    v2.asciiCharCompressedPostings);
            }

            for (std::size_t i = 0; i < v2.asciiBigramPostings.size(); ++i)
            {
                EncodeShortPosting_NoLock(
                    v2.asciiBigramPostings[i],
                    v2.asciiBigramEntries[i],
                    v2.asciiBigramCompressedPostings);
            }

            for (auto& pair : v2.nonAsciiShortPostings)
            {
                NativeV2ShortBucketEntry entry;
                EncodeShortPosting_NoLock(pair.second, entry, v2.nonAsciiShortCompressedPostings);
                if (entry.count > 0)
                {
                    v2.nonAsciiShortEntries[pair.first] = entry;
                }
            }

            v2.nonAsciiShortPostings.clear();
            v2.asciiCharCompressedPostings.shrink_to_fit();
            v2.asciiBigramCompressedPostings.shrink_to_fit();
            v2.nonAsciiShortCompressedPostings.shrink_to_fit();
        }

        static bool TryGetChildRangeForSnapshotV2(
            const NativeV2Accelerator& v2,
            std::uint64_t key,
            std::uint32_t& offset,
            std::uint32_t& count)
        {
            auto lo = v2.childEntries.begin();
            auto hi = v2.childEntries.end();
            while (lo < hi)
            {
                auto mid = lo + ((hi - lo) / 2);
                if (mid->key < key)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            if (lo == v2.childEntries.end() || lo->key != key)
            {
                return false;
            }

            offset = lo->offset;
            count = lo->count;
            return true;
        }

        static void BuildParentOrderForSnapshotRecords(
            const std::vector<Record>& records,
            const std::unordered_map<wchar_t, VolumeState>& volumes,
            NativeV2Accelerator& v2)
        {
            v2.parentOrder.resize(records.size());
            v2.childEntries.clear();
            v2.childRecordIds.clear();
            v2.subtreeEnter.clear();
            v2.subtreeExit.clear();
            v2.subtreeRecordIds.clear();

            std::unordered_map<std::uint64_t, std::uint32_t> recordIdByKey;
            recordIdByKey.reserve(records.size());
            for (std::uint32_t i = 0; i < records.size(); ++i)
            {
                v2.parentOrder[i] = i;
                recordIdByKey[MakeRecordKey(records[i].driveLetter, records[i].frn)] = i;
            }

            std::sort(v2.parentOrder.begin(), v2.parentOrder.end(), [&records](std::uint32_t left, std::uint32_t right)
            {
                const auto& leftRecord = records[left];
                const auto& rightRecord = records[right];
                if (leftRecord.driveLetter != rightRecord.driveLetter)
                {
                    return leftRecord.driveLetter < rightRecord.driveLetter;
                }

                if (leftRecord.parentFrn != rightRecord.parentFrn)
                {
                    return leftRecord.parentFrn < rightRecord.parentFrn;
                }

                return left < right;
            });

            v2.childEntries.reserve(records.size() / 4);
            v2.childRecordIds.reserve(records.size());
            std::uint64_t currentKey = 0;
            std::uint32_t currentOffset = 0;
            std::uint32_t currentCount = 0;
            bool hasCurrent = false;
            for (const auto recordId : v2.parentOrder)
            {
                if (recordId >= records.size())
                {
                    continue;
                }

                const auto& record = records[recordId];
                const auto key = MakeRecordKey(record.driveLetter, record.parentFrn);
                if (!hasCurrent || key != currentKey)
                {
                    if (hasCurrent)
                    {
                        v2.childEntries.push_back(NativeV2ChildBucketEntry{ currentKey, currentOffset, currentCount });
                    }

                    hasCurrent = true;
                    currentKey = key;
                    currentOffset = static_cast<std::uint32_t>(v2.childRecordIds.size());
                    currentCount = 0;
                }

                v2.childRecordIds.push_back(recordId);
                ++currentCount;
            }

            if (hasCurrent)
            {
                v2.childEntries.push_back(NativeV2ChildBucketEntry{ currentKey, currentOffset, currentCount });
            }

            v2.childEntries.shrink_to_fit();
            v2.childRecordIds.shrink_to_fit();
            BuildSubtreeIntervalsForSnapshotRecords(records, volumes, recordIdByKey, v2);
        }

        static void BuildSubtreeIntervalsForSnapshotRecords(
            const std::vector<Record>& records,
            const std::unordered_map<wchar_t, VolumeState>& volumes,
            const std::unordered_map<std::uint64_t, std::uint32_t>& recordIdByKey,
            NativeV2Accelerator& v2)
        {
            const auto recordCount = records.size();
            v2.subtreeEnter.assign(recordCount, std::numeric_limits<std::uint32_t>::max());
            v2.subtreeExit.assign(recordCount, std::numeric_limits<std::uint32_t>::max());
            v2.subtreeRecordIds.clear();
            v2.subtreeRecordIds.reserve(recordCount);
            if (recordCount == 0 || v2.childRecordIds.empty())
            {
                return;
            }

            std::vector<std::uint32_t> roots;
            roots.reserve(64);
            for (const auto& pair : volumes)
            {
                const auto drive = static_cast<wchar_t>(towupper(pair.first));
                const auto it = recordIdByKey.find(MakeRecordKey(drive, NtfsRootFileReferenceNumber));
                if (it != recordIdByKey.end() && it->second < recordCount)
                {
                    roots.push_back(it->second);
                }
            }

            if (roots.empty())
            {
                for (std::uint32_t recordId = 0; recordId < recordCount; ++recordId)
                {
                    const auto parent = records[recordId].parentFrn;
                    if (parent == 0 || parent == NtfsRootFileReferenceNumber)
                    {
                        roots.push_back(recordId);
                    }
                }
            }

            std::sort(roots.begin(), roots.end());
            roots.erase(std::unique(roots.begin(), roots.end()), roots.end());

            std::uint32_t ordinal = 0;
            std::vector<std::pair<std::uint32_t, bool>> stack;
            stack.reserve(256);
            for (const auto rootId : roots)
            {
                if (rootId >= recordCount || v2.subtreeEnter[rootId] != std::numeric_limits<std::uint32_t>::max())
                {
                    continue;
                }

                stack.emplace_back(rootId, false);
                while (!stack.empty())
                {
                    const auto current = stack.back().first;
                    const auto exiting = stack.back().second;
                    stack.pop_back();
                    if (current >= recordCount)
                    {
                        continue;
                    }

                    if (exiting)
                    {
                        v2.subtreeExit[current] = ordinal;
                        continue;
                    }

                    if (v2.subtreeEnter[current] != std::numeric_limits<std::uint32_t>::max())
                    {
                        continue;
                    }

                    v2.subtreeEnter[current] = ordinal++;
                    v2.subtreeRecordIds.push_back(current);
                    stack.emplace_back(current, true);

                    std::uint32_t childOffset = 0;
                    std::uint32_t childCount = 0;
                    const auto childKey = MakeRecordKey(records[current].driveLetter, records[current].frn);
                    if (!TryGetChildRangeForSnapshotV2(v2, childKey, childOffset, childCount))
                    {
                        continue;
                    }

                    const auto childEnd = static_cast<std::size_t>(childOffset) + childCount;
                    for (std::size_t i = childEnd; i > childOffset && i <= v2.childRecordIds.size(); --i)
                    {
                        const auto childId = v2.childRecordIds[i - 1];
                        if (childId < recordCount && v2.subtreeEnter[childId] == std::numeric_limits<std::uint32_t>::max())
                        {
                            stack.emplace_back(childId, false);
                        }
                    }
                }
            }

            for (std::uint32_t recordId = 0; recordId < recordCount; ++recordId)
            {
                if (v2.subtreeEnter[recordId] != std::numeric_limits<std::uint32_t>::max())
                {
                    continue;
                }

                v2.subtreeEnter[recordId] = ordinal++;
                v2.subtreeRecordIds.push_back(recordId);
                v2.subtreeExit[recordId] = ordinal;
            }

            v2.subtreeRecordIds.shrink_to_fit();
        }

        static std::uint64_t EstimateShortPostingBytesForV2(const NativeV2Accelerator& v2)
        {
            std::uint64_t bytes = 0;
            bytes += static_cast<std::uint64_t>(v2.asciiCharCompressedPostings.capacity());
            bytes += static_cast<std::uint64_t>(v2.asciiBigramCompressedPostings.capacity());
            bytes += static_cast<std::uint64_t>(v2.nonAsciiShortCompressedPostings.capacity());
            bytes += static_cast<std::uint64_t>(v2.asciiCharEntries.size()) * sizeof(NativeV2ShortBucketEntry);
            bytes += static_cast<std::uint64_t>(v2.asciiBigramEntries.size()) * sizeof(NativeV2ShortBucketEntry);
            bytes += static_cast<std::uint64_t>(v2.nonAsciiShortEntries.size()) * (sizeof(std::uint64_t) + sizeof(NativeV2ShortBucketEntry));
            return bytes;
        }

        void BuildV2RuntimeStructures_NoLock()
        {
            const auto workerCount = GetV2WorkerCount(records_.size());
            auto ranges = BuildV2WorkerRanges(records_.size(), workerCount);
            if (workerCount == 1)
            {
                for (std::uint32_t recordId = 0; recordId < records_.size(); ++recordId)
                {
                    AddShortQueryTokens_NoLock(records_[recordId].lowerName, recordId);
                }
            }
            else
            {
                std::vector<std::unique_ptr<NativeV2ShortQueryLocal>> locals(workerCount);
                std::vector<std::future<void>> tasks;
                tasks.reserve(workerCount);
                for (std::size_t worker = 0; worker < workerCount; ++worker)
                {
                    locals[worker] = std::make_unique<NativeV2ShortQueryLocal>();
                    tasks.emplace_back(std::async(std::launch::async, [this, &ranges, &locals, worker]()
                    {
                        for (std::uint32_t recordId = ranges[worker].first; recordId < ranges[worker].second; ++recordId)
                        {
                            AddShortQueryTokensToLocal_NoLock(records_[recordId].lowerName, recordId, *locals[worker]);
                        }
                    }));
                }

                for (auto& task : tasks)
                {
                    task.get();
                }

                for (const auto& local : locals)
                {
                    for (std::size_t i = 0; i < v2_.asciiCharPostings.size(); ++i)
                    {
                        auto& target = v2_.asciiCharPostings[i];
                        target.insert(target.end(), local->chars[i].begin(), local->chars[i].end());
                    }

                    for (std::size_t i = 0; i < v2_.asciiBigramPostings.size(); ++i)
                    {
                        auto& target = v2_.asciiBigramPostings[i];
                        target.insert(target.end(), local->bigrams[i].begin(), local->bigrams[i].end());
                    }

                    for (const auto& pair : local->nonAscii)
                    {
                        auto& target = v2_.nonAsciiShortPostings[pair.first];
                        target.insert(target.end(), pair.second.begin(), pair.second.end());
                    }
                }
            }

            CompressShortPostings_NoLock();

            BuildParentOrder_NoLock();
            v2_.postingsBytes =
                (static_cast<std::uint64_t>(v2_.trigramEntries.size()) * sizeof(NativeV2BucketEntry))
                + static_cast<std::uint64_t>(v2_.trigramPostings.size())
                + (static_cast<std::uint64_t>(v2_.prefix4Entries.size()) * sizeof(NativeV2BucketEntry))
                + static_cast<std::uint64_t>(v2_.prefix4Postings.size())
                + EstimateShortPostingBytes_NoLock()
                + (static_cast<std::uint64_t>(v2_.parentOrder.size()) * sizeof(std::uint32_t));
            v2_.ready = !v2_.trigramEntries.empty() || records_.empty();
        }

        static std::size_t GetV2WorkerCount(std::size_t recordCount)
        {
            if (recordCount < 32768)
            {
                return 1;
            }

            const auto hardware = std::thread::hardware_concurrency();
            const auto count = hardware == 0 ? 4 : hardware;
            return static_cast<std::size_t>(std::max(1u, std::min(18u, count)));
        }

        static std::vector<std::pair<std::uint32_t, std::uint32_t>> BuildV2WorkerRanges(std::size_t recordCount, std::size_t workerCount)
        {
            std::vector<std::pair<std::uint32_t, std::uint32_t>> ranges;
            ranges.reserve(workerCount);
            for (std::size_t i = 0; i < workerCount; ++i)
            {
                const auto start = static_cast<std::uint32_t>((recordCount * i) / workerCount);
                const auto end = static_cast<std::uint32_t>((recordCount * (i + 1)) / workerCount);
                ranges.emplace_back(start, end);
            }

            return ranges;
        }

        static void BuildUniqueTrigramKeys_NoLock(const std::wstring& name, std::vector<std::uint64_t>& uniqueKeys)
        {
            uniqueKeys.clear();
            if (name.length() < 3)
            {
                return;
            }

            uniqueKeys.reserve(name.length() - 2);
            for (std::size_t i = 0; i + 2 < name.length(); ++i)
            {
                uniqueKeys.push_back(PackTrigram(name[i], name[i + 1], name[i + 2]));
            }

            std::sort(uniqueKeys.begin(), uniqueKeys.end());
            uniqueKeys.erase(std::unique(uniqueKeys.begin(), uniqueKeys.end()), uniqueKeys.end());
        }

        void AddShortQueryTokens_NoLock(const std::wstring& name, std::uint32_t recordId)
        {
            for (std::size_t i = 0; i < name.length(); ++i)
            {
                const auto ch = name[i];
                if (ch >= 0 && ch < 128)
                {
                    v2_.asciiCharPostings[static_cast<std::size_t>(ch)].push_back(recordId);
                }
                else
                {
                    v2_.nonAsciiShortPostings[PackShortToken(ch, 0, 1)].push_back(recordId);
                }

                if (i + 1 >= name.length())
                {
                    continue;
                }

                const auto next = name[i + 1];
                if (ch >= 0 && ch < 128 && next >= 0 && next < 128)
                {
                    const auto index = (static_cast<std::size_t>(ch) << 7) | static_cast<std::size_t>(next);
                    v2_.asciiBigramPostings[index].push_back(recordId);
                }
                else
                {
                    v2_.nonAsciiShortPostings[PackShortToken(ch, next, 2)].push_back(recordId);
                }
            }
        }

        static void AddShortQueryTokensToLocal_NoLock(
            const std::wstring& name,
            std::uint32_t recordId,
            NativeV2ShortQueryLocal& local)
        {
            for (std::size_t i = 0; i < name.length(); ++i)
            {
                const auto ch = name[i];
                if (ch >= 0 && ch < 128)
                {
                    local.chars[static_cast<std::size_t>(ch)].push_back(recordId);
                }
                else
                {
                    local.nonAscii[PackShortToken(ch, 0, 1)].push_back(recordId);
                }

                if (i + 1 >= name.length())
                {
                    continue;
                }

                const auto next = name[i + 1];
                if (ch >= 0 && ch < 128 && next >= 0 && next < 128)
                {
                    const auto index = (static_cast<std::size_t>(ch) << 7) | static_cast<std::size_t>(next);
                    local.bigrams[index].push_back(recordId);
                }
                else
                {
                    local.nonAscii[PackShortToken(ch, next, 2)].push_back(recordId);
                }
            }
        }

        static void AddOverlaySearchTokens_NoLock(
            const std::wstring& name,
            std::uint32_t overlayIndex,
            OverlaySearchIndex& index,
            std::vector<std::uint64_t>* uniqueKeysScratch = nullptr)
        {
            for (std::size_t i = 0; i < name.length(); ++i)
            {
                const auto ch = name[i];
                if (ch >= 0 && ch < 128)
                {
                    index.asciiCharPostings[static_cast<std::size_t>(ch)].push_back(overlayIndex);
                }
                else
                {
                    index.nonAsciiShortPostings[PackShortToken(ch, 0, 1)].push_back(overlayIndex);
                }

                if (i + 1 < name.length())
                {
                    const auto next = name[i + 1];
                    if (ch >= 0 && ch < 128 && next >= 0 && next < 128)
                    {
                        const auto bigramIndex = (static_cast<std::size_t>(ch) << 7) | static_cast<std::size_t>(next);
                        index.asciiBigramPostings[bigramIndex].push_back(overlayIndex);
                    }
                    else
                    {
                        index.nonAsciiShortPostings[PackShortToken(ch, next, 2)].push_back(overlayIndex);
                    }
                }
            }

            if (uniqueKeysScratch != nullptr)
            {
                BuildUniqueTrigramKeys_NoLock(name, *uniqueKeysScratch);
                for (const auto key : *uniqueKeysScratch)
                {
                    index.trigramPostings[key].push_back(overlayIndex);
                }
            }
            else
            {
                std::vector<std::uint64_t> uniqueKeys;
                BuildUniqueTrigramKeys_NoLock(name, uniqueKeys);
                for (const auto key : uniqueKeys)
                {
                    index.trigramPostings[key].push_back(overlayIndex);
                }
            }

            const auto extension = GetExtension(name);
            if (!extension.empty())
            {
                index.extensionPostings[extension].push_back(overlayIndex);
            }
        }

        void AddOverlayPathScopePostings_NoLock(
            const Record& record,
            std::uint32_t overlayIndex,
            OverlaySearchIndex& index,
            std::unordered_map<std::uint64_t, std::vector<std::uint64_t>>* ancestorCache) const
        {
            if (ancestorCache == nullptr)
            {
                AddOverlayPathScopePostings_NoLock(record, overlayIndex, index);
                return;
            }

            const auto parentKey = MakeRecordKey(record.driveLetter, record.parentFrn);
            const auto cached = ancestorCache->find(parentKey);
            if (cached != ancestorCache->end())
            {
                for (const auto key : cached->second)
                {
                    index.parentFrnPostings[key].push_back(overlayIndex);
                }
                return;
            }

            std::vector<std::uint64_t> ancestorKeys;
            ancestorKeys.reserve(8);
            auto parentFrn = record.parentFrn;
            std::array<std::uint64_t, 256> visited{};
            std::size_t visitedCount = 0;

            for (int depth = 0; depth < 256; ++depth)
            {
                const auto key = MakeRecordKey(record.driveLetter, parentFrn);
                if (std::find(visited.begin(), visited.begin() + visitedCount, key) != visited.begin() + visitedCount)
                {
                    break;
                }

                visited[visitedCount++] = key;
                ancestorKeys.push_back(key);
                index.parentFrnPostings[key].push_back(overlayIndex);

                if (parentFrn == 0)
                {
                    break;
                }

                std::uint32_t parentId = 0;
                if (!TryGetRecordIdByKey_NoLock(key, parentId))
                {
                    break;
                }

                const auto nextParentFrn = GetRecordParentFrn_NoLock(parentId);
                if (nextParentFrn == parentFrn)
                {
                    break;
                }

                parentFrn = nextParentFrn;
            }

            if (ancestorCache->size() < OverlayPathScopeAncestorCacheLimit)
            {
                ancestorCache->emplace(parentKey, std::move(ancestorKeys));
            }
        }

        void AddOverlayPathScopePostings_NoLock(
            const Record& record,
            std::uint32_t overlayIndex,
            OverlaySearchIndex& index) const
        {
            auto parentFrn = record.parentFrn;
            std::array<std::uint64_t, 256> visited{};
            std::size_t visitedCount = 0;

            for (int depth = 0; depth < 256; ++depth)
            {
                const auto key = MakeRecordKey(record.driveLetter, parentFrn);
                if (std::find(visited.begin(), visited.begin() + visitedCount, key) != visited.begin() + visitedCount)
                {
                    break;
                }

                visited[visitedCount++] = key;
                index.parentFrnPostings[key].push_back(overlayIndex);

                if (parentFrn == 0)
                {
                    break;
                }

                std::uint32_t parentId = 0;
                if (!TryGetRecordIdByKey_NoLock(key, parentId))
                {
                    break;
                }

                const auto nextParentFrn = GetRecordParentFrn_NoLock(parentId);
                if (nextParentFrn == parentFrn)
                {
                    break;
                }

                parentFrn = nextParentFrn;
            }
        }

        void BuildOverlaySearchIndexInto_NoLock(OverlaySearchIndex& target) const
        {
            target.ResetKeepCapacity();
            if (overlayRecords_.empty())
            {
                return;
            }

            target.trigramPostings.reserve(overlayRecords_.size() * 4);
            target.extensionPostings.reserve(std::max<std::size_t>(16, overlayRecords_.size() / 8));
            target.parentFrnPostings.reserve(std::max<std::size_t>(16, overlayRecords_.size() / 4));
            std::vector<std::uint64_t> uniqueKeysScratch;
            uniqueKeysScratch.reserve(64);
            std::unordered_map<std::uint64_t, std::vector<std::uint64_t>> pathScopeAncestorCache;
            pathScopeAncestorCache.reserve(std::min<std::size_t>(OverlayPathScopeAncestorCacheLimit, overlayRecords_.size()));
            for (std::uint32_t i = 0; i < overlayRecords_.size(); ++i)
            {
                const auto& record = overlayRecords_[i];
                const auto key = MakeRecordKey(record.driveLetter, record.frn);
                const auto liveOverlay = overlayIndexByKey_.find(key);
                if (liveOverlay == overlayIndexByKey_.end()
                    || liveOverlay->second != i
                    || overlayDeletedKeys_.find(key) != overlayDeletedKeys_.end())
                {
                    continue;
                }

                AddOverlaySearchTokens_NoLock(record.lowerName, i, target, &uniqueKeysScratch);
                AddOverlayPathScopePostings_NoLock(record, i, target, &pathScopeAncestorCache);
            }

            target.ready = true;
        }

        void RebuildOverlaySearchIndex_NoLock()
        {
            BuildOverlaySearchIndexInto_NoLock(overlaySearch_);
            overlaySearch_.ready = true;
            overlayStalePostingEstimate_ = 0;
            lastOverlayRebuildAttempt_ = std::chrono::steady_clock::now();
        }

        void MarkOverlaySearchNeedsRebuild_NoLock()
        {
            overlaySearch_.ready = false;
            overlayStalePostingEstimate_ = overlayRecords_.size();
            lastOverlayRebuildAttempt_ = std::chrono::steady_clock::now();
        }

        bool RebuildOverlaySearchIndexOutsideExclusiveLock(std::uint64_t expectedGeneration)
        {
            const auto totalStart = std::chrono::steady_clock::now();
            auto rebuilt = std::make_unique<OverlaySearchIndex>();
            std::size_t overlayRecordCount = 0;
            std::size_t overlayDeleteCount = 0;
            long long buildMs = 0;
            {
                std::shared_lock<std::shared_mutex> guard(mutex_);
                if (hasShutdown_ || generationId_.load(std::memory_order_acquire) != expectedGeneration)
                {
                    return false;
                }

                overlayRecordCount = overlayRecords_.size();
                overlayDeleteCount = overlayDeletedKeys_.size();
                const auto buildStart = std::chrono::steady_clock::now();
                BuildOverlaySearchIndexInto_NoLock(*rebuilt);
                buildMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - buildStart).count();
            }

            const auto swapStart = std::chrono::steady_clock::now();
            std::unique_lock<std::shared_mutex> guard(mutex_);
            if (hasShutdown_ || generationId_.load(std::memory_order_acquire) != expectedGeneration)
            {
                return false;
            }

            overlaySearch_ = std::move(*rebuilt);
            overlaySearch_.ready = true;
            overlayStalePostingEstimate_ = 0;
            lastOverlayRebuildAttempt_ = std::chrono::steady_clock::now();
            const auto swapMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - swapStart).count();
            NativeLogWrite(
                "[NATIVE OVERLAY SEARCH REBUILD] success totalMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - totalStart).count())
                + " buildMs=" + std::to_string(buildMs)
                + " swapMs=" + std::to_string(swapMs)
                + " overlayRecords=" + std::to_string(overlayRecordCount)
                + " overlayDeletes=" + std::to_string(overlayDeleteCount)
                + " generation=" + std::to_string(expectedGeneration));
            return true;
        }

        void AddOverlaySearchEntry_NoLock(std::uint32_t overlayIndex)
        {
            if (overlayIndex >= overlayRecords_.size())
            {
                return;
            }

            if (!overlaySearch_.ready)
            {
                RebuildOverlaySearchIndex_NoLock();
                return;
            }

            AddOverlaySearchTokens_NoLock(overlayRecords_[overlayIndex].lowerName, overlayIndex, overlaySearch_);
            AddOverlayPathScopePostings_NoLock(overlayRecords_[overlayIndex], overlayIndex, overlaySearch_);
        }

        void MarkOverlaySearchStale_NoLock(std::size_t staleCount = 1)
        {
            overlayStalePostingEstimate_ += staleCount;
        }

        bool ShouldRebuildOverlaySearch_NoLock() const
        {
            if (!overlaySearch_.ready)
            {
                return true;
            }

            if (overlayStalePostingEstimate_ >= OverlayStaleRebuildThreshold)
            {
                return true;
            }

            return overlayRecords_.size() > 0
                && overlayStalePostingEstimate_ > overlayRecords_.size() / OverlayStaleRebuildRatioDivisor;
        }

        static void EncodeDeltaPosting_NoLock(
            const std::vector<std::uint32_t>& rawPostings,
            std::uint32_t offset,
            std::uint32_t count,
            std::vector<std::uint8_t>& output)
        {
            std::uint32_t previous = 0;
            for (std::uint32_t i = 0; i < count; ++i)
            {
                const auto value = rawPostings[static_cast<std::size_t>(offset) + i];
                const auto delta = i == 0 ? value : value - previous;
                WriteVarUInt(delta, output);
                previous = value;
            }
        }

        static void EncodeMergedLocalTrigramPosting_NoLock(
            std::uint64_t key,
            const std::vector<std::unique_ptr<NativeV2BuildLocal>>& locals,
            std::vector<std::uint8_t>& output)
        {
            bool hasPrevious = false;
            std::uint32_t previous = 0;
            for (const auto& local : locals)
            {
                const auto it = local->trigrams.find(key);
                if (it == local->trigrams.end())
                {
                    continue;
                }

                for (const auto value : it->second)
                {
                    WriteVarUInt(hasPrevious ? value - previous : value, output);
                    previous = value;
                    hasPrevious = true;
                }
            }
        }

        static void EncodeMergedLocalPrefix4Posting_NoLock(
            std::uint64_t key,
            const std::vector<std::unique_ptr<NativeV2BuildLocal>>& locals,
            std::vector<std::uint8_t>& output)
        {
            bool hasPrevious = false;
            std::uint32_t previous = 0;
            for (const auto& local : locals)
            {
                const auto it = local->prefix4.find(key);
                if (it == local->prefix4.end())
                {
                    continue;
                }

                for (const auto value : it->second)
                {
                    WriteVarUInt(hasPrevious ? value - previous : value, output);
                    previous = value;
                    hasPrevious = true;
                }
            }
        }

        static void EncodePrefix4PostingsFromLocals_NoLock(
            const std::unordered_map<std::uint64_t, std::uint32_t>& prefix4Counts,
            const std::vector<std::unique_ptr<NativeV2BuildLocal>>& locals,
            NativeV2Accelerator& v2)
        {
            v2.prefix4Entries.clear();
            v2.prefix4Postings.clear();
            v2.prefix4Entries.reserve(prefix4Counts.size());
            std::uint64_t totalPostings = 0;
            for (const auto& pair : prefix4Counts)
            {
                totalPostings += pair.second;
                v2.prefix4Entries.push_back(NativeV2BucketEntry{ pair.first, 0, pair.second, 0 });
            }

            std::sort(v2.prefix4Entries.begin(), v2.prefix4Entries.end(), [](const auto& left, const auto& right)
            {
                return left.key < right.key;
            });

            v2.prefix4Postings.reserve(static_cast<std::size_t>(std::min<std::uint64_t>(
                totalPostings * 2,
                static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max()))));
            for (auto& entry : v2.prefix4Entries)
            {
                const auto byteOffset = static_cast<std::uint32_t>(v2.prefix4Postings.size());
                EncodeMergedLocalPrefix4Posting_NoLock(entry.key, locals, v2.prefix4Postings);
                entry.offset = byteOffset;
                entry.byteCount = static_cast<std::uint32_t>(v2.prefix4Postings.size() - byteOffset);
            }

            v2.prefix4Postings.shrink_to_fit();
        }

        static void DecodeDeltaPosting_NoLock(
            const std::vector<std::uint8_t>& bytes,
            const NativeV2BucketEntry& entry,
            std::vector<std::uint32_t>& output)
        {
            output.clear();
            output.reserve(entry.count);
            std::size_t offset = entry.offset;
            const std::size_t end = static_cast<std::size_t>(entry.offset) + entry.byteCount;
            std::uint32_t previous = 0;
            for (std::uint32_t i = 0; i < entry.count && offset < end; ++i)
            {
                std::uint32_t delta = 0;
                if (!ReadVarUInt(bytes, offset, end, delta))
                {
                    output.clear();
                    return;
                }

                const auto value = i == 0 ? delta : previous + delta;
                output.push_back(value);
                previous = value;
            }
        }

        static void EncodeShortPosting_NoLock(
            std::vector<std::uint32_t>& posting,
            NativeV2ShortBucketEntry& entry,
            std::vector<std::uint8_t>& output)
        {
            // Full-build postings are appended in ascending recordId order; merging worker
            // ranges preserves that order, so only duplicate ids from repeated tokens need removal.
            posting.erase(std::unique(posting.begin(), posting.end()), posting.end());
            entry.offset = static_cast<std::uint32_t>(output.size());
            entry.count = static_cast<std::uint32_t>(posting.size());
            std::uint32_t previous = 0;
            for (std::uint32_t i = 0; i < entry.count; ++i)
            {
                const auto value = posting[i];
                WriteVarUInt(i == 0 ? value : value - previous, output);
                previous = value;
            }

            entry.byteCount = static_cast<std::uint32_t>(output.size() - entry.offset);
            posting.clear();
            posting.shrink_to_fit();
        }

        void MergeShortQueryLocals_NoLock(const std::vector<std::unique_ptr<NativeV2BuildLocal>>& locals)
        {
            for (const auto& local : locals)
            {
                for (std::size_t i = 0; i < v2_.asciiCharPostings.size(); ++i)
                {
                    auto& target = v2_.asciiCharPostings[i];
                    const auto& source = local->shortQuery.chars[i];
                    target.insert(target.end(), source.begin(), source.end());
                }

                for (std::size_t i = 0; i < v2_.asciiBigramPostings.size(); ++i)
                {
                    auto& target = v2_.asciiBigramPostings[i];
                    const auto& source = local->shortQuery.bigrams[i];
                    target.insert(target.end(), source.begin(), source.end());
                }

                for (const auto& pair : local->shortQuery.nonAscii)
                {
                    auto& target = v2_.nonAsciiShortPostings[pair.first];
                    target.insert(target.end(), pair.second.begin(), pair.second.end());
                }
            }
        }

        static void DecodeShortPosting_NoLock(
            const std::vector<std::uint8_t>& bytes,
            const NativeV2ShortBucketEntry& entry,
            std::vector<std::uint32_t>& output)
        {
            output.clear();
            output.reserve(entry.count);
            std::size_t offset = entry.offset;
            const std::size_t end = static_cast<std::size_t>(entry.offset) + entry.byteCount;
            std::uint32_t previous = 0;
            for (std::uint32_t i = 0; i < entry.count && offset < end; ++i)
            {
                std::uint32_t delta = 0;
                if (!ReadVarUInt(bytes, offset, end, delta))
                {
                    output.clear();
                    return;
                }

                const auto value = i == 0 ? delta : previous + delta;
                output.push_back(value);
                previous = value;
            }
        }

        static void DecodeShortPostingPage_NoLock(
            const std::vector<std::uint8_t>& bytes,
            const NativeV2ShortBucketEntry& entry,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page)
        {
            page.clear();
            if (entry.count == 0 || maxResults <= 0)
            {
                return;
            }

            page.reserve(static_cast<std::size_t>(std::min(maxResults, 64)));
            std::size_t byteOffset = entry.offset;
            const std::size_t end = static_cast<std::size_t>(entry.offset) + entry.byteCount;
            std::uint32_t previous = 0;
            const auto safeOffset = static_cast<std::uint32_t>(std::max(offset, 0));
            for (std::uint32_t i = 0; i < entry.count && byteOffset < end; ++i)
            {
                std::uint32_t delta = 0;
                if (!ReadVarUInt(bytes, byteOffset, end, delta))
                {
                    page.clear();
                    return;
                }

                const auto value = i == 0 ? delta : previous + delta;
                previous = value;
                if (i < safeOffset)
                {
                    continue;
                }

                page.push_back(value);
                if (static_cast<int>(page.size()) >= maxResults)
                {
                    break;
                }
            }
        }

        void CompressShortPostings_NoLock()
        {
            v2_.asciiCharEntries = {};
            v2_.asciiBigramEntries = {};
            v2_.nonAsciiShortEntries.clear();
            v2_.asciiCharCompressedPostings.clear();
            v2_.asciiBigramCompressedPostings.clear();
            v2_.nonAsciiShortCompressedPostings.clear();

            BuildShortDriveCounts_NoLock();
            for (std::size_t i = 0; i < v2_.asciiCharPostings.size(); ++i)
            {
                EncodeShortPosting_NoLock(
                    v2_.asciiCharPostings[i],
                    v2_.asciiCharEntries[i],
                    v2_.asciiCharCompressedPostings);
            }

            for (std::size_t i = 0; i < v2_.asciiBigramPostings.size(); ++i)
            {
                EncodeShortPosting_NoLock(
                    v2_.asciiBigramPostings[i],
                    v2_.asciiBigramEntries[i],
                    v2_.asciiBigramCompressedPostings);
            }

            for (auto& pair : v2_.nonAsciiShortPostings)
            {
                NativeV2ShortBucketEntry entry;
                EncodeShortPosting_NoLock(pair.second, entry, v2_.nonAsciiShortCompressedPostings);
                if (entry.count > 0)
                {
                    v2_.nonAsciiShortEntries[pair.first] = entry;
                }
            }

            v2_.nonAsciiShortPostings.clear();
            v2_.asciiCharCompressedPostings.shrink_to_fit();
            v2_.asciiBigramCompressedPostings.shrink_to_fit();
            v2_.nonAsciiShortCompressedPostings.shrink_to_fit();
        }

        void BuildShortDriveCounts_NoLock()
        {
            v2_.asciiCharDriveCounts.assign(v2_.asciiCharPostings.size(), {});
            v2_.asciiBigramDriveCounts.assign(v2_.asciiBigramPostings.size(), {});
            v2_.asciiCharDrivePages.assign(v2_.asciiCharPostings.size(), {});
            v2_.asciiBigramDrivePages.assign(v2_.asciiBigramPostings.size(), {});

            for (std::size_t token = 0; token < v2_.asciiCharPostings.size(); ++token)
            {
                auto& counts = v2_.asciiCharDriveCounts[token];
                auto& pages = v2_.asciiCharDrivePages[token];
                for (const auto recordId : v2_.asciiCharPostings[token])
                {
                    const auto driveIndex = GetDriveIndex_NoLock(recordId);
                    if (driveIndex >= 0)
                    {
                        const auto index = static_cast<std::size_t>(driveIndex);
                        ++counts[index];
                        if (pages[index].size() < ShortDrivePageSize)
                        {
                            pages[index].push_back(recordId);
                        }
                    }
                }
            }

            for (std::size_t token = 0; token < v2_.asciiBigramPostings.size(); ++token)
            {
                auto& counts = v2_.asciiBigramDriveCounts[token];
                auto& pages = v2_.asciiBigramDrivePages[token];
                for (const auto recordId : v2_.asciiBigramPostings[token])
                {
                    const auto driveIndex = GetDriveIndex_NoLock(recordId);
                    if (driveIndex >= 0)
                    {
                        const auto index = static_cast<std::size_t>(driveIndex);
                        ++counts[index];
                        if (pages[index].size() < ShortDrivePageSize)
                        {
                            pages[index].push_back(recordId);
                        }
                    }
                }
            }
        }

        void BuildShortDrivePagesFromRuntime_NoLock()
        {
            v2_.asciiCharDrivePages.assign(v2_.asciiCharEntries.size(), {});
            v2_.asciiBigramDrivePages.assign(v2_.asciiBigramEntries.size(), {});
            for (std::size_t token = 0; token < v2_.asciiCharEntries.size(); ++token)
            {
                BuildShortDrivePagesForEntry_NoLock(
                    v2_.asciiCharCompressedPostings,
                    v2_.asciiCharEntries[token],
                    v2_.asciiCharDrivePages[token]);
            }

            for (std::size_t token = 0; token < v2_.asciiBigramEntries.size(); ++token)
            {
                BuildShortDrivePagesForEntry_NoLock(
                    v2_.asciiBigramCompressedPostings,
                    v2_.asciiBigramEntries[token],
                    v2_.asciiBigramDrivePages[token]);
            }
        }

        void BuildShortDriveCountsFromRuntime_NoLock()
        {
            v2_.asciiCharDriveCounts.assign(v2_.asciiCharEntries.size(), {});
            v2_.asciiBigramDriveCounts.assign(v2_.asciiBigramEntries.size(), {});
            for (std::size_t token = 0; token < v2_.asciiCharEntries.size(); ++token)
            {
                BuildShortDriveCountsForEntry_NoLock(
                    v2_.asciiCharCompressedPostings,
                    v2_.asciiCharEntries[token],
                    v2_.asciiCharDriveCounts[token]);
            }

            for (std::size_t token = 0; token < v2_.asciiBigramEntries.size(); ++token)
            {
                BuildShortDriveCountsForEntry_NoLock(
                    v2_.asciiBigramCompressedPostings,
                    v2_.asciiBigramEntries[token],
                    v2_.asciiBigramDriveCounts[token]);
            }
        }

        void BuildShortDriveCountsForEntry_NoLock(
            const std::vector<std::uint8_t>& bytes,
            const NativeV2ShortBucketEntry& entry,
            std::array<std::uint32_t, 26>& counts) const
        {
            counts = {};
            if (entry.count == 0)
            {
                return;
            }

            std::size_t byteOffset = entry.offset;
            const std::size_t end = static_cast<std::size_t>(entry.offset) + entry.byteCount;
            std::uint32_t previous = 0;
            for (std::uint32_t i = 0; i < entry.count && byteOffset < end; ++i)
            {
                std::uint32_t delta = 0;
                if (!ReadVarUInt(bytes, byteOffset, end, delta))
                {
                    counts = {};
                    return;
                }

                const auto recordId = i == 0 ? delta : previous + delta;
                previous = recordId;
                const auto driveIndex = GetDriveIndex_NoLock(recordId);
                if (driveIndex >= 0 && !IsRecordSuppressedByOverlay_NoLock(recordId))
                {
                    ++counts[static_cast<std::size_t>(driveIndex)];
                }
            }
        }

        void BuildShortDrivePagesForEntry_NoLock(
            const std::vector<std::uint8_t>& bytes,
            const NativeV2ShortBucketEntry& entry,
            std::array<std::vector<std::uint32_t>, 26>& pages) const
        {
            for (auto& page : pages)
            {
                page.clear();
                page.reserve(std::min<std::size_t>(ShortDrivePageSize, 256));
            }

            if (entry.count == 0)
            {
                return;
            }

            std::size_t byteOffset = entry.offset;
            const std::size_t end = static_cast<std::size_t>(entry.offset) + entry.byteCount;
            std::uint32_t previous = 0;
            std::size_t completedDrives = 0;
            std::array<bool, 26> completed{};
            for (std::uint32_t i = 0; i < entry.count && byteOffset < end && completedDrives < 26; ++i)
            {
                std::uint32_t delta = 0;
                if (!ReadVarUInt(bytes, byteOffset, end, delta))
                {
                    return;
                }

                const auto recordId = i == 0 ? delta : previous + delta;
                previous = recordId;
                const auto driveIndex = GetDriveIndex_NoLock(recordId);
                if (driveIndex < 0)
                {
                    continue;
                }

                const auto index = static_cast<std::size_t>(driveIndex);
                if (completed[index] || IsRecordSuppressedByOverlay_NoLock(recordId))
                {
                    continue;
                }

                auto& page = pages[index];
                page.push_back(recordId);
                if (page.size() >= ShortDrivePageSize)
                {
                    completed[index] = true;
                    ++completedDrives;
                }
            }
        }

        static void WriteVarUInt(std::uint32_t value, std::vector<std::uint8_t>& output)
        {
            while (value >= 0x80)
            {
                output.push_back(static_cast<std::uint8_t>(value | 0x80));
                value >>= 7;
            }

            output.push_back(static_cast<std::uint8_t>(value));
        }

        static bool ReadVarUInt(
            const std::vector<std::uint8_t>& input,
            std::size_t& offset,
            std::size_t end,
            std::uint32_t& value)
        {
            value = 0;
            int shift = 0;
            for (int i = 0; i < 5 && offset < end && offset < input.size(); ++i)
            {
                const auto current = input[offset++];
                value |= static_cast<std::uint32_t>(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    return true;
                }

                shift += 7;
            }

            return false;
        }

        void BuildParentOrder_NoLock()
        {
            v2_.parentOrder.resize(records_.size());
            v2_.childEntries.clear();
            v2_.childRecordIds.clear();
            v2_.subtreeEnter.clear();
            v2_.subtreeExit.clear();
            v2_.subtreeRecordIds.clear();
            for (std::uint32_t i = 0; i < records_.size(); ++i)
            {
                v2_.parentOrder[i] = i;
            }

            std::sort(v2_.parentOrder.begin(), v2_.parentOrder.end(), [this](std::uint32_t left, std::uint32_t right)
            {
                const auto& leftRecord = records_[left];
                const auto& rightRecord = records_[right];
                if (leftRecord.driveLetter != rightRecord.driveLetter)
                {
                    return leftRecord.driveLetter < rightRecord.driveLetter;
                }

                if (leftRecord.parentFrn != rightRecord.parentFrn)
                {
                    return leftRecord.parentFrn < rightRecord.parentFrn;
                }

                return left < right;
            });

            v2_.childEntries.reserve(records_.size() / 4);
            v2_.childRecordIds.reserve(records_.size());
            std::uint64_t currentKey = 0;
            std::uint32_t currentOffset = 0;
            std::uint32_t currentCount = 0;
            bool hasCurrent = false;
            for (const auto recordId : v2_.parentOrder)
            {
                if (recordId >= records_.size())
                {
                    continue;
                }

                const auto& record = records_[recordId];
                const auto key = MakeRecordKey(record.driveLetter, record.parentFrn);
                if (!hasCurrent || key != currentKey)
                {
                    if (hasCurrent)
                    {
                        v2_.childEntries.push_back(NativeV2ChildBucketEntry{ currentKey, currentOffset, currentCount });
                    }

                    hasCurrent = true;
                    currentKey = key;
                    currentOffset = static_cast<std::uint32_t>(v2_.childRecordIds.size());
                    currentCount = 0;
                }

                v2_.childRecordIds.push_back(recordId);
                ++currentCount;
            }

            if (hasCurrent)
            {
                v2_.childEntries.push_back(NativeV2ChildBucketEntry{ currentKey, currentOffset, currentCount });
            }

            v2_.childEntries.shrink_to_fit();
            v2_.childRecordIds.shrink_to_fit();
            BuildSubtreeIntervals_NoLock();
        }

        void BuildSubtreeIntervals_NoLock()
        {
            const auto recordCount = GetRecordCount_NoLock();
            v2_.subtreeEnter.assign(recordCount, std::numeric_limits<std::uint32_t>::max());
            v2_.subtreeExit.assign(recordCount, std::numeric_limits<std::uint32_t>::max());
            v2_.subtreeRecordIds.clear();
            v2_.subtreeRecordIds.reserve(recordCount);
            if (recordCount == 0 || v2_.childRecordIds.empty())
            {
                return;
            }

            std::vector<std::uint32_t> roots;
            roots.reserve(64);
            for (const auto& pair : volumes_)
            {
                std::uint32_t rootId = 0;
                const auto drive = static_cast<wchar_t>(towupper(pair.first));
                if (TryGetRecordIdByKey_NoLock(MakeRecordKey(drive, NtfsRootFileReferenceNumber), rootId)
                    && !IsOverlayRecordId(rootId)
                    && rootId < recordCount)
                {
                    roots.push_back(rootId);
                }
            }

            if (roots.empty())
            {
                for (std::uint32_t recordId = 0; recordId < recordCount; ++recordId)
                {
                    const auto parent = GetRecordParentFrn_NoLock(recordId);
                    if (parent == 0 || parent == NtfsRootFileReferenceNumber)
                    {
                        roots.push_back(recordId);
                    }
                }
            }

            std::sort(roots.begin(), roots.end());
            roots.erase(std::unique(roots.begin(), roots.end()), roots.end());

            std::uint32_t ordinal = 0;
            std::vector<std::pair<std::uint32_t, bool>> stack;
            stack.reserve(256);
            for (const auto rootId : roots)
            {
                if (rootId >= recordCount || v2_.subtreeEnter[rootId] != std::numeric_limits<std::uint32_t>::max())
                {
                    continue;
                }

                stack.emplace_back(rootId, false);
                while (!stack.empty())
                {
                    const auto current = stack.back().first;
                    const auto exiting = stack.back().second;
                    stack.pop_back();
                    if (current >= recordCount)
                    {
                        continue;
                    }

                    if (exiting)
                    {
                        v2_.subtreeExit[current] = ordinal;
                        continue;
                    }

                    if (v2_.subtreeEnter[current] != std::numeric_limits<std::uint32_t>::max())
                    {
                        continue;
                    }

                    v2_.subtreeEnter[current] = ordinal++;
                    v2_.subtreeRecordIds.push_back(current);
                    stack.emplace_back(current, true);

                    std::uint32_t childOffset = 0;
                    std::uint32_t childCount = 0;
                    if (!TryGetChildRange_NoLock(MakeRecordKey(GetRecordDrive_NoLock(current), GetRecordFrn_NoLock(current)), childOffset, childCount))
                    {
                        continue;
                    }

                    const auto childEnd = static_cast<std::size_t>(childOffset) + childCount;
                    for (std::size_t i = childEnd; i > childOffset && i <= v2_.childRecordIds.size(); --i)
                    {
                        const auto childId = v2_.childRecordIds[i - 1];
                        if (childId < recordCount && v2_.subtreeEnter[childId] == std::numeric_limits<std::uint32_t>::max())
                        {
                            stack.emplace_back(childId, false);
                        }
                    }
                }
            }

            for (std::uint32_t recordId = 0; recordId < recordCount; ++recordId)
            {
                if (v2_.subtreeEnter[recordId] != std::numeric_limits<std::uint32_t>::max())
                {
                    continue;
                }

                v2_.subtreeEnter[recordId] = ordinal++;
                v2_.subtreeRecordIds.push_back(recordId);
                v2_.subtreeExit[recordId] = ordinal;
            }

            v2_.subtreeRecordIds.shrink_to_fit();
        }

        std::uint64_t EstimateShortPostingBytes_NoLock() const
        {
            std::uint64_t bytes = 0;
            bytes += static_cast<std::uint64_t>(v2_.asciiCharCompressedPostings.capacity());
            bytes += static_cast<std::uint64_t>(v2_.asciiBigramCompressedPostings.capacity());
            bytes += static_cast<std::uint64_t>(v2_.nonAsciiShortCompressedPostings.capacity());
            bytes += static_cast<std::uint64_t>(v2_.asciiCharEntries.size()) * sizeof(NativeV2ShortBucketEntry);
            bytes += static_cast<std::uint64_t>(v2_.asciiBigramEntries.size()) * sizeof(NativeV2ShortBucketEntry);
            bytes += static_cast<std::uint64_t>(v2_.nonAsciiShortEntries.size()) * (sizeof(std::uint64_t) + sizeof(NativeV2ShortBucketEntry));
            return bytes;
        }

        void ExportStableRecords_NoLock(std::vector<Record>& output) const
        {
            output.clear();
            const auto baseCount = GetRecordCount_NoLock();
            output.reserve(baseCount + overlayIndexByKey_.size());
            for (std::uint32_t i = 0; i < baseCount; ++i)
            {
                if (IsRecordSuppressedByOverlay_NoLock(i))
                {
                    continue;
                }

                Record record;
                record.frn = GetRecordFrn_NoLock(i);
                record.parentFrn = GetRecordParentFrn_NoLock(i);
                record.driveLetter = GetRecordDrive_NoLock(i);
                record.isDirectory = IsDirectoryRecord_NoLock(i);
                record.originalName = std::wstring(GetOriginalNameView_NoLock(i));
                record.lowerName = std::wstring(GetLowerNameView_NoLock(i));
                output.push_back(std::move(record));
            }

            for (std::uint32_t i = 0; i < overlayRecords_.size(); ++i)
            {
                if (OverlayRecordIsLive_NoLock(i))
                {
                    output.push_back(overlayRecords_[i]);
                }
            }

            std::sort(output.begin(), output.end(), [](const Record& left, const Record& right)
            {
                if (left.lowerName != right.lowerName)
                {
                    return left.lowerName < right.lowerName;
                }

                if (left.driveLetter != right.driveLetter)
                {
                    return left.driveLetter < right.driveLetter;
                }

                return left.frn < right.frn;
            });
        }

        void SortFrnRecordIndex_NoLock()
        {
            std::sort(frnRecordIndex_.begin(), frnRecordIndex_.end(), [](const auto& left, const auto& right)
            {
                if (left.key != right.key)
                {
                    return left.key < right.key;
                }

                return left.recordId < right.recordId;
            });
            frnRecordIndex_.shrink_to_fit();
        }

        void RebuildFrnRecordIndex_NoLock()
        {
            recordIndexByKey_.clear();
            frnRecordIndex_.clear();
            frnRecordIndex_.reserve(records_.size());
            for (std::uint32_t i = 0; i < records_.size(); ++i)
            {
                const auto& record = records_[i];
                const auto key = MakeRecordKey(record.driveLetter, record.frn);
                frnRecordIndex_.push_back(FrnRecordIndexEntry{ key, i });
            }

            SortFrnRecordIndex_NoLock();
        }

        void ClearDriveIds_NoLock()
        {
            for (auto& ids : driveIds_)
            {
                ids.clear();
            }
        }

        void AddDriveId_NoLock(wchar_t driveLetter, std::uint32_t recordId)
        {
            const auto index = static_cast<int>(towupper(driveLetter) - L'A');
            if (index >= 0 && index < static_cast<int>(driveIds_.size()))
            {
                driveIds_[static_cast<std::size_t>(index)].push_back(recordId);
            }
        }

        int GetDriveIndex_NoLock(std::uint32_t recordId) const
        {
            if (recordId >= GetRecordCount_NoLock())
            {
                return -1;
            }

            const auto index = static_cast<int>(towupper(GetRecordDrive_NoLock(recordId)) - L'A');
            return index >= 0 && index < 26 ? index : -1;
        }

        const std::vector<std::uint32_t>* GetDriveIds_NoLock(wchar_t driveLetter) const
        {
            const auto index = static_cast<int>(towupper(driveLetter) - L'A');
            if (index < 0 || index >= static_cast<int>(driveIds_.size()))
            {
                return nullptr;
            }

            return &driveIds_[static_cast<std::size_t>(index)];
        }

        void RebuildDriveIdsFromAllIds_NoLock()
        {
            ClearDriveIds_NoLock();
            for (const auto recordId : allIds_)
            {
                if (recordId < GetRecordCount_NoLock())
                {
                    AddDriveId_NoLock(GetRecordDrive_NoLock(recordId), recordId);
                }
            }
        }

        void RebuildFrnRecordIndexFromMapped_NoLock()
        {
            recordIndexByKey_.clear();
            frnRecordIndex_.clear();
            overlayRecords_.clear();
            overlayIndexByKey_.clear();
            overlayDeletedKeys_.clear();
            overlaySuppressedRecordIds_.clear();
            overlaySearch_.Clear();
            overlaySegmentBaseRecordCountOverride_ = 0;
            overlayCompactedKeys_.clear();
            overlayCompactedDeletedKeys_.clear();
            if (!mappedRecords_.Ready())
            {
                return;
            }

            frnRecordIndex_.reserve(mappedRecords_.recordCount);
            for (std::uint32_t i = 0; i < mappedRecords_.recordCount; ++i)
            {
                const auto& record = mappedRecords_.records[i];
                const auto key = MakeRecordKey(static_cast<wchar_t>(record.drive), record.frn);
                frnRecordIndex_.push_back(FrnRecordIndexEntry{ key, i });
            }

            SortFrnRecordIndex_NoLock();
        }

        static void PopulateVolumeRecordMetrics_NoLock(
            const std::vector<Record>& records,
            std::unordered_map<wchar_t, VolumeState>& volumes)
        {
            for (auto& pair : volumes)
            {
                pair.second.recordCount = 0;
                pair.second.maxFrn = 0;
            }

            for (const auto& record : records)
            {
                auto volumeIt = volumes.find(record.driveLetter);
                if (volumeIt == volumes.end())
                {
                    continue;
                }

                auto& volume = volumeIt->second;
                if (volume.recordCount < std::numeric_limits<std::uint32_t>::max())
                {
                    ++volume.recordCount;
                }

                if (record.frn > volume.maxFrn)
                {
                    volume.maxFrn = record.frn;
                }

                if (!record.isDirectory)
                {
                    continue;
                }

                const auto existing = volume.frnMap.find(record.frn);
                if (existing == volume.frnMap.end() || record.originalName.length() > existing->second.name.length())
                {
                    volume.frnMap[record.frn] = FrnEntry{ record.originalName, record.parentFrn, true };
                }
            }
        }

        void PopulateVolumeRecordMetricsFromMapped_NoLock(std::unordered_map<wchar_t, VolumeState>& volumes) const
        {
            for (auto& pair : volumes)
            {
                pair.second.recordCount = 0;
                pair.second.maxFrn = 0;
            }

            if (!mappedRecords_.Ready())
            {
                return;
            }

            for (std::uint32_t i = 0; i < mappedRecords_.recordCount; ++i)
            {
                const auto& compact = mappedRecords_.records[i];
                auto volumeIt = volumes.find(static_cast<wchar_t>(compact.drive));
                if (volumeIt == volumes.end())
                {
                    continue;
                }

                auto& volume = volumeIt->second;
                if (volume.recordCount < std::numeric_limits<std::uint32_t>::max())
                {
                    ++volume.recordCount;
                }

                if (compact.frn > volume.maxFrn)
                {
                    volume.maxFrn = compact.frn;
                }
            }
        }

        void ReleaseDuplicateFrnNameStorage_NoLock()
        {
            for (auto& pair : volumes_)
            {
                pair.second.frnMap.clear();
                pair.second.frnMap.rehash(0);
                pair.second.pathCache.clear();
                pair.second.pathCache.rehash(0);
            }
        }

        void TrimPathCache_NoLock(VolumeState& volume) const
        {
            if (volume.pathCache.size() <= MaxPathCacheEntriesPerVolume)
            {
                return;
            }

            volume.pathCache.clear();
            volume.pathCache.rehash(MaxPathCacheEntriesPerVolume / 2);
        }

        void ClearPathScopeRecordIdsCache_NoLock() const
        {
            pathScopeRecordIdsCache_.clear();
            pathScopeRecordIdsCacheGeneration_ = generationId_.load(std::memory_order_acquire);
        }

        void EnsurePathScopeRecordIdsCacheCurrent_NoLock() const
        {
            const auto generation = generationId_.load(std::memory_order_acquire);
            if (pathScopeRecordIdsCacheGeneration_ == generation)
            {
                return;
            }

            pathScopeRecordIdsCache_.clear();
            pathScopeRecordIdsCacheGeneration_ = generation;
        }

        bool TryGetCachedPathScopeRecordIds_NoLock(std::uint64_t key, std::vector<std::uint32_t>& ids) const
        {
            EnsurePathScopeRecordIdsCacheCurrent_NoLock();
            const auto it = pathScopeRecordIdsCache_.find(key);
            if (it == pathScopeRecordIdsCache_.end())
            {
                return false;
            }

            ids = it->second;
            return true;
        }

        void StorePathScopeRecordIdsCache_NoLock(std::uint64_t key, const std::vector<std::uint32_t>& ids) const
        {
            EnsurePathScopeRecordIdsCacheCurrent_NoLock();
            if (pathScopeRecordIdsCache_.size() >= MaxPathScopeRecordIdsCacheEntries)
            {
                pathScopeRecordIdsCache_.clear();
            }

            pathScopeRecordIdsCache_[key] = ids;
        }

        bool TryGetRecordIdByKey_NoLock(std::uint64_t key, std::uint32_t& recordId) const
        {
            const auto overlayIt = overlayIndexByKey_.find(key);
            if (overlayIt != overlayIndexByKey_.end())
            {
                recordId = MakeOverlayRecordId(overlayIt->second);
                return true;
            }

            if (overlayDeletedKeys_.find(key) != overlayDeletedKeys_.end())
            {
                return false;
            }

            auto lo = frnRecordIndex_.begin();
            auto hi = frnRecordIndex_.end();
            while (lo < hi)
            {
                auto mid = lo + ((hi - lo) / 2);
                if (mid->key < key)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            if (lo == frnRecordIndex_.end() || lo->key != key || lo->recordId >= GetRecordCount_NoLock())
            {
                return false;
            }

            recordId = lo->recordId;
            return true;
        }

        bool TryGetStableRecordIdByKey_NoLock(std::uint64_t key, std::uint32_t& recordId) const
        {
            auto lo = frnRecordIndex_.begin();
            auto hi = frnRecordIndex_.end();
            while (lo < hi)
            {
                auto mid = lo + ((hi - lo) / 2);
                if (mid->key < key)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            if (lo == frnRecordIndex_.end() || lo->key != key || lo->recordId >= GetRecordCount_NoLock())
            {
                return false;
            }

            recordId = lo->recordId;
            return true;
        }

        void SetOverlaySuppressedRecordId_NoLock(std::uint64_t key, bool suppressed)
        {
            const auto drive = static_cast<wchar_t>((key >> 56) & 0xFF);
            const auto frn = key & 0x00FFFFFFFFFFFFFFull;
            const auto volumeIt = volumes_.find(drive);
            if (volumeIt != volumes_.end() && frn > volumeIt->second.maxFrn)
            {
                return;
            }

            std::uint32_t recordId = 0;
            if (!TryGetStableRecordIdByKey_NoLock(key, recordId))
            {
                return;
            }

            const auto recordCount = GetRecordCount_NoLock();
            if (overlaySuppressedRecordIds_.size() != recordCount)
            {
                overlaySuppressedRecordIds_.assign(recordCount, 0);
            }

            overlaySuppressedRecordIds_[recordId] = suppressed ? 1 : 0;
        }

        void RebuildOverlaySuppressedRecordIds_NoLock()
        {
            const auto recordCount = GetRecordCount_NoLock();
            overlaySuppressedRecordIds_.clear();
            if (recordCount == 0 || (overlayDeletedKeys_.empty() && overlayIndexByKey_.empty()))
            {
                return;
            }

            overlaySuppressedRecordIds_.assign(recordCount, 0);
            for (const auto key : overlayDeletedKeys_)
            {
                std::uint32_t recordId = 0;
                if (TryGetStableRecordIdByKey_NoLock(key, recordId))
                {
                    overlaySuppressedRecordIds_[recordId] = 1;
                }
            }

            for (const auto& pair : overlayIndexByKey_)
            {
                std::uint32_t recordId = 0;
                if (TryGetStableRecordIdByKey_NoLock(pair.first, recordId))
                {
                    overlaySuppressedRecordIds_[recordId] = 1;
                }
            }
        }

        std::uint32_t GetRecordCount_NoLock() const
        {
            return mappedRecords_.Ready()
                ? mappedRecords_.recordCount
                : static_cast<std::uint32_t>(records_.size());
        }

        static bool IsOverlayRecordId(std::uint32_t recordId)
        {
            return (recordId & OverlayRecordIdMask) != 0;
        }

        static std::uint32_t MakeOverlayRecordId(std::uint32_t overlayIndex)
        {
            return OverlayRecordIdMask | overlayIndex;
        }

        static std::uint32_t GetOverlayIndex(std::uint32_t recordId)
        {
            return recordId & ~OverlayRecordIdMask;
        }

        bool TryGetOverlayRecord_NoLock(std::uint32_t recordId, const Record*& record) const
        {
            record = nullptr;
            if (!IsOverlayRecordId(recordId))
            {
                return false;
            }

            const auto overlayIndex = GetOverlayIndex(recordId);
            if (overlayIndex >= overlayRecords_.size())
            {
                return false;
            }

            record = &overlayRecords_[overlayIndex];
            return true;
        }

        bool IsRecordSuppressedByOverlay_NoLock(std::uint32_t recordId) const
        {
            if (IsOverlayRecordId(recordId))
            {
                return false;
            }

            if (recordId < overlaySuppressedRecordIds_.size())
            {
                return overlaySuppressedRecordIds_[recordId] != 0;
            }

            if (overlayDeletedKeys_.empty() && overlayIndexByKey_.empty())
            {
                return false;
            }

            const auto key = MakeRecordKey(GetRecordDrive_NoLock(recordId), GetRecordFrn_NoLock(recordId));
            return overlayDeletedKeys_.find(key) != overlayDeletedKeys_.end()
                || overlayIndexByKey_.find(key) != overlayIndexByKey_.end();
        }

        bool TryGetCompactRecord_NoLock(std::uint32_t recordId, const NativeV2CompactRecord*& record) const
        {
            record = nullptr;
            if (!mappedRecords_.Ready() || recordId >= mappedRecords_.recordCount)
            {
                return false;
            }

            record = &mappedRecords_.records[recordId];
            return true;
        }

        std::wstring_view GetLowerNameView_NoLock(std::uint32_t recordId) const
        {
            const NativeV2CompactRecord* compact = nullptr;
            const Record* overlay = nullptr;
            if (TryGetOverlayRecord_NoLock(recordId, overlay))
            {
                return std::wstring_view(overlay->lowerName);
            }

            if (TryGetCompactRecord_NoLock(recordId, compact))
            {
                if (static_cast<std::size_t>(compact->lowerOffset) + compact->lowerLength <= mappedRecords_.stringPoolChars)
                {
                    return std::wstring_view(mappedRecords_.stringPool + compact->lowerOffset, compact->lowerLength);
                }

                return {};
            }

            if (recordId >= records_.size())
            {
                return {};
            }

            return std::wstring_view(records_[recordId].lowerName);
        }

        std::wstring_view GetOriginalNameView_NoLock(std::uint32_t recordId) const
        {
            const NativeV2CompactRecord* compact = nullptr;
            const Record* overlay = nullptr;
            if (TryGetOverlayRecord_NoLock(recordId, overlay))
            {
                return std::wstring_view(overlay->originalName);
            }

            if (TryGetCompactRecord_NoLock(recordId, compact))
            {
                if (static_cast<std::size_t>(compact->originalOffset) + compact->originalLength <= mappedRecords_.stringPoolChars)
                {
                    return std::wstring_view(mappedRecords_.stringPool + compact->originalOffset, compact->originalLength);
                }

                return {};
            }

            if (recordId >= records_.size())
            {
                return {};
            }

            return std::wstring_view(records_[recordId].originalName);
        }

        std::uint64_t GetRecordFrn_NoLock(std::uint32_t recordId) const
        {
            const NativeV2CompactRecord* compact = nullptr;
            const Record* overlay = nullptr;
            if (TryGetOverlayRecord_NoLock(recordId, overlay))
            {
                return overlay->frn;
            }

            if (TryGetCompactRecord_NoLock(recordId, compact))
            {
                return compact->frn;
            }

            return recordId < records_.size() ? records_[recordId].frn : 0;
        }

        std::uint64_t GetRecordParentFrn_NoLock(std::uint32_t recordId) const
        {
            const NativeV2CompactRecord* compact = nullptr;
            const Record* overlay = nullptr;
            if (TryGetOverlayRecord_NoLock(recordId, overlay))
            {
                return overlay->parentFrn;
            }

            if (TryGetCompactRecord_NoLock(recordId, compact))
            {
                return compact->parentFrn;
            }

            return recordId < records_.size() ? records_[recordId].parentFrn : 0;
        }

        wchar_t GetRecordDrive_NoLock(std::uint32_t recordId) const
        {
            const NativeV2CompactRecord* compact = nullptr;
            const Record* overlay = nullptr;
            if (TryGetOverlayRecord_NoLock(recordId, overlay))
            {
                return overlay->driveLetter;
            }

            if (TryGetCompactRecord_NoLock(recordId, compact))
            {
                return static_cast<wchar_t>(compact->drive);
            }

            return recordId < records_.size() ? records_[recordId].driveLetter : L'\0';
        }

        bool IsDirectoryRecord_NoLock(std::uint32_t recordId) const
        {
            const NativeV2CompactRecord* compact = nullptr;
            const Record* overlay = nullptr;
            if (TryGetOverlayRecord_NoLock(recordId, overlay))
            {
                return overlay->isDirectory;
            }

            if (TryGetCompactRecord_NoLock(recordId, compact))
            {
                return (compact->flags & 0x1) != 0;
            }

            return recordId < records_.size() && records_[recordId].isDirectory;
        }

        bool TryGetChildRange_NoLock(std::uint64_t key, std::uint32_t& offset, std::uint32_t& count) const
        {
            auto lo = v2_.childEntries.begin();
            auto hi = v2_.childEntries.end();
            while (lo < hi)
            {
                auto mid = lo + ((hi - lo) / 2);
                if (mid->key < key)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            if (lo == v2_.childEntries.end() || lo->key != key)
            {
                return false;
            }

            offset = lo->offset;
            count = lo->count;
            return true;
        }

        std::uint64_t EstimateNativeMemoryBytes_NoLock() const
        {
            std::uint64_t bytes = static_cast<std::uint64_t>(records_.capacity()) * sizeof(Record);
            for (const auto& record : records_)
            {
                bytes += (static_cast<std::uint64_t>(record.lowerName.capacity()) + record.originalName.capacity()) * sizeof(wchar_t);
            }
            if (mappedRecords_.Ready())
            {
                bytes += mappedRecords_.file.Size();
            }

            bytes += static_cast<std::uint64_t>(allIds_.capacity()
                + directoryIds_.capacity()
                + launchableIds_.capacity()
                + scriptIds_.capacity()
                + logIds_.capacity()
                + configIds_.capacity()
                + v2_.trigramPostings.capacity()
                + v2_.parentOrder.capacity()
                + v2_.childRecordIds.capacity()) * sizeof(std::uint32_t);
            for (const auto& ids : driveIds_)
            {
                bytes += static_cast<std::uint64_t>(ids.capacity()) * sizeof(std::uint32_t);
            }
            bytes += static_cast<std::uint64_t>(v2_.trigramEntries.capacity()) * sizeof(NativeV2BucketEntry);
            bytes += static_cast<std::uint64_t>(v2_.prefix4Entries.capacity()) * sizeof(NativeV2BucketEntry);
            bytes += static_cast<std::uint64_t>(v2_.prefix4Postings.capacity());
            bytes += static_cast<std::uint64_t>(v2_.childEntries.capacity()) * sizeof(NativeV2ChildBucketEntry);
            bytes += static_cast<std::uint64_t>(frnRecordIndex_.capacity()) * sizeof(FrnRecordIndexEntry);
            bytes += EstimateShortPostingBytes_NoLock();
            return bytes;
        }

        bool CatchUpVolume(
            UsnReadState& volumeState,
            const std::atomic_bool* stopToken,
            std::vector<NativeChange>& allChanges)
        {
            allChanges.clear();

            const HANDLE handle = OpenVolumeHandle(BuildVolumePath(volumeState.driveLetter));
            if (handle == INVALID_HANDLE_VALUE)
            {
                return false;
            }

            auto* buffer = static_cast<std::byte*>(std::malloc(CatchUpBufferSize));
            if (buffer == nullptr)
            {
                CloseHandle(handle);
                throw std::bad_alloc();
            }

            bool journalReset = false;
            std::vector<NativeChange> changes;

            while (true)
            {
                if (stopToken != nullptr && stopToken->load())
                {
                    break;
                }

                changes.clear();
                if (!ReadUsnChanges_NoLock(handle, volumeState, buffer, CatchUpBufferSize, 0, stopToken, changes, journalReset))
                {
                    break;
                }

                if (changes.empty())
                {
                    break;
                }

                allChanges.insert(allChanges.end(), changes.begin(), changes.end());
            }

            std::free(buffer);
            CloseHandle(handle);
            return journalReset;
        }

        void ScheduleJournalResetRebuild()
        {
            auto expected = false;
            if (!journalResetRebuildScheduled_.compare_exchange_strong(expected, true))
            {
                return;
            }

            std::thread([this]()
            {
                try
                {
                    StopBackgroundCatchUp();
                    StopWatchers();

                    bool publishRefresh = false;
                    {
                        std::unique_lock<std::shared_mutex> guard(mutex_);
                        if (hasShutdown_)
                        {
                            journalResetRebuildScheduled_.store(false);
                            return;
                        }

                        currentStatusMessage_ = L"native USN 日志失效，正在重建索引...";
                        isBackgroundCatchUpInProgress_ = true;
                        publishRefresh = true;
                    }

                    if (publishRefresh)
                    {
                        PublishStatus(true);
                    }

                    {
                        std::unique_lock<std::shared_mutex> guard(mutex_);
                        if (!hasShutdown_)
                        {
                            currentStatusMessage_ = L"native USN 日志失效；当前快照继续提供搜索，等待下一次显式重建";
                            isBackgroundCatchUpInProgress_ = false;
                        }
                    }

                    PublishStatus(true);
                }
                catch (const std::exception& ex)
                {
                    {
                        std::unique_lock<std::shared_mutex> guard(mutex_);
                        currentStatusMessage_ = L"native 重建失败: " + Utf8ToWide(ex.what());
                        isBackgroundCatchUpInProgress_ = false;
                    }

                    PublishStatus(false);
                }
                catch (...)
                {
                    {
                        std::unique_lock<std::shared_mutex> guard(mutex_);
                        currentStatusMessage_ = L"native 重建失败";
                        isBackgroundCatchUpInProgress_ = false;
                    }

                    PublishStatus(false);
                }

                journalResetRebuildScheduled_.store(false);
            }).detach();
        }

        void WatchLoop(wchar_t driveLetter, const std::shared_ptr<std::atomic_bool>& stopToken)
        {
            const HANDLE handle = OpenVolumeHandle(BuildVolumePath(driveLetter));
            if (handle == INVALID_HANDLE_VALUE)
            {
                return;
            }

            auto* buffer = static_cast<std::byte*>(std::malloc(LiveWatcherBufferSize));
            if (buffer == nullptr)
            {
                CloseHandle(handle);
                return;
            }

            while (!stopToken->load())
            {
                std::vector<std::string> payloads;
                bool shouldSave = false;
                bool publishRefresh = false;
                bool journalReset = false;
                bool shouldRebuild = false;
                UsnReadState readState;
                bool volumeExists = false;

                {
                    std::unique_lock<std::shared_mutex> guard(mutex_);
                    auto volumeIt = volumes_.find(driveLetter);
                    if (volumeIt != volumes_.end())
                    {
                        readState = CreateUsnReadState(volumeIt->second);
                        volumeExists = true;
                    }
                }

                if (!volumeExists)
                {
                    break;
                }

                std::vector<NativeChange> changes;
                const auto readSucceeded = ReadUsnChanges_NoLock(
                    handle,
                    readState,
                    buffer,
                    LiveWatcherBufferSize,
                    LiveWaitMilliseconds,
                    stopToken.get(),
                    changes,
                    journalReset);

                if (stopToken->load())
                {
                    break;
                }

                {
                    std::unique_lock<std::shared_mutex> guard(mutex_);
                    if (stopToken->load())
                    {
                        break;
                    }

                    auto volumeIt = volumes_.find(driveLetter);
                    if (volumeIt == volumes_.end())
                    {
                        break;
                    }

                    ApplyUsnReadState(volumeIt->second, std::move(readState));

                    if (!readSucceeded)
                    {
                        if (journalReset)
                        {
                            currentStatusMessage_ = L"native watcher 发现 USN 日志失效";
                            isBackgroundCatchUpInProgress_ = true;
                            publishRefresh = true;
                            shouldRebuild = true;
                        }
                    }

                    if (!journalReset && !changes.empty())
                    {
                        if (ApplyCollectedChanges_NoLock(changes, &payloads))
                        {
                            generationId_.fetch_add(1, std::memory_order_acq_rel);
                            shouldSave = true;
                            publishRefresh = true;
                            NativeLogWrite(
                                "[NATIVE LIVE WATCH OVERLAY] changes="
                                + std::to_string(changes.size())
                                + " overlayRecords=" + std::to_string(overlayRecords_.size())
                                + " overlayDeletes=" + std::to_string(overlayDeletedKeys_.size()));
                        }
                    }

                }

                if (shouldSave)
                {
                    QueueSnapshotSave();
                }

                if (publishRefresh)
                {
                    PublishStatus(shouldRebuild);
                }

                if (shouldRebuild)
                {
                    ScheduleJournalResetRebuild();
                    break;
                }

                for (const auto& payload : payloads)
                {
                    PublishChangePayload(payload);
                }
            }

            std::free(buffer);
            CloseHandle(handle);
        }

        static std::wstring BuildVolumePath(wchar_t driveLetter)
        {
            return LR"(\\.\)" + std::wstring(1, driveLetter) + L":";
        }

        static HANDLE OpenVolumeHandle(const std::wstring& volumePath)
        {
            return CreateFileW(
                volumePath.c_str(),
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                nullptr,
                OPEN_EXISTING,
                0,
                nullptr);
        }

        static bool QueryJournal_NoLock(HANDLE handle, USN_JOURNAL_DATA_V0& journalData)
        {
            DWORD bytesReturned = 0;
            return DeviceIoControl(
                handle,
                FSCTL_QUERY_USN_JOURNAL,
                nullptr,
                0,
                &journalData,
                sizeof(journalData),
                &bytesReturned,
                nullptr) != FALSE;
        }

        bool ReadUsnChanges_NoLock(
            HANDLE handle,
            UsnReadState& volume,
            std::byte* buffer,
            DWORD bufferSize,
            unsigned long long waitMilliseconds,
            const std::atomic_bool* stopToken,
            std::vector<NativeChange>& changes,
            bool& journalReset)
        {
            changes.clear();
            journalReset = false;

            if (stopToken != nullptr && stopToken->load())
            {
                return true;
            }

            USN_JOURNAL_DATA_V0 journalData{};
            if (!QueryJournal_NoLock(handle, journalData))
            {
                return false;
            }

            if (volume.journalId != 0 &&
                (volume.journalId != journalData.UsnJournalID || volume.nextUsn < journalData.LowestValidUsn))
            {
                volume.journalId = journalData.UsnJournalID;
                volume.nextUsn = journalData.NextUsn;
                journalReset = true;
                return false;
            }

            volume.journalId = journalData.UsnJournalID;

            READ_USN_JOURNAL_DATA_V0 readData{};
            readData.StartUsn = volume.nextUsn;
            readData.ReasonMask = USN_REASON_FILE_CREATE | USN_REASON_FILE_DELETE | USN_REASON_RENAME_OLD_NAME | USN_REASON_RENAME_NEW_NAME;
            readData.ReturnOnlyOnClose = 0;
            readData.Timeout = waitMilliseconds * 10000ull;
            readData.BytesToWaitFor = waitMilliseconds > 0 ? 1ull : 0ull;
            readData.UsnJournalID = volume.journalId;

            while (stopToken == nullptr || !stopToken->load())
            {
                if (stopToken != nullptr && stopToken->load())
                {
                    return true;
                }

                DWORD bytesReturned = 0;
                if (!DeviceIoControl(
                    handle,
                    FSCTL_READ_USN_JOURNAL,
                    &readData,
                    sizeof(readData),
                    buffer,
                    bufferSize,
                    &bytesReturned,
                    nullptr))
                {
                    const auto lastError = GetLastError();
                    if (lastError == ErrorJournalEntryDeleted)
                    {
                        journalReset = true;
                    }

                    return false;
                }

                if (bytesReturned <= sizeof(USN))
                {
                    volume.nextUsn = readData.StartUsn;
                    if (waitMilliseconds == 0 || volume.nextUsn >= journalData.NextUsn)
                    {
                        return true;
                    }

                    continue;
                }

                readData.StartUsn = *reinterpret_cast<USN*>(buffer);
                volume.nextUsn = readData.StartUsn;

                DWORD offset = sizeof(USN);
                while (offset + 60 < bytesReturned)
                {
                    if (stopToken != nullptr && stopToken->load())
                    {
                        return true;
                    }

                    const auto* recordHeader = reinterpret_cast<const USN_RECORD_V2*>(buffer + offset);
                    if (recordHeader->RecordLength == 0)
                    {
                        break;
                    }

                    const auto frn = static_cast<std::uint64_t>(recordHeader->FileReferenceNumber & 0x0000FFFFFFFFFFFFull);
                    const auto parentFrn = static_cast<std::uint64_t>(recordHeader->ParentFileReferenceNumber & 0x0000FFFFFFFFFFFFull);
                    const auto reason = recordHeader->Reason;
                    const auto isDirectory = (recordHeader->FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                    const auto* fileNamePtr = reinterpret_cast<const wchar_t*>(reinterpret_cast<const std::byte*>(recordHeader) + recordHeader->FileNameOffset);
                    const auto fileNameLength = recordHeader->FileNameLength / sizeof(wchar_t);
                    const std::wstring fileName(fileNamePtr, fileNamePtr + fileNameLength);

                    if ((reason & USN_REASON_RENAME_OLD_NAME) != 0)
                    {
                        volume.pendingRenameOldByFrn[frn] = std::make_pair(fileName, parentFrn);
                    }
                    else if ((reason & USN_REASON_RENAME_NEW_NAME) != 0)
                    {
                        const auto pendingIt = volume.pendingRenameOldByFrn.find(frn);
                        if (pendingIt != volume.pendingRenameOldByFrn.end())
                        {
                            NativeChange change;
                            change.kind = ChangeKind::Rename;
                            change.frn = frn;
                            change.parentFrn = parentFrn;
                            change.oldParentFrn = pendingIt->second.second;
                            change.driveLetter = volume.driveLetter;
                            change.isDirectory = isDirectory;
                            change.originalName = fileName;
                            change.lowerName = ToLower(fileName);
                            change.oldLowerName = ToLower(pendingIt->second.first);
                            AppendCollectedChange(changes, change);
                            volume.pendingRenameOldByFrn.erase(pendingIt);
                        }
                    }
                    else if ((reason & USN_REASON_FILE_DELETE) != 0)
                    {
                        volume.pendingRenameOldByFrn.erase(frn);

                        NativeChange change;
                        change.kind = ChangeKind::Delete;
                        change.frn = frn;
                        change.parentFrn = parentFrn;
                        change.driveLetter = volume.driveLetter;
                        change.isDirectory = isDirectory;
                        change.originalName = fileName;
                        change.lowerName = ToLower(fileName);
                        AppendCollectedChange(changes, change);
                    }
                    else if ((reason & USN_REASON_FILE_CREATE) != 0)
                    {
                        NativeChange change;
                        change.kind = ChangeKind::Create;
                        change.frn = frn;
                        change.parentFrn = parentFrn;
                        change.driveLetter = volume.driveLetter;
                        change.isDirectory = isDirectory;
                        change.originalName = fileName;
                        change.lowerName = ToLower(fileName);
                        AppendCollectedChange(changes, change);
                    }

                    offset += recordHeader->RecordLength;
                }

                return true;
            }

            return true;
        }

        bool ApplyChange_NoLock(NativeChange& change, std::string* payload, std::uint32_t* changedOverlayIndex)
        {
            if (changedOverlayIndex != nullptr)
            {
                *changedOverlayIndex = std::numeric_limits<std::uint32_t>::max();
            }

            auto volumeIt = volumes_.find(change.driveLetter);
            if (volumeIt == volumes_.end())
            {
                return false;
            }

            if (IsSelfManagedChange_NoLock(change))
            {
                return false;
            }

            switch (change.kind)
            {
            case ChangeKind::Create:
                return ApplyCreate_NoLock(volumeIt->second, change, payload, changedOverlayIndex);
            case ChangeKind::Delete:
                return ApplyDelete_NoLock(volumeIt->second, change, payload);
            case ChangeKind::Rename:
                return ApplyRename_NoLock(volumeIt->second, change, payload, changedOverlayIndex);
            default:
                return false;
            }
        }

        bool ApplyCollectedChanges_NoLock(
            std::vector<NativeChange>& changes,
            std::vector<std::string>* payloads,
            bool forceRebuildOverlaySearch = false,
            bool deferOverlayRebuild = false)
        {
            const auto totalStart = std::chrono::steady_clock::now();
            bool changed = false;
            std::vector<std::uint32_t> changedOverlayIndexes;
            const bool incrementalOverlay = !forceRebuildOverlaySearch
                && changes.size() <= OverlayImmediateRebuildThreshold;
            if (incrementalOverlay)
            {
                changedOverlayIndexes.reserve(changes.size());
            }

            if (payloads != nullptr)
            {
                payloads->clear();
                payloads->reserve(changes.size());
            }

            ReserveOverlayMutationCapacity_NoLock(changes);

            for (auto& change : changes)
            {
                std::string payload;
                std::uint32_t changedOverlayIndex = std::numeric_limits<std::uint32_t>::max();
                if (ApplyChange_NoLock(change, payloads == nullptr ? nullptr : &payload, &changedOverlayIndex))
                {
                    changed = true;
                    if (change.kind == ChangeKind::Delete)
                    {
                        MarkOverlaySearchStale_NoLock();
                    }
                    else if (changedOverlayIndex != std::numeric_limits<std::uint32_t>::max())
                    {
                        if (incrementalOverlay)
                        {
                            changedOverlayIndexes.push_back(changedOverlayIndex);
                        }
                        else
                        {
                            MarkOverlaySearchStale_NoLock();
                        }
                    }

                    if (payloads != nullptr && !payload.empty())
                    {
                        payloads->push_back(std::move(payload));
                    }
                }
            }
            const auto applyLoopMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - totalStart).count();

            if (changed)
            {
                lastOverlayMutationTickMs_.store(SteadyClockMilliseconds(), std::memory_order_release);
                const bool shouldRebuildOverlaySearch = forceRebuildOverlaySearch
                    || changes.size() > OverlayImmediateRebuildThreshold
                    || ShouldRebuildOverlaySearch_NoLock();
                if (changes.size() >= OverlayImmediateRebuildThreshold)
                {
                    NativeLogWrite(
                        "[NATIVE DELTA APPLY] changes=" + std::to_string(changes.size())
                        + " applyLoopMs=" + std::to_string(applyLoopMs)
                        + " changed=true"
                        + " incrementalOverlay=" + std::string(incrementalOverlay ? "true" : "false")
                        + " shouldRebuildOverlaySearch=" + std::string(shouldRebuildOverlaySearch ? "true" : "false")
                        + " deferOverlayRebuild=" + std::string(deferOverlayRebuild ? "true" : "false")
                        + " overlayRecords=" + std::to_string(overlayRecords_.size())
                        + " overlayDeletes=" + std::to_string(overlayDeletedKeys_.size()));
                }

                if (shouldRebuildOverlaySearch)
                {
                    if (deferOverlayRebuild)
                    {
                        MarkOverlaySearchNeedsRebuild_NoLock();
                    }
                    else
                    {
                        RebuildOverlaySearchIndex_NoLock();
                    }
                }
                else
                {
                    for (const auto overlayIndex : changedOverlayIndexes)
                    {
                        AddOverlaySearchEntry_NoLock(overlayIndex);
                    }
                }
            }
            else if (changes.size() >= OverlayImmediateRebuildThreshold)
            {
                NativeLogWrite(
                    "[NATIVE DELTA APPLY] changes=" + std::to_string(changes.size())
                    + " applyLoopMs=" + std::to_string(applyLoopMs)
                    + " changed=false"
                    + " overlayRecords=" + std::to_string(overlayRecords_.size())
                    + " overlayDeletes=" + std::to_string(overlayDeletedKeys_.size()));
            }

            return changed;
        }

        void ReserveOverlayMutationCapacity_NoLock(const std::vector<NativeChange>& changes)
        {
            if (changes.size() < OverlayImmediateRebuildThreshold)
            {
                return;
            }

            std::size_t upsertCount = 0;
            std::size_t deleteCount = 0;
            for (const auto& change : changes)
            {
                switch (change.kind)
                {
                case ChangeKind::Create:
                case ChangeKind::Rename:
                    ++upsertCount;
                    break;
                case ChangeKind::Delete:
                    ++deleteCount;
                    break;
                default:
                    break;
                }
            }

            if (upsertCount > 0)
            {
                const auto expectedOverlayRecords = overlayRecords_.size() + upsertCount;
                if (overlayRecords_.capacity() < expectedOverlayRecords)
                {
                    overlayRecords_.reserve(expectedOverlayRecords);
                }

                overlayIndexByKey_.reserve(overlayIndexByKey_.size() + upsertCount);
            }

            if (deleteCount > 0)
            {
                overlayDeletedKeys_.reserve(overlayDeletedKeys_.size() + deleteCount);
            }
        }

        std::string InjectSyntheticUsnBacklog(int requestedCount, int seed)
        {
            const auto totalStart = std::chrono::steady_clock::now();
            if (requestedCount <= 0)
            {
                return BuildDeltaStateJson(true, 0);
            }

            auto count = std::min(requestedCount, 1000000);
            std::vector<NativeChange> changes;
            changes.reserve(static_cast<std::size_t>(count));

            {
                std::shared_lock<std::shared_mutex> guard(mutex_);
                const auto drive = SelectSyntheticDrive_NoLock();
                const auto parent = SelectSyntheticParentFrn_NoLock(drive);
                const auto baseFrn = SelectSyntheticBaseFrn_NoLock(drive, static_cast<std::uint64_t>(seed));

                const auto createCount = (count * 60) / 100;
                const auto renameCount = (count * 20) / 100;
                const auto deleteCount = count - createCount - renameCount;
                for (int i = 0; i < createCount; ++i)
                {
                    changes.push_back(BuildSyntheticChange(
                        ChangeKind::Create,
                        drive,
                        baseFrn + static_cast<std::uint64_t>(i),
                        parent,
                        L"__pm_delta_create_" + std::to_wstring(seed) + L"_" + std::to_wstring(i) + L".txt"));
                }

                for (int i = 0; i < renameCount; ++i)
                {
                    auto change = BuildSyntheticChange(
                        ChangeKind::Rename,
                        drive,
                        baseFrn + static_cast<std::uint64_t>(createCount + i),
                        parent,
                        L"__pm_delta_rename_new_" + std::to_wstring(seed) + L"_" + std::to_wstring(i) + L".txt");
                    change.oldParentFrn = parent;
                    change.oldLowerName = L"__pm_delta_rename_old_" + std::to_wstring(seed) + L"_" + std::to_wstring(i) + L".txt";
                    changes.push_back(std::move(change));
                }

                for (int i = 0; i < deleteCount; ++i)
                {
                    auto change = BuildSyntheticChange(
                        ChangeKind::Delete,
                        drive,
                        baseFrn + static_cast<std::uint64_t>(createCount + renameCount + i),
                        parent,
                        L"__pm_delta_delete_" + std::to_wstring(seed) + L"_" + std::to_wstring(i) + L".txt");
                    changes.push_back(std::move(change));
                }
            }

            const auto emitMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - totalStart).count();
            const auto applyStart = std::chrono::steady_clock::now();
            bool changed = false;
            std::uint64_t mutationGeneration = 0;
            {
                std::unique_lock<std::shared_mutex> guard(mutex_);
                changed = ApplyCollectedChanges_NoLock(changes, nullptr, false, true);
                if (changed)
                {
                    currentStatusMessage_ = L"已应用 synthetic USN delta";
                    mutationGeneration = generationId_.fetch_add(1, std::memory_order_acq_rel) + 1;
                }
            }

            if (changed)
            {
                const auto rebuildStart = std::chrono::steady_clock::now();
                const bool overlayRebuilt = RebuildOverlaySearchIndexOutsideExclusiveLock(mutationGeneration);
                const auto rebuildMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - rebuildStart).count();

                const auto overlaySaveStart = std::chrono::steady_clock::now();
                const bool overlaySaved = SaveOverlaySegmentSnapshotOutsideExclusiveLock(true);
                const auto overlaySaveMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - overlaySaveStart).count();
                if (!overlaySaved)
                {
                    NativeLogWrite("[NATIVE TEST DELTA] overlay-segment-save-failed");
                }

                const auto queueStart = std::chrono::steady_clock::now();
                QueueSnapshotSave();
                const auto queueMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - queueStart).count();
                NativeLogWrite(
                    "[NATIVE TEST DELTA DETAIL] changes=" + std::to_string(count)
                    + " overlayRebuilt=" + std::string(overlayRebuilt ? "true" : "false")
                    + " rebuildMs=" + std::to_string(rebuildMs)
                    + " overlaySaved=" + std::string(overlaySaved ? "true" : "false")
                    + " overlaySaveMs=" + std::to_string(overlaySaveMs)
                    + " queueMs=" + std::to_string(queueMs));
            }

            const auto applyMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - applyStart).count();
            if (changed)
            {
                PublishStatus(true);
            }

            const auto totalMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - totalStart).count();
            NativeLogWrite(
                "[NATIVE TEST DELTA] changes=" + std::to_string(count)
                + " emitMs=" + std::to_string(emitMs)
                + " applyMs=" + std::to_string(applyMs)
                + " totalMs=" + std::to_string(totalMs));
            return BuildDeltaStateJson(true, totalMs, emitMs, applyMs);
        }

        std::string BuildDeltaStateJson(bool success, long long totalMs, long long emitMs = 0, long long applyMs = 0) const
        {
            std::shared_lock<std::shared_mutex> guard(mutex_);
            std::ostringstream ss;
            ss << "{";
            ss << "\"success\":" << (success ? "true" : "false") << ",";
            ss << "\"ready\":true,";
            ss << "\"totalMs\":" << totalMs << ",";
            ss << "\"deltaBuildMs\":" << totalMs << ",";
            ss << "\"readUsnMs\":0,";
            ss << "\"coalesceMs\":0,";
            ss << "\"applyMs\":" << applyMs << ",";
            ss << "\"tokenEmitMs\":" << emitMs << ",";
            ss << "\"sortMs\":0,";
            ss << "\"publishLockMs\":" << applyMs << ",";
            ss << "\"deltaRecordCount\":" << overlayRecords_.size() << ",";
            ss << "\"tombstoneCount\":" << overlayDeletedKeys_.size() << ",";
            ss << "\"segmentCount\":" << (overlayRecords_.empty() && overlayDeletedKeys_.empty() ? 0 : 1);
            ss << "}";
            return ss.str();
        }

        static NativeChange BuildSyntheticChange(
            ChangeKind kind,
            wchar_t drive,
            std::uint64_t frn,
            std::uint64_t parent,
            const std::wstring& originalName)
        {
            NativeChange change;
            change.kind = kind;
            change.driveLetter = drive;
            change.frn = frn;
            change.parentFrn = parent;
            change.oldParentFrn = parent;
            change.isDirectory = false;
            change.originalName = originalName;
            change.lowerName = ToLower(originalName);
            change.oldLowerName = change.lowerName;
            return change;
        }

        wchar_t SelectSyntheticDrive_NoLock() const
        {
            if (!volumes_.empty())
            {
                return volumes_.begin()->first;
            }

            return L'C';
        }

        std::uint64_t SelectSyntheticParentFrn_NoLock(wchar_t drive) const
        {
            const auto* source = !directoryIds_.empty() ? &directoryIds_ : &allIds_;
            for (const auto recordId : *source)
            {
                if (recordId < GetRecordCount_NoLock() && GetRecordDrive_NoLock(recordId) == drive)
                {
                    return GetRecordFrn_NoLock(recordId);
                }
            }

            return NtfsRootFileReferenceNumber;
        }

        std::uint64_t SelectSyntheticBaseFrn_NoLock(wchar_t drive, std::uint64_t seed) const
        {
            std::uint64_t maxFrn = 0;
            const auto volumeIt = volumes_.find(drive);
            if (volumeIt != volumes_.end())
            {
                maxFrn = volumeIt->second.maxFrn;
            }

            return maxFrn + 1000000ull + (seed * 1000000ull);
        }

        bool ApplyCreate_NoLock(VolumeState& volume, NativeChange& change, std::string* payload, std::uint32_t* changedOverlayIndex)
        {
            Record record;
            record.frn = change.frn;
            record.parentFrn = change.parentFrn;
            record.driveLetter = change.driveLetter;
            record.isDirectory = change.isDirectory;
            if (payload == nullptr)
            {
                record.originalName = std::move(change.originalName);
                record.lowerName = std::move(change.lowerName);
            }
            else
            {
                record.originalName = change.originalName;
                record.lowerName = change.lowerName;
            }

            const auto key = MakeRecordKey(record.driveLetter, record.frn);
            if (!overlayCompactedKeys_.empty())
            {
                overlayCompactedKeys_.erase(key);
            }

            if (!overlayCompactedDeletedKeys_.empty())
            {
                overlayCompactedDeletedKeys_.erase(key);
            }

            const auto overlayIndex = static_cast<std::uint32_t>(overlayRecords_.size());
            auto [overlayIt, insertedOverlay] = overlayIndexByKey_.try_emplace(key, overlayIndex);
            if (!insertedOverlay)
            {
                if (payload == nullptr)
                {
                    overlayRecords_[overlayIt->second] = std::move(record);
                }
                else
                {
                    overlayRecords_[overlayIt->second] = record;
                }

                if (changedOverlayIndex != nullptr)
                {
                    *changedOverlayIndex = overlayIt->second;
                }
                MarkOverlaySearchStale_NoLock();
            }
            else
            {
                if (payload == nullptr)
                {
                    overlayRecords_.push_back(std::move(record));
                }
                else
                {
                    overlayRecords_.push_back(record);
                }

                if (changedOverlayIndex != nullptr)
                {
                    *changedOverlayIndex = overlayIndex;
                }
            }

            overlayDeletedKeys_.erase(key);
            if (record.frn <= volume.maxFrn)
            {
                SetOverlaySuppressedRecordId_NoLock(key, true);
                if (!volume.pathCache.empty())
                {
                    volume.pathCache.erase(record.frn);
                }
            }

            if (payload != nullptr)
            {
                *payload = BuildChangeJson(
                    "Created",
                    record.lowerName,
                    ResolveFullPath_NoLock(record),
                    L"",
                    L"",
                    L"",
                    record.isDirectory);
            }

            return true;
        }

        bool ApplyDelete_NoLock(VolumeState& volume, NativeChange& change, std::string* payload)
        {
            const auto key = MakeRecordKey(change.driveLetter, change.frn);
            if (!overlayCompactedKeys_.empty())
            {
                overlayCompactedKeys_.erase(key);
            }

            if (!overlayCompactedDeletedKeys_.empty())
            {
                overlayCompactedDeletedKeys_.erase(key);
            }

            std::uint32_t recordId = 0;
            if (change.frn > volume.maxFrn)
            {
                const auto overlayIt = overlayIndexByKey_.find(key);
                if (overlayIt != overlayIndexByKey_.end())
                {
                    overlayIndexByKey_.erase(overlayIt);
                    overlayDeletedKeys_.erase(key);
                    if (!volume.pathCache.empty())
                    {
                        volume.pathCache.erase(change.frn);
                    }

                    return true;
                }

                return false;
            }

            if (!TryGetRecordIdByKey_NoLock(key, recordId))
            {
                if (!volume.pathCache.empty())
                {
                    volume.pathCache.erase(change.frn);
                }

                return false;
            }

            std::wstring removedLowerName;
            std::wstring fullPath;
            bool removedIsDirectory = false;
            if (payload != nullptr)
            {
                removedLowerName = std::wstring(GetLowerNameView_NoLock(recordId));
                removedIsDirectory = IsDirectoryRecord_NoLock(recordId);
                fullPath = ResolveFullPathById_NoLock(recordId);
            }

            overlayIndexByKey_.erase(key);
            overlayDeletedKeys_.insert(key);
            SetOverlaySuppressedRecordId_NoLock(key, true);
            if (!volume.pathCache.empty())
            {
                volume.pathCache.erase(change.frn);
            }

            if (payload != nullptr)
            {
                *payload = BuildChangeJson(
                    "Deleted",
                    removedLowerName,
                    fullPath,
                    L"",
                    L"",
                    L"",
                    removedIsDirectory);
            }

            return true;
        }

        bool ApplyRename_NoLock(VolumeState& volume, NativeChange& change, std::string* payload, std::uint32_t* changedOverlayIndex)
        {
            const auto key = MakeRecordKey(change.driveLetter, change.frn);
            if (!overlayCompactedKeys_.empty())
            {
                overlayCompactedKeys_.erase(key);
            }

            if (!overlayCompactedDeletedKeys_.empty())
            {
                overlayCompactedDeletedKeys_.erase(key);
            }

            Record record;
            record.frn = change.frn;
            record.parentFrn = change.parentFrn;
            record.driveLetter = change.driveLetter;
            record.isDirectory = change.isDirectory;
            if (payload == nullptr)
            {
                record.originalName = std::move(change.originalName);
                record.lowerName = std::move(change.lowerName);
            }
            else
            {
                record.originalName = change.originalName;
                record.lowerName = change.lowerName;
            }

            const auto overlayIndex = static_cast<std::uint32_t>(overlayRecords_.size());
            auto [overlayIt, insertedOverlay] = overlayIndexByKey_.try_emplace(key, overlayIndex);
            if (!insertedOverlay)
            {
                if (payload == nullptr)
                {
                    overlayRecords_[overlayIt->second] = std::move(record);
                }
                else
                {
                    overlayRecords_[overlayIt->second] = record;
                }

                if (changedOverlayIndex != nullptr)
                {
                    *changedOverlayIndex = overlayIt->second;
                }
                MarkOverlaySearchStale_NoLock();
            }
            else
            {
                if (payload == nullptr)
                {
                    overlayRecords_.push_back(std::move(record));
                }
                else
                {
                    overlayRecords_.push_back(record);
                }

                if (changedOverlayIndex != nullptr)
                {
                    *changedOverlayIndex = overlayIndex;
                }
            }

            overlayDeletedKeys_.erase(key);
            if (record.frn <= volume.maxFrn)
            {
                SetOverlaySuppressedRecordId_NoLock(key, true);
                if (!volume.pathCache.empty())
                {
                    volume.pathCache.erase(record.frn);
                }
            }

            if (payload != nullptr)
            {
                std::wstring oldFullPath = ResolveFullPath_NoLock(change.driveLetter, change.oldParentFrn, change.oldLowerName);
                std::uint32_t existingRecordId = 0;
                if (TryGetRecordIdByKey_NoLock(key, existingRecordId))
                {
                    oldFullPath = ResolveFullPathById_NoLock(existingRecordId);
                }

                *payload = BuildChangeJson(
                    "Renamed",
                    change.oldLowerName,
                    ResolveFullPath_NoLock(record),
                    oldFullPath,
                    record.originalName,
                    record.lowerName,
                    record.isDirectory);
            }

            return true;
        }

        void RemoveRecordAt_NoLock(std::size_t index)
        {
            const auto lastIndex = records_.size() - 1;
            if (index != lastIndex)
            {
                records_[index] = std::move(records_[lastIndex]);
            }

            records_.pop_back();
            derivedDirty_ = true;
        }

        void MatchRecords_NoLock(
            MatchMode mode,
            const std::wstring& query,
            SearchFilter filter,
            const std::vector<std::uint32_t>& source,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched) const
        {
            page.clear();
            totalMatched = 0;
            page.reserve(static_cast<std::size_t>(std::min(maxResults, 64)));

            if (query.empty())
            {
                return;
            }

            if (mode == MatchMode::Prefix)
            {
                PrefixMatch_NoLock(query, source, offset, maxResults, page, totalMatched);
                return;
            }

            if (mode == MatchMode::Suffix)
            {
                SuffixMatch_NoLock(query, source, offset, maxResults, page, totalMatched);
                return;
            }

            if (mode == MatchMode::Contains)
            {
                ContainsMatch_NoLock(query, filter, source, offset, maxResults, page, totalMatched);
                return;
            }

            std::wregex regex;
            try
            {
                if (mode == MatchMode::Regex)
                {
                    regex = std::wregex(query, std::regex_constants::icase);
                }
                else if (mode == MatchMode::Wildcard)
                {
                    regex = std::wregex(WildcardToRegex(query), std::regex_constants::icase);
                }
            }
            catch (const std::regex_error&)
            {
                page.clear();
                totalMatched = 0;
                return;
            }

            for (const auto recordIndex : source)
            {
                const auto lowerName = GetLowerNameView_NoLock(recordIndex);
                bool matched = false;

                try
                {
                    switch (mode)
                    {
                    case MatchMode::Prefix:
                        matched = StartsWith(lowerName, query);
                        break;
                    case MatchMode::Suffix:
                        matched = EndsWith(lowerName, query);
                        break;
                    case MatchMode::Regex:
                        matched = std::regex_search(lowerName.begin(), lowerName.end(), regex);
                        break;
                    case MatchMode::Wildcard:
                        matched = std::regex_search(lowerName.begin(), lowerName.end(), regex);
                        break;
                    default:
                        matched = false;
                        break;
                    }
                }
                catch (const std::regex_error&)
                {
                    page.clear();
                    totalMatched = 0;
                    return;
                }

                if (!matched)
                {
                    continue;
                }

                ++totalMatched;
                if (totalMatched > offset && static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(recordIndex);
                }
            }
        }

        bool ExecuteQueryPlan_NoLock(
            const QueryPlan& plan,
            SearchFilter filter,
            const std::wstring& pathPrefix,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched,
            const char*& acceleratorMode) const
        {
            page.clear();
            totalMatched = 0;

            switch (plan.kind)
            {
            case QueryPlanKind::Contains:
                return TryMatchRecordsV2_NoLock(
                    MatchMode::Contains,
                    plan.query,
                    filter,
                    pathPrefix,
                    offset,
                    maxResults,
                    page,
                    totalMatched,
                    acceleratorMode);

            case QueryPlanKind::Prefix:
                return PrefixMatchIndexed_NoLock(
                    plan.query,
                    filter,
                    pathPrefix,
                    offset,
                    maxResults,
                    page,
                    totalMatched,
                    acceleratorMode);

            case QueryPlanKind::Suffix:
                return TryMatchVerifiedLiteral_NoLock(
                    MatchMode::Suffix,
                    plan.query,
                    plan.query,
                    filter,
                    pathPrefix,
                    offset,
                    maxResults,
                    page,
                    totalMatched,
                    acceleratorMode);

            case QueryPlanKind::ExtensionWildcard:
                return ExtensionMatchIndexed_NoLock(
                    plan.extension,
                    filter,
                    pathPrefix,
                    offset,
                    maxResults,
                    page,
                    totalMatched,
                    acceleratorMode);

            case QueryPlanKind::WildcardLiteral:
                return TryMatchVerifiedLiteral_NoLock(
                    MatchMode::Wildcard,
                    plan.pattern,
                    plan.requiredLiteral,
                    filter,
                    pathPrefix,
                    offset,
                    maxResults,
                    page,
                    totalMatched,
                    acceleratorMode);

            case QueryPlanKind::RegexLiteral:
                return TryMatchVerifiedLiteral_NoLock(
                    MatchMode::Regex,
                    plan.pattern,
                    plan.requiredLiteral,
                    filter,
                    pathPrefix,
                    offset,
                    maxResults,
                    page,
                    totalMatched,
                    acceleratorMode);

            case QueryPlanKind::RegexScan:
                return RegexScan_NoLock(
                    plan.pattern,
                    filter,
                    pathPrefix,
                    offset,
                    maxResults,
                    page,
                    totalMatched,
                    acceleratorMode);

            default:
                return false;
            }
        }

        bool PrefixMatchIndexed_NoLock(
            const std::wstring& prefix,
            SearchFilter filter,
            const std::wstring& pathPrefix,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched,
            const char*& acceleratorMode) const
        {
            page.clear();
            totalMatched = 0;
            acceleratorMode = pathPrefix.empty() && filter == SearchFilter::All ? "prefix-sorted" : "prefix-sorted+filter";
            if (prefix.empty())
            {
                return false;
            }

            std::vector<std::uint32_t> candidates;
            const bool prefix4Accelerated = TryBuildPrefix4Candidates_NoLock(prefix, candidates);
            if (!prefix4Accelerated && !TryBuildLiteralCandidates_NoLock(prefix, candidates))
            {
                acceleratorMode = "prefix-literal-empty";
                return true;
            }

            if (prefix4Accelerated)
            {
                acceleratorMode = pathPrefix.empty() && filter == SearchFilter::All ? "v2-prefix4" : "v2-prefix4+filter";
            }

            ApplyFilterToCandidates_NoLock(candidates, filter);
            if (!pathPrefix.empty())
            {
                ApplyPathScopeToCandidates_NoLock(candidates, pathPrefix);
                acceleratorMode = prefix4Accelerated ? "v2-prefix4+path" : "prefix-sorted+path";
            }

            FillPageFromCandidateIds_NoLock(
                candidates,
                offset,
                maxResults,
                page,
                totalMatched,
                nullptr,
                MatchMode::Prefix,
                prefix);
            return true;
        }

        bool ExtensionMatchIndexed_NoLock(
            const std::wstring& extension,
            SearchFilter filter,
            const std::wstring& pathPrefix,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched,
            const char*& acceleratorMode) const
        {
            page.clear();
            totalMatched = 0;
            const auto bucketIt = extensionIds_.find(extension);
            if (bucketIt == extensionIds_.end())
            {
                acceleratorMode = pathPrefix.empty() ? "extension-bucket" : "extension-bucket+path";
                return true;
            }

            auto candidates = bucketIt->second;
            ApplyFilterToCandidates_NoLock(candidates, filter);
            if (!pathPrefix.empty())
            {
                ApplyPathScopeToCandidates_NoLock(candidates, pathPrefix);
                acceleratorMode = "extension-bucket+path";
            }
            else
            {
                acceleratorMode = filter == SearchFilter::All ? "extension-bucket" : "extension-bucket+filter";
            }

            FillPageFromCandidateIds_NoLock(
                candidates,
                offset,
                maxResults,
                page,
                totalMatched,
                nullptr,
                MatchMode::Wildcard,
                L"");
            return true;
        }

        bool TryMatchVerifiedLiteral_NoLock(
            MatchMode verifyMode,
            const std::wstring& verifyPattern,
            const std::wstring& requiredLiteral,
            SearchFilter filter,
            const std::wstring& pathPrefix,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched,
            const char*& acceleratorMode) const
        {
            if (requiredLiteral.empty())
            {
                return false;
            }

            auto candidates = BuildV2ContainsCandidates_NoLock(requiredLiteral);
            if (!candidates.accelerated)
            {
                return false;
            }
            acceleratorMode = verifyMode == MatchMode::Suffix ? "v2-literal+suffix-verify" : "v2-literal+wildcard-verify";
            ApplyFilterToCandidates_NoLock(candidates.ids, filter);
            if (!pathPrefix.empty())
            {
                ApplyPathScopeToCandidates_NoLock(candidates.ids, pathPrefix);
                acceleratorMode = verifyMode == MatchMode::Suffix ? "v2-literal+suffix-verify+path" : "v2-literal+wildcard-verify+path";
            }

            std::wregex wildcardRegex;
            std::wregex* regexPtr = nullptr;
            if (verifyMode == MatchMode::Wildcard || verifyMode == MatchMode::Regex)
            {
                try
                {
                    wildcardRegex = verifyMode == MatchMode::Wildcard
                        ? std::wregex(WildcardToRegex(verifyPattern), std::regex_constants::icase)
                        : std::wregex(verifyPattern, std::regex_constants::icase);
                    regexPtr = &wildcardRegex;
                }
                catch (const std::regex_error&)
                {
                    return false;
                }
            }

            FillPageFromCandidateIds_NoLock(
                candidates.ids,
                offset,
                maxResults,
                page,
                totalMatched,
                regexPtr,
                verifyMode,
                verifyPattern);
            return true;
        }

        bool RegexScan_NoLock(
            const std::wstring& pattern,
            SearchFilter filter,
            const std::wstring& pathPrefix,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched,
            const char*& acceleratorMode) const
        {
            page.clear();
            totalMatched = 0;
            acceleratorMode = pathPrefix.empty() ? "regex-scan" : "regex-scan+path";

            std::wregex regex;
            try
            {
                regex = std::wregex(pattern, std::regex_constants::icase);
            }
            catch (const std::regex_error&)
            {
                return false;
            }

            const auto* source = GetCandidateSource(filter);
            if (source == nullptr)
            {
                return true;
            }

            page.reserve(static_cast<std::size_t>(std::min(maxResults, 64)));
            for (const auto recordId : *source)
            {
                if (recordId >= GetRecordCount_NoLock())
                {
                    continue;
                }
                if (IsRecordSuppressedByOverlay_NoLock(recordId))
                {
                    continue;
                }
                if (!pathPrefix.empty() && !RecordPassesPathScope_NoLock(recordId, pathPrefix))
                {
                    continue;
                }

                const auto lowerName = GetLowerNameView_NoLock(recordId);
                bool matched = false;
                try
                {
                    matched = std::regex_search(lowerName.begin(), lowerName.end(), regex);
                }
                catch (const std::regex_error&)
                {
                    return false;
                }

                if (!matched)
                {
                    continue;
                }

                ++totalMatched;
                if (totalMatched > offset && static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(recordId);
                }
            }

            return true;
        }

        void FillPageFromCandidateIds_NoLock(
            const std::vector<std::uint32_t>& candidates,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched,
            const std::wregex* regex,
            MatchMode verifyMode,
            const std::wstring& verifyPattern) const
        {
            page.clear();
            totalMatched = 0;
            page.reserve(static_cast<std::size_t>(std::min(maxResults, 64)));
            for (const auto recordId : candidates)
            {
                if (recordId >= GetRecordCount_NoLock())
                {
                    continue;
                }
                if (IsRecordSuppressedByOverlay_NoLock(recordId))
                {
                    continue;
                }

                const auto lowerName = GetLowerNameView_NoLock(recordId);
                bool matched = true;
                if (verifyMode == MatchMode::Contains)
                {
                    matched = ContainsText(lowerName, verifyPattern);
                }
                else if (verifyMode == MatchMode::Prefix)
                {
                    matched = StartsWith(lowerName, verifyPattern);
                }
                else if (verifyMode == MatchMode::Suffix)
                {
                    matched = EndsWith(lowerName, verifyPattern);
                }
                else if ((verifyMode == MatchMode::Wildcard || verifyMode == MatchMode::Regex) && regex != nullptr)
                {
                    try
                    {
                        matched = std::regex_search(lowerName.begin(), lowerName.end(), *regex);
                    }
                    catch (const std::regex_error&)
                    {
                        matched = false;
                    }
                }

                if (!matched)
                {
                    continue;
                }

                ++totalMatched;
                if (totalMatched > offset && static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(recordId);
                }
            }

        }

        void AppendOverlayMatches_NoLock(
            const QueryPlan& plan,
            SearchFilter filter,
            const std::wstring& pathPrefix,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched) const
        {
            if (overlayRecords_.empty())
            {
                return;
            }

            std::vector<std::uint32_t> candidates;
            bool candidatesPathScoped = false;
            std::uint64_t pathScopeKey = 0;
            if (!pathPrefix.empty()
                && TryBuildOverlayPathScopedCandidates_NoLock(plan, pathPrefix, candidates, pathScopeKey))
            {
                candidatesPathScoped = true;
                AppendOverlayCandidateMatches_NoLock(
                    plan,
                    filter,
                    pathPrefix,
                    candidatesPathScoped,
                    pathScopeKey,
                    offset,
                    maxResults,
                    candidates,
                    page,
                    totalMatched);
                return;
            }

            if (TryBuildOverlayCandidates_NoLock(plan, candidates))
            {
                AppendOverlayCandidateMatches_NoLock(
                    plan,
                    filter,
                    pathPrefix,
                    candidatesPathScoped,
                    pathScopeKey,
                    offset,
                    maxResults,
                    candidates,
                    page,
                    totalMatched);
                return;
            }

            std::wregex regex;
            const bool needsRegex = plan.kind == QueryPlanKind::WildcardLiteral
                || plan.kind == QueryPlanKind::RegexLiteral
                || plan.kind == QueryPlanKind::RegexScan;
            if (needsRegex)
            {
                try
                {
                    regex = plan.kind == QueryPlanKind::WildcardLiteral
                        ? std::wregex(WildcardToRegex(plan.pattern), std::regex_constants::icase)
                        : std::wregex(plan.pattern, std::regex_constants::icase);
                }
                catch (const std::regex_error&)
                {
                    return;
                }
            }

            for (std::uint32_t i = 0; i < overlayRecords_.size(); ++i)
            {
                const auto& record = overlayRecords_[i];
                const auto key = MakeRecordKey(record.driveLetter, record.frn);
                const auto liveOverlay = overlayIndexByKey_.find(key);
                if (liveOverlay == overlayIndexByKey_.end() || liveOverlay->second != i)
                {
                    continue;
                }

                if (overlayDeletedKeys_.find(key) != overlayDeletedKeys_.end())
                {
                    continue;
                }

                if (!RecordPassesFilter(record, filter))
                {
                    continue;
                }

                const auto recordId = MakeOverlayRecordId(i);
                if (!pathPrefix.empty() && !RecordPassesPathScope_NoLock(recordId, pathPrefix))
                {
                    continue;
                }

                bool matched = false;
                switch (plan.kind)
                {
                case QueryPlanKind::Contains:
                    matched = ContainsText(record.lowerName, plan.query);
                    break;
                case QueryPlanKind::Prefix:
                    matched = StartsWith(record.lowerName, plan.query);
                    break;
                case QueryPlanKind::Suffix:
                    matched = EndsWith(record.lowerName, plan.query);
                    break;
                case QueryPlanKind::ExtensionWildcard:
                    matched = GetExtension(record.lowerName) == plan.extension;
                    break;
                case QueryPlanKind::WildcardLiteral:
                case QueryPlanKind::RegexLiteral:
                    if (ContainsText(record.lowerName, plan.requiredLiteral))
                    {
                        try
                        {
                            matched = std::regex_search(record.lowerName, regex);
                        }
                        catch (const std::regex_error&)
                        {
                            matched = false;
                        }
                    }
                    break;
                case QueryPlanKind::RegexScan:
                    try
                    {
                        matched = std::regex_search(record.lowerName, regex);
                    }
                    catch (const std::regex_error&)
                    {
                        matched = false;
                    }
                    break;
                default:
                    matched = false;
                    break;
                }

                if (!matched)
                {
                    continue;
                }

                ++totalMatched;
                if (totalMatched > offset && static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(recordId);
                }
            }

        }

        bool TryBuildOverlayCandidates_NoLock(const QueryPlan& plan, std::vector<std::uint32_t>& candidates) const
        {
            candidates.clear();
            if (!overlaySearch_.ready)
            {
                return false;
            }

            switch (plan.kind)
            {
            case QueryPlanKind::Contains:
            case QueryPlanKind::Prefix:
            case QueryPlanKind::Suffix:
                return TryBuildOverlayLiteralCandidates_NoLock(plan.query, candidates);

            case QueryPlanKind::ExtensionWildcard:
            {
                const auto it = overlaySearch_.extensionPostings.find(plan.extension);
                if (it != overlaySearch_.extensionPostings.end())
                {
                    candidates = it->second;
                }
                return true;
            }

            case QueryPlanKind::WildcardLiteral:
            case QueryPlanKind::RegexLiteral:
                return TryBuildOverlayLiteralCandidates_NoLock(plan.requiredLiteral, candidates);

            default:
                return false;
            }
        }

        bool TryBuildOverlayPathScopedCandidates_NoLock(
            const QueryPlan& plan,
            const std::wstring& pathPrefix,
            std::vector<std::uint32_t>& candidates,
            std::uint64_t& pathScopeKey) const
        {
            candidates.clear();
            pathScopeKey = 0;
            if (!overlaySearch_.ready || pathPrefix.empty())
            {
                return false;
            }

            if (!TryResolvePathScopeKey_NoLock(pathPrefix, pathScopeKey))
            {
                return false;
            }

            const auto scopeIt = overlaySearch_.parentFrnPostings.find(pathScopeKey);
            if (scopeIt == overlaySearch_.parentFrnPostings.end() || scopeIt->second.empty())
            {
                return true;
            }

            std::vector<std::uint32_t> literalCandidates;
            if (!TryBuildOverlayCandidates_NoLock(plan, literalCandidates))
            {
                return false;
            }

            const auto* scopeCandidates = &scopeIt->second;
            std::vector<std::uint32_t> sortedScopeCandidates;
            if (overlayStalePostingEstimate_ != 0)
            {
                std::sort(literalCandidates.begin(), literalCandidates.end());
                literalCandidates.erase(std::unique(literalCandidates.begin(), literalCandidates.end()), literalCandidates.end());
                sortedScopeCandidates = scopeIt->second;
                std::sort(sortedScopeCandidates.begin(), sortedScopeCandidates.end());
                sortedScopeCandidates.erase(std::unique(sortedScopeCandidates.begin(), sortedScopeCandidates.end()), sortedScopeCandidates.end());
                scopeCandidates = &sortedScopeCandidates;
            }

            candidates.reserve(std::min(literalCandidates.size(), scopeCandidates->size()));
            std::set_intersection(
                literalCandidates.begin(),
                literalCandidates.end(),
                scopeCandidates->begin(),
                scopeCandidates->end(),
                std::back_inserter(candidates));
            candidates.erase(std::unique(candidates.begin(), candidates.end()), candidates.end());
            return true;
        }

        bool TryBuildOverlayLiteralCandidates_NoLock(const std::wstring& literal, std::vector<std::uint32_t>& candidates) const
        {
            candidates.clear();
            if (literal.empty())
            {
                return false;
            }

            if (literal.length() == 1)
            {
                const auto ch = literal[0];
                if (ch >= 0 && ch < 128)
                {
                    candidates = overlaySearch_.asciiCharPostings[static_cast<std::size_t>(ch)];
                }
                else
                {
                    const auto it = overlaySearch_.nonAsciiShortPostings.find(PackShortToken(ch, 0, 1));
                    if (it != overlaySearch_.nonAsciiShortPostings.end())
                    {
                        candidates = it->second;
                    }
                }

                return true;
            }

            if (literal.length() == 2)
            {
                const auto first = literal[0];
                const auto second = literal[1];
                if (first >= 0 && first < 128 && second >= 0 && second < 128)
                {
                    const auto index = (static_cast<std::size_t>(first) << 7) | static_cast<std::size_t>(second);
                    candidates = overlaySearch_.asciiBigramPostings[index];
                }
                else
                {
                    const auto it = overlaySearch_.nonAsciiShortPostings.find(PackShortToken(first, second, 2));
                    if (it != overlaySearch_.nonAsciiShortPostings.end())
                    {
                        candidates = it->second;
                    }
                }

                return true;
            }

            std::vector<std::uint64_t> keys;
            keys.reserve(literal.length() - 2);
            for (std::size_t i = 0; i + 2 < literal.length(); ++i)
            {
                keys.push_back(PackTrigram(literal[i], literal[i + 1], literal[i + 2]));
            }

            std::sort(keys.begin(), keys.end());
            keys.erase(std::unique(keys.begin(), keys.end()), keys.end());

            const std::vector<std::uint32_t>* best = nullptr;
            for (const auto key : keys)
            {
                const auto it = overlaySearch_.trigramPostings.find(key);
                if (it == overlaySearch_.trigramPostings.end() || it->second.empty())
                {
                    return true;
                }

                if (best == nullptr || it->second.size() < best->size())
                {
                    best = &it->second;
                }
            }

            if (best == nullptr)
            {
                return false;
            }

            candidates = *best;
            if (keys.size() <= 1)
            {
                return true;
            }

            candidates.erase(
                std::remove_if(candidates.begin(), candidates.end(), [this, &literal](std::uint32_t overlayIndex)
                {
                    return overlayIndex >= overlayRecords_.size()
                        || !ContainsText(overlayRecords_[overlayIndex].lowerName, literal);
                }),
                candidates.end());
            return true;
        }

        void AppendOverlayCandidateMatches_NoLock(
            const QueryPlan& plan,
            SearchFilter filter,
            const std::wstring& pathPrefix,
            bool candidatesPathScoped,
            std::uint64_t pathScopeKey,
            int offset,
            int maxResults,
            const std::vector<std::uint32_t>& candidates,
            std::vector<std::uint32_t>& page,
            int& totalMatched) const
        {
            std::wregex regex;
            const bool needsRegex = plan.kind == QueryPlanKind::WildcardLiteral
                || plan.kind == QueryPlanKind::RegexLiteral;
            if (needsRegex)
            {
                try
                {
                    regex = plan.kind == QueryPlanKind::WildcardLiteral
                        ? std::wregex(WildcardToRegex(plan.pattern), std::regex_constants::icase)
                        : std::wregex(plan.pattern, std::regex_constants::icase);
                }
                catch (const std::regex_error&)
                {
                    return;
                }
            }

            for (const auto overlayIndex : candidates)
            {
                if (!OverlayRecordIsLive_NoLock(overlayIndex))
                {
                    continue;
                }

                const auto& record = overlayRecords_[overlayIndex];
                if (!RecordPassesFilter(record, filter))
                {
                    continue;
                }

                const auto recordId = MakeOverlayRecordId(overlayIndex);
                if (!pathPrefix.empty())
                {
                    const bool pathMatched = candidatesPathScoped
                        ? (overlayStalePostingEstimate_ == 0
                            || OverlayRecordPassesPathScopeKey_NoLock(record, pathScopeKey))
                        : RecordPassesPathScope_NoLock(recordId, pathPrefix);
                    if (!pathMatched)
                    {
                        continue;
                    }
                }

                bool matched = false;
                switch (plan.kind)
                {
                case QueryPlanKind::Contains:
                    matched = ContainsText(record.lowerName, plan.query);
                    break;
                case QueryPlanKind::Prefix:
                    matched = StartsWith(record.lowerName, plan.query);
                    break;
                case QueryPlanKind::Suffix:
                    matched = EndsWith(record.lowerName, plan.query);
                    break;
                case QueryPlanKind::ExtensionWildcard:
                    matched = GetExtension(record.lowerName) == plan.extension;
                    break;
                case QueryPlanKind::WildcardLiteral:
                case QueryPlanKind::RegexLiteral:
                    try
                    {
                        matched = std::regex_search(record.lowerName, regex);
                    }
                    catch (const std::regex_error&)
                    {
                        matched = false;
                    }
                    break;
                default:
                    matched = false;
                    break;
                }

                if (!matched)
                {
                    continue;
                }

                ++totalMatched;
                if (totalMatched > offset && static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(recordId);
                }
            }
        }

        bool TryMatchRecordsV2_NoLock(
            MatchMode mode,
            const std::wstring& query,
            SearchFilter filter,
            const std::wstring& pathPrefix,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched,
            const char*& acceleratorMode) const
        {
            page.clear();
            totalMatched = 0;
            acceleratorMode = "none";
            if (!v2_.ready || query.empty())
            {
                return false;
            }

            if (mode != MatchMode::Contains)
            {
                return false;
            }

            if (pathPrefix.empty() && filter == SearchFilter::All && query.length() <= 2)
            {
                if (TryMatchShortQueryPage_NoLock(
                    query,
                    offset,
                    maxResults,
                    page,
                    totalMatched,
                    acceleratorMode))
                {
                    return true;
                }
            }

            if (!pathPrefix.empty() && query.length() <= 2)
            {
                if (!TryMatchPathScopedShortQuery_NoLock(
                    query,
                    filter,
                    pathPrefix,
                    offset,
                    maxResults,
                    page,
                    totalMatched,
                    acceleratorMode))
                {
                    return false;
                }

                return true;
            }

            auto candidates = BuildV2ContainsCandidates_NoLock(query);
            if (!candidates.accelerated)
            {
                return false;
            }

            acceleratorMode = candidates.mode;
            ApplyFilterToCandidates_NoLock(candidates.ids, filter);
            if (!pathPrefix.empty())
            {
                ApplyPathScopeToCandidates_NoLock(candidates.ids, pathPrefix);
                acceleratorMode = query.length() == 1 ? "v2-short-char+path"
                    : (query.length() == 2 ? "v2-short-bigram+path" : "v2-trigram+path");
            }

            const bool requiresContainsVerify = query.length() > 3;
            page.reserve(static_cast<std::size_t>(std::min(maxResults, 64)));
            for (const auto recordId : candidates.ids)
            {
                if (recordId >= GetRecordCount_NoLock())
                {
                    continue;
                }

                if (requiresContainsVerify && !ContainsText(GetLowerNameView_NoLock(recordId), query))
                {
                    continue;
                }

                ++totalMatched;
                if (totalMatched > offset && static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(recordId);
                }
            }

            return true;
        }

        bool TryMatchShortQueryPage_NoLock(
            const std::wstring& query,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched,
            const char*& acceleratorMode) const
        {
            page.clear();
            totalMatched = 0;
            if (query.length() == 1)
            {
                const auto ch = query[0];
                const NativeV2ShortBucketEntry* entry = nullptr;
                const std::vector<std::uint8_t>* bytes = nullptr;
                if (ch >= 0 && ch < 128)
                {
                    entry = &v2_.asciiCharEntries[static_cast<std::size_t>(ch)];
                    bytes = &v2_.asciiCharCompressedPostings;
                }
                else
                {
                    const auto it = v2_.nonAsciiShortEntries.find(PackShortToken(ch, 0, 1));
                    if (it != v2_.nonAsciiShortEntries.end())
                    {
                        entry = &it->second;
                        bytes = &v2_.nonAsciiShortCompressedPostings;
                    }
                }

                acceleratorMode = "v2-short-char-page";
                if (entry == nullptr || bytes == nullptr)
                {
                    return true;
                }

                totalMatched = static_cast<int>(entry->count);
                DecodeShortPostingPage_NoLock(*bytes, *entry, offset, maxResults, page);
                return true;
            }

            if (query.length() == 2)
            {
                const auto first = query[0];
                const auto second = query[1];
                const NativeV2ShortBucketEntry* entry = nullptr;
                const std::vector<std::uint8_t>* bytes = nullptr;
                if (first >= 0 && first < 128 && second >= 0 && second < 128)
                {
                    const auto index = (static_cast<std::size_t>(first) << 7) | static_cast<std::size_t>(second);
                    entry = &v2_.asciiBigramEntries[index];
                    bytes = &v2_.asciiBigramCompressedPostings;
                }
                else
                {
                    const auto it = v2_.nonAsciiShortEntries.find(PackShortToken(first, second, 2));
                    if (it != v2_.nonAsciiShortEntries.end())
                    {
                        entry = &it->second;
                        bytes = &v2_.nonAsciiShortCompressedPostings;
                    }
                }

                acceleratorMode = "v2-short-bigram-page";
                if (entry == nullptr || bytes == nullptr)
                {
                    return true;
                }

                totalMatched = static_cast<int>(entry->count);
                DecodeShortPostingPage_NoLock(*bytes, *entry, offset, maxResults, page);
                return true;
            }

            return false;
        }

        bool TryMatchPathScopedShortQuery_NoLock(
            const std::wstring& query,
            SearchFilter filter,
            const std::wstring& pathPrefix,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched,
            const char*& acceleratorMode) const
        {
            page.clear();
            totalMatched = 0;
            const auto normalizedPath = EnsureTrailingSlash(pathPrefix);
            if (query.empty() || query.length() > 2)
            {
                return false;
            }

            const NativeV2ShortBucketEntry* entry = nullptr;
            const std::vector<std::uint8_t>* bytes = nullptr;
            if (!TryGetShortPostingEntry_NoLock(query, entry, bytes))
            {
                acceleratorMode = query.length() == 1 ? "path-subtree-short-char" : "path-subtree-short-bigram";
                return true;
            }

            if (IsDriveRootPath(normalizedPath))
            {
                if (!TryMatchShortQueryRootDrive_NoLock(
                    query,
                    filter,
                    static_cast<wchar_t>(towupper(normalizedPath[0])),
                    offset,
                    maxResults,
                    page,
                    totalMatched))
                {
                    return false;
                }

                acceleratorMode = query.length() == 1 ? "v2-short-char+root-drive" : "v2-short-bigram+root-drive";
                return true;
            }

            std::uint32_t directoryId = std::numeric_limits<std::uint32_t>::max();
            if (!TryResolvePathScopeDirectoryId_NoLock(normalizedPath, directoryId))
            {
                acceleratorMode = query.length() == 1 ? "path-subtree-short-char" : "path-subtree-short-bigram";
                return true;
            }

            if (entry == nullptr || bytes == nullptr || entry->count == 0)
            {
                acceleratorMode = query.length() == 1 ? "path-subtree-short-char" : "path-subtree-short-bigram";
                return true;
            }

            CountAndBuildPathScopedShortQueryPage_NoLock(
                query,
                *bytes,
                *entry,
                directoryId,
                filter,
                offset,
                maxResults,
                page,
                totalMatched);
            acceleratorMode = query.length() == 1 ? "path-subtree-short-char" : "path-subtree-short-bigram";
            return true;
        }

        bool TryGetShortPostingEntry_NoLock(
            const std::wstring& query,
            const NativeV2ShortBucketEntry*& entry,
            const std::vector<std::uint8_t>*& bytes) const
        {
            entry = nullptr;
            bytes = nullptr;
            if (query.length() == 1)
            {
                const auto ch = query[0];
                if (ch >= 0 && ch < 128)
                {
                    entry = &v2_.asciiCharEntries[static_cast<std::size_t>(ch)];
                    bytes = &v2_.asciiCharCompressedPostings;
                    return true;
                }

                const auto it = v2_.nonAsciiShortEntries.find(PackShortToken(ch, 0, 1));
                if (it != v2_.nonAsciiShortEntries.end())
                {
                    entry = &it->second;
                    bytes = &v2_.nonAsciiShortCompressedPostings;
                }

                return true;
            }

            if (query.length() == 2)
            {
                const auto first = query[0];
                const auto second = query[1];
                if (first >= 0 && first < 128 && second >= 0 && second < 128)
                {
                    const auto index = (static_cast<std::size_t>(first) << 7) | static_cast<std::size_t>(second);
                    entry = &v2_.asciiBigramEntries[index];
                    bytes = &v2_.asciiBigramCompressedPostings;
                    return true;
                }

                const auto it = v2_.nonAsciiShortEntries.find(PackShortToken(first, second, 2));
                if (it != v2_.nonAsciiShortEntries.end())
                {
                    entry = &it->second;
                    bytes = &v2_.nonAsciiShortCompressedPostings;
                }

                return true;
            }

            return false;
        }

        void ApplyDriveScopeToCandidates_NoLock(std::vector<std::uint32_t>& ids, wchar_t drive) const
        {
            ids.erase(
                std::remove_if(ids.begin(), ids.end(), [this, drive](std::uint32_t recordId)
                {
                    return recordId >= GetRecordCount_NoLock()
                        || static_cast<wchar_t>(towupper(GetRecordDrive_NoLock(recordId))) != drive;
                }),
                ids.end());
        }

        bool TryMatchShortQueryRootDrive_NoLock(
            const std::wstring& query,
            SearchFilter filter,
            wchar_t drive,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched) const
        {
            page.clear();
            totalMatched = 0;
            if (query.empty() || query.length() > 2)
            {
                return false;
            }

            const NativeV2ShortBucketEntry* entry = nullptr;
            const std::vector<std::uint8_t>* bytes = nullptr;
            if (query.length() == 1)
            {
                const auto ch = query[0];
                if (ch >= 0 && ch < 128)
                {
                    entry = &v2_.asciiCharEntries[static_cast<std::size_t>(ch)];
                    bytes = &v2_.asciiCharCompressedPostings;
                }
                else
                {
                    const auto it = v2_.nonAsciiShortEntries.find(PackShortToken(ch, 0, 1));
                    if (it != v2_.nonAsciiShortEntries.end())
                    {
                        entry = &it->second;
                        bytes = &v2_.nonAsciiShortCompressedPostings;
                    }
                }
            }
            else
            {
                const auto first = query[0];
                const auto second = query[1];
                if (first >= 0 && first < 128 && second >= 0 && second < 128)
                {
                    const auto index = (static_cast<std::size_t>(first) << 7) | static_cast<std::size_t>(second);
                    entry = &v2_.asciiBigramEntries[index];
                    bytes = &v2_.asciiBigramCompressedPostings;
                }
                else
                {
                    const auto it = v2_.nonAsciiShortEntries.find(PackShortToken(first, second, 2));
                    if (it != v2_.nonAsciiShortEntries.end())
                    {
                        entry = &it->second;
                        bytes = &v2_.nonAsciiShortCompressedPostings;
                    }
                }
            }

            if (entry == nullptr || bytes == nullptr || entry->count == 0 || maxResults <= 0)
            {
                return true;
            }

            page.reserve(static_cast<std::size_t>(std::min(maxResults, 64)));
            const auto safeOffset = std::max(offset, 0);
            const auto driveIndex = static_cast<int>(towupper(drive) - L'A');
            const auto canUseDriveCount = driveIndex >= 0 && driveIndex < 26 && filter == SearchFilter::All
                && !v2_.asciiCharDriveCounts.empty()
                && !v2_.asciiBigramDriveCounts.empty()
                && ((query.length() == 1 && query[0] >= 0 && query[0] < 128)
                    || (query.length() == 2 && query[0] >= 0 && query[0] < 128 && query[1] >= 0 && query[1] < 128));
            if (canUseDriveCount)
            {
                if (query.length() == 1)
                {
                    if (static_cast<std::size_t>(query[0]) >= v2_.asciiCharDriveCounts.size())
                    {
                        return true;
                    }

                    totalMatched = static_cast<int>(v2_.asciiCharDriveCounts[static_cast<std::size_t>(query[0])][static_cast<std::size_t>(driveIndex)]);
                    if (TryCopyRootDriveShortQueryPage_NoLock(
                        v2_.asciiCharDrivePages,
                        static_cast<std::size_t>(query[0]),
                        static_cast<std::size_t>(driveIndex),
                        safeOffset,
                        maxResults,
                        page))
                    {
                        return true;
                    }
                }
                else
                {
                    const auto token = (static_cast<std::size_t>(query[0]) << 7) | static_cast<std::size_t>(query[1]);
                    if (token >= v2_.asciiBigramDriveCounts.size())
                    {
                        return true;
                    }

                    totalMatched = static_cast<int>(v2_.asciiBigramDriveCounts[token][static_cast<std::size_t>(driveIndex)]);
                    if (TryCopyRootDriveShortQueryPage_NoLock(
                        v2_.asciiBigramDrivePages,
                        token,
                        static_cast<std::size_t>(driveIndex),
                        safeOffset,
                        maxResults,
                        page))
                    {
                        return true;
                    }
                }

                const auto* driveIds = GetDriveIds_NoLock(drive);
                if (driveIds == nullptr || driveIds->empty())
                {
                    return true;
                }

                BuildRootDriveShortQueryPageFromPosting_NoLock(
                    *bytes,
                    *entry,
                    *driveIds,
                    safeOffset,
                    maxResults,
                    page);
                return true;
            }

            const auto* driveIds = GetDriveIds_NoLock(drive);
            if (driveIds == nullptr || driveIds->empty())
            {
                return true;
            }

            if (filter == SearchFilter::All)
            {
                CountAndBuildRootDriveShortQueryPageFromPosting_NoLock(
                    *bytes,
                    *entry,
                    *driveIds,
                    safeOffset,
                    maxResults,
                    page,
                    totalMatched);
                return true;
            }

            return TryScanShortPostingForRootDrive_NoLock(
                *bytes,
                *entry,
                *driveIds,
                filter,
                safeOffset,
                maxResults,
                page,
                totalMatched);
        }

        void CountAndBuildRootDriveShortQueryPageFromPosting_NoLock(
            const std::vector<std::uint8_t>& bytes,
            const NativeV2ShortBucketEntry& entry,
            const std::vector<std::uint32_t>& driveIds,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched) const
        {
            page.clear();
            totalMatched = 0;
            if (entry.count == 0 || driveIds.empty())
            {
                return;
            }

            page.reserve(static_cast<std::size_t>(std::min(maxResults, 64)));
            std::size_t byteOffset = entry.offset;
            const std::size_t end = static_cast<std::size_t>(entry.offset) + entry.byteCount;
            std::uint32_t previous = 0;
            std::size_t driveIndex = 0;
            std::uint64_t matchedCount = 0;
            const auto safeOffset = std::max(offset, 0);
            auto matchedBeforePage = 0;
            for (std::uint32_t i = 0; i < entry.count && byteOffset < end; ++i)
            {
                std::uint32_t delta = 0;
                if (!ReadVarUInt(bytes, byteOffset, end, delta))
                {
                    page.clear();
                    totalMatched = 0;
                    return;
                }

                const auto recordId = i == 0 ? delta : previous + delta;
                previous = recordId;
                while (driveIndex < driveIds.size() && driveIds[driveIndex] < recordId)
                {
                    ++driveIndex;
                }

                if (driveIndex >= driveIds.size())
                {
                    break;
                }

                if (driveIds[driveIndex] == recordId
                    && recordId < GetRecordCount_NoLock()
                    && !IsRecordSuppressedByOverlay_NoLock(recordId))
                {
                    ++matchedCount;
                    if (matchedBeforePage++ >= safeOffset && static_cast<int>(page.size()) < maxResults)
                    {
                        page.push_back(recordId);
                    }
                }
            }

            totalMatched = static_cast<int>(std::min<std::uint64_t>(
                matchedCount,
                static_cast<std::uint64_t>(std::numeric_limits<int>::max())));
        }

        void CountAndBuildPathScopedShortQueryPage_NoLock(
            const std::wstring& query,
            const std::vector<std::uint8_t>& bytes,
            const NativeV2ShortBucketEntry& entry,
            std::uint32_t directoryId,
            SearchFilter filter,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched) const
        {
            const auto begin = directoryId < v2_.subtreeEnter.size()
                ? v2_.subtreeEnter[directoryId]
                : std::numeric_limits<std::uint32_t>::max();
            const auto end = directoryId < v2_.subtreeExit.size()
                ? v2_.subtreeExit[directoryId]
                : std::numeric_limits<std::uint32_t>::max();
            const auto subtreeCount = begin != std::numeric_limits<std::uint32_t>::max()
                && end != std::numeric_limits<std::uint32_t>::max()
                && end >= begin
                ? end - begin
                : std::numeric_limits<std::uint32_t>::max();

            if (subtreeCount != std::numeric_limits<std::uint32_t>::max()
                && subtreeCount <= 100000
                && subtreeCount < entry.count
                && static_cast<std::size_t>(end) <= v2_.subtreeRecordIds.size())
            {
                CountAndBuildPathScopedShortQueryPageFromSubtree_NoLock(
                    query,
                    directoryId,
                    begin,
                    end,
                    filter,
                    offset,
                    maxResults,
                    page,
                    totalMatched);
                return;
            }

            CountAndBuildPathScopedShortQueryPageFromPosting_NoLock(
                bytes,
                entry,
                directoryId,
                filter,
                offset,
                maxResults,
                page,
                totalMatched);
        }

        void CountAndBuildPathScopedShortQueryPageFromSubtree_NoLock(
            const std::wstring& query,
            std::uint32_t directoryId,
            std::uint32_t begin,
            std::uint32_t end,
            SearchFilter filter,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched) const
        {
            page.clear();
            totalMatched = 0;
            if (query.empty() || maxResults <= 0 || begin > end || static_cast<std::size_t>(end) > v2_.subtreeRecordIds.size())
            {
                return;
            }

            page.reserve(static_cast<std::size_t>(std::min(maxResults, 64)));
            std::uint64_t matchedCount = 0;
            const auto safeOffset = std::max(offset, 0);
            auto matchedBeforePage = 0;
            for (std::uint32_t ordinal = begin; ordinal < end; ++ordinal)
            {
                const auto recordId = v2_.subtreeRecordIds[ordinal];
                if (recordId == directoryId
                    || recordId >= GetRecordCount_NoLock()
                    || IsRecordSuppressedByOverlay_NoLock(recordId)
                    || !RecordIdPassesFilter_NoLock(recordId, filter)
                    || GetLowerNameView_NoLock(recordId).find(query) == std::wstring_view::npos)
                {
                    continue;
                }

                ++matchedCount;
                if (matchedBeforePage++ >= safeOffset && static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(recordId);
                }
            }

            totalMatched = static_cast<int>(std::min<std::uint64_t>(
                matchedCount,
                static_cast<std::uint64_t>(std::numeric_limits<int>::max())));
        }

        void CountAndBuildPathScopedShortQueryPageFromPosting_NoLock(
            const std::vector<std::uint8_t>& bytes,
            const NativeV2ShortBucketEntry& entry,
            std::uint32_t directoryId,
            SearchFilter filter,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched) const
        {
            page.clear();
            totalMatched = 0;
            if (entry.count == 0 || maxResults <= 0 || directoryId >= GetRecordCount_NoLock())
            {
                return;
            }

            page.reserve(static_cast<std::size_t>(std::min(maxResults, 64)));
            std::size_t byteOffset = entry.offset;
            const std::size_t end = static_cast<std::size_t>(entry.offset) + entry.byteCount;
            std::uint32_t previous = 0;
            std::uint64_t matchedCount = 0;
            const auto safeOffset = std::max(offset, 0);
            auto matchedBeforePage = 0;
            for (std::uint32_t i = 0; i < entry.count && byteOffset < end; ++i)
            {
                std::uint32_t delta = 0;
                if (!ReadVarUInt(bytes, byteOffset, end, delta))
                {
                    page.clear();
                    totalMatched = 0;
                    return;
                }

                const auto recordId = i == 0 ? delta : previous + delta;
                previous = recordId;
                if (recordId < GetRecordCount_NoLock()
                    && !IsRecordSuppressedByOverlay_NoLock(recordId)
                    && RecordIdPassesSubtreeScope_NoLock(recordId, directoryId)
                    && RecordIdPassesFilter_NoLock(recordId, filter))
                {
                    ++matchedCount;
                    if (matchedBeforePage++ >= safeOffset && static_cast<int>(page.size()) < maxResults)
                    {
                        page.push_back(recordId);
                    }
                }
            }

            totalMatched = static_cast<int>(std::min<std::uint64_t>(
                matchedCount,
                static_cast<std::uint64_t>(std::numeric_limits<int>::max())));
        }

        void BuildRootDriveShortQueryPageFromPosting_NoLock(
            const std::vector<std::uint8_t>& bytes,
            const NativeV2ShortBucketEntry& entry,
            const std::vector<std::uint32_t>& driveIds,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page) const
        {
            page.clear();
            if (entry.count == 0 || driveIds.empty() || maxResults <= 0)
            {
                return;
            }

            page.reserve(static_cast<std::size_t>(std::min(maxResults, 64)));
            std::size_t byteOffset = entry.offset;
            const std::size_t end = static_cast<std::size_t>(entry.offset) + entry.byteCount;
            std::uint32_t previous = 0;
            const auto safeOffset = std::max(offset, 0);
            auto matchedBeforePage = 0;
            std::size_t driveIndex = 0;
            for (std::uint32_t i = 0; i < entry.count && byteOffset < end; ++i)
            {
                std::uint32_t delta = 0;
                if (!ReadVarUInt(bytes, byteOffset, end, delta))
                {
                    page.clear();
                    return;
                }

                const auto recordId = i == 0 ? delta : previous + delta;
                previous = recordId;
                while (driveIndex < driveIds.size() && driveIds[driveIndex] < recordId)
                {
                    ++driveIndex;
                }

                if (driveIndex >= driveIds.size())
                {
                    return;
                }

                if (driveIds[driveIndex] != recordId
                    || recordId >= GetRecordCount_NoLock()
                    || IsRecordSuppressedByOverlay_NoLock(recordId))
                {
                    continue;
                }

                if (matchedBeforePage++ < safeOffset)
                {
                    continue;
                }

                page.push_back(recordId);
                if (static_cast<int>(page.size()) >= maxResults)
                {
                    return;
                }
            }
        }

        bool TryCopyRootDriveShortQueryPage_NoLock(
            const std::vector<std::array<std::vector<std::uint32_t>, 26>>& pages,
            std::size_t token,
            std::size_t driveIndex,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page) const
        {
            page.clear();
            if (token >= pages.size()
                || driveIndex >= 26
                || offset < 0
                || maxResults <= 0)
            {
                return false;
            }

            const auto& source = pages[token][driveIndex];
            if (source.empty()
                || static_cast<std::size_t>(offset) >= source.size()
                || static_cast<std::size_t>(offset) + static_cast<std::size_t>(maxResults) > source.size())
            {
                return false;
            }

            const auto begin = source.begin() + offset;
            const auto count = std::min<std::size_t>(static_cast<std::size_t>(maxResults), source.size() - static_cast<std::size_t>(offset));
            page.assign(begin, begin + count);
            return true;
        }

        void BuildRootDriveShortQueryPageFromDriveIds_NoLock(
            const std::wstring& query,
            const std::vector<std::uint32_t>& driveIds,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page) const
        {
            page.clear();
            if (query.empty() || query.length() > 2 || maxResults <= 0)
            {
                return;
            }

            page.reserve(static_cast<std::size_t>(std::min(maxResults, 64)));
            const auto safeOffset = std::max(offset, 0);
            auto matchedBeforePage = 0;
            const std::wstring_view queryView(query.data(), query.length());
            for (const auto recordId : driveIds)
            {
                if (recordId >= GetRecordCount_NoLock() || IsRecordSuppressedByOverlay_NoLock(recordId))
                {
                    continue;
                }

                const auto name = GetLowerNameView_NoLock(recordId);
                const bool matches = query.length() == 1
                    ? name.find(query[0]) != std::wstring_view::npos
                    : name.find(queryView) != std::wstring_view::npos;
                if (!matches)
                {
                    continue;
                }

                if (matchedBeforePage++ < safeOffset)
                {
                    continue;
                }

                page.push_back(recordId);
                if (static_cast<int>(page.size()) >= maxResults)
                {
                    return;
                }
            }
        }

        bool TryScanShortPostingForRootDrive_NoLock(
            const std::vector<std::uint8_t>& bytes,
            const NativeV2ShortBucketEntry& entry,
            const std::vector<std::uint32_t>& driveIds,
            SearchFilter filter,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched) const
        {
            page.clear();
            totalMatched = 0;
            if (entry.count == 0 || maxResults <= 0)
            {
                return true;
            }

            page.reserve(static_cast<std::size_t>(std::min(maxResults, 64)));
            std::size_t byteOffset = entry.offset;
            const std::size_t end = static_cast<std::size_t>(entry.offset) + entry.byteCount;
            std::uint32_t previous = 0;
            const auto safeOffset = std::max(offset, 0);
            auto matchedBeforePage = 0;
            std::uint64_t matchedCount = 0;
            std::size_t driveIndex = 0;
            for (std::uint32_t i = 0; i < entry.count && byteOffset < end; ++i)
            {
                std::uint32_t delta = 0;
                if (!ReadVarUInt(bytes, byteOffset, end, delta))
                {
                    page.clear();
                    totalMatched = 0;
                    return true;
                }

                const auto recordId = i == 0 ? delta : previous + delta;
                previous = recordId;
                while (driveIndex < driveIds.size() && driveIds[driveIndex] < recordId)
                {
                    ++driveIndex;
                }

                if (driveIndex >= driveIds.size())
                {
                    break;
                }

                if (driveIds[driveIndex] != recordId
                    || recordId >= GetRecordCount_NoLock()
                    || IsRecordSuppressedByOverlay_NoLock(recordId)
                    || !RecordIdPassesFilter_NoLock(recordId, filter))
                {
                    continue;
                }

                ++matchedCount;
                if (matchedBeforePage++ < safeOffset)
                {
                    continue;
                }

                if (static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(recordId);
                }
            }

            totalMatched = static_cast<int>(std::min<std::uint64_t>(
                matchedCount,
                static_cast<std::uint64_t>(std::numeric_limits<int>::max())));
            return true;
        }

        NativeV2CandidateResult BuildV2ContainsCandidates_NoLock(const std::wstring& query) const
        {
            NativeV2CandidateResult result;
            if (query.length() == 1)
            {
                const auto ch = query[0];
                if (ch >= 0 && ch < 128)
                {
                    DecodeShortPosting_NoLock(
                        v2_.asciiCharCompressedPostings,
                        v2_.asciiCharEntries[static_cast<std::size_t>(ch)],
                        result.ids);
                }
                else
                {
                    const auto it = v2_.nonAsciiShortEntries.find(PackShortToken(ch, 0, 1));
                    if (it != v2_.nonAsciiShortEntries.end())
                    {
                        DecodeShortPosting_NoLock(v2_.nonAsciiShortCompressedPostings, it->second, result.ids);
                    }
                }

                result.mode = "v2-short-char";
                result.accelerated = true;
                return result;
            }

            if (query.length() == 2)
            {
                const auto first = query[0];
                const auto second = query[1];
                if (first >= 0 && first < 128 && second >= 0 && second < 128)
                {
                    const auto index = (static_cast<std::size_t>(first) << 7) | static_cast<std::size_t>(second);
                    DecodeShortPosting_NoLock(
                        v2_.asciiBigramCompressedPostings,
                        v2_.asciiBigramEntries[index],
                        result.ids);
                }
                else
                {
                    const auto it = v2_.nonAsciiShortEntries.find(PackShortToken(first, second, 2));
                    if (it != v2_.nonAsciiShortEntries.end())
                    {
                        DecodeShortPosting_NoLock(v2_.nonAsciiShortCompressedPostings, it->second, result.ids);
                    }
                }

                result.mode = "v2-short-bigram";
                result.accelerated = true;
                return result;
            }

            std::vector<std::uint64_t> keys;
            keys.reserve(query.length() - 2);
            for (std::size_t i = 0; i + 2 < query.length(); ++i)
            {
                keys.push_back(PackTrigram(query[i], query[i + 1], query[i + 2]));
            }

            std::sort(keys.begin(), keys.end());
            keys.erase(std::unique(keys.begin(), keys.end()), keys.end());
            NativeV2BucketEntry bestEntry;
            bool hasBestEntry = false;
            for (const auto key : keys)
            {
                NativeV2BucketEntry entry;
                if (!TryGetTrigramPosting_NoLock(key, entry) || entry.count == 0)
                {
                    result.mode = "v2-trigram";
                    result.accelerated = true;
                    return result;
                }

                if (!hasBestEntry || entry.count < bestEntry.count)
                {
                    bestEntry = entry;
                    hasBestEntry = true;
                }
            }

            if (hasBestEntry)
            {
                DecodeDeltaPosting_NoLock(v2_.trigramPostings, bestEntry, result.ids);
            }

            std::vector<std::uint32_t> decodedPosting;
            for (const auto key : keys)
            {
                NativeV2BucketEntry entry;
                if (!TryGetTrigramPosting_NoLock(key, entry) || entry.count == 0)
                {
                    result.ids.clear();
                    break;
                }

                if (entry.key == bestEntry.key)
                {
                    continue;
                }

                DecodeDeltaPosting_NoLock(v2_.trigramPostings, entry, decodedPosting);
                IntersectWithPosting_NoLock(result.ids, decodedPosting, 0, static_cast<std::uint32_t>(decodedPosting.size()));
                if (result.ids.empty())
                {
                    break;
                }
            }

            result.mode = "v2-trigram";
            result.accelerated = true;
            return result;
        }

        bool TryBuildLiteralCandidates_NoLock(const std::wstring& literal, std::vector<std::uint32_t>& ids) const
        {
            ids.clear();
            if (literal.empty())
            {
                return false;
            }

            auto candidates = BuildV2ContainsCandidates_NoLock(literal);
            if (!candidates.accelerated)
            {
                return false;
            }

            ids = std::move(candidates.ids);
            return true;
        }

        bool TryBuildPrefix4Candidates_NoLock(const std::wstring& prefix, std::vector<std::uint32_t>& ids) const
        {
            ids.clear();
            if (prefix.length() < 4 || v2_.prefix4Entries.empty())
            {
                return false;
            }

            NativeV2BucketEntry entry;
            if (!TryGetPrefix4Posting_NoLock(PackPrefix4(prefix), entry))
            {
                return true;
            }

            DecodeDeltaPosting_NoLock(v2_.prefix4Postings, entry, ids);
            return ids.size() == entry.count;
        }

        bool TryGetPrefix4Posting_NoLock(std::uint64_t key, NativeV2BucketEntry& entry) const
        {
            auto lo = v2_.prefix4Entries.begin();
            auto hi = v2_.prefix4Entries.end();
            while (lo < hi)
            {
                auto mid = lo + ((hi - lo) / 2);
                if (mid->key < key)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            if (lo == v2_.prefix4Entries.end() || lo->key != key)
            {
                return false;
            }

            entry = *lo;
            return true;
        }

        bool TryGetTrigramPosting_NoLock(std::uint64_t key, NativeV2BucketEntry& entry) const
        {
            auto lo = v2_.trigramEntries.begin();
            auto hi = v2_.trigramEntries.end();
            while (lo < hi)
            {
                auto mid = lo + ((hi - lo) / 2);
                if (mid->key < key)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            if (lo == v2_.trigramEntries.end() || lo->key != key)
            {
                return false;
            }

            entry = *lo;
            return true;
        }

        void IntersectWithPosting_NoLock(
            std::vector<std::uint32_t>& ids,
            const std::vector<std::uint32_t>& posting,
            std::uint32_t offset,
            std::uint32_t count) const
        {
            std::vector<std::uint32_t> intersection;
            intersection.reserve(std::min<std::size_t>(ids.size(), count));
            std::size_t left = 0;
            std::size_t right = offset;
            const std::size_t rightEnd = static_cast<std::size_t>(offset) + count;
            while (left < ids.size() && right < rightEnd)
            {
                const auto leftValue = ids[left];
                const auto rightValue = posting[right];
                if (leftValue == rightValue)
                {
                    intersection.push_back(leftValue);
                    ++left;
                    ++right;
                }
                else if (leftValue < rightValue)
                {
                    ++left;
                }
                else
                {
                    ++right;
                }
            }

            ids.swap(intersection);
        }

        void ApplyFilterToCandidates_NoLock(std::vector<std::uint32_t>& ids, SearchFilter filter) const
        {
            const auto* filterIds = GetCandidateSource(filter);
            if (filterIds == nullptr || filter == SearchFilter::All)
            {
                return;
            }

            IntersectSortedVectors(ids, *filterIds);
        }

        static bool RecordPassesFilter(const Record& record, SearchFilter filter)
        {
            if (filter == SearchFilter::All)
            {
                return true;
            }

            if (filter == SearchFilter::Folder)
            {
                return record.isDirectory;
            }

            if (record.isDirectory)
            {
                return false;
            }

            const auto extension = GetExtension(record.lowerName);
            switch (filter)
            {
            case SearchFilter::Launchable:
                return IsLaunchableExtension(extension);
            case SearchFilter::Script:
                return IsScriptExtension(extension);
            case SearchFilter::Log:
                return IsLogExtension(extension);
            case SearchFilter::Config:
                return IsConfigExtension(extension);
            default:
                return true;
            }
        }

        bool RecordIdPassesFilter_NoLock(std::uint32_t recordId, SearchFilter filter) const
        {
            if (filter == SearchFilter::All)
            {
                return true;
            }

            const auto isDirectory = IsDirectoryRecord_NoLock(recordId);
            if (filter == SearchFilter::Folder)
            {
                return isDirectory;
            }

            if (isDirectory)
            {
                return false;
            }

            const auto extension = GetExtension(std::wstring(GetLowerNameView_NoLock(recordId)));
            switch (filter)
            {
            case SearchFilter::Launchable:
                return IsLaunchableExtension(extension);
            case SearchFilter::Script:
                return IsScriptExtension(extension);
            case SearchFilter::Log:
                return IsLogExtension(extension);
            case SearchFilter::Config:
                return IsConfigExtension(extension);
            default:
                return true;
            }
        }

        void ApplyPathScopeToCandidates_NoLock(std::vector<std::uint32_t>& ids, const std::wstring& pathPrefix) const
        {
            const auto normalized = EnsureTrailingSlash(pathPrefix);
            if (IsDriveRootPath(normalized))
            {
                ApplyDriveScopeToCandidates_NoLock(ids, static_cast<wchar_t>(towupper(normalized[0])));
                return;
            }

            std::uint32_t directoryId = std::numeric_limits<std::uint32_t>::max();
            if (!TryResolvePathScopeDirectoryId_NoLock(normalized, directoryId))
            {
                ids.clear();
                return;
            }

            ids.erase(
                std::remove_if(ids.begin(), ids.end(), [this, directoryId](std::uint32_t recordId)
                {
                    return !RecordIdPassesSubtreeScope_NoLock(recordId, directoryId);
                }),
                ids.end());
        }

        bool RecordPassesPathScope_NoLock(std::uint32_t recordId, const std::wstring& pathPrefix) const
        {
            if (pathPrefix.empty())
            {
                return true;
            }

            const auto normalizedPrefix = EnsureTrailingSlash(pathPrefix);
            std::uint32_t directoryId = std::numeric_limits<std::uint32_t>::max();
            if (TryResolvePathScopeDirectoryId_NoLock(normalizedPrefix, directoryId))
            {
                return RecordIdPassesSubtreeScope_NoLock(recordId, directoryId);
            }

            return StartsWithI(ResolveFullPathById_NoLock(recordId), normalizedPrefix);
        }

        bool TryResolvePathScopeDirectoryId_NoLock(const std::wstring& normalizedPath, std::uint32_t& directoryId) const
        {
            directoryId = std::numeric_limits<std::uint32_t>::max();
            if (normalizedPath.length() < 3 || normalizedPath[1] != L':' || normalizedPath[2] != L'\\')
            {
                return false;
            }

            const wchar_t drive = static_cast<wchar_t>(towupper(normalizedPath[0]));
            if (IsDriveRootPath(normalizedPath))
            {
                if (TryGetRecordIdByKey_NoLock(MakeRecordKey(drive, NtfsRootFileReferenceNumber), directoryId))
                {
                    return true;
                }

                return false;
            }

            directoryId = TryResolveDirectoryRecordId_NoLock(normalizedPath);
            return directoryId != std::numeric_limits<std::uint32_t>::max();
        }

        bool RecordIdPassesSubtreeScope_NoLock(std::uint32_t recordId, std::uint32_t directoryId) const
        {
            if (IsOverlayRecordId(recordId))
            {
                const Record* overlay = nullptr;
                if (!TryGetOverlayRecord_NoLock(recordId, overlay) || overlay == nullptr)
                {
                    return false;
                }

                return OverlayRecordPassesPathScopeKey_NoLock(
                    *overlay,
                    MakeRecordKey(GetRecordDrive_NoLock(directoryId), GetRecordFrn_NoLock(directoryId)));
            }

            if (recordId >= GetRecordCount_NoLock()
                || directoryId >= GetRecordCount_NoLock()
                || recordId >= v2_.subtreeEnter.size()
                || recordId >= v2_.subtreeExit.size()
                || directoryId >= v2_.subtreeEnter.size()
                || directoryId >= v2_.subtreeExit.size())
            {
                return false;
            }

            const auto begin = v2_.subtreeEnter[directoryId];
            const auto end = v2_.subtreeExit[directoryId];
            const auto value = v2_.subtreeEnter[recordId];
            return begin != std::numeric_limits<std::uint32_t>::max()
                && end != std::numeric_limits<std::uint32_t>::max()
                && value >= begin
                && value < end;
        }

        bool OverlayRecordPassesPathScopeKey_NoLock(const Record& record, std::uint64_t pathScopeKey) const
        {
            auto parentFrn = record.parentFrn;
            std::array<std::uint64_t, 256> visited{};
            std::size_t visitedCount = 0;

            for (int depth = 0; depth < 256; ++depth)
            {
                const auto key = MakeRecordKey(record.driveLetter, parentFrn);
                if (key == pathScopeKey)
                {
                    return true;
                }

                if (std::find(visited.begin(), visited.begin() + visitedCount, key) != visited.begin() + visitedCount)
                {
                    break;
                }

                visited[visitedCount++] = key;
                if (parentFrn == 0)
                {
                    break;
                }

                std::uint32_t parentId = 0;
                if (!TryGetRecordIdByKey_NoLock(key, parentId))
                {
                    break;
                }

                const auto nextParentFrn = GetRecordParentFrn_NoLock(parentId);
                if (nextParentFrn == parentFrn)
                {
                    break;
                }

                parentFrn = nextParentFrn;
            }

            return false;
        }

        bool OverlayRecordIsLive_NoLock(std::uint32_t overlayIndex) const
        {
            if (overlayIndex >= overlayRecords_.size())
            {
                return false;
            }

            const auto& record = overlayRecords_[overlayIndex];
            const auto key = MakeRecordKey(record.driveLetter, record.frn);
            const auto liveOverlay = overlayIndexByKey_.find(key);
            return liveOverlay != overlayIndexByKey_.end()
                && liveOverlay->second == overlayIndex
                && overlayDeletedKeys_.find(key) == overlayDeletedKeys_.end();
        }

        bool TryResolvePathScopeKey_NoLock(const std::wstring& pathPrefix, std::uint64_t& key) const
        {
            key = 0;
            auto normalized = EnsureTrailingSlash(pathPrefix);
            if (normalized.length() < 3 || normalized[1] != L':' || normalized[2] != L'\\')
            {
                return false;
            }

            const wchar_t drive = static_cast<wchar_t>(towupper(normalized[0]));
            if (IsDriveRootPath(normalized))
            {
                key = MakeRecordKey(drive, NtfsRootFileReferenceNumber);
                return true;
            }

            const auto directoryId = TryResolveDirectoryRecordId_NoLock(normalized);
            if (directoryId == std::numeric_limits<std::uint32_t>::max())
            {
                return false;
            }

            key = MakeRecordKey(GetRecordDrive_NoLock(directoryId), GetRecordFrn_NoLock(directoryId));
            return true;
        }

        std::uint32_t TryResolveDirectoryRecordId_NoLock(const std::wstring& pathPrefix) const
        {
            auto normalized = EnsureTrailingSlash(pathPrefix);
            if (normalized.length() < 3 || normalized[1] != L':' || normalized[2] != L'\\')
            {
                return std::numeric_limits<std::uint32_t>::max();
            }

            const wchar_t drive = static_cast<wchar_t>(towupper(normalized[0]));
            if (IsDriveRootPath(normalized))
            {
                std::uint32_t rootRecordId = 0;
                return TryGetRecordIdByKey_NoLock(MakeRecordKey(drive, NtfsRootFileReferenceNumber), rootRecordId)
                    ? rootRecordId
                    : std::numeric_limits<std::uint32_t>::max();
            }

            std::uint64_t currentParent = NtfsRootFileReferenceNumber;
            std::uint32_t currentRecordId = std::numeric_limits<std::uint32_t>::max();
            std::size_t segmentStart = 3;
            while (segmentStart < normalized.length())
            {
                auto segmentEnd = normalized.find(L'\\', segmentStart);
                if (segmentEnd == std::wstring::npos)
                {
                    segmentEnd = normalized.length();
                }

                if (segmentEnd == segmentStart)
                {
                    segmentStart = segmentEnd + 1;
                    continue;
                }

                const auto segment = ToLower(normalized.substr(segmentStart, segmentEnd - segmentStart));
                std::uint32_t childOffset = 0;
                std::uint32_t childCount = 0;
                auto hasChildren = TryGetChildRange_NoLock(MakeRecordKey(drive, currentParent), childOffset, childCount);
                if (!hasChildren && currentParent == NtfsRootFileReferenceNumber)
                {
                    hasChildren = TryGetChildRange_NoLock(MakeRecordKey(drive, 0), childOffset, childCount);
                }

                if (!hasChildren)
                {
                    return std::numeric_limits<std::uint32_t>::max();
                }

                bool found = false;
                const auto childEnd = static_cast<std::size_t>(childOffset) + childCount;
                for (std::size_t i = childOffset; i < childEnd && i < v2_.childRecordIds.size(); ++i)
                {
                    const auto childId = v2_.childRecordIds[i];
                    if (childId >= GetRecordCount_NoLock())
                    {
                        continue;
                    }

                    if (IsDirectoryRecord_NoLock(childId) && GetLowerNameView_NoLock(childId) == segment)
                    {
                        currentParent = GetRecordFrn_NoLock(childId);
                        currentRecordId = childId;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return std::numeric_limits<std::uint32_t>::max();
                }

                segmentStart = segmentEnd + 1;
            }

            return currentRecordId;
        }

        bool TryBuildPathScopeRecordIds_NoLock(const std::wstring& pathPrefix, std::vector<std::uint32_t>& ids) const
        {
            ids.clear();
            auto normalized = EnsureTrailingSlash(pathPrefix);
            if (normalized.length() < 3 || normalized[1] != L':' || normalized[2] != L'\\')
            {
                return false;
            }

            const wchar_t drive = static_cast<wchar_t>(towupper(normalized[0]));
            if (IsDriveRootPath(normalized))
            {
                const auto rootKey = MakeRecordKey(drive, NtfsRootFileReferenceNumber);
                if (TryGetCachedPathScopeRecordIds_NoLock(rootKey, ids))
                {
                    return true;
                }

                std::uint32_t rootRecordId = 0;
                if (TryGetRecordIdByKey_NoLock(rootKey, rootRecordId))
                {
                    BuildSubtreeRecordIds_NoLock(rootRecordId, ids);
                    StorePathScopeRecordIdsCache_NoLock(rootKey, ids);
                    return true;
                }

                BuildSubtreeRecordIdsFromParentKey_NoLock(drive, NtfsRootFileReferenceNumber, ids);
                if (ids.empty())
                {
                    BuildSubtreeRecordIdsFromParentKey_NoLock(drive, 0, ids);
                }

                StorePathScopeRecordIdsCache_NoLock(rootKey, ids);
                return true;
            }

            const auto directoryId = TryResolveDirectoryRecordId_NoLock(normalized);
            if (directoryId == std::numeric_limits<std::uint32_t>::max())
            {
                return false;
            }

            const auto directoryKey = MakeRecordKey(GetRecordDrive_NoLock(directoryId), GetRecordFrn_NoLock(directoryId));
            if (TryGetCachedPathScopeRecordIds_NoLock(directoryKey, ids))
            {
                return true;
            }

            BuildSubtreeRecordIds_NoLock(directoryId, ids);
            StorePathScopeRecordIdsCache_NoLock(directoryKey, ids);
            return true;
        }

        void BuildSubtreeRecordIds_NoLock(std::uint32_t directoryId, std::vector<std::uint32_t>& ids) const
        {
            ids.clear();
            if (directoryId >= GetRecordCount_NoLock())
            {
                return;
            }

            std::vector<std::uint32_t> stack;
            stack.push_back(directoryId);
            while (!stack.empty())
            {
                const auto current = stack.back();
                stack.pop_back();
                ids.push_back(current);
                std::uint32_t childOffset = 0;
                std::uint32_t childCount = 0;
                if (!TryGetChildRange_NoLock(MakeRecordKey(GetRecordDrive_NoLock(current), GetRecordFrn_NoLock(current)), childOffset, childCount))
                {
                    continue;
                }

                const auto childEnd = static_cast<std::size_t>(childOffset) + childCount;
                for (std::size_t i = childOffset; i < childEnd && i < v2_.childRecordIds.size(); ++i)
                {
                    const auto childId = v2_.childRecordIds[i];
                    if (childId < GetRecordCount_NoLock())
                    {
                        stack.push_back(childId);
                    }
                }
            }

            std::sort(ids.begin(), ids.end());
            ids.erase(std::unique(ids.begin(), ids.end()), ids.end());
        }

        void BuildSubtreeRecordIdsFromParentKey_NoLock(wchar_t drive, std::uint64_t parentFrn, std::vector<std::uint32_t>& ids) const
        {
            ids.clear();
            std::uint32_t childOffset = 0;
            std::uint32_t childCount = 0;
            if (!TryGetChildRange_NoLock(MakeRecordKey(drive, parentFrn), childOffset, childCount))
            {
                return;
            }

            std::vector<std::uint32_t> stack;
            const auto childEnd = static_cast<std::size_t>(childOffset) + childCount;
            for (std::size_t i = childOffset; i < childEnd && i < v2_.childRecordIds.size(); ++i)
            {
                const auto childId = v2_.childRecordIds[i];
                if (childId < GetRecordCount_NoLock())
                {
                    stack.push_back(childId);
                }
            }

            while (!stack.empty())
            {
                const auto current = stack.back();
                stack.pop_back();
                ids.push_back(current);
                std::uint32_t nestedOffset = 0;
                std::uint32_t nestedCount = 0;
                if (!TryGetChildRange_NoLock(MakeRecordKey(GetRecordDrive_NoLock(current), GetRecordFrn_NoLock(current)), nestedOffset, nestedCount))
                {
                    continue;
                }

                const auto nestedEnd = static_cast<std::size_t>(nestedOffset) + nestedCount;
                for (std::size_t i = nestedOffset; i < nestedEnd && i < v2_.childRecordIds.size(); ++i)
                {
                    const auto childId = v2_.childRecordIds[i];
                    if (childId < GetRecordCount_NoLock())
                    {
                        stack.push_back(childId);
                    }
                }
            }

            std::sort(ids.begin(), ids.end());
            ids.erase(std::unique(ids.begin(), ids.end()), ids.end());
        }

        static void IntersectSortedVectors(std::vector<std::uint32_t>& ids, const std::vector<std::uint32_t>& filterIds)
        {
            std::sort(ids.begin(), ids.end());
            ids.erase(std::unique(ids.begin(), ids.end()), ids.end());

            std::vector<std::uint32_t> intersection;
            intersection.reserve(std::min(ids.size(), filterIds.size()));
            std::set_intersection(
                ids.begin(),
                ids.end(),
                filterIds.begin(),
                filterIds.end(),
                std::back_inserter(intersection));
            ids.swap(intersection);
        }

        void PrefixMatch_NoLock(
            const std::wstring& prefix,
            const std::vector<std::uint32_t>& source,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched) const
        {
            totalMatched = 0;
            if (source.empty())
            {
                return;
            }

            std::size_t lo = 0;
            std::size_t hi = source.size();
            while (lo < hi)
            {
                const auto mid = lo + ((hi - lo) / 2);
                const auto name = GetLowerNameView_NoLock(source[mid]);
                if (name < prefix)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            for (auto i = lo; i < source.size(); ++i)
            {
                if (!StartsWith(GetLowerNameView_NoLock(source[i]), prefix))
                {
                    break;
                }

                ++totalMatched;
                if (totalMatched > offset && static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(source[i]);
                }
            }
        }

        void SuffixMatch_NoLock(
            const std::wstring& suffix,
            const std::vector<std::uint32_t>& source,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched) const
        {
            totalMatched = 0;
            for (const auto recordIndex : source)
            {
                if (!EndsWith(GetLowerNameView_NoLock(recordIndex), suffix))
                {
                    continue;
                }

                ++totalMatched;
                if (totalMatched > offset && static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(recordIndex);
                }
            }
        }

        void ExtensionMatch_NoLock(
            const std::wstring& extension,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched) const
        {
            totalMatched = 0;
            const auto bucketIt = extensionIds_.find(extension);
            if (bucketIt == extensionIds_.end())
            {
                return;
            }

            totalMatched = static_cast<int>(bucketIt->second.size());
            for (auto i = std::max(offset, 0); i < totalMatched && static_cast<int>(page.size()) < maxResults; ++i)
            {
                page.push_back(bucketIt->second[static_cast<std::size_t>(i)]);
            }
        }

        void ContainsMatch_NoLock(
            const std::wstring& query,
            SearchFilter filter,
            const std::vector<std::uint32_t>& source,
            int offset,
            int maxResults,
            std::vector<std::uint32_t>& page,
            int& totalMatched) const
        {
            totalMatched = 0;
            for (const auto recordIndex : source)
            {
                const auto lowerName = GetLowerNameView_NoLock(recordIndex);
                if (lowerName.find(query) == std::wstring_view::npos)
                {
                    continue;
                }

                ++totalMatched;
                if (totalMatched > offset && static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(recordIndex);
                }
            }
        }

        bool IsSelfManagedChange_NoLock(const NativeChange& change) const
        {
            switch (change.kind)
            {
            case ChangeKind::Create:
                if (!CouldBeSelfManagedLeafName(change.lowerName))
                {
                    return false;
                }

                if (!IsSelfManagedDrive(change.driveLetter))
                {
                    return false;
                }

                return IsSelfManagedPath(ResolveFullPath_NoLock(change.driveLetter, change.parentFrn, change.originalName));
            case ChangeKind::Delete:
            {
                if (!CouldBeSelfManagedLeafName(change.lowerName))
                {
                    return false;
                }

                if (!IsSelfManagedDrive(change.driveLetter))
                {
                    return false;
                }

                const auto key = MakeRecordKey(change.driveLetter, change.frn);
                std::uint32_t recordId = 0;
                if (TryGetRecordIdByKey_NoLock(key, recordId))
                {
                    return IsSelfManagedPath(ResolveFullPathById_NoLock(recordId));
                }

                return IsSelfManagedPath(ResolveFullPath_NoLock(change.driveLetter, change.parentFrn, change.originalName));
            }
            case ChangeKind::Rename:
                if (!CouldBeSelfManagedLeafName(change.lowerName) && !CouldBeSelfManagedLeafName(change.oldLowerName))
                {
                    return false;
                }

                if (!IsSelfManagedDrive(change.driveLetter))
                {
                    return false;
                }

                return IsSelfManagedPath(ResolveFullPath_NoLock(change.driveLetter, change.parentFrn, change.originalName))
                    || IsSelfManagedPath(ResolveFullPath_NoLock(change.driveLetter, change.oldParentFrn, change.oldLowerName));
            default:
                return false;
            }
        }

        static bool IsSelfManagedDrive(wchar_t driveLetter)
        {
            const auto drive = static_cast<wchar_t>(towupper(driveLetter));
            if (drive < L'A' || drive > L'Z')
            {
                return false;
            }

            return drive == GetDriveLetterFromPath(GetSnapshotDirectoryPath())
                || drive == GetDriveLetterFromPath(GetNativeLogDirectoryPath())
                || drive == GetDriveLetterFromPath(GetNativeLogSettingsPath());
        }

        static bool CouldBeSelfManagedLeafName(const std::wstring& lowerName)
        {
            return StartsWith(std::wstring_view(lowerName), std::wstring_view(L"index-native"))
                || EndsWith(std::wstring_view(lowerName), std::wstring_view(L".log"))
                || lowerName == L"settings.json";
        }

        static wchar_t GetDriveLetterFromPath(const std::filesystem::path& path)
        {
            if (path.empty())
            {
                return L'\0';
            }

            const auto value = NormalizePathSeparators(path.wstring());
            if (value.length() < 2 || value[1] != L':' || !iswalpha(value[0]))
            {
                return L'\0';
            }

            return static_cast<wchar_t>(towupper(value[0]));
        }

        static bool IsSelfManagedPath(const std::wstring& fullPath)
        {
            return IsPathUnderDirectory(fullPath, GetSnapshotDirectoryPath())
                || IsPathUnderDirectory(fullPath, GetNativeLogDirectoryPath())
                || PathsEqual(fullPath, GetNativeLogSettingsPath());
        }

        static bool IsPathUnderDirectory(const std::wstring& fullPath, const std::filesystem::path& directory)
        {
            if (fullPath.empty() || directory.empty())
            {
                return false;
            }

            const auto path = NormalizePathSeparators(fullPath);
            const auto prefix = EnsureTrailingSlash(NormalizePathSeparators(directory.wstring()));
            return StartsWithI(path, prefix);
        }

        static bool PathsEqual(const std::wstring& left, const std::filesystem::path& right)
        {
            if (left.empty() || right.empty())
            {
                return false;
            }

            return EqualsI(NormalizePathSeparators(left), NormalizePathSeparators(right.wstring()));
        }

        static bool EqualsI(const std::wstring& left, const std::wstring& right)
        {
            if (left.length() != right.length())
            {
                return false;
            }

            for (std::size_t i = 0; i < left.length(); ++i)
            {
                if (towlower(left[i]) != towlower(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        static std::wstring NormalizePathSeparators(std::wstring value)
        {
            std::replace(value.begin(), value.end(), L'/', L'\\');
            return value;
        }

        std::wstring ResolveFullPath_NoLock(const Record& record)
        {
            return ResolveFullPath_NoLock(record.driveLetter, record.parentFrn, record.originalName);
        }

        std::wstring ResolveFullPathById_NoLock(std::uint32_t recordId) const
        {
            return ResolveFullPath_NoLock(
                GetRecordDrive_NoLock(recordId),
                GetRecordParentFrn_NoLock(recordId),
                std::wstring(GetOriginalNameView_NoLock(recordId)));
        }

        std::wstring ResolveFullPath_NoLock(wchar_t driveLetter, std::uint64_t parentFrn, const std::wstring& name) const
        {
            auto volumeIt = volumes_.find(driveLetter);
            if (volumeIt == volumes_.end())
            {
                return std::wstring(1, driveLetter) + L":\\" + name;
            }

            auto directoryPath = parentFrn == 0
                ? std::wstring(1, driveLetter) + L":\\"
                : ResolveDirectoryPath_NoLock(volumeIt->second, parentFrn);

            if (!directoryPath.empty() && directoryPath.back() != L'\\')
            {
                directoryPath.push_back(L'\\');
            }

            return directoryPath + name;
        }

        std::wstring ResolveDirectoryPath_NoLock(const VolumeState& volume, std::uint64_t frn) const
        {
            if (frn == 0 || frn == NtfsRootFileReferenceNumber)
            {
                return std::wstring(1, volume.driveLetter) + L":\\";
            }

            auto& mutableVolume = const_cast<VolumeState&>(volume);
            const auto cached = mutableVolume.pathCache.find(frn);
            if (cached != mutableVolume.pathCache.end())
            {
                return cached->second;
            }

            std::vector<std::pair<std::uint64_t, std::wstring>> parts;
            parts.reserve(32);
            std::vector<std::uint64_t> visited;
            visited.reserve(64);

            auto currentFrn = frn;
            std::wstring basePath;
            for (int depth = 0; depth < 4096; ++depth)
            {
                if (currentFrn == 0 || currentFrn == NtfsRootFileReferenceNumber)
                {
                    basePath = std::wstring(1, volume.driveLetter) + L":\\";
                    break;
                }

                if (std::find(visited.begin(), visited.end(), currentFrn) != visited.end())
                {
                    basePath = std::wstring(1, volume.driveLetter) + L":\\";
                    break;
                }

                visited.push_back(currentFrn);

                const auto cachedParent = mutableVolume.pathCache.find(currentFrn);
                if (cachedParent != mutableVolume.pathCache.end())
                {
                    basePath = cachedParent->second;
                    break;
                }

                const auto frnEntry = volume.frnMap.find(currentFrn);
                if (frnEntry != volume.frnMap.end())
                {
                    parts.emplace_back(currentFrn, frnEntry->second.name);
                    if (frnEntry->second.parentFrn == currentFrn)
                    {
                        basePath = std::wstring(1, volume.driveLetter) + L":\\";
                        break;
                    }

                    currentFrn = frnEntry->second.parentFrn;
                    continue;
                }

                std::uint32_t recordId = 0;
                if (!TryGetRecordIdByKey_NoLock(MakeRecordKey(volume.driveLetter, currentFrn), recordId))
                {
                    basePath = std::wstring(1, volume.driveLetter) + L":\\";
                    break;
                }

                parts.emplace_back(currentFrn, std::wstring(GetOriginalNameView_NoLock(recordId)));
                const auto parentFrn = GetRecordParentFrn_NoLock(recordId);
                if (parentFrn == currentFrn)
                {
                    basePath = std::wstring(1, volume.driveLetter) + L":\\";
                    break;
                }

                currentFrn = parentFrn;
            }

            if (basePath.empty())
            {
                basePath = std::wstring(1, volume.driveLetter) + L":\\";
            }

            auto fullPath = basePath;
            for (auto it = parts.rbegin(); it != parts.rend(); ++it)
            {
                if (!fullPath.empty() && fullPath.back() != L'\\')
                {
                    fullPath.push_back(L'\\');
                }

                fullPath += it->second;
                mutableVolume.pathCache[it->first] = fullPath;
            }

            TrimPathCache_NoLock(mutableVolume);
            return fullPath;
        }

        bool TryLoadSnapshot()
        {
            const auto totalStart = std::chrono::steady_clock::now();
            if (TryLoadV2RecordsSnapshot_NoLock(totalStart))
            {
                return true;
            }

            const auto snapshotPath = GetSnapshotPath();
            if (!std::filesystem::exists(snapshotPath))
            {
                NativeLogWrite("[NATIVE SNAPSHOT LOAD] miss elapsedMs=0");
                return false;
            }

            std::error_code fileSizeError;
            const auto fileBytes = std::filesystem::file_size(snapshotPath, fileSizeError);

            std::ifstream input(snapshotPath, std::ios::binary);
            if (!input)
            {
                return false;
            }

            std::int32_t version = 0;
            input.read(reinterpret_cast<char*>(&version), sizeof(version));
            if (!input.good() || (version != SnapshotVersion3 && version != SnapshotVersion4))
            {
                return false;
            }

            std::int32_t stringPoolCount = 0;
            input.read(reinterpret_cast<char*>(&stringPoolCount), sizeof(stringPoolCount));
            if (!input.good() || stringPoolCount < 0)
            {
                return false;
            }

            std::vector<std::wstring> stringPool(static_cast<std::size_t>(stringPoolCount));
            for (auto& value : stringPool)
            {
                if (!ReadSnapshotString(input, value))
                {
                    return false;
                }
            }

            std::int32_t recordCount = 0;
            input.read(reinterpret_cast<char*>(&recordCount), sizeof(recordCount));
            if (!input.good() || recordCount <= 0)
            {
                return false;
            }

            std::vector<Record> loadedRecords;
            loadedRecords.reserve(static_cast<std::size_t>(recordCount));
            for (std::int32_t i = 0; i < recordCount; ++i)
            {
                Record record;
                std::int32_t nameIndex = 0;
                input.read(reinterpret_cast<char*>(&nameIndex), sizeof(nameIndex));
                input.read(reinterpret_cast<char*>(&record.parentFrn), sizeof(record.parentFrn));
                if (!ReadSnapshotChar(input, record.driveLetter))
                {
                    return false;
                }
                if (!ReadSnapshotBool(input, record.isDirectory))
                {
                    return false;
                }
                input.read(reinterpret_cast<char*>(&record.frn), sizeof(record.frn));
                if (!input.good() || nameIndex < 0 || nameIndex >= stringPoolCount)
                {
                    return false;
                }
                record.originalName = stringPool[static_cast<std::size_t>(nameIndex)];
                record.lowerName = ToLower(record.originalName);
                loadedRecords.push_back(std::move(record));
            }

            std::int32_t volumeCount = 0;
            input.read(reinterpret_cast<char*>(&volumeCount), sizeof(volumeCount));
            if (!input.good() || volumeCount <= 0)
            {
                return false;
            }

            std::unordered_map<wchar_t, VolumeState> loadedVolumes;
            for (std::int32_t i = 0; i < volumeCount; ++i)
            {
                VolumeState volume;
                if (!ReadSnapshotChar(input, volume.driveLetter))
                {
                    return false;
                }
                input.read(reinterpret_cast<char*>(&volume.nextUsn), sizeof(volume.nextUsn));
                input.read(reinterpret_cast<char*>(&volume.journalId), sizeof(volume.journalId));
                volume.enumerationComplete = true;
                if (version >= SnapshotVersion4)
                {
                    input.read(reinterpret_cast<char*>(&volume.recordCount), sizeof(volume.recordCount));
                    input.read(reinterpret_cast<char*>(&volume.maxFrn), sizeof(volume.maxFrn));
                    if (!ReadSnapshotBool(input, volume.enumerationComplete))
                    {
                        return false;
                    }
                }
                std::int32_t frnCount = 0;
                input.read(reinterpret_cast<char*>(&frnCount), sizeof(frnCount));
                if (!input.good() || frnCount < 0)
                {
                    return false;
                }

                volume.frnMap.reserve(static_cast<std::size_t>(frnCount));
                for (std::int32_t j = 0; j < frnCount; ++j)
                {
                    std::int32_t nameIndex = 0;
                    std::uint64_t frn = 0;
                    std::uint64_t parentFrn = 0;
                    bool isDirectory = false;
                    input.read(reinterpret_cast<char*>(&nameIndex), sizeof(nameIndex));
                    input.read(reinterpret_cast<char*>(&frn), sizeof(frn));
                    input.read(reinterpret_cast<char*>(&parentFrn), sizeof(parentFrn));
                    if (!ReadSnapshotBool(input, isDirectory))
                    {
                        return false;
                    }
                    if (!input.good() || nameIndex < 0 || nameIndex >= stringPoolCount)
                    {
                        return false;
                    }
                    volume.frnMap[frn] = FrnEntry
                    {
                        stringPool[static_cast<std::size_t>(nameIndex)],
                        parentFrn,
                        isDirectory
                    };
                }

                loadedVolumes[volume.driveLetter] = std::move(volume);
            }

            if (!input.good() && !input.eof())
            {
                return false;
            }

            const auto snapshotReadMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - totalStart).count();
            mappedRecords_.Clear();
            records_ = std::move(loadedRecords);
            volumes_ = std::move(loadedVolumes);
            PopulateVolumeRecordMetrics_NoLock(records_, volumes_);
            RebuildFrnRecordIndex_NoLock();
            ReleaseDuplicateFrnNameStorage_NoLock();
            const auto restoreStart = std::chrono::steady_clock::now();
            const auto v2SidecarLoaded = TryLoadV2PostingsSnapshot_NoLock();
            const auto runtimeSidecarLoaded = v2SidecarLoaded && TryLoadV2RuntimeSnapshot_NoLock();
            const auto typeBucketsLoaded = TryLoadV2TypeBucketsSnapshot_NoLock();
            if (!typeBucketsLoaded)
            {
                BuildBaseBuckets_NoLock("snapshot-restore-base", true);
            }
            if (runtimeSidecarLoaded)
            {
                v2_.postingsBytes =
                    (static_cast<std::uint64_t>(v2_.trigramEntries.size()) * sizeof(NativeV2BucketEntry))
                    + static_cast<std::uint64_t>(v2_.trigramPostings.size())
                    + (static_cast<std::uint64_t>(v2_.prefix4Entries.size()) * sizeof(NativeV2BucketEntry))
                    + static_cast<std::uint64_t>(v2_.prefix4Postings.size())
                    + EstimateShortPostingBytes_NoLock()
                    + (static_cast<std::uint64_t>(v2_.parentOrder.size()) * sizeof(std::uint32_t));
                v2_.ready = true;
                derivedDirty_ = false;
                generationId_.fetch_add(1, std::memory_order_relaxed);
            }
            else if (v2SidecarLoaded)
            {
                BuildV2RuntimeStructures_NoLock();
            }
            else
            {
                BuildV2Accelerator_NoLock();
            }
            const auto restoreMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - restoreStart).count();
            NativeLogWrite(
                "[NATIVE SNAPSHOT LOAD] hit elapsedMs=" + std::to_string(snapshotReadMs)
                + " version=" + std::to_string(version)
                + " fileBytes=" + std::to_string(fileSizeError ? 0 : fileBytes)
                + " records=" + std::to_string(records_.size())
                + " volumes=" + std::to_string(volumes_.size())
                + " frnEntries=" + std::to_string(CountLiveFrnEntries(volumes_, records_.size())));
            NativeLogWrite(
                "[NATIVE SNAPSHOT RESTORE] elapsedMs=" + std::to_string(restoreMs)
                + " records=" + std::to_string(records_.size())
                + " volumes=" + std::to_string(volumes_.size())
                + " v2SidecarLoaded=" + std::string(v2SidecarLoaded ? "true" : "false")
                + " runtimeSidecarLoaded=" + std::string(runtimeSidecarLoaded ? "true" : "false")
                + " typeBucketsLoaded=" + std::string(typeBucketsLoaded ? "true" : "false"));
            NativeLogWrite(
                "[NATIVE SNAPSHOT RESTORE TOTAL] totalMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - totalStart).count())
                + " snapshotReadMs=" + std::to_string(snapshotReadMs)
                + " restoreMs=" + std::to_string(restoreMs)
                + " restoredCount=" + std::to_string(records_.size()));
            return true;
        }

        bool TryLoadV2RecordsSnapshot_NoLock(const std::chrono::steady_clock::time_point& totalStart)
        {
            std::unordered_map<wchar_t, VolumeState> mappedVolumes;
            std::uint32_t mappedRecordCount = 0;
            std::uint32_t mappedStringPoolChars = 0;
            std::uint64_t mappedFileBytes = 0;
            if (TryMapV2RecordsSnapshot_NoLock(
                totalStart,
                mappedVolumes,
                mappedRecordCount,
                mappedStringPoolChars,
                mappedFileBytes))
            {
                records_.clear();
                records_.shrink_to_fit();
                volumes_ = std::move(mappedVolumes);
                RebuildFrnRecordIndexFromMapped_NoLock();
                ReleaseDuplicateFrnNameStorage_NoLock();

                const auto restoreStart = std::chrono::steady_clock::now();
                const auto v2SidecarLoaded = TryLoadV2PostingsSnapshot_NoLock();
                const auto runtimeSidecarLoaded = v2SidecarLoaded && TryLoadV2RuntimeSnapshot_NoLock();
                const auto typeBucketsLoaded = TryLoadV2TypeBucketsSnapshot_NoLock();
                if (!v2SidecarLoaded || !runtimeSidecarLoaded || !typeBucketsLoaded)
                {
                    NativeLogWrite(
                        "[NATIVE SNAPSHOT RESTORE] failed reason=incomplete-v2-sidecar"
                        + std::string(" v2SidecarLoaded=") + (v2SidecarLoaded ? "true" : "false")
                        + " runtimeSidecarLoaded=" + (runtimeSidecarLoaded ? "true" : "false")
                        + " typeBucketsLoaded=" + (typeBucketsLoaded ? "true" : "false"));
                    v2_.ready = false;
                    return false;
                }

                v2_.postingsBytes =
                    (static_cast<std::uint64_t>(v2_.trigramEntries.size()) * sizeof(NativeV2BucketEntry))
                    + static_cast<std::uint64_t>(v2_.trigramPostings.size())
                    + (static_cast<std::uint64_t>(v2_.prefix4Entries.size()) * sizeof(NativeV2BucketEntry))
                    + static_cast<std::uint64_t>(v2_.prefix4Postings.size())
                    + EstimateShortPostingBytes_NoLock()
                    + (static_cast<std::uint64_t>(v2_.parentOrder.size() + v2_.childRecordIds.size()) * sizeof(std::uint32_t))
                    + (static_cast<std::uint64_t>(v2_.childEntries.size()) * sizeof(NativeV2ChildBucketEntry));
                v2_.ready = true;
                derivedDirty_ = false;
                generationId_.fetch_add(1, std::memory_order_relaxed);
                TryLoadV2OverlaySnapshot_NoLock(mappedRecordCount);

                const auto restoreMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - restoreStart).count();
                NativeLogWrite(
                    "[NATIVE SNAPSHOT RESTORE] elapsedMs=" + std::to_string(restoreMs)
                    + " records=" + std::to_string(mappedRecordCount)
                    + " volumes=" + std::to_string(volumes_.size())
                    + " v2SidecarLoaded=true runtimeSidecarLoaded=true typeBucketsLoaded=true mappedRecords=true");
                NativeLogWrite(
                    "[NATIVE SNAPSHOT RESTORE TOTAL] totalMs="
                    + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - totalStart).count())
                    + " snapshotReadMs=0"
                    + " restoreMs=" + std::to_string(restoreMs)
                    + " restoredCount=" + std::to_string(mappedRecordCount)
                    + " source=v2-mmap");
                return true;
            }

            const auto path = GetV2RecordsSnapshotPath();
            if (!std::filesystem::exists(path))
            {
                NativeLogWrite("[NATIVE V2 RECORDS LOAD] miss reason=missing");
                return false;
            }

            std::error_code fileSizeError;
            const auto fileBytes = std::filesystem::file_size(path, fileSizeError);
            std::ifstream input(path, std::ios::binary);
            if (!input)
            {
                return false;
            }

            std::int32_t version = 0;
            std::uint32_t recordCount = 0;
            std::uint32_t volumeCount = 0;
            std::uint32_t stringPoolChars = 0;
            input.read(reinterpret_cast<char*>(&version), sizeof(version));
            input.read(reinterpret_cast<char*>(&recordCount), sizeof(recordCount));
            input.read(reinterpret_cast<char*>(&volumeCount), sizeof(volumeCount));
            input.read(reinterpret_cast<char*>(&stringPoolChars), sizeof(stringPoolChars));
            if (!input.good()
                || (version != V2RecordsSnapshotVersion1 && version != V2RecordsSnapshotVersion2)
                || recordCount == 0
                || volumeCount == 0)
            {
                NativeLogWrite("[NATIVE V2 RECORDS LOAD] miss reason=version-or-header");
                return false;
            }

            std::vector<NativeV2CompactRecord> compactRecords(recordCount);
            if (recordCount > 0)
            {
                input.read(reinterpret_cast<char*>(compactRecords.data()),
                    static_cast<std::streamsize>(recordCount * sizeof(NativeV2CompactRecord)));
                if (!input.good())
                {
                    return false;
                }
            }

            std::wstring stringPool;
            stringPool.resize(stringPoolChars);
            if (stringPoolChars > 0)
            {
                input.read(reinterpret_cast<char*>(stringPool.data()),
                    static_cast<std::streamsize>(stringPoolChars * sizeof(wchar_t)));
                if (!input.good())
                {
                    return false;
                }
            }

            std::unordered_map<wchar_t, VolumeState> loadedVolumes;
            loadedVolumes.reserve(volumeCount);
            for (std::uint32_t i = 0; i < volumeCount; ++i)
            {
                VolumeState volume;
                if (!ReadSnapshotChar(input, volume.driveLetter))
                {
                    return false;
                }

                input.read(reinterpret_cast<char*>(&volume.nextUsn), sizeof(volume.nextUsn));
                input.read(reinterpret_cast<char*>(&volume.journalId), sizeof(volume.journalId));
                volume.enumerationComplete = true;
                if (version >= V2RecordsSnapshotVersion2)
                {
                    input.read(reinterpret_cast<char*>(&volume.recordCount), sizeof(volume.recordCount));
                    input.read(reinterpret_cast<char*>(&volume.maxFrn), sizeof(volume.maxFrn));
                    if (!ReadSnapshotBool(input, volume.enumerationComplete))
                    {
                        return false;
                    }
                }
                if (!input.good())
                {
                    return false;
                }

                loadedVolumes[volume.driveLetter] = std::move(volume);
            }

            std::vector<Record> loadedRecords;
            loadedRecords.reserve(recordCount);
            for (const auto& compact : compactRecords)
            {
                if (compact.originalOffset > stringPool.size()
                    || compact.lowerOffset > stringPool.size()
                    || static_cast<std::size_t>(compact.originalOffset) + compact.originalLength > stringPool.size()
                    || static_cast<std::size_t>(compact.lowerOffset) + compact.lowerLength > stringPool.size())
                {
                    return false;
                }

                Record record;
                record.frn = compact.frn;
                record.parentFrn = compact.parentFrn;
                record.driveLetter = static_cast<wchar_t>(compact.drive);
                record.isDirectory = (compact.flags & 0x1) != 0;
                record.originalName.assign(stringPool.data() + compact.originalOffset, compact.originalLength);
                record.lowerName.assign(stringPool.data() + compact.lowerOffset, compact.lowerLength);
                loadedRecords.push_back(std::move(record));
            }

            const auto recordsLoadMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - totalStart).count();
            records_ = std::move(loadedRecords);
            volumes_ = std::move(loadedVolumes);
            RebuildFrnRecordIndex_NoLock();
            ReleaseDuplicateFrnNameStorage_NoLock();

            const auto restoreStart = std::chrono::steady_clock::now();
            const auto v2SidecarLoaded = TryLoadV2PostingsSnapshot_NoLock();
            const auto runtimeSidecarLoaded = v2SidecarLoaded && TryLoadV2RuntimeSnapshot_NoLock();
            const auto typeBucketsLoaded = TryLoadV2TypeBucketsSnapshot_NoLock();
            if (!typeBucketsLoaded)
            {
                BuildBaseBuckets_NoLock("v2-records-restore-base", true);
            }
            if (runtimeSidecarLoaded)
            {
                v2_.postingsBytes =
                    (static_cast<std::uint64_t>(v2_.trigramEntries.size()) * sizeof(NativeV2BucketEntry))
                    + static_cast<std::uint64_t>(v2_.trigramPostings.size())
                    + (static_cast<std::uint64_t>(v2_.prefix4Entries.size()) * sizeof(NativeV2BucketEntry))
                    + static_cast<std::uint64_t>(v2_.prefix4Postings.size())
                    + EstimateShortPostingBytes_NoLock()
                    + (static_cast<std::uint64_t>(v2_.parentOrder.size() + v2_.childRecordIds.size()) * sizeof(std::uint32_t))
                    + (static_cast<std::uint64_t>(v2_.childEntries.size()) * sizeof(NativeV2ChildBucketEntry));
                v2_.ready = true;
                derivedDirty_ = false;
                generationId_.fetch_add(1, std::memory_order_relaxed);
            }
            else if (v2SidecarLoaded)
            {
                BuildV2RuntimeStructures_NoLock();
            }
            else
            {
                BuildV2Accelerator_NoLock();
            }
            TryLoadV2OverlaySnapshot_NoLock(records_.size());

            const auto restoreMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - restoreStart).count();
            NativeLogWrite(
                "[NATIVE V2 RECORDS LOAD] success elapsedMs=" + std::to_string(recordsLoadMs)
                + " version=" + std::to_string(version)
                + " fileBytes=" + std::to_string(fileSizeError ? 0 : fileBytes)
                + " records=" + std::to_string(records_.size())
                + " volumes=" + std::to_string(volumes_.size())
                + " stringPoolChars=" + std::to_string(stringPoolChars));
            NativeLogWrite(
                "[NATIVE SNAPSHOT RESTORE] elapsedMs=" + std::to_string(restoreMs)
                + " records=" + std::to_string(records_.size())
                + " volumes=" + std::to_string(volumes_.size())
                + " v2SidecarLoaded=" + std::string(v2SidecarLoaded ? "true" : "false")
                + " runtimeSidecarLoaded=" + std::string(runtimeSidecarLoaded ? "true" : "false")
                + " typeBucketsLoaded=" + std::string(typeBucketsLoaded ? "true" : "false"));
            NativeLogWrite(
                "[NATIVE SNAPSHOT RESTORE TOTAL] totalMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - totalStart).count())
                + " snapshotReadMs=" + std::to_string(recordsLoadMs)
                + " restoreMs=" + std::to_string(restoreMs)
                + " restoredCount=" + std::to_string(records_.size())
                + " source=v2-records");
            return true;
        }

        bool TryLoadV2OverlaySnapshot_NoLock(std::size_t stableRecordCount)
        {
            const auto path = GetV2OverlaySnapshotPath();
            if (!std::filesystem::exists(path))
            {
                return false;
            }

            const auto start = std::chrono::steady_clock::now();
            std::ifstream input(path, std::ios::binary);
            if (!input)
            {
                NativeLogWrite("[NATIVE V2 OVERLAY LOAD] miss reason=open-failed");
                return false;
            }

            std::int32_t version = 0;
            std::uint64_t baseRecordCount = 0;
            std::uint32_t volumeCount = 0;
            std::uint32_t recordCount = 0;
            std::uint32_t tombstoneCount = 0;
            input.read(reinterpret_cast<char*>(&version), sizeof(version));
            input.read(reinterpret_cast<char*>(&baseRecordCount), sizeof(baseRecordCount));
            input.read(reinterpret_cast<char*>(&volumeCount), sizeof(volumeCount));
            input.read(reinterpret_cast<char*>(&recordCount), sizeof(recordCount));
            input.read(reinterpret_cast<char*>(&tombstoneCount), sizeof(tombstoneCount));
            if (!input.good()
                || version != V2OverlaySnapshotVersion1
                || baseRecordCount != stableRecordCount
                || recordCount > 10000000u
                || tombstoneCount > 10000000u)
            {
                NativeLogWrite(
                    "[NATIVE V2 OVERLAY LOAD] miss reason=header"
                    " stableRecords=" + std::to_string(stableRecordCount)
                    + " baseRecordCount=" + std::to_string(baseRecordCount));
                return false;
            }

            std::unordered_map<wchar_t, std::pair<std::int64_t, std::uint64_t>> volumeCheckpoints;
            volumeCheckpoints.reserve(volumeCount);
            for (std::uint32_t i = 0; i < volumeCount; ++i)
            {
                wchar_t drive = L'\0';
                std::int64_t nextUsn = 0;
                std::uint64_t journalId = 0;
                if (!ReadSnapshotChar(input, drive))
                {
                    return false;
                }

                input.read(reinterpret_cast<char*>(&nextUsn), sizeof(nextUsn));
                input.read(reinterpret_cast<char*>(&journalId), sizeof(journalId));
                if (!input.good())
                {
                    return false;
                }

                volumeCheckpoints[drive] = { nextUsn, journalId };
            }

            std::vector<Record> loadedOverlayRecords;
            loadedOverlayRecords.reserve(recordCount);
            for (std::uint32_t i = 0; i < recordCount; ++i)
            {
                Record record;
                if (!ReadSnapshotChar(input, record.driveLetter))
                {
                    return false;
                }

                input.read(reinterpret_cast<char*>(&record.frn), sizeof(record.frn));
                input.read(reinterpret_cast<char*>(&record.parentFrn), sizeof(record.parentFrn));
                if (!ReadSnapshotBool(input, record.isDirectory)
                    || !ReadSnapshotString(input, record.originalName)
                    || !ReadSnapshotString(input, record.lowerName)
                    || !input.good())
                {
                    return false;
                }

                loadedOverlayRecords.push_back(std::move(record));
            }

            std::unordered_set<std::uint64_t> loadedDeletes;
            loadedDeletes.reserve(tombstoneCount);
            for (std::uint32_t i = 0; i < tombstoneCount; ++i)
            {
                wchar_t drive = L'\0';
                std::uint64_t frn = 0;
                if (!ReadSnapshotChar(input, drive))
                {
                    return false;
                }

                input.read(reinterpret_cast<char*>(&frn), sizeof(frn));
                if (!input.good())
                {
                    return false;
                }

                loadedDeletes.insert(MakeRecordKey(drive, frn));
            }

            overlayRecords_.clear();
            overlayIndexByKey_.clear();
            overlayDeletedKeys_ = std::move(loadedDeletes);
            overlaySegmentBaseRecordCountOverride_ = 0;
            overlayCompactedKeys_.clear();
            overlayCompactedDeletedKeys_.clear();
            overlayRecords_.reserve(loadedOverlayRecords.size());
            overlayIndexByKey_.reserve(loadedOverlayRecords.size());
            for (auto& record : loadedOverlayRecords)
            {
                const auto key = MakeRecordKey(record.driveLetter, record.frn);
                if (overlayDeletedKeys_.find(key) != overlayDeletedKeys_.end())
                {
                    continue;
                }

                overlayIndexByKey_[key] = static_cast<std::uint32_t>(overlayRecords_.size());
                overlayRecords_.push_back(std::move(record));
            }
            RebuildOverlaySuppressedRecordIds_NoLock();

            for (const auto& pair : volumeCheckpoints)
            {
                auto volumeIt = volumes_.find(pair.first);
                if (volumeIt == volumes_.end())
                {
                    continue;
                }

                volumeIt->second.nextUsn = std::max(volumeIt->second.nextUsn, pair.second.first);
                volumeIt->second.journalId = pair.second.second;
            }

            RebuildOverlaySearchIndex_NoLock();
            generationId_.fetch_add(1, std::memory_order_relaxed);
            NativeLogWrite(
                "[NATIVE V2 OVERLAY LOAD] success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - start).count())
                + " stableRecords=" + std::to_string(stableRecordCount)
                + " overlayRecords=" + std::to_string(overlayRecords_.size())
                + " overlayDeletes=" + std::to_string(overlayDeletedKeys_.size())
                + " volumes=" + std::to_string(volumeCheckpoints.size()));
            return true;
        }

        bool RemapCurrentSidecars_NoLock()
        {
            mappedRecords_.Clear();
            v2_.Clear();
            allIds_.clear();
            directoryIds_.clear();
            launchableIds_.clear();
            scriptIds_.clear();
            logIds_.clear();
            configIds_.clear();
            ClearDriveIds_NoLock();
            extensionIds_.clear();
            exactNameIds_.clear();
            ClearPathScopeRecordIdsCache_NoLock();

            std::unordered_map<wchar_t, VolumeState> mappedVolumes;
            std::uint32_t mappedRecordCount = 0;
            std::uint32_t mappedStringPoolChars = 0;
            std::uint64_t mappedFileBytes = 0;
            const auto start = std::chrono::steady_clock::now();
            if (!TryMapV2RecordsSnapshot_NoLock(start, mappedVolumes, mappedRecordCount, mappedStringPoolChars, mappedFileBytes))
            {
                return false;
            }

            records_.clear();
            records_.shrink_to_fit();
            volumes_ = std::move(mappedVolumes);
            RebuildFrnRecordIndexFromMapped_NoLock();
            if (!TryLoadV2PostingsSnapshot_NoLock()
                || !TryLoadV2RuntimeSnapshot_NoLock()
                || !TryLoadV2TypeBucketsSnapshot_NoLock())
            {
                return false;
            }

            v2_.ready = true;
            derivedDirty_ = false;
            generationId_.fetch_add(1, std::memory_order_relaxed);
            NativeLogWrite(
                "[NATIVE GENERATION REMAP] success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - start).count())
                + " records=" + std::to_string(mappedRecordCount));
            return true;
        }

        bool WriteAndMapCurrentRecords_NoLock()
        {
            if (records_.empty() || volumes_.empty())
            {
                return mappedRecords_.Ready();
            }

            const auto start = std::chrono::steady_clock::now();
            WriteV2RecordsSnapshotFile(records_, volumes_);

            std::unordered_map<wchar_t, VolumeState> mappedVolumes;
            std::uint32_t mappedRecordCount = 0;
            std::uint32_t mappedStringPoolChars = 0;
            std::uint64_t mappedFileBytes = 0;
            if (!TryMapV2RecordsSnapshot_NoLock(start, mappedVolumes, mappedRecordCount, mappedStringPoolChars, mappedFileBytes))
            {
                return false;
            }

            records_.clear();
            records_.shrink_to_fit();
            volumes_ = std::move(mappedVolumes);
            RebuildFrnRecordIndexFromMapped_NoLock();
            NativeLogWrite(
                "[NATIVE RECORDS REMAP] success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - start).count())
                + " records=" + std::to_string(mappedRecordCount)
                + " fileBytes=" + std::to_string(mappedFileBytes)
                + " stringPoolChars=" + std::to_string(mappedStringPoolChars));
            return true;
        }

        bool CanForceFullSnapshotNow(bool ignoreIdleGate = false) const
        {
            if (activeSearchCount_.load(std::memory_order_acquire) > 0)
            {
                return false;
            }

            if (ignoreIdleGate)
            {
                return true;
            }

            const auto lastSearchCompleted = lastSearchCompletedTickMs_.load(std::memory_order_acquire);
            const auto nowMs = SteadyClockMilliseconds();
            if (lastSearchCompleted == 0)
            {
                return true;
            }

            const auto idleMs = nowMs - lastSearchCompleted;
            return idleMs >= ForceFullSnapshotIdleMilliseconds.count();
        }

        bool SaveForceFullSnapshot(bool ignoreIdleGate = false)
        {
            std::unique_lock<std::shared_mutex> guard(mutex_, std::try_to_lock);
            if (!guard.owns_lock())
            {
                NativeLogWrite("[NATIVE SNAPSHOT SAVE] skipped reason=force-full-index-lock-busy");
                return false;
            }

            const auto hasRecords = !records_.empty() && !volumes_.empty();
            if ((!hasRecords && !mappedRecords_.Ready()) || !v2_.ready)
            {
                return false;
            }

            if (!CanForceFullSnapshotNow(ignoreIdleGate))
            {
                NativeLogWrite(
                    "[NATIVE SNAPSHOT SAVE] skipped reason=force-full-not-idle"
                    " overlayRecords=" + std::to_string(overlayRecords_.size())
                    + " overlayDeletes=" + std::to_string(overlayDeletedKeys_.size()));
                return false;
            }

            OverlaySegmentSnapshotData overlaySegmentData;
            const auto hasOverlay = !overlayRecords_.empty() || !overlayDeletedKeys_.empty();
            const auto directSaveStart = std::chrono::steady_clock::now();
            if (hasOverlay)
            {
                BuildOverlaySegmentSnapshotData_NoLock(overlaySegmentData);
                WriteOverlaySegmentSnapshotData(overlaySegmentData);
            }

            const auto recordsRemapStart = std::chrono::steady_clock::now();
            const bool recordsRemapped = hasRecords ? WriteAndMapCurrentRecords_NoLock() : mappedRecords_.Ready();
            const auto recordsRemapMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - recordsRemapStart).count();
            if (!recordsRemapped)
            {
                NativeLogWrite(
                    "[NATIVE SNAPSHOT SAVE] skipped reason=force-full-records-remap-failed"
                    " recordsRemapMs=" + std::to_string(recordsRemapMs));
                return false;
            }

            const auto recordCount = GetRecordCount_NoLock();
            if (hasRecords && allIds_.size() != recordCount)
            {
                BuildBaseBuckets_NoLock("force-full-type-buckets", true);
            }

            if (allIds_.size() != recordCount)
            {
                NativeLogWrite(
                    "[NATIVE SNAPSHOT SAVE] skipped reason=force-full-type-buckets-record-count-mismatch"
                    " records=" + std::to_string(recordCount)
                    + " all=" + std::to_string(allIds_.size()));
                return false;
            }

            WriteV2PostingsSnapshotFile(recordCount, v2_);
            WriteV2RuntimeSnapshotFile(recordCount, v2_);
            WriteV2TypeBucketsSnapshotFile(recordCount, allIds_, directoryIds_, launchableIds_, scriptIds_, logIds_, configIds_, extensionIds_);
            forceFullSnapshotSavePending_.store(false, std::memory_order_release);
            NativeLogWrite(
                "[NATIVE SNAPSHOT SAVE] force-full-generation success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - directSaveStart).count())
                + " recordsRemapMs=" + std::to_string(recordsRemapMs)
                + " recordsRemapped=" + std::string(recordsRemapped ? "true" : "false")
                + " records=" + std::to_string(recordCount)
                + " overlayRecords=" + std::to_string(overlayRecords_.size())
                + " overlayDeletes=" + std::to_string(overlayDeletedKeys_.size()));
            return true;
        }

        bool TryMapV2RecordsSnapshot_NoLock(
            const std::chrono::steady_clock::time_point& totalStart,
            std::unordered_map<wchar_t, VolumeState>& loadedVolumes,
            std::uint32_t& recordCount,
            std::uint32_t& stringPoolChars,
            std::uint64_t& fileBytes)
        {
            loadedVolumes.clear();
            recordCount = 0;
            stringPoolChars = 0;
            fileBytes = 0;

            const auto path = GetV2RecordsSnapshotPath();
            if (!std::filesystem::exists(path))
            {
                NativeLogWrite("[NATIVE V2 RECORDS MAP] miss reason=missing");
                return false;
            }

            NativeV2MappedRecords mapped;
            if (!mapped.file.Open(path))
            {
                NativeLogWrite("[NATIVE V2 RECORDS MAP] miss reason=open-failed");
                return false;
            }

            fileBytes = mapped.file.Size();
            const auto* data = mapped.file.Data();
            auto remaining = mapped.file.Size();
            if (remaining < sizeof(std::int32_t) + (sizeof(std::uint32_t) * 3))
            {
                NativeLogWrite("[NATIVE V2 RECORDS MAP] miss reason=short-header");
                return false;
            }

            std::int32_t version = 0;
            std::memcpy(&version, data, sizeof(version));
            data += sizeof(version);
            remaining -= sizeof(version);
            std::memcpy(&mapped.recordCount, data, sizeof(mapped.recordCount));
            data += sizeof(mapped.recordCount);
            remaining -= sizeof(mapped.recordCount);
            std::memcpy(&mapped.volumeCount, data, sizeof(mapped.volumeCount));
            data += sizeof(mapped.volumeCount);
            remaining -= sizeof(mapped.volumeCount);
            std::memcpy(&mapped.stringPoolChars, data, sizeof(mapped.stringPoolChars));
            data += sizeof(mapped.stringPoolChars);
            remaining -= sizeof(mapped.stringPoolChars);

            if ((version != V2RecordsSnapshotVersion1 && version != V2RecordsSnapshotVersion2)
                || mapped.recordCount == 0
                || mapped.volumeCount == 0)
            {
                NativeLogWrite("[NATIVE V2 RECORDS MAP] miss reason=version-or-header");
                return false;
            }

            const auto recordsBytes = static_cast<std::uint64_t>(mapped.recordCount) * sizeof(NativeV2CompactRecord);
            const auto stringBytes = static_cast<std::uint64_t>(mapped.stringPoolChars) * sizeof(wchar_t);
            if (recordsBytes > remaining || stringBytes > remaining - static_cast<std::size_t>(recordsBytes))
            {
                NativeLogWrite("[NATIVE V2 RECORDS MAP] miss reason=invalid-size");
                return false;
            }

            mapped.records = reinterpret_cast<const NativeV2CompactRecord*>(data);
            data += static_cast<std::size_t>(recordsBytes);
            remaining -= static_cast<std::size_t>(recordsBytes);
            mapped.stringPool = reinterpret_cast<const wchar_t*>(data);
            data += static_cast<std::size_t>(stringBytes);
            remaining -= static_cast<std::size_t>(stringBytes);

            loadedVolumes.reserve(mapped.volumeCount);
            for (std::uint32_t i = 0; i < mapped.volumeCount; ++i)
            {
                if (remaining < sizeof(char) + sizeof(std::int64_t) + sizeof(std::uint64_t))
                {
                    NativeLogWrite("[NATIVE V2 RECORDS MAP] miss reason=invalid-volume");
                    return false;
                }

                char rawDrive = '\0';
                std::memcpy(&rawDrive, data, sizeof(rawDrive));
                data += sizeof(rawDrive);
                remaining -= sizeof(rawDrive);

                VolumeState volume;
                volume.driveLetter = static_cast<unsigned char>(rawDrive);
                std::memcpy(&volume.nextUsn, data, sizeof(volume.nextUsn));
                data += sizeof(volume.nextUsn);
                remaining -= sizeof(volume.nextUsn);
                std::memcpy(&volume.journalId, data, sizeof(volume.journalId));
                data += sizeof(volume.journalId);
                remaining -= sizeof(volume.journalId);
                volume.enumerationComplete = true;
                if (version >= V2RecordsSnapshotVersion2)
                {
                    if (remaining < sizeof(volume.recordCount) + sizeof(volume.maxFrn) + sizeof(char))
                    {
                        NativeLogWrite("[NATIVE V2 RECORDS MAP] miss reason=invalid-volume-metrics");
                        return false;
                    }

                    std::memcpy(&volume.recordCount, data, sizeof(volume.recordCount));
                    data += sizeof(volume.recordCount);
                    remaining -= sizeof(volume.recordCount);
                    std::memcpy(&volume.maxFrn, data, sizeof(volume.maxFrn));
                    data += sizeof(volume.maxFrn);
                    remaining -= sizeof(volume.maxFrn);
                    char rawComplete = '\0';
                    std::memcpy(&rawComplete, data, sizeof(rawComplete));
                    data += sizeof(rawComplete);
                    remaining -= sizeof(rawComplete);
                    volume.enumerationComplete = rawComplete != 0;
                }
                loadedVolumes[volume.driveLetter] = std::move(volume);
            }

            recordCount = mapped.recordCount;
            stringPoolChars = mapped.stringPoolChars;
            mappedRecords_ = std::move(mapped);
            PopulateVolumeRecordMetricsFromMapped_NoLock(loadedVolumes);
            NativeLogWrite(
                "[NATIVE V2 RECORDS MAP] success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - totalStart).count())
                + " fileBytes=" + std::to_string(fileBytes)
                + " records=" + std::to_string(recordCount)
                + " volumes=" + std::to_string(loadedVolumes.size())
                + " stringPoolChars=" + std::to_string(stringPoolChars));
            return true;
        }

        bool SaveSnapshot(bool flushPending = false)
        {
            if (forceFullSnapshotSavePending_.load(std::memory_order_acquire))
            {
                return SaveForceFullSnapshot(flushPending);
            }

            auto recordsCopy = std::make_unique<std::vector<Record>>();
            auto volumesCopy = std::make_unique<std::unordered_map<wchar_t, VolumeState>>();
            auto v2Copy = std::make_unique<NativeV2Accelerator>();
            auto allIdsCopy = std::make_unique<std::vector<std::uint32_t>>();
            auto directoryIdsCopy = std::make_unique<std::vector<std::uint32_t>>();
            auto launchableIdsCopy = std::make_unique<std::vector<std::uint32_t>>();
            auto scriptIdsCopy = std::make_unique<std::vector<std::uint32_t>>();
            auto logIdsCopy = std::make_unique<std::vector<std::uint32_t>>();
            auto configIdsCopy = std::make_unique<std::vector<std::uint32_t>>();
            auto extensionIdsCopy = std::make_unique<std::unordered_map<std::wstring, std::vector<std::uint32_t>>>();
            auto overlaySegmentData = std::make_unique<OverlaySegmentSnapshotData>();
            bool writeOverlaySegment = false;
            bool skipAfterOverlaySegment = false;
            bool compactedOverlay = false;
            std::uint64_t compactionGeneration = 0;

            {
                std::shared_lock<std::shared_mutex> guard(mutex_, std::try_to_lock);
                if (!guard.owns_lock())
                {
                    NativeLogWrite("[NATIVE SNAPSHOT SAVE] skipped reason=index-lock-busy");
                    return false;
                }

                const auto hasOverlay = !overlayRecords_.empty() || !overlayDeletedKeys_.empty();
                if (hasOverlay)
                {
                    BuildOverlaySegmentSnapshotData_NoLock(*overlaySegmentData);
                    writeOverlaySegment = true;
                    const auto forceOverlayCompaction = ShouldForceCompactOverlay_NoLock();
                    if (!CanCompactOverlayNow(forceOverlayCompaction))
                    {
                        NativeLogWrite(
                            "[NATIVE SNAPSHOT SAVE] skipped reason=overlay-compaction-not-idle"
                            " overlayRecords=" + std::to_string(overlayRecords_.size())
                            + " overlayDeletes=" + std::to_string(overlayDeletedKeys_.size())
                            + " forced=" + std::string(forceOverlayCompaction ? "true" : "false"));
                        skipAfterOverlaySegment = true;
                    }
                    else
                    {
                        ExportStableRecords_NoLock(*recordsCopy);
                        compactedOverlay = true;
                        compactionGeneration = generationId_.load(std::memory_order_acquire);
                        volumesCopy->reserve(volumes_.size());
                        for (const auto& pair : volumes_)
                        {
                            VolumeState volume;
                            volume.driveLetter = pair.second.driveLetter;
                            volume.nextUsn = pair.second.nextUsn;
                            volume.journalId = pair.second.journalId;
                            volume.recordCount = pair.second.recordCount;
                            volume.maxFrn = pair.second.maxFrn;
                            volume.enumerationComplete = pair.second.enumerationComplete;
                            volumesCopy->emplace(pair.first, std::move(volume));
                        }
                        NativeLogWrite(
                            "[NATIVE SNAPSHOT SAVE] compacting-overlay"
                            " baseRecords=" + std::to_string(GetRecordCount_NoLock())
                            + " overlayRecords=" + std::to_string(overlayRecords_.size())
                            + " overlayDeletes=" + std::to_string(overlayDeletedKeys_.size())
                            + " compactedRecords=" + std::to_string(recordsCopy->size())
                            + " forced=" + std::string(forceOverlayCompaction ? "true" : "false"));
                    }
                }
                else if (records_.empty() && mappedRecords_.Ready())
                {
                    NativeLogWrite("[NATIVE SNAPSHOT SAVE] skipped reason=mmap-generation-immutable");
                    return true;
                }
                else if (records_.empty() || volumes_.empty())
                {
                    return true;
                }
                else
                {
                    recordsCopy->assign(records_.begin(), records_.end());
                    volumesCopy->reserve(volumes_.size());
                    for (const auto& pair : volumes_)
                    {
                        VolumeState volume;
                        volume.driveLetter = pair.second.driveLetter;
                        volume.nextUsn = pair.second.nextUsn;
                        volume.journalId = pair.second.journalId;
                        volume.recordCount = pair.second.recordCount;
                        volume.maxFrn = pair.second.maxFrn;
                        volume.enumerationComplete = pair.second.enumerationComplete;
                        volumesCopy->emplace(pair.first, std::move(volume));
                    }
                    v2Copy->trigramEntries = v2_.trigramEntries;
                    v2Copy->trigramPostings = v2_.trigramPostings;
                    v2Copy->prefix4Entries = v2_.prefix4Entries;
                    v2Copy->prefix4Postings = v2_.prefix4Postings;
                    v2Copy->asciiCharEntries = v2_.asciiCharEntries;
                    v2Copy->asciiBigramEntries = v2_.asciiBigramEntries;
                    v2Copy->asciiCharDriveCounts = v2_.asciiCharDriveCounts;
                    v2Copy->asciiBigramDriveCounts = v2_.asciiBigramDriveCounts;
                    v2Copy->asciiCharDrivePages = v2_.asciiCharDrivePages;
                    v2Copy->asciiBigramDrivePages = v2_.asciiBigramDrivePages;
                    v2Copy->nonAsciiShortEntries = v2_.nonAsciiShortEntries;
                    v2Copy->asciiCharCompressedPostings = v2_.asciiCharCompressedPostings;
                    v2Copy->asciiBigramCompressedPostings = v2_.asciiBigramCompressedPostings;
                    v2Copy->nonAsciiShortCompressedPostings = v2_.nonAsciiShortCompressedPostings;
                    v2Copy->parentOrder = v2_.parentOrder;
                    v2Copy->childEntries = v2_.childEntries;
                    v2Copy->childRecordIds = v2_.childRecordIds;
                    v2Copy->subtreeEnter = v2_.subtreeEnter;
                    v2Copy->subtreeExit = v2_.subtreeExit;
                    v2Copy->subtreeRecordIds = v2_.subtreeRecordIds;
                    v2Copy->ready = v2_.ready;
                    v2Copy->buildMs = v2_.buildMs;
                    v2Copy->postingsBytes = v2_.postingsBytes;
                    *allIdsCopy = allIds_;
                    *directoryIdsCopy = directoryIds_;
                    *launchableIdsCopy = launchableIds_;
                    *scriptIdsCopy = scriptIds_;
                    *logIdsCopy = logIds_;
                    *configIdsCopy = configIds_;
                    *extensionIdsCopy = extensionIds_;
                }
            }

            if (writeOverlaySegment)
            {
                WriteOverlaySegmentSnapshotData(*overlaySegmentData);
                if (skipAfterOverlaySegment)
                {
                    return true;
                }
            }

            bool rebuiltV2ForSnapshot = false;
            if (!recordsCopy->empty() && (v2Copy->ready == false || v2Copy->parentOrder.empty() || v2Copy->parentOrder.size() != recordsCopy->size()))
            {
                const auto rebuildStart = std::chrono::steady_clock::now();
                BuildTypeBucketsForRecords(
                    *recordsCopy,
                    *allIdsCopy,
                    *directoryIdsCopy,
                    *launchableIdsCopy,
                    *scriptIdsCopy,
                    *logIdsCopy,
                    *configIdsCopy,
                    *extensionIdsCopy);
                rebuiltV2ForSnapshot = BuildV2AcceleratorForSnapshotRecords(*recordsCopy, *volumesCopy, *v2Copy);
                NativeLogWrite(
                    "[NATIVE SNAPSHOT SAVE] rebuilt-v2-sidecars"
                    " records=" + std::to_string(recordsCopy->size())
                    + " success=" + std::string(rebuiltV2ForSnapshot ? "true" : "false")
                    + " totalMs=" + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - rebuildStart).count()));
                if (!rebuiltV2ForSnapshot)
                {
                    NativeLogWrite("[NATIVE SNAPSHOT SAVE] skipped reason=v2-sidecar-rebuild-failed");
                    return false;
                }
            }

            if (!recordsCopy->empty() && allIdsCopy->size() != recordsCopy->size())
            {
                BuildTypeBucketsForRecords(
                    *recordsCopy,
                    *allIdsCopy,
                    *directoryIdsCopy,
                    *launchableIdsCopy,
                    *scriptIdsCopy,
                    *logIdsCopy,
                    *configIdsCopy,
                    *extensionIdsCopy);
                NativeLogWrite(
                    "[NATIVE SNAPSHOT SAVE] rebuilt-type-buckets"
                    " records=" + std::to_string(recordsCopy->size())
                    + " all=" + std::to_string(allIdsCopy->size()));
            }

            if (allIdsCopy->size() != recordsCopy->size())
            {
                NativeLogWrite(
                    "[NATIVE SNAPSHOT SAVE] skipped reason=type-buckets-record-count-mismatch"
                    " records=" + std::to_string(recordsCopy->size())
                    + " all=" + std::to_string(allIdsCopy->size()));
                return false;
            }

            WriteV2RecordsSnapshotFile(*recordsCopy, *volumesCopy);
            WriteV2PostingsSnapshotFile(recordsCopy->size(), *v2Copy);
            WriteV2RuntimeSnapshotFile(recordsCopy->size(), *v2Copy);
            WriteV2TypeBucketsSnapshotFile(recordsCopy->size(), *allIdsCopy, *directoryIdsCopy, *launchableIdsCopy, *scriptIdsCopy, *logIdsCopy, *configIdsCopy, *extensionIdsCopy);
            if (compactedOverlay)
            {
                CompleteOverlayCompaction(compactionGeneration, recordsCopy->size(), *overlaySegmentData);
            }
            forceFullSnapshotSavePending_.store(false, std::memory_order_release);
            return true;
        }

        void CompleteOverlayCompaction(
            std::uint64_t compactionGeneration,
            std::size_t compactedRecordCount,
            const OverlaySegmentSnapshotData& compactedData)
        {
            std::unique_lock<std::shared_mutex> guard(mutex_, std::try_to_lock);
            if (!guard.owns_lock())
            {
                NativeLogWrite("[NATIVE SNAPSHOT SAVE] compacted but memory-swap skipped reason=index-lock-busy");
                return;
            }

            const auto currentGeneration = generationId_.load(std::memory_order_acquire);
            const auto generationChanged = currentGeneration != compactionGeneration;
            std::unordered_set<std::uint64_t> compactedKeys;
            std::unordered_set<std::uint64_t> compactedDeletedKeys;

            if (!generationChanged)
            {
                compactedKeys.reserve(overlayIndexByKey_.size());
                compactedDeletedKeys.reserve(overlayDeletedKeys_.size());
                for (const auto& pair : overlayIndexByKey_)
                {
                    compactedKeys.insert(pair.first);
                }

                compactedDeletedKeys = overlayDeletedKeys_;
            }
            else
            {
                compactedKeys.reserve(compactedData.overlayIndexByKey.size());
                compactedDeletedKeys.reserve(compactedData.overlayDeletedKeys.size());

                for (const auto& pair : compactedData.overlayIndexByKey)
                {
                    const auto key = pair.first;
                    const auto compactedIndex = pair.second;
                    if (compactedIndex >= compactedData.overlayRecords.size())
                    {
                        continue;
                    }

                    const auto currentIt = overlayIndexByKey_.find(key);
                    if (currentIt == overlayIndexByKey_.end()
                        || currentIt->second >= overlayRecords_.size()
                        || overlayDeletedKeys_.find(key) != overlayDeletedKeys_.end())
                    {
                        continue;
                    }

                    if (RecordsEquivalentForCompaction(
                        compactedData.overlayRecords[compactedIndex],
                        overlayRecords_[currentIt->second]))
                    {
                        compactedKeys.insert(key);
                    }
                }

                for (const auto key : compactedData.overlayDeletedKeys)
                {
                    if (overlayDeletedKeys_.find(key) != overlayDeletedKeys_.end())
                    {
                        compactedDeletedKeys.insert(key);
                    }
                }
            }

            overlaySegmentBaseRecordCountOverride_ = static_cast<std::uint64_t>(compactedRecordCount);
            overlayCompactedKeys_ = std::move(compactedKeys);
            overlayCompactedDeletedKeys_ = std::move(compactedDeletedKeys);
            NativeLogWrite(
                "[NATIVE SNAPSHOT SAVE] compacted-overlay-stable-written records="
                + std::to_string(compactedRecordCount)
                + " liveBaseRecords=" + std::to_string(GetRecordCount_NoLock())
                + " generationChanged=" + std::string(generationChanged ? "true" : "false")
                + " compactedOverlayKeys=" + std::to_string(overlayCompactedKeys_.size())
                + " compactedDeleteKeys=" + std::to_string(overlayCompactedDeletedKeys_.size()));
            guard.unlock();
            SaveOverlaySegmentSnapshotOutsideExclusiveLock(true);
        }

        static bool RecordsEquivalentForCompaction(const Record& left, const Record& right)
        {
            return left.frn == right.frn
                && left.parentFrn == right.parentFrn
                && left.driveLetter == right.driveLetter
                && left.isDirectory == right.isDirectory
                && left.originalName == right.originalName
                && left.lowerName == right.lowerName;
        }

        static void BuildTypeBucketsForRecords(
            const std::vector<Record>& records,
            std::vector<std::uint32_t>& allIds,
            std::vector<std::uint32_t>& directoryIds,
            std::vector<std::uint32_t>& launchableIds,
            std::vector<std::uint32_t>& scriptIds,
            std::vector<std::uint32_t>& logIds,
            std::vector<std::uint32_t>& configIds,
            std::unordered_map<std::wstring, std::vector<std::uint32_t>>& extensionIds)
        {
            allIds.clear();
            directoryIds.clear();
            launchableIds.clear();
            scriptIds.clear();
            logIds.clear();
            configIds.clear();
            extensionIds.clear();

            allIds.reserve(records.size());
            directoryIds.reserve(records.size() / 8);
            launchableIds.reserve(records.size() / 8);
            extensionIds.reserve(records.size() / 8);

            for (std::uint32_t i = 0; i < records.size(); ++i)
            {
                const auto& record = records[i];
                allIds.push_back(i);
                if (record.isDirectory)
                {
                    directoryIds.push_back(i);
                    continue;
                }

                const auto extension = GetExtension(record.lowerName);
                if (!extension.empty())
                {
                    extensionIds[extension].push_back(i);
                }
                if (IsLaunchableExtension(extension))
                {
                    launchableIds.push_back(i);
                }
                if (IsScriptExtension(extension))
                {
                    scriptIds.push_back(i);
                }
                if (IsLogExtension(extension))
                {
                    logIds.push_back(i);
                }
                if (IsConfigExtension(extension))
                {
                    configIds.push_back(i);
                }
            }
        }

        bool ShouldForceCompactOverlay_NoLock() const
        {
            return overlayRecords_.size() + overlayDeletedKeys_.size() >= OverlayCompactionThreshold;
        }

        bool CanCompactOverlayNow(bool ignoreOverlayMutation = false) const
        {
            if (activeSearchCount_.load(std::memory_order_acquire) > 0)
            {
                return false;
            }

            const auto lastSearchCompleted = lastSearchCompletedTickMs_.load(std::memory_order_acquire);
            const auto lastOverlayMutation = lastOverlayMutationTickMs_.load(std::memory_order_acquire);
            const auto nowMs = SteadyClockMilliseconds();
            if (!ignoreOverlayMutation
                && lastOverlayMutation > 0
                && nowMs - lastOverlayMutation < SnapshotCompactionIdleMilliseconds.count())
            {
                return false;
            }

            if (lastSearchCompleted == 0)
            {
                return true;
            }

            const auto idleMs = nowMs - lastSearchCompleted;
            return idleMs >= SnapshotCompactionIdleMilliseconds.count();
        }

        bool WriteOverlaySegmentSnapshot_NoLock() const
        {
            return WriteOverlaySegmentSnapshotCore_NoLock(false);
        }

        void BuildOverlaySegmentSnapshotData_NoLock(OverlaySegmentSnapshotData& data) const
        {
            data.generation = generationId_.load(std::memory_order_acquire);
            data.stableRecordCount = overlaySegmentBaseRecordCountOverride_ == 0
                ? static_cast<std::uint64_t>(GetRecordCount_NoLock())
                : overlaySegmentBaseRecordCountOverride_;

            data.volumes.clear();
            data.volumes.reserve(volumes_.size());
            for (const auto& pair : volumes_)
            {
                VolumeState volume;
                volume.driveLetter = pair.second.driveLetter;
                volume.nextUsn = pair.second.nextUsn;
                volume.journalId = pair.second.journalId;
                data.volumes.emplace(pair.first, std::move(volume));
            }

            data.overlayRecords = overlayRecords_;
            data.overlayIndexByKey = overlayIndexByKey_;
            data.overlayDeletedKeys = overlayDeletedKeys_;
            data.compactedKeys = overlayCompactedKeys_;
            data.compactedDeletedKeys = overlayCompactedDeletedKeys_;
        }

        std::uint64_t BuildCurrentOverlaySegmentSnapshotSignature_NoLock() const
        {
            std::uint64_t value = generationId_.load(std::memory_order_acquire);
            const auto mix = [&value](std::uint64_t next)
            {
                value ^= next + 0x9e3779b97f4a7c15ull + (value << 6) + (value >> 2);
            };

            const auto stableRecordCount = overlaySegmentBaseRecordCountOverride_ == 0
                ? static_cast<std::uint64_t>(GetRecordCount_NoLock())
                : overlaySegmentBaseRecordCountOverride_;
            mix(stableRecordCount);
            mix(volumes_.size());
            mix(overlayRecords_.size());
            mix(overlayIndexByKey_.size());
            mix(overlayDeletedKeys_.size());
            mix(overlayCompactedKeys_.size());
            mix(overlayCompactedDeletedKeys_.size());
            return value == 0 ? 1 : value;
        }

        bool CurrentOverlaySegmentSnapshotAlreadySaved_NoLock() const
        {
            const auto signature = BuildCurrentOverlaySegmentSnapshotSignature_NoLock();
            return signature != 0
                && lastOverlaySegmentSaveSignature_.load(std::memory_order_acquire) == signature;
        }

        static std::uint64_t BuildOverlaySegmentSnapshotSignature(const OverlaySegmentSnapshotData& data)
        {
            std::uint64_t value = data.generation;
            const auto mix = [&value](std::uint64_t next)
            {
                value ^= next + 0x9e3779b97f4a7c15ull + (value << 6) + (value >> 2);
            };

            mix(data.stableRecordCount);
            mix(data.volumes.size());
            mix(data.overlayRecords.size());
            mix(data.overlayIndexByKey.size());
            mix(data.overlayDeletedKeys.size());
            mix(data.compactedKeys.size());
            mix(data.compactedDeletedKeys.size());
            return value == 0 ? 1 : value;
        }

        bool WriteOverlaySegmentSnapshotData(const OverlaySegmentSnapshotData& data) const
        {
            const auto signature = BuildOverlaySegmentSnapshotSignature(data);
            std::lock_guard<std::mutex> saveGuard(overlaySegmentSaveMutex_);
            if (signature != 0
                && lastOverlaySegmentSaveSignature_.load(std::memory_order_acquire) == signature)
            {
                lastOverlaySegmentSaveTickMs_.store(SteadyClockMilliseconds(), std::memory_order_release);
                NativeLogWrite(
                    "[NATIVE V2 OVERLAY SAVE] skipped reason=same-generation-signature"
                    " generation=" + std::to_string(data.generation)
                    + " overlayRecords=" + std::to_string(data.overlayRecords.size())
                    + " overlayDeletes=" + std::to_string(data.overlayDeletedKeys.size()));
                return true;
            }

            const bool saved = WriteV2OverlaySnapshotFile(
                data.stableRecordCount,
                data.volumes,
                data.overlayRecords,
                data.overlayIndexByKey,
                data.overlayDeletedKeys,
                data.compactedKeys,
                data.compactedDeletedKeys,
                lastOverlaySegmentSaveTickMs_);
            if (saved && signature != 0)
            {
                lastOverlaySegmentSaveSignature_.store(signature, std::memory_order_release);
            }

            return saved;
        }

        bool SaveOverlaySegmentSnapshotOutsideExclusiveLock(bool force) const
        {
            if (!force)
            {
                const auto nowMs = SteadyClockMilliseconds();
                const auto lastSaveMs = lastOverlaySegmentSaveTickMs_.load(std::memory_order_acquire);
                if (lastSaveMs > 0
                    && nowMs - lastSaveMs < OverlaySegmentSaveDebounceMilliseconds.count())
                {
                    return true;
                }
            }

            OverlaySegmentSnapshotData data;
            {
                std::shared_lock<std::shared_mutex> guard(mutex_);
                if (CurrentOverlaySegmentSnapshotAlreadySaved_NoLock())
                {
                    lastOverlaySegmentSaveTickMs_.store(SteadyClockMilliseconds(), std::memory_order_release);
                    NativeLogWrite("[NATIVE V2 OVERLAY SAVE] skipped reason=same-generation-signature-precopy");
                    return true;
                }

                BuildOverlaySegmentSnapshotData_NoLock(data);
            }

            return WriteOverlaySegmentSnapshotData(data);
        }

        bool WriteOverlaySegmentSnapshotCore_NoLock(bool force) const
        {
            if (!force)
            {
                const auto nowMs = SteadyClockMilliseconds();
                const auto lastSaveMs = lastOverlaySegmentSaveTickMs_.load(std::memory_order_acquire);
                if (lastSaveMs > 0
                    && nowMs - lastSaveMs < OverlaySegmentSaveDebounceMilliseconds.count())
                {
                    return true;
                }
            }

            OverlaySegmentSnapshotData data;
            if (CurrentOverlaySegmentSnapshotAlreadySaved_NoLock())
            {
                lastOverlaySegmentSaveTickMs_.store(SteadyClockMilliseconds(), std::memory_order_release);
                NativeLogWrite("[NATIVE V2 OVERLAY SAVE] skipped reason=same-generation-signature-precopy");
                return true;
            }

            BuildOverlaySegmentSnapshotData_NoLock(data);
            return WriteOverlaySegmentSnapshotData(data);
        }

        bool WriteCurrentGenerationSidecars_NoLock() const
        {
            if (records_.empty() || volumes_.empty() || !v2_.ready)
            {
                return false;
            }

            WriteV2RecordsSnapshotFile(records_, volumes_);
            WriteV2PostingsSnapshotFile(records_.size(), v2_);
            WriteV2RuntimeSnapshotFile(records_.size(), v2_);
            WriteV2TypeBucketsSnapshotFile(records_.size(), allIds_, directoryIds_, launchableIds_, scriptIds_, logIds_, configIds_, extensionIds_);
            return true;
        }

        void QueueSnapshotSave()
        {
            if (hasShutdown_)
            {
                return;
            }

            {
                std::lock_guard<std::mutex> guard(snapshotRequestMutex_);
                snapshotSavePending_ = true;
            }

            snapshotRequestCv_.notify_one();
        }

        void QueueSnapshotSave_NoLock() const
        {
            if (GetRecordCount_NoLock() == 0 || volumes_.empty() || hasShutdown_)
            {
                return;
            }

            {
                std::lock_guard<std::mutex> guard(snapshotRequestMutex_);
                snapshotSavePending_ = true;
            }

            snapshotRequestCv_.notify_one();
        }

        static void WriteSnapshotFile(
            const std::vector<Record>& records,
            const std::unordered_map<wchar_t, VolumeState>& volumes)
        {
            const auto totalStart = std::chrono::steady_clock::now();
            if (records.empty() || volumes.empty())
            {
                return;
            }

            const auto snapshotPath = GetSnapshotPath();
            std::filesystem::create_directories(snapshotPath.parent_path());

            HANDLE writeMutex = CreateMutexW(nullptr, FALSE, SnapshotWriteMutexName);
            if (writeMutex == nullptr)
            {
                return;
            }

            const auto waitResult = WaitForSingleObject(writeMutex, SnapshotWriteLockTimeoutMilliseconds);
            if (waitResult != WAIT_OBJECT_0 && waitResult != WAIT_ABANDONED)
            {
                CloseHandle(writeMutex);
                return;
            }

            const auto tempPath = snapshotPath.wstring()
                + L"."
                + std::to_wstring(GetCurrentProcessId())
                + L"."
                + std::to_wstring(GetTickCount64())
                + L".tmp";

            std::ofstream output(tempPath, std::ios::binary | std::ios::trunc);
            if (!output)
            {
                ReleaseMutex(writeMutex);
                CloseHandle(writeMutex);
                return;
            }

            const auto orderedVolumes = GetOrderedVolumes(volumes);
            const auto stringPool = BuildSnapshotStringPool(records, orderedVolumes);
            std::unordered_map<std::wstring, std::int32_t> stringIndexMap;
            stringIndexMap.reserve(stringPool.size());
            for (std::size_t i = 0; i < stringPool.size(); ++i)
            {
                stringIndexMap[stringPool[i]] = static_cast<std::int32_t>(i);
            }

            output.write(reinterpret_cast<const char*>(&SnapshotVersion4), sizeof(SnapshotVersion4));
            const auto stringPoolCount = static_cast<std::int32_t>(stringPool.size());
            output.write(reinterpret_cast<const char*>(&stringPoolCount), sizeof(stringPoolCount));
            for (const auto& value : stringPool)
            {
                if (!WriteSnapshotString(output, value))
                {
                    output.close();
                    DeleteFileW(tempPath.c_str());
                    ReleaseMutex(writeMutex);
                    CloseHandle(writeMutex);
                    return;
                }
            }

            const auto recordCount = static_cast<std::int32_t>(records.size());
            output.write(reinterpret_cast<const char*>(&recordCount), sizeof(recordCount));
            for (const auto& record : records)
            {
                const auto it = stringIndexMap.find(record.originalName);
                const auto nameIndex = it == stringIndexMap.end() ? 0 : it->second;
                output.write(reinterpret_cast<const char*>(&nameIndex), sizeof(nameIndex));
                output.write(reinterpret_cast<const char*>(&record.parentFrn), sizeof(record.parentFrn));
                if (!WriteSnapshotChar(output, record.driveLetter) || !WriteSnapshotBool(output, record.isDirectory))
                {
                    output.close();
                    DeleteFileW(tempPath.c_str());
                    ReleaseMutex(writeMutex);
                    CloseHandle(writeMutex);
                    return;
                }
                output.write(reinterpret_cast<const char*>(&record.frn), sizeof(record.frn));
            }

            const auto volumeCount = static_cast<std::int32_t>(orderedVolumes.size());
            output.write(reinterpret_cast<const char*>(&volumeCount), sizeof(volumeCount));
            for (const auto* volume : orderedVolumes)
            {
                if (!WriteSnapshotChar(output, volume->driveLetter))
                {
                    output.close();
                    DeleteFileW(tempPath.c_str());
                    ReleaseMutex(writeMutex);
                    CloseHandle(writeMutex);
                    return;
                }
                output.write(reinterpret_cast<const char*>(&volume->nextUsn), sizeof(volume->nextUsn));
                output.write(reinterpret_cast<const char*>(&volume->journalId), sizeof(volume->journalId));
                const auto volumeRecordCount = CountRecordsForVolume(records, volume->driveLetter);
                const auto volumeMaxFrn = MaxFrnForVolume(records, volume->driveLetter);
                output.write(reinterpret_cast<const char*>(&volumeRecordCount), sizeof(volumeRecordCount));
                output.write(reinterpret_cast<const char*>(&volumeMaxFrn), sizeof(volumeMaxFrn));
                const auto complete = volume->enumerationComplete;
                if (!WriteSnapshotBool(output, complete))
                {
                    output.close();
                    DeleteFileW(tempPath.c_str());
                    ReleaseMutex(writeMutex);
                    CloseHandle(writeMutex);
                    return;
                }

                const auto orderedEntries = GetOrderedRecordsForVolume(records, volume->driveLetter);
                const auto frnCount = static_cast<std::int32_t>(orderedEntries.size());
                output.write(reinterpret_cast<const char*>(&frnCount), sizeof(frnCount));
                for (const auto* record : orderedEntries)
                {
                    const auto it = stringIndexMap.find(record->originalName);
                    const auto nameIndex = it == stringIndexMap.end() ? 0 : it->second;
                    output.write(reinterpret_cast<const char*>(&nameIndex), sizeof(nameIndex));
                    output.write(reinterpret_cast<const char*>(&record->frn), sizeof(record->frn));
                    output.write(reinterpret_cast<const char*>(&record->parentFrn), sizeof(record->parentFrn));
                    if (!WriteSnapshotBool(output, record->isDirectory))
                    {
                        output.close();
                        DeleteFileW(tempPath.c_str());
                        ReleaseMutex(writeMutex);
                        CloseHandle(writeMutex);
                        return;
                    }
                }
            }

            output.flush();
            const auto writeSucceeded = output.good();
            output.close();
            if (!writeSucceeded)
            {
                DeleteFileW(tempPath.c_str());
                ReleaseMutex(writeMutex);
                CloseHandle(writeMutex);
                return;
            }

            if (std::filesystem::exists(snapshotPath))
            {
                if (!ReplaceFileW(snapshotPath.c_str(), tempPath.c_str(), nullptr, REPLACEFILE_IGNORE_MERGE_ERRORS, nullptr, nullptr))
                {
                    DeleteFileW(tempPath.c_str());
                    ReleaseMutex(writeMutex);
                    CloseHandle(writeMutex);
                    return;
                }
            }
            else if (!MoveFileExW(tempPath.c_str(), snapshotPath.c_str(), MOVEFILE_REPLACE_EXISTING))
            {
                DeleteFileW(tempPath.c_str());
                ReleaseMutex(writeMutex);
                CloseHandle(writeMutex);
                return;
            }

            ReleaseMutex(writeMutex);
            CloseHandle(writeMutex);
            std::error_code fileSizeError;
            const auto fileBytes = std::filesystem::file_size(snapshotPath, fileSizeError);
            NativeLogWrite(
                "[NATIVE SNAPSHOT SAVE] elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - totalStart).count())
                + " version=" + std::to_string(SnapshotVersion4)
                + " fileBytes=" + std::to_string(fileSizeError ? 0 : fileBytes)
                + " records=" + std::to_string(records.size())
                + " volumes=" + std::to_string(volumes.size())
                + " frnEntries=" + std::to_string(CountLiveFrnEntries(volumes, records.size())));
        }

        static void WriteV2RecordsSnapshotFile(
            const std::vector<Record>& records,
            const std::unordered_map<wchar_t, VolumeState>& volumes)
        {
            const auto totalStart = std::chrono::steady_clock::now();
            if (records.empty() || volumes.empty())
            {
                return;
            }

            const auto path = GetV2RecordsSnapshotPath();
            std::filesystem::create_directories(path.parent_path());
            const auto tempPath = path.wstring()
                + L"."
                + std::to_wstring(GetCurrentProcessId())
                + L"."
                + std::to_wstring(GetTickCount64())
                + L".tmp";

            std::vector<NativeV2CompactRecord> compactRecords;
            compactRecords.reserve(records.size());
            std::wstring stringPool;
            std::uint64_t maxChars = 0;
            for (const auto& record : records)
            {
                maxChars += record.originalName.size() + record.lowerName.size();
            }

            if (maxChars <= static_cast<std::uint64_t>(std::numeric_limits<std::uint32_t>::max()))
            {
                stringPool.reserve(static_cast<std::size_t>(maxChars));
            }

            for (const auto& record : records)
            {
                if (record.originalName.size() > std::numeric_limits<std::uint16_t>::max()
                    || record.lowerName.size() > std::numeric_limits<std::uint16_t>::max()
                    || stringPool.size() > std::numeric_limits<std::uint32_t>::max())
                {
                    return;
                }

                NativeV2CompactRecord compact;
                compact.frn = record.frn;
                compact.parentFrn = record.parentFrn;
                compact.originalOffset = static_cast<std::uint32_t>(stringPool.size());
                compact.originalLength = static_cast<std::uint16_t>(record.originalName.size());
                stringPool.append(record.originalName);
                if (stringPool.size() > std::numeric_limits<std::uint32_t>::max())
                {
                    return;
                }

                compact.lowerOffset = static_cast<std::uint32_t>(stringPool.size());
                compact.lowerLength = static_cast<std::uint16_t>(record.lowerName.size());
                stringPool.append(record.lowerName);
                compact.drive = static_cast<std::uint8_t>(record.driveLetter & 0xFF);
                compact.flags = record.isDirectory ? 0x1 : 0x0;
                compactRecords.push_back(compact);
            }

            const auto orderedVolumes = GetOrderedVolumes(volumes);
            std::ofstream output(tempPath, std::ios::binary | std::ios::trunc);
            if (!output)
            {
                return;
            }

            const auto version = V2RecordsSnapshotVersion2;
            const auto recordCount = static_cast<std::uint32_t>(compactRecords.size());
            const auto volumeCount = static_cast<std::uint32_t>(orderedVolumes.size());
            const auto stringPoolChars = static_cast<std::uint32_t>(stringPool.size());
            output.write(reinterpret_cast<const char*>(&version), sizeof(version));
            output.write(reinterpret_cast<const char*>(&recordCount), sizeof(recordCount));
            output.write(reinterpret_cast<const char*>(&volumeCount), sizeof(volumeCount));
            output.write(reinterpret_cast<const char*>(&stringPoolChars), sizeof(stringPoolChars));
            output.write(reinterpret_cast<const char*>(compactRecords.data()),
                static_cast<std::streamsize>(compactRecords.size() * sizeof(NativeV2CompactRecord)));
            if (!stringPool.empty())
            {
                output.write(reinterpret_cast<const char*>(stringPool.data()),
                    static_cast<std::streamsize>(stringPool.size() * sizeof(wchar_t)));
            }

            for (const auto* volume : orderedVolumes)
            {
                if (!WriteSnapshotChar(output, volume->driveLetter))
                {
                    output.close();
                    DeleteFileW(tempPath.c_str());
                    return;
                }

                output.write(reinterpret_cast<const char*>(&volume->nextUsn), sizeof(volume->nextUsn));
                output.write(reinterpret_cast<const char*>(&volume->journalId), sizeof(volume->journalId));
                const auto volumeRecordCount = CountRecordsForVolume(records, volume->driveLetter);
                const auto volumeMaxFrn = MaxFrnForVolume(records, volume->driveLetter);
                output.write(reinterpret_cast<const char*>(&volumeRecordCount), sizeof(volumeRecordCount));
                output.write(reinterpret_cast<const char*>(&volumeMaxFrn), sizeof(volumeMaxFrn));
                const auto complete = volume->enumerationComplete;
                if (!WriteSnapshotBool(output, complete))
                {
                    output.close();
                    DeleteFileW(tempPath.c_str());
                    return;
                }
            }

            output.flush();
            const auto ok = output.good();
            output.close();
            if (!ok)
            {
                DeleteFileW(tempPath.c_str());
                return;
            }

            if (std::filesystem::exists(path))
            {
                if (!ReplaceFileW(path.c_str(), tempPath.c_str(), nullptr, REPLACEFILE_IGNORE_MERGE_ERRORS, nullptr, nullptr))
                {
                    DeleteFileW(tempPath.c_str());
                    return;
                }
            }
            else if (!MoveFileExW(tempPath.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING))
            {
                DeleteFileW(tempPath.c_str());
                return;
            }

            std::error_code fileSizeError;
            const auto fileBytes = std::filesystem::file_size(path, fileSizeError);
            NativeLogWrite(
                "[NATIVE V2 RECORDS SAVE] success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - totalStart).count())
                + " version=" + std::to_string(V2RecordsSnapshotVersion2)
                + " fileBytes=" + std::to_string(fileSizeError ? 0 : fileBytes)
                + " records=" + std::to_string(records.size())
                + " volumes=" + std::to_string(volumes.size())
                + " stringPoolChars=" + std::to_string(stringPool.size()));
        }

        static bool WriteV2OverlaySnapshotFile(
            std::uint64_t stableRecordCount,
            const std::unordered_map<wchar_t, VolumeState>& volumes,
            const std::vector<Record>& overlayRecords,
            const std::unordered_map<std::uint64_t, std::uint32_t>& overlayIndexByKey,
            const std::unordered_set<std::uint64_t>& overlayDeletedKeys,
            const std::unordered_set<std::uint64_t>& compactedKeys,
            const std::unordered_set<std::uint64_t>& compactedDeletedKeys,
            std::atomic<long long>& lastSaveTickMs)
        {
            const auto start = std::chrono::steady_clock::now();
            const auto path = GetV2OverlaySnapshotPath();
            const auto liveOverlayCount = overlayIndexByKey.size() > compactedKeys.size()
                ? overlayIndexByKey.size() - compactedKeys.size()
                : 0;
            const auto liveDeleteCount = overlayDeletedKeys.size() > compactedDeletedKeys.size()
                ? overlayDeletedKeys.size() - compactedDeletedKeys.size()
                : 0;
            if (liveOverlayCount == 0 && liveDeleteCount == 0)
            {
                DeleteFileW(path.c_str());
                lastSaveTickMs.store(SteadyClockMilliseconds(), std::memory_order_release);
                NativeLogWrite(
                    "[NATIVE V2 OVERLAY SAVE] deleted-empty stableRecords="
                    + std::to_string(stableRecordCount));
                return true;
            }

            std::filesystem::create_directories(path.parent_path());
            const auto tempPath = path.wstring()
                + L"."
                + std::to_wstring(GetCurrentProcessId())
                + L"."
                + std::to_wstring(GetTickCount64())
                + L".tmp";

            std::ofstream output(tempPath, std::ios::binary | std::ios::trunc);
            if (!output)
            {
                return false;
            }

            std::vector<const Record*> liveOverlayRecords;
            liveOverlayRecords.reserve(overlayIndexByKey.size());
            for (std::uint32_t i = 0; i < overlayRecords.size(); ++i)
            {
                const auto& record = overlayRecords[i];
                const auto key = MakeRecordKey(record.driveLetter, record.frn);
                const auto liveIt = overlayIndexByKey.find(key);
                if (liveIt == overlayIndexByKey.end()
                    || liveIt->second != i
                    || overlayDeletedKeys.find(key) != overlayDeletedKeys.end()
                    || compactedKeys.find(key) != compactedKeys.end())
                {
                    continue;
                }

                liveOverlayRecords.push_back(&record);
            }

            const auto version = V2OverlaySnapshotVersion1;
            const auto baseRecordCount = stableRecordCount;
            const auto orderedVolumes = GetOrderedVolumes(volumes);
            const auto volumeCount = static_cast<std::uint32_t>(orderedVolumes.size());
            const auto recordCount = static_cast<std::uint32_t>(liveOverlayRecords.size());
            const auto tombstoneCount = static_cast<std::uint32_t>(liveDeleteCount);
            output.write(reinterpret_cast<const char*>(&version), sizeof(version));
            output.write(reinterpret_cast<const char*>(&baseRecordCount), sizeof(baseRecordCount));
            output.write(reinterpret_cast<const char*>(&volumeCount), sizeof(volumeCount));
            output.write(reinterpret_cast<const char*>(&recordCount), sizeof(recordCount));
            output.write(reinterpret_cast<const char*>(&tombstoneCount), sizeof(tombstoneCount));

            for (const auto* volume : orderedVolumes)
            {
                if (!WriteSnapshotChar(output, volume->driveLetter))
                {
                    output.close();
                    DeleteFileW(tempPath.c_str());
                    return false;
                }

                output.write(reinterpret_cast<const char*>(&volume->nextUsn), sizeof(volume->nextUsn));
                output.write(reinterpret_cast<const char*>(&volume->journalId), sizeof(volume->journalId));
            }

            for (const auto* record : liveOverlayRecords)
            {
                if (!WriteSnapshotChar(output, record->driveLetter))
                {
                    output.close();
                    DeleteFileW(tempPath.c_str());
                    return false;
                }

                output.write(reinterpret_cast<const char*>(&record->frn), sizeof(record->frn));
                output.write(reinterpret_cast<const char*>(&record->parentFrn), sizeof(record->parentFrn));
                if (!WriteSnapshotBool(output, record->isDirectory)
                    || !WriteSnapshotString(output, record->originalName)
                    || !WriteSnapshotString(output, record->lowerName))
                {
                    output.close();
                    DeleteFileW(tempPath.c_str());
                    return false;
                }
            }

            for (const auto key : overlayDeletedKeys)
            {
                if (compactedDeletedKeys.find(key) != compactedDeletedKeys.end())
                {
                    continue;
                }

                const auto drive = static_cast<wchar_t>((key >> 56) & 0xFF);
                const auto frn = key & 0x00FFFFFFFFFFFFFFull;
                if (!WriteSnapshotChar(output, drive))
                {
                    output.close();
                    DeleteFileW(tempPath.c_str());
                    return false;
                }

                output.write(reinterpret_cast<const char*>(&frn), sizeof(frn));
            }

            output.flush();
            const auto ok = output.good();
            output.close();
            if (!ok)
            {
                DeleteFileW(tempPath.c_str());
                return false;
            }

            if (std::filesystem::exists(path))
            {
                if (!ReplaceFileW(path.c_str(), tempPath.c_str(), nullptr, REPLACEFILE_IGNORE_MERGE_ERRORS, nullptr, nullptr))
                {
                    DeleteFileW(tempPath.c_str());
                    return false;
                }
            }
            else if (!MoveFileExW(tempPath.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING))
            {
                DeleteFileW(tempPath.c_str());
                return false;
            }

            lastSaveTickMs.store(SteadyClockMilliseconds(), std::memory_order_release);
            NativeLogWrite(
                "[NATIVE V2 OVERLAY SAVE] success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - start).count())
                + " stableRecords=" + std::to_string(stableRecordCount)
                + " overlayRecords=" + std::to_string(liveOverlayRecords.size())
                + " overlayDeletes=" + std::to_string(liveDeleteCount)
                + " volumes=" + std::to_string(volumes.size()));
            return true;
        }

        static void DeleteV2OverlaySnapshotFile()
        {
            DeleteFileW(GetV2OverlaySnapshotPath().c_str());
        }

        bool TryLoadV2PostingsSnapshot_NoLock()
        {
            const auto path = GetV2PostingsSnapshotPath();
            if (!std::filesystem::exists(path))
            {
                NativeLogWrite("[NATIVE V2 POSTINGS LOAD] miss reason=missing");
                return false;
            }

            const auto start = std::chrono::steady_clock::now();
            std::ifstream input(path, std::ios::binary);
            if (!input)
            {
                return false;
            }

            std::int32_t version = 0;
            std::int32_t recordCount = 0;
            input.read(reinterpret_cast<char*>(&version), sizeof(version));
            input.read(reinterpret_cast<char*>(&recordCount), sizeof(recordCount));
            if (!input.good()
                || version != V2PostingsSnapshotVersion1
                || recordCount != static_cast<std::int32_t>(GetRecordCount_NoLock()))
            {
                NativeLogWrite("[NATIVE V2 POSTINGS LOAD] miss reason=version-or-record-count");
                return false;
            }

            std::uint32_t entryCount = 0;
            input.read(reinterpret_cast<char*>(&entryCount), sizeof(entryCount));
            if (!input.good())
            {
                return false;
            }

            std::vector<NativeV2BucketEntry> entries(entryCount);
            for (auto& entry : entries)
            {
                input.read(reinterpret_cast<char*>(&entry.key), sizeof(entry.key));
                input.read(reinterpret_cast<char*>(&entry.offset), sizeof(entry.offset));
                input.read(reinterpret_cast<char*>(&entry.count), sizeof(entry.count));
                input.read(reinterpret_cast<char*>(&entry.byteCount), sizeof(entry.byteCount));
                if (!input.good())
                {
                    return false;
                }
            }

            std::uint32_t byteCount = 0;
            input.read(reinterpret_cast<char*>(&byteCount), sizeof(byteCount));
            if (!input.good())
            {
                return false;
            }

            std::vector<std::uint8_t> postings(byteCount);
            if (byteCount > 0)
            {
                input.read(reinterpret_cast<char*>(postings.data()), byteCount);
                if (!input.good())
                {
                    return false;
                }
            }

            v2_.Clear();
            v2_.trigramEntries = std::move(entries);
            v2_.trigramPostings = std::move(postings);
            v2_.postingsBytes =
                (static_cast<std::uint64_t>(v2_.trigramEntries.size()) * sizeof(NativeV2BucketEntry))
                + static_cast<std::uint64_t>(v2_.trigramPostings.size());
            NativeLogWrite(
                "[NATIVE V2 POSTINGS LOAD] success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - start).count())
                + " buckets=" + std::to_string(v2_.trigramEntries.size())
                + " bytes=" + std::to_string(v2_.trigramPostings.size()));
            return true;
        }

        static void WriteV2PostingsSnapshotFile(std::size_t recordCount, const NativeV2Accelerator& v2)
        {
            if (!v2.ready || v2.trigramEntries.empty() || v2.trigramPostings.empty())
            {
                return;
            }

            const auto start = std::chrono::steady_clock::now();
            const auto path = GetV2PostingsSnapshotPath();
            std::filesystem::create_directories(path.parent_path());
            const auto tempPath = path.wstring()
                + L"."
                + std::to_wstring(GetCurrentProcessId())
                + L"."
                + std::to_wstring(GetTickCount64())
                + L".tmp";

            std::ofstream output(tempPath, std::ios::binary | std::ios::trunc);
            if (!output)
            {
                return;
            }

            const auto version = V2PostingsSnapshotVersion1;
            const auto count = static_cast<std::int32_t>(recordCount);
            const auto entryCount = static_cast<std::uint32_t>(v2.trigramEntries.size());
            const auto byteCount = static_cast<std::uint32_t>(v2.trigramPostings.size());
            output.write(reinterpret_cast<const char*>(&version), sizeof(version));
            output.write(reinterpret_cast<const char*>(&count), sizeof(count));
            output.write(reinterpret_cast<const char*>(&entryCount), sizeof(entryCount));
            for (const auto& entry : v2.trigramEntries)
            {
                output.write(reinterpret_cast<const char*>(&entry.key), sizeof(entry.key));
                output.write(reinterpret_cast<const char*>(&entry.offset), sizeof(entry.offset));
                output.write(reinterpret_cast<const char*>(&entry.count), sizeof(entry.count));
                output.write(reinterpret_cast<const char*>(&entry.byteCount), sizeof(entry.byteCount));
            }

            output.write(reinterpret_cast<const char*>(&byteCount), sizeof(byteCount));
            if (byteCount > 0)
            {
                output.write(reinterpret_cast<const char*>(v2.trigramPostings.data()), byteCount);
            }

            output.flush();
            const auto ok = output.good();
            output.close();
            if (!ok)
            {
                DeleteFileW(tempPath.c_str());
                return;
            }

            if (std::filesystem::exists(path))
            {
                if (!ReplaceFileW(path.c_str(), tempPath.c_str(), nullptr, REPLACEFILE_IGNORE_MERGE_ERRORS, nullptr, nullptr))
                {
                    DeleteFileW(tempPath.c_str());
                    return;
                }
            }
            else if (!MoveFileExW(tempPath.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING))
            {
                DeleteFileW(tempPath.c_str());
                return;
            }

            NativeLogWrite(
                "[NATIVE V2 POSTINGS SAVE] success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - start).count())
                + " buckets=" + std::to_string(v2.trigramEntries.size())
                + " bytes=" + std::to_string(v2.trigramPostings.size()));
        }

        bool TryLoadV2RuntimeSnapshot_NoLock()
        {
            const auto path = GetV2RuntimeSnapshotPath();
            if (!std::filesystem::exists(path))
            {
                NativeLogWrite("[NATIVE V2 RUNTIME LOAD] miss reason=missing");
                return false;
            }

            const auto start = std::chrono::steady_clock::now();
            std::ifstream input(path, std::ios::binary);
            if (!input)
            {
                return false;
            }

            std::int32_t version = 0;
            std::int32_t recordCount = 0;
            input.read(reinterpret_cast<char*>(&version), sizeof(version));
            input.read(reinterpret_cast<char*>(&recordCount), sizeof(recordCount));
            if (!input.good()
                || (version != V2RuntimeSnapshotVersion7 && version != 6 && version != 5 && version != 4 && version != 3 && version != 2)
                || recordCount != static_cast<std::int32_t>(GetRecordCount_NoLock()))
            {
                NativeLogWrite("[NATIVE V2 RUNTIME LOAD] miss reason=version-or-record-count");
                return false;
            }

            input.read(reinterpret_cast<char*>(v2_.asciiCharEntries.data()),
                static_cast<std::streamsize>(v2_.asciiCharEntries.size() * sizeof(NativeV2ShortBucketEntry)));
            input.read(reinterpret_cast<char*>(v2_.asciiBigramEntries.data()),
                static_cast<std::streamsize>(v2_.asciiBigramEntries.size() * sizeof(NativeV2ShortBucketEntry)));
            if (version >= 3)
            {
                if (!ReadDriveCountVector(input, v2_.asciiCharDriveCounts)
                    || !ReadDriveCountVector(input, v2_.asciiBigramDriveCounts))
                {
                    return false;
                }
            }
            else
            {
                v2_.asciiCharDriveCounts.clear();
                v2_.asciiBigramDriveCounts.clear();
            }

            if (version >= 4)
            {
                if (!ReadDrivePageVector(input, v2_.asciiCharDrivePages)
                    || !ReadDrivePageVector(input, v2_.asciiBigramDrivePages))
                {
                    return false;
                }
            }
            else
            {
                v2_.asciiCharDrivePages.clear();
                v2_.asciiBigramDrivePages.clear();
            }

            if (!input.good()
                || !ReadByteVector(input, v2_.asciiCharCompressedPostings)
                || !ReadByteVector(input, v2_.asciiBigramCompressedPostings)
                || !ReadByteVector(input, v2_.nonAsciiShortCompressedPostings)
                || !ReadUInt32Vector(input, v2_.parentOrder))
            {
                return false;
            }

            std::uint32_t nonAsciiEntryCount = 0;
            input.read(reinterpret_cast<char*>(&nonAsciiEntryCount), sizeof(nonAsciiEntryCount));
            if (!input.good())
            {
                return false;
            }

            v2_.nonAsciiShortEntries.clear();
            v2_.nonAsciiShortEntries.reserve(nonAsciiEntryCount);
            for (std::uint32_t i = 0; i < nonAsciiEntryCount; ++i)
            {
                std::uint64_t key = 0;
                NativeV2ShortBucketEntry entry;
                input.read(reinterpret_cast<char*>(&key), sizeof(key));
                input.read(reinterpret_cast<char*>(&entry), sizeof(entry));
                if (!input.good())
                {
                    return false;
                }

                v2_.nonAsciiShortEntries[key] = entry;
            }

            if (!ReadChildBucketEntries(input, v2_.childEntries)
                || !ReadUInt32Vector(input, v2_.childRecordIds))
            {
                return false;
            }

            if (version >= 6)
            {
                if (!ReadUInt32Vector(input, v2_.subtreeEnter)
                    || !ReadUInt32Vector(input, v2_.subtreeExit)
                    || !ReadUInt32Vector(input, v2_.subtreeRecordIds))
                {
                    return false;
                }
            }
            else
            {
                BuildSubtreeIntervals_NoLock();
            }

            if (version >= V2RuntimeSnapshotVersion7)
            {
                if (!ReadBucketEntryVector(input, v2_.prefix4Entries)
                    || !ReadByteVector(input, v2_.prefix4Postings))
                {
                    return false;
                }
            }
            else
            {
                v2_.prefix4Entries.clear();
                v2_.prefix4Postings.clear();
            }

            if (version >= 3 && version < 4)
            {
                BuildShortDrivePagesFromRuntime_NoLock();
            }

            if (version < 3)
            {
                NativeLogWrite("[NATIVE V2 RUNTIME LOAD] compatibility mode=v2-without-drive-counts");
            }

            NativeLogWrite(
                "[NATIVE V2 RUNTIME LOAD] success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - start).count())
                + " shortBytes=" + std::to_string(
                    v2_.asciiCharCompressedPostings.size()
                    + v2_.asciiBigramCompressedPostings.size()
                    + v2_.nonAsciiShortCompressedPostings.size())
                + " prefix4Buckets=" + std::to_string(v2_.prefix4Entries.size())
                + " prefix4Bytes=" + std::to_string(v2_.prefix4Postings.size())
                + " parentOrder=" + std::to_string(v2_.parentOrder.size())
                + " childBuckets=" + std::to_string(v2_.childEntries.size())
                + " childIds=" + std::to_string(v2_.childRecordIds.size()));
            return true;
        }

        static void WriteV2RuntimeSnapshotFile(std::size_t recordCount, const NativeV2Accelerator& v2)
        {
            if (!v2.ready || v2.parentOrder.empty())
            {
                return;
            }

            const auto start = std::chrono::steady_clock::now();
            const auto path = GetV2RuntimeSnapshotPath();
            std::filesystem::create_directories(path.parent_path());
            const auto tempPath = path.wstring()
                + L"."
                + std::to_wstring(GetCurrentProcessId())
                + L"."
                + std::to_wstring(GetTickCount64())
                + L".tmp";

            std::ofstream output(tempPath, std::ios::binary | std::ios::trunc);
            if (!output)
            {
                return;
            }

            const auto version = V2RuntimeSnapshotVersion7;
            const auto count = static_cast<std::int32_t>(recordCount);
            output.write(reinterpret_cast<const char*>(&version), sizeof(version));
            output.write(reinterpret_cast<const char*>(&count), sizeof(count));
            output.write(reinterpret_cast<const char*>(v2.asciiCharEntries.data()),
                static_cast<std::streamsize>(v2.asciiCharEntries.size() * sizeof(NativeV2ShortBucketEntry)));
            output.write(reinterpret_cast<const char*>(v2.asciiBigramEntries.data()),
                static_cast<std::streamsize>(v2.asciiBigramEntries.size() * sizeof(NativeV2ShortBucketEntry)));
            WriteDriveCountVector(output, v2.asciiCharDriveCounts);
            WriteDriveCountVector(output, v2.asciiBigramDriveCounts);
            WriteDrivePageVector(output, v2.asciiCharDrivePages);
            WriteDrivePageVector(output, v2.asciiBigramDrivePages);
            WriteByteVector(output, v2.asciiCharCompressedPostings);
            WriteByteVector(output, v2.asciiBigramCompressedPostings);
            WriteByteVector(output, v2.nonAsciiShortCompressedPostings);
            WriteUInt32Vector(output, v2.parentOrder);

            const auto nonAsciiEntryCount = static_cast<std::uint32_t>(v2.nonAsciiShortEntries.size());
            output.write(reinterpret_cast<const char*>(&nonAsciiEntryCount), sizeof(nonAsciiEntryCount));
            for (const auto& pair : v2.nonAsciiShortEntries)
            {
                output.write(reinterpret_cast<const char*>(&pair.first), sizeof(pair.first));
                output.write(reinterpret_cast<const char*>(&pair.second), sizeof(pair.second));
            }

            WriteChildBucketEntries(output, v2.childEntries);
            WriteUInt32Vector(output, v2.childRecordIds);
            WriteUInt32Vector(output, v2.subtreeEnter);
            WriteUInt32Vector(output, v2.subtreeExit);
            WriteUInt32Vector(output, v2.subtreeRecordIds);
            WriteBucketEntryVector(output, v2.prefix4Entries);
            WriteByteVector(output, v2.prefix4Postings);

            output.flush();
            const auto ok = output.good();
            output.close();
            if (!ok)
            {
                DeleteFileW(tempPath.c_str());
                return;
            }

            if (std::filesystem::exists(path))
            {
                if (!ReplaceFileW(path.c_str(), tempPath.c_str(), nullptr, REPLACEFILE_IGNORE_MERGE_ERRORS, nullptr, nullptr))
                {
                    DeleteFileW(tempPath.c_str());
                    return;
                }
            }
            else if (!MoveFileExW(tempPath.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING))
            {
                DeleteFileW(tempPath.c_str());
                return;
            }

            NativeLogWrite(
                "[NATIVE V2 RUNTIME SAVE] success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - start).count())
                + " shortBytes=" + std::to_string(
                    v2.asciiCharCompressedPostings.size()
                    + v2.asciiBigramCompressedPostings.size()
                    + v2.nonAsciiShortCompressedPostings.size())
                + " prefix4Buckets=" + std::to_string(v2.prefix4Entries.size())
                + " prefix4Bytes=" + std::to_string(v2.prefix4Postings.size())
                + " parentOrder=" + std::to_string(v2.parentOrder.size())
                + " childBuckets=" + std::to_string(v2.childEntries.size())
                + " childIds=" + std::to_string(v2.childRecordIds.size()));
        }

        bool TryLoadV2TypeBucketsSnapshot_NoLock()
        {
            const auto path = GetV2TypeBucketsSnapshotPath();
            if (!std::filesystem::exists(path))
            {
                NativeLogWrite("[NATIVE V2 TYPE BUCKETS LOAD] miss reason=missing");
                return false;
            }

            const auto start = std::chrono::steady_clock::now();
            std::ifstream input(path, std::ios::binary);
            if (!input)
            {
                return false;
            }

            std::int32_t version = 0;
            std::int32_t recordCount = 0;
            input.read(reinterpret_cast<char*>(&version), sizeof(version));
            input.read(reinterpret_cast<char*>(&recordCount), sizeof(recordCount));
            if (!input.good()
                || version != V2TypeBucketsSnapshotVersion1
                || recordCount != static_cast<std::int32_t>(GetRecordCount_NoLock()))
            {
                NativeLogWrite("[NATIVE V2 TYPE BUCKETS LOAD] miss reason=version-or-record-count");
                return false;
            }

            if (!ReadUInt32Vector(input, allIds_)
                || !ReadUInt32Vector(input, directoryIds_)
                || !ReadUInt32Vector(input, launchableIds_)
                || !ReadUInt32Vector(input, scriptIds_)
                || !ReadUInt32Vector(input, logIds_)
                || !ReadUInt32Vector(input, configIds_))
            {
                return false;
            }

            std::uint32_t extensionCount = 0;
            input.read(reinterpret_cast<char*>(&extensionCount), sizeof(extensionCount));
            if (!input.good())
            {
                return false;
            }

            recordIndexByKey_.clear();
            exactNameIds_.clear();
            extensionIds_.clear();
            extensionIds_.reserve(extensionCount);
            for (std::uint32_t i = 0; i < extensionCount; ++i)
            {
                std::wstring extension;
                std::vector<std::uint32_t> ids;
                if (!ReadSnapshotString(input, extension) || !ReadUInt32Vector(input, ids))
                {
                    return false;
                }

                extensionIds_[std::move(extension)] = std::move(ids);
            }

            const auto expectedRecordCount = GetRecordCount_NoLock();
            const auto idsInRange = [expectedRecordCount](const std::vector<std::uint32_t>& ids)
            {
                return std::all_of(ids.begin(), ids.end(), [expectedRecordCount](std::uint32_t id)
                {
                    return id < expectedRecordCount;
                });
            };

            bool extensionIdsInRange = true;
            for (const auto& pair : extensionIds_)
            {
                if (!idsInRange(pair.second))
                {
                    extensionIdsInRange = false;
                    break;
                }
            }

            if (allIds_.size() != expectedRecordCount
                || !idsInRange(allIds_)
                || !idsInRange(directoryIds_)
                || !idsInRange(launchableIds_)
                || !idsInRange(scriptIds_)
                || !idsInRange(logIds_)
                || !idsInRange(configIds_)
                || !extensionIdsInRange)
            {
                NativeLogWrite(
                    "[NATIVE V2 TYPE BUCKETS LOAD] miss reason=invalid-record-ids"
                    " expected=" + std::to_string(expectedRecordCount)
                    + " all=" + std::to_string(allIds_.size()));
                allIds_.clear();
                directoryIds_.clear();
                launchableIds_.clear();
                scriptIds_.clear();
                logIds_.clear();
                configIds_.clear();
                extensionIds_.clear();
                return false;
            }

            RebuildDriveIdsFromAllIds_NoLock();
            derivedDirty_ = false;
            generationId_.fetch_add(1, std::memory_order_relaxed);
            NativeLogWrite(
                "[NATIVE V2 TYPE BUCKETS LOAD] success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - start).count())
                + " all=" + std::to_string(allIds_.size())
                + " dirs=" + std::to_string(directoryIds_.size())
                + " launchable=" + std::to_string(launchableIds_.size())
                + " config=" + std::to_string(configIds_.size())
                + " extensions=" + std::to_string(extensionIds_.size()));
            return true;
        }

        static void WriteV2TypeBucketsSnapshotFile(
            std::size_t recordCount,
            const std::vector<std::uint32_t>& allIds,
            const std::vector<std::uint32_t>& directoryIds,
            const std::vector<std::uint32_t>& launchableIds,
            const std::vector<std::uint32_t>& scriptIds,
            const std::vector<std::uint32_t>& logIds,
            const std::vector<std::uint32_t>& configIds,
            const std::unordered_map<std::wstring, std::vector<std::uint32_t>>& extensionIds)
        {
            if (allIds.empty())
            {
                return;
            }

            const auto start = std::chrono::steady_clock::now();
            const auto path = GetV2TypeBucketsSnapshotPath();
            std::filesystem::create_directories(path.parent_path());
            const auto tempPath = path.wstring()
                + L"."
                + std::to_wstring(GetCurrentProcessId())
                + L"."
                + std::to_wstring(GetTickCount64())
                + L".tmp";

            std::ofstream output(tempPath, std::ios::binary | std::ios::trunc);
            if (!output)
            {
                return;
            }

            const auto version = V2TypeBucketsSnapshotVersion1;
            const auto count = static_cast<std::int32_t>(recordCount);
            output.write(reinterpret_cast<const char*>(&version), sizeof(version));
            output.write(reinterpret_cast<const char*>(&count), sizeof(count));
            WriteUInt32Vector(output, allIds);
            WriteUInt32Vector(output, directoryIds);
            WriteUInt32Vector(output, launchableIds);
            WriteUInt32Vector(output, scriptIds);
            WriteUInt32Vector(output, logIds);
            WriteUInt32Vector(output, configIds);

            const auto extensionCount = static_cast<std::uint32_t>(extensionIds.size());
            output.write(reinterpret_cast<const char*>(&extensionCount), sizeof(extensionCount));
            for (const auto& pair : extensionIds)
            {
                WriteSnapshotString(output, pair.first);
                WriteUInt32Vector(output, pair.second);
            }

            output.flush();
            const auto ok = output.good();
            output.close();
            if (!ok)
            {
                DeleteFileW(tempPath.c_str());
                return;
            }

            if (std::filesystem::exists(path))
            {
                if (!ReplaceFileW(path.c_str(), tempPath.c_str(), nullptr, REPLACEFILE_IGNORE_MERGE_ERRORS, nullptr, nullptr))
                {
                    DeleteFileW(tempPath.c_str());
                    return;
                }
            }
            else if (!MoveFileExW(tempPath.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING))
            {
                DeleteFileW(tempPath.c_str());
                return;
            }

            NativeLogWrite(
                "[NATIVE V2 TYPE BUCKETS SAVE] success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - start).count())
                + " all=" + std::to_string(allIds.size())
                + " dirs=" + std::to_string(directoryIds.size())
                + " launchable=" + std::to_string(launchableIds.size())
                + " config=" + std::to_string(configIds.size())
                + " extensions=" + std::to_string(extensionIds.size()));
        }

        std::string BuildStateJson(bool requireSearchRefresh) const
        {
            std::ostringstream ss;
            ss << "{";
            ss << "\"indexedCount\":" << GetRecordCount_NoLock() << ",";
            ss << "\"currentStatusMessage\":\"" << EscapeJson(WideToUtf8(currentStatusMessage_)) << "\",";
            ss << "\"isBackgroundCatchUpInProgress\":" << (isBackgroundCatchUpInProgress_ ? "true" : "false") << ",";
            ss << "\"requireSearchRefresh\":" << (requireSearchRefresh ? "true" : "false") << ",";
            ss << "\"buildProgress\":{";
            ss << "\"stage\":\"" << EscapeJson(WideToUtf8(buildProgress_.stage)) << "\",";
            ss << "\"percent\":" << FormatBuildProgressPercent(buildProgress_.percent) << ",";
            ss << "\"message\":\"" << EscapeJson(WideToUtf8(buildProgress_.message)) << "\",";
            ss << "\"indexedCount\":" << buildProgress_.indexedCount << ",";
            ss << "\"currentDrive\":\"" << EscapeJson(WideToUtf8(BuildProgressDriveText(buildProgress_.currentDrive))) << "\",";
            ss << "\"elapsedMs\":" << buildProgress_.elapsedMs;
            ss << "},";
            ss << "\"engineVersion\":\"native-v2\",";
            ss << "\"readyStage\":\"" << (v2_.ready ? "records+trigram+prefix4+shortQuery+pathOrder+typeBuckets" : "not-ready") << "\",";
            ss << "\"capabilities\":{";
            ss << "\"records\":" << (GetRecordCount_NoLock() > 0 ? "true" : "false") << ",";
            ss << "\"trigram\":" << (v2_.ready ? "true" : "false") << ",";
            ss << "\"prefix4\":" << (!v2_.prefix4Entries.empty() ? "true" : "false") << ",";
            ss << "\"shortQuery\":" << (v2_.ready ? "true" : "false") << ",";
            ss << "\"pathOrder\":" << (v2_.ready ? "true" : "false") << ",";
            ss << "\"typeBuckets\":" << (!allIds_.empty() ? "true" : "false");
            ss << "},";
            ss << "\"buildMetrics\":{";
            ss << "\"v2BuildMs\":" << v2_.buildMs << ",";
            ss << "\"v2TrigramBuckets\":" << v2_.trigramEntries.size() << ",";
            ss << "\"v2TrigramBytes\":" << v2_.trigramPostings.size() << ",";
            ss << "\"v2Prefix4Buckets\":" << v2_.prefix4Entries.size() << ",";
            ss << "\"v2Prefix4Bytes\":" << v2_.prefix4Postings.size() << ",";
            ss << "\"v2PostingsBytes\":" << v2_.postingsBytes << ",";
            ss << "\"shortPostingsBytes\":" << (
                v2_.asciiCharCompressedPostings.size()
                + v2_.asciiBigramCompressedPostings.size()
                + v2_.nonAsciiShortCompressedPostings.size()) << ",";
            ss << "\"pathOrderBytes\":" << (
                (static_cast<std::uint64_t>(v2_.parentOrder.size() + v2_.childRecordIds.size()) * sizeof(std::uint32_t))
                + (static_cast<std::uint64_t>(v2_.childEntries.size()) * sizeof(NativeV2ChildBucketEntry))) << ",";
            ss << "\"recordObjectBytes\":" << (static_cast<std::uint64_t>(records_.capacity()) * sizeof(Record)) << ",";
            ss << "\"mappedRecordBytes\":" << (mappedRecords_.Ready() ? mappedRecords_.file.Size() : 0) << ",";
            ss << "\"frnIndexBytes\":" << (static_cast<std::uint64_t>(frnRecordIndex_.capacity()) * sizeof(FrnRecordIndexEntry)) << ",";
            ss << "\"childCsrBytes\":" << (
                (static_cast<std::uint64_t>(v2_.childRecordIds.capacity()) * sizeof(std::uint32_t))
                + (static_cast<std::uint64_t>(v2_.childEntries.capacity()) * sizeof(NativeV2ChildBucketEntry))) << ",";
            ss << "\"generationId\":" << generationId_.load(std::memory_order_relaxed) << ",";
            ss << "\"snapshotFormatVersion\":" << V2RuntimeSnapshotVersion7 << ",";
            ss << "\"restoreMappedBytes\":0";
            ss << "},";
            ss << "\"memoryBytes\":" << EstimateNativeMemoryBytes_NoLock() << ",";
            ss << "\"snapshotDirectory\":\"" << EscapeJson(WideToUtf8(GetSnapshotDirectoryPath().wstring())) << "\"";
            ss << "}";
            return ss.str();
        }

        std::string BuildSearchJson(int totalMatchedCount, bool isTruncated, const std::vector<SearchResultRow>& rows) const
        {
            std::ostringstream ss;
            ss << "{";
            ss << "\"totalIndexedCount\":" << GetRecordCount_NoLock() << ",";
            ss << "\"totalMatchedCount\":" << totalMatchedCount << ",";
            ss << "\"isTruncated\":" << (isTruncated ? "true" : "false") << ",";
            ss << "\"results\":[";
            for (std::size_t i = 0; i < rows.size(); ++i)
            {
                if (i > 0)
                {
                    ss << ",";
                }

                ss << "{";
                ss << "\"FullPath\":\"" << EscapeJson(WideToUtf8(rows[i].fullPath)) << "\",";
                ss << "\"FileName\":\"" << EscapeJson(WideToUtf8(rows[i].fileName)) << "\",";
                ss << "\"SizeBytes\":0,";
                ss << "\"ModifiedTimeUtc\":\"0001-01-01T00:00:00Z\",";
                ss << "\"RootPath\":\"\",";
                ss << "\"RootDisplayName\":\"\",";
                ss << "\"IsDirectory\":" << (rows[i].isDirectory ? "true" : "false");
                ss << "}";
            }
            ss << "]";
            ss << "}";
            return ss.str();
        }

        static std::string FormatBuildProgressPercent(double value)
        {
            if (value < 0.0)
            {
                return "-1";
            }

            if (value > 100.0)
            {
                value = 100.0;
            }

            std::ostringstream ss;
            ss.setf(std::ios::fixed);
            ss.precision(1);
            ss << value;
            return ss.str();
        }

        static std::wstring BuildProgressDriveText(wchar_t drive)
        {
            if (drive == L'\0')
            {
                return L"";
            }

            std::wstring text;
            text.push_back(static_cast<wchar_t>(towupper(drive)));
            text.append(L":");
            return text;
        }

        static int DeduplicateSearchRowsByPath(std::vector<SearchResultRow>& rows)
        {
            if (rows.size() < 2)
            {
                return 0;
            }

            std::unordered_set<std::wstring> seen;
            seen.reserve(rows.size() * 2);
            std::size_t writeIndex = 0;
            int duplicateCount = 0;
            for (std::size_t i = 0; i < rows.size(); ++i)
            {
                const auto inserted = seen.insert(rows[i].fullPath).second;
                if (!inserted)
                {
                    ++duplicateCount;
                    continue;
                }

                if (writeIndex != i)
                {
                    rows[writeIndex] = std::move(rows[i]);
                }

                ++writeIndex;
            }

            rows.resize(writeIndex);
            return duplicateCount;
        }

        std::string BuildSearchResponse(
            int totalMatchedCount,
            bool isTruncated,
            const std::vector<SearchResultRow>& rows,
            SearchBinaryPayload* binaryPayload,
            bool buildJson,
            int physicalMatchedCount = -1,
            int uniqueMatchedCount = -1,
            int duplicatePathCount = 0) const
        {
            if (binaryPayload != nullptr)
            {
                binaryPayload->totalIndexedCount = static_cast<int>(GetRecordCount_NoLock());
                binaryPayload->totalMatchedCount = totalMatchedCount;
                binaryPayload->physicalMatchedCount = physicalMatchedCount >= 0 ? physicalMatchedCount : totalMatchedCount;
                binaryPayload->uniqueMatchedCount = uniqueMatchedCount >= 0 ? uniqueMatchedCount : totalMatchedCount;
                binaryPayload->duplicatePathCount = duplicatePathCount;
                binaryPayload->isTruncated = isTruncated;
                binaryPayload->rows = rows;
            }

            return buildJson ? BuildSearchJson(totalMatchedCount, isTruncated, rows) : std::string();
        }

        static std::string BuildErrorJson(const std::string& error)
        {
            return "{\"error\":\"" + EscapeJson(error) + "\"}";
        }

        static std::string BuildChangeJson(
            const char* type,
            const std::wstring& lowerName,
            const std::wstring& fullPath,
            const std::wstring& oldFullPath,
            const std::wstring& newOriginalName,
            const std::wstring& newLowerName,
            bool isDirectory)
        {
            std::ostringstream ss;
            ss << "{";
            ss << "\"type\":\"" << type << "\",";
            ss << "\"lowerName\":\"" << EscapeJson(WideToUtf8(lowerName)) << "\",";
            ss << "\"fullPath\":\"" << EscapeJson(WideToUtf8(fullPath)) << "\",";
            ss << "\"oldFullPath\":\"" << EscapeJson(WideToUtf8(oldFullPath)) << "\",";
            ss << "\"newOriginalName\":\"" << EscapeJson(WideToUtf8(newOriginalName)) << "\",";
            ss << "\"newLowerName\":\"" << EscapeJson(WideToUtf8(newLowerName)) << "\",";
            ss << "\"isDirectory\":" << (isDirectory ? "true" : "false");
            ss << "}";
            return ss.str();
        }

        void PublishStatus(bool requireSearchRefresh) const
        {
            if (statusCallback_ == nullptr)
            {
                return;
            }

            const auto json = BuildStateJson(requireSearchRefresh);
            statusCallback_(userData_, json.c_str());
        }

        void PublishChangePayload(const std::string& payload) const
        {
            if (changeCallback_ == nullptr)
            {
                return;
            }

            changeCallback_(userData_, payload.c_str());
        }

        const std::vector<std::uint32_t>* GetCandidateSource(SearchFilter filter) const
        {
            switch (filter)
            {
            case SearchFilter::Folder:
                return &directoryIds_;
            case SearchFilter::Launchable:
                return &launchableIds_;
            case SearchFilter::Script:
                return &scriptIds_;
            case SearchFilter::Log:
                return &logIds_;
            case SearchFilter::Config:
                return &configIds_;
            default:
                return &allIds_;
            }
        }

        static SearchFilter ParseFilter(const std::string& filterText)
        {
            if (_stricmp(filterText.c_str(), "Launchable") == 0)
            {
                return SearchFilter::Launchable;
            }
            if (_stricmp(filterText.c_str(), "Folder") == 0)
            {
                return SearchFilter::Folder;
            }
            if (_stricmp(filterText.c_str(), "Script") == 0)
            {
                return SearchFilter::Script;
            }
            if (_stricmp(filterText.c_str(), "Log") == 0)
            {
                return SearchFilter::Log;
            }
            if (_stricmp(filterText.c_str(), "Config") == 0)
            {
                return SearchFilter::Config;
            }

            return SearchFilter::All;
        }

        static SearchFilter ParseFilterValue(int filterValue)
        {
            switch (filterValue)
            {
            case 1:
                return SearchFilter::Launchable;
            case 2:
                return SearchFilter::Folder;
            case 3:
                return SearchFilter::Script;
            case 4:
                return SearchFilter::Log;
            case 5:
                return SearchFilter::Config;
            default:
                return SearchFilter::All;
            }
        }

        static void ParsePathScope(const std::wstring& keyword, std::wstring& pathPrefix, std::wstring& searchTerm)
        {
            const auto spaceIndex = keyword.find(L' ');
            if (spaceIndex == std::wstring::npos || spaceIndex == 0)
            {
                pathPrefix.clear();
                searchTerm = keyword;
                return;
            }

            const auto candidate = keyword.substr(0, spaceIndex);
            const bool isDrivePath = candidate.length() >= 3 && iswalpha(candidate[0]) && candidate[1] == L':' && candidate[2] == L'\\';
            const bool isUncPath = candidate.rfind(L"\\", 0) == 0;
            if (!isDrivePath && !isUncPath)
            {
                pathPrefix.clear();
                searchTerm = keyword;
                return;
            }

            pathPrefix = candidate;
            searchTerm = Trim(keyword.substr(spaceIndex + 1));
        }

        static std::wstring DetectMatchMode(const std::wstring& keyword, MatchMode& mode)
        {
            if (!keyword.empty() && keyword.front() == L'^')
            {
                mode = MatchMode::Prefix;
                return ToLower(keyword.substr(1));
            }

            if (!keyword.empty() && keyword.back() == L'$')
            {
                mode = MatchMode::Suffix;
                return ToLower(keyword.substr(0, keyword.length() - 1));
            }

            if (keyword.length() >= 3 && keyword.front() == L'/' && keyword.back() == L'/')
            {
                mode = MatchMode::Regex;
                return keyword.substr(1, keyword.length() - 2);
            }

            if (keyword.find(L'*') != std::wstring::npos || keyword.find(L'?') != std::wstring::npos)
            {
                mode = MatchMode::Wildcard;
                return ToLower(keyword);
            }

            mode = MatchMode::Contains;
            return ToLower(keyword);
        }

        static bool TryBuildQueryPlan(
            MatchMode mode,
            const std::wstring& normalizedQuery,
            QueryPlan& plan,
            std::string& unsupportedReason)
        {
            unsupportedReason.clear();
            plan = QueryPlan{};
            plan.mode = mode;
            plan.query = normalizedQuery;

            if (normalizedQuery.empty())
            {
                unsupportedReason = "empty-query";
                return false;
            }

            if (mode == MatchMode::Contains)
            {
                plan.kind = QueryPlanKind::Contains;
                plan.name = "contains-v2";
                return true;
            }

            if (mode == MatchMode::Prefix)
            {
                plan.kind = QueryPlanKind::Prefix;
                plan.name = "prefix-sorted";
                return !plan.query.empty();
            }

            if (mode == MatchMode::Suffix)
            {
                if (plan.query.empty())
                {
                    unsupportedReason = "empty-suffix";
                    return false;
                }

                if (IsPlainExtensionSuffix(plan.query))
                {
                    plan.kind = QueryPlanKind::ExtensionWildcard;
                    plan.extension = plan.query;
                    plan.name = "suffix-extension";
                    return true;
                }

                plan.kind = QueryPlanKind::Suffix;
                plan.requiredLiteral = plan.query;
                plan.name = "suffix-literal";
                return true;
            }

            if (mode == MatchMode::Wildcard)
            {
                std::wstring extension;
                if (TryGetSimpleExtensionWildcard(normalizedQuery, extension))
                {
                    plan.kind = QueryPlanKind::ExtensionWildcard;
                    plan.extension = extension;
                    plan.pattern = normalizedQuery;
                    plan.name = "wildcard-extension";
                    return true;
                }

                MatchMode simplifiedMode = MatchMode::Wildcard;
                std::wstring simplifiedQuery;
                if (TrySimplifyWildcard(normalizedQuery, simplifiedMode, simplifiedQuery))
                {
                    if (simplifiedMode == MatchMode::Contains)
                    {
                        plan.kind = QueryPlanKind::Contains;
                        plan.mode = MatchMode::Contains;
                        plan.query = simplifiedQuery;
                        plan.name = "wildcard-contains-v2";
                        return !plan.query.empty();
                    }

                    if (simplifiedMode == MatchMode::Prefix)
                    {
                        plan.kind = QueryPlanKind::Prefix;
                        plan.mode = MatchMode::Prefix;
                        plan.query = simplifiedQuery;
                        plan.name = "wildcard-prefix";
                        return !plan.query.empty();
                    }

                    if (simplifiedMode == MatchMode::Suffix)
                    {
                        plan.kind = QueryPlanKind::Suffix;
                        plan.mode = MatchMode::Suffix;
                        plan.query = simplifiedQuery;
                        plan.requiredLiteral = simplifiedQuery;
                        plan.name = "wildcard-suffix-literal";
                        return !plan.query.empty();
                    }
                }

                std::wstring literal;
                if (TryExtractRequiredWildcardLiteral(normalizedQuery, literal))
                {
                    plan.kind = QueryPlanKind::WildcardLiteral;
                    plan.pattern = normalizedQuery;
                    plan.requiredLiteral = literal;
                    plan.name = "wildcard-literal";
                    return true;
                }

                unsupportedReason = "wildcard-without-required-literal";
                return false;
            }

            if (mode == MatchMode::Regex)
            {
                std::wstring literal;
                if (TryExtractRequiredRegexLiteral(normalizedQuery, literal))
                {
                    plan.kind = QueryPlanKind::RegexLiteral;
                    plan.pattern = normalizedQuery;
                    plan.requiredLiteral = literal;
                    plan.name = "regex-literal";
                    return true;
                }

                plan.kind = QueryPlanKind::RegexScan;
                plan.pattern = normalizedQuery;
                plan.name = "regex-scan";
                return true;
            }

            unsupportedReason = "unknown-mode";
            return false;
        }

        static bool TryGetSimpleExtensionWildcard(const std::wstring& pattern, std::wstring& extension)
        {
            extension.clear();
            if (pattern.length() < 3 || pattern[0] != L'*' || pattern[1] != L'.' || pattern.find(L'?') != std::wstring::npos)
            {
                return false;
            }

            if (pattern.find(L'*', 1) != std::wstring::npos)
            {
                return false;
            }

            extension = pattern.substr(1);
            return extension.length() > 1;
        }

        static bool TrySimplifyWildcard(const std::wstring& pattern, MatchMode& simplifiedMode, std::wstring& simplifiedQuery)
        {
            simplifiedMode = MatchMode::Wildcard;
            simplifiedQuery.clear();
            if (pattern.empty() || pattern.find(L'?') != std::wstring::npos)
            {
                return false;
            }

            const auto firstStar = pattern.find(L'*');
            if (firstStar == std::wstring::npos)
            {
                return false;
            }

            const auto lastStar = pattern.find_last_of(L'*');
            if (firstStar == lastStar)
            {
                if (firstStar == 0)
                {
                    simplifiedMode = MatchMode::Suffix;
                    simplifiedQuery = pattern.substr(1);
                    return true;
                }

                if (firstStar == pattern.length() - 1)
                {
                    simplifiedMode = MatchMode::Prefix;
                    simplifiedQuery = pattern.substr(0, pattern.length() - 1);
                    return true;
                }

                return false;
            }

            if (firstStar == 0
                && lastStar == pattern.length() - 1
                && pattern.find(L'*', 1) == lastStar)
            {
                simplifiedMode = MatchMode::Contains;
                simplifiedQuery = pattern.substr(1, pattern.length() - 2);
                return true;
            }

            return false;
        }

        static bool TryExtractRequiredWildcardLiteral(const std::wstring& pattern, std::wstring& literal)
        {
            literal.clear();
            std::wstring current;
            for (const auto ch : pattern)
            {
                if (ch == L'*' || ch == L'?')
                {
                    if (current.length() > literal.length())
                    {
                        literal = current;
                    }

                    current.clear();
                    continue;
                }

                current.push_back(ch);
            }

            if (current.length() > literal.length())
            {
                literal = current;
            }

            return !literal.empty();
        }

        static bool TryExtractRequiredRegexLiteral(const std::wstring& pattern, std::wstring& literal)
        {
            literal.clear();
            std::wstring current;
            bool escaping = false;
            for (const auto ch : pattern)
            {
                if (escaping)
                {
                    if (IsRegexEscapedLiteral(ch))
                    {
                        current.push_back(static_cast<wchar_t>(std::towlower(ch)));
                    }
                    else
                    {
                        if (current.length() > literal.length())
                        {
                            literal = current;
                        }

                        current.clear();
                    }

                    escaping = false;
                    continue;
                }

                if (ch == L'\\')
                {
                    escaping = true;
                    continue;
                }

                if (IsRegexMetaCharacter(ch))
                {
                    if (current.length() > literal.length())
                    {
                        literal = current;
                    }

                    current.clear();
                    continue;
                }

                current.push_back(static_cast<wchar_t>(std::towlower(ch)));
            }

            if (current.length() > literal.length())
            {
                literal = current;
            }

            return !literal.empty();
        }

        static bool IsRegexMetaCharacter(wchar_t ch)
        {
            switch (ch)
            {
            case L'.':
            case L'^':
            case L'$':
            case L'*':
            case L'+':
            case L'?':
            case L'(':
            case L')':
            case L'[':
            case L']':
            case L'{':
            case L'}':
            case L'|':
                return true;
            default:
                return false;
            }
        }

        static bool IsRegexEscapedLiteral(wchar_t ch)
        {
            switch (ch)
            {
            case L'd':
            case L'D':
            case L's':
            case L'S':
            case L'w':
            case L'W':
            case L'b':
            case L'B':
                return false;
            default:
                return true;
            }
        }

        static std::wstring WildcardToRegex(const std::wstring& pattern)
        {
            std::wstring regex = L"^";
            for (const auto ch : pattern)
            {
                if (ch == L'*')
                {
                    regex += L".*";
                }
                else if (ch == L'?')
                {
                    regex += L".";
                }
                else
                {
                    if (std::wcschr(LR"(\.^$|()[]{}+?)", ch) != nullptr)
                    {
                        regex.push_back(L'\\');
                    }
                    regex.push_back(ch);
                }
            }
            regex.push_back(L'$');
            return regex;
        }

        static bool EndsWith(const std::wstring& value, const std::wstring& suffix)
        {
            return value.length() >= suffix.length()
                && value.compare(value.length() - suffix.length(), suffix.length(), suffix) == 0;
        }

        static bool EndsWith(const std::wstring& value, std::wstring_view suffix)
        {
            return EndsWith(std::wstring_view(value), suffix);
        }

        static bool EndsWith(std::wstring_view value, const std::wstring& suffix)
        {
            return EndsWith(value, std::wstring_view(suffix));
        }

        static bool EndsWith(std::wstring_view value, std::wstring_view suffix)
        {
            return value.length() >= suffix.length()
                && value.compare(value.length() - suffix.length(), suffix.length(), suffix) == 0;
        }

        static bool ContainsText(const std::wstring& value, const std::wstring& query)
        {
            return ContainsText(std::wstring_view(value), query);
        }

        static bool ContainsText(const std::wstring& value, std::wstring_view query)
        {
            return ContainsText(std::wstring_view(value), query);
        }

        static bool ContainsText(std::wstring_view value, const std::wstring& query)
        {
            return ContainsText(value, std::wstring_view(query));
        }

        static bool ContainsText(std::wstring_view value, std::wstring_view query)
        {
            if (query.empty())
            {
                return true;
            }

            if (query.length() == 1)
            {
                return value.find(query[0]) != std::wstring_view::npos;
            }

            if (query.length() == 2)
            {
                const auto* valuePtr = value.data();
                const auto* queryPtr = query.data();
                for (std::size_t i = 0; i + 1 < value.length(); ++i)
                {
                    if (valuePtr[i] == queryPtr[0] && valuePtr[i + 1] == queryPtr[1])
                    {
                        return true;
                    }
                }

                return false;
            }

            return value.find(query) != std::wstring_view::npos;
        }

        static std::uint64_t PackTrigram(wchar_t a, wchar_t b, wchar_t c)
        {
            return (static_cast<std::uint64_t>(static_cast<std::uint16_t>(a)) << 32)
                | (static_cast<std::uint64_t>(static_cast<std::uint16_t>(b)) << 16)
                | static_cast<std::uint64_t>(static_cast<std::uint16_t>(c));
        }

        static std::uint64_t PackPrefix4(std::wstring_view value)
        {
            return (static_cast<std::uint64_t>(static_cast<std::uint16_t>(value[0])) << 48)
                | (static_cast<std::uint64_t>(static_cast<std::uint16_t>(value[1])) << 32)
                | (static_cast<std::uint64_t>(static_cast<std::uint16_t>(value[2])) << 16)
                | static_cast<std::uint64_t>(static_cast<std::uint16_t>(value[3]));
        }

        static std::uint64_t PackShortToken(wchar_t a, wchar_t b, int length)
        {
            return (static_cast<std::uint64_t>(static_cast<std::uint16_t>(length)) << 48)
                | (static_cast<std::uint64_t>(static_cast<std::uint16_t>(a)) << 24)
                | static_cast<std::uint64_t>(static_cast<std::uint16_t>(b));
        }

        static bool StartsWith(const std::wstring& value, const std::wstring& prefix)
        {
            return StartsWith(std::wstring_view(value), prefix);
        }

        static bool StartsWith(const std::wstring& value, std::wstring_view prefix)
        {
            return StartsWith(std::wstring_view(value), prefix);
        }

        static bool StartsWith(std::wstring_view value, const std::wstring& prefix)
        {
            return StartsWith(value, std::wstring_view(prefix));
        }

        static bool StartsWith(std::wstring_view value, std::wstring_view prefix)
        {
            return value.length() >= prefix.length()
                && value.compare(0, prefix.length(), prefix) == 0;
        }

        static bool StartsWithI(const std::wstring& value, const std::wstring& prefix)
        {
            if (value.length() < prefix.length())
            {
                return false;
            }

            for (std::size_t i = 0; i < prefix.length(); ++i)
            {
                if (towlower(value[i]) != towlower(prefix[i]))
                {
                    return false;
                }
            }

            return true;
        }

        static std::wstring EnsureTrailingSlash(const std::wstring& value)
        {
            if (value.empty() || value.back() == L'\\')
            {
                return value;
            }

            return value + L'\\';
        }

        static bool IsDriveRootPath(const std::wstring& value)
        {
            return value.length() == 3
                && iswalpha(value[0])
                && value[1] == L':'
                && value[2] == L'\\';
        }

        static std::wstring GetExtension(const std::wstring& lowerName)
        {
            const auto dotIndex = lowerName.find_last_of(L'.');
            if (dotIndex == std::wstring::npos || dotIndex + 1 >= lowerName.length())
            {
                return {};
            }

            return lowerName.substr(dotIndex);
        }

        static bool IsPlainExtensionSuffix(const std::wstring& value)
        {
            if (value.length() < 2 || value[0] != L'.')
            {
                return false;
            }

            for (std::size_t i = 1; i < value.length(); ++i)
            {
                const auto ch = value[i];
                if (!std::iswalnum(ch) && ch != L'_' && ch != L'-')
                {
                    return false;
                }
            }

            return true;
        }

        static bool IsLaunchableExtension(const std::wstring& extension)
        {
            return extension == L".exe" || extension == L".bat" || extension == L".cmd" || extension == L".ps1" || extension == L".lnk";
        }

        static bool IsScriptExtension(const std::wstring& extension)
        {
            return extension == L".bat" || extension == L".cmd" || extension == L".ps1";
        }

        static bool IsLogExtension(const std::wstring& extension)
        {
            return extension == L".log" || extension == L".txt";
        }

        static bool IsConfigExtension(const std::wstring& extension)
        {
            return extension == L".json" || extension == L".xml" || extension == L".ini" || extension == L".config" || extension == L".yaml" || extension == L".yml";
        }

        static std::uint64_t MakeRecordKey(wchar_t driveLetter, std::uint64_t frn)
        {
            return (static_cast<std::uint64_t>(driveLetter & 0xFF) << 56) | (frn & 0x00FFFFFFFFFFFFFFull);
        }

        static long long SteadyClockMilliseconds()
        {
            return std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now().time_since_epoch()).count();
        }

        static std::wstring ToLower(const std::wstring& value)
        {
            std::wstring result = value;
            std::transform(result.begin(), result.end(), result.begin(), [](wchar_t ch)
            {
                return static_cast<wchar_t>(towlower(ch));
            });
            return result;
        }

        static std::wstring Trim(const std::wstring& value)
        {
            std::size_t start = 0;
            while (start < value.length() && iswspace(value[start]))
            {
                ++start;
            }

            std::size_t end = value.length();
            while (end > start && iswspace(value[end - 1]))
            {
                --end;
            }

            return value.substr(start, end - start);
        }

        static std::filesystem::path GetSnapshotPath()
        {
            return GetSnapshotDirectoryPath() / L"index-native.bin";
        }

        static std::filesystem::path GetV2PostingsSnapshotPath()
        {
            return GetSnapshotDirectoryPath() / L"index-native-v2.postings.bin";
        }

        static std::filesystem::path GetV2RecordsSnapshotPath()
        {
            return GetSnapshotDirectoryPath() / L"index-native-v2.records.bin";
        }

        static std::filesystem::path GetV2RuntimeSnapshotPath()
        {
            return GetSnapshotDirectoryPath() / L"index-native-v2.runtime.bin";
        }

        static std::filesystem::path GetV2TypeBucketsSnapshotPath()
        {
            return GetSnapshotDirectoryPath() / L"index-native-v2.type-buckets.bin";
        }

        static std::filesystem::path GetV2OverlaySnapshotPath()
        {
            return GetSnapshotDirectoryPath() / L"index-native-v2.overlay.bin";
        }

        static std::filesystem::path GetSnapshotDirectoryPath()
        {
            const auto overridePath = GetEnvironmentValue(L"PM_MFT_INDEX_SNAPSHOT_DIR");
            if (!overridePath.empty())
            {
                return std::filesystem::path(overridePath);
            }

            wchar_t appData[MAX_PATH] = {};
            if (GetEnvironmentVariableW(L"APPDATA", appData, MAX_PATH) == 0)
            {
                throw std::runtime_error("APPDATA is unavailable");
            }

            return std::filesystem::path(appData) / L"PackageManager" / L"MftScannerIndex";
        }

        static std::string WideToUtf8(const std::wstring& value)
        {
            if (value.empty())
            {
                return {};
            }

            const auto size = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
            std::string result(size, '\0');
            WideCharToMultiByte(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), result.data(), size, nullptr, nullptr);
            return result;
        }

        static std::wstring Utf8ToWide(const std::string& value)
        {
            if (value.empty())
            {
                return {};
            }

            const auto size = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), nullptr, 0);
            std::wstring result(size, L'\0');
            MultiByteToWideChar(CP_UTF8, 0, value.c_str(), static_cast<int>(value.size()), result.data(), size);
            return result;
        }

        static void Write7BitEncodedInt(std::ofstream& output, std::uint32_t value)
        {
            while (value >= 0x80)
            {
                const auto current = static_cast<std::uint8_t>(value | 0x80);
                output.put(static_cast<char>(current));
                value >>= 7;
            }

            output.put(static_cast<char>(value));
        }

        static bool Read7BitEncodedInt(std::ifstream& input, std::uint32_t& value)
        {
            value = 0;
            int shift = 0;
            for (int i = 0; i < 5; ++i)
            {
                const auto next = input.get();
                if (next == std::char_traits<char>::eof())
                {
                    return false;
                }

                const auto byte = static_cast<std::uint8_t>(next);
                value |= static_cast<std::uint32_t>(byte & 0x7F) << shift;
                if ((byte & 0x80) == 0)
                {
                    return true;
                }

                shift += 7;
            }

            return false;
        }

        static bool WriteSnapshotString(std::ofstream& output, const std::wstring& value)
        {
            const auto utf8 = WideToUtf8(value);
            Write7BitEncodedInt(output, static_cast<std::uint32_t>(utf8.size()));
            if (!output.good())
            {
                return false;
            }

            if (!utf8.empty())
            {
                output.write(utf8.data(), static_cast<std::streamsize>(utf8.size()));
            }

            return output.good();
        }

        static bool ReadSnapshotString(std::ifstream& input, std::wstring& value)
        {
            value.clear();

            std::uint32_t length = 0;
            if (!Read7BitEncodedInt(input, length))
            {
                return false;
            }

            std::string utf8(length, '\0');
            if (length > 0)
            {
                input.read(utf8.data(), static_cast<std::streamsize>(length));
                if (!input.good())
                {
                    return false;
                }
            }

            value = Utf8ToWide(utf8);
            return true;
        }

        static void AppendUInt8(std::vector<std::uint8_t>& payload, std::uint8_t value)
        {
            payload.push_back(value);
        }

        static void AppendInt32(std::vector<std::uint8_t>& payload, std::int32_t value)
        {
            for (int i = 0; i < 4; ++i)
            {
                payload.push_back(static_cast<std::uint8_t>((static_cast<std::uint32_t>(value) >> (i * 8)) & 0xFFu));
            }
        }

        static void AppendInt64(std::vector<std::uint8_t>& payload, std::int64_t value)
        {
            for (int i = 0; i < 8; ++i)
            {
                payload.push_back(static_cast<std::uint8_t>((static_cast<std::uint64_t>(value) >> (i * 8)) & 0xFFu));
            }
        }

        static void AppendUtf16String(std::vector<std::uint8_t>& payload, const std::wstring& value)
        {
            const auto byteLength = static_cast<std::int32_t>(value.size() * sizeof(wchar_t));
            AppendInt32(payload, byteLength);
            if (byteLength <= 0)
            {
                return;
            }

            const auto* bytes = reinterpret_cast<const std::uint8_t*>(value.data());
            payload.insert(payload.end(), bytes, bytes + byteLength);
        }

        static std::vector<std::uint8_t> BuildSearchBinaryPayload(const SearchBinaryPayload& result)
        {
            std::vector<std::uint8_t> payload;
            payload.reserve(128 + result.rows.size() * 256);
            AppendInt32(payload, 1);
            AppendInt32(payload, result.totalIndexedCount);
            AppendInt32(payload, result.totalMatchedCount);
            AppendInt32(payload, result.physicalMatchedCount);
            AppendInt32(payload, result.uniqueMatchedCount);
            AppendInt32(payload, result.duplicatePathCount);
            AppendUInt8(payload, result.isTruncated ? 1 : 0);
            AppendInt32(payload, static_cast<std::int32_t>(result.rows.size()));
            for (const auto& row : result.rows)
            {
                AppendUtf16String(payload, row.fullPath);
                AppendUtf16String(payload, row.fileName);
                AppendInt64(payload, 0);
                AppendInt64(payload, 0);
                AppendUtf16String(payload, std::wstring());
                AppendUtf16String(payload, std::wstring());
                AppendUInt8(payload, row.isDirectory ? 1 : 0);
            }

            return payload;
        }

        static bool TryExtractErrorJson(const std::string& json, std::string& error)
        {
            error.clear();
            return TryReadJsonStringField(json, "error", error) && !error.empty();
        }

        static bool WriteSnapshotChar(std::ofstream& output, wchar_t value)
        {
            const auto raw = static_cast<char>(value & 0xFF);
            output.write(&raw, sizeof(raw));
            return output.good();
        }

        static bool ReadSnapshotChar(std::ifstream& input, wchar_t& value)
        {
            char raw = '\0';
            input.read(&raw, sizeof(raw));
            if (!input.good())
            {
                return false;
            }

            value = static_cast<unsigned char>(raw);
            return true;
        }

        static bool WriteSnapshotBool(std::ofstream& output, bool value)
        {
            const auto raw = static_cast<std::uint8_t>(value ? 1 : 0);
            output.write(reinterpret_cast<const char*>(&raw), sizeof(raw));
            return output.good();
        }

        static bool ReadSnapshotBool(std::ifstream& input, bool& value)
        {
            std::uint8_t raw = 0;
            input.read(reinterpret_cast<char*>(&raw), sizeof(raw));
            if (!input.good())
            {
                return false;
            }

            value = raw != 0;
            return true;
        }

        static void WriteByteVector(std::ofstream& output, const std::vector<std::uint8_t>& values)
        {
            const auto count = static_cast<std::uint32_t>(values.size());
            output.write(reinterpret_cast<const char*>(&count), sizeof(count));
            if (count > 0)
            {
                output.write(reinterpret_cast<const char*>(values.data()), count);
            }
        }

        static bool ReadByteVector(std::ifstream& input, std::vector<std::uint8_t>& values)
        {
            std::uint32_t count = 0;
            input.read(reinterpret_cast<char*>(&count), sizeof(count));
            if (!input.good())
            {
                return false;
            }

            values.resize(count);
            if (count > 0)
            {
                input.read(reinterpret_cast<char*>(values.data()), count);
                if (!input.good())
                {
                    return false;
                }
            }

            values.shrink_to_fit();
            return true;
        }

        static void WriteBucketEntryVector(std::ofstream& output, const std::vector<NativeV2BucketEntry>& entries)
        {
            const auto count = static_cast<std::uint32_t>(entries.size());
            output.write(reinterpret_cast<const char*>(&count), sizeof(count));
            for (const auto& entry : entries)
            {
                output.write(reinterpret_cast<const char*>(&entry.key), sizeof(entry.key));
                output.write(reinterpret_cast<const char*>(&entry.offset), sizeof(entry.offset));
                output.write(reinterpret_cast<const char*>(&entry.count), sizeof(entry.count));
                output.write(reinterpret_cast<const char*>(&entry.byteCount), sizeof(entry.byteCount));
            }
        }

        static bool ReadBucketEntryVector(std::ifstream& input, std::vector<NativeV2BucketEntry>& entries)
        {
            std::uint32_t count = 0;
            input.read(reinterpret_cast<char*>(&count), sizeof(count));
            if (!input.good())
            {
                return false;
            }

            entries.resize(count);
            for (auto& entry : entries)
            {
                input.read(reinterpret_cast<char*>(&entry.key), sizeof(entry.key));
                input.read(reinterpret_cast<char*>(&entry.offset), sizeof(entry.offset));
                input.read(reinterpret_cast<char*>(&entry.count), sizeof(entry.count));
                input.read(reinterpret_cast<char*>(&entry.byteCount), sizeof(entry.byteCount));
                if (!input.good())
                {
                    return false;
                }
            }

            entries.shrink_to_fit();
            return true;
        }

        static void WriteUInt32Vector(std::ofstream& output, const std::vector<std::uint32_t>& values)
        {
            const auto count = static_cast<std::uint32_t>(values.size());
            output.write(reinterpret_cast<const char*>(&count), sizeof(count));
            if (count > 0)
            {
                output.write(reinterpret_cast<const char*>(values.data()), static_cast<std::streamsize>(count * sizeof(std::uint32_t)));
            }
        }

        static bool ReadUInt32Vector(std::ifstream& input, std::vector<std::uint32_t>& values)
        {
            std::uint32_t count = 0;
            input.read(reinterpret_cast<char*>(&count), sizeof(count));
            if (!input.good())
            {
                return false;
            }

            values.resize(count);
            if (count > 0)
            {
                input.read(reinterpret_cast<char*>(values.data()), static_cast<std::streamsize>(count * sizeof(std::uint32_t)));
                if (!input.good())
                {
                    return false;
                }
            }

            values.shrink_to_fit();
            return true;
        }

        static void WriteDriveCountVector(std::ofstream& output, const std::vector<std::array<std::uint32_t, 26>>& values)
        {
            const auto count = static_cast<std::uint32_t>(values.size());
            output.write(reinterpret_cast<const char*>(&count), sizeof(count));
            if (count > 0)
            {
                output.write(reinterpret_cast<const char*>(values.data()),
                    static_cast<std::streamsize>(count * sizeof(values[0])));
            }
        }

        static bool ReadDriveCountVector(std::ifstream& input, std::vector<std::array<std::uint32_t, 26>>& values)
        {
            std::uint32_t count = 0;
            input.read(reinterpret_cast<char*>(&count), sizeof(count));
            if (!input.good())
            {
                return false;
            }

            values.resize(count);
            if (count > 0)
            {
                input.read(reinterpret_cast<char*>(values.data()),
                    static_cast<std::streamsize>(count * sizeof(values[0])));
                if (!input.good())
                {
                    return false;
                }
            }

            values.shrink_to_fit();
            return true;
        }

        static void WriteDrivePageVector(std::ofstream& output, const std::vector<std::array<std::vector<std::uint32_t>, 26>>& values)
        {
            const auto tokenCount = static_cast<std::uint32_t>(values.size());
            output.write(reinterpret_cast<const char*>(&tokenCount), sizeof(tokenCount));
            for (const auto& tokenPages : values)
            {
                for (const auto& drivePage : tokenPages)
                {
                    WriteUInt32Vector(output, drivePage);
                }
            }
        }

        static bool ReadDrivePageVector(std::ifstream& input, std::vector<std::array<std::vector<std::uint32_t>, 26>>& values)
        {
            std::uint32_t tokenCount = 0;
            input.read(reinterpret_cast<char*>(&tokenCount), sizeof(tokenCount));
            if (!input.good())
            {
                return false;
            }

            values.clear();
            values.resize(tokenCount);
            for (auto& tokenPages : values)
            {
                for (auto& drivePage : tokenPages)
                {
                    if (!ReadUInt32Vector(input, drivePage))
                    {
                        return false;
                    }
                }
            }

            values.shrink_to_fit();
            return true;
        }

        static void WriteChildBucketEntries(std::ofstream& output, const std::vector<NativeV2ChildBucketEntry>& values)
        {
            const auto count = static_cast<std::uint32_t>(values.size());
            output.write(reinterpret_cast<const char*>(&count), sizeof(count));
            if (count > 0)
            {
                output.write(reinterpret_cast<const char*>(values.data()),
                    static_cast<std::streamsize>(count * sizeof(NativeV2ChildBucketEntry)));
            }
        }

        static bool ReadChildBucketEntries(std::ifstream& input, std::vector<NativeV2ChildBucketEntry>& values)
        {
            std::uint32_t count = 0;
            input.read(reinterpret_cast<char*>(&count), sizeof(count));
            if (!input.good())
            {
                return false;
            }

            values.resize(count);
            if (count > 0)
            {
                input.read(reinterpret_cast<char*>(values.data()),
                    static_cast<std::streamsize>(count * sizeof(NativeV2ChildBucketEntry)));
                if (!input.good())
                {
                    return false;
                }
            }

            values.shrink_to_fit();
            return true;
        }

        static void AppendCollectedChange(std::vector<NativeChange>& changes, const NativeChange& change)
        {
            if (!changes.empty())
            {
                const auto& last = changes.back();
                if (last.kind == change.kind
                    && last.frn == change.frn
                    && last.parentFrn == change.parentFrn
                    && last.driveLetter == change.driveLetter
                    && last.oldParentFrn == change.oldParentFrn
                    && last.lowerName == change.lowerName
                    && last.oldLowerName == change.oldLowerName)
                {
                    return;
                }
            }

            changes.push_back(change);
        }

        static std::vector<const VolumeState*> GetOrderedVolumes(const std::unordered_map<wchar_t, VolumeState>& volumes)
        {
            std::vector<const VolumeState*> ordered;
            ordered.reserve(volumes.size());
            for (const auto& pair : volumes)
            {
                ordered.push_back(&pair.second);
            }

            std::sort(ordered.begin(), ordered.end(), [](const VolumeState* left, const VolumeState* right)
            {
                return left->driveLetter < right->driveLetter;
            });

            return ordered;
        }

        static std::vector<const std::pair<const std::uint64_t, FrnEntry>*> GetOrderedFrnEntries(const std::unordered_map<std::uint64_t, FrnEntry>& frnMap)
        {
            std::vector<const std::pair<const std::uint64_t, FrnEntry>*> ordered;
            ordered.reserve(frnMap.size());
            for (const auto& pair : frnMap)
            {
                ordered.push_back(&pair);
            }

            std::sort(ordered.begin(), ordered.end(), [](const auto* left, const auto* right)
            {
                return left->first < right->first;
            });

            return ordered;
        }

        static std::vector<const Record*> GetOrderedRecordsForVolume(const std::vector<Record>& records, wchar_t driveLetter)
        {
            std::vector<const Record*> ordered;
            ordered.reserve(records.size() / 3);
            for (const auto& record : records)
            {
                if (record.driveLetter == driveLetter)
                {
                    ordered.push_back(&record);
                }
            }

            std::sort(ordered.begin(), ordered.end(), [](const auto* left, const auto* right)
            {
                return left->frn < right->frn;
            });

            return ordered;
        }

        static std::vector<std::wstring> BuildSnapshotStringPool(
            const std::vector<Record>& records,
            const std::vector<const VolumeState*>& volumes)
        {
            std::vector<std::wstring> pool;
            pool.reserve((records.size() > 16 ? records.size() : 16));
            std::unordered_map<std::wstring, std::size_t> seen;
            seen.reserve(records.size() + 64);

            const auto add = [&pool, &seen](const std::wstring& value)
            {
                const auto [_, inserted] = seen.emplace(value, seen.size());
                if (inserted)
                {
                    pool.push_back(value);
                }
            };

            for (const auto& record : records)
            {
                add(record.originalName);
            }

            return pool;
        }

        static std::string EscapeJson(const std::string& value)
        {
            std::ostringstream ss;
            for (const auto ch : value)
            {
                switch (ch)
                {
                case '\\': ss << "\\\\"; break;
                case '"': ss << "\\\""; break;
                case '\r': ss << "\\r"; break;
                case '\n': ss << "\\n"; break;
                case '\t': ss << "\\t"; break;
                default: ss << ch; break;
                }
            }
            return ss.str();
        }

        static bool TryReadJsonStringField(const std::string& json, const std::string& key, std::string& value)
        {
            value.clear();
            const auto marker = "\"" + key + "\"";
            const auto keyPos = json.find(marker);
            if (keyPos == std::string::npos)
            {
                return false;
            }

            auto colonPos = json.find(':', keyPos + marker.length());
            if (colonPos == std::string::npos)
            {
                return false;
            }

            ++colonPos;
            while (colonPos < json.length() && std::isspace(static_cast<unsigned char>(json[colonPos])))
            {
                ++colonPos;
            }

            if (colonPos >= json.length() || json[colonPos] != '"')
            {
                return false;
            }

            ++colonPos;
            bool escape = false;
            for (auto i = colonPos; i < json.length(); ++i)
            {
                const auto ch = json[i];
                if (escape)
                {
                    switch (ch)
                    {
                    case '"': value.push_back('"'); break;
                    case '\\': value.push_back('\\'); break;
                    case '/': value.push_back('/'); break;
                    case 'b': value.push_back('\b'); break;
                    case 'f': value.push_back('\f'); break;
                    case 'n': value.push_back('\n'); break;
                    case 'r': value.push_back('\r'); break;
                    case 't': value.push_back('\t'); break;
                    default: value.push_back(ch); break;
                    }
                    escape = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escape = true;
                    continue;
                }

                if (ch == '"')
                {
                    return true;
                }

                value.push_back(ch);
            }

            value.clear();
            return false;
        }

        static bool TryReadJsonIntField(const std::string& json, const std::string& key, int& value)
        {
            const auto marker = "\"" + key + "\"";
            const auto keyPos = json.find(marker);
            if (keyPos == std::string::npos)
            {
                return false;
            }

            auto colonPos = json.find(':', keyPos + marker.length());
            if (colonPos == std::string::npos)
            {
                return false;
            }

            ++colonPos;
            while (colonPos < json.length() && std::isspace(static_cast<unsigned char>(json[colonPos])))
            {
                ++colonPos;
            }

            auto endPos = colonPos;
            while (endPos < json.length() && (std::isdigit(static_cast<unsigned char>(json[endPos])) || json[endPos] == '-'))
            {
                ++endPos;
            }

            if (endPos == colonPos)
            {
                return false;
            }

            value = std::atoi(json.substr(colonPos, endPos - colonPos).c_str());
            return true;
        }

        static bool IsTestHooksEnabled()
        {
            char value[16]{};
            const auto length = GetEnvironmentVariableA("PM_MFT_INDEX_TEST_HOOKS", value, static_cast<DWORD>(sizeof(value)));
            return length == 1 && value[0] == '1';
        }
    };

    char* DuplicateUtf8(const std::string& value)
    {
        const auto size = value.size() + 1;
        auto* buffer = static_cast<char*>(std::malloc(size));
        if (buffer == nullptr)
        {
            return nullptr;
        }

        std::memcpy(buffer, value.c_str(), size);
        return buffer;
    }

    std::string BuildInteropErrorJson(const std::string& error)
    {
        std::string escaped;
        escaped.reserve(error.size());
        for (const auto ch : error)
        {
            switch (ch)
            {
            case '\\': escaped += "\\\\"; break;
            case '"': escaped += "\\\""; break;
            case '\r': escaped += "\\r"; break;
            case '\n': escaped += "\\n"; break;
            case '\t': escaped += "\\t"; break;
            default: escaped.push_back(ch); break;
            }
        }

        return "{\"error\":\"" + escaped + "\"}";
    }

    template <typename TOperation>
    int ExecuteOperation(NativeIndex* index, char** response, TOperation&& operation)
    {
        if (response == nullptr)
        {
            return 0;
        }

        *response = nullptr;
        if (index == nullptr)
        {
            *response = DuplicateUtf8("{\"error\":\"native handle is null\"}");
            return 0;
        }

        try
        {
            const auto json = operation();
            *response = DuplicateUtf8(json);
            return 1;
        }
        catch (const std::exception& ex)
        {
            *response = DuplicateUtf8(BuildInteropErrorJson(ex.what()));
            return 0;
        }
        catch (...)
        {
            *response = DuplicateUtf8("{\"error\":\"unknown native failure\"}");
            return 0;
        }
    }
}

extern "C"
{
    __declspec(dllexport) void* __cdecl pm_index_create(NativeJsonCallback statusCallback, NativeJsonCallback changeCallback, void* userData)
    {
        try
        {
            return new NativeIndex(statusCallback, changeCallback, userData);
        }
        catch (...)
        {
            return nullptr;
        }
    }

    __declspec(dllexport) void __cdecl pm_index_destroy(void* handle)
    {
        delete static_cast<NativeIndex*>(handle);
    }

    __declspec(dllexport) int __cdecl pm_index_build(void* handle, const char* requestJsonUtf8, char** responseJsonUtf8)
    {
        (void)requestJsonUtf8;
        return ExecuteOperation(static_cast<NativeIndex*>(handle), responseJsonUtf8, [handle]()
        {
            return static_cast<NativeIndex*>(handle)->Build(false);
        });
    }

    __declspec(dllexport) int __cdecl pm_index_rebuild(void* handle, const char* requestJsonUtf8, char** responseJsonUtf8)
    {
        (void)requestJsonUtf8;
        return ExecuteOperation(static_cast<NativeIndex*>(handle), responseJsonUtf8, [handle]()
        {
            return static_cast<NativeIndex*>(handle)->Build(true);
        });
    }

    __declspec(dllexport) int __cdecl pm_index_search(void* handle, const char* requestJsonUtf8, char** responseJsonUtf8)
    {
        const std::string request = requestJsonUtf8 == nullptr ? std::string("{}") : std::string(requestJsonUtf8);
        return ExecuteOperation(static_cast<NativeIndex*>(handle), responseJsonUtf8, [handle, &request]()
        {
            return static_cast<NativeIndex*>(handle)->Search(request);
        });
    }

    __declspec(dllexport) int __cdecl pm_index_search_binary(
        void* handle,
        const char* keywordUtf8,
        int maxResults,
        int offset,
        int filterValue,
        unsigned char** responseBytes,
        int* responseLength,
        char** errorUtf8)
    {
        if (responseBytes == nullptr || responseLength == nullptr)
        {
            return 0;
        }

        *responseBytes = nullptr;
        *responseLength = 0;
        if (errorUtf8 != nullptr)
        {
            *errorUtf8 = nullptr;
        }

        if (handle == nullptr)
        {
            if (errorUtf8 != nullptr)
            {
                *errorUtf8 = DuplicateUtf8("native handle is null");
            }
            return 0;
        }

        try
        {
            std::vector<std::uint8_t> payload;
            std::string error;
            if (!static_cast<NativeIndex*>(handle)->SearchBinary(
                keywordUtf8,
                maxResults,
                offset,
                filterValue,
                payload,
                error))
            {
                if (errorUtf8 != nullptr)
                {
                    *errorUtf8 = DuplicateUtf8(error.empty() ? "native search failed" : error);
                }
                return 0;
            }

            if (payload.size() > static_cast<std::size_t>(std::numeric_limits<int>::max()))
            {
                if (errorUtf8 != nullptr)
                {
                    *errorUtf8 = DuplicateUtf8("native binary search payload is too large");
                }
                return 0;
            }

            auto* buffer = static_cast<unsigned char*>(std::malloc(payload.size()));
            if (buffer == nullptr && !payload.empty())
            {
                if (errorUtf8 != nullptr)
                {
                    *errorUtf8 = DuplicateUtf8("native binary search allocation failed");
                }
                return 0;
            }

            if (!payload.empty())
            {
                std::memcpy(buffer, payload.data(), payload.size());
            }
            *responseBytes = buffer;
            *responseLength = static_cast<int>(payload.size());
            return 1;
        }
        catch (const std::exception& ex)
        {
            if (errorUtf8 != nullptr)
            {
                *errorUtf8 = DuplicateUtf8(ex.what());
            }
            return 0;
        }
        catch (...)
        {
            if (errorUtf8 != nullptr)
            {
                *errorUtf8 = DuplicateUtf8("unknown native failure");
            }
            return 0;
        }
    }

    __declspec(dllexport) int __cdecl pm_index_get_state(void* handle, const char* requestJsonUtf8, char** responseJsonUtf8)
    {
        (void)requestJsonUtf8;
        return ExecuteOperation(static_cast<NativeIndex*>(handle), responseJsonUtf8, [handle]()
        {
            return static_cast<NativeIndex*>(handle)->GetState();
        });
    }

    __declspec(dllexport) int __cdecl pm_index_test_control(void* handle, const char* requestJsonUtf8, char** responseJsonUtf8)
    {
        const std::string request = requestJsonUtf8 == nullptr ? std::string("{}") : std::string(requestJsonUtf8);
        return ExecuteOperation(static_cast<NativeIndex*>(handle), responseJsonUtf8, [handle, &request]()
        {
            return static_cast<NativeIndex*>(handle)->TestControl(request);
        });
    }

    __declspec(dllexport) void __cdecl pm_index_shutdown(void* handle)
    {
        if (handle != nullptr)
        {
            static_cast<NativeIndex*>(handle)->Shutdown();
        }
    }

    __declspec(dllexport) void __cdecl pm_index_free_string(char* value)
    {
        std::free(value);
    }

    __declspec(dllexport) void __cdecl pm_index_free_bytes(unsigned char* value)
    {
        std::free(value);
    }
}
