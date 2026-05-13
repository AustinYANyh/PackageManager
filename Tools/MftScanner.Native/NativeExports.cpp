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

    struct NativeV2CandidateResult
    {
        std::vector<std::uint32_t> ids;
        const char* mode = "none";
        bool accelerated = false;
    };

    struct NativeV2Accelerator
    {
        std::vector<NativeV2BucketEntry> trigramEntries;
        std::vector<std::uint8_t> trigramPostings;
        std::array<std::vector<std::uint32_t>, 128> asciiCharPostings;
        std::array<std::vector<std::uint32_t>, 128 * 128> asciiBigramPostings;
        std::unordered_map<std::uint64_t, std::vector<std::uint32_t>> nonAsciiShortPostings;
        std::array<NativeV2ShortBucketEntry, 128> asciiCharEntries{};
        std::array<NativeV2ShortBucketEntry, 128 * 128> asciiBigramEntries{};
        std::unordered_map<std::uint64_t, NativeV2ShortBucketEntry> nonAsciiShortEntries;
        std::vector<std::uint8_t> asciiCharCompressedPostings;
        std::vector<std::uint8_t> asciiBigramCompressedPostings;
        std::vector<std::uint8_t> nonAsciiShortCompressedPostings;
        std::vector<std::uint32_t> parentOrder;
        std::unordered_map<std::uint64_t, std::vector<std::uint32_t>> childIds;
        bool ready = false;
        std::uint64_t buildMs = 0;
        std::uint64_t postingsBytes = 0;

        void Clear()
        {
            trigramEntries.clear();
            trigramPostings.clear();
            trigramPostings.shrink_to_fit();
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
            nonAsciiShortEntries.clear();
            asciiCharCompressedPostings.clear();
            asciiCharCompressedPostings.shrink_to_fit();
            asciiBigramCompressedPostings.clear();
            asciiBigramCompressedPostings.shrink_to_fit();
            nonAsciiShortCompressedPostings.clear();
            nonAsciiShortCompressedPostings.shrink_to_fit();
            parentOrder.clear();
            childIds.clear();
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

    std::size_t CountFrnEntries(const std::unordered_map<wchar_t, VolumeState>& volumes)
    {
        std::size_t total = 0;
        for (const auto& pair : volumes)
        {
            total += pair.second.frnMap.size();
        }

        return total;
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
            StopBackgroundCatchUp();
            StopWatchers();

            std::unique_lock<std::shared_mutex> guard(mutex_);
            hasShutdown_ = false;

            if (!forceRebuild && TryLoadSnapshot())
            {
                currentStatusMessage_ = L"已从 native 快照恢复索引，可立即搜索；后台正在追平 USN...";
                isBackgroundCatchUpInProgress_ = true;
                PublishStatus(false);
                StartBackgroundCatchUp_NoLock();
                NativeLogWrite(
                    "[NATIVE BUILD] mode=snapshot-restore totalMs="
                    + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - totalStart).count())
                    + " indexedCount=" + std::to_string(records_.size())
                    + " isBackgroundCatchUpInProgress=true");
                return BuildStateJson(false);
            }

            BuildFromMft();
            const auto watcherStart = std::chrono::steady_clock::now();
            currentStatusMessage_ = L"已通过 native 内核构建索引";
            isBackgroundCatchUpInProgress_ = false;
            StartWatchers_NoLock();
            const auto watcherStartMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - watcherStart).count();
            QueueSnapshotSave_NoLock();
            PublishStatus(true);
            NativeLogWrite(
                "[NATIVE BUILD] mode=full totalMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - totalStart).count())
                + " watcherStartMs=" + std::to_string(watcherStartMs)
                + " indexedCount=" + std::to_string(records_.size())
                + " isBackgroundCatchUpInProgress=false");
            return BuildStateJson(true);
        }

        std::string Search(const std::string& requestJson)
        {
            const auto totalStart = std::chrono::steady_clock::now();
            const auto lockWaitStart = std::chrono::steady_clock::now();
            std::shared_lock<std::shared_mutex> guard(mutex_);
            const auto lockWaitMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - lockWaitStart).count();
            if (derivedDirty_ || !v2_.ready)
            {
                return BuildErrorJson("native v2 search accelerator is not ready");
            }

            std::string keywordUtf8;
            std::string filterText;
            int maxResults = 0;
            int offset = 0;
            const auto parseStart = std::chrono::steady_clock::now();
            TryReadJsonStringField(requestJson, "keyword", keywordUtf8);
            TryReadJsonStringField(requestJson, "filter", filterText);
            TryReadJsonIntField(requestJson, "maxResults", maxResults);
            TryReadJsonIntField(requestJson, "offset", offset);

            const auto keyword = Utf8ToWide(keywordUtf8);
            const auto filter = ParseFilter(filterText);
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
                return BuildSearchJson(0, false, {});
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
                return BuildSearchJson(0, false, {});
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
                return BuildSearchJson(0, false, {});
            }

            const bool pathScoped = !pathPrefix.empty();
            const int fetchOffset = pathScoped ? 0 : safeOffset;
            const int fetchLimit = pathScoped ? static_cast<int>(source->size()) : safeMaxResults;
            const auto candidateCount = source->size();

            std::vector<const Record*> matchedPage;
            int totalMatched = 0;
            const char* acceleratorMode = "legacy";
            bool pathScopeHandledByV2 = false;
            const auto matchStart = std::chrono::steady_clock::now();
            if (mode == MatchMode::Wildcard)
            {
                std::wstring extension;
                MatchMode simplifiedMode = MatchMode::Wildcard;
                std::wstring simplifiedQuery;
                if (filter == SearchFilter::All && TryGetSimpleExtensionWildcard(normalizedQuery, extension))
                {
                    ExtensionMatch_NoLock(extension, fetchOffset, fetchLimit, matchedPage, totalMatched);
                }
                else if (TrySimplifyWildcard(normalizedQuery, simplifiedMode, simplifiedQuery))
                {
                    if (simplifiedMode == MatchMode::Contains)
                    {
                        if (!TryMatchRecordsV2_NoLock(simplifiedMode, simplifiedQuery, filter, pathPrefix, safeOffset, safeMaxResults, matchedPage, totalMatched, acceleratorMode))
                        {
                            return BuildErrorJson("native v2 contains accelerator is not ready");
                        }

                        pathScopeHandledByV2 = pathScoped;
                    }
                    else
                    {
                        MatchRecords_NoLock(simplifiedMode, simplifiedQuery, filter, *source, fetchOffset, fetchLimit, matchedPage, totalMatched);
                    }
                }
                else
                {
                    MatchRecords_NoLock(mode, normalizedQuery, filter, *source, fetchOffset, fetchLimit, matchedPage, totalMatched);
                }
            }
            else if (mode == MatchMode::Contains)
            {
                if (!TryMatchRecordsV2_NoLock(mode, normalizedQuery, filter, pathPrefix, safeOffset, safeMaxResults, matchedPage, totalMatched, acceleratorMode))
                {
                    return BuildErrorJson("native v2 contains accelerator is not ready");
                }

                pathScopeHandledByV2 = pathScoped;
            }
            else
            {
                MatchRecords_NoLock(mode, normalizedQuery, filter, *source, fetchOffset, fetchLimit, matchedPage, totalMatched);
            }
            const auto matchMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - matchStart).count();

            std::vector<SearchResultRow> rows;
            rows.reserve(matchedPage.size());
            const auto resolveStart = std::chrono::steady_clock::now();

            if (pathScoped && !pathScopeHandledByV2)
            {
                const auto normalizedPrefix = EnsureTrailingSlash(pathPrefix);
                int filteredTotal = 0;
                for (const auto* record : matchedPage)
                {
                    const auto fullPath = ResolveFullPath_NoLock(*record);
                    if (!StartsWithI(fullPath, normalizedPrefix))
                    {
                        continue;
                    }

                    ++filteredTotal;
                    if (filteredTotal <= safeOffset || static_cast<int>(rows.size()) >= safeMaxResults)
                    {
                        continue;
                    }

                    rows.push_back({ fullPath, record->originalName, record->isDirectory });
                }

                totalMatched = filteredTotal;
            }
            else
            {
                for (const auto* record : matchedPage)
                {
                    rows.push_back({ ResolveFullPath_NoLock(*record), record->originalName, record->isDirectory });
                }
            }
            const auto resolveMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - resolveStart).count();

            const bool isTruncated = totalMatched > safeOffset + static_cast<int>(rows.size());
            const auto jsonStart = std::chrono::steady_clock::now();
            auto json = BuildSearchJson(totalMatched, isTruncated, rows);
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
                + " offset=" + std::to_string(safeOffset)
                + " maxResults=" + std::to_string(safeMaxResults)
                + " candidateCount=" + std::to_string(candidateCount)
                + " matched=" + std::to_string(totalMatched)
                + " returned=" + std::to_string(rows.size())
                + " truncated=" + std::string(isTruncated ? "true" : "false")
                + " indexed=" + std::to_string(records_.size())
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

        void Shutdown()
        {
            {
                std::unique_lock<std::shared_mutex> guard(mutex_);
                if (hasShutdown_)
                {
                    return;
                }

                hasShutdown_ = true;
            }

            StopBackgroundCatchUp();
            StopWatchers();
            StopSnapshotWorker(false);

            std::unique_lock<std::shared_mutex> guard(mutex_);
            currentStatusMessage_ = L"native 索引已关闭";
            isBackgroundCatchUpInProgress_ = false;
        }

    private:
        static constexpr std::int32_t SnapshotVersion3 = 3;
        static constexpr std::int32_t V2PostingsSnapshotVersion1 = 1;
        static constexpr std::int32_t V2RuntimeSnapshotVersion1 = 1;
        static constexpr std::int32_t V2TypeBucketsSnapshotVersion1 = 1;
        static constexpr DWORD LiveWatcherBufferSize = 256 * 1024;
        static constexpr DWORD CatchUpBufferSize = 1024 * 1024;
        static constexpr unsigned long long LiveWaitMilliseconds = 1000;
        static constexpr DWORD ErrorJournalEntryDeleted = 1181;
        static constexpr std::uint64_t NtfsRootFileReferenceNumber = 5;
        static constexpr wchar_t SnapshotWriteMutexName[] = L"PackageManager.MftScannerNativeIndex.WriteLock";
        static constexpr DWORD SnapshotWriteLockTimeoutMilliseconds = 200;
        static constexpr auto SnapshotSaveDebounceMilliseconds = std::chrono::milliseconds(1000);

        mutable std::shared_mutex mutex_;
        std::vector<Record> records_;
        std::unordered_map<std::uint64_t, std::size_t> recordIndexByKey_;
        std::vector<std::uint32_t> allIds_;
        std::vector<std::uint32_t> directoryIds_;
        std::vector<std::uint32_t> launchableIds_;
        std::vector<std::uint32_t> scriptIds_;
        std::vector<std::uint32_t> logIds_;
        std::vector<std::uint32_t> configIds_;
        std::unordered_map<std::wstring, std::vector<std::uint32_t>> extensionIds_;
        std::unordered_map<std::wstring_view, std::vector<std::uint32_t>, WStringViewHash, WStringViewEqual> exactNameIds_;
        NativeV2Accelerator v2_;
        std::unordered_map<wchar_t, VolumeState> volumes_;
        std::wstring currentStatusMessage_;
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

        void SnapshotWorkerLoop() const
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
                    SaveSnapshot();
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
                std::unique_lock<std::shared_mutex> guard(mutex_);
                oldToken = watcherStopToken_;
                if (oldToken != nullptr)
                {
                    oldToken->store(true);
                }

                threadsToJoin.swap(watcherThreads_);
                watcherStopToken_ = std::make_shared<std::atomic_bool>(false);
            }

            for (auto& thread : threadsToJoin)
            {
                if (thread.joinable())
                {
                    thread.join();
                }
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
                backgroundCatchUpThread_.join();
            }

            backgroundCatchUpStopToken_ = std::make_shared<std::atomic_bool>(false);
        }

        void StartWatchers_NoLock()
        {
            const auto token = watcherStopToken_;
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
                        result.journalReset = CatchUpVolume(state, result.changes);
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
                    bool deferredChanges = false;
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
                                deferredChanges = true;
                                publishRefresh = true;
                                NativeLogWrite(
                                    "[NATIVE SNAPSHOT CATCHUP DEFERRED] changes="
                                    + std::to_string(totalChangeCount)
                                    + " reason=preserve-ready-v2-generation");
                            }

                            currentStatusMessage_ = L"已索引 " + std::to_wstring(records_.size()) + L" 个对象";
                        }

                        isBackgroundCatchUpInProgress_ = false;
                        const auto watcherStart = std::chrono::steady_clock::now();
                        if (!journalReset)
                        {
                            StartWatchers_NoLock();
                        }
                        const auto watcherStartMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                            std::chrono::steady_clock::now() - watcherStart).count();
                        if (!journalReset && !deferredChanges)
                        {
                            QueueSnapshotSave_NoLock();
                        }
                        publishRefresh = true;
                        NativeLogWrite(
                            "[NATIVE SNAPSHOT CATCHUP TOTAL] catchUpMs="
                            + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                                std::chrono::steady_clock::now() - catchUpStart).count())
                            + " watcherStartMs=" + std::to_string(watcherStartMs)
                            + " indexedCount=" + std::to_string(records_.size()));
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

        void BuildFromMft()
        {
            const auto totalStart = std::chrono::steady_clock::now();
            records_.clear();
            recordIndexByKey_.clear();
            volumes_.clear();
            derivedDirty_ = false;

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

            struct EnumeratedVolume
            {
                wchar_t driveLetter = L'\0';
                bool success = false;
                std::vector<Record> records;
                VolumeState volume;
                std::string error;
            };

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
                            NativeLogWrite(
                                "[NATIVE MFT ENUM] drive=" + std::string(1, static_cast<char>(results[i].driveLetter))
                                + " success elapsedMs=" + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                                    std::chrono::steady_clock::now() - volumeStart).count())
                                + " records=" + std::to_string(results[i].records.size())
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

            for (auto& task : tasks)
            {
                task.get();
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
                    continue;
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

            const auto mergeMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - mergeStart).count();
            const auto buildStart = std::chrono::steady_clock::now();
            RebuildDerivedData_NoLock("mft-build", false);
            const auto buildMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - buildStart).count();
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
            const auto volumePath = BuildVolumePath(driveLetter);
            const HANDLE handle = OpenVolumeHandle(volumePath);
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

            struct MftEnumDataV0Native
            {
                DWORDLONG StartFileReferenceNumber;
                USN LowUsn;
                USN HighUsn;
            };

            std::unordered_map<std::uint64_t, FrnEntry> frnMap;
            frnMap.reserve(300000);

            MftEnumDataV0Native enumData{};
            enumData.StartFileReferenceNumber = 0;
            enumData.LowUsn = 0;
            enumData.HighUsn = MAXLONGLONG;

            DWORD bytesReturned = 0;
            while (DeviceIoControl(
                handle,
                FSCTL_ENUM_USN_DATA,
                &enumData,
                sizeof(enumData),
                buffer,
                CatchUpBufferSize,
                &bytesReturned,
                nullptr))
            {
                if (bytesReturned <= sizeof(USN))
                {
                    break;
                }

                enumData.StartFileReferenceNumber = *reinterpret_cast<DWORDLONG*>(buffer);
                DWORD offset = sizeof(USN);
                while (offset + 60 < bytesReturned)
                {
                    const auto* recordHeader = reinterpret_cast<const USN_RECORD_V2*>(buffer + offset);
                    if (recordHeader->RecordLength == 0)
                    {
                        break;
                    }

                    const auto frn = static_cast<std::uint64_t>(recordHeader->FileReferenceNumber & 0x0000FFFFFFFFFFFFull);
                    const auto parentFrn = static_cast<std::uint64_t>(recordHeader->ParentFileReferenceNumber & 0x0000FFFFFFFFFFFFull);
                    const auto* fileNamePtr = reinterpret_cast<const wchar_t*>(reinterpret_cast<const std::byte*>(recordHeader) + recordHeader->FileNameOffset);
                    const auto fileNameLength = recordHeader->FileNameLength / sizeof(wchar_t);
                    std::wstring fileName(fileNamePtr, fileNamePtr + fileNameLength);
                    const bool isDirectory = (recordHeader->FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;

                    const auto existing = frnMap.find(frn);
                    if (existing == frnMap.end() || fileName.length() > existing->second.name.length())
                    {
                        frnMap[frn] = FrnEntry{ fileName, parentFrn, isDirectory };
                    }

                    offset += recordHeader->RecordLength;
                }
            }

            VolumeState volume;
            volume.driveLetter = driveLetter;
            volume.frnMap = std::move(frnMap);

            USN_JOURNAL_DATA_V0 journalData{};
            if (QueryJournal_NoLock(handle, journalData))
            {
                volume.nextUsn = journalData.NextUsn;
                volume.journalId = journalData.UsnJournalID;
            }

            outRecords.clear();
            outRecords.reserve(volume.frnMap.size());
            for (const auto& pair : volume.frnMap)
            {
                Record record;
                record.frn = pair.first;
                record.parentFrn = pair.second.parentFrn;
                record.driveLetter = driveLetter;
                record.originalName = pair.second.name;
                record.lowerName = ToLower(record.originalName);
                record.isDirectory = pair.second.isDirectory;
                outRecords.push_back(std::move(record));
            }

            outVolume = std::move(volume);

            std::free(buffer);
            CloseHandle(handle);
            return true;
        }

        void RebuildDerivedData_NoLock(const char* reason = nullptr, bool recordsAlreadySorted = false, bool buildV2 = true)
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
            allIds_.clear();
            directoryIds_.clear();
            launchableIds_.clear();
            scriptIds_.clear();
            logIds_.clear();
            configIds_.clear();
            exactNameIds_.clear();
            extensionIds_.clear();
            v2_.Clear();

            recordIndexByKey_.reserve(records_.size());
            allIds_.reserve(records_.size());
            directoryIds_.reserve(records_.size() / 8);
            launchableIds_.reserve(records_.size() / 8);
            exactNameIds_.reserve(records_.size());
            extensionIds_.reserve(records_.size() / 8);

            const auto bucketStart = std::chrono::steady_clock::now();
            for (std::uint32_t i = 0; i < records_.size(); ++i)
            {
                const auto& record = records_[i];
                recordIndexByKey_[MakeRecordKey(record.driveLetter, record.frn)] = i;
                allIds_.push_back(i);
                exactNameIds_[std::wstring_view(record.lowerName)].push_back(i);
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
                    + " sortSkipped=" + std::string(recordsAlreadySorted ? "true" : "false")
                    + " bucketMs=" + std::to_string(bucketMs)
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
            allIds_.clear();
            directoryIds_.clear();
            launchableIds_.clear();
            scriptIds_.clear();
            logIds_.clear();
            configIds_.clear();
            exactNameIds_.clear();
            extensionIds_.clear();

            recordIndexByKey_.reserve(records_.size());
            allIds_.reserve(records_.size());
            directoryIds_.reserve(records_.size() / 8);
            launchableIds_.reserve(records_.size() / 8);
            extensionIds_.reserve(records_.size() / 8);

            const auto bucketStart = std::chrono::steady_clock::now();
            for (std::uint32_t i = 0; i < records_.size(); ++i)
            {
                const auto& record = records_[i];
                recordIndexByKey_[MakeRecordKey(record.driveLetter, record.frn)] = i;
                allIds_.push_back(i);
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
            std::unordered_map<std::uint64_t, std::uint32_t> trigramCounts;
            trigramCounts.reserve(131072);
            const auto workerCount = GetV2WorkerCount(records_.size());
            auto ranges = BuildV2WorkerRanges(records_.size(), workerCount);
            std::vector<std::unordered_map<std::uint64_t, std::uint32_t>> localCounts(workerCount);
            std::vector<std::future<void>> countTasks;
            countTasks.reserve(workerCount);
            for (std::size_t worker = 0; worker < workerCount; ++worker)
            {
                countTasks.emplace_back(std::async(std::launch::async, [this, &ranges, &localCounts, worker]()
                {
                    auto& counts = localCounts[worker];
                    counts.reserve(32768);
                    std::vector<std::uint64_t> uniqueKeys;
                    for (std::uint32_t recordId = ranges[worker].first; recordId < ranges[worker].second; ++recordId)
                    {
                        BuildUniqueTrigramKeys_NoLock(records_[recordId].lowerName, uniqueKeys);
                        for (const auto key : uniqueKeys)
                        {
                            ++counts[key];
                        }
                    }
                }));
            }

            for (auto& task : countTasks)
            {
                task.get();
            }

            for (auto& counts : localCounts)
            {
                for (const auto& pair : counts)
                {
                    trigramCounts[pair.first] += pair.second;
                }
            }

            for (std::uint32_t recordId = 0; recordId < records_.size(); ++recordId)
            {
                const auto& name = records_[recordId].lowerName;
                AddShortQueryTokens_NoLock(name, recordId);
            }

            v2_.trigramEntries.clear();
            v2_.trigramPostings.clear();
            v2_.trigramEntries.reserve(trigramCounts.size());
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

            std::unordered_map<std::uint64_t, std::uint32_t> bucketIndexes;
            bucketIndexes.reserve(v2_.trigramEntries.size());
            std::vector<std::atomic<std::uint32_t>> writePositions(v2_.trigramEntries.size());
            for (std::uint32_t i = 0; i < v2_.trigramEntries.size(); ++i)
            {
                bucketIndexes[v2_.trigramEntries[i].key] = i;
                writePositions[i].store(v2_.trigramEntries[i].offset, std::memory_order_relaxed);
            }

            if (totalPostings > static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max()))
            {
                throw std::bad_alloc();
            }

            std::vector<std::uint32_t> rawPostings(static_cast<std::size_t>(totalPostings));
            std::vector<std::future<void>> fillTasks;
            fillTasks.reserve(workerCount);
            for (std::size_t worker = 0; worker < workerCount; ++worker)
            {
                fillTasks.emplace_back(std::async(std::launch::async, [this, &ranges, &bucketIndexes, &writePositions, &rawPostings, worker]()
                {
                    std::vector<std::uint64_t> uniqueKeys;
                    for (std::uint32_t recordId = ranges[worker].first; recordId < ranges[worker].second; ++recordId)
                    {
                        BuildUniqueTrigramKeys_NoLock(records_[recordId].lowerName, uniqueKeys);
                        for (const auto key : uniqueKeys)
                        {
                            const auto bucketIt = bucketIndexes.find(key);
                            if (bucketIt == bucketIndexes.end())
                            {
                                continue;
                            }

                            const auto pos = writePositions[bucketIt->second].fetch_add(1, std::memory_order_relaxed);
                            rawPostings[pos] = recordId;
                        }
                    }
                }));
            }

            for (auto& task : fillTasks)
            {
                task.get();
            }

            for (const auto& entry : v2_.trigramEntries)
            {
                auto begin = rawPostings.begin() + entry.offset;
                auto end = begin + entry.count;
                std::sort(begin, end);
            }

            v2_.trigramPostings.reserve(static_cast<std::size_t>(std::min<std::uint64_t>(
                totalPostings * 2,
                static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max()))));
            for (auto& entry : v2_.trigramEntries)
            {
                const auto rawOffset = entry.offset;
                const auto byteOffset = static_cast<std::uint32_t>(v2_.trigramPostings.size());
                EncodeDeltaPosting_NoLock(rawPostings, rawOffset, entry.count, v2_.trigramPostings);
                entry.offset = byteOffset;
                entry.byteCount = static_cast<std::uint32_t>(v2_.trigramPostings.size() - byteOffset);
            }
            v2_.trigramPostings.shrink_to_fit();

            CompressShortPostings_NoLock();

            BuildParentOrder_NoLock();
            v2_.buildMs = static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - totalStart).count());
            v2_.postingsBytes =
                (static_cast<std::uint64_t>(v2_.trigramEntries.size()) * sizeof(NativeV2BucketEntry))
                + static_cast<std::uint64_t>(v2_.trigramPostings.size())
                + EstimateShortPostingBytes_NoLock()
                + (static_cast<std::uint64_t>(v2_.parentOrder.size()) * sizeof(std::uint32_t));
            v2_.ready = true;
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
            std::sort(posting.begin(), posting.end());
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

        void CompressShortPostings_NoLock()
        {
            v2_.asciiCharEntries = {};
            v2_.asciiBigramEntries = {};
            v2_.nonAsciiShortEntries.clear();
            v2_.asciiCharCompressedPostings.clear();
            v2_.asciiBigramCompressedPostings.clear();
            v2_.nonAsciiShortCompressedPostings.clear();

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
            v2_.childIds.clear();
            v2_.childIds.reserve(records_.size() / 4);
            for (std::uint32_t i = 0; i < records_.size(); ++i)
            {
                v2_.parentOrder[i] = i;
                v2_.childIds[MakeRecordKey(records_[i].driveLetter, records_[i].parentFrn)].push_back(i);
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

            for (auto& pair : v2_.childIds)
            {
                auto& ids = pair.second;
                std::sort(ids.begin(), ids.end());
                ids.erase(std::unique(ids.begin(), ids.end()), ids.end());
                ids.shrink_to_fit();
            }
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

        std::uint64_t EstimateNativeMemoryBytes_NoLock() const
        {
            std::uint64_t bytes = static_cast<std::uint64_t>(records_.capacity()) * sizeof(Record);
            for (const auto& record : records_)
            {
                bytes += (static_cast<std::uint64_t>(record.lowerName.capacity()) + record.originalName.capacity()) * sizeof(wchar_t);
            }

            bytes += static_cast<std::uint64_t>(allIds_.capacity()
                + directoryIds_.capacity()
                + launchableIds_.capacity()
                + scriptIds_.capacity()
                + logIds_.capacity()
                + configIds_.capacity()
                + v2_.trigramPostings.capacity()
                + v2_.parentOrder.capacity()) * sizeof(std::uint32_t);
            bytes += static_cast<std::uint64_t>(v2_.trigramEntries.capacity()) * sizeof(NativeV2BucketEntry);
            bytes += EstimateShortPostingBytes_NoLock();
            return bytes;
        }

        bool CatchUpVolume(UsnReadState& volumeState, std::vector<NativeChange>& allChanges)
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
                changes.clear();
                if (!ReadUsnChanges_NoLock(handle, volumeState, buffer, CatchUpBufferSize, 0, nullptr, changes, journalReset))
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

                {
                    std::unique_lock<std::shared_mutex> guard(mutex_);
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
                        publishRefresh = true;
                        NativeLogWrite(
                            "[NATIVE LIVE WATCH DEFERRED] changes="
                            + std::to_string(changes.size())
                            + " reason=preserve-ready-v2-generation");
                    }

                }

                if (shouldSave)
                {
                    QueueSnapshotSave();
                }

                if (publishRefresh)
                {
                    PublishStatus(true);
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

        bool ApplyChange_NoLock(const NativeChange& change, std::string* payload)
        {
            auto volumeIt = volumes_.find(change.driveLetter);
            if (volumeIt == volumes_.end())
            {
                return false;
            }

            switch (change.kind)
            {
            case ChangeKind::Create:
                return ApplyCreate_NoLock(volumeIt->second, change, payload);
            case ChangeKind::Delete:
                return ApplyDelete_NoLock(volumeIt->second, change, payload);
            case ChangeKind::Rename:
                return ApplyRename_NoLock(volumeIt->second, change, payload);
            default:
                return false;
            }
        }

        bool ApplyCollectedChanges_NoLock(const std::vector<NativeChange>& changes, std::vector<std::string>* payloads)
        {
            bool changed = false;
            if (payloads != nullptr)
            {
                payloads->clear();
                payloads->reserve(changes.size());
            }

            for (const auto& change : changes)
            {
                std::string payload;
                if (ApplyChange_NoLock(change, payloads == nullptr ? nullptr : &payload))
                {
                    changed = true;
                    if (payloads != nullptr && !payload.empty())
                    {
                        payloads->push_back(std::move(payload));
                    }
                }
            }

            return changed;
        }

        bool ApplyCreate_NoLock(VolumeState& volume, const NativeChange& change, std::string* payload)
        {
            Record record;
            record.frn = change.frn;
            record.parentFrn = change.parentFrn;
            record.driveLetter = change.driveLetter;
            record.isDirectory = change.isDirectory;
            record.originalName = change.originalName;
            record.lowerName = change.lowerName;

            const auto key = MakeRecordKey(record.driveLetter, record.frn);
            const auto existing = recordIndexByKey_.find(key);
            if (existing != recordIndexByKey_.end())
            {
                records_[existing->second] = record;
            }
            else
            {
                recordIndexByKey_[key] = records_.size();
                records_.push_back(record);
            }

            volume.frnMap[record.frn] = FrnEntry{ record.originalName, record.parentFrn, record.isDirectory };
            volume.pathCache.erase(record.frn);
            derivedDirty_ = true;

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

        bool ApplyDelete_NoLock(VolumeState& volume, const NativeChange& change, std::string* payload)
        {
            const auto key = MakeRecordKey(change.driveLetter, change.frn);
            const auto existing = recordIndexByKey_.find(key);
            if (existing == recordIndexByKey_.end())
            {
                volume.frnMap.erase(change.frn);
                volume.pathCache.erase(change.frn);
                return false;
            }

            const auto removedRecord = records_[existing->second];
            const auto fullPath = ResolveFullPath_NoLock(removedRecord);
            RemoveRecordAt_NoLock(existing->second);
            volume.frnMap.erase(change.frn);
            volume.pathCache.erase(change.frn);
            derivedDirty_ = true;

            if (payload != nullptr)
            {
                *payload = BuildChangeJson(
                    "Deleted",
                    removedRecord.lowerName,
                    fullPath,
                    L"",
                    L"",
                    L"",
                    removedRecord.isDirectory);
            }

            return true;
        }

        bool ApplyRename_NoLock(VolumeState& volume, const NativeChange& change, std::string* payload)
        {
            const auto key = MakeRecordKey(change.driveLetter, change.frn);

            Record record;
            record.frn = change.frn;
            record.parentFrn = change.parentFrn;
            record.driveLetter = change.driveLetter;
            record.isDirectory = change.isDirectory;
            record.originalName = change.originalName;
            record.lowerName = change.lowerName;

            std::wstring oldFullPath = ResolveFullPath_NoLock(change.driveLetter, change.oldParentFrn, change.oldLowerName);
            const auto existing = recordIndexByKey_.find(key);
            if (existing != recordIndexByKey_.end())
            {
                oldFullPath = ResolveFullPath_NoLock(records_[existing->second]);
                records_[existing->second] = record;
            }
            else
            {
                recordIndexByKey_[key] = records_.size();
                records_.push_back(record);
            }

            volume.frnMap[record.frn] = FrnEntry{ record.originalName, record.parentFrn, record.isDirectory };
            volume.pathCache.erase(record.frn);
            derivedDirty_ = true;

            if (payload != nullptr)
            {
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
            const auto removedKey = MakeRecordKey(records_[index].driveLetter, records_[index].frn);
            const auto lastIndex = records_.size() - 1;
            if (index != lastIndex)
            {
                records_[index] = std::move(records_[lastIndex]);
                recordIndexByKey_[MakeRecordKey(records_[index].driveLetter, records_[index].frn)] = index;
            }

            records_.pop_back();
            recordIndexByKey_.erase(removedKey);
        }

        void MatchRecords_NoLock(
            MatchMode mode,
            const std::wstring& query,
            SearchFilter filter,
            const std::vector<std::uint32_t>& source,
            int offset,
            int maxResults,
            std::vector<const Record*>& page,
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
                const auto& record = records_[recordIndex];
                bool matched = false;

                try
                {
                    switch (mode)
                    {
                    case MatchMode::Prefix:
                        matched = record.lowerName.rfind(query, 0) == 0;
                        break;
                    case MatchMode::Suffix:
                        matched = EndsWith(record.lowerName, query);
                        break;
                    case MatchMode::Regex:
                        matched = std::regex_search(record.lowerName, regex);
                        break;
                    case MatchMode::Wildcard:
                        matched = std::regex_search(record.lowerName, regex);
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
                    page.push_back(&record);
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
            std::vector<const Record*>& page,
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

            const bool requiresContainsVerify = query.length() >= 3;
            page.reserve(static_cast<std::size_t>(std::min(maxResults, 64)));
            for (const auto recordId : candidates.ids)
            {
                if (recordId >= records_.size())
                {
                    continue;
                }

                const auto& record = records_[recordId];
                if (requiresContainsVerify && !ContainsText(record.lowerName, query))
                {
                    continue;
                }

                ++totalMatched;
                if (totalMatched > offset && static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(&record);
                }
            }

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

        void ApplyPathScopeToCandidates_NoLock(std::vector<std::uint32_t>& ids, const std::wstring& pathPrefix) const
        {
            const auto directoryId = TryResolveDirectoryRecordId_NoLock(pathPrefix);
            if (directoryId == std::numeric_limits<std::uint32_t>::max())
            {
                ids.clear();
                return;
            }

            std::vector<std::uint32_t> subtree;
            BuildSubtreeRecordIds_NoLock(directoryId, subtree);
            IntersectSortedVectors(ids, subtree);
        }

        std::uint32_t TryResolveDirectoryRecordId_NoLock(const std::wstring& pathPrefix) const
        {
            auto normalized = EnsureTrailingSlash(pathPrefix);
            if (normalized.length() < 3 || normalized[1] != L':' || normalized[2] != L'\\')
            {
                return std::numeric_limits<std::uint32_t>::max();
            }

            const wchar_t drive = static_cast<wchar_t>(towupper(normalized[0]));
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
                auto childIt = v2_.childIds.find(MakeRecordKey(drive, currentParent));
                if (childIt == v2_.childIds.end() && currentParent == NtfsRootFileReferenceNumber)
                {
                    childIt = v2_.childIds.find(MakeRecordKey(drive, 0));
                }

                if (childIt == v2_.childIds.end())
                {
                    return std::numeric_limits<std::uint32_t>::max();
                }

                bool found = false;
                for (const auto childId : childIt->second)
                {
                    if (childId >= records_.size())
                    {
                        continue;
                    }

                    const auto& child = records_[childId];
                    if (child.isDirectory && child.lowerName == segment)
                    {
                        currentParent = child.frn;
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

        void BuildSubtreeRecordIds_NoLock(std::uint32_t directoryId, std::vector<std::uint32_t>& ids) const
        {
            ids.clear();
            if (directoryId >= records_.size())
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
                const auto& record = records_[current];
                const auto children = v2_.childIds.find(MakeRecordKey(record.driveLetter, record.frn));
                if (children == v2_.childIds.end())
                {
                    continue;
                }

                for (const auto childId : children->second)
                {
                    if (childId < records_.size())
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
            std::vector<std::uint32_t> sortedFilter = filterIds;
            std::sort(ids.begin(), ids.end());
            ids.erase(std::unique(ids.begin(), ids.end()), ids.end());
            std::sort(sortedFilter.begin(), sortedFilter.end());
            sortedFilter.erase(std::unique(sortedFilter.begin(), sortedFilter.end()), sortedFilter.end());

            std::vector<std::uint32_t> intersection;
            intersection.reserve(std::min(ids.size(), sortedFilter.size()));
            std::set_intersection(
                ids.begin(),
                ids.end(),
                sortedFilter.begin(),
                sortedFilter.end(),
                std::back_inserter(intersection));
            ids.swap(intersection);
        }

        void PrefixMatch_NoLock(
            const std::wstring& prefix,
            const std::vector<std::uint32_t>& source,
            int offset,
            int maxResults,
            std::vector<const Record*>& page,
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
                const auto& name = records_[source[mid]].lowerName;
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
                const auto& record = records_[source[i]];
                if (!StartsWith(record.lowerName, prefix))
                {
                    break;
                }

                ++totalMatched;
                if (totalMatched > offset && static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(&record);
                }
            }
        }

        void SuffixMatch_NoLock(
            const std::wstring& suffix,
            const std::vector<std::uint32_t>& source,
            int offset,
            int maxResults,
            std::vector<const Record*>& page,
            int& totalMatched) const
        {
            totalMatched = 0;
            for (const auto recordIndex : source)
            {
                const auto& record = records_[recordIndex];
                if (!EndsWith(record.lowerName, suffix))
                {
                    continue;
                }

                ++totalMatched;
                if (totalMatched > offset && static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(&record);
                }
            }
        }

        void ExtensionMatch_NoLock(
            const std::wstring& extension,
            int offset,
            int maxResults,
            std::vector<const Record*>& page,
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
                page.push_back(&records_[bucketIt->second[static_cast<std::size_t>(i)]]);
            }
        }

        void ContainsMatch_NoLock(
            const std::wstring& query,
            SearchFilter filter,
            const std::vector<std::uint32_t>& source,
            int offset,
            int maxResults,
            std::vector<const Record*>& page,
            int& totalMatched) const
        {
            totalMatched = 0;
            bool hasExactBucket = false;

            if (filter == SearchFilter::All)
            {
            const auto exactIt = exactNameIds_.find(std::wstring_view(query));
                if (exactIt != exactNameIds_.end())
                {
                    hasExactBucket = true;
                    for (const auto recordIndex : exactIt->second)
                    {
                        ++totalMatched;
                        if (totalMatched > offset && static_cast<int>(page.size()) < maxResults)
                        {
                            page.push_back(&records_[recordIndex]);
                        }
                    }
                }
            }

            for (const auto recordIndex : source)
            {
                const auto& record = records_[recordIndex];
                if (hasExactBucket && record.lowerName == query)
                {
                    continue;
                }

                if (record.lowerName.find(query) == std::wstring::npos)
                {
                    continue;
                }

                ++totalMatched;
                if (totalMatched > offset && static_cast<int>(page.size()) < maxResults)
                {
                    page.push_back(&record);
                }
            }
        }

        std::wstring ResolveFullPath_NoLock(const Record& record)
        {
            return ResolveFullPath_NoLock(record.driveLetter, record.parentFrn, record.originalName);
        }

        std::wstring ResolveFullPath_NoLock(wchar_t driveLetter, std::uint64_t parentFrn, const std::wstring& name)
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

        std::wstring ResolveDirectoryPath_NoLock(VolumeState& volume, std::uint64_t frn)
        {
            if (frn == 0)
            {
                return std::wstring(1, volume.driveLetter) + L":\\";
            }

            const auto cached = volume.pathCache.find(frn);
            if (cached != volume.pathCache.end())
            {
                return cached->second;
            }

            const auto entry = volume.frnMap.find(frn);
            if (entry == volume.frnMap.end())
            {
                return std::wstring(1, volume.driveLetter) + L":\\";
            }

            auto parentPath = ResolveDirectoryPath_NoLock(volume, entry->second.parentFrn);
            if (!parentPath.empty() && parentPath.back() != L'\\')
            {
                parentPath.push_back(L'\\');
            }

            auto fullPath = parentPath + entry->second.name;
            volume.pathCache[frn] = fullPath;
            return fullPath;
        }

        bool TryLoadSnapshot()
        {
            const auto totalStart = std::chrono::steady_clock::now();
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
            if (!input.good() || version != SnapshotVersion3)
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
            records_ = std::move(loadedRecords);
            volumes_ = std::move(loadedVolumes);
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
                + " version=" + std::to_string(SnapshotVersion3)
                + " fileBytes=" + std::to_string(fileSizeError ? 0 : fileBytes)
                + " records=" + std::to_string(records_.size())
                + " volumes=" + std::to_string(volumes_.size())
                + " frnEntries=" + std::to_string(CountFrnEntries(volumes_)));
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

        void SaveSnapshot() const
        {
            std::vector<Record> recordsCopy;
            std::unordered_map<wchar_t, VolumeState> volumesCopy;
            NativeV2Accelerator v2Copy;

            {
                std::shared_lock<std::shared_mutex> guard(mutex_);
                if (records_.empty() || volumes_.empty())
                {
                    return;
                }

                recordsCopy = records_;
                volumesCopy = volumes_;
                v2Copy.trigramEntries = v2_.trigramEntries;
                v2Copy.trigramPostings = v2_.trigramPostings;
                v2Copy.asciiCharEntries = v2_.asciiCharEntries;
                v2Copy.asciiBigramEntries = v2_.asciiBigramEntries;
                v2Copy.nonAsciiShortEntries = v2_.nonAsciiShortEntries;
                v2Copy.asciiCharCompressedPostings = v2_.asciiCharCompressedPostings;
                v2Copy.asciiBigramCompressedPostings = v2_.asciiBigramCompressedPostings;
                v2Copy.nonAsciiShortCompressedPostings = v2_.nonAsciiShortCompressedPostings;
                v2Copy.parentOrder = v2_.parentOrder;
                v2Copy.childIds = v2_.childIds;
                v2Copy.ready = v2_.ready;
                v2Copy.buildMs = v2_.buildMs;
                v2Copy.postingsBytes = v2_.postingsBytes;
            }

            WriteSnapshotFile(recordsCopy, volumesCopy);
            WriteV2PostingsSnapshotFile(recordsCopy.size(), v2Copy);
            WriteV2RuntimeSnapshotFile(recordsCopy.size(), v2Copy);
            WriteV2TypeBucketsSnapshotFile(recordsCopy.size(), allIds_, directoryIds_, launchableIds_, scriptIds_, logIds_, configIds_, extensionIds_);
        }

        void QueueSnapshotSave()
        {
            std::unique_lock<std::shared_mutex> guard(mutex_);
            QueueSnapshotSave_NoLock();
        }

        void QueueSnapshotSave_NoLock() const
        {
            if (records_.empty() || volumes_.empty() || hasShutdown_)
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

            output.write(reinterpret_cast<const char*>(&SnapshotVersion3), sizeof(SnapshotVersion3));
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

                const auto orderedEntries = GetOrderedFrnEntries(volume->frnMap);
                const auto frnCount = static_cast<std::int32_t>(orderedEntries.size());
                output.write(reinterpret_cast<const char*>(&frnCount), sizeof(frnCount));
                for (const auto* frnPair : orderedEntries)
                {
                    const auto it = stringIndexMap.find(frnPair->second.name);
                    const auto nameIndex = it == stringIndexMap.end() ? 0 : it->second;
                    output.write(reinterpret_cast<const char*>(&nameIndex), sizeof(nameIndex));
                    output.write(reinterpret_cast<const char*>(&frnPair->first), sizeof(frnPair->first));
                    output.write(reinterpret_cast<const char*>(&frnPair->second.parentFrn), sizeof(frnPair->second.parentFrn));
                    if (!WriteSnapshotBool(output, frnPair->second.isDirectory))
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
                + " version=" + std::to_string(SnapshotVersion3)
                + " fileBytes=" + std::to_string(fileSizeError ? 0 : fileBytes)
                + " records=" + std::to_string(records.size())
                + " volumes=" + std::to_string(volumes.size())
                + " frnEntries=" + std::to_string(CountFrnEntries(volumes)));
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
                || recordCount != static_cast<std::int32_t>(records_.size()))
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
                || version != V2RuntimeSnapshotVersion1
                || recordCount != static_cast<std::int32_t>(records_.size()))
            {
                NativeLogWrite("[NATIVE V2 RUNTIME LOAD] miss reason=version-or-record-count");
                return false;
            }

            input.read(reinterpret_cast<char*>(v2_.asciiCharEntries.data()),
                static_cast<std::streamsize>(v2_.asciiCharEntries.size() * sizeof(NativeV2ShortBucketEntry)));
            input.read(reinterpret_cast<char*>(v2_.asciiBigramEntries.data()),
                static_cast<std::streamsize>(v2_.asciiBigramEntries.size() * sizeof(NativeV2ShortBucketEntry)));
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

            std::uint32_t childBucketCount = 0;
            input.read(reinterpret_cast<char*>(&childBucketCount), sizeof(childBucketCount));
            if (!input.good())
            {
                return false;
            }

            v2_.childIds.clear();
            v2_.childIds.reserve(childBucketCount);
            for (std::uint32_t i = 0; i < childBucketCount; ++i)
            {
                std::uint64_t key = 0;
                input.read(reinterpret_cast<char*>(&key), sizeof(key));
                std::vector<std::uint32_t> ids;
                if (!input.good() || !ReadUInt32Vector(input, ids))
                {
                    return false;
                }

                v2_.childIds.emplace(key, std::move(ids));
            }

            NativeLogWrite(
                "[NATIVE V2 RUNTIME LOAD] success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - start).count())
                + " shortBytes=" + std::to_string(
                    v2_.asciiCharCompressedPostings.size()
                    + v2_.asciiBigramCompressedPostings.size()
                    + v2_.nonAsciiShortCompressedPostings.size())
                + " parentOrder=" + std::to_string(v2_.parentOrder.size())
                + " childBuckets=" + std::to_string(v2_.childIds.size()));
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

            const auto version = V2RuntimeSnapshotVersion1;
            const auto count = static_cast<std::int32_t>(recordCount);
            output.write(reinterpret_cast<const char*>(&version), sizeof(version));
            output.write(reinterpret_cast<const char*>(&count), sizeof(count));
            output.write(reinterpret_cast<const char*>(v2.asciiCharEntries.data()),
                static_cast<std::streamsize>(v2.asciiCharEntries.size() * sizeof(NativeV2ShortBucketEntry)));
            output.write(reinterpret_cast<const char*>(v2.asciiBigramEntries.data()),
                static_cast<std::streamsize>(v2.asciiBigramEntries.size() * sizeof(NativeV2ShortBucketEntry)));
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

            const auto childBucketCount = static_cast<std::uint32_t>(v2.childIds.size());
            output.write(reinterpret_cast<const char*>(&childBucketCount), sizeof(childBucketCount));
            for (const auto& pair : v2.childIds)
            {
                output.write(reinterpret_cast<const char*>(&pair.first), sizeof(pair.first));
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
                "[NATIVE V2 RUNTIME SAVE] success elapsedMs="
                + std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - start).count())
                + " shortBytes=" + std::to_string(
                    v2.asciiCharCompressedPostings.size()
                    + v2.asciiBigramCompressedPostings.size()
                    + v2.nonAsciiShortCompressedPostings.size())
                + " parentOrder=" + std::to_string(v2.parentOrder.size())
                + " childBuckets=" + std::to_string(v2.childIds.size()));
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
                || recordCount != static_cast<std::int32_t>(records_.size()))
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
            ss << "\"indexedCount\":" << records_.size() << ",";
            ss << "\"currentStatusMessage\":\"" << EscapeJson(WideToUtf8(currentStatusMessage_)) << "\",";
            ss << "\"isBackgroundCatchUpInProgress\":" << (isBackgroundCatchUpInProgress_ ? "true" : "false") << ",";
            ss << "\"requireSearchRefresh\":" << (requireSearchRefresh ? "true" : "false") << ",";
            ss << "\"engineVersion\":\"native-v2\",";
            ss << "\"readyStage\":\"" << (v2_.ready ? "records+trigram+shortQuery+pathOrder+typeBuckets" : "not-ready") << "\",";
            ss << "\"capabilities\":{";
            ss << "\"records\":" << (!records_.empty() ? "true" : "false") << ",";
            ss << "\"trigram\":" << (v2_.ready ? "true" : "false") << ",";
            ss << "\"shortQuery\":" << (v2_.ready ? "true" : "false") << ",";
            ss << "\"pathOrder\":" << (v2_.ready ? "true" : "false") << ",";
            ss << "\"typeBuckets\":" << (!allIds_.empty() ? "true" : "false");
            ss << "},";
            ss << "\"buildMetrics\":{";
            ss << "\"v2BuildMs\":" << v2_.buildMs << ",";
            ss << "\"v2TrigramBuckets\":" << v2_.trigramEntries.size() << ",";
            ss << "\"v2TrigramBytes\":" << v2_.trigramPostings.size() << ",";
            ss << "\"v2PostingsBytes\":" << v2_.postingsBytes << ",";
            ss << "\"shortPostingsBytes\":" << (
                v2_.asciiCharCompressedPostings.size()
                + v2_.asciiBigramCompressedPostings.size()
                + v2_.nonAsciiShortCompressedPostings.size()) << ",";
            ss << "\"pathOrderBytes\":" << (
                static_cast<std::uint64_t>(v2_.parentOrder.size()) * sizeof(std::uint32_t)) << ",";
            ss << "\"generationId\":" << generationId_.load(std::memory_order_relaxed) << ",";
            ss << "\"snapshotFormatVersion\":" << V2RuntimeSnapshotVersion1 << ",";
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
            ss << "\"totalIndexedCount\":" << records_.size() << ",";
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

        static bool ContainsText(const std::wstring& value, const std::wstring& query)
        {
            if (query.empty())
            {
                return true;
            }

            if (query.length() == 1)
            {
                return value.find(query[0]) != std::wstring::npos;
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

            return value.find(query) != std::wstring::npos;
        }

        static std::uint64_t PackTrigram(wchar_t a, wchar_t b, wchar_t c)
        {
            return (static_cast<std::uint64_t>(static_cast<std::uint16_t>(a)) << 32)
                | (static_cast<std::uint64_t>(static_cast<std::uint16_t>(b)) << 16)
                | static_cast<std::uint64_t>(static_cast<std::uint16_t>(c));
        }

        static std::uint64_t PackShortToken(wchar_t a, wchar_t b, int length)
        {
            return (static_cast<std::uint64_t>(static_cast<std::uint16_t>(length)) << 48)
                | (static_cast<std::uint64_t>(static_cast<std::uint16_t>(a)) << 24)
                | static_cast<std::uint64_t>(static_cast<std::uint16_t>(b));
        }

        static bool StartsWith(const std::wstring& value, const std::wstring& prefix)
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

        static std::wstring GetExtension(const std::wstring& lowerName)
        {
            const auto dotIndex = lowerName.find_last_of(L'.');
            if (dotIndex == std::wstring::npos || dotIndex + 1 >= lowerName.length())
            {
                return {};
            }

            return lowerName.substr(dotIndex);
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

        static std::filesystem::path GetV2RuntimeSnapshotPath()
        {
            return GetSnapshotDirectoryPath() / L"index-native-v2.runtime.bin";
        }

        static std::filesystem::path GetV2TypeBucketsSnapshotPath()
        {
            return GetSnapshotDirectoryPath() / L"index-native-v2.type-buckets.bin";
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

            for (const auto* volume : volumes)
            {
                for (const auto& pair : volume->frnMap)
                {
                    add(pair.second.name);
                }
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

    __declspec(dllexport) int __cdecl pm_index_get_state(void* handle, const char* requestJsonUtf8, char** responseJsonUtf8)
    {
        (void)requestJsonUtf8;
        return ExecuteOperation(static_cast<NativeIndex*>(handle), responseJsonUtf8, [handle]()
        {
            return static_cast<NativeIndex*>(handle)->GetState();
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
}
