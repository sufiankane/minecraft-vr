#pragma once

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <memory>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "cg/core_math/quat.hpp"
#include "cg/core_math/vec3.hpp"
#include "cg/glasses/fake_head_pose_source.hpp"
#include "cg/glasses/manual_clock.hpp"
#include "cg/glasses/viture_api.hpp"
#include "ports.hpp"

namespace cg::glasses::test {

/// Consumes the front of `script`, or returns `fallback` when it is empty.
template <typename T> [[nodiscard]] Result<T> NextResult(std::deque<Result<T>> &script, const Result<T> &fallback) {
    if (script.empty()) {
        return fallback;
    }
    Result<T> result = std::move(script.front());
    script.pop_front();
    return result;
}

/// `NextResult` without a fallback; the precondition is that `script` is not
/// empty (used by the fake after checking `poll_script`).
template <typename T> [[nodiscard]] Result<T> PopResult(std::deque<Result<T>> &script) {
    Result<T> result = std::move(script.front());
    script.pop_front();
    return result;
}

/// Programmable `IVitureApi` double for `VitureHeadPoseSource` tests.
///
/// Every call returns a scripted `Result`: the matching script deque is
/// popped first, and the configured default is returned once the script runs
/// out. Call counters and the last display/recentre arguments are exposed for
/// assertions. `poll_delay_ns` makes `PollPose` block like the SDK does; the
/// wait is cut short as soon as `RequestStop()` is called (or `stop_requested`
/// is set directly), and the interrupted poll returns `Timeout` promptly.
///
/// The sample feed is either a plain list of `cg_head_sample`, returned
/// verbatim one per successful poll, or a Task 1 `FakeScript` pattern advanced
/// on demand over an owned `ManualClock`. Poll scripts take precedence over
/// both.
class FakeVitureApi final : public IVitureApi {
  public:
    FakeVitureApi() = default;
    ~FakeVitureApi() override = default;

    FakeVitureApi(const FakeVitureApi &) = delete;
    FakeVitureApi &operator=(const FakeVitureApi &) = delete;

    // --- scripted results and their scripts -------------------------------
    Result<void> create_result = Ok();
    Result<void> start_result = Ok();
    Result<void> reset_origin_result = Ok();
    Result<void> set_display_mode_result = Ok();
    Result<std::uint32_t> refresh_result = Ok(std::uint32_t{90});
    Result<cg_head_sample> empty_poll_result = Err<cg_head_sample>(Status{StatusCode::Timeout, "fake: no sample"});
    std::string sdk_version = "fake-viture-0.0.0";

    std::deque<Result<void>> create_script;
    std::deque<Result<void>> start_script;
    std::deque<Result<void>> reset_origin_script;
    std::deque<Result<void>> set_display_mode_script;
    std::deque<Result<std::uint32_t>> refresh_script;
    std::deque<Result<cg_head_sample>> poll_script;

    // --- sample feed ------------------------------------------------------
    std::vector<cg_head_sample> samples;
    std::size_t next_sample = 0;
    std::int64_t poll_delay_ns = 0;

    // --- observability ----------------------------------------------------
    std::atomic<std::uint64_t> create_calls{0};
    std::atomic<std::uint64_t> destroy_calls{0};
    std::atomic<std::uint64_t> start_calls{0};
    std::atomic<std::uint64_t> poll_calls{0};
    std::atomic<std::uint64_t> reset_origin_calls{0};
    std::atomic<std::uint64_t> set_display_mode_calls{0};
    std::atomic<std::uint64_t> get_refresh_hz_calls{0};
    mutable std::atomic<std::uint64_t> sdk_version_calls{0};

    std::uint32_t last_refresh_hz = 0;
    bool last_sbs = false;
    float last_reset_pose[7] = {};
    bool has_reset_pose = false;

    // --- IVitureApi -------------------------------------------------------
    Result<void> CreateDevice() override {
        create_calls.fetch_add(1, std::memory_order_relaxed);
        return NextResult(create_script, create_result);
    }

    void DestroyDevice() noexcept override { destroy_calls.fetch_add(1, std::memory_order_relaxed); }

    Result<void> StartPose() override {
        start_calls.fetch_add(1, std::memory_order_relaxed);
        stop_requested_.store(false, std::memory_order_relaxed);
        return NextResult(start_script, start_result);
    }

    Result<cg_head_sample> PollPose() override {
        poll_calls.fetch_add(1, std::memory_order_relaxed);
        if (IsStopped()) {
            return Interrupted();
        }
        WaitPollDelay();
        if (IsStopped()) {
            return Interrupted();
        }
        if (!poll_script.empty()) {
            return PopResult(poll_script);
        }
        if (use_script_) {
            return NextScriptSample();
        }
        if (next_sample < samples.size()) {
            return Ok(samples[next_sample++]);
        }
        return empty_poll_result;
    }

    Result<void> ResetOriginCarina(const float pose[7]) override {
        reset_origin_calls.fetch_add(1, std::memory_order_relaxed);
        if (pose == nullptr) {
            return Err<void>(Status{StatusCode::InvalidArgument, "fake: null recentre pose"});
        }
        for (std::size_t i = 0; i < 7; ++i) {
            last_reset_pose[i] = pose[i];
        }
        has_reset_pose = true;
        return NextResult(reset_origin_script, reset_origin_result);
    }

    Result<void> SetDisplayMode(std::uint32_t refresh_hz, bool sbs) override {
        set_display_mode_calls.fetch_add(1, std::memory_order_relaxed);
        last_refresh_hz = refresh_hz;
        last_sbs = sbs;
        return NextResult(set_display_mode_script, set_display_mode_result);
    }

    Result<std::uint32_t> GetRefreshHz() override {
        get_refresh_hz_calls.fetch_add(1, std::memory_order_relaxed);
        return NextResult(refresh_script, refresh_result);
    }

    std::string SdkVersion() const override {
        sdk_version_calls.fetch_add(1, std::memory_order_relaxed);
        return sdk_version;
    }

    void RequestStop() noexcept override { stop_requested_.store(true, std::memory_order_relaxed); }

    // --- test controls ----------------------------------------------------

    /// True once `RequestStop()` was called and before the next `StartPose()`.
    [[nodiscard]] bool stop_requested() const noexcept { return IsStopped(); }

    /// Replaces the feed with samples generated by a Task 1 `FakeScript` at
    /// `rate_hz`, advanced one sample per successful poll. Returns the
    /// source's `Start` status, so an invalid rate is visible to the test.
    Result<void> FeedScript(FakeScript script, double rate_hz = 100.0) {
        script_clock_ = std::make_unique<ManualClock>();
        script_source_ =
            std::make_unique<FakeHeadPoseSource>(FakeHeadPoseSourceConfig{rate_hz, script_clock_.get(), script});
        const Result<void> started = script_source_->Start();
        if (!started.ok()) {
            script_source_.reset();
            script_clock_.reset();
            return started;
        }
        use_script_ = true;
        next_sample = 0;
        samples.clear();
        return Ok();
    }

    /// The clock behind a `FeedScript` feed; null for a plain sample list.
    [[nodiscard]] ManualClock *script_clock() noexcept { return script_clock_.get(); }

  private:
    [[nodiscard]] bool IsStopped() const noexcept { return stop_requested_.load(std::memory_order_relaxed); }

    [[nodiscard]] Result<cg_head_sample> Interrupted() const {
        return Err<cg_head_sample>(Status{StatusCode::Timeout, "fake: poll interrupted by RequestStop"});
    }

    void WaitPollDelay() noexcept {
        std::int64_t remaining = poll_delay_ns;
        constexpr std::int64_t kSliceNs = 1'000'000;
        while (remaining > 0 && !IsStopped()) {
            const std::int64_t slice = std::min(remaining, kSliceNs);
            std::this_thread::sleep_for(std::chrono::nanoseconds(slice));
            remaining -= slice;
        }
    }

    [[nodiscard]] Result<cg_head_sample> NextScriptSample() {
        script_source_->AdvanceSamples(1);
        HeadSample sample{0, core_math::Pose{core_math::Vec3{0.0, 0.0, 0.0}, core_math::Quat::kIdentity},
                          TrackState::Stable, 0};
        if (!script_source_->TryGetLatest(sample, Duration{0})) {
            return Err<cg_head_sample>(Status{StatusCode::NotReady, "fake: script produced no sample"});
        }
        return Ok(ToCgSample(sample));
    }

    [[nodiscard]] static cg_head_sample ToCgSample(const HeadSample &sample) noexcept {
        static_assert(static_cast<int>(TrackState::Stable) == CG_TRACK_STABLE, "TrackState must match cg_track_state");
        static_assert(static_cast<int>(TrackState::Unstable) == CG_TRACK_UNSTABLE,
                      "TrackState must match cg_track_state");
        static_assert(static_cast<int>(TrackState::Lost) == CG_TRACK_LOST, "TrackState must match cg_track_state");
        cg_head_sample out{};
        out.host_time = sample.time;
        out.pose.p.x = static_cast<float>(sample.pose.position.x);
        out.pose.p.y = static_cast<float>(sample.pose.position.y);
        out.pose.p.z = static_cast<float>(sample.pose.position.z);
        out.pose.q.w = static_cast<float>(sample.pose.rotation.w());
        out.pose.q.x = static_cast<float>(sample.pose.rotation.x());
        out.pose.q.y = static_cast<float>(sample.pose.rotation.y());
        out.pose.q.z = static_cast<float>(sample.pose.rotation.z());
        out.state = static_cast<cg_track_state>(sample.state);
        out.sequence = sample.seq;
        return out;
    }

    std::unique_ptr<ManualClock> script_clock_;
    std::unique_ptr<FakeHeadPoseSource> script_source_;
    bool use_script_ = false;
    std::atomic<bool> stop_requested_{false};
};

} // namespace cg::glasses::test
