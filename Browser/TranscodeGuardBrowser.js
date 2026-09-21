/*
 * Transcode Guard: shows the browser install warning in Jellyfin Web.
 *
 * Registered through the JavaScript Injector plugin. It makes no requests of its own: the server
 * attaches the warning to the playback nag or transcode refusal it already sends over this
 * session's WebSocket, and this script only renders it. Tabs without the script keep Jellyfin's normal popup.
 *
 * Written in ES5 on purpose. JavaScript Injector serves every plugin's script from one file, so
 * syntax an older TV browser cannot parse would break the other scripts too.
 */
(function () {
    'use strict';

    if (window.__transcodeGuardBrowserNag) {
        return;
    }

    window.__transcodeGuardBrowserNag = true;

    // Must match BrowserNagRules on the server.
    var MARKER_ARGUMENT = 'TranscodeGuardNag';
    var MARKER_VALUE = '1';
    var NAG_ID_ARGUMENT = 'TranscodeGuardNagId';
    var TITLE_ARGUMENT = 'TranscodeGuardTitle';
    var MESSAGE_ARGUMENT = 'TranscodeGuardMessage';
    var REASON_ARGUMENT = 'TranscodeGuardReason';
    var INSTALL_URL_ARGUMENT = 'TranscodeGuardInstallUrl';
    var AUTO_CLOSE_SECONDS_ARGUMENT = 'TranscodeGuardAutoCloseSeconds';
    var DISMISS_LABEL_ARGUMENT = 'TranscodeGuardDismissLabel';
    var REASON_LABEL_ARGUMENT = 'TranscodeGuardReasonLabel';
    var MIN_AUTO_CLOSE_SECONDS = 5;
    var MAX_AUTO_CLOSE_SECONDS = 180;
    var DEFAULT_AUTO_CLOSE_SECONDS = 60;

    // A playback nag sends no labels; a refusal sends its own, because playback has already failed.
    var DEFAULT_DISMISS_LABEL = 'Continue';
    var DEFAULT_REASON_LABEL = 'Reason';

    var HOST_ID = 'transcode-guard-browser-nag';

    var STYLE = [
        ':host { all: initial; }',
        '.transcode-guard-backdrop {',
        '  --tg-surface: #202226; --tg-text: #f1f1f1; --tg-muted: #b3b7bf; --tg-border: rgba(255, 255, 255, 0.14);',
        '  --tg-secondary: rgba(255, 255, 255, 0.08); --tg-accent: #00a4dc; --tg-accent-text: #ffffff; --tg-warning: #f5b301;',
        '  position: fixed; top: 0; right: 0; bottom: 0; left: 0; z-index: 2147483000;',
        '  display: flex; align-items: center; justify-content: center;',
        '  box-sizing: border-box; padding: 16px; background: rgba(0, 0, 0, 0.6);',
        '  font-family: "Noto Sans", system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;',
        '}',
        '@media (prefers-color-scheme: light) {',
        '  .transcode-guard-backdrop {',
        '    --tg-surface: #ffffff; --tg-text: #1c1d21; --tg-muted: #5a5f69; --tg-border: rgba(0, 0, 0, 0.14);',
        '    --tg-secondary: rgba(0, 0, 0, 0.06); --tg-accent: #0080b0; --tg-warning: #a86400;',
        '  }',
        '}',
        '.transcode-guard-dialog {',
        '  position: relative; box-sizing: border-box; width: 100%; max-width: 28rem; max-height: 100%; overflow: auto;',
        '  padding: 24px 24px 0; border: 1px solid var(--tg-border); border-radius: 12px;',
        '  background: var(--tg-surface); color: var(--tg-text); box-shadow: 0 16px 48px rgba(0, 0, 0, 0.45); outline: none;',
        '}',
        '.transcode-guard-header { display: flex; align-items: center; margin: 0 0 12px; }',
        '.transcode-guard-icon { margin-right: 10px; color: var(--tg-warning); font-size: 1.5rem; line-height: 1; }',
        '.transcode-guard-title { margin: 0; font-size: 1.25rem; font-weight: 600; line-height: 1.3; }',
        '.transcode-guard-message { margin: 0 0 8px; font-size: 1rem; line-height: 1.5; white-space: pre-line; }',
        '.transcode-guard-reason { margin: 0; font-size: 0.875rem; line-height: 1.4; color: var(--tg-muted); }',
        '.transcode-guard-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; margin: 12px -4px 20px; }',
        '.transcode-guard-button {',
        '  display: inline-flex; align-items: center; justify-content: center; box-sizing: border-box;',
        '  min-height: 40px; margin: 8px 4px 0; padding: 8px 18px; border: 1px solid transparent; border-radius: 6px;',
        '  font: inherit; font-size: 0.95rem; font-weight: 600; text-decoration: none; cursor: pointer;',
        '}',
        '.transcode-guard-button:focus { outline: 3px solid var(--tg-accent); outline-offset: 2px; }',
        '.transcode-guard-install { background: var(--tg-accent); color: var(--tg-accent-text); text-transform: uppercase; letter-spacing: 0.03em; }',
        '.transcode-guard-dismiss { background: var(--tg-secondary); color: var(--tg-text); border-color: var(--tg-border); }',
        '.transcode-guard-timer { position: absolute; right: 0; bottom: 0; left: 0; height: 3px; overflow: hidden; }',
        '.transcode-guard-timer-bar {',
        '  height: 100%; background: var(--tg-accent); transform-origin: left center;',
        '  animation-name: transcode-guard-countdown; animation-timing-function: linear; animation-fill-mode: forwards;',
        '}',
        '.transcode-guard-paused .transcode-guard-timer-bar { animation-play-state: paused; }',
        '@keyframes transcode-guard-countdown { from { transform: scaleX(1); } to { transform: scaleX(0); } }',
        '@media (max-width: 480px) {',
        '  .transcode-guard-actions { flex-direction: column-reverse; }',
        '  .transcode-guard-button { width: calc(100% - 8px); }',
        '}',
        '@media (prefers-reduced-motion: reduce) { .transcode-guard-timer { display: none; } }'
    ].join('\n');

    // Static markup only; every value from the server is set with textContent or a checked href.
    var MARKUP = '<style>' + STYLE + '</style>'
        + '<div class="transcode-guard-backdrop">'
        + '<div class="transcode-guard-dialog" role="dialog" aria-modal="true" tabindex="-1"'
        + ' aria-labelledby="transcode-guard-title" aria-describedby="transcode-guard-message">'
        + '<div class="transcode-guard-header">'
        + '<span class="transcode-guard-icon" aria-hidden="true">⚠</span>'
        + '<h2 class="transcode-guard-title" id="transcode-guard-title"></h2>'
        + '</div>'
        + '<p class="transcode-guard-message" id="transcode-guard-message"></p>'
        + '<p class="transcode-guard-reason"></p>'
        + '<div class="transcode-guard-actions">'
        + '<button type="button" class="transcode-guard-button transcode-guard-dismiss"></button>'
        + '<a class="transcode-guard-button transcode-guard-install" target="_blank" rel="noopener noreferrer">Install client</a>'
        + '</div>'
        + '<div class="transcode-guard-timer" aria-hidden="true"><div class="transcode-guard-timer-bar"></div></div>'
        + '</div>'
        + '</div>';

    var subscribedClient = null;
    var warnedNoSubscribe = false;
    var seenNagIds = {};
    var current = null;

    function subscribe() {
        var client = window.ApiClient;
        if (!client || client === subscribedClient) {
            return;
        }

        if (typeof client.subscribe !== 'function') {
            if (!warnedNoSubscribe) {
                warnedNoSubscribe = true;
                console.warn('Transcode Guard: ApiClient.subscribe is unavailable, so browser install warnings fall back to Jellyfin\'s normal popup.');
            }

            return;
        }

        client.subscribe(['GeneralCommand'], onGeneralCommand);
        subscribedClient = client;
    }

    function onGeneralCommand(message) {
        // Jellyfin Web's own handlers share this dispatch loop, so nothing may escape from here.
        try {
            var command = message && message.Data;
            var args = command && command.Arguments;
            if (!command || command.Name !== 'DisplayMessage' || !args || args[MARKER_ARGUMENT] !== MARKER_VALUE) {
                return;
            }

            var nagId = String(args[NAG_ID_ARGUMENT] || '');

            // Sticky messages resend the same nag; show it once, and never again after it closes.
            if (!nagId || seenNagIds[nagId]) {
                return;
            }

            seenNagIds[nagId] = true;
            show({
                title: String(args[TITLE_ARGUMENT] || ''),
                message: String(args[MESSAGE_ARGUMENT] || ''),
                reason: String(args[REASON_ARGUMENT] || ''),
                reasonLabel: String(args[REASON_LABEL_ARGUMENT] || DEFAULT_REASON_LABEL),
                dismissLabel: String(args[DISMISS_LABEL_ARGUMENT] || DEFAULT_DISMISS_LABEL),
                installUrl: safeUrl(args[INSTALL_URL_ARGUMENT]),
                autoCloseSeconds: clampSeconds(args[AUTO_CLOSE_SECONDS_ARGUMENT])
            });
        } catch (error) {
            console.error('Transcode Guard: could not show the browser install warning', error);
        }
    }

    function safeUrl(value) {
        if (typeof value !== 'string' || !/^https?:\/\//i.test(value)) {
            return null;
        }

        try {
            var parsed = new URL(value);
            return parsed.protocol === 'http:' || parsed.protocol === 'https:' ? parsed.href : null;
        } catch (error) {
            return null;
        }
    }

    function clampSeconds(value) {
        var seconds = parseInt(value, 10);
        if (isNaN(seconds)) {
            return DEFAULT_AUTO_CLOSE_SECONDS;
        }

        return Math.min(MAX_AUTO_CLOSE_SECONDS, Math.max(MIN_AUTO_CLOSE_SECONDS, seconds));
    }

    function container() {
        var fullscreen = document.fullscreenElement || document.webkitFullscreenElement;

        // Jellyfin Web fullscreens the whole page, but anything else that fullscreens an element
        // would hide a warning attached to the body. A media element cannot hold children.
        if (fullscreen && fullscreen !== document.documentElement && !(fullscreen instanceof HTMLMediaElement)) {
            return fullscreen;
        }

        return document.body;
    }

    function show(nag) {
        close();

        var host = document.createElement('div');
        host.id = HOST_ID;
        var root = typeof host.attachShadow === 'function' ? host.attachShadow({ mode: 'open' }) : host;
        root.innerHTML = MARKUP;

        var backdrop = root.querySelector('.transcode-guard-backdrop');
        var dialog = root.querySelector('.transcode-guard-dialog');
        var install = root.querySelector('.transcode-guard-install');
        var dismissButton = root.querySelector('.transcode-guard-dismiss');
        var reason = root.querySelector('.transcode-guard-reason');

        root.querySelector('.transcode-guard-title').textContent = nag.title;
        root.querySelector('.transcode-guard-message').textContent = nag.message;
        dismissButton.textContent = nag.dismissLabel;

        if (nag.reason) {
            reason.textContent = nag.reasonLabel + ': ' + nag.reason;
        } else {
            reason.parentNode.removeChild(reason);
        }

        if (nag.installUrl) {
            install.href = nag.installUrl;
            install.addEventListener('click', function () {
                // Let the link open its tab before the warning is taken down.
                setTimeout(close, 0);
            });
        } else {
            // A warning without a usable link still helps; a broken button would not.
            install.parentNode.removeChild(install);
            install = null;
        }

        var state = {
            host: host,
            dialog: dialog,
            restoreFocus: document.activeElement,
            remainingMs: nag.autoCloseSeconds * 1000,
            startedAt: 0,
            closeTimer: null
        };

        root.querySelector('.transcode-guard-timer-bar').style.animationDuration = nag.autoCloseSeconds + 's';

        dismissButton.addEventListener('click', close);

        backdrop.addEventListener('mousedown', function (event) {
            // Clicking outside the dialog keeps focus, so keys cannot reach the player behind it.
            if (event.target === backdrop) {
                event.preventDefault();
            }
        });

        // Reading the warning should not be cut short, so the countdown waits while the pointer is on it.
        dialog.addEventListener('mouseenter', function () {
            pauseTimer(state);
        });
        dialog.addEventListener('mouseleave', function () {
            startTimer(state);
        });

        dialog.addEventListener('keydown', function (event) {
            if (event.key === 'Escape' || event.key === 'Esc') {
                event.preventDefault();
                close();
            } else if (event.key === 'Tab') {
                trapFocus(event, root, install ? [dismissButton, install] : [dismissButton]);
            }

            // Keep Jellyfin's player shortcuts (space, arrows, f) from acting behind the warning.
            event.stopPropagation();
        });

        current = state;
        container().appendChild(host);
        document.addEventListener('fullscreenchange', onFullscreenChange);
        (install || dismissButton).focus();
        startTimer(state);
    }

    function trapFocus(event, root, focusables) {
        var active = 'activeElement' in root ? root.activeElement : document.activeElement;
        var first = focusables[0];
        var last = focusables[focusables.length - 1];

        if (event.shiftKey && (active === first || focusables.indexOf(active) === -1)) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && (active === last || focusables.indexOf(active) === -1)) {
            event.preventDefault();
            first.focus();
        }
    }

    function startTimer(state) {
        if (current !== state || state.closeTimer) {
            return;
        }

        state.dialog.classList.remove('transcode-guard-paused');
        state.startedAt = Date.now();
        state.closeTimer = setTimeout(close, Math.max(0, state.remainingMs));
    }

    function pauseTimer(state) {
        if (!state.closeTimer) {
            return;
        }

        clearTimeout(state.closeTimer);
        state.closeTimer = null;
        state.remainingMs -= Date.now() - state.startedAt;
        state.dialog.classList.add('transcode-guard-paused');
    }

    function onFullscreenChange() {
        if (current) {
            container().appendChild(current.host);
        }
    }

    function close() {
        if (!current) {
            return;
        }

        var state = current;
        current = null;
        clearTimeout(state.closeTimer);
        document.removeEventListener('fullscreenchange', onFullscreenChange);

        if (state.host.parentNode) {
            state.host.parentNode.removeChild(state.host);
        }

        var restore = state.restoreFocus;
        if (restore && typeof restore.focus === 'function' && document.body.contains(restore)) {
            try {
                restore.focus();
            } catch (error) {
                // The element that had focus may no longer accept it; nothing else to do.
            }
        }
    }

    subscribe();

    // Jellyfin Web can swap ApiClient (another server, a new sign-in); check again on navigation.
    document.addEventListener('viewshow', subscribe);
})();
