using ReactiveUI;
using Sanabi.Framework.Data;
using Splat;
using SS14.Launcher.Models;
using SS14.Launcher.Models.Data;
using SS14.Launcher.Utility;

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
