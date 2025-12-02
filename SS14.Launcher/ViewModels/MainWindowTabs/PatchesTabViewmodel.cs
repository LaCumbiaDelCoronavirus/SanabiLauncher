using Splat;
using SS14.Launcher.Models.Data;
using SS14.Launcher.Utility;
using SS14.Common.Data.CVars;

namespace SS14.Launcher.ViewModels.MainWindowTabs;

public class PatchesTabViewModel : MainWindowTabViewModel
{
    public DataManager Cfg { get; }

    public PatchesTabViewModel()
    {
        Cfg = Locator.Current.GetRequiredService<DataManager>();
    }

    private void SetAndCommitCvar<T>(CVarDef<T> cVarDef, T newValue)
    {
        Cfg.SetCVar(cVarDef, newValue);
        Cfg.CommitConfig();
    }

    public override string Name => "Patches";

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
}
