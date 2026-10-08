// Two complete plugin configurations. The round-trip test seeds the server with A, checks the
// page shows A, then types B in through the page and checks B is what reaches the server.
// Every setting differs between them, so a field that loads but never saves (or saves but never
// loads) cannot pass by coincidence: each boolean flips, each number moves, each text changes.

export const ALL_REASONS = [
    'ContainerNotSupported',
    'VideoCodecNotSupported',
    'AudioCodecNotSupported',
    'SubtitleCodecNotSupported',
    'VideoProfileNotSupported',
    'VideoLevelNotSupported',
    'VideoResolutionNotSupported',
    'VideoBitDepthNotSupported',
    'VideoFramerateNotSupported',
    'RefFramesNotSupported',
    'AnamorphicVideoNotSupported',
    'InterlacedVideoNotSupported',
    'AudioChannelsNotSupported',
    'AudioProfileNotSupported',
    'AudioSampleRateNotSupported',
    'SecondaryAudioNotSupported',
    'VideoRangeTypeNotSupported',
    'DirectPlayError'
];

// Every feature is switched on in A so that each of its fields is on screen to be checked and
// edited. B switches every one of them off again, last, which is also how the test proves the
// values behind a collapsed feature still save.
export function configA(users) {
    return {
        NagMessage: 'Harness A: this video is being transcoded.',
        UseStickyPlaybackMessages: true,
        MessageTimeoutMs: 12000,
        EnableLogging: false,
        DelaySeconds: 7,
        EnableLoginNag: true,
        LoginNagThreshold: 6,
        ExcludeLiveTv: true,
        LoginNagTimeWindow: 'Month',
        LoginNagMessage: 'Harness A: {{transcodes}} transcodes in the last {{timewindow}}.',
        UseStickyLoginNagMessages: false,
        EnableTranscodeLimit: true,
        TranscodeLimitThreshold: 12,
        TranscodeLimitHeader: 'Harness A limit title',
        TranscodeLimitMessage: 'Harness A: over the limit of {{limit}}.',
        UseStickyTranscodeLimitMessages: true,
        EnableConcurrentTranscodeLimit: true,
        MaxConcurrentTranscodes: 2,
        UserConcurrentTranscodeLimits: [
            { UserId: users.livingroom, MaxTranscodes: 4 },
            { UserId: users.guest, MaxTranscodes: 1 }
        ],
        ConcurrentTranscodeLimitHeader: 'Harness A simultaneous title',
        ConcurrentTranscodeLimitMessage: 'Harness A: {{active}} of {{limit}} running.',
        UseStickyConcurrentTranscodeLimitMessages: false,
        EnableBrowserInstallNag: true,
        BrowserInstallUrl: 'https://example.com/client-a',
        BrowserNagTitle: 'Harness A browser title',
        BrowserNagMessage: 'Harness A browser message.',
        BrowserNagAutoCloseSeconds: 45,
        AlertTranscodeReasons: ['ContainerNotSupported', 'VideoCodecNotSupported', 'AudioCodecNotSupported'],
        ReasonMessageOverrides: [
            { ReasonName: 'VideoCodecNotSupported', Message: 'Harness A override for the video codec.' }
        ],
        IncludedClientPatterns: ['Jellyfin Web'],
        ExcludedClientPatterns: ['Kodi', 'Roku'],
        ExcludedUserIds: [users.guest],
        EnableMotd: true,
        MotdMessage: 'Harness A message of the day.',
        UseStickyMotdMessages: true,
        MotdExcludedUserIds: [],
        MotdIncludedClientPatterns: ['Android'],
        MotdExcludedClientPatterns: ['Swiftfin'],
        EnableGpuResourceGuard: true,
        GpuIndex: 1,
        GpuCheckTimeoutMilliseconds: 1500,
        NvidiaSmiPath: '/usr/bin/nvidia-smi',
        GpuGuardDeniedHeader: 'Harness A GPU title',
        GpuGuardDeniedMessage: 'Harness A GPU message.',
        UseStickyGpuGuardMessages: false,
        EnablePausedTranscodeReaper: true,
        PausedTranscodeTimeoutMinutes: 30,
        PausedTranscodeWarningMinutes: 3,
        PausedTranscodeWarningHeader: 'Harness A reaper title',
        PausedTranscodeWarningMessage: 'Harness A: stopping in {{minutes}} minute(s).',
        UseStickyPausedTranscodeMessages: true,
        ReapPausedDirectPlay: false,
        PausedTranscodeExcludedUserIds: [users.livingroom]
    };
}

// What the server should hold after B is typed over A and saved. The user exclusion lists are
// A's on purpose: they are edited in their own dialogs, never by the main Save.
export function configB(users) {
    return {
        ...configA(users),
        NagMessage: 'Harness B: still transcoding.',
        UseStickyPlaybackMessages: false,
        MessageTimeoutMs: 15000,
        EnableLogging: true,
        DelaySeconds: 9,
        EnableLoginNag: false,
        LoginNagThreshold: 8,
        ExcludeLiveTv: false,
        LoginNagTimeWindow: 'Week',
        LoginNagMessage: 'Harness B: {{transcodes}} this {{timewindow}}.',
        UseStickyLoginNagMessages: true,
        EnableTranscodeLimit: false,
        TranscodeLimitThreshold: 20,
        TranscodeLimitHeader: 'Harness B limit title',
        TranscodeLimitMessage: 'Harness B: limit {{limit}} reached.',
        UseStickyTranscodeLimitMessages: false,
        EnableConcurrentTranscodeLimit: false,
        MaxConcurrentTranscodes: 3,
        // livingroom is cleared in the page, so it falls back to the global maximum and drops out.
        UserConcurrentTranscodeLimits: [
            { UserId: users.admin, MaxTranscodes: 5 },
            { UserId: users.guest, MaxTranscodes: 2 }
        ],
        ConcurrentTranscodeLimitHeader: 'Harness B simultaneous title',
        ConcurrentTranscodeLimitMessage: 'Harness B: {{active}} active, {{limit}} allowed.',
        UseStickyConcurrentTranscodeLimitMessages: true,
        EnableBrowserInstallNag: false,
        BrowserInstallUrl: 'https://example.com/client-b',
        BrowserNagTitle: 'Harness B browser title',
        BrowserNagMessage: 'Harness B browser message.',
        BrowserNagAutoCloseSeconds: 90,
        // Saved in the page's own reason order, not the order they were ticked in.
        AlertTranscodeReasons: ['SubtitleCodecNotSupported', 'VideoRangeTypeNotSupported', 'DirectPlayError'],
        ReasonMessageOverrides: [
            { ReasonName: 'DirectPlayError', Message: 'Harness B override for direct play errors.' }
        ],
        IncludedClientPatterns: ['browser', 'Jellyfin Media Player'],
        ExcludedClientPatterns: [],
        EnableMotd: false,
        MotdMessage: 'Harness B message of the day.',
        UseStickyMotdMessages: false,
        MotdIncludedClientPatterns: [],
        MotdExcludedClientPatterns: ['Kodi', 'Infuse'],
        EnableGpuResourceGuard: false,
        GpuIndex: 2,
        GpuCheckTimeoutMilliseconds: 2500,
        NvidiaSmiPath: '/opt/nvidia/bin/nvidia-smi',
        GpuGuardDeniedHeader: 'Harness B GPU title',
        GpuGuardDeniedMessage: 'Harness B GPU message.',
        UseStickyGpuGuardMessages: true,
        EnablePausedTranscodeReaper: false,
        PausedTranscodeTimeoutMinutes: 40,
        // 0 is a real value here ("no warning"), so it must survive rather than read as unset.
        PausedTranscodeWarningMinutes: 0,
        PausedTranscodeWarningHeader: 'Harness B reaper title',
        PausedTranscodeWarningMessage: 'Harness B: {{minutes}} minute(s) left.',
        UseStickyPausedTranscodeMessages: false,
        ReapPausedDirectPlay: true
    };
}

// What gets typed when it is not simply the stored value, to exercise the page's clean-up on
// the way out: surrounding spaces trimmed, case-insensitive duplicates dropped, commas and new
// lines both accepted as separators.
export const TYPED_FOR_B = {
    NvidiaSmiPath: '  /opt/nvidia/bin/nvidia-smi  ',
    IncludedClientPatterns: 'browser\nJellyfin Media Player\nBROWSER',
    MotdExcludedClientPatterns: 'Kodi, kodi\nInfuse'
};

// Every field on the page and the setting it edits, grouped by the tab it lives on. A `toggle`
// is a feature's on/off switch: it shows and hides `options`, and is set last in its tab so the
// fields it would hide have been filled first.
export const FIELDS = [
    { tab: 'playback', id: 'txtNagMessage', prop: 'NagMessage', kind: 'text' },
    { tab: 'playback', id: 'chkUseStickyPlaybackMessages', prop: 'UseStickyPlaybackMessages', kind: 'checkbox' },
    { tab: 'playback', id: 'txtDelaySeconds', prop: 'DelaySeconds', kind: 'number' },
    { tab: 'playback', id: 'txtMessageTimeout', prop: 'MessageTimeoutMs', kind: 'number' },
    { tab: 'playback', id: 'chkEnableLogging', prop: 'EnableLogging', kind: 'checkbox' },
    { tab: 'playback', id: 'chkExcludeLiveTv', prop: 'ExcludeLiveTv', kind: 'checkbox' },
    { tab: 'playback', id: 'txtIncludedClientPatterns', prop: 'IncludedClientPatterns', kind: 'patterns' },
    { tab: 'playback', id: 'txtExcludedClientPatterns', prop: 'ExcludedClientPatterns', kind: 'patterns' },

    { tab: 'browser', id: 'txtBrowserInstallUrl', prop: 'BrowserInstallUrl', kind: 'text' },
    { tab: 'browser', id: 'txtBrowserNagTitle', prop: 'BrowserNagTitle', kind: 'text' },
    { tab: 'browser', id: 'txtBrowserNagMessage', prop: 'BrowserNagMessage', kind: 'text' },
    { tab: 'browser', id: 'txtBrowserNagAutoCloseSeconds', prop: 'BrowserNagAutoCloseSeconds', kind: 'number' },
    { tab: 'browser', id: 'chkEnableBrowserInstallNag', prop: 'EnableBrowserInstallNag', kind: 'toggle', options: 'browserInstallNagOptions' },

    { tab: 'login', id: 'txtLoginNagThreshold', prop: 'LoginNagThreshold', kind: 'number' },
    { tab: 'login', id: 'selectLoginNagTimeWindow', prop: 'LoginNagTimeWindow', kind: 'select' },
    { tab: 'login', id: 'txtLoginNagMessage', prop: 'LoginNagMessage', kind: 'text' },
    { tab: 'login', id: 'chkUseStickyLoginNagMessages', prop: 'UseStickyLoginNagMessages', kind: 'checkbox' },
    { tab: 'login', id: 'chkEnableLoginNag', prop: 'EnableLoginNag', kind: 'checkbox' },

    { tab: 'limit', id: 'txtTranscodeLimitThreshold', prop: 'TranscodeLimitThreshold', kind: 'number' },
    { tab: 'limit', id: 'txtTranscodeLimitHeader', prop: 'TranscodeLimitHeader', kind: 'text' },
    { tab: 'limit', id: 'txtTranscodeLimitMessage', prop: 'TranscodeLimitMessage', kind: 'text' },
    { tab: 'limit', id: 'chkUseStickyTranscodeLimitMessages', prop: 'UseStickyTranscodeLimitMessages', kind: 'checkbox' },
    { tab: 'limit', id: 'chkEnableTranscodeLimit', prop: 'EnableTranscodeLimit', kind: 'toggle', options: 'transcodeLimitOptions' },

    { tab: 'simultaneous', id: 'txtMaxConcurrentTranscodes', prop: 'MaxConcurrentTranscodes', kind: 'number' },
    { tab: 'simultaneous', id: 'txtConcurrentTranscodeLimitHeader', prop: 'ConcurrentTranscodeLimitHeader', kind: 'text' },
    { tab: 'simultaneous', id: 'txtConcurrentTranscodeLimitMessage', prop: 'ConcurrentTranscodeLimitMessage', kind: 'text' },
    { tab: 'simultaneous', id: 'chkUseStickyConcurrentTranscodeLimitMessages', prop: 'UseStickyConcurrentTranscodeLimitMessages', kind: 'checkbox' },
    { tab: 'simultaneous', id: 'chkEnableConcurrentTranscodeLimit', prop: 'EnableConcurrentTranscodeLimit', kind: 'toggle', options: 'concurrentTranscodeLimitOptions' },

    { tab: 'gpu', id: 'txtGpuIndex', prop: 'GpuIndex', kind: 'number' },
    { tab: 'gpu', id: 'txtGpuCheckTimeoutMs', prop: 'GpuCheckTimeoutMilliseconds', kind: 'number' },
    { tab: 'gpu', id: 'txtNvidiaSmiPath', prop: 'NvidiaSmiPath', kind: 'text' },
    { tab: 'gpu', id: 'txtGpuGuardDeniedHeader', prop: 'GpuGuardDeniedHeader', kind: 'text' },
    { tab: 'gpu', id: 'txtGpuGuardDeniedMessage', prop: 'GpuGuardDeniedMessage', kind: 'text' },
    { tab: 'gpu', id: 'chkUseStickyGpuGuardMessages', prop: 'UseStickyGpuGuardMessages', kind: 'checkbox' },
    { tab: 'gpu', id: 'chkEnableGpuResourceGuard', prop: 'EnableGpuResourceGuard', kind: 'toggle', options: 'gpuGuardOptions' },

    { tab: 'reaper', id: 'txtPausedTranscodeTimeoutMinutes', prop: 'PausedTranscodeTimeoutMinutes', kind: 'number' },
    { tab: 'reaper', id: 'txtPausedTranscodeWarningMinutes', prop: 'PausedTranscodeWarningMinutes', kind: 'number' },
    { tab: 'reaper', id: 'txtPausedTranscodeWarningHeader', prop: 'PausedTranscodeWarningHeader', kind: 'text' },
    { tab: 'reaper', id: 'txtPausedTranscodeWarningMessage', prop: 'PausedTranscodeWarningMessage', kind: 'text' },
    { tab: 'reaper', id: 'chkUseStickyPausedTranscodeMessages', prop: 'UseStickyPausedTranscodeMessages', kind: 'checkbox' },
    { tab: 'reaper', id: 'chkReapPausedDirectPlay', prop: 'ReapPausedDirectPlay', kind: 'checkbox' },
    { tab: 'reaper', id: 'chkEnablePausedTranscodeReaper', prop: 'EnablePausedTranscodeReaper', kind: 'toggle', options: 'pausedReaperOptions' },

    { tab: 'motd', id: 'txtMotdMessage', prop: 'MotdMessage', kind: 'text' },
    { tab: 'motd', id: 'chkUseStickyMotdMessages', prop: 'UseStickyMotdMessages', kind: 'checkbox' },
    { tab: 'motd', id: 'txtMotdIncludedClientPatterns', prop: 'MotdIncludedClientPatterns', kind: 'patterns' },
    { tab: 'motd', id: 'txtMotdExcludedClientPatterns', prop: 'MotdExcludedClientPatterns', kind: 'patterns' },
    { tab: 'motd', id: 'chkEnableMotd', prop: 'EnableMotd', kind: 'toggle', options: 'motdOptions' }
];

// Settings the page edits with something other than a single field. Each has its own test.
export const COMPOSITE_SETTINGS = {
    AlertTranscodeReasons: 'playback',
    ReasonMessageOverrides: 'playback',
    UserConcurrentTranscodeLimits: 'simultaneous'
};

// The three user exclusion lists, each edited in its own dialog and saved by that dialog.
export const EXCLUSION_DIALOGS = [
    {
        prop: 'ExcludedUserIds',
        tab: 'playback',
        open: 'btnManageExclusions',
        modal: 'userExclusionModal',
        list: 'userExclusionList',
        selectAll: 'btnUsersSelectAll',
        reset: 'btnUsersResetDefaults',
        clearAll: 'btnUsersClearAll',
        save: 'btnSaveExclusions',
        cancel: 'btnCancelExclusions',
        savedMessage: count => `${count} user(s) excluded from nag messages.`
    },
    {
        prop: 'MotdExcludedUserIds',
        tab: 'motd',
        open: 'btnManageMotdExclusions',
        modal: 'motdUserExclusionModal',
        list: 'motdUserExclusionList',
        selectAll: 'btnMotdUsersSelectAll',
        reset: 'btnMotdUsersResetDefaults',
        clearAll: 'btnMotdUsersClearAll',
        save: 'btnSaveMotdExclusions',
        cancel: 'btnCancelMotdExclusions',
        savedMessage: count => `${count} user(s) excluded from the MOTD.`
    },
    {
        prop: 'PausedTranscodeExcludedUserIds',
        tab: 'reaper',
        open: 'btnManagePausedTranscodeExclusions',
        modal: 'pausedTranscodeUserExclusionModal',
        list: 'pausedTranscodeUserExclusionList',
        selectAll: 'btnPausedTranscodeUsersSelectAll',
        reset: 'btnPausedTranscodeUsersResetDefaults',
        clearAll: 'btnPausedTranscodeUsersClearAll',
        save: 'btnSavePausedTranscodeExclusions',
        cancel: 'btnCancelPausedTranscodeExclusions',
        savedMessage: count => `${count} user(s) excluded from the paused transcode reaper.`
    }
];
