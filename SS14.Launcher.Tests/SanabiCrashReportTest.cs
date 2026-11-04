using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using SS14.Launcher.Models.SanabiCrash;

namespace SS14.Launcher.Tests;

[TestFixture]
[TestOf(typeof(SanabiCrashReportBuilder))]
public sealed class SanabiCrashReportTest
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"sanabi-crash-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Test]
    [TestCase(139, false, "139 (SIGSEGV)")]
    [TestCase(134, false, "134 (SIGABRT)")]
    [TestCase(137, false, "137 (SIGKILL)")]
    [TestCase(1, false, "1")]
    [TestCase(200, false, "200")]
    [TestCase(-1073741819, true, "0xC0000005 (STATUS_ACCESS_VIOLATION)")]
    [TestCase(-532462766, true, "0xE0434352 (CLR_EXCEPTION)")]
    [TestCase(-2146232797, true, "0x80131623 (COR_E_FAILFAST)")]
    [TestCase(-1, true, "0xFFFFFFFF")]
    [TestCase(3, true, "3")]
    public void DescribesExitCodes(int exitCode, bool isWindows, string expected)
    {
        Assert.That(SanabiCrashReportBuilder.DescribeExitCode(exitCode, isWindows), Is.EqualTo(expected));
    }

    [Test]
    public void RedactsSecretsAndHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = $"{home}/Library/x user=ExampleUser token=abc.def.ghi";

        var redacted = SanabiCrashReportBuilder.Redact(text, ["exampleuser", "abc.def.ghi"]);

        Assert.That(redacted, Is.EqualTo("~/Library/x user=[redacted] token=[redacted]"));
    }

    [Test]
    public void ShortSecretsDoNotShredTheReport()
    {
        Assert.That(SanabiCrashReportBuilder.Redact("a stack trace", ["a"]), Is.EqualTo("a stack trace"));
    }

    [Test]
    public void BuiltReportNeverContainsSecrets()
    {
        var report = SanabiCrashReportBuilder.Build(new SanabiCrashReportInput
        {
            Failure = new SanabiClientFailure(SanabiClientFailureKind.Crashed, 139, TimeSpan.FromSeconds(4)),
            GameReport = "game saw token-secret",
            OpenOutput = ["open saw token-secret"],
            ClientOutput = ["client saw token-secret"],
            NativeCrashReportName = "dotnet-1.ips",
            NativeCrashReport = "native saw token-secret",
            Secrets = ["token-secret"]
        });

        Assert.Multiple(() =>
        {
            Assert.That(report, Does.Not.Contain("token-secret"));
            Assert.That(report, Does.Contain("game saw [redacted]"));
            Assert.That(report, Does.Contain("open saw [redacted]"));
            Assert.That(report, Does.Contain("client saw [redacted]"));
            Assert.That(report, Does.Contain("native saw [redacted]"));
        });
    }

    [Test]
    public void ReportIncludesExitCodeAndOpenFailure()
    {
        var report = SanabiCrashReportBuilder.Build(new SanabiCrashReportInput
        {
            Failure = new SanabiClientFailure(SanabiClientFailureKind.LaunchFailed, 1, TimeSpan.FromSeconds(1)),
            OpenExitCode = 1,
            OpenOutput = ["The application cannot be opened for an unexpected reason"]
        });

        Assert.That(report, Does.Contain("`open` exit code: 1"));
        Assert.That(report, Does.Contain("cannot be opened"));
    }

    [Test]
    public void ReadLogTailKeepsOnlyTheLastLines()
    {
        var path = Path.Combine(_directory, "client.log");
        File.WriteAllLines(path, Enumerable.Range(0, 10).Select(i => $"line {i}"));

        var tail = SanabiCrashReportBuilder.ReadLogTail(path, DateTime.MinValue, maxLines: 3);

        Assert.That(tail, Is.EqualTo(new[] { "line 7", "line 8", "line 9" }));
    }

    [Test]
    public void ReadLogTailIgnoresLogsFromAnEarlierLaunch()
    {
        var path = Path.Combine(_directory, "client.log");
        File.WriteAllText(path, "output of the previous launch");

        var launchStarted = DateTime.UtcNow.AddMinutes(1);

        Assert.That(SanabiCrashReportBuilder.ReadLogTail(path, launchStarted), Is.Empty);
    }

    [Test]
    public void FindsNewestMacCrashReportFromThisLaunchOnly()
    {
        var launchStarted = DateTime.UtcNow.AddMinutes(-1);

        WriteCrashReport("dotnet-old.ips", "old", launchStarted.AddHours(-1));
        WriteCrashReport("dotnet-first.ips", "first", launchStarted.AddSeconds(5));
        WriteCrashReport("dotnet-newest.ips", "newest", launchStarted.AddSeconds(10));
        WriteCrashReport("Safari-1.ips", "unrelated", launchStarted.AddSeconds(20));

        var found = SanabiCrashReportBuilder.FindMacCrashReport(_directory, launchStarted);

        Assert.That(found, Is.EqualTo(("dotnet-newest.ips", "newest")));
    }

    [Test]
    public void NoMacCrashReportFromThisLaunchFindsNothing()
    {
        var launchStarted = DateTime.UtcNow.AddMinutes(-1);
        WriteCrashReport("dotnet-old.ips", "old", launchStarted.AddHours(-1));

        Assert.That(SanabiCrashReportBuilder.FindMacCrashReport(_directory, launchStarted), Is.Null);
    }

    [Test]
    public void SavePrunesOldReports()
    {
        for (var i = 0; i < 5; i++)
        {
            var old = Path.Combine(_directory, $"crash-old{i}.txt");
            File.WriteAllText(old, "old");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1 - i));
        }

        var saved = SanabiCrashReportBuilder.Save(_directory, "crash", "report", keep: 2);

        Assert.That(File.ReadAllText(saved!), Is.EqualTo("report"));
        Assert.That(Directory.GetFiles(_directory, "crash-*.txt"), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task OutputCaptureKeepsDrainingPastItsLimits()
    {
        var path = Path.Combine(_directory, "stdout.log");
        var lines = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"line {i:D3}"));

        var capture = SanabiClientOutputCapture.Start(
            processId: 1,
            new StringReader(lines),
            new StringReader(""),
            path,
            stderrPath: null,
            logLines: false,
            maxTailLines: 3,
            maxFileBytes: 50);

        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(capture.GetTail(), Is.EqualTo(new[] { "line 097", "line 098", "line 099" }), "stopped reading at a limit");

            var file = File.ReadAllText(path);
            Assert.That(file, Does.Contain("line 000"));
            Assert.That(file, Does.Contain("[log truncated"));
            Assert.That(file, Does.Not.Contain("line 099"));
        });
    }

    [Test]
    public async Task OutputCaptureMarksStderr()
    {
        var capture = SanabiClientOutputCapture.Start(
            processId: 1,
            new StringReader("regular output"),
            new StringReader("native failure"),
            stdoutPath: null,
            stderrPath: null);

        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(capture.GetTail(), Is.EquivalentTo(new[] { "regular output", "[stderr] native failure" }));
    }

    [Test]
    public async Task OutputCaptureRecordsARealProcess()
    {
        // The real loader, not a stand-in: run with no arguments it prints usage and exits 1, the same way the launcher
        // would see any early failure. Runs through `dotnet` since that's how the macOS bundle starts it.
        var startInfo = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { LocateLoader() },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)!;
        var capture = SanabiClientOutputCapture.Start(process, stdoutPath: null, stderrPath: null);

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await capture.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(process.ExitCode, Is.EqualTo(1));
        Assert.That(capture.GetTail(), Has.Some.Contains("Usage: SS14.Loader"));
    }

    [Test]
    [TestCase(true, true, null, SanabiTestLaunchVerdict.Passed)]
    [TestCase(null, true, null, SanabiTestLaunchVerdict.Passed)]
    [TestCase(false, true, null, SanabiTestLaunchVerdict.OpenBroken)]
    [TestCase(true, false, null, SanabiTestLaunchVerdict.GameBroken)]
    [TestCase(false, false, null, SanabiTestLaunchVerdict.GameBroken)]
    [TestCase(true, false, false, SanabiTestLaunchVerdict.GameBroken)]
    [TestCase(true, false, true, SanabiTestLaunchVerdict.PatchesBroken)]
    public void DeterminesVerdict(bool? openWorks, bool runPassed, bool? runWithoutPatchesPassed, SanabiTestLaunchVerdict expected)
    {
        var verdict = SanabiLaunchDiagnostics.DetermineVerdict(
            openWorks,
            MakeRun(runPassed),
            runWithoutPatchesPassed is { } passed ? MakeRun(passed) : null);

        Assert.That(verdict, Is.EqualTo(expected));
    }

    // en-US is the fallback, so a key missing from ru wouldn't fail anywhere: it would just quietly show English.
    [Test]
    public void CrashStringsExistInBothMaintainedLocales()
    {
        var locales = Path.Combine(FindRepositoryRoot(), "SS14.Launcher", "Assets", "Locale");

        var english = ReadCrashKeys(Path.Combine(locales, "en-US", "sanabi.ftl"));
        var russian = ReadCrashKeys(Path.Combine(locales, "ru", "sanabi.ftl"));

        Assert.That(english, Is.Not.Empty);
        Assert.That(russian, Is.EquivalentTo(english));
    }

    private static string[] ReadCrashKeys(string path)
    {
        return Regex
            .Matches(File.ReadAllText(path), @"^((?:crash-report|connecting-status-client)-[\w-]+) =", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToArray();
    }

    private static SanabiTestLaunchRun MakeRun(bool passed)
    {
        return new SanabiTestLaunchRun("run", passed, passed ? 0 : 139, ReachedLoader: true, TimeSpan.Zero, null, []);
    }

    private void WriteCrashReport(string name, string content, DateTime writtenUtc)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, writtenUtc);
    }

    private static string LocateLoader()
    {
        // The test runs from .../SS14.Launcher.Tests/bin/<cfg>/<tfm>/.
        var baseDirectory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        var targetFramework = baseDirectory.Name;
        var configuration = baseDirectory.Parent!.Name;

        return Directory
            .EnumerateFiles(
                Path.Combine(FindRepositoryRoot(), "SS14.Loader", "bin", configuration, targetFramework),
                "SS14.Loader.dll",
                SearchOption.AllDirectories)
            .First();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "SS14.Launcher.sln")))
        {
            directory = directory.Parent!;
        }

        return directory.FullName;
    }
}
