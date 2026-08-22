using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Sanabi.Framework.Data;
using Serilog;

namespace SS14.Launcher.Models;

/// <summary>
/// Fetches and caches information from <see cref="ConfigConstants.UrlLauncherInfo"/>.
/// </summary>
public sealed class LauncherInfoManager(HttpClient httpClient)
{
    private const string FallbackMessageLocale = "en-US";

    private readonly Random _messageRandom = new();

    private LauncherInfoModel? _model;

    public LauncherInfoModel? Model
    {
        get
        {
            if (!LoadTask.IsCompleted)
                throw new InvalidOperationException("Data has not been loaded yet");

            return _model;
        }
    }

    public Task LoadTask { get; private set; } = default!;

    public void Initialize()
    {
        LoadTask = LoadData();
    }

    private async Task LoadData()
    {
        var cancellationToken = new CancellationTokenSource(2200).Token;
        LauncherInfoModel? info;
        try
        {
            Log.Debug("Loading launcher info... {Url}", ConfigConstants.UrlLauncherInfo);
            info = await ConfigConstants.UrlLauncherInfo.GetFromJsonAsync<LauncherInfoModel>(httpClient, cancel: cancellationToken);
            if (info == null)
            {
                Log.Warning("Launcher info response was null.");
                return;
            }

        }
        catch (Exception e)
        {
            Log.Error($"Failed to load launcher info! Using fallback. Exception: {e}");
            info = JsonSerializer.Deserialize<LauncherInfoModel>(SanabiGlobal.FallbackLauncherInfoData, options: JsonSerializerOptions.Web);
        }

        _model = info;
    }

    public string? GetRandomMessage()
    {
        var messages = GetMessagesForCurrentCulture();
        if (messages == null || messages.Length == 0)
            return null;

        return messages[_messageRandom.Next(messages.Length)];
    }

    /// <summary>
    ///     Picks the message list matching the launcher's currently selected language
    ///     (falling back to parent cultures, then <see cref="FallbackMessageLocale"/>).
    /// </summary>
    private string[]? GetMessagesForCurrentCulture()
    {
        var messages = _model?.Messages;
        if (messages == null)
            return null;

        for (var culture = CultureInfo.CurrentUICulture;
             !culture.Equals(CultureInfo.InvariantCulture);
             culture = culture.Parent)
        {
            if (messages.TryGetValue(culture.Name, out var localeMessages))
                return localeMessages;
        }

        return messages.GetValueOrDefault(FallbackMessageLocale);
    }

    public sealed record LauncherInfoModel(
        [property:JsonPropertyName("messages")]
        Dictionary<string, string[]> Messages,

        [property:JsonPropertyName("allowedVersions")]
        string[] AllowedVersions,

        [property:JsonPropertyName("overrideAssets")]
        Dictionary<string, string?> OverrideAssets,

        [property:JsonPropertyName("changelog")]
        ChangelogEntry[]? Changelog = null,

        [property:JsonPropertyName("changelogMediaUrl")]
        string ChangelogMediaUrl = ""
    )
    {
        /// <summary>
        ///     Changelog entries, guaranteed non-null and free of malformed entries even if the
        ///     remote data omits or malforms the field.
        /// </summary>
        [JsonIgnore]
        public ChangelogEntry[] ChangelogEntries =>
            Changelog?.Where(e => !string.IsNullOrWhiteSpace(e?.Version) && e.Changes != null).ToArray() ?? [];

        /// <summary>
        ///     Optional media URL to show below the changelog. Guaranteed non-null (empty if unset/invalid).
        /// </summary>
        [JsonIgnore]
        public string SafeChangelogMediaUrl => ChangelogMediaUrl ?? "";
    }

    public sealed record ChangelogEntry(
        [property:JsonPropertyName("version")]
        string Version,

        [property:JsonPropertyName("changes")]
        string Changes
    );
}
