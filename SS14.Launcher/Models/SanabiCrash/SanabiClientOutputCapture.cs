using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;

namespace SS14.Launcher.Models.SanabiCrash;

/// <summary>
///     Drains a process's redirected stdout/stderr, keeping the last lines for a crash report and optionally a copy
///         on disk. Always keeps reading past every limit: a redirected process stalls once its pipe fills up.
/// </summary>
public sealed class SanabiClientOutputCapture
{
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const int DefaultTailLines = 200;

    private readonly object _lock = new();
    private readonly Queue<string> _tail = new();
    private readonly int _maxTailLines;
    private readonly bool _logLines;
    private readonly int _processId;
    private readonly long _maxFileBytes;

    /// <summary>
    ///     Completes once both streams hit EOF.
    /// </summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    private SanabiClientOutputCapture(int processId, int maxTailLines, bool logLines, long maxFileBytes)
    {
        _processId = processId;
        _maxTailLines = maxTailLines;
        _logLines = logLines;
        _maxFileBytes = maxFileBytes;
    }

    /// <param name="process">A started process with both streams redirected.</param>
    /// <param name="stdoutPath">File to mirror stdout to, or null for none.</param>
    /// <param name="stderrPath">File to mirror stderr to, or null for none.</param>
    /// <param name="logLines">Also write every line to the launcher log.</param>
    /// <param name="maxTailLines">How many of the last lines to keep in memory.</param>
    public static SanabiClientOutputCapture Start(
        Process process,
        string? stdoutPath,
        string? stderrPath,
        bool logLines = false,
        int maxTailLines = DefaultTailLines)
    {
        return Start(process.Id, process.StandardOutput, process.StandardError, stdoutPath, stderrPath, logLines, maxTailLines, MaxFileBytes);
    }

    public static SanabiClientOutputCapture Start(
        int processId,
        TextReader stdout,
        TextReader stderr,
        string? stdoutPath,
        string? stderrPath,
        bool logLines = false,
        int maxTailLines = DefaultTailLines,
        long maxFileBytes = MaxFileBytes)
    {
        var capture = new SanabiClientOutputCapture(processId, maxTailLines, logLines, maxFileBytes);
        capture.Completion = Task.WhenAll(
            Task.Run(() => capture.PumpAsync(stdout, stdoutPath, "")),
            Task.Run(() => capture.PumpAsync(stderr, stderrPath, "[stderr] ")));

        return capture;
    }

    public IReadOnlyList<string> GetTail()
    {
        lock (_lock)
        {
            return _tail.ToList();
        }
    }

    /// <summary>
    ///     Gives the pipes a moment to deliver what a dead process left in them. Bounded because anything the
    ///         process spawned can inherit the pipe and hold it open long after the process itself is gone.
    /// </summary>
    public async Task WaitForDrainAsync()
    {
        await Task.WhenAny(Completion, Task.Delay(TimeSpan.FromSeconds(1)));
    }

    private async Task PumpAsync(TextReader reader, string? path, string tailPrefix)
    {
        var writer = OpenWriter(path);
        var written = 0L;

        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                AddToTail(tailPrefix + line);

                if (_logLines)
                    Log.Information("piped: {Content}", line);

                if (writer == null)
                    continue;

                written += line.Length + 1;
                if (written <= _maxFileBytes)
                {
                    await writer.WriteLineAsync(line);
                    continue;
                }

                await writer.WriteLineAsync($"[log truncated: over {_maxFileBytes} bytes]");
                await writer.DisposeAsync();
                writer = null;
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            Log.Debug(e, "Output pipe for process {ProcessId} ended abruptly", _processId);
        }
        finally
        {
            if (writer != null)
                await writer.DisposeAsync();
        }

        Log.Debug("EOF, ending pipe logging for {ProcessId}", _processId);
    }

    private void AddToTail(string line)
    {
        lock (_lock)
        {
            _tail.Enqueue(line);
            while (_tail.Count > _maxTailLines)
                _tail.Dequeue();
        }
    }

    private static StreamWriter? OpenWriter(string? path)
    {
        if (path == null)
            return null;

        try
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(stream) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A second client has this open on some platforms. The in-memory tail still works, so keep draining.
            Log.Warning(e, "Failed to open client log {Path}", path);
            return null;
        }
    }
}
