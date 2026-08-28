namespace Jellyfin.Plugin.TranscodeNag.Tests;

/// <summary>
/// A fact that is reported as skipped on Windows, for tests that need a POSIX shell to stand in
/// for an external tool. Skipping keeps them visible rather than silently passing.
/// </summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Requires a POSIX shell to stand in for nvidia-smi.";
        }
    }
}
