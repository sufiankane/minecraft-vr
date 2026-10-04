#pragma once

#include <optional>
#include <type_traits>
#include <utility>

#include "cg_types.h"

namespace cg {

/// The C++ status vocabulary behind `Result`, one word per `cg_status` value
/// (additive contract vocabulary; ADR-0009). `Ok` is the zero value.
enum class StatusCode { Ok, InvalidArgument, NotReady, Device, Timeout, Unsupported, Internal };

/// A status word plus a non-owning, human-readable message.
///
/// `message` is a pointer whose lifetime the caller controls; the default is
/// the empty string. A default-constructed `Status` is `Ok`. Copying a
/// `Status` copies the pointer, not the message text. Every operation is
/// `noexcept` and allocates nothing.
class Status {
  public:
    constexpr Status() noexcept = default;

    constexpr Status(StatusCode code, const char *message = "") noexcept : code_(code), message_(message) {}

    /// The status word. No allocation.
    [[nodiscard]] constexpr StatusCode code() const noexcept { return code_; }

    /// The message pointer passed at construction; never null. No allocation.
    [[nodiscard]] constexpr const char *message() const noexcept { return message_ == nullptr ? "" : message_; }

    /// True exactly when `code() == StatusCode::Ok`. No allocation.
    [[nodiscard]] constexpr bool IsOk() const noexcept { return code_ == StatusCode::Ok; }

  private:
    StatusCode code_ = StatusCode::Ok;
    const char *message_ = "";
};

/// Maps the C ABI status word to `Status`, one to one (ADR-0009):
/// `CG_OK` -> `Ok`, `CG_ERR_INVALID_ARG` -> `InvalidArgument`,
/// `CG_ERR_NOT_READY` -> `NotReady`, `CG_ERR_DEVICE` -> `Device`,
/// `CG_ERR_TIMEOUT` -> `Timeout`, `CG_ERR_UNSUPPORTED` -> `Unsupported`,
/// `CG_ERR_INTERNAL` -> `Internal`. An out-of-range value maps to `Internal`.
/// No allocation.
[[nodiscard]] constexpr Status FromCgStatus(cg_status status) noexcept {
    switch (status) {
    case CG_OK:
        return Status{StatusCode::Ok};
    case CG_ERR_INVALID_ARG:
        return Status{StatusCode::InvalidArgument};
    case CG_ERR_NOT_READY:
        return Status{StatusCode::NotReady};
    case CG_ERR_DEVICE:
        return Status{StatusCode::Device};
    case CG_ERR_TIMEOUT:
        return Status{StatusCode::Timeout};
    case CG_ERR_UNSUPPORTED:
        return Status{StatusCode::Unsupported};
    case CG_ERR_INTERNAL:
        return Status{StatusCode::Internal};
    }
    return Status{StatusCode::Internal};
}

/// A value or a `Status`, without exceptions.
///
/// An ok `Result<T>` holds a moved `T`; an error `Result` holds the `Status`
/// and no `T`. `operator*` is a precondition-checked accessor: calling it on an
/// error result is undefined behaviour, exactly like `std::optional`.
/// `operator*` is `noexcept`; for the contract value types (`HeadSample` and
/// friends) construction, copies and moves allocate nothing.
template <typename T> class Result {
  public:
    Result(T value) noexcept(std::is_nothrow_move_constructible_v<T>) : value_(std::move(value)) {}

    Result(Status status) noexcept : status_(status) {}

    /// True when a value is held. No allocation.
    [[nodiscard]] bool ok() const noexcept { return value_.has_value(); }

    /// The status; `Ok` for a value result, the carried error otherwise. No allocation.
    [[nodiscard]] const Status &status() const noexcept { return status_; }

    /// The value. Precondition: `ok()`. No allocation.
    [[nodiscard]] const T &operator*() const noexcept { return *value_; }

    /// The value. Precondition: `ok()`. No allocation.
    [[nodiscard]] T &operator*() noexcept { return *value_; }

  private:
    std::optional<T> value_;
    Status status_{};
};

/// A status-only result.
template <> class Result<void> {
  public:
    Result() noexcept = default;

    Result(Status status) noexcept : status_(status) {}

    /// True when the status is `Ok`. No allocation.
    [[nodiscard]] bool ok() const noexcept { return status_.IsOk(); }

    /// The carried status. No allocation.
    [[nodiscard]] const Status &status() const noexcept { return status_; }

  private:
    Status status_{};
};

/// Builds an ok `Result` from `value`.
template <typename T> [[nodiscard]] Result<std::decay_t<T>> Ok(T &&value) {
    return Result<std::decay_t<T>>(std::forward<T>(value));
}

/// Builds an ok `Result<void>`.
[[nodiscard]] inline Result<void> Ok() noexcept { return Result<void>{}; }

/// Builds an error `Result<T>` from `status`.
template <typename T> [[nodiscard]] Result<T> Err(Status status) { return Result<T>(std::move(status)); }

} // namespace cg
