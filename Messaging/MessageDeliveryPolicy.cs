using System;

namespace Jellyfin.Plugin.TranscodeGuard.Messaging;

/// <summary>
/// Defines the fixed compatibility behavior used by opt-in sticky messages.
/// </summary>
internal static class MessageDeliveryPolicy
{
    internal const int StickyMessageTimeoutMs = 4000;
    internal const int StickyMessageSendCount = 3;
    internal const int StickyMessageIntervalMs = 3000;

    internal static TimeSpan GetStickyRefreshDelay(int sendNumber)
    {
        if (sendNumber < 2 || sendNumber > StickyMessageSendCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sendNumber));
        }

        // Widen before multiplying so the offset is computed in double rather than
        // overflowing int and then being converted (cs/loss-of-precision).
        return TimeSpan.FromMilliseconds((sendNumber - 1) * (double)StickyMessageIntervalMs);
    }

    internal static int GetEffectiveVisibilityDurationMs(bool useStickyMessages, int configuredTimeoutMs)
    {
        if (!useStickyMessages)
        {
            return configuredTimeoutMs;
        }

        return StickyMessageTimeoutMs
            + ((StickyMessageSendCount - 1) * StickyMessageIntervalMs);
    }
}
