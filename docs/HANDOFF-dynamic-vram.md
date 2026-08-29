# Dynamic VRAM Guard — Implementation Handoff

Updated: 2026-08-28

## Status

The fixed `MinimumFreeGpuMemoryMiB` setting has been replaced in the current worktree by automatic,
per-job admission. The guard reads current free VRAM immediately before launch and asks whether the
pending FFmpeg job plus short-lived in-flight reservations fit.

PR #28's Jellyfin-specific `MediaBrowser.Controller.Net.SecurityException` change is already present
locally. A refusal is HTTP 403 and does not launch FFmpeg.

## Important precision boundary

`nvidia-smi` provides the current free-memory reading and launched-process usage in whole MiB. Those
measurements are exact to the precision exposed by the driver.

A future FFmpeg allocation cannot be measured before that process exists. Pre-launch admission must
therefore use a conservative requirement derived from the completed command and stream shape. Do not
describe that requirement as an exact future measurement. The implementation separately samples the
launched FFmpeg PID so the model can be compared with real MiB usage and refined from evidence.

## Calibration supplied for the RTX 4000 Ada

| Job | Total-used delta | FFmpeg process row | Current requirement |
| --- | ---: | ---: | ---: |
| 1080p show forced to 720p | 329 MiB | 323 MiB | 512 MiB |
| GoT S01E01 4K HEVC Main10 HDR, no CUDA tone map | 829 MiB | 824 MiB | 1024 MiB |
| Gladiator 4K filter-heavy transcode | 1345 MiB in the supplied snapshot | 1339 MiB | 1536 MiB |

The Gladiator workload was also reported around 1496 MiB and the smaller workload around 390 MiB at
other points. One snapshot is not a peak measurement, which is why the admission requirements retain
headroom. The post-launch sampler takes three process-specific readings and logs the maximum alongside
the source/output shape and requirement.

## Admission model

- No budget and no GPU query for Direct Play, audio-only, stream copy/remux, CPU transcodes, or
  non-NVIDIA hardware paths.
- 1080p and below: 512 MiB.
- 1440p: 768 MiB.
- 4K 8-bit: 768 MiB.
- 4K 10-bit: 1024 MiB.
- CUDA tone mapping: +256 MiB through 1440p, +512 MiB at 4K.
- AV1 output, high reference-frame pressure, frame rates above 60 fps, and additional CUDA filters
  add conservative surface allowances.
- Requirements above 4K scale with pixel count; they are no longer capped at 4096 MiB.
- Missing metadata has a 1536 MiB floor, while any known larger dimensions still scale above it.
- Pixel formats and FFmpeg output options are used to recover bit depth when Jellyfin's target fields
  are null during a normal non-static transcode.

The policy comparison is inclusive: `freeMiB >= jobRequirementMiB + inFlightMiB`.

## Admin override

`GpuVramBudgetPercent` (default 100, clamped to 10-500) scales the model's requirement before it is
compared and before it is reserved. It exists because the model deliberately returns the worst
plausible peak for a shape: on a card already mostly occupied by something else, a 4K HDR tone-mapped
job budgeted at 1536 MiB is refused against a 1490 MiB reading even though the same shape has been
measured at 1339 MiB. Without a dial the only recourse was switching the guard off entirely.

Scaling applies to the decision, the in-flight reservation, and the refusal reason, so a tuned
deployment cannot reserve one figure and be judged against another. The `GPU VRAM calibration` line
keeps reporting the unscaled model budget next to the observed maximum: that is the calibration
record, and it must stay comparable across deployments with different percentages.

## Concurrent starts

The free-memory query, decision, and reservation are serialized. This prevents two starts from both
spending the same pre-allocation reading.

- Reservation identity uses Jellyfin's server-derived output path and GPU index.
- `PlaySessionId` is not trusted as job identity; it is client supplied and can collide.
- A failed free-memory query remains fail-open, but that admitted job still receives a temporary
  reservation so the next successful query cannot spend its not-yet-visible allocation.
- A successful process-specific query for the exact FFmpeg PID releases that reservation once a
  positive allocation is visible. Unrelated Gemma growth cannot release it.
- If PID attribution is unavailable (including container PID namespace mismatches), the reservation
  expires after three seconds.
- An exception from Jellyfin's inner `StartFfMpeg` conservatively keeps the three-second reservation,
  because Jellyfin can throw after starting the process while waiting for output.

## Multi-GPU behavior

The detector reads numeric GPU selectors from `-init_hw_device`, `-hwaccel_device`, and `-gpu`.
An explicit FFmpeg selection overrides the configured fallback GPU index. Contradictory selectors are
treated as unknown telemetry and fail open; their temporary reservation counts against every GPU.

## Invariants retained

- GPU telemetry failure allows playback.
- Successful free-memory readings are never cached.
- Caller cancellation is never cached as a GPU failure.
- A notification or logger failure cannot reverse a decided denial.
- A guard/metadata failure before a decision cannot break playback.
- Client refusal messages contain no VRAM, encoder, PID, or server-path detail.
- One playback retry burst produces one popup; a deliberate reopen after a quiet gap is announced.

## Relevant files

```text
Gpu/GuardedTranscodeManager.cs       Veto point and FFmpeg PID handoff
Gpu/GpuResourceGuard.cs              Query, decision, reservations, messages, calibration log
Gpu/GpuAdmissionPolicy.cs            Pure comparison rules
Gpu/GpuVramEstimator.cs              Pure conservative requirement model
Gpu/NvidiaTranscodeDetector.cs       Token/graph parsing and selected-GPU detection
Gpu/NvidiaSmiGpuMemoryProvider.cs    Free and per-process nvidia-smi queries
Gpu/NvidiaSmiOutputParser.cs         Whole-MiB CSV parsing
Configuration/configPage.html        Guard settings (budget percentage, no fixed free-memory knob)
tests/Jellyfin.Plugin.TranscodeNag.Tests/
```

## Calibration follow-up

Collect `GPU VRAM calibration for FFmpeg PID ...` log lines across repeated examples of each shape.
Only narrow a requirement after repeated peak observations on the target driver/GPU/FFmpeg stack.
Do not lower a requirement from a single startup snapshot: an underestimate recreates CUDA OOM exit
187 and Jellyfin's retry storm, while a modest overestimate only refuses a marginal start cleanly.
