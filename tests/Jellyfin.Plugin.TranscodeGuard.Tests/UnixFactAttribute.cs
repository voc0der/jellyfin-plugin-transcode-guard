using System.Runtime.CompilerServices;

namespace Jellyfin.Plugin.TranscodeGuard.Tests;

/// <summary>
/// A fact that is reported as skipped on Windows, for tests that need a POSIX shell to stand in
/// for an external tool. Skipping keeps them visible rather than silently passing.
/// </summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Requires a POSIX shell to stand in for nvidia-smi.";
        }
    }
}
