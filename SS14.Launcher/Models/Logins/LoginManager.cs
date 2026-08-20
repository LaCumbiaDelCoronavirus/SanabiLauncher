using System;
using System.Linq;
using System.Threading.Tasks;
using DynamicData;
using ReactiveUI;
using Sanabi.Framework.Data;
using Serilog;
using SS14.Launcher.Api;
using SS14.Launcher.Models.Data;

namespace SS14.Launcher.Models.Logins;

// This is different from DataManager in that this class actually manages logic more complex than raw storage.
// Checking and refreshing tokens, marking accounts as "need signing in again", etc...
public sealed class LoginManager : ReactiveObject
{
    // TODO: If the user tries to connect to a server or such
    // on the split second interval that the launcher does a token refresh
    // (once a week, if you leave it open for long).
    // there is a possibility the token used by said action will be invalid because it's actively being replaced
    // oh well.
    // Do I really care to fix that?

    private readonly DataManager _dataManager;
    private readonly AuthApi _authApi;

    private IObservableCache<ActiveLoginData, Guid> _logins;

    /// <summary>
    ///     <see cref="ActiveLoginData"> of the currently active
    ///         account, if any.
    ///
    ///     Should not be set directly.
    /// </summary>
    //public LoggedInAccount? ActiveAccount { get => _activeLoginId == null ? null : _logins.Lookup(_activeLoginId.Value).Value; }
    public LoggedInAccount? ActiveAccount { get; private set; }

    /// <summary>
    ///     Raised right before the account seed has been regenerated (if necessary) and AfterActiveAccountUpdate is raised, but after
    ///         the new active account has been set. The single parameter is the
    ///         new account.
    /// </summary>
    public Action<LoggedInAccount?>? BeforeActiveAccountUpdate = null;

    /// <summary>
    ///     Raised when the active account is changed, with the single
    ///         parameter being the new account.
    /// </summary>
    public Action<LoggedInAccount?>? AfterActiveAccountUpdate = null;

    /// <summary>
    ///     Sets a new account to be active in by it's ID, or logs out (if possible)
    ///         if none is provided.
    /// </summary>
    /// <param name="value">The account ID, if any.</param>
    /// <exception cref="ArgumentException">Thrown when setting a new active account, but there is no login data for it.</exception>
    public void SetActiveAccountById(Guid? newAccountId)
    {
        if (newAccountId != null)
        {
            var lookup = _logins.Lookup(newAccountId.Value);
            SetActiveAccountFull(newAccountId, lookup.Value);

            if (!lookup.HasValue)
                throw new ArgumentException("We do not have a login with that ID.");
        }
        else
            SetActiveAccountFull(null, null);
    }

    /// <summary>
    ///     If setting a new existing active account, refreshes tokens first.
    ///         Otherwise logs out.
    /// </summary>
    /// <inheritdoc cref="SetActiveAccountById(Guid?)"/>
    public async Task TryRefreshTokensAndSetActiveAccountById(Guid? value)
    {
        if (value.HasValue)
            await RefreshTokens(value.Value);

        SetActiveAccountById(value);
    }

    /// <summary>
    ///     Sets a new account via a <see cref="LoggedInAccount"/>. Logs out
    ///         if none is provided.
    /// </summary>
    public void SetActiveAccount(LoggedInAccount? loggedInAccount)
        => SetActiveAccountFull(loggedInAccount?.UserId, loggedInAccount);

    public void SetActiveAccountFull(Guid? newAccountId, LoggedInAccount? newLoggedInAccount)
    {
        ActiveAccount = newLoggedInAccount;

        this.RaisePropertyChanged(nameof(ActiveAccount));
        _dataManager.SelectedLoginId = newAccountId;

        BeforeActiveAccountUpdate?.Invoke(ActiveAccount);

        if (newAccountId.HasValue &&
            _dataManager.GetAccountCVarOrDefault(SanabiAccountCVars.ShouldRegenerateSeed, newAccountId))
        {
            var ulongBytes = (Span<byte>)stackalloc byte[8];
            new Random().NextBytes(ulongBytes);

            _dataManager.SetAccountCVar(SanabiAccountCVars.SpoofingSeed, newAccountId.Value, BitConverter.ToInt64(ulongBytes));
            _dataManager.SetAccountCVar(SanabiAccountCVars.ShouldRegenerateSeed, newAccountId.Value, false);
            _dataManager.CommitConfig();
        }

        AfterActiveAccountUpdate?.Invoke(ActiveAccount);
    }

    public IObservableCache<LoggedInAccount, Guid> Logins { get; private set; }

#pragma warning disable CS8618 // Non-nullable variable must contain a non-null value when exiting constructor. Consider declaring it as nullable.
    public LoginManager(DataManager cfg, AuthApi authApi)
#pragma warning restore CS8618 // Non-nullable variable must contain a non-null value when exiting constructor. Consider declaring it as nullable.
    {
        _dataManager = cfg;
        _dataManager.SetLoginManager(this);

        _authApi = authApi;
    }

    public void Initialise()
    {
        _logins = _dataManager.Logins
            .Connect()
            .Transform(p => new ActiveLoginData(p))
            .OnItemRemoved(p =>
            {
                if (p.LoginInfo.UserId == ActiveAccount?.UserId)
                    SetActiveAccount(null);
            })
            .AsObservableCache();

        Logins = _logins
            .Connect()
            .Transform((data, guid) => (LoggedInAccount)data)
            .AsObservableCache();
    }

    public async Task RefreshAllTokens()
    {
        Log.Debug("Refreshing all tokens.");

        const int delayStart = 2;
        const int delayValue = 200;

        await Task.WhenAll(_logins.Items.Select(async (l, i) =>
        {
            if (l.Status == AccountLoginStatus.Expired)
            {
                // Literally don't even bother we already know it's dead and the user has to solve it.
                Log.Debug("Token for {login} is already expired", l.LoginInfo);
                return;
            }

            if (l.LoginInfo.Token.IsTimeExpired())
            {
                // Oh hey, time expiry.
                Log.Debug("Token for {login} expired due to time", l.LoginInfo);
                l.SetStatus(AccountLoginStatus.Expired);
                return;
            }

            if (i > delayStart)
                await Task.Delay(delayValue * (i - delayStart));

            try
            {
                // Initialise cvars for account if not already
                _dataManager.AssignAccountCVars([l.UserId], typeof(SanabiAccountCVars), overwrite: false);
                await UpdateSingleAccountStatus(l);
            }
            catch (AuthApiException e)
            {
                // TODO: Maybe retry to refresh tokens sooner if an error occured.
                // Ignore, I guess.
                Log.Warning(e, "AuthApiException while trying to refresh token for {login}", l.LoginInfo);
            }
        }));
    }

    /// <summary>
    ///     Refreshes token(s) for the specified account given it's login id.
    /// </summary>
    public async Task RefreshTokens(Guid loginId)
        => await RefreshTokens(_logins.Lookup(loginId).Value);

    /// <summary>
    ///     Refreshes token(s) for the specified account.
    /// </summary>
    public async Task RefreshTokens(ActiveLoginData loginData)
    {
        if (loginData.Status == AccountLoginStatus.Expired)
        {
            // Literally don't even bother we already know it's dead and the user has to solve it.
            Log.Debug("Token for {login} is already expired", loginData.LoginInfo);
            return;
        }

        if (loginData.LoginInfo.Token.IsTimeExpired())
        {
            // Oh hey, time expiry.
            Log.Debug("Token for {login} expired due to time", loginData.LoginInfo);
            loginData.SetStatus(AccountLoginStatus.Expired);
            return;
        }

        try
        {
            await UpdateSingleAccountStatus(loginData);
        }
        catch (AuthApiException e)
        {
            // TODO: Maybe retry to refresh tokens sooner if an error occured.
            // Ignore, I guess.
            Log.Warning(e, "AuthApiException while trying to refresh token for {login}", loginData.LoginInfo);
        }
    }

    public void AddFreshLogin(LoginInfo info, AuthServerInfo authServerInfo)
    {
        _dataManager.AddLogin(info);

        _dataManager.AssignAccountCVars([info.UserId], typeof(SanabiAccountCVars), overwrite: false);
        _dataManager.SetAccountCVar(SanabiAccountCVars.AuthServers, info.UserId, SanabiAuthManager.SerializeAuthServerDataString(authServerInfo));

        _logins.Lookup(info.UserId).Value.SetStatus(AccountLoginStatus.Available);
    }

    public void UpdateToNewToken(LoggedInAccount account, LoginToken token)
    {
        var cast = (ActiveLoginData)account;
        cast.SetStatus(AccountLoginStatus.Available);
        account.LoginInfo.Token = token;
    }

    /// <exception cref="AuthApiException">Thrown if an API error occured.</exception>
    public Task<AccountLoginStatus> UpdateSingleAccountStatus(LoggedInAccount account)
    {
        return UpdateSingleAccountStatus((ActiveLoginData)account);
    }

    private async Task<AccountLoginStatus> UpdateSingleAccountStatus(ActiveLoginData data)
    {
        SanabiAuthManager.OnAccountUpdated(data);

        foreach (var authInfo in data.SupportedAuthServers!)
        {
            Log.Warning($":!!!: AUTHAPI is being contacted with logininfo: {data.LoginInfo.Username}");
            if (data.LoginInfo.Token.ShouldRefresh())
            {
                Log.Debug("Refreshing token for {login}", data.LoginInfo);
                // If we need to refresh the token anyways we'll just
                // implicitly do the "is it still valid" with the refresh request.
                var newTokenHopefully = await _authApi.RefreshTokenAsync(data.LoginInfo.Token.Token, authInfo);
                if (newTokenHopefully == null)
                {
                    // Token expired or whatever?
                    data.SetStatus(AccountLoginStatus.Expired);
                    Log.Debug("Token for {login} expired while refreshing it", data.LoginInfo);

                }
                else
                {
                    Log.Debug("Refreshed token for {login}", data.LoginInfo);
                    data.LoginInfo.Token = newTokenHopefully.Value;
                    data.SetStatus(AccountLoginStatus.Available);
                }
            }
            else if (data.Status == AccountLoginStatus.Unsure)
            {
                var valid = await _authApi.CheckTokenAsync(data.LoginInfo.Token.Token, authInfo);
                Log.Debug("Token for {login} still valid? {valid}", data.LoginInfo, valid);
                data.SetStatus(valid ? AccountLoginStatus.Available : AccountLoginStatus.Expired);
            }
        }

        return data.Status;
    }

    public sealed class ActiveLoginData : LoggedInAccount
    {
        public AccountLoginStatus _status;

        public ActiveLoginData(LoginInfo info) : base(info)
        {
        }

        public override AccountLoginStatus Status => _status;

        public void SetStatus(AccountLoginStatus status)
        {
            this.RaiseAndSetIfChanged(ref _status, status, nameof(Status));
            Log.Debug("Setting status for login {account} to {status}", LoginInfo, status);
        }
    }
}
