using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SS14.Launcher.Models.SanabiCrash;

public enum SanabiTestLaunchStage
{
    Checks,
    TestLaunch,
    TestLaunchWithoutPatches
}

public enum SanabiTestLaunchVerdict
{
    /// <summary>The game ran, by every route tried.</summary>
    Passed,

    /// <summary>The game runs when started directly, but macOS refuses to start it through <c>open</c>.</summary>
    OpenBroken,

    /// <summary>The game only runs with patching off.</summary>
    PatchesBroken,

    /// <summary>The game fails however it's started.</summary>
    GameBroken,

    /// <summary>The active proxy profile is down, and nothing may connect while it is.</summary>
    ProxyBlocked,

    /// <summary>No launch got far enough to know what to start.</summary>
    NothingToTest
}

/// <param name="Name">What this run was, for the report.</param>
/// <param name="Passed">The game stayed up for the whole test, or chose to exit cleanly.</param>
/// <param name="ExitCode">Null if the game was still running and got stopped by the test.</param>
/// <param name="ReachedLoader">The game got as far as the Sanabi entry point.</param>
public sealed record SanabiTestLaunchRun(
    string Name,
    bool Passed,
    int? ExitCode,
    bool ReachedLoader,
    TimeSpan Runtime,
    string? GameReport,
    IReadOnlyList<string> Output);

public sealed record SanabiTestLaunchResult(SanabiTestLaunchVerdict Verdict, string Report, string? SavedPath);

/// <summary>
///     The parts of a test launch that don't need a <see cref="Connector"/>: inspecting the install and writing
///         up what was found. Nothing here may stop the rest from running, so failures become report lines.
/// </summary>
public static class SanabiLaunchDiagnostics
{
    /// <summary>
    ///     How long a test client has to stay up to count as working.
    /// </summary>
    public static readonly TimeSpan SurvivalTime = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    public static SanabiTestLaunchVerdict DetermineVerdict(
        bool? openWorks,
        SanabiTestLaunchRun? run,
        SanabiTestLaunchRun? runWithoutPatches)
    {
        if (run == null)
            return SanabiTestLaunchVerdict.GameBroken;

        if (!run.Passed)
        {
            return runWithoutPatches is { Passed: true }
                ? SanabiTestLaunchVerdict.PatchesBroken
                : SanabiTestLaunchVerdict.GameBroken;
        }

        return openWorks == false ? SanabiTestLaunchVerdict.OpenBroken : SanabiTestLaunchVerdict.Passed;
    }

    public static async Task<string> RunChecksAsync(string? enginePath, CancellationToken cancel)
    {
        var text = new StringBuilder();

        text.AppendLine($"Launcher: {ConfigConstants.CurrentLauncherVersion}");
        text.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        text.AppendLine($"Launcher process: {RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}");
        text.AppendLine($"Engine zip: {DescribeFile(enginePath)}");
        text.AppendLine($"Free disk space: {DescribeFreeSpace()}");

        if (OperatingSystem.IsMacOS())
            await AppendMacChecksAsync(text, cancel);

        return text.ToString();
    }

    private static async Task AppendMacChecksAsync(StringBuilder text, CancellationToken cancel)
    {
        // DirLauncherInstall is <launcher>.app/Contents/Resources/<arch>/bin.
        var launcherApp = Path.GetFullPath(Path.Combine(LauncherPaths.DirLauncherInstall, "..", "..", "..", ".."));
        var gameApp = Path.Combine(launcherApp, "Contents", "Resources", "Space Station 14.app");
        var stub = Path.Combine(gameApp, "Contents", "MacOS", "SS14");
        var dotnetHost = Path.GetFullPath(Path.Combine(LauncherPaths.DirLauncherInstall, "..", "dotnet", "dotnet"));

        text.AppendLine($"Rosetta: {await RunAsync("sysctl", ["-n", "sysctl.proc_translated"], cancel)}");
        text.AppendLine($"macOS version: {await RunAsync("sw_vers", ["-productVersion"], cancel)}");
        text.AppendLine($"Machine: {await RunAsync("uname", ["-m"], cancel)}");
        text.AppendLine($"DOTNET_ROOT: {Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "(unset)"}");
        text.AppendLine($"Game app: {DescribeFile(gameApp)}");
        text.AppendLine($"Game stub: {await RunAsync("ls", ["-l", stub], cancel)}");
        text.AppendLine($"dotnet host: {await RunAsync("ls", ["-l", dotnetHost], cancel)}");

        var attributes = await RunAsync("xattr", ["-r", "-l", launcherApp], cancel);
        var quarantined = attributes.Split('\n').Where(l => l.Contains("com.apple.quarantine")).ToList();
        text.AppendLine($"Quarantined files: {quarantined.Count}");
        foreach (var line in quarantined.Take(5))
        {
            text.AppendLine($"  {line.Trim()}");
        }

        // Only the process IDs: the full command lines carry the engine arguments.
        var running = await RunAsync("pgrep", ["-f", "SS14.Loader"], cancel);
        text.AppendLine($"Game processes already running: {(string.IsNullOrWhiteSpace(running) ? "none" : running)}");

        text.AppendLine();
        text.AppendLine("--- codesign (dotnet host) ---");
        text.AppendLine(await RunAsync("codesign", ["-dv", dotnetHost], cancel));
        text.AppendLine("--- spctl (game app) ---");
        text.AppendLine(await RunAsync("spctl", ["-a", "-vv", gameApp], cancel));
    }

    /// <summary>
    ///     Asks <c>open</c> to start the game with no arguments. The loader just prints its usage and quits, so this
    ///         tests only whether macOS will launch the bundle.
    /// </summary>
    public static async Task<(bool Works, string Output)> ProbeOpenAsync(ProcessStartInfo openStartInfo, CancellationToken cancel)
    {
        var startInfo = new ProcessStartInfo(openStartInfo.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in openStartInfo.ArgumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync(cancel);
            var stderr = process.StandardError.ReadToEndAsync(cancel);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(CommandTimeout);
            await process.WaitForExitAsync(timeout.Token);

            return (process.ExitCode == 0, $"exit code {process.ExitCode}\n{await stdout}{await stderr}".TrimEnd());
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            return (false, $"could not run: {e.GetType().Name}: {e.Message}");
        }
    }

    public static string BuildReport(
        SanabiTestLaunchVerdict verdict,
        string checks,
        bool? openWorks,
        string? openOutput,
        IEnumerable<SanabiTestLaunchRun> runs,
        IReadOnlyList<string> hostTrace,
        IReadOnlyCollection<string> secrets)
    {
        var text = new StringBuilder();
        text.AppendLine($"Test launch verdict: {verdict}");
        text.AppendLine();
        text.AppendLine(checks.TrimEnd());

        if (openWorks != null)
        {
            text.AppendLine();
            text.AppendLine($"--- `open` test: {(openWorks.Value ? "works" : "FAILED")} ---");
            text.AppendLine(openOutput);
        }

        foreach (var run in runs)
        {
            text.AppendLine();
            text.AppendLine($"--- {run.Name}: {(run.Passed ? "passed" : "FAILED")} ---");
            text.AppendLine($"Exit code: {(run.ExitCode is { } code ? SanabiCrashReportBuilder.DescribeExitCode(code) : "still running, stopped by test")}");
            text.AppendLine($"Reached loader: {run.ReachedLoader}");
            text.AppendLine($"Runtime: {run.Runtime.TotalSeconds:F1}s");

            if (!string.IsNullOrWhiteSpace(run.GameReport))
            {
                text.AppendLine("Reported by the game:");
                text.AppendLine(run.GameReport.TrimEnd());
            }

            text.AppendLine("Output:");
            text.AppendLine(run.Output.Count > 0 ? string.Join('\n', run.Output) : "(none)");
        }

        if (hostTrace.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("--- .NET host trace (last lines) ---");
            text.AppendLine(string.Join('\n', hostTrace));
        }

        return SanabiCrashReportBuilder.Redact(text.ToString(), secrets);
    }

    private static string DescribeFile(string? path)
    {
        if (path == null)
            return "(unknown)";

        if (Directory.Exists(path))
            return $"{path} (directory)";

        return File.Exists(path) ? $"{path} ({new FileInfo(path).Length} bytes)" : $"{path} (MISSING)";
    }

    private static string DescribeFreeSpace()
    {
        try
        {
            return $"{new DriveInfo(LauncherPaths.DirUserData).AvailableFreeSpace / (1024 * 1024)} MiB";
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return $"unknown ({e.Message})";
        }
    }

    private static async Task<string> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancel)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync(cancel);
            var stderr = process.StandardError.ReadToEndAsync(cancel);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(CommandTimeout);
            await process.WaitForExitAsync(timeout.Token);

            return $"{await stdout}{await stderr}".Trim();
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            return $"could not run {fileName}: {e.GetType().Name}: {e.Message}";
        }
    }
}
