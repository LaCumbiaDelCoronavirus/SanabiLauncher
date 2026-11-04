using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using Sanabi.Framework.Data;
using Sanabi.Framework.Game.Managers;
using Sanabi.Framework.Game.Patches;
using Sanabi.Framework.Proxy;
using Serilog;
using SS14.Launcher.Models.EngineManager;
using SS14.Launcher.Models.SanabiCrash;

namespace SS14.Launcher.Models;

/// <summary>
///     Watches the launched client and works out what happened to it. The macOS release launches through
///         <c>open</c>, which hands the game to the system and exits, so <see cref="Process"/> there is not the
///         game. The lifeline pipe is what tracks it instead.
/// </summary>
public partial class Connector
{
    /// <summary>
    ///     How long to wait for a game launched through <c>open</c> before calling it dead. Comfortably over
    ///         <see cref="IpcManager.ObjectPipeConnectTimeoutMs"/>, so a game stuck on the config pipe reports itself first.
    /// </summary>
    private static readonly TimeSpan OpenLaunchTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     How long the overlay stays up for a game with a real process handle that hasn't reached the lifeline yet.
    ///         Going past this isn't a failure: the process itself can still tell us what happens.
    /// </summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     macOS writes crash reports a moment after the process dies.
    /// </summary>
    private static readonly TimeSpan MacCrashReportDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    ///     A client log can be stamped slightly before our own clock read, with coarse filesystem timestamps.
    /// </summary>
    private static readonly TimeSpan LogTimestampSlack = TimeSpan.FromSeconds(1);

    private static readonly string HostTracePath = Path.Combine(LauncherPaths.DirLogs, "test-launch-host-trace.txt");

    private SanabiClientFailure? _clientFailure;
    private SanabiLaunchRequest? _lastLaunch;

    /// <summary>
    ///     What went wrong with the client, or null if nothing did (yet). Set before
    ///         <see cref="ConnectionStatus.ClientExited"/> is.
    /// </summary>
    public SanabiClientFailure? ClientFailure
    {
        get => _clientFailure;
        private set => this.RaiseAndSetIfChanged(ref _clientFailure, value);
    }

    private sealed record SanabiLaunchRequest(
        ContentLaunchInfo LaunchInfo,
        List<string> Args,
        List<(string, string)> Env,
        SanabiProxyEndpoint? GameProxy,
        bool CompatMode,
        bool ContentBundle);

    /// <summary>
    ///     The state of one attempt to run a client, which can span two processes if <c>open</c> fails.
    /// </summary>
    private sealed class ClientSession : IDisposable
    {
        private readonly Stopwatch _runtime = Stopwatch.StartNew();

        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public TimeSpan Runtime => _runtime.Elapsed;

        public SanabiClientLifeline Lifeline { get; } = new();
        public CancellationTokenSource ConfigPipeCancel { get; } = new();
        public CancellationTokenSource CrashPipeCancel { get; } = new();
        public Task<string?>? CrashReport { get; set; }

        public bool ViaOpen { get; set; }
        public string? Route { get; set; }
        public int? OpenExitCode { get; set; }
        public SanabiClientOutputCapture? OpenOutput { get; set; }
        public SanabiClientOutputCapture? ClientOutput { get; set; }
        public Exception? StartException { get; set; }

        public void Dispose()
        {
            Lifeline.Dispose();
            ConfigPipeCancel.Cancel();
            ConfigPipeCancel.Dispose();
        }
    }

    /// <summary>
    ///     Stands up everything the client talks to. Only once per session: a direct launch after a failed
    ///         <c>open</c> takes over the pipes that were waiting for the first attempt.
    /// </summary>
    private void StartClientPipes(ClientSession session, SanabiProxyEndpoint? gameProxy, PatchRunLevel? patchRunLevelOverride)
    {
        if (session.CrashReport != null)
            return;

        var sanabiConfig = new SanabiConfig().Configure(_cfg);
        sanabiConfig.GameProxy = gameProxy;
        sanabiConfig.LifelinePipeName = session.Lifeline.PipeName;

        if (patchRunLevelOverride is { } patchRunLevel)
            sanabiConfig.PatchRunLevel = patchRunLevel;

        _ = ServeConfigAsync(sanabiConfig, session.ConfigPipeCancel.Token);

        session.CrashReport = IpcManager.RunStringPipeServer(IpcManager.SanabiCrashIpcName, session.CrashPipeCancel.Token);
    }

    private static async Task ServeConfigAsync(SanabiConfig config, CancellationToken cancel)
    {
        try
        {
            await IpcManager.RunObjectPipeServer(IpcManager.SanabiIpcName, config, SanabiJsonContext.Default.SanabiConfig, cancel);
        }
        catch (OperationCanceledException)
        {
            // The client never asked for it, and the launch is over.
        }
        catch (Exception e)
        {
            // Not fatal here: the client's own connect timeout turns this into a crash report if it matters.
            Log.Warning(e, "Failed to serve config to client");
        }
    }

    private void CaptureClientOutput(ClientSession session, Process process)
    {
        if (session.ViaOpen)
        {
            // The game's own output goes to the client log via the app bundle's script, as it isn't our child.
            // What this catches is `open` itself explaining why it couldn't start it.
            session.OpenOutput = SanabiClientOutputCapture.Start(process, stdoutPath: null, stderrPath: null, logLines: true);
            return;
        }

        session.ClientOutput = SanabiClientOutputCapture.Start(
            process,
            LauncherPaths.PathClientStdoutLog,
            LauncherPaths.PathClientStderrLog);
    }

    private async Task<SanabiClientFailure?> WatchClientAsync(Process? process, ClientSession session, CancellationToken cancel)
    {
        if (process == null)
            return Failure(SanabiClientFailureKind.LaunchFailed, session, exitCode: null);

        if (session.ViaOpen)
        {
            await WaitForAnyAsync(cancel, OpenLaunchTimeout, process.WaitForExitAsync(cancel), session.Lifeline.Connected);

            if (!process.HasExited || process.ExitCode == 0)
                return await WatchLifelineAsync(session, cancel);

            session.OpenExitCode = process.ExitCode;
            Log.Warning("`open` failed with exit code {ExitCode}, starting the game directly instead", process.ExitCode);

            if (session.OpenOutput is { } openOutput)
                await openOutput.WaitForDrainAsync();

            process = await StartDirectFallbackAsync(session);

            if (process == null)
                return Failure(SanabiClientFailureKind.LaunchFailed, session, session.OpenExitCode);
        }

        return await WatchProcessAsync(process, session, cancel);
    }

    private async Task<Process?> StartDirectFallbackAsync(ClientSession session)
    {
        if (_lastLaunch is not { } launch)
            return null;

        try
        {
            var process = await LaunchClient(launch.LaunchInfo, launch.Args, launch.Env, launch.GameProxy, session, direct: true);
            session.Route = "direct, after `open` failed";
            return process;
        }
        catch (Exception e)
        {
            Log.Error(e, "Exception while starting client directly");
            session.StartException = e;
            return null;
        }
    }

    /// <summary>
    ///     Follows a game only known through the lifeline, once <c>open</c> has reported success.
    /// </summary>
    private async Task<SanabiClientFailure?> WatchLifelineAsync(ClientSession session, CancellationToken cancel)
    {
        var lifeline = session.Lifeline;
        await WaitForAnyAsync(cancel, OpenLaunchTimeout, lifeline.Connected);

        if (!lifeline.Connected.IsCompleted)
        {
            Log.Error("Game never connected after being started with `open`");
            return Failure(SanabiClientFailureKind.NeverStarted, session, exitCode: null);
        }

        if (!await lifeline.Connected)
        {
            // Our side broke, so there's no way to tell how the game is doing. Don't invent a crash.
            Log.Warning("Lifeline unavailable, can't track the game");
            Status = ConnectionStatus.ClientRunning;
            return null;
        }

        Status = ConnectionStatus.ClientRunning;

        var result = await lifeline.Completed.WaitAsync(cancel);
        Log.Information("Game lifeline ended: {Outcome}, exit code {ExitCode}", result.Outcome, result.ExitCode);

        return result is { Outcome: SanabiLifelineOutcome.Exited, ExitCode: 0 }
            ? null
            : Failure(SanabiClientFailureKind.Crashed, session, result.ExitCode);
    }

    /// <summary>
    ///     Follows a game whose process we own, which is everything except a successful <c>open</c>.
    /// </summary>
    private async Task<SanabiClientFailure?> WatchProcessAsync(Process process, ClientSession session, CancellationToken cancel)
    {
        var waitClient = process.WaitForExitAsync(cancel);
        await WaitForAnyAsync(cancel, StartupGrace, waitClient, session.Lifeline.Connected);

        if (!process.HasExited)
        {
            Status = ConnectionStatus.ClientRunning;
            await waitClient;
        }

        var exitCode = process.ExitCode;
        Log.Information("Game process exited with code {ExitCode}", exitCode);

        if (session.ClientOutput is { } output)
            await output.WaitForDrainAsync();

        if (exitCode == 0)
            return null;

        var reachedLoader = session.Lifeline.Connected is { IsCompleted: true, Result: true };
        return Failure(
            reachedLoader ? SanabiClientFailureKind.Crashed : SanabiClientFailureKind.ExitedDuringStartup,
            session,
            exitCode);
    }

    private static SanabiClientFailure Failure(SanabiClientFailureKind kind, ClientSession session, int? exitCode)
    {
        return new SanabiClientFailure(kind, exitCode, session.Runtime, session.StartException);
    }

    private static async Task WaitForAnyAsync(CancellationToken cancel, TimeSpan timeout, params Task[] tasks)
    {
        await Task.WhenAny([.. tasks, Task.Delay(timeout, cancel)]);
        cancel.ThrowIfCancellationRequested();
    }

    /// <summary>
    ///     Stops listening for a crash report from the client process and publishes whatever is known. If one may be
    ///         in flight, gives it a short grace period to arrive before giving up, so this can never hang forever
    ///         waiting for a client that will never connect (e.g. a native crash).
    /// </summary>
    private async Task FinalizeCrashReport(ClientSession session, SanabiClientFailure? failure, string? engineVersion)
    {
        try
        {
            var gameReport = await CollectGameReportAsync(session, waitForReport: failure != null);

            // A report over IPC is a crash, even if the process somehow went on to exit with 0.
            if (failure == null && gameReport != null)
                failure = Failure(SanabiClientFailureKind.Crashed, session, exitCode: null);

            if (failure == null)
                return;

            var report = await BuildCrashReportAsync(session, failure, gameReport, engineVersion);

            Log.Error("Client failure report:\n{Report}", report);
            SanabiCrashReportBuilder.Save(LauncherPaths.DirLogs, "crash", report);

            CrashReport = report;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to finalize client crash report");
        }
    }

    private static async Task<string?> CollectGameReportAsync(ClientSession session, bool waitForReport)
    {
        if (session.CrashReport is not { } crashReport)
            return null;

        if (waitForReport)
            await Task.WhenAny(crashReport, Task.Delay(TimeSpan.FromSeconds(2)));

        session.CrashPipeCancel.Cancel();

        try
        {
            return await crashReport;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed waiting for client crash report");
            return null;
        }
        finally
        {
            session.CrashPipeCancel.Dispose();
        }
    }

    private async Task<string> BuildCrashReportAsync(
        ClientSession session,
        SanabiClientFailure failure,
        string? gameReport,
        string? engineVersion)
    {
        (string Name, string Content)? nativeReport = null;
        if (OperatingSystem.IsMacOS())
        {
            if (gameReport == null)
                await Task.Delay(MacCrashReportDelay);

            nativeReport = SanabiCrashReportBuilder.FindMacCrashReport(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "DiagnosticReports"),
                session.StartedUtc);
        }

        var clientOutput = session.ClientOutput?.GetTail()
                           ?? SanabiCrashReportBuilder.ReadLogTail(LauncherPaths.PathClientMacLog, session.StartedUtc - LogTimestampSlack);

        return SanabiCrashReportBuilder.Build(new SanabiCrashReportInput
        {
            Failure = failure,
            GameReport = gameReport,
            EngineVersion = engineVersion,
            LaunchRoute = session.Route,
            OpenExitCode = session.OpenExitCode,
            OpenOutput = session.OpenOutput?.GetTail() ?? [],
            ClientOutput = clientOutput,
            NativeCrashReportName = nativeReport?.Name,
            NativeCrashReport = nativeReport?.Content,
            Secrets = GetReportSecrets()
        });
    }

    private List<string> GetReportSecrets()
    {
        var secrets = new List<string> { Environment.UserName };

        if (_loginManager.ActiveAccount is { } account)
        {
            secrets.Add(account.Username);
            secrets.Add(account.LoginInfo.Token.Token);
        }

        return secrets;
    }

    private static ProcessStartInfo GetMacDirectStartInfo(string appPath)
    {
        // The launcher is <launcher>.app/Contents/Resources/<arch>/bin, and has its own runtime next to it.
        var dotnetRoot = Path.GetFullPath(Path.Combine(LauncherPaths.DirLauncherInstall, "..", "dotnet"));
        var dotnetHost = Path.Combine(dotnetRoot, "dotnet");
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x86_64";

        EnsureExecutable(dotnetHost);

        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetHost,
            WorkingDirectory = Path.Combine(appPath, "Contents", "MacOS"),
            ArgumentList = { Path.Combine(appPath, "Contents", "Resources", arch, "bin", "SS14.Loader.dll") }
        };

        startInfo.EnvironmentVariables["DOTNET_ROOT"] = dotnetRoot;
        return startInfo;
    }

    /// <summary>
    ///     Checks the install, then starts the game with everything captured to find out why it won't run. Offline
    ///         on purpose: no login token and no server address, so this can't touch a game server, and it respects
    ///         the proxy profile like any other launch.
    /// </summary>
    public async Task<SanabiTestLaunchResult> RunTestLaunchAsync(IProgress<SanabiTestLaunchStage> progress, CancellationToken cancel)
    {
        progress.Report(SanabiTestLaunchStage.Checks);

        var launch = _lastLaunch;
        var enginePath = launch == null ? null : GetEnginePath(launch.LaunchInfo);
        var checks = await SanabiLaunchDiagnostics.RunChecksAsync(enginePath, cancel);

        if (launch == null)
            return FinishTestLaunch(SanabiTestLaunchVerdict.NothingToTest, checks, null, null, []);

        bool? openWorks = null;
        string? openOutput = null;

        var openStartInfo = await GetLoaderStartInfo(direct: false);
        if (openStartInfo.FileName == "open")
            (openWorks, openOutput) = await SanabiLaunchDiagnostics.ProbeOpenAsync(openStartInfo, cancel);

        try
        {
            await EnsureProxyAllowedAsync(cancel);
        }
        catch (ConnectException)
        {
            return FinishTestLaunch(SanabiTestLaunchVerdict.ProxyBlocked, checks, openWorks, openOutput, []);
        }

        var runs = new List<SanabiTestLaunchRun>();

        progress.Report(SanabiTestLaunchStage.TestLaunch);
        var run = await RunTestClientAsync("Test launch", launch, patchRunLevelOverride: null, cancel);
        runs.Add(run);

        SanabiTestLaunchRun? runWithoutPatches = null;
        if (!run.Passed && new SanabiConfig().Configure(_cfg).PatchRunLevel != PatchRunLevel.None)
        {
            progress.Report(SanabiTestLaunchStage.TestLaunchWithoutPatches);
            runWithoutPatches = await RunTestClientAsync("Test launch without patches", launch, PatchRunLevel.None, cancel);
            runs.Add(runWithoutPatches);
        }

        var verdict = SanabiLaunchDiagnostics.DetermineVerdict(openWorks, run, runWithoutPatches);
        return FinishTestLaunch(verdict, checks, openWorks, openOutput, runs);
    }

    private SanabiTestLaunchResult FinishTestLaunch(
        SanabiTestLaunchVerdict verdict,
        string checks,
        bool? openWorks,
        string? openOutput,
        List<SanabiTestLaunchRun> runs)
    {
        var hostTrace = runs.Any(r => !r.Passed)
            ? SanabiCrashReportBuilder.ReadLogTail(HostTracePath, DateTime.MinValue)
            : [];

        var report = SanabiLaunchDiagnostics.BuildReport(verdict, checks, openWorks, openOutput, runs, hostTrace, GetReportSecrets());
        var savedPath = SanabiCrashReportBuilder.Save(LauncherPaths.DirLogs, "test-launch", report);

        Log.Information("Test launch finished: {Verdict}, report at {Path}", verdict, savedPath);
        return new SanabiTestLaunchResult(verdict, report, savedPath);
    }

    private async Task<SanabiTestLaunchRun> RunTestClientAsync(
        string name,
        SanabiLaunchRequest launch,
        PatchRunLevel? patchRunLevelOverride,
        CancellationToken cancel)
    {
        using var session = new ClientSession();
        Process? process = null;

        try
        {
            process = await LaunchClient(
                launch.LaunchInfo,
                GetTestLaunchArgs(launch),
                GetTestLaunchEnv(patchRunLevelOverride),
                _proxyManager.ActiveProfile?.Game,
                session,
                direct: true,
                patchRunLevelOverride);

            await Task.WhenAny(process!.WaitForExitAsync(cancel), Task.Delay(SanabiLaunchDiagnostics.SurvivalTime, cancel));
            cancel.ThrowIfCancellationRequested();

            // Exiting 0 by itself means someone closed the test window, which only a working game lets you do.
            var stillRunning = !process.HasExited;
            var exitCode = stillRunning ? (int?)null : process.ExitCode;

            if (stillRunning)
                process.Kill(entireProcessTree: true);

            if (session.ClientOutput is { } output)
                await output.WaitForDrainAsync();

            return new SanabiTestLaunchRun(
                name,
                Passed: exitCode is null or 0,
                exitCode,
                ReachedLoader: session.Lifeline.Connected is { IsCompleted: true, Result: true },
                session.Runtime,
                await CollectGameReportAsync(session, waitForReport: exitCode is not (null or 0)),
                session.ClientOutput?.GetTail() ?? []);
        }
        catch (OperationCanceledException)
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);

            throw;
        }
        catch (Exception e)
        {
            Log.Error(e, "Test launch failed to start");
            return new SanabiTestLaunchRun(
                name,
                Passed: false,
                ExitCode: null,
                ReachedLoader: false,
                session.Runtime,
                e.ToString(),
                session.ClientOutput?.GetTail() ?? []);
        }
    }

    private static List<string> GetTestLaunchArgs(SanabiLaunchRequest launch)
    {
        var args = new List<string>
        {
            "--username", ConfigConstants.FallbackUsername,
            "--cvar", $"display.compat={launch.CompatMode}",
            "--cvar", "launch.launcher=true"
        };

        if (launch.ContentBundle)
        {
            args.Add("--cvar");
            args.Add("launch.content_bundle=true");
        }

        return args;
    }

    private static List<(string, string)> GetTestLaunchEnv(PatchRunLevel? patchRunLevelOverride)
    {
        var env = new List<(string, string)>
        {
            ("COREHOST_TRACE", "1"),
            ("COREHOST_TRACE_VERBOSITY", "4"),
            ("COREHOST_TRACEFILE", HostTracePath)
        };

        if (patchRunLevelOverride == null)
            env.Add(("HARMONY_DEBUG", "1"));

        return env;
    }

    private string? GetEnginePath(ContentLaunchInfo launchInfo)
    {
        var engineVersion = launchInfo.ModuleInfo.SingleOrDefault(x => x.Module == "Robust").Version;
        return engineVersion == null ? null : _engineManager.GetEnginePath(engineVersion);
    }
}
