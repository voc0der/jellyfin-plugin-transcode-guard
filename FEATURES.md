# Transcode Guard Features

Every setting lives in one place: **Dashboard → Plugins → Transcode Guard**.

The two nag features are on out of the box and only send messages. Everything
that can interrupt or refuse playback is off until you turn it on, and stays
off across upgrades.

## Which one do you want?

| Your problem | Feature | Default |
| --- | --- | --- |
| Users don't know their client is forcing a transcode | [Playback nag](#playback-nag) | **On** |
| A few users transcode constantly and don't notice the nags | [Login nag](#login-nag) | **On** |
| Nagging isn't working and you want it to actually stop | [Transcode limit](#transcode-limit) | Off |
| Paused streams hold VRAM for hours | [Paused transcode reaper](#paused-transcode-reaper) | Off |
| Big jobs OOM the GPU and take the working ones down | [GPU resource guard](#gpu-resource-guard) | Off |
| You need to tell everyone something at login | [Message of the Day](#message-of-the-day) | Off |
| You want to see who's transcoding right now | [Live session monitor](#live-session-monitor) | On |

Cross-cutting behaviour — who gets skipped, which clients count, how messages
are delivered — is in [Shared settings](#shared-settings) at the end.

---

## Playback nag

Pops up a message when someone starts a stream that Jellyfin is transcoding
for a reason you care about.

The distinction that makes this plugin worth installing: Jellyfin transcodes
both because a client *can't play the file* and because a client *asked for
less bitrate*. Only the first is worth a message. Someone capping quality on
hotel Wi-Fi gets left alone.

**How it decides.** Jellyfin reports a set of `TranscodeReasons` for each
stream. You pick which ones are worth a nag under **Playback Trigger Reasons**;
if any selected reason is active, the nag fires. A transcode with no reasons at
all is bitrate-driven and never nags.

The defaults cover the compatibility failures — unsupported container, video
and audio codec, subtitle codec, profile, level, resolution, bit depth,
framerate, ref frames, anamorphic and interlaced video, audio channels and
sample rate, video range, and direct play errors.

| Setting | Does | Default |
| --- | --- | --- |
| Nag Message | What the popup says | Generic "use a client that direct plays" text |
| Delay Before Check (seconds) | How long to wait after playback starts before reading transcode info — Jellyfin doesn't populate it instantly | 5 |
| Message Timeout (ms) | How long the popup stays up | 10000 |
| Playback Trigger Reasons | Which reasons nag | The compatibility set above |

Each trigger reason can also carry **its own message**, so an unsupported
subtitle codec can say something different from an unsupported video codec.
Leave an override blank and the reason falls back to the main Nag Message.
When several selected reasons are active at once, the first override in the
reason list wins.

**Worth knowing.** The nag fires once per video, not once per session — a user
watching three bad files gets three messages, but seeking around one file gets
one. Every nagged playback is also recorded, which is what feeds the login nag
and the transcode limit.

---

## Login nag

Catches the repeat offender who dismisses every playback nag. When a user signs
in — or reopens Jellyfin after being idle for 10 minutes — this checks their
recent history and, past a threshold, tells them how bad it's got.

| Setting | Does | Default |
| --- | --- | --- |
| Login Nag Threshold | Bad transcodes needed to trigger | 5 |
| Login Nag Time Window | How far back to count: Week or Month | Week |
| Login Nag Message | Supports `{{transcodes}}` and `{{timewindow}}` | Generic text |

**Two things stop it becoming noise.**

It's rate-limited to once per time window, so a user on a weekly window hears
about it once a week regardless of how often they sign in.

And it forgives. If a user goes back to direct play after a bad transcode, they
earn an improvement credit and the nag goes quiet until they regress. Someone
who fixes their client stops hearing about it immediately, rather than serving
out the rest of the window.

History is kept for 30 days, so a Month window always has full data.

---

## Transcode limit

The login nag can count, but it can only ever ask nicely. This makes the same
count enforceable: past a second, higher threshold, the next bad transcode is
refused before FFmpeg starts.

**Set it above the login nag threshold.** Nag at 5, stop at 10 — the user gets
warned several times before anything is denied. There's deliberately no second
policy to configure: the count, the window, the trigger reasons, and every user
and client exclusion are the login nag's. One policy, two points on it.

| Setting | Does | Default |
| --- | --- | --- |
| Transcode Limit | Bad transcodes before refusing | 10 |
| Blocked Title / Blocked Message | The popup. Supports `{{transcodes}}`, `{{timewindow}}`, `{{limit}}` | "Transcode limit reached" |

**What it never refuses.** Only what the login nag counts. Direct play, direct
stream, bitrate-only transcodes, audio-only streams, excluded users, filtered
clients, and Live TV when it's excluded all pass through. A user over the limit
can still watch anything their client plays properly — the limit removes the
option to make the server do the work, not the ability to watch.

**It won't cut off what someone is already watching.** Jellyfin starts a fresh
FFmpeg job every time you seek, and the event recorded for the current
playback is often what pushes its own owner over the line. Without an
exemption, the film that hit the limit would be the one killed mid-scene. It
isn't: the limit applies to the next thing a user starts.

**A refusal costs nothing.** It returns HTTP 403, starts no FFmpeg process, and
records no event — so being refused can't push someone further over. Raise the
limit or wait for the window to roll and playback works again, with no restart.

**One caveat.** The count is held for a few seconds to keep a client's retry
storm off the history file, so a user sitting exactly on the limit may get one
or two more through, and an edited threshold applies within seconds rather than
instantly.

Setting the limit below 1 disables it rather than blocking everything.

---

## Paused transcode reaper

Jellyfin never evicts a paused transcode. A paused client keeps checking in, so
its FFmpeg process holds VRAM, an encoder session, and its output files for as
long as someone leaves the pause screen up — which can be overnight.

This stops one that's been paused too long.

| Setting | Does | Default |
| --- | --- | --- |
| Stop after paused for (minutes) | The deadline | 25 |
| Warn this many minutes first | Popup before the deadline; 0 sends none | 2 |
| Warning Title / Warning Message | Supports `{{minutes}}` | "Still there?" |
| Also stop paused direct play | Widens it to every paused session | Off |

**How it stops things.** The client is asked to stop first, which leaves behind
the resume point the last progress report saved. A client that ignores that has
its FFmpeg job ended server-side. Neither path signs the user out or removes
their device — they press play and carry on from where they were.

This one applies to *every* paused transcode, including Live TV and CPU
transcodes, because all of them hold resources. It has **its own exclusion
list**, separate from the nag exclusions.

**On direct play.** Off by default and deliberately so: direct play holds an
open file handle and a stream slot rather than VRAM and an encoder session, so
it's cheaper to leave alone. It's also stop-only — there's no FFmpeg process to
end if the client ignores the request.

---

## GPU resource guard

Refuses an NVIDIA hardware transcode that isn't going to fit, before FFmpeg
launches. The alternative is worse than a refusal: when CUDA can't allocate,
Jellyfin retries, and you get a storm of doomed FFmpeg launches instead of one
clean failure.

**Not a fixed threshold.** It reads free VRAM immediately before launch and
budgets each job from its actual shape — source resolution and bit depth, CUDA
filters like tonemapping, output resolution, ref frames. A 1080p SDR job gets a
small budget and can use a gap that a "keep 2GB free" rule would waste, while a
4K HDR tonemap job is kept out when it genuinely won't fit.

| Setting | Does | Default |
| --- | --- | --- |
| GPU index | Fallback device. An explicit GPU chosen by FFmpeg wins over this | 0 |
| GPU check timeout (ms) | How long to wait for `nvidia-smi` before giving up and allowing | 1000 |
| nvidia-smi path | Leave blank to resolve from `PATH` | blank |
| Refusal Title / Refusal Message | The popup — keep server detail out of it | "Transcoding unavailable" |

**Scope.** NVIDIA video transcodes only. Direct play, direct stream, remux,
audio-only, and CPU transcodes are never refused. QSV, VAAPI, and VideoToolbox
are never refused.

**It fails open.** If free VRAM can't be read — `nvidia-smi` missing, too slow,
not permitted — playback is allowed. A guard that can't see the GPU must not
become an outage. `nvidia-smi` has to be runnable by the Jellyfin server
process, which in Docker means the NVIDIA runtime is wired up.

**Calibration.** After launch it samples per-process memory for the FFmpeg PID
and logs the real figure next to the job shape and the budget it predicted,
which is how the estimates get tuned. If container PID namespaces block
attribution, admission still works — the temporary reservation just expires on
its timer.

A refused stream returns HTTP 403 and starts no process. Free some VRAM and the
next attempt works, with no setting change or restart.

---

## Message of the Day

An announcement sent once per session at login. Maintenance windows, new
library, whatever.

Entirely unrelated to transcode history. It has its own message, its own
exclusion list, and its own client filters, none of which are shared with the
nags — so you can announce to everyone while nagging only browser users, or the
reverse.

**Sessions already signed in when you enable it get nothing** until they sign in
again. If a user is due both the MOTD and a login nag, the nag waits for the
MOTD to time out first, so clients that only show one message at a time still
display both.

---

## Live session monitor

On the settings page, under the configuration. Shows the sessions currently
matching your rules, so you can confirm a filter does what you expected without
tailing the server log. Refreshes on a timer, or on demand.

---

## Shared settings

### User exclusions

Three separate lists, deliberately not shared:

| List | Covers |
| --- | --- |
| **Manage Excluded Users** | Playback nags, login nags, and the transcode limit |
| **Manage Excluded Users (MOTD)** | The MOTD only |
| **Manage Excluded Users (Paused Transcodes)** | The reaper only |

Excluding someone from nags also excludes them from the transcode limit — the
limit enforces the nag's count, so it inherits the nag's exemptions.

### Client filters

Case-insensitive substring matching against the client name, one per line or
comma-separated. `browser` matches "Jellyfin Web (browser)".

- Empty include list means every client is eligible.
- A non-empty include list means only matching clients are.
- **Exclude always wins** over include.

A filtered client is skipped for nags *and* its history doesn't count toward
the login nag or the transcode limit. The MOTD keeps its own separate pair.

### Exclude Live TV

Off by default. When on, Live TV channel streams don't trigger playback nags,
don't count toward the login nag, and aren't refused by the transcode limit.

It does **not** apply to the paused transcode reaper — a paused Live TV stream
holds the same resources as any other and is still reaped.

### Sticky messages

Some clients dismiss Jellyfin popups almost instantly. Sticky mode sends the
message three times, 3 seconds apart, with a 4-second timeout per send.

It's a separate toggle on each feature, so you can make refusals sticky while
leaving routine nags alone. Non-sticky messages use the configured Message
Timeout.

### Message placeholders

| Placeholder | Available in | Is |
| --- | --- | --- |
| `{{transcodes}}` | Login nag, Blocked message | Their count in the window |
| `{{timewindow}}` | Login nag, Blocked message | "week" or "month" |
| `{{limit}}` | Blocked message | The configured limit |
| `{{minutes}}` | Paused transcode warning | Minutes until the stream is stopped |

### Logging

**Enable Logging** is on by default and adds per-decision detail — which
sessions were skipped and why, which messages were delivered. Refusals and
guard warnings are logged regardless. Turn it on before reporting a bug.
