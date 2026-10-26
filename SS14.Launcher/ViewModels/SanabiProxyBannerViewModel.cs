using ReactiveUI;
using Splat;
using SS14.Launcher.Localization;
using SS14.Launcher.Models.SanabiProxy;
using SS14.Launcher.Utility;

namespace SS14.Launcher.ViewModels;

/// <summary>
///     The always-visible strip above the header that says a proxy is in use, through what, and whether it works.
/// </summary>
public sealed class SanabiProxyBannerViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _mainWindow;
    private readonly SanabiProxyManager _proxyManager;
    private readonly LocalizationManager _loc;

    public SanabiProxyBannerViewModel(MainWindowViewModel mainWindow)
    {
        _mainWindow = mainWindow;
        _proxyManager = Locator.Current.GetRequiredService<SanabiProxyManager>();
        _loc = LocalizationManager.Instance;

        _proxyManager.Changed += Refresh;
        _loc.LanguageSwitched += Refresh;
    }

    public bool IsVisible => _proxyManager.IsProxyActive;
    public bool IsWorking => _proxyManager.State == SanabiProxyState.Working;
    public bool IsChecking => _proxyManager.State == SanabiProxyState.Checking;
    public bool IsFailed => _proxyManager.State == SanabiProxyState.Failed;

    public string ProfileName => _proxyManager.ActiveProfile is { } profile
        ? _loc.GetString("proxy-banner-profile", ("name", profile.Name))
        : string.Empty;

    public string Details
    {
        get
        {
            if (_proxyManager.ActiveProfile is not { } profile)
                return string.Empty;

            var direct = _loc.GetString("proxy-banner-direct");
            return _loc.GetString("proxy-banner-details",
                ("launcher", profile.Launcher?.DisplayString ?? direct),
                ("game", profile.Game?.DisplayString ?? direct));
        }
    }

    public string StatusText => _loc.GetString(_proxyManager.State switch
    {
        SanabiProxyState.Working => "proxy-banner-status-working",
        SanabiProxyState.Failed => "proxy-banner-status-failed",
        _ => "proxy-banner-status-checking",
    });

    public string ToolTip => _proxyManager.State == SanabiProxyState.Failed && _proxyManager.LastError is { } error
        ? $"{error}\n\n{_loc.GetString("proxy-banner-tooltip")}"
        : _loc.GetString("proxy-banner-tooltip");

    // Binding; do not rename/remove/change signature
    public void OpenProxyTab() => _mainWindow.SelectTabProxy();

    private void Refresh()
    {
        this.RaisePropertyChanged(nameof(IsVisible));
        this.RaisePropertyChanged(nameof(IsWorking));
        this.RaisePropertyChanged(nameof(IsChecking));
        this.RaisePropertyChanged(nameof(IsFailed));
        this.RaisePropertyChanged(nameof(ProfileName));
        this.RaisePropertyChanged(nameof(Details));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(ToolTip));
    }
}
