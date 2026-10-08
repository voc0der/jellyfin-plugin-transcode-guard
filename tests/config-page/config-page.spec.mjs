import { expect, test as base } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import {
    ALL_REASONS,
    COMPOSITE_SETTINGS,
    EXCLUSION_DIALOGS,
    FIELDS,
    TYPED_FOR_B,
    configA,
    configB
} from './harness/fixtures.mjs';
import { PLUGIN_ID, REPO_ROOT } from './harness/jellyfin.mjs';
import {
    TABS,
    collectPageErrors,
    comparable,
    enterField,
    expectFieldShows,
    expectNothingSaved,
    expectTabSelected,
    expectToast,
    field,
    harness,
    isConfigurationSave,
    openSettings,
    save,
    saveButton,
    setChecked,
    settings,
    showTab,
    tabButton,
    tabPanel,
    waitUntilLoaded
} from './harness/settings-page.mjs';

// Every test that opens the page also fails on anything the page threw or logged as an error,
// even when what it asserted came out right. A test that provokes an error on purpose takes the
// ones it expects back out of `pageErrors`.
const test = base.extend({
    pageErrors: async ({}, use) => {
        await use([]);
    },
    page: async ({ page, pageErrors }, use) => {
        collectPageErrors(page, pageErrors);
        await use(page);
        expect(pageErrors, 'errors the page threw or logged').toEqual([]);
    }
});

const reasonBox = (page, reason) => settings(page).locator(`#transcodeReasonList input[data-reason-name="${reason}"]`);
const overrideToggle = (page, reason) => settings(page).locator(`#transcodeReasonList input[data-reason-override-toggle="${reason}"]`);
const overrideText = (page, reason) => settings(page).locator(`#transcodeReasonList textarea[data-reason-override-text="${reason}"]`);
const userMaximum = (page, userId) => settings(page).locator(`#concurrentTranscodeUserLimitList input[data-user-id="${userId}"]`);
const checkedCount = boxes => boxes.evaluateAll(list => list.filter(box => box.checked).length);

async function seed(config) {
    await harness().api.setPluginConfiguration(config);
    return config;
}

async function expectPageShows(page, config, users) {
    for (const spec of FIELDS) {
        await expectFieldShows(page, spec, config[spec.prop]);
    }

    for (const reason of ALL_REASONS) {
        await expect(reasonBox(page, reason), reason).toBeChecked({ checked: config.AlertTranscodeReasons.includes(reason) });
    }

    const overrides = Object.fromEntries(config.ReasonMessageOverrides.map(entry => [entry.ReasonName, entry.Message]));
    for (const reason of ALL_REASONS) {
        await expect(overrideToggle(page, reason), `${reason} override`).toBeChecked({ checked: reason in overrides });
        if (reason in overrides) {
            await expect(overrideText(page, reason)).toHaveValue(overrides[reason]);
        }
    }

    const maximums = Object.fromEntries(config.UserConcurrentTranscodeLimits.map(entry => [entry.UserId, String(entry.MaxTranscodes)]));
    for (const [name, id] of Object.entries(users)) {
        await expect(userMaximum(page, id), `${name}'s maximum`).toHaveValue(maximums[id] ?? '');
    }
}

async function enterReasons(page, config) {
    await settings(page).locator('#btnReasonClearAll').click();
    for (const reason of config.AlertTranscodeReasons) {
        await setChecked(reasonBox(page, reason), true);
    }

    const overrides = Object.fromEntries(config.ReasonMessageOverrides.map(entry => [entry.ReasonName, entry.Message]));
    for (const reason of ALL_REASONS) {
        await setChecked(overrideToggle(page, reason), reason in overrides);
        if (reason in overrides) {
            await overrideText(page, reason).fill(overrides[reason]);
        }
    }
}

// Types every value of `config` into the page, one tab at a time. A feature's on/off switch goes
// last in its tab, after the fields it hides.
async function enterConfig(page, config, users, maximums) {
    for (const tab of TABS) {
        const fields = FIELDS.filter(spec => spec.tab === tab);
        const composite = Object.values(COMPOSITE_SETTINGS).includes(tab);
        if (!fields.length && !composite) {
            continue;
        }

        await showTab(page, tab);
        if (tab === COMPOSITE_SETTINGS.AlertTranscodeReasons) {
            await enterReasons(page, config);
        }

        if (tab === COMPOSITE_SETTINGS.UserConcurrentTranscodeLimits) {
            for (const [name, value] of Object.entries(maximums)) {
                await userMaximum(page, users[name]).fill(value);
            }
        }

        for (const spec of fields.filter(entry => entry.kind !== 'toggle')) {
            await enterField(page, spec, config[spec.prop], TYPED_FOR_B[spec.prop]);
        }

        for (const spec of fields.filter(entry => entry.kind === 'toggle')) {
            await enterField(page, spec, config[spec.prop]);
        }
    }
}

function liveSessions(users) {
    const transcoding = reasons => ({ IsVideoDirect: false, TranscodeReasons: reasons });
    return [
        // The one the nag would fire on.
        { UserId: users.livingroom, UserName: 'livingroom', Client: 'Jellyfin Web', DeviceName: 'Firefox', NowPlayingItem: { Name: 'Nosferatu (1922)', Type: 'Movie' }, TranscodingInfo: transcoding(['VideoCodecNotSupported']) },
        // guest is on the nag exclusion list.
        { UserId: users.guest, UserName: 'guest', Client: 'Jellyfin Web', DeviceName: 'Chrome', NowPlayingItem: { Name: 'Metropolis', Type: 'Movie' }, TranscodingInfo: transcoding(['ContainerNotSupported']) },
        // Roku is an excluded client, and not an included one either.
        { UserId: users.admin, UserName: 'admin', Client: 'Jellyfin Roku', DeviceName: 'Roku Ultra', NowPlayingItem: { Name: 'Sunrise', Type: 'Movie' }, TranscodingInfo: transcoding(['VideoCodecNotSupported']) },
        // Bitrate only, which no selected reason covers.
        { UserId: users.admin, UserName: 'admin', Client: 'Jellyfin Web', DeviceName: 'Edge', NowPlayingItem: { Name: 'Faust', Type: 'Movie' }, TranscodingInfo: transcoding(['ContainerBitrateExceedsLimit']) },
        // Live TV, which A excludes.
        { UserId: users.admin, UserName: 'admin', Client: 'Jellyfin Web', DeviceName: 'Safari', NowPlayingItem: { Name: 'Channel 4', Type: 'TvChannel' }, TranscodingInfo: transcoding(['VideoCodecNotSupported']) },
        // Direct play: nothing to nag about.
        { UserId: users.admin, UserName: 'admin', Client: 'Jellyfin Web', DeviceName: 'Opera', NowPlayingItem: { Name: 'The Golem', Type: 'Movie' } }
    ];
}

test.describe('markup', () => {
    test('every element the script looks up by id exists exactly once', async () => {
        const html = await readFile(join(REPO_ROOT, 'Configuration', 'configPage.html'), 'utf8');
        const [markup, script] = html.split('<script');

        const declared = [...markup.matchAll(/\sid="([^"]+)"/g)].map(match => match[1]);
        const duplicates = declared.filter((id, index) => declared.indexOf(id) !== index);
        expect(duplicates, 'ids declared more than once').toEqual([]);

        const referenced = new Set([
            ...[...script.matchAll(/getElementById\('([^']+)'\)/g)].map(match => match[1]),
            ...[...script.matchAll(/querySelector(?:All)?\('#([\w-]+)/g)].map(match => match[1]),
            // The user exclusion dialogs are wired up from options objects (modalId, openButtonId, ...).
            ...[...script.matchAll(/\b\w*Id:\s*'([^']+)'/g)].map(match => match[1])
        ]);
        const missing = [...referenced].filter(id => !declared.includes(id));
        expect(missing, 'ids the script reaches for that the markup does not declare').toEqual([]);
    });

    test('every setting the server stores is exercised by this suite', async () => {
        const stored = Object.keys(await harness().api.getPluginConfiguration());
        const covered = new Set([
            ...FIELDS.map(spec => spec.prop),
            ...Object.keys(COMPOSITE_SETTINGS),
            ...EXCLUSION_DIALOGS.map(dialog => dialog.prop)
        ]);

        expect(stored.filter(prop => !covered.has(prop)), 'settings with no entry in harness/fixtures.mjs').toEqual([]);
    });
});

test.describe('saving', () => {
    test('the page shows every stored setting, and Save sends every edit to the server', async ({ page }) => {
        const { users, api } = harness();
        const a = await seed(configA(users));
        const b = configB(users);

        await openSettings(page, a);
        await expectPageShows(page, a, users);

        await enterConfig(page, b, users, { admin: '5', livingroom: '', guest: '2' });
        const sent = await save(page);
        expect(comparable(sent), 'what Save sent').toEqual(comparable(b));
        expect(comparable(await api.getPluginConfiguration()), 'what the server stored').toEqual(comparable(b));

        // And it all comes back: the switched-off features keep the values typed behind them.
        await page.reload();
        await waitUntilLoaded(page, b);
        await expectPageShows(page, b, users);
    });

    test('Save sends the configuration from every tab', async ({ page }) => {
        const a = await seed(configA(harness().users));
        await openSettings(page, a);

        for (const tab of TABS) {
            await showTab(page, tab);
            const sent = await save(page);
            expect(comparable(sent), `saved from the ${tab} tab`).toEqual(comparable(a));
        }
    });

    test('a bad value on another tab is shown where it is instead of swallowing Save', async ({ page }) => {
        const a = await seed(configA(harness().users));
        await openSettings(page, a);

        await showTab(page, 'gpu');
        await field(page, 'txtGpuIndex').fill('99');
        await showTab(page, 'playback');

        await expectNothingSaved(page, () => saveButton(page).click());
        await expectTabSelected(page, 'gpu');
        await expect(field(page, 'txtGpuIndex')).toBeVisible();
        await expect(field(page, 'txtGpuIndex')).toBeFocused();
        expect(await field(page, 'txtGpuIndex').evaluate(input => input.validationMessage)).not.toBe('');

        await field(page, 'txtGpuIndex').fill('3');
        expect((await save(page)).GpuIndex).toBe(3);
    });

    test('a cleared field with no fallback is shown where it is instead of failing on the server', async ({ page }) => {
        const a = await seed(configA(harness().users));
        await openSettings(page, a);

        await field(page, 'txtDelaySeconds').fill('');
        await showTab(page, 'motd');
        await expectNothingSaved(page, () => saveButton(page).click());
        await expectTabSelected(page, 'playback');
        await expect(field(page, 'txtDelaySeconds')).toBeFocused();
    });

    test('a Save the server turns down says so instead of spinning forever', async ({ page, pageErrors }) => {
        const a = await seed(configA(harness().users));
        await openSettings(page, a);
        await page.route(
            url => url.pathname.replace(/-/g, '').toLowerCase().endsWith(`/plugins/${PLUGIN_ID.replace(/-/g, '')}/configuration`),
            route => (route.request().method() === 'POST' ? route.fulfill({ status: 500, body: 'harness: refused' }) : route.fallback()));

        await saveButton(page).click();
        await expectToast(page, 'Failed to save the settings.');
        await expect(page.locator('.docspinner.mdlSpinnerActive'), 'the loading spinner').toHaveCount(0);

        // The browser reports the 500 and the page logs why it gave up; both are the point here.
        expect(pageErrors.some(error => error.includes('Failed to save Transcode Guard settings'))).toBe(true);
        pageErrors.length = 0;
    });

    test('a value left behind a switched-off feature never blocks Save', async ({ page }) => {
        const { users } = harness();

        // The state that made Save do nothing: the GPU guard off, and a GPU index above the
        // field's maximum of 15 stored behind it.
        const stored = await seed({ ...configA(users), EnableGpuResourceGuard: false, GpuIndex: 99 });
        await openSettings(page, stored);
        let sent = await save(page);
        expect(sent.EnableGpuResourceGuard).toBe(false);
        expect(sent.GpuIndex).toBe(99);

        // The same state reached by hand: a warning longer than the field's 60-minute maximum,
        // then the reaper switched off.
        await showTab(page, 'reaper');
        await field(page, 'txtPausedTranscodeWarningMinutes').fill('90');
        await setChecked(field(page, 'chkEnablePausedTranscodeReaper'), false);
        sent = await save(page);
        expect(sent.EnablePausedTranscodeReaper).toBe(false);
        expect(sent.PausedTranscodeWarningMinutes).toBe(90);
    });

    test('a browser install link that is not http(s) is refused before saving', async ({ page }) => {
        const a = await seed(configA(harness().users));
        await openSettings(page, a);
        const url = field(page, 'txtBrowserInstallUrl');

        // Not a URL at all: the field itself objects, from whichever tab Save was pressed on.
        await showTab(page, 'browser');
        await url.fill('example.com/client');
        await showTab(page, 'playback');
        await expectNothingSaved(page, () => saveButton(page).click());
        await expectTabSelected(page, 'browser');
        await expect(url).toBeFocused();

        // A URL, but not one to send a browser to.
        await url.fill('ftp://example.com/client');
        await expectNothingSaved(page, async () => {
            await saveButton(page).click();
            await expectToast(page, 'must be a full http:// or https:// link');
        });

        await url.fill('https://example.com/client-c');
        expect((await save(page)).BrowserInstallUrl).toBe('https://example.com/client-c');
    });
});

test.describe('buttons', () => {
    test('each feature switch shows and hides its settings', async ({ page }) => {
        const a = await seed(configA(harness().users));
        await openSettings(page, a);

        for (const toggle of FIELDS.filter(spec => spec.kind === 'toggle')) {
            await showTab(page, toggle.tab);
            const options = field(page, toggle.options);
            await expect(options, `${toggle.options} with ${toggle.id} on`).toBeVisible();
            await setChecked(field(page, toggle.id), false);
            await expect(options, `${toggle.options} with ${toggle.id} off`).toBeHidden();
            await setChecked(field(page, toggle.id), true);
            await expect(options).toBeVisible();
        }
    });

    test('trigger reason buttons and per-reason message overrides', async ({ page }) => {
        const a = await seed(configA(harness().users));
        await openSettings(page, a);
        await showTab(page, 'playback');
        const boxes = settings(page).locator('#transcodeReasonList input[data-reason-name]');

        await settings(page).locator('#btnReasonSelectAll').click();
        await expect.poll(() => checkedCount(boxes)).toBe(ALL_REASONS.length);
        await settings(page).locator('#btnReasonClearAll').click();
        await expect.poll(() => checkedCount(boxes)).toBe(0);
        // The shipped defaults are every reason.
        await settings(page).locator('#btnReasonResetDefaults').click();
        await expect.poll(() => checkedCount(boxes)).toBe(ALL_REASONS.length);

        const text = overrideText(page, 'AudioCodecNotSupported');
        await expect(text).toBeHidden();
        await setChecked(overrideToggle(page, 'AudioCodecNotSupported'), true);
        await expect(text).toBeVisible();
        await setChecked(overrideToggle(page, 'AudioCodecNotSupported'), false);
        await expect(text).toBeHidden();

        await settings(page).locator('#btnReasonClearAll').click();
        await setChecked(reasonBox(page, 'AudioCodecNotSupported'), true);
        expect((await save(page)).AlertTranscodeReasons).toEqual(['AudioCodecNotSupported']);
    });

    test('an empty per-user maximum says what it inherits', async ({ page }) => {
        const { users } = harness();
        const a = await seed(configA(users));
        await openSettings(page, a);
        await showTab(page, 'simultaneous');

        const admin = userMaximum(page, users.admin);
        await expect(admin).toHaveValue('');
        await expect(admin).toHaveAttribute('placeholder', 'Global (2)');
        await field(page, 'txtMaxConcurrentTranscodes').fill('7');
        await expect(admin).toHaveAttribute('placeholder', 'Global (7)');
        await field(page, 'txtMaxConcurrentTranscodes').fill('');
        await expect(admin).toHaveAttribute('placeholder', 'No limit');
    });

    for (const dialog of EXCLUSION_DIALOGS) {
        test(`${dialog.prop}: the dialog's buttons work, Save stores the list and Cancel does not`, async ({ page }) => {
            const { users, api } = harness();
            const a = await seed(configA(users));
            await openSettings(page, a);
            await showTab(page, dialog.tab);

            const modal = settings(page).locator(`#${dialog.modal}`);
            const boxes = modal.locator(`#${dialog.list} input[type="checkbox"]`);
            const box = userId => modal.locator(`#${dialog.list} input[data-user-id="${userId}"]`);

            await settings(page).locator(`#${dialog.open}`).click();
            await expect(modal).toBeVisible();
            await expect(boxes).toHaveCount(Object.keys(users).length);
            for (const id of Object.values(users)) {
                // Ticked means included, so a user on the list shows unticked.
                await expect(box(id)).toBeChecked({ checked: !a[dialog.prop].includes(id) });
            }

            await modal.locator(`#${dialog.clearAll}`).click();
            await expect.poll(() => checkedCount(boxes)).toBe(0);
            await modal.locator(`#${dialog.selectAll}`).click();
            await expect.poll(() => checkedCount(boxes)).toBe(3);
            await modal.locator(`#${dialog.clearAll}`).click();
            await modal.locator(`#${dialog.reset}`).click();
            await expect.poll(() => checkedCount(boxes)).toBe(3);

            await setChecked(box(users.guest), false);
            await setChecked(box(users.livingroom), false);
            const posted = page.waitForRequest(isConfigurationSave, { timeout: 5000 });
            await modal.locator(`#${dialog.save}`).click();
            const excluded = [users.guest, users.livingroom].sort();
            expect([...(await posted).postDataJSON()[dialog.prop]].sort()).toEqual(excluded);
            await expectToast(page, dialog.savedMessage(2));
            await expect(modal).toBeHidden();

            const stored = await api.getPluginConfiguration();
            expect([...stored[dialog.prop]].sort()).toEqual(excluded);
            expect(comparable({ ...stored, [dialog.prop]: a[dialog.prop] }), 'everything else').toEqual(comparable(a));

            await settings(page).locator(`#${dialog.open}`).click();
            await expect(box(users.guest)).not.toBeChecked();
            await setChecked(box(users.guest), true);
            await expectNothingSaved(page, () => modal.locator(`#${dialog.cancel}`).click());
            await expect(modal).toBeHidden();
        });
    }

    test('the live session monitor lists only the sessions the nag would fire on', async ({ page }) => {
        const { users } = harness();
        const a = await seed(configA(users));
        let sessionRequests = 0;
        await page.route(url => url.pathname.toLowerCase().endsWith('/sessions'), route => {
            sessionRequests++;
            return route.fulfill({ json: liveSessions(users) });
        });

        await openSettings(page, a);
        await showTab(page, 'sessions');
        const before = sessionRequests;
        await settings(page).locator('#btnRefreshLiveSessions').click();
        await expect.poll(() => sessionRequests).toBeGreaterThan(before);

        await expect(field(page, 'liveSettingsSummary')).toHaveText('1 active session(s) currently matching nag criteria.');
        const cards = field(page, 'liveSettingsSessionList').locator(':scope > div');
        await expect(cards).toHaveCount(1);
        await expect(cards.first()).toContainText('Nosferatu (1922)');
        await expect(cards.first()).toContainText('livingroom');
        await expect(field(page, 'liveSettingsLastUpdated')).toContainText('Last updated:');
    });
});

test.describe('navigation', () => {
    // Jellyfin runs the page's script again on every fresh visit and replays pageshow on a
    // restored one. Handlers bound twice show up as a click that toggles something twice, or a
    // Save that posts twice; handlers bound to a stale copy show up as a click that does nothing.
    async function expectWorksOnce(page) {
        await showTab(page, 'playback');
        const text = overrideText(page, 'AudioCodecNotSupported');
        await expect(text).toBeHidden();
        await setChecked(overrideToggle(page, 'AudioCodecNotSupported'), true);
        await expect(text).toBeVisible();
        await setChecked(overrideToggle(page, 'AudioCodecNotSupported'), false);

        const saves = [];
        const listener = request => isConfigurationSave(request) && saves.push(request);
        page.on('request', listener);
        await save(page);
        await page.waitForTimeout(1500);
        page.off('request', listener);
        expect(saves, 'POSTs from one click on Save').toHaveLength(1);
    }

    test('leaving the page and coming back leaves every handler working once', async ({ page }) => {
        const a = await seed(configA(harness().users));
        await openSettings(page, a);
        await expectWorksOnce(page);

        // Out through the dashboard and back with the browser's Back button...
        await page.evaluate(() => Dashboard.navigate('dashboard/plugins'));
        await expect(page).toHaveURL(/dashboard\/plugins/);
        await page.goBack();
        await waitUntilLoaded(page, a);
        await expectWorksOnce(page);

        // ...and out again, then back in through the dashboard's own navigation.
        await page.evaluate(() => Dashboard.navigate('dashboard/plugins'));
        await expect(page).toHaveURL(/dashboard\/plugins/);
        await page.evaluate(() => Dashboard.navigate(`configurationpage?name=${encodeURIComponent('Transcode Guard')}`));
        await waitUntilLoaded(page, a);
        await expectWorksOnce(page);
    });
});

test.describe('tabs', () => {
    const LABELS = {
        playback: 'Playback',
        browser: 'Browser Warning',
        login: 'Login Nag',
        limit: 'Transcode Limit',
        simultaneous: 'Simultaneous',
        gpu: 'GPU Guard',
        reaper: 'Paused Reaper',
        motd: 'MOTD',
        sessions: 'Live Sessions'
    };

    test('one tab per section, in order, showing exactly one panel at a time', async ({ page }) => {
        const a = await seed(configA(harness().users));
        await openSettings(page, a);

        const tabs = settings(page).locator('[role="tablist"] [role="tab"]');
        await expect(tabs).toHaveText(TABS.map(tab => LABELS[tab]));
        await expect(tabButton(page, 'playback'), 'the tab the page opens on').toHaveAttribute('aria-selected', 'true');

        // The tabs sit inside the form, so one that forgot it is a type="button" would submit it.
        await expectNothingSaved(page, async () => {
            for (const tab of TABS) {
                await showTab(page, tab);
                for (const other of TABS.filter(entry => entry !== tab)) {
                    await expect(tabPanel(page, other)).toBeHidden();
                    await expect(tabButton(page, other)).toHaveAttribute('aria-selected', 'false');
                }
            }
        });
    });

    test('arrow keys, Home and End move between tabs', async ({ page }) => {
        const a = await seed(configA(harness().users));
        await openSettings(page, a);

        await tabButton(page, 'playback').focus();
        await page.keyboard.press('ArrowRight');
        await expect(tabButton(page, 'browser')).toBeFocused();
        await expect(tabPanel(page, 'browser')).toBeVisible();
        await page.keyboard.press('ArrowLeft');
        await page.keyboard.press('ArrowLeft');
        await expect(tabButton(page, 'sessions'), 'wraps from the first tab to the last').toBeFocused();
        await page.keyboard.press('Home');
        await expect(tabButton(page, 'playback')).toBeFocused();
        await page.keyboard.press('End');
        await expect(tabPanel(page, 'sessions')).toBeVisible();
    });

    test('the live session monitor polls only while its tab is open', async ({ page }) => {
        const { users } = harness();
        const a = await seed(configA(users));
        let sessionRequests = 0;
        await page.route(url => url.pathname.toLowerCase().endsWith('/sessions'), route => {
            sessionRequests++;
            return route.fulfill({ json: liveSessions(users) });
        });

        await page.clock.install();
        await openSettings(page, a);

        // On another tab, a minute passes without a single session lookup...
        const onPlayback = sessionRequests;
        await page.clock.runFor(60_000);
        expect(sessionRequests - onPlayback, 'session lookups while the monitor is out of sight').toBe(0);

        // ...opening the monitor looks straight away and keeps looking while it stays open...
        await showTab(page, 'sessions');
        await expect(field(page, 'liveSettingsSummary')).toHaveText('1 active session(s) currently matching nag criteria.');
        const opened = sessionRequests;
        await page.clock.runFor(31_000);
        await expect.poll(() => sessionRequests - opened).toBeGreaterThanOrEqual(2);

        // ...and leaving it stops the lookups again.
        await showTab(page, 'gpu');
        const left = sessionRequests;
        await page.clock.runFor(60_000);
        expect(sessionRequests - left, 'session lookups after leaving the monitor').toBe(0);
    });
});
