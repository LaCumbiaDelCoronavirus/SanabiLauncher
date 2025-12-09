using System;
using System.Diagnostics;
using Sanabi.Framework.Data;
using Splat;
using SS14.Launcher.Localization;
using SS14.Launcher.Models.ContentManagement;
using SS14.Launcher.Models.Data;
using SS14.Launcher.Models.EngineManager;
using SS14.Launcher.Utility;

namespace SS14.Launcher.ViewModels.MainWindowTabs;

public class OptionsTabViewModel : MainWindowTabViewModel
{
    public DataManager Cfg { get; }
    private readonly IEngineManager _engineManager;
    private readonly ContentManager _contentManager;

    public LanguageSelectorViewModel Language { get; } = new();

    public OptionsTabViewModel()
    {
        Cfg = Locator.Current.GetRequiredService<DataManager>();
        _engineManager = Locator.Current.GetRequiredService<IEngineManager>();
        _contentManager = Locator.Current.GetRequiredService<ContentManager>();

        DisableIncompatibleMacOS = OperatingSystem.IsMacOS();
    }
    public bool DisableIncompatibleMacOS { get; }

#if RELEASE
        public bool HideDisableSigning => true;
#else
    public bool HideDisableSigning => false;
#endif

    public override string Name => LocalizationManager.Instance.GetString("tab-options-title");

    public bool CompatMode
    {
        get => Cfg.GetCVar(CVars.CompatMode);
        set
        {
            Cfg.SetCVar(CVars.CompatMode, value);
            Cfg.CommitConfig();
        }
    }

    public bool LogLauncherVerbose
    {
        get => Cfg.GetCVar(CVars.LogLauncherVerbose);
        set
        {
            Cfg.SetCVar(CVars.LogLauncherVerbose, value);
            Cfg.CommitConfig();
        }
    }

    public bool DisableSigning
    {
        get => Cfg.GetCVar(CVars.DisableSigning);
        set
        {
            Cfg.SetCVar(CVars.DisableSigning, value);
            Cfg.CommitConfig();
        }
    }

    public bool OverrideAssets
    {
        get => Cfg.GetCVar(CVars.OverrideAssets);
        set
        {
            Cfg.SetCVar(CVars.OverrideAssets, value);
            Cfg.CommitConfig();
        }
    }

    #region Sanabi
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

    public bool SpoofFingerprintOnLogin
    {
        get => Cfg.GetCVar(SanabiCVars.SpoofFingerprintOnLogin);
        set
        {
            Cfg.SetCVar(SanabiCVars.SpoofFingerprintOnLogin, value);
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

    public bool StartLoggedIn
    {
        get => Cfg.GetCVar(SanabiCVars.StartLoggedIn);
        set
        {
            Cfg.SetCVar(SanabiCVars.StartLoggedIn, value);
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
    #endregion

    public void ClearEngines()
    {
        _engineManager.ClearAllEngines();
    }

    public void ClearServerContent()
    {
        _contentManager.ClearAll();
    }

    public void OpenLogDirectory()
    {
        Process.Start(new ProcessStartInfo
        {
            UseShellExecute = true,
            FileName = LauncherPaths.DirLogs
        });
    }

    public void OpenAccountSettings()
    {
        Helpers.OpenUri(ConfigConstants.AccountManagementUrl);
    }
}
