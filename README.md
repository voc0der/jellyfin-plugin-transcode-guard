<p align="center">
  <img src="icon.png" alt="jellyfin-plugin-transcode-guard icon" width="180" />
</p>

# Jellyfin Transcode Guard Plugin

<p align="center">
  <a href="https://github.com/voc0der/jellyfin-plugin-transcode-guard/releases/latest">
    <img src="https://img.shields.io/github/v/release/voc0der/jellyfin-plugin-transcode-guard?label=stable%20release" alt="Stable release version" />
  </a>
  <a href="https://github.com/voc0der/jellyfin-plugin-transcode-guard/tree/main/tests">
    <img src="https://img.shields.io/badge/coverage-71%25-yellowgreen" alt="Code coverage percentage" />
  </a>
  <a href="https://github.com/voc0der/jellyfin-plugin-transcode-guard/issues">
    <img src="https://img.shields.io/github/issues/voc0der/jellyfin-plugin-transcode-guard?color=DAA520" alt="Open issues" />
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/voc0der/jellyfin-plugin-transcode-guard?color=97CA00" alt="License" />
  </a>
  <a href="https://github.com/voc0der/jellyfin-plugin-transcode-guard/blob/main/Jellyfin.Plugin.TranscodeGuard.csproj">
    <img src="https://img.shields.io/badge/dependencies-2%20outdated-orange" alt="Dependencies status" />
  </a>
</p>

A Jellyfin plugin that intelligently nags users when they're transcoding due to **unsupported formats or codecs**, while allowing bitrate-based transcoding to pass through without harassment.

<p align="center">
  <img src="docs/images/transcode-guard-playback-settings.png" alt="Transcode Guard playback settings and trigger reasons in the Jellyfin dashboard" width="880" />
</p>
<p align="center">
  <em>Playback nag configuration and trigger reason selection</em>
</p>

<p align="center">
  <img src="docs/images/transcode-guard-login-monitor.png" alt="Transcode Guard login nag settings in the Jellyfin dashboard" width="880" />
</p>
<p align="center">
  <em>Login nag threshold, time window, and message</em>
</p>

<p align="center">
  <img src="docs/images/transcode-guard-transcode-limit.png" alt="Transcode Guard transcode limit settings, user exclusions, and live session monitor in the Jellyfin dashboard" width="880" />
</p>
<p align="center">
  <em>The transcode limit, user exclusions, and the live session monitor</em>
</p>

<p align="center">
  <img src="docs/images/transcode-guard-gpu-guard.png" alt="Transcode Guard GPU resource guard settings in the Jellyfin dashboard" width="880" />
</p>
<p align="center">
  <em>GPU resource guard settings</em>
</p>

<p align="center">
  <img src="docs/images/transcode-guard-paused-reaper.png" alt="Transcode Guard paused transcode reaper settings in the Jellyfin dashboard" width="880" />
</p>
<p align="center">
  <em>The paused transcode reaper, its warning, and its own exclusion list</em>
</p>

## What It Does

- Sends a playback nag when Jellyfin reports selected `TranscodeReasons`.
- Ignores bitrate-only transcodes, so users lowering quality for bandwidth do not get warned.
- Can exclude Live TV channel streams from playback nags and login nag history.
- Can send a login nag when a user keeps hitting bad transcodes over the last week or month.
- Can refuse a user's next transcode outright once that same count passes a second, higher limit, so heavy transcoders are warned before they are stopped.
- Lets you exclude users from all nags.
- Includes a live session monitor in the plugin settings page.
- Can broadcast an optional Message of the Day to users at login, with its own user exclusions and client filters.
- Can refuse an NVIDIA hardware transcode before FFmpeg starts when that job's conservative VRAM budget will not fit.
- Can stop a transcode that has been left paused past a configurable timeout, so its FFmpeg process stops holding VRAM, an encoder session, and its output files for a viewer who is not coming back.

See here for all [FEATURES.md](FEATURES.md).

## Installation

> [!NOTE]
> Migrating from Transcode Nag: run both until the config matches, then uninstall the old one.

### Plugin Repository

1. Go to **Dashboard** → **Plugins** → **Repositories**
2. Add `https://raw.githubusercontent.com/voc0der/jellyfin-plugin-transcode-guard/main/manifest.json`
3. Install **Transcode Guard** from **Catalog**
4. Restart Jellyfin

> [!NOTE]
> Full repository of this author's plugins: [voc0der/jellyfin-plugins](https://github.com/voc0der/jellyfin-plugins).

### Manual

1. Download the latest ZIP from the [Releases page](https://github.com/voc0der/jellyfin-plugin-transcode-guard/releases/latest)
2. Extract it into your Jellyfin plugins directory:
   - Linux: `/var/lib/jellyfin/plugins/`
   - Windows: `%AppData%\Jellyfin\Server\plugins\`
   - Docker: `/config/plugins/`
3. Restart Jellyfin

#### Build from Source

```bash
dotnet build --configuration Release
```

Copy `bin/Release/net8.0/Jellyfin.Plugin.TranscodeGuard.dll` into a versioned plugin folder, then restart Jellyfin.

## Configuration

Open **Dashboard** → **Plugins** → **Transcode Guard**.
