using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using DynamicData;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;
using Splat;
using SS14.Launcher.Api;
using SS14.Launcher.Localization;
using SS14.Launcher.Models;
using SS14.Launcher.Models.Data;
using SS14.Launcher.Models.Logins;
using SS14.Launcher.Models.OverrideAssets;
using SS14.Launcher.Utility;
using SS14.Launcher.ViewModels.Login;
using SS14.Launcher.ViewModels.MainWindowTabs;
using SS14.Launcher.Views;

namespace SS14.Launcher.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IErrorOverlayOwner
{
    private readonly DataManager _cfg;
    private readonly LoginManager _loginMgr;
    private readonly LauncherInfoManager _infoManager;
    private readonly LocalizationManager _loc;

    private int _selectedIndex;

    public DataManager Cfg => _cfg;
    [Reactive] public bool OutOfDate { get; private set; }

    public HomePageViewModel HomeTab { get; }
    public ServerListTabViewModel ServersTab { get; }
    public NewsTabViewModel NewsTab { get; }
    public OptionsTabViewModel OptionsTab { get; }

    public MainWindowViewModel()
    {
        _cfg = Locator.Current.GetRequiredService<DataManager>();
        _loginMgr = Locator.Current.GetRequiredService<LoginManager>();
        _infoManager = Locator.Current.GetRequiredService<LauncherInfoManager>();
        _loc = LocalizationManager.Instance;

        ServersTab = new ServerListTabViewModel(this);
        NewsTab = new NewsTabViewModel();
        HomeTab = new HomePageViewModel(this);
        OptionsTab = new OptionsTabViewModel();

        var tabs = new List<MainWindowTabViewModel>();
        tabs.Add(HomeTab);
        tabs.Add(ServersTab);
        //tabs.Add(NewsTab);
        tabs.Add(OptionsTab);
        tabs.Add(new PatchesTabViewModel());
#if DEVELOPMENT
        tabs.Add(new DevelopmentTabViewModel());
#endif
        Tabs = tabs;

        AccountDropDown = new AccountDropDownViewModel(this);
        LoginViewModel = new MainWindowLoginViewModel(this);

        _loginMgr.OnActiveAccountChanged += () =>
        {
            this.RaisePropertyChanged(nameof(Username));
            this.RaisePropertyChanged(nameof(ShowLoginMenu));
        };

        _cfg.Logins.Connect()
            .Subscribe(_ => { this.RaisePropertyChanged(nameof(AccountDropDownVisible)); });

        SetLoginMenuShowing(_cfg.GetCVar(SanabiCVars.StartOnLoginMenu));
    }

    private static bool _didStartingInit = false;

    /// <summary>
    ///     Initialises <see cref="LauncherInfoManager"/>
    ///         and <see cref="OverrideAssetsManager"/>.
    ///         Also checks for launcher update.
    ///
    ///     This accesses external hub API and is therefore
    ///         a security risk.
    /// </summary>
    // TODO fix busytask not working here whatever i dont care.
    private async void SCRISK_DoStartingInitialisation()
    {
        _didStartingInit = true;
        BusyTask = "Doing endpoint initalisation";

        var launcherInfo = Locator.Current.GetRequiredService<LauncherInfoManager>();
        var overrideAssets = Locator.Current.GetRequiredService<OverrideAssetsManager>();

        launcherInfo.Initialize();
        overrideAssets.Initialize();

        BusyTask = _loc.GetString("main-window-busy-checking-update");
        await SCRISK_CheckLauncherUpdate();

        BusyTask = null;
    }

    public void SetLoginMenuShowing(bool value)
    {
        if (!value && !_didStartingInit)
            SCRISK_DoStartingInitialisation();

        ShowLoginMenu = value;
        this.RaisePropertyChanged(nameof(ShowLoginMenu));

        if (value)
        {
            RunDeselectedOnTab();
            LoginViewModel.SwitchToLogin();
        }
        else
            // "Switch" to main window.
            RunSelectedOnTab();
    }

    public MainWindow? Control { get; set; }

    public IReadOnlyList<MainWindowTabViewModel> Tabs { get; }

    private bool _transitioningImagesVisible;
    public bool TransitioningImagesVisible
    {
        get => _transitioningImagesVisible;
        set => this.RaiseAndSetIfChanged(ref _transitioningImagesVisible, value);
    }

    [Reactive] public bool ShowLoginMenu { get; set; }
    public bool LoggedIn => _loginMgr.ActiveAccount != null;
    private string? Username => _loginMgr.ActiveAccount?.Username;
    public bool AccountDropDownVisible => _loginMgr.Logins.Count != 0;

    public AccountDropDownViewModel AccountDropDown { get; }

    public MainWindowLoginViewModel LoginViewModel { get; }

    [Reactive] public ConnectingViewModel? ConnectingVM { get; set; }

    [Reactive] public string? BusyTask { get; private set; }
    [Reactive] public ViewModelBase? OverlayViewModel { get; private set; }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            RunDeselectedOnTab();
            this.RaiseAndSetIfChanged(ref _selectedIndex, value);

            RunSelectedOnTab();
        }
    }

    private void RunDeselectedOnTab()
    {
        var tab = Tabs[_selectedIndex];
        tab.IsSelected = false;

        // if we're deselecting the home tab, dont show transitioning images
        if (tab == HomeTab)
            TransitioningImagesVisible = false;
    }

    private void RunSelectedOnTab()
    {
        var tab = Tabs[_selectedIndex];
        tab.IsSelected = true;
        tab.Selected();

        TransitioningImagesVisible = tab == HomeTab;
    }

    public ICVarEntry<bool> HasDismissedEarlyAccessWarning => Cfg.GetCVarEntry(CVars.HasDismissedEarlyAccessWarning);
    public bool ShouldShowIntelDegradationWarning => IsVulnerableToIntelDegradation(_cfg);
    public bool ShouldShowRosettaWarning => IsAppleSiliconInRosetta(_cfg);

    public string Version => $"v{LauncherVersion.Version}";

    public void OnDiscordButtonPressed()
    {
        Helpers.OpenUri(new Uri(ConfigConstants.DiscordUrl));
    }

    public void OnWebsiteButtonPressed()
    {
        Helpers.OpenUri(new Uri(ConfigConstants.WebsiteUrl));
    }

    private async Task SCRISK_CheckLauncherUpdate()
    {
        // await Task.Delay(1000);
        if (!ConfigConstants.DoVersionCheck)
        {
            return;
        }

        await _infoManager.LoadTask;
        if (_infoManager.Model == null)
        {
            // Error while loading.
            Log.Warning("Unable to check for launcher update due to error, assuming up-to-date.");
            OutOfDate = false;
            return;
        }

        OutOfDate = Array.IndexOf(_infoManager.Model.AllowedVersions, ConfigConstants.CurrentLauncherVersion) == -1;
        Log.Debug("Launcher out of date? {Value}", OutOfDate);
    }

    public void ExitPressed()
    {
        Control?.Close();
    }

    public void DownloadPressed()
    {
        Helpers.OpenUri(new Uri(ConfigConstants.DownloadUrl));
    }

    public void DismissEarlyAccessPressed()
    {
        Cfg.SetCVar(CVars.HasDismissedEarlyAccessWarning, true);
        Cfg.CommitConfig();
    }

    public void DismissIntelDegradationPressed()
    {
        Cfg.SetCVar(CVars.HasDismissedIntelDegradation, true);
        Cfg.CommitConfig();
        this.RaisePropertyChanged(nameof(ShouldShowIntelDegradationWarning));
    }

    public void DismissAppleSiliconRosettaPressed()
    {
        Cfg.SetCVar(CVars.HasDismissedRosettaWarning, true);
        Cfg.CommitConfig();
        this.RaisePropertyChanged(nameof(ShouldShowRosettaWarning));
    }

    public void SelectTabServers()
    {
        SelectedIndex = Tabs.IndexOf(ServersTab);
    }

    public void TrySwitchToAccount(LoggedInAccount account)
    {
        Log.Debug($"{account.Status} ACCSTAT");
        switch (account.Status)
        {
            case AccountLoginStatus.Unsure:
                TrySelectUnsureAccount(account);
                break;

            case AccountLoginStatus.Available:
                _ = _loginMgr.TryRefreshTokensAndSetActiveAccount(account);
                break;

            case AccountLoginStatus.Expired:
                _loginMgr.SetActiveAccount(null);
                LoginViewModel.SwitchToExpiredLogin(account);
                break;
        }
    }

    private async void TrySelectUnsureAccount(LoggedInAccount account)
    {
        BusyTask = _loc.GetString("main-window-busy-checking-account-status");
        try
        {
            await _loginMgr.UpdateSingleAccountStatus(account);

            // Can't be unsure, that'd have thrown.
            Debug.Assert(account.Status != AccountLoginStatus.Unsure);
            TrySwitchToAccount(account);
        }
        catch (AuthApiException e)
        {
            Log.Warning(e, "AuthApiException while trying to refresh account {login}", account.LoginInfo);
            OverlayViewModel = new AuthErrorsOverlayViewModel(this, _loc.GetString("main-window-error-connecting-auth-server"),
                new[]
                {
                    e.InnerException?.Message ?? _loc.GetString("main-window-error-unknown")
                });
        }
        finally
        {
            BusyTask = null;
        }
    }

    public void OverlayOk()
    {
        OverlayViewModel = null;
    }

    public bool IsContentBundleDropValid(IStorageFile file)
    {
        // Can only load content bundles if logged in, in some capacity.
        if (!LoggedIn)
            return false;

        // Disallow if currently connecting to a server.
        if (ConnectingVM != null)
            return false;

        return Path.GetExtension(file.Name) == ".zip";
    }

    public void Dropped(IStorageFile file)
    {
        // Trust view validated this.
        Debug.Assert(IsContentBundleDropValid(file));

        ConnectingViewModel.StartContentBundle(this, file);
    }

    private static bool IsVulnerableToIntelDegradation(DataManager cfg)
    {
        var processor = LauncherDiagnostics.GetProcessorModel();

        // No Intel processor, or already dismissed the warning.
        if (!processor.Contains("Intel") || cfg.GetCVar(CVars.HasDismissedIntelDegradation))
            return false;

        // Get the i#-#### from the processor string.
        var match = Regex.Match(processor, @"i\d+-\d+(?:[A-Z]+)?(?=\s|$)");
        if (!match.Success)
            return false;

        var affectedGenerations = new[] { "i3-13", "i5-13", "i7-13", "i9-13", "i3-14", "i5-14", "i7-14", "i9-14" };
        var excludedSuffixes = new[] { "HX", "H", "P", "U" };

        return affectedGenerations.Any(match.Value.Contains) && !excludedSuffixes.Any(match.Value.EndsWith);
    }

    private static bool IsAppleSiliconInRosetta(DataManager cfg)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        var processor = LauncherDiagnostics.GetProcessorModel();

        return processor.Contains("VirtualApple") && !cfg.GetCVar(CVars.HasDismissedRosettaWarning);
    }
}
