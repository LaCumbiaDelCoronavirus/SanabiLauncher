using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ReactiveUI;
using Sanabi.Framework.Data;
using Splat;
using SS14.Launcher.Models;
using SS14.Launcher.Models.Data;
using SS14.Launcher.Models.SanabiCrash;
using SS14.Launcher.Utility;
using SS14.Launcher.Views;

namespace SS14.Launcher.ViewModels;

/// <summary>
///     Lets a connection (and its content download) carry on in the background: the overlay is hidden and a
///     banner above the header shows the progress instead, so the rest of the launcher stays usable.
/// </summary>
public partial class ConnectingViewModel
{
    // With ConnectInBackground on, the overlay only shows when asked for, or when it needs the user.
    private bool _isBackgrounded = Locator.Current.GetRequiredService<DataManager>()
        .GetCVar(SanabiCVars.ConnectInBackground);

    /// <summary>
    ///     True while the overlay is hidden and the connection shows as a banner instead.
    /// </summary>
    public bool IsBackgrounded
    {
        get => _isBackgrounded;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBackgrounded, value);
            this.RaisePropertyChanged(nameof(IsForegrounded));
        }
    }

    public bool IsForegrounded => !IsBackgrounded;

    /// <summary>
    ///     Anything that needs the user's input (an error, the privacy policy prompt) can't sit in the background.
    /// </summary>
    public bool CanSendToBackground => !IsErrored && !IsAskingPrivacyPolicy;

    /// <summary>
    ///     <see cref="StatusText"/> on one line, for the banner. The extra lines are only the connect reason.
    /// </summary>
    public string BannerStatusText
    {
        get
        {
            var statusText = StatusText;
            var newlineIndex = statusText.IndexOf('\n');
            return newlineIndex == -1 ? statusText : statusText[..newlineIndex];
        }
    }

    /// <summary>
    ///     The game failed, as opposed to the connection or update, so there's something to report and test.
    /// </summary>
    public bool ShowClientFailureActions => _connectorStatus == Connector.ConnectionStatus.ClientExited
                                            && _connector.ClientExitedBadly;

    /// <summary>
    ///     The report takes a few seconds to appear, as it waits on the game and on macOS's own crash reporter.
    /// </summary>
    public bool HasCrashReport => _connector.CrashReport != null;

    private CrashReportDialog? _crashDialog;

    private string FormatClientFailure()
    {
        if (_connector.ClientFailure is not { } failure)
            return _loc.GetString("connecting-status-client-crashed");

        var reason = failure.ExitCode is { } exitCode
            ? SanabiCrashReportBuilder.DescribeExitCode(exitCode)
            : _loc.GetString("connecting-status-client-reason-unknown");

        return failure.Kind switch
        {
            SanabiClientFailureKind.LaunchFailed => _loc.GetString("connecting-status-client-launch-failed", ("reason", reason)),
            SanabiClientFailureKind.ExitedDuringStartup => _loc.GetString("connecting-status-client-exited-during-startup", ("reason", reason)),
            SanabiClientFailureKind.NeverStarted => _loc.GetString("connecting-status-client-never-started"),
            _ => _loc.GetString("connecting-status-client-crashed-running", ("reason", reason))
        };
    }

    private void ShowCrashDialog(string? report)
    {
        // A second window for the same launch would just be the same report twice.
        if (_crashDialog is { } existing)
        {
            if (report != null)
                existing.SetReport(report);

            existing.Activate();
            return;
        }

        _crashDialog = new CrashReportDialog(report ?? "", _connector.RunTestLaunchAsync);
        _crashDialog.Closed += (_, _) => _crashDialog = null;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
            _crashDialog.Show(mainWindow);
        else
            _crashDialog.Show();
    }

    // Binding; do not rename/remove/change signature
    public void OpenCrashReport()
    {
        ShowCrashDialog(_connector.CrashReport);
    }

    // Binding; do not rename/remove/change signature
    public void RunTestLaunch()
    {
        ShowCrashDialog(_connector.CrashReport);
        _crashDialog?.StartTestLaunch();
    }

    // Binding; do not rename/remove/change signature
    public void OpenLogDirectory()
    {
        Process.Start(new ProcessStartInfo
        {
            UseShellExecute = true,
            FileName = LauncherPaths.DirLogs
        });
    }

    // Binding; do not rename/remove/change signature
    public void SendToBackground()
    {
        if (CanSendToBackground)
            IsBackgrounded = true;
    }

    // Binding; do not rename/remove/change signature
    public void BringToForeground()
    {
        IsBackgrounded = false;
    }

    private void SanabiConnectorStatusChanged()
    {
        this.RaisePropertyChanged(nameof(CanSendToBackground));
        this.RaisePropertyChanged(nameof(BannerStatusText));
        this.RaisePropertyChanged(nameof(ShowClientFailureActions));

        // Pop the overlay back up whenever it needs the user, so a background connection never silently stalls.
        if (!CanSendToBackground)
            IsBackgrounded = false;
    }

    private void SanabiUpdaterStatusChanged()
    {
        this.RaisePropertyChanged(nameof(BannerStatusText));
    }

    /// <summary>
    ///     Only one connection can run at a time (they share the <see cref="Updater"/>), and with the overlay in the
    ///     background the server list's connect buttons are clickable again. Show the running one instead.
    /// </summary>
    private static bool TryForegroundExisting(MainWindowViewModel windowVm)
    {
        if (windowVm.ConnectingVM is not { } existing)
            return false;

        existing.BringToForeground();
        return true;
    }
}
