import { chromium } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import {
    ADMIN,
    HARNESS_ROOT,
    JELLYFIN_IMAGE,
    STORAGE_STATE,
    buildPlugin,
    startJellyfin,
    stopJellyfin
} from './jellyfin.mjs';

// Builds the plugin, boots a Jellyfin with it installed, adds the users the exclusion lists and
// per-user maximums need, and signs a browser in once so every test starts on the dashboard.
// The server's address, an admin token and the user IDs reach the tests through the
// environment, which Playwright hands to its workers after this returns.
export default async function globalSetup() {
    const started = Date.now();
    const dll = await buildPlugin();
    const server = await startJellyfin(dll);

    const users = {
        admin: server.api.userId,
        guest: await server.api.createUser('guest'),
        livingroom: await server.api.createUser('livingroom')
    };

    await mkdir(dirname(STORAGE_STATE), { recursive: true });
    const browser = await chromium.launch();
    try {
        const page = await browser.newPage();
        await page.goto(`${server.base}/web/`);
        await page.locator('#txtManualName').fill(ADMIN.name);
        await page.locator('#txtManualPassword').fill(ADMIN.password);
        await page.locator('#txtManualPassword').press('Enter');
        await page.waitForURL(/#\/home/, { timeout: 30_000 });
        await page.context().storageState({ path: STORAGE_STATE });
    } finally {
        await browser.close();
    }

    process.env.TRANSCODE_GUARD_HARNESS = JSON.stringify({
        base: server.base,
        token: server.api.token,
        users
    });

    const seconds = ((Date.now() - started) / 1000).toFixed(1);
    console.log(`Jellyfin ${server.version} (${JELLYFIN_IMAGE}) with Transcode Guard at ${server.base}, ready in ${seconds}s`);

    return async () => {
        const logPath = join(HARNESS_ROOT, 'test-results', 'jellyfin.log');
        if (process.env.HARNESS_KEEP) {
            console.log(`HARNESS_KEEP set: left running at ${server.base}. Remove it with: docker rm -f -v transcode-guard-config-page-harness`);
            return;
        }

        await stopJellyfin({ logPath });
    };
}
