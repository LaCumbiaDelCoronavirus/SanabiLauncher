namespace SS14.Launcher.ViewModels;

public sealed class ChangelogEntryViewModel(string version, string changes) : ViewModelBase
{
    public string Version { get; } = version;
    public string Changes { get; } = changes;
}
