// Boots a throwaway Jellyfin with this repository's freshly built plugin installed.
//
// Why the real plugin in a real server, rather than the page grafted onto a stubbed dashboard
// the way the README screenshots are taken: the failures this harness exists to catch live in
// the seams a stub papers over. Jellyfin's own page loader decides how the page's script runs,
// jellyfin-web's emby-* elements decide what a click on a checkbox or button actually does, and
// only the server's configuration endpoint can say whether a Save arrived and what it stored.
// Every check goes through all three.

import { execFile } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const exec = promisify(execFile);
const sleep = ms => new Promise(done => setTimeout(done, ms));

const HERE = dirname(fileURLToPath(import.meta.url));

export const HARNESS_ROOT = resolve(HERE, '..');
export const REPO_ROOT = resolve(HARNESS_ROOT, '../..');
export const STORAGE_STATE = join(HARNESS_ROOT, '.auth', 'admin.json');

// The newest Jellyfin release, not the oldest one the plugin supports: the page has to work on
// what admins are running. JELLYFIN_IMAGE=jellyfin/jellyfin:12.1 checks another release.
export const JELLYFIN_IMAGE = process.env.JELLYFIN_IMAGE ?? 'jellyfin/jellyfin:12.2';

export const PLUGIN_ID = '2ce6b237-2610-4dfd-a34b-3f4c2ac64a88';
export const ADMIN = { name: 'admin', password: 'harness-admin-pw' };

const CONTAINER = 'transcode-guard-config-page-harness';
const CLIENT_AUTH = 'MediaBrowser Client="Transcode Guard harness", Device="harness", '
    + 'DeviceId="transcode-guard-config-page-harness", Version="1.0.0"';

async function docker(...args) {
    const { stdout } = await exec('docker', args, { maxBuffer: 64 * 1024 * 1024 });
    return stdout.trim();
}

// The page is an embedded resource, so the DLL is the only honest way to ship it to the server:
// a stale build would quietly test yesterday's page. Building every run costs a few seconds.
//
// Built inside the harness folder rather than the system temp folder: rootless Docker's daemon
// does not see this process's /tmp, and given a mount source it cannot see, Docker quietly makes
// an empty directory in the DLL's place and Jellyfin loads nothing.
export async function buildPlugin() {
    const output = join(HARNESS_ROOT, '.build');
    await exec('dotnet', [
        'build', join(REPO_ROOT, 'Jellyfin.Plugin.TranscodeGuard.csproj'),
        '--configuration', 'Release',
        '--nologo',
        '--verbosity', 'quiet',
        '--output', output
    ], { maxBuffer: 64 * 1024 * 1024 });

    return join(output, 'Jellyfin.Plugin.TranscodeGuard.dll');
}

// A booting Jellyfin answers on its port well before its API is live, and serves an HTML splash
// page to every route meanwhile. Readiness therefore means "returned JSON", not "returned 200".
async function waitForJson(url, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    let lastReason = 'never responded';

    while (Date.now() < deadline) {
        try {
            const response = await fetch(url);
            const contentType = response.headers.get('content-type') ?? '';
            if (response.ok && contentType.includes('json')) {
                return await response.json();
            }

            lastReason = `${response.status} ${contentType || 'no content-type'}`;
        } catch (error) {
            lastReason = error.message;
        }

        await sleep(1000);
    }

    throw new Error(`${url} never returned JSON within ${timeoutMs}ms (last: ${lastReason})`);
}

// Applying the wizard's first step makes the server rewrite its config and briefly 404 its
// routes, so each step is retried rather than failing the run on the first miss.
async function postWithRetry(base, path, body, attempts = 20) {
    let lastReason = 'never attempted';

    for (let attempt = 1; attempt <= attempts; attempt++) {
        try {
            const response = await fetch(base + path, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body ?? {})
            });

            if (response.ok) {
                return;
            }

            lastReason = `${response.status} ${(await response.text()).slice(0, 200)}`;
        } catch (error) {
            lastReason = error.message;
        }

        await sleep(1000);
    }

    throw new Error(`POST ${path} failed after ${attempts} attempts (last: ${lastReason})`);
}

async function completeSetupWizard(base) {
    await waitForJson(`${base}/Startup/Configuration`, 120_000);
    await postWithRetry(base, '/Startup/Configuration', {
        UICulture: 'en-US',
        MetadataCountryCode: 'US',
        PreferredMetadataLanguage: 'en'
    });
    // Not redundant: POST /Startup/User updates the default administrator, and 404s until this
    // GET has materialised it.
    await fetch(`${base}/Startup/User`);
    await postWithRetry(base, '/Startup/User', { Name: ADMIN.name, Password: ADMIN.password });
    await postWithRetry(base, '/Startup/RemoteAccess', {
        EnableRemoteAccess: true,
        EnableAutomaticPortMapping: false
    });
    await postWithRetry(base, '/Startup/Complete');
}

export async function stopJellyfin({ logPath } = {}) {
    if (logPath) {
        // Kept whatever the result, so a failed run in CI still has the server's side of it.
        const logs = await exec('docker', ['logs', CONTAINER], { maxBuffer: 64 * 1024 * 1024 })
            .then(({ stdout, stderr }) => stdout + stderr)
            .catch(() => '');
        if (logs) {
            await mkdir(dirname(logPath), { recursive: true });
            await writeFile(logPath, logs);
        }
    }

    await docker('rm', '-f', '-v', CONTAINER).catch(() => {});
}

export async function startJellyfin(dll) {
    await stopJellyfin();

    // The host port is left to Docker so a run never collides with a real server, the README
    // screenshot harness, or another checkout's harness. 127.0.0.1 only: nothing outside this
    // machine needs to reach it.
    //
    // Only the DLL is mounted, read-only, so the folder around it stays the server's own: it
    // writes the plugin's meta.json there, and a mounted folder would be left behind on the host
    // full of files owned by the container's root.
    await docker(
        'run', '--detach',
        '--name', CONTAINER,
        '--publish', '127.0.0.1::8096',
        '--volume', `${dll}:/config/plugins/TranscodeGuard/Jellyfin.Plugin.TranscodeGuard.dll:ro`,
        JELLYFIN_IMAGE);
    const port = (await docker('port', CONTAINER, '8096/tcp')).split('\n')[0].split(':').pop();
    const base = `http://127.0.0.1:${port}`;

    let info = await waitForJson(`${base}/System/Info/Public`, 180_000);
    if (!info.StartupWizardCompleted) {
        await completeSetupWizard(base);
    }

    // Asked again: what a server still booting answers with can be missing its version.
    info = await waitForJson(`${base}/System/Info/Public`, 30_000);

    const api = await JellyfinApi.login(base, ADMIN);
    const plugins = await api.request('GET', '/Plugins');
    const plugin = plugins.find(entry => entry.Id.replace(/-/g, '') === PLUGIN_ID.replace(/-/g, ''));
    if (!plugin || plugin.Status !== 'Active') {
        const tail = (await exec('docker', ['logs', '--tail', '80', CONTAINER]).catch(() => ({ stdout: '' }))).stdout;
        throw new Error(`Transcode Guard did not load on ${info.Version} (status: ${plugin?.Status ?? 'not listed'}).\n${tail}`);
    }

    return { base, version: info.Version, api };
}

export class JellyfinApi {
    constructor(base, token, userId) {
        this.base = base;
        this.token = token;
        this.userId = userId;
    }

    static async login(base, { name, password }) {
        const response = await fetch(`${base}/Users/AuthenticateByName`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', Authorization: CLIENT_AUTH },
            body: JSON.stringify({ Username: name, Pw: password })
        });
        if (!response.ok) {
            throw new Error(`could not sign in as ${name}: ${response.status}`);
        }

        const session = await response.json();
        return new JellyfinApi(base, session.AccessToken, session.User.Id);
    }

    async request(method, path, body) {
        const response = await fetch(this.base + path, {
            method,
            headers: {
                'Content-Type': 'application/json',
                Authorization: `${CLIENT_AUTH}, Token="${this.token}"`
            },
            body: body === undefined ? undefined : JSON.stringify(body)
        });
        if (!response.ok) {
            throw new Error(`${method} ${path} -> ${response.status} ${(await response.text()).slice(0, 300)}`);
        }

        const text = await response.text();
        return text ? JSON.parse(text) : null;
    }

    async createUser(name) {
        const user = await this.request('POST', '/Users/New', { Name: name, Password: `${name}-harness-pw` });
        return user.Id;
    }

    getPluginConfiguration() {
        return this.request('GET', `/Plugins/${PLUGIN_ID}/Configuration`);
    }

    setPluginConfiguration(config) {
        return this.request('POST', `/Plugins/${PLUGIN_ID}/Configuration`, config);
    }
}
