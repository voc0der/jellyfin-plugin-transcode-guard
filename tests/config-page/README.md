# Settings page harness

Drives the Transcode Guard settings page inside a real Jellyfin and checks that every tab, button
and Save still works.

```bash
cd tests/config-page
npm ci
npx playwright install chromium
npx playwright test
```

Needs `docker`, `node` and the .NET SDK. Each run builds the plugin, boots a throwaway
`jellyfin/jellyfin` container with that build installed, and removes the container afterwards.
The whole suite takes about a minute.

- `JELLYFIN_IMAGE=jellyfin/jellyfin:12.1 npx playwright test` checks another Jellyfin release.
- `HARNESS_KEEP=1 npx playwright test` leaves the server running so you can look around in it.
- A failed run leaves a trace per failing test, a screenshot, and the server's log in
  `test-results/`. `npx playwright show-trace <trace.zip>` replays it step by step.

## What it checks

- **Save reaches the server from every tab.** Each test that saves waits for the configuration
  POST and fails with "Save sent nothing to the server" when there isn't one.
- **Every setting survives the round trip.** The server is seeded with one complete configuration,
  the page has to show all of it, then a second one is typed in through the page and has to be
  exactly what the server stores, and what the page shows after a reload.
- **Nothing blocks Save silently.** A value the page rejects is shown on its own tab with the
  browser's message, and a value left behind a switched-off feature does not block anything.
- **Every button does what it says**: the tabs (mouse and keyboard), each feature's on/off switch,
  the trigger reason buttons and per-reason overrides, the three user exclusion dialogs, the
  per-user maximums, and the live session monitor, which only polls while its tab is open.
- **Leaving the page and coming back** leaves every handler working exactly once.
- **Nothing on the page throws or logs an error** while any of that happens.

## Why a real Jellyfin

The plugin is installed for real, the page is reached through Jellyfin's own router, and saves go
to the server's real configuration endpoint. The failures this exists to catch live in those
seams: how Jellyfin runs the page's script, what jellyfin-web's `emby-*` elements do with a click,
and whether a Save arrived at all. A stubbed page would pass where the real one fails.

The README screenshots are taken differently, with the page grafted onto a stubbed dashboard. That
harness lives in [voc0der/jellyfin-plugins](https://github.com/voc0der/jellyfin-plugins).

## Adding a setting

Add its field to `FIELDS` in `harness/fixtures.mjs`, and give it a value in both `configA` and
`configB` that differ from each other. Until you do, the test named "every setting the server
stores is exercised by this suite" fails and names it.
