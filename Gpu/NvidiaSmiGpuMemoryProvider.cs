using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeGuard.Gpu;

/// <summary>
/// Reads free and per-process VRAM by running narrow, machine-readable nvidia-smi queries.
/// </summary>
public sealed class NvidiaSmiGpuMemoryProvider : IGpuMemoryProvider, IGpuProcessMemoryProvider, IDisposable
{
    private const string DefaultExecutable = "nvidia-smi";

    // Only FAILED lookups are ever reused, and only briefly.
    //
    // A successful reading decides whether a transcode launches, so it must always be fresh:
    // reusing one would hand two transcodes arriving close together the same pre-allocation
    // number and admit both - the contention this feature exists to prevent.
    //
    // A failed lookup cannot change any decision (the guard is fail-open either way), and it is
    // the expensive case: without this, a hung nvidia-smi would cost every queued admission the
    // full timeout in turn.
    private static readonly TimeSpan DefaultFailureCacheWindow = TimeSpan.FromSeconds(1);

    private readonly ILogger<NvidiaSmiGpuMemoryProvider> _logger;
    private readonly Func<string> _executablePathAccessor;
    private readonly TimeSpan _failureCacheWindow;
    private readonly SemaphoreSlim _queryLock = new(1, 1);
    private readonly object _cacheLock = new();

    private int _cachedFailureGpuIndex = -1;
    private GpuMemoryQueryResult _cachedFailure;
    private DateTimeOffset _cachedFailureAtUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    public NvidiaSmiGpuMemoryProvider(ILogger<NvidiaSmiGpuMemoryProvider> logger)
        : this(logger, () => Plugin.Instance?.Configuration.NvidiaSmiPath ?? string.Empty, DefaultFailureCacheWindow)
    {
    }

    internal NvidiaSmiGpuMemoryProvider(
        ILogger<NvidiaSmiGpuMemoryProvider> logger,
        Func<string> executablePathAccessor,
        TimeSpan failureCacheWindow)
    {
        _logger = logger;
        _executablePathAccessor = executablePathAccessor;
        _failureCacheWindow = failureCacheWindow;
    }

    /// <inheritdoc />
    public async Task<GpuMemoryQueryResult> GetFreeMemoryAsync(
        int gpuIndex,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (TryReadCachedFailure(gpuIndex, out var cachedFailure))
        {
            return cachedFailure;
        }

        try
        {
            await _queryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return GpuMemoryQueryResult.Failed("the request was cancelled before the GPU could be queried");
        }
        catch (ObjectDisposedException)
        {
            return GpuMemoryQueryResult.Failed("the plugin is shutting down");
        }

        try
        {
            // Re-check under the lock: a query that failed while this caller was queued spares it
            // from repeating the same doomed (and possibly slow) lookup.
            if (TryReadCachedFailure(gpuIndex, out cachedFailure))
            {
                return cachedFailure;
            }

            // Serialised, so concurrent admissions each take their own reading in turn rather
            // than spawning a process apiece.
            var result = await RunQueryAsync(gpuIndex, timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            CacheFailure(gpuIndex, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return GpuMemoryQueryResult.Failed("the request was cancelled before the GPU could be queried");
        }
        finally
        {
            _queryLock.Release();
        }
    }

    async Task<GpuProcessMemoryQueryResult> IGpuProcessMemoryProvider.GetUsedMemoryAsync(
        int processId,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (processId <= 0)
        {
            return GpuProcessMemoryQueryResult.Failed("the process ID is invalid");
        }

        try
        {
            await _queryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return GpuProcessMemoryQueryResult.Failed("the process-memory query was cancelled");
        }
        catch (ObjectDisposedException)
        {
            return GpuProcessMemoryQueryResult.Failed("the plugin is shutting down");
        }

        try
        {
            return await RunProcessQueryAsync(processId, timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return GpuProcessMemoryQueryResult.Failed("the process-memory query was cancelled");
        }
        finally
        {
            _queryLock.Release();
        }
    }

    private bool TryReadCachedFailure(int gpuIndex, out GpuMemoryQueryResult result)
    {
        lock (_cacheLock)
        {
            if (_cachedFailureGpuIndex == gpuIndex
                && _failureCacheWindow > TimeSpan.Zero
                && DateTimeOffset.UtcNow - _cachedFailureAtUtc < _failureCacheWindow)
            {
                result = _cachedFailure;
                return true;
            }
        }

        result = default;
        return false;
    }

    private void CacheFailure(int gpuIndex, GpuMemoryQueryResult result)
    {
        if (result.Success)
        {
            // A successful reading decides whether a transcode launches, so it is never reused.
            return;
        }

        lock (_cacheLock)
        {
            _cachedFailureGpuIndex = gpuIndex;
            _cachedFailure = result;
            _cachedFailureAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private async Task<GpuMemoryQueryResult> RunQueryAsync(
        int gpuIndex,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var configuredPath = _executablePathAccessor();
        var executable = string.IsNullOrWhiteSpace(configuredPath) ? DefaultExecutable : configuredPath.Trim();
        var startInfo = CreateStartInfo(executable);

        // ArgumentList avoids any shell or quoting interpretation of these values.
        startInfo.ArgumentList.Add("--query-gpu=index,memory.free");
        startInfo.ArgumentList.Add("--format=csv,noheader,nounits");

        var command = await RunCommandAsync(
            startInfo,
            executable,
            timeoutMilliseconds,
            cancellationToken).ConfigureAwait(false);

        if (!command.Success)
        {
            return GpuMemoryQueryResult.Failed(command.FailureReason!);
        }

        if (!NvidiaSmiOutputParser.TryGetFreeMiB(command.StandardOutput, gpuIndex, out var freeMiB))
        {
            return GpuMemoryQueryResult.Failed($"{executable} did not report a usable value for GPU {gpuIndex}");
        }

        return GpuMemoryQueryResult.FromFreeMiB(freeMiB);
    }

    private async Task<GpuProcessMemoryQueryResult> RunProcessQueryAsync(
        int processId,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var configuredPath = _executablePathAccessor();
        var executable = string.IsNullOrWhiteSpace(configuredPath) ? DefaultExecutable : configuredPath.Trim();
        var startInfo = CreateStartInfo(executable);
        startInfo.ArgumentList.Add("--query-compute-apps=pid,used_gpu_memory");
        startInfo.ArgumentList.Add("--format=csv,noheader,nounits");

        var command = await RunCommandAsync(
            startInfo,
            executable,
            timeoutMilliseconds,
            cancellationToken).ConfigureAwait(false);

        if (!command.Success)
        {
            return GpuProcessMemoryQueryResult.Failed(command.FailureReason!);
        }

        if (!NvidiaSmiOutputParser.TryGetProcessUsedMiB(command.StandardOutput, processId, out var usedMiB))
        {
            return GpuProcessMemoryQueryResult.Failed(
                $"{executable} did not report usable GPU memory for process {processId}");
        }

        return GpuProcessMemoryQueryResult.FromUsedMiB(usedMiB);
    }

    private static ProcessStartInfo CreateStartInfo(string executable)
        => new()
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

    private async Task<NvidiaSmiCommandResult> RunCommandAsync(
        ProcessStartInfo startInfo,
        string executable,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(Math.Max(1, timeoutMilliseconds));

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return NvidiaSmiCommandResult.Failed($"{executable} could not be started");
            }
        }
        catch (Win32Exception ex)
        {
            // Covers "not found" and "permission denied" for the executable itself.
            return NvidiaSmiCommandResult.Failed($"{executable} is not available ({ex.Message})");
        }
        catch (InvalidOperationException ex)
        {
            return NvidiaSmiCommandResult.Failed($"{executable} could not be started ({ex.Message})");
        }
        catch (PlatformNotSupportedException ex)
        {
            return NvidiaSmiCommandResult.Failed($"{executable} cannot be started on this platform ({ex.Message})");
        }

        // Both pipes must be drained or the child can block on a full buffer. They are started
        // before the wait and observed in the finally, so a timeout kill cannot leave a faulted
        // task behind unobserved.
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;

        try
        {
            stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);

            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

            if (process.ExitCode != 0)
            {
                return NvidiaSmiCommandResult.Failed(
                    $"{executable} exited with code {process.ExitCode}{(stderr.Length == 0 ? string.Empty : ": " + FirstLine(stderr))}");
            }

            return NvidiaSmiCommandResult.FromOutput(stdout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's request went away. Let this travel past the caching layer: it says
            // nothing about the GPU, and caching it would let the next admission skip its check.
            throw;
        }
        catch (OperationCanceledException)
        {
            // A genuine timeout. The free-memory caller caches this expensive failure; the
            // optional process-calibration caller simply reports it.
            return NvidiaSmiCommandResult.Failed($"{executable} did not respond within {timeoutMilliseconds} ms");
        }
        catch (InvalidOperationException ex)
        {
            return NvidiaSmiCommandResult.Failed($"{executable} could not be read ({ex.Message})");
        }
        catch (IOException ex)
        {
            return NvidiaSmiCommandResult.Failed($"{executable} could not be read ({ex.Message})");
        }
        finally
        {
            TryKill(process, executable);
            Observe(stdoutTask);
            Observe(stderrTask);
        }
    }

    /// <summary>
    /// Swallows the result of an abandoned read so a cancelled or broken pipe cannot surface
    /// as an unobserved task exception.
    /// </summary>
    /// <param name="task">The read task, or null if it was never started.</param>
    private static void Observe(Task? task)
    {
        if (task == null || task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void TryKill(Process process, string executable)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        catch (NotSupportedException ex)
        {
            BestEffort(() => _logger.LogDebug(ex, "Unable to terminate {Executable}", executable));
        }
        catch (Win32Exception ex)
        {
            BestEffort(() => _logger.LogDebug(ex, "Unable to terminate {Executable}", executable));
        }
    }

    private static void BestEffort(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Diagnostic logging cannot make an otherwise handled process failure escape.
        }
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOf('\n', StringComparison.Ordinal);
        return end < 0 ? text : text[..end].Trim();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queryLock.Dispose();
    }

    private readonly record struct NvidiaSmiCommandResult(
        bool Success,
        string? StandardOutput,
        string? FailureReason)
    {
        internal static NvidiaSmiCommandResult FromOutput(string output)
            => new(true, output, null);

        internal static NvidiaSmiCommandResult Failed(string reason)
            => new(false, null, reason);
    }
}
