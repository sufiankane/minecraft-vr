#include "cg/capture/cgrec.hpp"

#include <algorithm>
#include <array>
#include <charconv>
#include <cstddef>
#include <cstdint>
#include <fstream>
#include <iomanip>
#include <optional>
#include <sstream>
#include <string>

#include "cg/capture/pgm.hpp"

#include <nlohmann/json.hpp>

namespace cg::capture {

namespace {

using json = nlohmann::json;

constexpr const char *kManifestName = "manifest.json";
constexpr const char *kCsvName = "stereo.csv";
constexpr const char *kFramesDirName = "frames";
constexpr const char *kCsvHeader = "seq,host_time_ns,sdk_time_s,width,height";

constexpr const char *kManifestMissing = "cgrec: manifest.json is missing";
constexpr const char *kManifestUnreadable = "cgrec: manifest.json cannot be read or parsed";
constexpr const char *kSchemaUnsupported = "cgrec: the manifest schema is not supported";
constexpr const char *kGeometryInvalid = "cgrec: the manifest geometry is invalid";
constexpr const char *kStorageInvalid = "cgrec: the manifest storage kind is invalid";
constexpr const char *kCsvMissing = "cgrec: stereo.csv is missing";
constexpr const char *kCsvUnreadable = "cgrec: stereo.csv cannot be read";
constexpr const char *kCsvHeaderBad = "cgrec: stereo.csv has an unexpected header";
constexpr const char *kCsvRowBad = "cgrec: stereo.csv has a malformed row";
constexpr const char *kCsvOrderBad = "cgrec: stereo.csv rows are not strictly increasing";
constexpr const char *kCsvGeometryBad = "cgrec: stereo.csv geometry disagrees with the manifest";
constexpr const char *kCountMismatch = "cgrec: the recorded frame count disagrees with the manifest";
constexpr const char *kFrameMissing = "cgrec: a recorded frame file is missing";
constexpr const char *kFrameSizeBad = "cgrec: a recorded frame file has the wrong size";
constexpr const char *kFrameReadBad = "cgrec: a recorded frame file cannot be read";
constexpr const char *kManifestWriteFailed = "cgrec: manifest.json cannot be written";

constexpr std::array<const char *, 4> kPgmSuffixes = {"_l0", "_r0", "_l1", "_r1"};
constexpr int kSequenceWidth = 8;
constexpr std::size_t kCsvFieldCount = 5;

[[nodiscard]] Status Named(const char *reason) { return Status{StatusCode::Unsupported, reason}; }

[[nodiscard]] std::string StorageText(FrameStorage storage) { return storage == FrameStorage::Pgm ? "pgm" : "bin"; }

[[nodiscard]] std::optional<FrameStorage> StorageFromText(const std::string &text) {
    if (text == "pgm") {
        return FrameStorage::Pgm;
    }
    if (text == "bin") {
        return FrameStorage::PackedBin;
    }
    return std::nullopt;
}

[[nodiscard]] std::string PgmFramePath(const std::filesystem::path &session_dir, std::uint64_t seq, std::size_t image) {
    return (session_dir / kFramesDirName / (FrameFileStem(seq) + kPgmSuffixes.at(image) + ".pgm")).string();
}

[[nodiscard]] std::string BinFramePath(const std::filesystem::path &session_dir, std::uint64_t seq) {
    return (session_dir / kFramesDirName / (FrameFileStem(seq) + ".bin")).string();
}

/// Header bytes of the PGM form this writer emits: "P5\n<w> <h>\n255\n".
[[nodiscard]] std::uintmax_t PgmHeaderBytes(int width, int height) {
    const std::string header = "P5\n" + std::to_string(width) + ' ' + std::to_string(height) + "\n255\n";
    return header.size();
}

/// `from_chars` requires raw pointers; one bounded helper keeps the arithmetic
/// in a single place.
[[nodiscard]] const char *TextEnd(const std::string_view text) noexcept {
    // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-pointer-arithmetic) — from_chars requires raw pointers.
    return text.data() + text.size();
}

[[nodiscard]] bool ParseUint64(const std::string_view text, std::uint64_t &out) noexcept {
    out = 0;
    const char *const end = TextEnd(text);
    // NOLINTNEXTLINE(bugprone-suspicious-stringview-data-usage) — bounded by `end`.
    const std::from_chars_result result = std::from_chars(text.data(), end, out);
    return result.ec == std::errc{} && result.ptr == end;
}

[[nodiscard]] bool ParseInt(const std::string_view text, int &out) noexcept {
    out = 0;
    const char *const end = TextEnd(text);
    // NOLINTNEXTLINE(bugprone-suspicious-stringview-data-usage) — bounded by `end`.
    const std::from_chars_result result = std::from_chars(text.data(), end, out);
    return result.ec == std::errc{} && result.ptr == end;
}

[[nodiscard]] bool ParseDouble(const std::string_view text, double &out) noexcept {
    out = 0.0;
    const char *const end = TextEnd(text);
    // NOLINTNEXTLINE(bugprone-suspicious-stringview-data-usage) — bounded by `end`.
    const std::from_chars_result result = std::from_chars(text.data(), end, out);
    return result.ec == std::errc{} && result.ptr == end;
}

[[nodiscard]] bool SplitRow(const std::string &line, std::array<std::string_view, kCsvFieldCount> &fields) {
    std::size_t start = 0;
    for (std::string_view &field : fields) {
        const std::size_t comma = line.find(',', start);
        if (comma == std::string::npos) {
            field = std::string_view{line}.substr(start);
            start = line.size() + 1;
        } else {
            field = std::string_view{line}.substr(start, comma - start);
            start = comma + 1;
        }
    }
    return start == line.size() + 1;
}

[[nodiscard]] Result<CgrecManifest> ReadManifest(const std::filesystem::path &session_dir) {
    std::ifstream input(session_dir / kManifestName);
    if (!input) {
        return Err<CgrecManifest>(Named(kManifestMissing));
    }
    json document;
    try {
        input >> document;
    } catch (const json::exception &) {
        return Err<CgrecManifest>(Named(kManifestUnreadable));
    }
    CgrecManifest manifest;
    try {
        manifest.schema = document.value("schema", 0);
        manifest.width = document.value("width", 0);
        manifest.height = document.value("height", 0);
        manifest.stride = document.value("stride", 0);
        manifest.frame_count = document.value("frame_count", 0U);
        manifest.dropped = document.value("dropped", 0U);
        manifest.dof = document.value("dof", std::string{});
        manifest.sdk_version = document.value("sdk_version", std::string{});
        manifest.created_utc = document.value("created_utc", std::string{});
        const std::string storage = document.value("storage", std::string{});
        const std::optional<FrameStorage> kind = StorageFromText(storage);
        if (!kind.has_value()) {
            return Err<CgrecManifest>(Named(kStorageInvalid));
        }
        manifest.storage = *kind;
    } catch (const json::exception &) {
        return Err<CgrecManifest>(Named(kManifestUnreadable));
    }
    return Ok(manifest);
}

} // namespace

std::string FrameFileStem(std::uint64_t seq) {
    std::ostringstream name;
    name << std::setw(kSequenceWidth) << std::setfill('0') << seq;
    return name.str();
}

Result<void> WriteManifest(const std::filesystem::path &session_dir, const CgrecManifest &manifest) {
    const json document{{"schema", manifest.schema},
                        {"width", manifest.width},
                        {"height", manifest.height},
                        {"stride", manifest.stride},
                        {"frame_count", manifest.frame_count},
                        {"dropped", manifest.dropped},
                        {"storage", StorageText(manifest.storage)},
                        {"dof", manifest.dof},
                        {"sdk_version", manifest.sdk_version},
                        {"created_utc", manifest.created_utc}};
    std::ofstream output(session_dir / kManifestName, std::ios::trunc);
    if (!output) {
        return Err<void>(Named(kManifestWriteFailed));
    }
    output << document.dump(2) << '\n';
    if (!output) {
        return Err<void>(Named(kManifestWriteFailed));
    }
    return Ok();
}

Result<std::vector<StereoCsvRow>> ReadStereoCsv(const std::filesystem::path &session_dir) {
    std::ifstream input(session_dir / kCsvName);
    if (!input) {
        return Err<std::vector<StereoCsvRow>>(Named(kCsvMissing));
    }
    std::string line;
    if (!std::getline(input, line)) {
        return Err<std::vector<StereoCsvRow>>(Named(kCsvUnreadable));
    }
    if (!line.empty() && line.back() == '\r') {
        line.pop_back();
    }
    if (line != kCsvHeader) {
        return Err<std::vector<StereoCsvRow>>(Named(kCsvHeaderBad));
    }
    std::vector<StereoCsvRow> rows;
    std::uint64_t previous_seq = 0;
    while (std::getline(input, line)) {
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        if (line.empty()) {
            continue;
        }
        std::array<std::string_view, kCsvFieldCount> fields{};
        if (!SplitRow(line, fields)) {
            return Err<std::vector<StereoCsvRow>>(Named(kCsvRowBad));
        }
        StereoCsvRow row;
        std::uint64_t host_time = 0;
        if (!ParseUint64(fields.at(0), row.seq) || !ParseUint64(fields.at(1), host_time) ||
            !ParseDouble(fields.at(2), row.sdk_time_s) || !ParseInt(fields.at(3), row.width) ||
            !ParseInt(fields.at(4), row.height)) {
            return Err<std::vector<StereoCsvRow>>(Named(kCsvRowBad));
        }
        row.time = static_cast<HostTime>(host_time);
        if (!rows.empty() && row.seq <= previous_seq) {
            return Err<std::vector<StereoCsvRow>>(Named(kCsvOrderBad));
        }
        previous_seq = row.seq;
        rows.push_back(row);
    }
    return Ok(std::move(rows));
}

Result<std::vector<std::uint8_t>> ReadFrameImages(const std::filesystem::path &session_dir, const SessionInfo &info,
                                                  std::uint64_t seq) {
    if (info.storage == FrameStorage::PackedBin) {
        std::ifstream input(BinFramePath(session_dir, seq), std::ios::binary);
        if (!input) {
            return Err<std::vector<std::uint8_t>>(Named(kFrameMissing));
        }
        const std::size_t bytes = static_cast<std::size_t>(info.stride) * static_cast<std::size_t>(info.height) * 4U;
        std::vector<std::uint8_t> data(bytes);
        // NOLINTNEXTLINE(cppcoreguidelines-pro-type-reinterpret-cast) — the stream reads bytes.
        input.read(reinterpret_cast<char *>(data.data()), static_cast<std::streamsize>(bytes));
        if (input.gcount() != static_cast<std::streamsize>(bytes)) {
            return Err<std::vector<std::uint8_t>>(Named(kFrameReadBad));
        }
        return Ok(std::move(data));
    }

    std::vector<std::uint8_t> data;
    data.reserve(static_cast<std::size_t>(info.width) * static_cast<std::size_t>(info.height) * 4U);
    for (std::size_t image = 0; image < 4; ++image) {
        const Result<PgmImage> read = ReadPgm(PgmFramePath(session_dir, seq, image));
        if (!read.ok()) {
            return Err<std::vector<std::uint8_t>>(read.status());
        }
        if ((*read).width != info.width || (*read).height != info.height) {
            return Err<std::vector<std::uint8_t>>(Named(kCsvGeometryBad));
        }
        data.insert(data.end(), (*read).pixels.begin(), (*read).pixels.end());
    }
    return Ok(std::move(data));
}

Result<SessionInfo> ValidateSession(const std::filesystem::path &session_dir) {
    const Result<CgrecManifest> manifest = ReadManifest(session_dir);
    if (!manifest.ok()) {
        return Err<SessionInfo>(manifest.status());
    }
    if ((*manifest).schema != kCgrecSchema) {
        return Err<SessionInfo>(Named(kSchemaUnsupported));
    }
    if ((*manifest).width <= 0 || (*manifest).height <= 0 || (*manifest).stride < (*manifest).width) {
        return Err<SessionInfo>(Named(kGeometryInvalid));
    }

    const Result<std::vector<StereoCsvRow>> rows = ReadStereoCsv(session_dir);
    if (!rows.ok()) {
        return Err<SessionInfo>(rows.status());
    }
    if ((*rows).size() != (*manifest).frame_count) {
        return Err<SessionInfo>(Named(kCountMismatch));
    }
    for (const StereoCsvRow &row : *rows) {
        if (row.width != (*manifest).width || row.height != (*manifest).height) {
            return Err<SessionInfo>(Named(kCsvGeometryBad));
        }
    }

    const std::uintmax_t image_bytes =
        static_cast<std::uintmax_t>((*manifest).width) * static_cast<std::uintmax_t>((*manifest).height);
    const std::uintmax_t padded_bytes =
        static_cast<std::uintmax_t>((*manifest).stride) * static_cast<std::uintmax_t>((*manifest).height);
    for (const StereoCsvRow &row : *rows) {
        if ((*manifest).storage == FrameStorage::PackedBin) {
            const std::filesystem::path path = BinFramePath(session_dir, row.seq);
            std::error_code error;
            const std::uintmax_t size = std::filesystem::file_size(path, error);
            if (error) {
                return Err<SessionInfo>(Named(kFrameMissing));
            }
            if (size != padded_bytes * 4U) {
                return Err<SessionInfo>(Named(kFrameSizeBad));
            }
            continue;
        }
        for (std::size_t image = 0; image < 4; ++image) {
            const std::filesystem::path path = PgmFramePath(session_dir, row.seq, image);
            std::error_code error;
            const std::uintmax_t size = std::filesystem::file_size(path, error);
            if (error) {
                return Err<SessionInfo>(Named(kFrameMissing));
            }
            if (size != PgmHeaderBytes((*manifest).width, (*manifest).height) + image_bytes) {
                return Err<SessionInfo>(Named(kFrameSizeBad));
            }
        }
    }

    SessionInfo info;
    info.schema = (*manifest).schema;
    info.width = (*manifest).width;
    info.height = (*manifest).height;
    info.stride = (*manifest).stride;
    info.frame_count = (*manifest).frame_count;
    info.dropped = (*manifest).dropped;
    info.storage = (*manifest).storage;
    info.dof = (*manifest).dof;
    info.sdk_version = (*manifest).sdk_version;
    return Ok(std::move(info));
}

} // namespace cg::capture
