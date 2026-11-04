using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace SS14.Launcher.Models.SanabiCrash;

public enum SanabiClientFailureKind
{
    /// <summary>The launcher couldn't start anything at all.</summary>
    LaunchFailed,

    /// <summary>The game process ended before reaching the Sanabi entry point.</summary>
    ExitedDuringStartup,

    /// <summary>The launch was accepted, but the game never showed up.</summary>
    NeverStarted,

    /// <summary>The game was running, then ended badly.</summary>
    Crashed
}

public sealed record SanabiClientFailure(
    SanabiClientFailureKind Kind,
    int? ExitCode,
    TimeSpan Runtime,
    Exception? Exception = null);

/// <summary>
///     Everything known about a failed launch. All text is redacted by <see cref="SanabiCrashReportBuilder.Build"/>.
/// </summary>
public sealed record SanabiCrashReportInput
{
    public required SanabiClientFailure Failure { get; init; }
    public string? GameReport { get; init; }
    public string? EngineVersion { get; init; }
    public string? LaunchRoute { get; init; }
    public int? OpenExitCode { get; init; }
    public IReadOnlyList<string> OpenOutput { get; init; } = [];
    public IReadOnlyList<string> ClientOutput { get; init; } = [];
    public string? NativeCrashReportName { get; init; }
    public string? NativeCrashReport { get; init; }
    public IReadOnlyCollection<string> Secrets { get; init; } = [];
}

/// <summary>
///     Builds the text shown in the crash dialog. It's meant to be pasted to whoever is helping, so it's plain
///         English regardless of locale, and has personal details removed.
/// </summary>
public static class SanabiCrashReportBuilder
{
    private const int MaxNativeCrashReportBytes = 128 * 1024;

    // Below this, replacing a name would chew up unrelated words.
    private const int MinSecretLength = 3;

    private static readonly Dictionary<int, string> UnixSignals = new()
    {
        [1] = "SIGHUP",
        [2] = "SIGINT",
        [3] = "SIGQUIT",
        [4] = "SIGILL",
        [5] = "SIGTRAP",
        [6] = "SIGABRT",
        [8] = "SIGFPE",
        [9] = "SIGKILL",
        [10] = "SIGBUS",
        [11] = "SIGSEGV",
        [13] = "SIGPIPE",
        [15] = "SIGTERM"
    };

    private static readonly Dictionary<uint, string> WindowsStatuses = new()
    {
        [0xC0000005] = "STATUS_ACCESS_VIOLATION",
        [0xC000001D] = "STATUS_ILLEGAL_INSTRUCTION",
        [0xC0000017] = "STATUS_NO_MEMORY",
        [0xC0000094] = "STATUS_INTEGER_DIVIDE_BY_ZERO",
        [0xC00000FD] = "STATUS_STACK_OVERFLOW",
        [0xC0000135] = "STATUS_DLL_NOT_FOUND",
        [0xC0000139] = "STATUS_ENTRYPOINT_NOT_FOUND",
        [0xC0000142] = "STATUS_DLL_INIT_FAILED",
        [0xC0000374] = "STATUS_HEAP_CORRUPTION",
        [0xC0000409] = "STATUS_STACK_BUFFER_OVERRUN",
        [0xC000013A] = "STATUS_CONTROL_C_EXIT",
        [0xE0434352] = "CLR_EXCEPTION",
        [0x80131623] = "COR_E_FAILFAST"
    };

    /// <summary>
    ///     Turns an exit code into something searchable. Uses the OS's own names, which don't need translating.
    /// </summary>
    public static string DescribeExitCode(int exitCode, bool? isWindows = null)
    {
        if (isWindows ?? OperatingSystem.IsWindows())
        {
            var status = unchecked((uint)exitCode);
            if (WindowsStatuses.TryGetValue(status, out var statusName))
                return $"0x{status:X8} ({statusName})";

            return exitCode < 0 ? $"0x{status:X8}" : exitCode.ToString();
        }

        // Both the .NET Process class and shells report death-by-signal as 128 + the signal number.
        return exitCode > 128 && UnixSignals.TryGetValue(exitCode - 128, out var signalName)
            ? $"{exitCode} ({signalName})"
            : exitCode.ToString();
    }

    public static string Build(SanabiCrashReportInput input)
    {
        var failure = input.Failure;
        var text = new StringBuilder();

        text.AppendLine($"Failure: {DescribeKind(failure.Kind)}");
        text.AppendLine($"Exit code: {(failure.ExitCode is { } code ? DescribeExitCode(code) : "unknown")}");
        text.AppendLine($"Runtime: {failure.Runtime.TotalSeconds:F1}s");
        text.AppendLine($"Launcher: {ConfigConstants.CurrentLauncherVersion}");
        text.AppendLine($"Engine: {input.EngineVersion ?? "unknown"}");
        text.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        text.AppendLine($"Launcher process: {RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}");
        text.AppendLine($"Launched via: {input.LaunchRoute ?? "unknown"}");

        if (input.OpenExitCode is { } openExitCode)
            text.AppendLine($"`open` exit code: {openExitCode}");

        if (failure.Exception is { } exception)
            AppendSection(text, "Launcher exception", exception.ToString());

        if (!string.IsNullOrWhiteSpace(input.GameReport))
            AppendSection(text, "Reported by the game", input.GameReport);

        if (input.OpenOutput.Count > 0)
            AppendSection(text, "`open` output", string.Join('\n', input.OpenOutput));

        AppendSection(text, "Game output (last lines)", input.ClientOutput.Count > 0
            ? string.Join('\n', input.ClientOutput)
            : "(none captured: the game may not have started)");

        if (input.NativeCrashReport is { } nativeReport)
            AppendSection(text, $"macOS crash report ({input.NativeCrashReportName})", nativeReport);

        return Redact(text.ToString(), input.Secrets);
    }

    private static string DescribeKind(SanabiClientFailureKind kind)
    {
        return kind switch
        {
            SanabiClientFailureKind.LaunchFailed => "the launcher could not start the game",
            SanabiClientFailureKind.ExitedDuringStartup => "the game exited while starting",
            SanabiClientFailureKind.NeverStarted => "the launch was accepted, but the game never started",
            SanabiClientFailureKind.Crashed => "the game crashed while running",
            _ => kind.ToString()
        };
    }

    private static void AppendSection(StringBuilder text, string title, string content)
    {
        text.AppendLine();
        text.AppendLine($"--- {title} ---");
        text.AppendLine(content.TrimEnd());
    }

    /// <summary>
    ///     Removes the home directory and each of <paramref name="secrets"/>. Home goes first, since it usually
    ///         contains the OS username.
    /// </summary>
    public static string Redact(string text, IEnumerable<string> secrets)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            text = text.Replace(home, "~", StringComparison.OrdinalIgnoreCase);

        foreach (var secret in secrets.Where(s => s.Length >= MinSecretLength).OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    /// <summary>
    ///     Reads the last lines of a log file, or nothing if it predates this launch: the file is only replaced once
    ///         the game starts, so a game that never ran would otherwise be blamed on an older run's output.
    /// </summary>
    public static IReadOnlyList<string> ReadLogTail(string path, DateTime notBeforeUtc, int maxLines = SanabiClientOutputCapture.DefaultTailLines)
    {
        try
        {
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < notBeforeUtc)
                return [];

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var lines = new Queue<string>(maxLines + 1);
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > maxLines)
                    lines.Dequeue();
            }

            return lines.ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Failed to read client log {Path}", path);
            return [];
        }
    }

    /// <summary>
    ///     Finds the crash report macOS wrote for the game, which covers native crashes that left nothing else
    ///         behind. The game runs as <c>dotnet</c>, so that's the name to match.
    /// </summary>
    public static (string Name, string Content)? FindMacCrashReport(string directory, DateTime notBeforeUtc)
    {
        try
        {
            if (!Directory.Exists(directory))
                return null;

            var newest = new DirectoryInfo(directory)
                .EnumerateFiles("dotnet*.ips")
                .Where(f => f.LastWriteTimeUtc >= notBeforeUtc)
                .MaxBy(f => f.LastWriteTimeUtc);

            if (newest == null)
                return null;

            using var stream = newest.OpenRead();
            var buffer = new byte[Math.Min(stream.Length, MaxNativeCrashReportBytes)];
            stream.ReadExactly(buffer);
            return (newest.Name, Encoding.UTF8.GetString(buffer));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Failed to read macOS crash reports");
            return null;
        }
    }

    /// <summary>
    ///     Writes a report next to the logs and prunes old ones, so reports can't pile up forever.
    /// </summary>
    /// <returns>The path written, or null if that failed. Losing the file must not lose the dialog.</returns>
    public static string? Save(string directory, string prefix, string report, int keep = 20)
    {
        try
        {
            var path = Path.Combine(directory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, report);

            var old = new DirectoryInfo(directory)
                .EnumerateFiles($"{prefix}-*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keep);

            foreach (var file in old)
            {
                file.Delete();
            }

            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Failed to save report to {Directory}", directory);
            return null;
        }
    }
}
