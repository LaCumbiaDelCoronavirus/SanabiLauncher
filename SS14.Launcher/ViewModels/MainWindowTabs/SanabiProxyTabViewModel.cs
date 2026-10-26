using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ReactiveUI;
using Sanabi.Framework.Proxy;
using Splat;
using SS14.Launcher.Localization;
using SS14.Launcher.Models.SanabiProxy;
using SS14.Launcher.Utility;

namespace SS14.Launcher.ViewModels.MainWindowTabs;

/// <summary>
///     The "Proxy" tab: pick, add, remove and edit proxy profiles, and see whether the one in use works.
/// </summary>
public sealed class SanabiProxyTabViewModel : MainWindowTabViewModel
{
    private readonly SanabiProxyManager _proxyManager;
    private readonly LocalizationManager _loc;

    private IReadOnlyList<SanabiProxyProfile>? _shownProfiles;
    private SanabiProxyProfileEntryViewModel? _selectedEntry;
    private SanabiProxyProfileEditorViewModel? _editor;

    // Set while we move the list selection to match the manager, so that doesn't count as the user picking.
    private bool _syncingSelection;

    public SanabiProxyTabViewModel()
    {
        _proxyManager = Locator.Current.GetRequiredService<SanabiProxyManager>();
        _loc = LocalizationManager.Instance;

        _proxyManager.Changed += Refresh;
        _loc.LanguageSwitched += () =>
        {
            _shownProfiles = null;
            Refresh();
        };

        Refresh();
    }

    public override string Name => _loc.GetString("tab-proxy-title");

    public ObservableCollection<SanabiProxyProfileEntryViewModel> Entries { get; } = new();

    public SanabiProxyProfileEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (ReferenceEquals(_selectedEntry, value))
                return;

            this.RaiseAndSetIfChanged(ref _selectedEntry, value);

            // Picking an entry is what selects it for use.
            if (!_syncingSelection && value != null)
                _proxyManager.SelectProfile(value.Profile?.Id);

            UpdateEditor();
            this.RaisePropertyChanged(nameof(CanRemove));
        }
    }

    public SanabiProxyProfileEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            this.RaiseAndSetIfChanged(ref _editor, value);
            this.RaisePropertyChanged(nameof(HasEditor));
        }
    }

    public bool HasEditor => Editor != null;

    public bool CanRemove => SelectedEntry?.Profile != null;

    public bool IsProxyActive => _proxyManager.IsProxyActive;
    public bool IsWorking => _proxyManager.State == SanabiProxyState.Working;
    public bool IsChecking => _proxyManager.State == SanabiProxyState.Checking;
    public bool IsFailed => _proxyManager.State == SanabiProxyState.Failed;

    public string ScopeText => _proxyManager.SelectionScopeUsername is { } username
        ? _loc.GetString("tab-proxy-scope-account", ("username", username))
        : _loc.GetString("tab-proxy-scope-logged-out");

    public string StatusText
    {
        get
        {
            var text = _proxyManager.State switch
            {
                SanabiProxyState.Checking => _loc.GetString("tab-proxy-status-checking"),
                SanabiProxyState.Working => _loc.GetString("tab-proxy-status-working",
                    ("time", _proxyManager.LastChecked?.ToString("T") ?? "?")),
                SanabiProxyState.Failed => _loc.GetString("tab-proxy-status-failed"),
                _ => _loc.GetString("tab-proxy-status-off"),
            };

            // A re-check of an already-judged profile doesn't change the state, but is worth showing.
            if (_proxyManager.IsChecking && _proxyManager.State != SanabiProxyState.Checking)
                text += " " + _loc.GetString("tab-proxy-status-rechecking");

            return text;
        }
    }

    public string? StatusDetail => _proxyManager.State == SanabiProxyState.Failed ? _proxyManager.LastError : null;

    // Binding; do not rename/remove/change signature
    public void AddProfile()
    {
        var profile = _proxyManager.AddProfile(_loc.GetString("tab-proxy-default-name", ("number", _proxyManager.Profiles.Count + 1)));

        // Selecting it also puts it in use; harmless while it has no proxies in it, and it's what's being edited.
        _proxyManager.SelectProfile(profile.Id);
    }

    // Binding; do not rename/remove/change signature
    public void RemoveSelectedProfile()
    {
        if (SelectedEntry?.Profile is { } profile)
            _proxyManager.RemoveProfile(profile.Id);
    }

    // Binding; do not rename/remove/change signature
    public void Recheck() => _proxyManager.Recheck();

    internal void SaveProfile(SanabiProxyProfile profile) => _proxyManager.UpdateProfile(profile);

    private void Refresh()
    {
        // Rebuild the list only when the profiles themselves changed, so health updates don't reset it.
        if (!ReferenceEquals(_shownProfiles, _proxyManager.Profiles))
        {
            _shownProfiles = _proxyManager.Profiles;

            _syncingSelection = true;
            Entries.Clear();
            Entries.Add(new SanabiProxyProfileEntryViewModel(null, _loc.GetString("tab-proxy-no-profile"), _loc.GetString("tab-proxy-no-profile-summary")));
            foreach (var profile in _shownProfiles)
                Entries.Add(new SanabiProxyProfileEntryViewModel(profile, profile.Name, Summarize(profile)));
            _syncingSelection = false;
        }

        _syncingSelection = true;
        var selectedId = _proxyManager.SelectedProfile?.Id;
        SelectedEntry = Entries.FirstOrDefault(entry => entry.Profile?.Id == selectedId) ?? Entries[0];
        _syncingSelection = false;

        UpdateEditor();

        this.RaisePropertyChanged(nameof(IsProxyActive));
        this.RaisePropertyChanged(nameof(IsWorking));
        this.RaisePropertyChanged(nameof(IsChecking));
        this.RaisePropertyChanged(nameof(IsFailed));
        this.RaisePropertyChanged(nameof(ScopeText));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(StatusDetail));
    }

    private void UpdateEditor()
    {
        var profile = SelectedEntry?.Profile;

        // Keep an editor (and its unsaved changes) across refreshes of the same stored profile.
        if (ReferenceEquals(Editor?.Original, profile))
            return;

        Editor = profile == null ? null : new SanabiProxyProfileEditorViewModel(this, profile);
    }

    private string Summarize(SanabiProxyProfile profile)
    {
        var direct = _loc.GetString("proxy-banner-direct");
        return _loc.GetString("tab-proxy-profile-summary",
            ("launcher", profile.Launcher?.DisplayString ?? direct),
            ("game", profile.Game?.DisplayString ?? direct));
    }
}

public sealed class SanabiProxyProfileEntryViewModel
{
    public SanabiProxyProfileEntryViewModel(SanabiProxyProfile? profile, string name, string summary)
    {
        Profile = profile;
        Name = name;
        Summary = summary;
    }

    /// <summary>
    ///     Null for the "no proxy" entry.
    /// </summary>
    public SanabiProxyProfile? Profile { get; }

    public string Name { get; }
    public string Summary { get; }
}

/// <summary>
///     Edit buffer for one profile; nothing reaches the stored profile until <see cref="Save"/>.
/// </summary>
public sealed class SanabiProxyProfileEditorViewModel : ReactiveObject
{
    /// <summary>
    ///     Launcher protocol choices, in <see cref="SanabiProxyProtocol"/> order (the index is the enum value).
    /// </summary>
    private static readonly IReadOnlyList<string> LauncherProtocolNames = ["HTTP", "HTTPS", "SOCKS4", "SOCKS4a", "SOCKS5"];

    // Binding; do not rename/remove/change signature
    public IReadOnlyList<string> LauncherProtocols => LauncherProtocolNames;

    private readonly SanabiProxyTabViewModel _parent;
    private readonly LocalizationManager _loc = LocalizationManager.Instance;

    private string _name = string.Empty;
    private bool _launcherEnabled;
    private int _launcherProtocolIndex = (int)SanabiProxyProtocol.Socks5;
    private string _launcherHost = string.Empty;
    private string _launcherPort = string.Empty;
    private string _launcherUsername = string.Empty;
    private string _launcherPassword = string.Empty;
    private bool _gameEnabled;
    private string _gameHost = string.Empty;
    private string _gamePort = string.Empty;
    private string _gameUsername = string.Empty;
    private string _gamePassword = string.Empty;
    private string? _errorText;
    private bool _isDirty;

    public SanabiProxyProfileEditorViewModel(SanabiProxyTabViewModel parent, SanabiProxyProfile original)
    {
        _parent = parent;
        Original = original;
        LoadFrom(original);
    }

    /// <summary>
    ///     The stored profile this edits.
    /// </summary>
    public SanabiProxyProfile Original { get; }

    public string Name { get => _name; set => SetField(ref _name, value); }

    public bool LauncherEnabled
    {
        get => _launcherEnabled;
        set
        {
            SetField(ref _launcherEnabled, value);
            this.RaisePropertyChanged(nameof(CanCopyLauncherToGame));
        }
    }

    public int LauncherProtocolIndex
    {
        get => _launcherProtocolIndex;
        set
        {
            SetField(ref _launcherProtocolIndex, value);
            this.RaisePropertyChanged(nameof(LauncherIsSocks4));
            this.RaisePropertyChanged(nameof(CanCopyLauncherToGame));
        }
    }

    public string LauncherHost { get => _launcherHost; set => SetField(ref _launcherHost, value); }
    public string LauncherPort { get => _launcherPort; set => SetField(ref _launcherPort, value); }
    public string LauncherUsername { get => _launcherUsername; set => SetField(ref _launcherUsername, value); }
    public string LauncherPassword { get => _launcherPassword; set => SetField(ref _launcherPassword, value); }

    public bool GameEnabled { get => _gameEnabled; set => SetField(ref _gameEnabled, value); }
    public string GameHost { get => _gameHost; set => SetField(ref _gameHost, value); }
    public string GamePort { get => _gamePort; set => SetField(ref _gamePort, value); }
    public string GameUsername { get => _gameUsername; set => SetField(ref _gameUsername, value); }
    public string GamePassword { get => _gamePassword; set => SetField(ref _gamePassword, value); }

    public bool LauncherIsSocks4 => (SanabiProxyProtocol)LauncherProtocolIndex is SanabiProxyProtocol.Socks4 or SanabiProxyProtocol.Socks4a;

    /// <summary>
    ///     Only a SOCKS5 launcher proxy can double as the game proxy.
    /// </summary>
    public bool CanCopyLauncherToGame => LauncherEnabled && (SanabiProxyProtocol)LauncherProtocolIndex == SanabiProxyProtocol.Socks5;

    public string? ErrorText
    {
        get => _errorText;
        private set => this.RaiseAndSetIfChanged(ref _errorText, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => this.RaiseAndSetIfChanged(ref _isDirty, value);
    }

    // Binding; do not rename/remove/change signature
    public void CopyLauncherToGame()
    {
        GameEnabled = true;
        GameHost = LauncherHost;
        GamePort = LauncherPort;
        GameUsername = LauncherUsername;
        GamePassword = LauncherPassword;
    }

    // Binding; do not rename/remove/change signature
    public void Save()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorText = _loc.GetString("tab-proxy-error-name");
            return;
        }

        var profile = new SanabiProxyProfile { Id = Original.Id, Name = Name.Trim() };

        if (LauncherEnabled)
        {
            profile.Launcher = BuildEndpoint((SanabiProxyProtocol)LauncherProtocolIndex, LauncherHost, LauncherPort, LauncherUsername, LauncherPassword);
            if (!profile.Launcher.IsValid(requireUdp: false, out var error))
            {
                ErrorText = DescribeError(error, "tab-proxy-which-launcher");
                return;
            }
        }

        if (GameEnabled)
        {
            profile.Game = BuildEndpoint(SanabiProxyProtocol.Socks5, GameHost, GamePort, GameUsername, GamePassword);
            if (!profile.Game.IsValid(requireUdp: true, out var error))
            {
                ErrorText = DescribeError(error, "tab-proxy-which-game");
                return;
            }
        }

        ErrorText = null;
        IsDirty = false;

        // Replaces the stored profile; the tab then builds a fresh editor for the new one.
        _parent.SaveProfile(profile);
    }

    // Binding; do not rename/remove/change signature
    public void Revert()
    {
        LoadFrom(Original);
        ErrorText = null;
        IsDirty = false;
    }

    private void LoadFrom(SanabiProxyProfile profile)
    {
        Name = profile.Name;

        LauncherEnabled = profile.Launcher != null;
        LauncherProtocolIndex = (int)(profile.Launcher?.Protocol ?? SanabiProxyProtocol.Socks5);
        LauncherHost = profile.Launcher?.Host ?? string.Empty;
        LauncherPort = profile.Launcher?.Port.ToString() ?? string.Empty;
        LauncherUsername = profile.Launcher?.Username ?? string.Empty;
        LauncherPassword = profile.Launcher?.Password ?? string.Empty;

        GameEnabled = profile.Game != null;
        GameHost = profile.Game?.Host ?? string.Empty;
        GamePort = profile.Game?.Port.ToString() ?? string.Empty;
        GameUsername = profile.Game?.Username ?? string.Empty;
        GamePassword = profile.Game?.Password ?? string.Empty;

        IsDirty = false;
    }

    private static SanabiProxyEndpoint BuildEndpoint(SanabiProxyProtocol protocol, string host, string port, string username, string password)
        => new()
        {
            Protocol = protocol,
            Host = host.Trim(),
            // An unparseable port becomes 0, which validation then rejects with the port message.
            Port = int.TryParse(port.Trim(), out var parsedPort) ? parsedPort : 0,
            Username = username,
            // SOCKS4/4a have no passwords; don't keep one around that will never be used.
            Password = protocol is SanabiProxyProtocol.Socks4 or SanabiProxyProtocol.Socks4a ? string.Empty : password,
        };

    private string DescribeError(SanabiProxyValidationError error, string whichKey)
    {
        var which = _loc.GetString(whichKey);
        return error switch
        {
            SanabiProxyValidationError.BadHost => _loc.GetString("tab-proxy-error-bad-host", ("which", which)),
            SanabiProxyValidationError.BadPort => _loc.GetString("tab-proxy-error-bad-port", ("which", which)),
            SanabiProxyValidationError.NoUdp => _loc.GetString("tab-proxy-error-no-udp"),
            SanabiProxyValidationError.CredentialsTooLong => _loc.GetString("tab-proxy-error-credentials-too-long", ("which", which)),
            _ => error.ToString(),
        };
    }

    private void SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        this.RaisePropertyChanged(propertyName);
        IsDirty = true;
    }
}
