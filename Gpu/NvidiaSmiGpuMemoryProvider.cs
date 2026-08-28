using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TranscodeNag.Gpu;

/// <summary>
/// Reads free VRAM by running nvidia-smi with a narrow machine-readable query.
/// </summary>
public sealed class NvidiaSmiGpuMemoryProvider : IGpuMemoryProvider, IDisposable
{
    private const string DefaultExecutable = "nvidia-smi";

    // Denied HLS segment requests arrive in bursts. Reusing a very recent reading keeps the guard
    // from spawning a process per retry while staying far shorter than any realistic VRAM swing.
    private static readonly TimeSpan DefaultCacheWindow = TimeSpan.FromSeconds(1);

    private readonly ILogger<NvidiaSmiGpuMemoryProvider> _logger;
    private readonly Func<string> _executablePathAccessor;
    private readonly TimeSpan _cacheWindow;
    private readonly SemaphoreSlim _queryLock = new(1, 1);
    private readonly object _cacheLock = new();

    private int _cachedGpuIndex = -1;
    private GpuMemoryQueryResult _cachedResult;
    private DateTimeOffset _cachedAtUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    public NvidiaSmiGpuMemoryProvider(ILogger<NvidiaSmiGpuMemoryProvider> logger)
        : this(logger, () => Plugin.Instance?.Configuration.NvidiaSmiPath ?? string.Empty, DefaultCacheWindow)
    {
    }

    internal NvidiaSmiGpuMemoryProvider(
        ILogger<NvidiaSmiGpuMemoryProvider> logger,
        Func<string> executablePathAccessor,
        TimeSpan cacheWindow)
    {
        _logger = logger;
        _executablePathAccessor = executablePathAccessor;
        _cacheWindow = cacheWindow;
    }

    /// <inheritdoc />
    public async Task<GpuMemoryQueryResult> GetFreeMemoryAsync(
        int gpuIndex,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        if (TryReadCache(gpuIndex, out var cached))
        {
            return cached;
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
            // A burst of concurrent requests collapses onto the first query's result.
            if (TryReadCache(gpuIndex, out cached))
            {
                return cached;
            }

            var result = await RunQueryAsync(gpuIndex, timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            WriteCache(gpuIndex, result);
            return result;
        }
        finally
        {
            _queryLock.Release();
        }
    }

    private bool TryReadCache(int gpuIndex, out GpuMemoryQueryResult result)
    {
        lock (_cacheLock)
        {
            if (_cachedGpuIndex == gpuIndex
                && _cacheWindow > TimeSpan.Zero
                && DateTimeOffset.UtcNow - _cachedAtUtc < _cacheWindow)
            {
                result = _cachedResult;
                return true;
            }
        }

        result = default;
        return false;
    }

    private void WriteCache(int gpuIndex, GpuMemoryQueryResult result)
    {
        lock (_cacheLock)
        {
            _cachedGpuIndex = gpuIndex;
            _cachedResult = result;
            _cachedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private async Task<GpuMemoryQueryResult> RunQueryAsync(
        int gpuIndex,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var configuredPath = _executablePathAccessor();
        var executable = string.IsNullOrWhiteSpace(configuredPath) ? DefaultExecutable : configuredPath.Trim();

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // ArgumentList avoids any shell or quoting interpretation of these values.
        startInfo.ArgumentList.Add("--query-gpu=index,memory.free");
        startInfo.ArgumentList.Add("--format=csv,noheader,nounits");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(Math.Max(1, timeoutMilliseconds));

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return GpuMemoryQueryResult.Failed($"{executable} could not be started");
            }
        }
        catch (Win32Exception ex)
        {
            // Covers "not found" and "permission denied" for the executable itself.
            return GpuMemoryQueryResult.Failed($"{executable} is not available ({ex.Message})");
        }
        catch (InvalidOperationException ex)
        {
            return GpuMemoryQueryResult.Failed($"{executable} could not be started ({ex.Message})");
        }
        catch (PlatformNotSupportedException ex)
        {
            return GpuMemoryQueryResult.Failed($"{executable} cannot be started on this platform ({ex.Message})");
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
                return GpuMemoryQueryResult.Failed(
                    $"{executable} exited with code {process.ExitCode}{(stderr.Length == 0 ? string.Empty : ": " + FirstLine(stderr))}");
            }

            if (!NvidiaSmiOutputParser.TryGetFreeMiB(stdout, gpuIndex, out var freeMiB))
            {
                return GpuMemoryQueryResult.Failed($"{executable} did not report a usable value for GPU {gpuIndex}");
            }

            return GpuMemoryQueryResult.FromFreeMiB(freeMiB);
        }
        catch (OperationCanceledException)
        {
            return GpuMemoryQueryResult.Failed(
                cancellationToken.IsCancellationRequested
                    ? "the request was cancelled before the GPU could be queried"
                    : $"{executable} did not respond within {timeoutMilliseconds} ms");
        }
        catch (InvalidOperationException ex)
        {
            return GpuMemoryQueryResult.Failed($"{executable} could not be read ({ex.Message})");
        }
        catch (IOException ex)
        {
            return GpuMemoryQueryResult.Failed($"{executable} could not be read ({ex.Message})");
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
            _logger.LogDebug(ex, "Unable to terminate {Executable}", executable);
        }
        catch (Win32Exception ex)
        {
            _logger.LogDebug(ex, "Unable to terminate {Executable}", executable);
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
}
