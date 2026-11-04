using System;
using System.IO;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using NUnit.Framework;
using Sanabi.Framework.Data;
using Sanabi.Framework.Game.Managers;
using SS14.Launcher.Models.SanabiCrash;

namespace SS14.Launcher.Tests;

[TestFixture]
[TestOf(typeof(SanabiClientLifeline))]
public sealed class SanabiClientLifelineTest
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string UniquePipeName() => $"sanabitest{Guid.NewGuid():N}";

    [Test]
    public async Task ReportedExitCodeArrives()
    {
        using var lifeline = new SanabiClientLifeline();

        await using (var client = IpcManager.RunLifelinePipeClient(lifeline.PipeName))
        {
            Assert.That(await lifeline.Connected.WaitAsync(Timeout), Is.True);
            IpcManager.WriteLifelineExit(client, 0);
        }

        var result = await lifeline.Completed.WaitAsync(Timeout);

        Assert.That(result, Is.EqualTo(new SanabiLifelineResult(SanabiLifelineOutcome.Exited, 0)));
    }

    [Test]
    public async Task NonZeroExitCodeSurvivesTheTrip()
    {
        using var lifeline = new SanabiClientLifeline();

        await using (var client = IpcManager.RunLifelinePipeClient(lifeline.PipeName))
        {
            IpcManager.WriteLifelineExit(client, 139);
        }

        var result = await lifeline.Completed.WaitAsync(Timeout);

        Assert.That(result, Is.EqualTo(new SanabiLifelineResult(SanabiLifelineOutcome.Exited, 139)));
    }

    // The negative control for the two above: going away without saying so has to be a crash, or a clean exit
    // and a dead game would look the same.
    [Test]
    public async Task DroppedPipeIsACrash()
    {
        using var lifeline = new SanabiClientLifeline();

        await using (IpcManager.RunLifelinePipeClient(lifeline.PipeName))
        {
            Assert.That(await lifeline.Connected.WaitAsync(Timeout), Is.True);
        }

        var result = await lifeline.Completed.WaitAsync(Timeout);

        Assert.That(result.Outcome, Is.EqualTo(SanabiLifelineOutcome.Lost));
    }

    [Test]
    public async Task NotConnectedWhileTheGameIsStillRunning()
    {
        using var lifeline = new SanabiClientLifeline();

        await using var client = IpcManager.RunLifelinePipeClient(lifeline.PipeName);
        await lifeline.Connected.WaitAsync(Timeout);

        await Task.Delay(100);

        Assert.That(lifeline.Completed.IsCompleted, Is.False);
    }

    [Test]
    public async Task GameThatNeverConnectsIsDistinguishable()
    {
        var lifeline = new SanabiClientLifeline();
        lifeline.Dispose();

        var result = await lifeline.Completed.WaitAsync(Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SanabiLifelineOutcome.NeverConnected));
            Assert.That(lifeline.Connected.Result, Is.False);
        });
    }

    [Test]
    public async Task ConfigPipeDeliversAndLetsTheClientFinish()
    {
        var pipeName = UniquePipeName();
        var config = new SanabiConfig { LifelinePipeName = "lifeline" };

        var server = IpcManager.RunObjectPipeServer(pipeName, config, SanabiJsonContext.Default.SanabiConfig);

        // The client reads until the server closes, so this only returns if the server gets that far. On Unix this
        // is the WaitForPipeDrain failure: the data arrives but the close never happens.
        var received = await Task
            .Run(() => IpcManager.RunObjectPipeClient(pipeName, SanabiJsonContext.Default.SanabiConfig))
            .WaitAsync(Timeout);

        await server.WaitAsync(Timeout);

        Assert.That(received.LifelinePipeName, Is.EqualTo("lifeline"));
    }

    [Test]
    public async Task CancelledConfigPipeFreesItsNameForTheNextLaunch()
    {
        var pipeName = UniquePipeName();
        var typeInfo = SanabiJsonContext.Default.SanabiConfig;

        using var cancel = new System.Threading.CancellationTokenSource();
        var abandoned = IpcManager.RunObjectPipeServer(pipeName, new SanabiConfig(), typeInfo, cancel.Token);

        // Mid-wait, which is when a real launch gets cancelled.
        await Task.Delay(500);
        await cancel.CancelAsync();

        Assert.That(async () => await abandoned.WaitAsync(Timeout), Throws.InstanceOf<OperationCanceledException>());

        var next = new SanabiConfig { LifelinePipeName = "next" };
        var server = IpcManager.RunObjectPipeServer(pipeName, next, typeInfo);

        var received = await Task.Run(() => IpcManager.RunObjectPipeClient(pipeName, typeInfo)).WaitAsync(Timeout);
        await server.WaitAsync(Timeout);

        Assert.That(received.LifelinePipeName, Is.EqualTo("next"));
    }

    [Test]
    public void ConfigClientGivesUpInsteadOfHanging()
    {
        Assert.That(
            () => IpcManager.RunObjectPipeClient(UniquePipeName(), SanabiJsonContext.Default.SanabiConfig, timeoutMs: 100),
            Throws.InstanceOf<TimeoutException>());
    }
}
