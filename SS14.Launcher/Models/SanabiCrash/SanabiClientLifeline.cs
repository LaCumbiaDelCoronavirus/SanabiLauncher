using System;
using System.Threading;
using System.Threading.Tasks;
using Sanabi.Framework.Game.Managers;
using Serilog;

namespace SS14.Launcher.Models.SanabiCrash;

public enum SanabiLifelineOutcome
{
    /// <summary>The game never connected, so it never reached the Sanabi entry point.</summary>
    NeverConnected,

    /// <summary>The pipe closed without an exit message: the process died.</summary>
    Lost,

    /// <summary>The game reported its exit code on the way out.</summary>
    Exited,

    /// <summary>The launcher side failed, which says nothing about the game.</summary>
    Unavailable
}

public readonly record struct SanabiLifelineResult(SanabiLifelineOutcome Outcome, int? ExitCode = null);

/// <summary>
///     The launcher's end of the pipe the game holds open for its whole life. See
///         <see cref="IpcManager.RunLifelinePipeServer"/>. Start before the client so it can't miss the connection.
/// </summary>
public sealed class SanabiClientLifeline : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string PipeName { get; }

    /// <summary>
    ///     Completes with true once the game connects, or false if it never will.
    /// </summary>
    public Task<bool> Connected => _connected.Task;

    /// <summary>
    ///     Completes when the game is gone, or on <see cref="Dispose"/>. Never faults.
    /// </summary>
    public Task<SanabiLifelineResult> Completed { get; }

    public SanabiClientLifeline(string? pipeName = null)
    {
        PipeName = pipeName ?? $"sanabi{Guid.NewGuid():N}";
        Completed = RunAsync();
    }

    private async Task<SanabiLifelineResult> RunAsync()
    {
        try
        {
            var exitCode = await IpcManager.RunLifelinePipeServer(
                PipeName,
                () => _connected.TrySetResult(true),
                _cancel.Token);

            return exitCode is { } code
                ? new SanabiLifelineResult(SanabiLifelineOutcome.Exited, code)
                : new SanabiLifelineResult(SanabiLifelineOutcome.Lost);
        }
        catch (OperationCanceledException)
        {
            return new SanabiLifelineResult(Connected is { IsCompleted: true, Result: true }
                ? SanabiLifelineOutcome.Lost
                : SanabiLifelineOutcome.NeverConnected);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Client lifeline failed");
            return new SanabiLifelineResult(SanabiLifelineOutcome.Unavailable);
        }
        finally
        {
            _connected.TrySetResult(false);
        }
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _cancel.Dispose();
    }
}
