import { expect } from '@playwright/test';
import { JellyfinApi, PLUGIN_ID } from './jellyfin.mjs';

const PAGE_ROUTE = '#/configurationpage?name=Transcode%20Guard';
const PLUGIN_ID_COMPACT = PLUGIN_ID.replace(/-/g, '');

// How long a Save gets to reach the server before the test calls it dead. A working Save posts
// within a few hundred milliseconds; one the browser swallowed never posts at all.
const SAVE_TIMEOUT_MS = 5000;

export const TABS = ['playback', 'browser', 'login', 'limit', 'simultaneous', 'gpu', 'reaper', 'motd', 'sessions'];

export function harness() {
    const state = JSON.parse(process.env.TRANSCODE_GUARD_HARNESS ?? 'null');
    if (!state) {
        throw new Error('TRANSCODE_GUARD_HARNESS is not set; run the suite through playwright test so global setup boots the server.');
    }

    return { ...state, api: new JellyfinApi(state.base, state.token) };
}

// Jellyfin keeps recently shown pages in the DOM, hidden, so a lookup by id could otherwise
// land on a stale copy of this page instead of the one on screen.
export function settings(page) {
    return page.locator('#TranscodeGuardConfigPage:not(.hide)');
}

export function field(page, id) {
    return settings(page).locator(`#${id}`);
}

export function saveButton(page) {
    return settings(page).locator('#TranscodeGuardConfigForm button[type="submit"]');
}

export function isConfigurationSave(request) {
    if (request.method() !== 'POST') {
        return false;
    }

    const path = new URL(request.url()).pathname.replace(/-/g, '').toLowerCase();
    return path.endsWith(`/plugins/${PLUGIN_ID_COMPACT}/configuration`);
}

// Opens the settings page through Jellyfin's own router, the way an admin reaches it, and waits
// for everything the page fills in asynchronously: the saved values, the trigger reason list and
// the per-user maximum list (one row per user).
export async function openSettings(page, config, { userCount = 3 } = {}) {
    const { base } = harness();
    await page.goto(`${base}/web/${PAGE_ROUTE}`);
    await waitUntilLoaded(page, config, { userCount });
}

// A page that throws while it loads will never finish filling in, so the first error it throws
// fails the wait on the spot rather than every test sitting out its timeout.
export async function waitUntilLoaded(page, config, { userCount = 3 } = {}) {
    let onError;
    const threw = new Promise((_, reject) => {
        onError = error => reject(new Error(`the page threw while loading: ${error.message}`));
        page.on('pageerror', onError);
    });

    const loaded = (async () => {
        await expect(settings(page)).toBeVisible({ timeout: 30_000 });
        await expect(field(page, 'txtNagMessage')).toHaveValue(config.NagMessage, { timeout: 30_000 });
        await expect(settings(page).locator('#transcodeReasonList input[data-reason-name]')).toHaveCount(18);
        await expect(settings(page).locator('#concurrentTranscodeUserLimitList input[data-user-id]')).toHaveCount(userCount);
    })();

    try {
        await Promise.race([loaded, threw]);
    } finally {
        page.off('pageerror', onError);
        // Whichever lost the race settles later with nobody listening; that is not a failure.
        loaded.catch(() => {});
        threw.catch(() => {});
    }
}

export function tabButton(page, tab) {
    return settings(page).locator(`[role="tab"][data-tab="${tab}"]`);
}

export function tabPanel(page, tab) {
    return settings(page).locator(`[role="tabpanel"][data-tab="${tab}"]`);
}

export async function expectTabSelected(page, tab) {
    await expect(tabButton(page, tab)).toHaveAttribute('aria-selected', 'true');
}

export async function showTab(page, tab) {
    const button = tabButton(page, tab);
    await button.click();
    await expect(button).toHaveAttribute('aria-selected', 'true');
    await expect(tabPanel(page, tab)).toBeVisible();
}

// emby-checkbox hides the real input behind its own drawn box, so it is driven the way a person
// drives it: by clicking its label.
export async function setChecked(input, checked) {
    if ((await input.isChecked()) !== checked) {
        await input.locator('xpath=ancestor::label[1]').click();
    }

    await expect(input).toBeChecked({ checked });
}

function displayedValue(fieldSpec, value) {
    switch (fieldSpec.kind) {
        case 'patterns':
            return value.join('\n');
        case 'number':
            // A maximum of 0 means "none" and is shown as an empty field.
            return fieldSpec.prop === 'MaxConcurrentTranscodes' && value === 0 ? '' : String(value);
        default:
            return String(value);
    }
}

export async function expectFieldShows(page, fieldSpec, value) {
    const input = field(page, fieldSpec.id);
    if (fieldSpec.kind === 'checkbox' || fieldSpec.kind === 'toggle') {
        await expect(input, fieldSpec.id).toBeChecked({ checked: value });
    } else {
        await expect(input, fieldSpec.id).toHaveValue(displayedValue(fieldSpec, value));
    }
}

export async function enterField(page, fieldSpec, value, typed) {
    const input = field(page, fieldSpec.id);
    switch (fieldSpec.kind) {
        case 'checkbox':
        case 'toggle':
            await setChecked(input, value);
            break;
        case 'select':
            await input.selectOption(value);
            break;
        default:
            await input.fill(typed ?? displayedValue(fieldSpec, value));
            break;
    }
}

// Clicks Save and returns the configuration the page sent. Fails, saying so, when nothing is
// sent: that is the "Save did nothing" failure this harness exists for.
export async function save(page) {
    const posted = page.waitForRequest(isConfigurationSave, { timeout: SAVE_TIMEOUT_MS });
    await saveButton(page).click();

    let request;
    try {
        request = await posted;
    } catch {
        throw new Error(`Save sent nothing to the server within ${SAVE_TIMEOUT_MS}ms`);
    }

    const response = await request.response();
    expect(response.status(), 'status of the configuration POST').toBeLessThan(300);
    await expectToast(page, 'Settings saved.');
    return request.postDataJSON();
}

// Runs `action` and checks the configuration was not posted while it ran or shortly after.
export async function expectNothingSaved(page, action, settleMs = 1500) {
    const saves = [];
    const listener = request => {
        if (isConfigurationSave(request)) {
            saves.push(request);
        }
    };

    page.on('request', listener);
    try {
        await action();
        await page.waitForTimeout(settleMs);
    } finally {
        page.off('request', listener);
    }

    expect(saves, 'configuration POSTs').toHaveLength(0);
}

// Dashboard.alert with a plain message does not open a dialog: jellyfin-web shows it as a toast,
// the same way it confirms a Save.
export async function expectToast(page, text) {
    await expect(page.locator('.toast', { hasText: text }).first()).toBeVisible();
}

// Sorting makes list order irrelevant where the page builds the list from the order the server
// happens to return users in.
export function comparable(config) {
    const copy = structuredClone(config);
    copy.UserConcurrentTranscodeLimits = [...(copy.UserConcurrentTranscodeLimits ?? [])]
        .sort((left, right) => left.UserId.localeCompare(right.UserId));
    return copy;
}

// Collects everything the page throws, including promise rejections nobody caught, which is how
// a broken Save handler usually fails: silently, from inside a .then().
export function collectPageErrors(page, errors = []) {
    page.on('pageerror', error => errors.push(`pageerror: ${error.message}`));
    page.on('console', message => {
        if (message.type() === 'error') {
            errors.push(`console.error: ${message.text()}`);
        }
    });
    return errors;
}
