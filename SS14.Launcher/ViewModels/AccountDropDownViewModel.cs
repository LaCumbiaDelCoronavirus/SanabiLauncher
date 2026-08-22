using System;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Threading.Tasks;
using DynamicData;
using JetBrains.Annotations;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;
using Splat;
using SS14.Launcher.Api;
using SS14.Launcher.Localization;
using SS14.Launcher.Models.Data;
using SS14.Launcher.Models.Logins;
using SS14.Launcher.Utility;

namespace SS14.Launcher.ViewModels;

public class AccountDropDownViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _mainVm;
    private readonly DataManager _cfg;
    private readonly AuthApi _authApi;
    private readonly LoginManager _loginMgr;
    private readonly ReadOnlyObservableCollection<AvailableAccountViewModel> _accounts;
    private readonly LocalizationManager _loc;

    public ReadOnlyObservableCollection<AvailableAccountViewModel> Accounts => _accounts;

    public bool EnableMultiAccounts => _cfg.ActuallyMultiAccounts;
    public bool AnyAccountsPresent => _loginMgr.Logins.Count != 0;

    public AccountDropDownViewModel(MainWindowViewModel mainVm)
    {
        _mainVm = mainVm;
        _cfg = Locator.Current.GetRequiredService<DataManager>();
        _authApi = Locator.Current.GetRequiredService<AuthApi>();
        _loginMgr = Locator.Current.GetRequiredService<LoginManager>();
        _loc = LocalizationManager.Instance;

        _loginMgr.AfterActiveAccountUpdate += (_) =>
        {
            this.RaisePropertyChanged(nameof(LoginText));
            this.RaisePropertyChanged(nameof(AccountSwitchText));
            this.RaisePropertyChanged(nameof(LogoutText));
            this.RaisePropertyChanged(nameof(AccountControlsVisible));
            this.RaisePropertyChanged(nameof(AccountSwitchVisible));
        };

        _loginMgr.Logins.Connect().Subscribe(_ =>
        {
            this.RaisePropertyChanged(nameof(LogoutText));
            this.RaisePropertyChanged(nameof(AccountSwitchVisible));
            this.RaisePropertyChanged(nameof(AnyAccountsPresent));
        });

        var filterObservable = this.WhenAnyValue(x => x._loginMgr.ActiveAccount)
            .Select(MakeFilter);

        _loginMgr.Logins
            .Connect()
            .Filter(filterObservable)
            .Transform(p => new AvailableAccountViewModel(p))
            .Bind(out _accounts)
            .Subscribe();

        this.RaisePropertyChanged(nameof(AnyAccountsPresent));
    }

    private static Func<LoggedInAccount?, bool> MakeFilter(LoggedInAccount? selected)
    {
        return l => l != selected;
    }

    public string LoginText => _loginMgr.ActiveAccount?.Username ??
                               (EnableMultiAccounts ? _loc.GetString("account-drop-down-none-selected") : _loc.GetString("account-drop-down-not-logged-in"));

    public string LogoutText => _cfg.Logins.Count == 1
        ? _loc.GetString("account-drop-down-log-out")
        : _loc.GetString("account-drop-down-log-out-of", ("name", _loginMgr.ActiveAccount?.Username));

    public bool AccountSwitchVisible => _cfg.Logins.Count > 1 || _loginMgr.ActiveAccount == null;
    public string AccountSwitchText => _loginMgr.ActiveAccount != null
        ? _loc.GetString("account-drop-down-switch-account")
        : _loc.GetString("account-drop-down-select-account");

    public bool AccountControlsVisible => _loginMgr.ActiveAccount != null;

    [Reactive] public bool IsDropDownOpen { get; set; }

    public async void LogoutPressed()
    {
        IsDropDownOpen = false;

        if (_loginMgr.ActiveAccount != null)
        {
            foreach (var authInfo in _loginMgr.ActiveAccount.SupportedAuthServers!)
                await _authApi.LogoutTokenAsync(_loginMgr.ActiveAccount.LoginInfo.Token.Token, authInfo);

            _cfg.RemoveLogin(_loginMgr.ActiveAccount.LoginInfo);
        }
    }

    [UsedImplicitly]
    public void UseNoAccountPressed()
    {
        IsDropDownOpen = false;
        _loginMgr.SetActiveAccount(null);
    }

    [UsedImplicitly]
    public void AccountButtonPressed(object account)
    {
        if (account is not LoggedInAccount loggedInAccount)
        {
            Log.Warning($"Tried to switch account but parameter was not of type {nameof(LoggedInAccount)}");
            return;
        }

        IsDropDownOpen = false;
        _mainVm.TrySwitchToAccount(loggedInAccount);
    }

    public void AddAccountPressed()
    {
        IsDropDownOpen = false;

        _loginMgr.SetActiveAccount(null);
        _mainVm.SetLoginMenuShowing(true);
    }

    public async void RefreshAllTokensPressed()
    {
        IsDropDownOpen = false;

        _mainVm.BusyTask = _loc.GetString("account-drop-down-busy-refreshing-tokens");
        await _loginMgr.RefreshAllTokens();
        _mainVm.BusyTask = null;
    }
}

public sealed class AvailableAccountViewModel : ViewModelBase
{
    public extern string StatusText { [ObservableAsProperty] get; }

    public LoggedInAccount Account { get; }

    public AvailableAccountViewModel(LoggedInAccount account)
    {
        Account = account;

        this.WhenAnyValue<AvailableAccountViewModel, AccountLoginStatus, string>(p => p.Account.Status, p => p.Account.Username)
            .Select(p => p.Item1 switch
            {
                AccountLoginStatus.Available => $"{p.Item2}",
                AccountLoginStatus.Expired => $"{p.Item2} (!)",
                _ => $"{p.Item2} (?)"
            })
            .ToPropertyEx(this, x => x.StatusText);
    }
}
