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
    private const string FallbackLocale = "en-US";

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
        var messages = GetForCurrentCulture(_model?.Messages);
        if (messages == null || messages.Length == 0)
            return null;

        return messages[_messageRandom.Next(messages.Length)];
    }

    /// <summary>
    ///     Returns the changelog with each entry's changes resolved for the launcher's currently selected
    ///     language (see <see cref="GetForCurrentCulture{T}"/>). Entries with no usable text are dropped.
    /// </summary>
    public (string Version, string Changes)[] GetLocalizedChangelog()
    {
        if (_model == null)
            return [];

        return _model.ChangelogEntries
            .Select(e => (e.Version, Changes: GetForCurrentCulture(e.Changes)))
            .Where(e => !string.IsNullOrWhiteSpace(e.Changes))
            .Select(e => (e.Version, e.Changes!))
            .ToArray();
    }

    /// <summary>
    ///     Picks the value matching the launcher's currently selected language
    ///     (falling back to parent cultures, then <see cref="FallbackLocale"/>).
    /// </summary>
    private static T? GetForCurrentCulture<T>(Dictionary<string, T>? localized) where T : class
    {
        if (localized == null)
            return null;

        for (var culture = CultureInfo.CurrentUICulture;
             !culture.Equals(CultureInfo.InvariantCulture);
             culture = culture.Parent)
        {
            if (localized.TryGetValue(culture.Name, out var value))
                return value;
        }

        return localized.GetValueOrDefault(FallbackLocale);
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
            Changelog?.Where(e => !string.IsNullOrWhiteSpace(e?.Version) && e.Changes is { Count: > 0 }).ToArray() ?? [];

        /// <summary>
        ///     Optional media URL to show below the changelog. Guaranteed non-null (empty if unset/invalid).
        /// </summary>
        [JsonIgnore]
        public string SafeChangelogMediaUrl => ChangelogMediaUrl ?? "";
    }

    public sealed record ChangelogEntry(
        [property:JsonPropertyName("version")]
        string Version,

        /// <summary>
        ///     Per-locale changelog text. In JSON this is either a locale -> text object, or a plain
        ///     string (legacy format), which is treated as <see cref="FallbackLocale"/> text.
        /// </summary>
        [property:JsonPropertyName("changes")]
        [property:JsonConverter(typeof(LocalizedStringConverter))]
        Dictionary<string, string> Changes
    );

    /// <summary>
    ///     Reads either a plain string or a locale -> string object into a locale -> string dictionary.
    /// </summary>
    private sealed class LocalizedStringConverter : JsonConverter<Dictionary<string, string>>
    {
        public override Dictionary<string, string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.String:
                    return new Dictionary<string, string> { [FallbackLocale] = reader.GetString()! };
                case JsonTokenType.StartObject:
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string?>>(ref reader, options);
                    return dict?
                        .Where(kv => kv.Value != null)
                        .ToDictionary(kv => kv.Key, kv => kv.Value!);
                default:
                    throw new JsonException($"Expected string or object for localized text, got {reader.TokenType}.");
            }
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<string, string> value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }
}
