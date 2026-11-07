using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Sanabi.Framework.Game.Managers;

namespace SS14.Launcher.Tests;

[TestFixture]
[TestOf(typeof(AssemblyLoadingManager))]
public sealed class SanabiModLifecycleTest
{
    private static readonly MethodInfo Init = typeof(FakeEntryPoint).GetMethod(nameof(FakeEntryPoint.Init))!;
    private static readonly MethodInfo Update = typeof(FakeEntryPoint).GetMethod(nameof(FakeEntryPoint.Update))!;

    [Test]
    public void ThrowingModDoesNotStopTheOthers()
    {
        var first = new FakeEntryPoint();
        var broken = new BrokenEntryPoint();
        var last = new FakeEntryPoint();
        var entryPoints = new List<object> { first, broken, last };

        AssemblyLoadingManager.InvokeDetachedEntryPoints(entryPoints, Init);

        Assert.Multiple(() =>
        {
            Assert.That(first.Calls, Is.EqualTo(1));
            Assert.That(broken.Calls, Is.EqualTo(1));
            Assert.That(last.Calls, Is.EqualTo(1));
        });
    }

    // Update runs every frame, so a mod that stayed in the list would fail and log on each one.
    [Test]
    public void ThrowingModIsNeverCalledAgain()
    {
        var healthy = new FakeEntryPoint();
        var broken = new BrokenEntryPoint();
        var entryPoints = new List<object> { broken, healthy };

        AssemblyLoadingManager.InvokeDetachedEntryPoints(entryPoints, Init);
        AssemblyLoadingManager.InvokeDetachedEntryPoints(entryPoints, Init);

        Assert.Multiple(() =>
        {
            Assert.That(entryPoints, Is.EqualTo(new List<object> { healthy }));
            Assert.That(broken.Calls, Is.EqualTo(1));
            Assert.That(healthy.Calls, Is.EqualTo(2));
        });
    }

    [Test]
    public void ArgumentsReachTheMod()
    {
        var entryPoint = new FakeEntryPoint();

        AssemblyLoadingManager.InvokeDetachedEntryPoints([entryPoint], Update, arguments: ["frame"]);

        Assert.That(entryPoint.LastArgument, Is.EqualTo("frame"));
    }

    public class FakeEntryPoint
    {
        public int Calls { get; private set; }
        public string? LastArgument { get; private set; }

        public virtual void Init() => Calls++;

        public void Update(string argument) => LastArgument = argument;
    }

    // The engine calls GameShared's virtual methods, so the override has to be what runs.
    private sealed class BrokenEntryPoint : FakeEntryPoint
    {
        public override void Init()
        {
            base.Init();
            throw new DllNotFoundException("kernel32.dll");
        }
    }
}
