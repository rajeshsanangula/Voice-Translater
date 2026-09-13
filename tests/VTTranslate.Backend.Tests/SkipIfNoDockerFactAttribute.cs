using System.Diagnostics;

namespace VTTranslate.Backend.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that self-skips (rather than failing the whole suite)
/// when no working Docker daemon is available — the real-PostgreSQL integration tests in
/// <see cref="EfPostgresPersistenceTests"/> need one (via Testcontainers) and this
/// environment's actual Docker availability is not something this test suite controls.
/// Skipping (not silently passing, not failing red) is the honest representation of
/// "this test genuinely could not run here."
/// </summary>
public sealed class SkipIfNoDockerFactAttribute : FactAttribute
{
    public SkipIfNoDockerFactAttribute()
    {
        if (!DockerProbe.IsAvailable.Value)
            Skip = "Docker is not available in this environment — real-PostgreSQL integration tests require a working Docker daemon.";
    }
}

internal static class DockerProbe
{
    public static readonly Lazy<bool> IsAvailable = new(Probe);

    private static bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "version --format {{.Server.Version}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null) return false;
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch
        {
            return false; // docker executable not found, or any other environment failure
        }
    }
}
