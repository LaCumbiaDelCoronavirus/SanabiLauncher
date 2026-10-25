#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

namespace SS14.Launcher.Tests;

/// <summary>
///     Drives the SS14.Launcher.StealthProbe "fake game" as a subprocess and checks that Sanabi stays hidden.
///     It has to be a subprocess: the harness applies AssemblyHidingManager.PatchDetectionVectors(), which hooks
///     process-wide runtime methods (AppDomain.GetAssemblies, Type.Assembly, ...) that would corrupt this test
///     host. The harness builds first via a build-only ProjectReference in the .csproj.
/// </summary>
[TestFixture]
public sealed class StealthHarnessTest
{
    private const int TimeoutMs = 60_000;

    /// <summary>Hiding ON: no detection vector may reveal Sanabi, so the harness must exit 0.</summary>
    [Test]
    public void HidesSanabiFromAllProbes()
    {
        var (exitCode, stdout, stderr) = RunHarness(argument: null);
        TestContext.Out.Write(stdout);

        Assert.That(exitCode, Is.EqualTo(0),
            $"The stealth harness found Sanabi through a detection vector:\n{stdout}\n{stderr}");
    }

    /// <summary>
    ///     Negative control (the §3 "prove a test can fail" example): with hiding OFF the probes MUST detect
    ///     Sanabi, otherwise they assert nothing. The harness exits 0 only when it did detect it.
    /// </summary>
    [Test]
    public void DetectsSanabiWhenHidingDisabled()
    {
        var (exitCode, stdout, stderr) = RunHarness(argument: "--expect-visible");
        TestContext.Out.Write(stdout);

        Assert.That(exitCode, Is.EqualTo(0),
            $"The probes failed to detect Sanabi with hiding disabled — they are not proving anything:\n{stdout}\n{stderr}");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunHarness(string? argument)
    {
        var harnessDll = LocateHarnessDll();

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(harnessDll)!,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(harnessDll);
        if (argument is not null)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the stealth harness process.");

        // Read both streams asynchronously so a full pipe buffer can't deadlock against WaitForExit.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeoutMs))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The stealth harness did not exit within the timeout.");
        }

        Task.WaitAll(stdoutTask, stderrTask);
        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static string LocateHarnessDll()
    {
        const string configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif

        // The test runs from .../SS14.Launcher.Tests/bin/<cfg>/<tfm>/; reuse <tfm> and walk up to the repo root.
        var baseDirectory = AppContext.BaseDirectory;
        var targetFramework = new DirectoryInfo(baseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Name;

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SS14.Launcher.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not find SS14.Launcher.sln above the test output directory.");

        var harnessDll = Path.Combine(
            directory!.FullName,
            "SS14.Launcher.StealthProbe", "bin", configuration, targetFramework, "SS14.Launcher.StealthProbe.dll");

        Assert.That(File.Exists(harnessDll), Is.True,
            $"Stealth harness not built at expected path: {harnessDll}");

        return harnessDll;
    }
}
