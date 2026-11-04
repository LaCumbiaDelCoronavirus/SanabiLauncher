using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using JetBrains.Annotations;
using Serilog;
using SS14.Launcher.Localization;
using SS14.Launcher.Models.SanabiCrash;

namespace SS14.Launcher.Views;

public partial class CrashReportDialog : Window
{
    private readonly Func<IProgress<SanabiTestLaunchStage>, CancellationToken, Task<SanabiTestLaunchResult>>? _runTestLaunch;
    private readonly CancellationTokenSource _testLaunchCancel = new();
    private string _report;
    private bool _testLaunchRunning;

    // Required by the Avalonia XAML loader/previewer; never used at runtime otherwise.
    [UsedImplicitly]
    public CrashReportDialog() : this("", null)
    {
    }

    public CrashReportDialog(
        string report,
        Func<IProgress<SanabiTestLaunchStage>, CancellationToken, Task<SanabiTestLaunchResult>>? runTestLaunch)
    {
        _report = report;
        _runTestLaunch = runTestLaunch;
        InitializeComponent();

        ReportText.Text = report;
        TestLaunchButton.IsVisible = runTestLaunch != null;
    }

    /// <summary>
    ///     Shows a report that finished arriving after this window opened. Won't replace a running test launch.
    /// </summary>
    public void SetReport(string report)
    {
        if (_testLaunchRunning)
            return;

        _report = report;
        ReportText.Text = report;
    }

    public async void StartTestLaunch()
    {
        if (_runTestLaunch == null || _testLaunchRunning)
            return;

        _testLaunchRunning = true;
        TestLaunchButton.IsEnabled = false;
        TestLaunchStatus.IsVisible = true;

        var progress = new Progress<SanabiTestLaunchStage>(stage => TestLaunchStatus.Text = GetStageText(stage));

        try
        {
            ShowTestLaunchResult(await _runTestLaunch(progress, _testLaunchCancel.Token));
        }
        catch (OperationCanceledException)
        {
            // The window was closed, which also stops the test client.
        }
        catch (Exception e)
        {
            Log.Error(e, "Test launch failed");
            TestLaunchStatus.Text = e.Message;
        }
        finally
        {
            _testLaunchRunning = false;

            if (!_testLaunchCancel.IsCancellationRequested)
                TestLaunchButton.IsEnabled = true;
        }
    }

    private void ShowTestLaunchResult(SanabiTestLaunchResult result)
    {
        var loc = LocalizationManager.Instance;
        var verdict = loc.GetString(result.Verdict switch
        {
            SanabiTestLaunchVerdict.Passed => "crash-report-test-verdict-passed",
            SanabiTestLaunchVerdict.OpenBroken => "crash-report-test-verdict-open-broken",
            SanabiTestLaunchVerdict.PatchesBroken => "crash-report-test-verdict-patches-broken",
            SanabiTestLaunchVerdict.ProxyBlocked => "crash-report-test-verdict-proxy-blocked",
            SanabiTestLaunchVerdict.NothingToTest => "crash-report-test-verdict-nothing-to-test",
            _ => "crash-report-test-verdict-game-broken"
        });

        TestLaunchStatus.Text = result.SavedPath is { } savedPath
            ? $"{verdict}\n{loc.GetString("crash-report-test-saved", ("path", savedPath))}"
            : verdict;

        _report = result.Report;
        ReportText.Text = result.Report;
    }

    private static string GetStageText(SanabiTestLaunchStage stage)
    {
        return LocalizationManager.Instance.GetString(stage switch
        {
            SanabiTestLaunchStage.Checks => "crash-report-test-stage-checks",
            SanabiTestLaunchStage.TestLaunch => "crash-report-test-stage-launch",
            _ => "crash-report-test-stage-launch-without-patches"
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _testLaunchCancel.Cancel();
    }

    private void TestLaunch_OnClick(object? sender, RoutedEventArgs e)
    {
        StartTestLaunch();
    }

    private void OpenLogs_OnClick(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            UseShellExecute = true,
            FileName = LauncherPaths.DirLogs
        });
    }

    private void Close_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void Copy_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clipboard)
            await clipboard.SetTextAsync(_report);
    }
}
