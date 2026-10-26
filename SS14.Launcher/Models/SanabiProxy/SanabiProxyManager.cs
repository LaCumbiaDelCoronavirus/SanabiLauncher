using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Sanabi.Framework.Data;
using Sanabi.Framework.Proxy;
using Serilog;
using SS14.Launcher.Models.Data;
using SS14.Launcher.Models.Logins;

namespace SS14.Launcher.Models.SanabiProxy;

public enum SanabiProxyState
{
    /// <summary>No profile with any proxy in it is in use; connections are direct.</summary>
    Off,

    /// <summary>The first health check of the profile is still running; connections wait for it.</summary>
    Checking,

    /// <summary>Every proxy in the profile passed its last check.</summary>
    Working,

    /// <summary>A proxy in the profile failed its last check; all connections are blocked.</summary>
    Failed,
}

/// <summary>
///     Owns the proxy profiles, which one is in use, and whether it works.
///
///     Profiles are global (<see cref="SanabiCVars.ProxyProfiles"/>); the selection is per account
///         (<see cref="SanabiAccountCVars.ProxyProfile"/>), with <see cref="SanabiCVars.LoggedOutProxyProfile"/>
///         standing in while no account is active. While the selected profile has any proxy in it, it is probed
///         continuously, and nothing may connect unless the last probe passed (see <see cref="EnsureTrafficAllowedAsync"/>).
/// </summary>
public sealed class SanabiProxyManager : ISanabiProxyGate
{
    private static readonly TimeSpan FirstVerdictWaitLimit = SanabiGlobal.ProxyProbeTimeout * 2 + TimeSpan.FromSeconds(5);

    private readonly DataManager _cfg;
    private LoginManager? _loginManager;
    private List<SanabiProxyProfile> _profiles;

    // Swapped wholesale whenever the in-use profile changes; read lock-free from HTTP threads.
    private volatile Session? _session;

    /// <summary>
    ///     Something shown in the UI changed: profiles, selection, or health. Always raised on the UI thread.
    /// </summary>
    public event Action? Changed;

    public SanabiProxyManager(DataManager cfg)
    {
        _cfg = cfg;
        _profiles = SanabiProxyProfilesCVar.Parse(_cfg.GetCVar(SanabiCVars.ProxyProfiles));

        // Start right away, so startup traffic (token refreshes, hub queries) already respects the profile the
        // last account used.
        Reevaluate();
    }

    /// <summary>
    ///     Hooks account switching. Separate from the constructor because the login manager needs the HTTP
    ///         client, which needs this.
    /// </summary>
    public void Initialize(LoginManager loginManager)
    {
        _loginManager = loginManager;
        _loginManager.AfterActiveAccountUpdate += _ => OnActiveAccountChanged();
        OnActiveAccountChanged();
    }

    public IReadOnlyList<SanabiProxyProfile> Profiles => _profiles;

    /// <summary>
    ///     The profile chosen for the current account (or logged-out state), even if it has no proxies in it.
    /// </summary>
    public SanabiProxyProfile? SelectedProfile => FindProfile(ResolveSelectedProfileId());

    /// <summary>
    ///     The profile actually in force: <see cref="SelectedProfile"/> if it has any proxy in it, else null.
    /// </summary>
    public SanabiProxyProfile? ActiveProfile => _session?.Profile;

    public SanabiProxyState State => _session?.State ?? SanabiProxyState.Off;

    /// <summary>
    ///     Why the last check failed, if it did.
    /// </summary>
    public string? LastError => _session?.Error;

    public DateTime? LastChecked => _session?.LastChecked;

    /// <summary>
    ///     Whether a check is running right now (including re-checks of a profile already known to work or fail).
    /// </summary>
    public bool IsChecking => _session?.IsChecking ?? false;

    /// <summary>
    ///     Username of the account the selection applies to, or null when it's the logged-out selection.
    /// </summary>
    public string? SelectionScopeUsername => _loginManager?.ActiveAccount?.Username;

    public SanabiProxyEndpoint? LauncherProxy => _session?.Profile.Launcher;

    public bool IsProxyActive => _session != null;

    public async ValueTask EnsureTrafficAllowedAsync(CancellationToken cancel)
    {
        while (_session is { } session)
        {
            if (session.State == SanabiProxyState.Checking)
            {
                try
                {
                    await session.FirstVerdict.Task.WaitAsync(FirstVerdictWaitLimit, cancel).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Fall through: still checking counts as not working.
                }
            }

            // The profile was switched while we waited; judge by the new one instead.
            if (!ReferenceEquals(session, _session))
                continue;

            if (session.State == SanabiProxyState.Working)
                return;

            throw new SanabiProxyBlockedException(session.Profile.Name, session.Error);
        }
    }

    public void ReportPossibleFailure() => _session?.WakeUp();

    /// <summary>
    ///     Re-probes the active profile now.
    /// </summary>
    public void Recheck() => _session?.WakeUp();

    /// <summary>
    ///     Uses <paramref name="profileId"/> (null for no proxy) for the active account, or for the logged-out
    ///         state if there is no active account.
    /// </summary>
    public void SelectProfile(string? profileId)
    {
        profileId ??= string.Empty;

        if (_loginManager?.ActiveAccount is { } account)
            _cfg.SetAccountCVar(SanabiAccountCVars.ProxyProfile, account.UserId, profileId);

        // Also the logged-out fallback, so logging out keeps whatever this account was using.
        _cfg.SetCVar(SanabiCVars.LoggedOutProxyProfile, profileId);
        _cfg.CommitConfig();

        Reevaluate();
    }

    public SanabiProxyProfile AddProfile(string name)
    {
        var profile = new SanabiProxyProfile { Name = name };
        _profiles = [.. _profiles, profile];
        SaveProfiles();
        RaiseChanged();
        return profile;
    }

    /// <summary>
    ///     Replaces the stored profile with the same <see cref="SanabiProxyProfile.Id"/>. If it's in use, it is
    ///         re-checked from scratch (connections wait again until it passes).
    /// </summary>
    public void UpdateProfile(SanabiProxyProfile updated)
    {
        _profiles = _profiles.Select(profile => profile.Id == updated.Id ? updated.Clone() : profile).ToList();
        SaveProfiles();
        Reevaluate();
    }

    /// <summary>
    ///     Deletes a profile. Accounts that used it are left with no proxy.
    /// </summary>
    public void RemoveProfile(string profileId)
    {
        _profiles = _profiles.Where(profile => profile.Id != profileId).ToList();
        SaveProfiles();
        Reevaluate();
    }

    private void OnActiveAccountChanged()
    {
        // Remember this account's choice for while logged out (see SanabiCVars.LoggedOutProxyProfile).
        if (_loginManager?.ActiveAccount is { } account)
        {
            var accountProfileId = _cfg.GetAccountCVarOrDefault(SanabiAccountCVars.ProxyProfile, account.UserId);
            if (_cfg.GetCVar(SanabiCVars.LoggedOutProxyProfile) != accountProfileId)
            {
                _cfg.SetCVar(SanabiCVars.LoggedOutProxyProfile, accountProfileId);
                _cfg.CommitConfig();
            }
        }

        Reevaluate();
    }

    private string ResolveSelectedProfileId()
    {
        if (_loginManager?.ActiveAccount is { } account)
            return _cfg.GetAccountCVarOrDefault(SanabiAccountCVars.ProxyProfile, account.UserId);

        return _cfg.GetCVar(SanabiCVars.LoggedOutProxyProfile);
    }

    private SanabiProxyProfile? FindProfile(string profileId)
        => profileId.Length == 0 ? null : _profiles.FirstOrDefault(profile => profile.Id == profileId);

    /// <summary>
    ///     Brings the monitoring session in line with the current selection. Profiles are replaced, never
    ///         mutated, so an unchanged reference means nothing about the selection changed.
    /// </summary>
    private void Reevaluate()
    {
        // A profile with invalid settings still counts as in use, so it blocks (fails its check) rather than
        // quietly connecting directly.
        var selected = SelectedProfile;
        var wanted = selected is { HasAnyProxy: true } ? selected : null;

        var current = _session;
        if (ReferenceEquals(current?.Profile, wanted))
        {
            RaiseChanged();
            return;
        }

        current?.Stop();

        if (wanted == null)
        {
            _session = null;
            Log.Information("Proxy: none in use; connecting directly.");
        }
        else
        {
            var session = new Session(wanted);
            _session = session;
            Log.Information("Proxy: using profile '{Name}' (launcher: {Launcher}, game: {Game}); checking it before allowing connections.",
                wanted.Name,
                wanted.Launcher?.DisplayString ?? "direct",
                wanted.Game?.DisplayString ?? "direct");

            _ = MonitorAsync(session);
        }

        LazySanabiConfig.ProxyActive = _session != null;
        RaiseChanged();
    }

    private async Task MonitorAsync(Session session)
    {
        var cancel = session.Cancel.Token;

        while (!cancel.IsCancellationRequested)
        {
            session.IsChecking = true;
            RaiseChanged();

            string? error;
            try
            {
                error = session.Profile.IsValid(out var validationError)
                    ? await SanabiProxyProbe.ProbeAsync(session.Profile, cancel).ConfigureAwait(false)
                    : $"the profile's settings are invalid ({validationError})";
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            if (cancel.IsCancellationRequested)
                return;

            var previous = session.State;
            session.Error = error;
            session.LastChecked = DateTime.Now;
            session.State = error == null ? SanabiProxyState.Working : SanabiProxyState.Failed;
            session.IsChecking = false;
            session.FirstVerdict.TrySetResult();

            if (session.State != previous)
            {
                if (error == null)
                    Log.Information("Proxy profile '{Name}' is working; connections allowed.", session.Profile.Name);
                else
                    Log.Warning("Proxy profile '{Name}' is not working; all connections blocked: {Error}", session.Profile.Name, error);
            }

            RaiseChanged();

            var interval = error == null ? SanabiGlobal.ProxyHealthyRecheckInterval : SanabiGlobal.ProxyFailedRecheckInterval;
            try
            {
                await session.Wake.WaitAsync(interval, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void SaveProfiles()
    {
        _cfg.SetCVar(SanabiCVars.ProxyProfiles, SanabiProxyProfilesCVar.Serialize(_profiles));
        _cfg.CommitConfig();
    }

    private void RaiseChanged()
    {
        // Touching Dispatcher.UIThread before Avalonia's platform is set up (we're initialized, and the active
        // account loaded, inside BuildAvaloniaApp) creates a dispatcher with no platform backend, and the main
        // loop then throws PlatformNotSupportedException. No UI is listening yet anyway, so invoke directly.
        if (Application.Current == null || Dispatcher.UIThread.CheckAccess())
            Changed?.Invoke();
        else
            Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    /// <summary>
    ///     Monitoring state for one in-use profile. A new one is made whenever the in-use profile changes, so a
    ///         stale check can never mark a different profile as working.
    /// </summary>
    private sealed class Session(SanabiProxyProfile profile)
    {
        public SanabiProxyProfile Profile { get; } = profile;
        public CancellationTokenSource Cancel { get; } = new();
        public TaskCompletionSource FirstVerdict { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim Wake { get; } = new(0, 1);

        public volatile SanabiProxyState State = SanabiProxyState.Checking;
        public volatile string? Error;
        public volatile bool IsChecking;
        public DateTime? LastChecked;

        public void WakeUp()
        {
            // Coalesce: one pending wake-up is as good as many.
            if (Wake.CurrentCount == 0)
            {
                try
                {
                    Wake.Release();
                }
                catch (SemaphoreFullException)
                {
                }
            }
        }

        public void Stop()
        {
            Cancel.Cancel();

            // Anyone still waiting on this session's verdict re-reads the new session instead.
            FirstVerdict.TrySetResult();
        }
    }
}
