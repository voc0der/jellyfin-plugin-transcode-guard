import { defineConfig, devices } from '@playwright/test';
import { STORAGE_STATE } from './harness/jellyfin.mjs';

export default defineConfig({
    testDir: '.',
    testMatch: '*.spec.mjs',
    globalSetup: './harness/global-setup.mjs',

    // One server and one plugin configuration for the whole run. Every test reseeds that
    // configuration before it starts, so two running at once would trample each other.
    workers: 1,
    fullyParallel: false,
    retries: 0,
    forbidOnly: !!process.env.CI,

    timeout: 90_000,
    expect: { timeout: 10_000 },

    reporter: process.env.CI
        ? [['list'], ['html', { open: 'never' }]]
        : [['list']],

    use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1280, height: 900 },
        locale: 'en-US',
        timezoneId: 'UTC',
        // Signed in once by global setup; every test starts as the server administrator.
        storageState: STORAGE_STATE,
        trace: 'retain-on-failure',
        screenshot: 'only-on-failure'
    }
});
