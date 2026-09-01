using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TranscodeGuard.Gpu;

/// <summary>
/// One admission result and, when needed, its temporary in-flight VRAM reservation.
/// </summary>
internal readonly record struct GpuAdmissionAttempt(bool IsAdmitted, GpuAdmissionReservation? Reservation)
{
    internal static GpuAdmissionAttempt AllowedWithoutReservation => new(true, null);

    internal static GpuAdmissionAttempt Denied => new(false, null);
}

/// <summary>
/// Holds a conservative VRAM requirement until a launched FFmpeg process has allocated it.
/// </summary>
internal sealed class GpuAdmissionReservation : IDisposable
{
    private GpuResourceGuard? _owner;
    private readonly string _key;
    private readonly long _reservationId;

    internal GpuAdmissionReservation(GpuResourceGuard owner, string key, long reservationId)
    {
        _owner = owner;
        _key = key;
        _reservationId = reservationId;
    }

    /// <summary>
    /// Marks FFmpeg as launched. The budget remains reserved briefly while nvidia-smi catches up.
    /// </summary>
    internal Task MarkLaunched(int? processId = null, GpuTranscodeRequest? request = null)
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner == null)
        {
            return Task.CompletedTask;
        }

        owner.CompleteReservation(_key, _reservationId, launched: true);
        if (processId is > 0 && request != null)
        {
            return owner.BeginProcessObservation(_key, _reservationId, processId.Value, request);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.CompleteReservation(_key, _reservationId, launched: false);
    }
}
