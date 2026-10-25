using Splat;
using SS14.Launcher.Localization;
using SS14.Launcher.Models.Data;
using SS14.Launcher.Utility;
using SS14.Common.Data.CVars;
using System.Diagnostics;
using Sanabi.Framework.Data;
using System.Collections.ObjectModel;
using Sanabi.Framework.Game.Managers;
using System;
using System.Linq;
using ReactiveUI;
using System.IO;

namespace SS14.Launcher.ViewModels.MainWindowTabs;

public class SanabiTabViewModel : MainWindowTabViewModel
{
    public DataManager Cfg { get; }

    public ObservableCollection<LoadedPatchViewmodel> PatchList { get; set; } = new();

    public MainWindowViewModel MainWindowViewModel;

    public SanabiTabViewModel(MainWindowViewModel mainWindowViewModel)
    {
        MainWindowViewModel = mainWindowViewModel;

        Cfg = Locator.Current.GetRequiredService<DataManager>();
        RefreshMods();

        LazySanabiConfig.PingServers = Cfg.GetCVar(SanabiCVars.PingServers);
        LazySanabiConfig.RandomiseServerPingQueryDelay = Cfg.GetCVar(SanabiCVars.RandomiseServerPingQueryDelay);
    }

    // Binding; do not rename/remove/change signature
    public void RefreshMods()
    {
        PatchList.Clear();

        if (!AssemblyLoadingManager.TryGetExternalMods(out var externalMods))
            return;

        var enabledMods = EnabledExternalModsCVar.Parse(Cfg.GetCVar(SanabiCVars.EnabledExternalMods)).ToHashSet();

        foreach (var mod in externalMods)
            PatchList.Add(new LoadedPatchViewmodel(this, mod.Name, enabledMods.Contains(mod.Name)));
    }

    /// <summary>
    ///     Adds or removes <paramref name="modName"/> from the persisted set of enabled external mods.
    /// </summary>
    internal void SetModEnabled(string modName, bool enabled)
    {
        var enabledMods = EnabledExternalModsCVar.Parse(Cfg.GetCVar(SanabiCVars.EnabledExternalMods)).ToHashSet();

        if (enabled)
            enabledMods.Add(modName);
        else
            enabledMods.Remove(modName);

        SetAndCommitCvar(SanabiCVars.EnabledExternalMods, EnabledExternalModsCVar.Serialize(enabledMods));
    }

    internal void SetAndCommitCvar<T>(CVarDef<T> cVarDef, T newValue)
    {
        Cfg.SetCVar(cVarDef, newValue);
        Cfg.CommitConfig();
    }

    // Binding; do not rename/remove/change signature
    public static void OpenModDirectory()
    {
        Process.Start(new ProcessStartInfo
        {
            UseShellExecute = true,
            FileName = LauncherPaths.SanabiModsPath
        });
    }

    public override string Name => LocalizationManager.Instance.GetString("tab-sanabi-title");

    public bool PatchingEnabled
    {
        get => Cfg.GetCVar(SanabiCVars.PatchingEnabled);
        set => SetAndCommitCvar(SanabiCVars.PatchingEnabled, value);
    }

    public bool PatchingLevel
    {
        get => Cfg.GetCVar(SanabiCVars.PatchingLevel);
        set => SetAndCommitCvar(SanabiCVars.PatchingLevel, value);
    }

    public bool HwidPatchEnabled
    {
        get => Cfg.GetCVar(SanabiCVars.HwidPatchEnabled);
        set => SetAndCommitCvar(SanabiCVars.HwidPatchEnabled, value);
    }

    public bool UserDataPatchEnabled
    {
        get => Cfg.GetCVar(SanabiCVars.UserDataPatchEnabled);
        set => SetAndCommitCvar(SanabiCVars.UserDataPatchEnabled, value);
    }

    public bool BetterFullscreenPatchEnabled
    {
        get => Cfg.GetCVar(SanabiCVars.BetterFullscreenPatchEnabled);
        set => SetAndCommitCvar(SanabiCVars.BetterFullscreenPatchEnabled, value);
    }

    public bool LoadInternalMods
    {
        get => Cfg.GetCVar(SanabiCVars.LoadInternalMods);
        set => SetAndCommitCvar(SanabiCVars.LoadInternalMods, value);
    }

    public bool LoadExternalMods
    {
        get => Cfg.GetCVar(SanabiCVars.LoadExternalMods);
        set => SetAndCommitCvar(SanabiCVars.LoadExternalMods, value);
    }

    public bool PassFingerprint
    {
        get => Cfg.GetCVar(SanabiCVars.PassFingerprint);
        set
        {
            Cfg.SetCVar(SanabiCVars.PassFingerprint, value);
            Cfg.CommitConfig();
        }
    }

    public bool PassSpoofedFingerprint
    {
        get => Cfg.GetCVar(SanabiCVars.PassSpoofedFingerprint);
        set
        {
            Cfg.SetCVar(SanabiCVars.PassSpoofedFingerprint, value);
            Cfg.CommitConfig();
        }
    }

    public bool AllowHwid
    {
        get => Cfg.GetCVar(SanabiCVars.AllowHwid);
        set
        {
            Cfg.SetCVar(SanabiCVars.AllowHwid, value);
            Cfg.CommitConfig();
        }
    }

    public bool StartOnLoginMenu
    {
        get => Cfg.GetCVar(SanabiCVars.StartOnLoginMenu);
        set
        {
            Cfg.SetCVar(SanabiCVars.StartOnLoginMenu, value);
            Cfg.CommitConfig();
        }
    }

    public string SpoofingSeedText
    {
        get => BitConverter.ToUInt64(BitConverter.GetBytes(Cfg.GetActiveAccountCVarOrDefault(SanabiAccountCVars.SpoofingSeed)), 0).ToString();
        set
        {
            if (ulong.TryParse(value, out var ulongValue) &&
                Cfg.TrySetActiveAccountCVar(SanabiAccountCVars.SpoofingSeed, BitConverter.ToInt64(BitConverter.GetBytes(ulongValue), 0)))
            {
                Cfg.CommitConfig();
            }

            this.RaisePropertyChanged(propertyName: nameof(SpoofingSeedText));
        }
    }

    public bool PingServers
    {
        get => Cfg.GetCVar(SanabiCVars.PingServers);
        set
        {
            Cfg.SetCVar(SanabiCVars.PingServers, value);
            Cfg.CommitConfig();

            LazySanabiConfig.PingServers = value;
        }
    }

    public bool RandomiseServerPingQueryDelay
    {
        get => Cfg.GetCVar(SanabiCVars.RandomiseServerPingQueryDelay);
        set
        {
            Cfg.SetCVar(SanabiCVars.RandomiseServerPingQueryDelay, value);
            Cfg.CommitConfig();

            LazySanabiConfig.RandomiseServerPingQueryDelay = value;
        }
    }

    public bool FancyBackground
    {
        get => Cfg.GetCVar(SanabiCVars.FancyBackground);
        set
        {
            Cfg.SetCVar(SanabiCVars.FancyBackground, value);
            Cfg.CommitConfig();

            MainWindowViewModel.SetFancyBackground(value);
        }
    }

    public bool HarmonyDebug
    {
        get => Cfg.GetCVar(SanabiCVars.HarmonyDebug);
        set => SetAndCommitCvar(SanabiCVars.HarmonyDebug, value);
    }

    public bool WaitForDebugger
    {
        get => Cfg.GetCVar(SanabiCVars.WaitForDebugger);
        set => SetAndCommitCvar(SanabiCVars.WaitForDebugger, value);
    }

    public static void OpenLauncherDataDirectory()
    {
        Process.Start(new ProcessStartInfo
        {
            UseShellExecute = true,
            FileName = LauncherPaths.DirUserData
        });
    }

    /// <summary>
    ///     Regenerates <see cref="SanabiAccountCVars.SpoofingSeed"/>
    ///         to something random.
    /// </summary>
    // Binding; do not rename/remove/change signature
    public void RegenerateAccountSeed()
    {
        var bytes = (Span<byte>)stackalloc byte[8];
        new Random().NextBytes(bytes);

        // setting cvar is redundant here
        SpoofingSeedText = BitConverter.ToUInt64(bytes).ToString();
    }
}

public class LoadedPatchViewmodel : ViewModelBase
{
    private readonly SanabiTabViewModel _parentVm;

    private bool _isEnabled;

    public LoadedPatchViewmodel(SanabiTabViewModel parentVm, string filename, bool isEnabled)
    {
        _parentVm = parentVm;
        Filename = filename;
        _isEnabled = isEnabled;
    }

    // Binding; do not rename/remove/change signature
    public string Filename { get; set; }

    // Binding; do not rename/remove/change signature
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
                return;

            _isEnabled = value;
            _parentVm.SetModEnabled(Filename, value);
            this.RaisePropertyChanged();
        }
    }
}
