using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Sanabi.Framework.Data;
using SS14.Launcher.Models.Logins;
using SS14.Launcher.Utility;

namespace SS14.Launcher.Models.Data;

public static class SanabiAuthManager
{
    public const string DefaultEnterableAuthUrl = "https://auth.spacestation14.com/";

    // sets are | separated
    public const char AuthDataSetSeparator = '|';
    // individual urls are , separated
    public const char AuthDataUrlSeparator = ',';

    /// <summary>
    ///     List of default auth servers for accounts that have none,
    ///         such as accounts added on versions before multiauth.
    /// </summary>
    private static readonly List<AuthServerInfo> DefaultActiveServers = new(
        new HashSet<AuthServerInfo>() {
            { new(ConfigConstants.AuthUrl) },
            //{ new(new(["https://auth.simplestation.org/"])) }
        }
    );
    private static readonly HashSet<AuthServerInfo> _activeServers = [];

    private static DataManager _dataManager = default!;
    public static void Initialize(DataManager dataManager)
    {
        _dataManager = dataManager;
    }

    public static bool TryGetMatchingInfo(string evaluatedAuthUrl, [MaybeNullWhen(false)] out AuthServerInfo authServerInfo)
    {
        foreach (var activeServer in _activeServers)
        {
            if (activeServer.UrlSet.GetMostSuccessfulUrl().Contains(evaluatedAuthUrl))
                continue;

            authServerInfo = activeServer;
            return true;
        }

        authServerInfo = null;
        return false;
    }

    public static List<AuthServerInfo> DeserializeAuthServerDataString(Guid userId) => DeserializeAuthServerDataString(_dataManager.GetAccountCVar(SanabiAccountCVars.AuthServers, userId));

    // holy allocations
    public static List<AuthServerInfo> DeserializeAuthServerDataString(string serializedData)
    {
        var list = new List<AuthServerInfo>();
        foreach (var setString in serializedData.Split(AuthDataSetSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var urlTransientSet = new HashSet<string>();
            foreach (var urlString in setString.Split(AuthDataUrlSeparator, StringSplitOptions.RemoveEmptyEntries))
                urlTransientSet.Add(urlString);

            var urlSet = new UrlFallbackSet([.. urlTransientSet]);
            list.Add(new(urlSet));
        }

        return list;
    }

    public static string SerializeAuthServerDataString(AuthServerInfo deserializedData) => string.Join(AuthDataUrlSeparator, deserializedData.UrlSet.Urls);

    public static string SerializeAuthServerDataString(List<AuthServerInfo> deserializedData)
    {
        var sets = new List<string>();
        foreach (var info in deserializedData)
        {
            // ABSOLUTELY INSANELY DEMENTED
            if (info.UrlSet.Urls.Length == 1 &&
                info.UrlSet.Urls[0] == "https://auth.spacestation14.com/")
            {
                sets.Add(string.Join(AuthDataUrlSeparator, ConfigConstants.AuthUrl.Urls));
                continue;
            }

            sets.Add(SerializeAuthServerDataString(info));
        }

        return string.Join(AuthDataSetSeparator, sets);
    }
    
    public static void OnAccountUpdated(LoggedInAccount? loggedInAccount)
    {
        if (loggedInAccount is not { })
            return;

        // This could be better but IDGAF

        var savedAuthServers = _dataManager.GetAccountCVar(SanabiAccountCVars.AuthServers, loggedInAccount.UserId);
        List<AuthServerInfo> specifiedData;

        if (savedAuthServers != SanabiAccountCVars.AuthServersDefaultSerializedNullEscapeValue)
            specifiedData = DeserializeAuthServerDataString(savedAuthServers);
        else
            specifiedData = [.. DefaultActiveServers]; // its cloned

        loggedInAccount.SupportedAuthServers = specifiedData;
        _dataManager.SetAccountCVar(SanabiAccountCVars.AuthServers, loggedInAccount.UserId, SerializeAuthServerDataString(specifiedData));
    }

    public static string EnsureAuthStringEndsInPath(string url) => url.EndsWith('/') ? url : url + '/';

    public static AuthServerInfo LazilyGetInfoFromUrl(string url) => new(new([EnsureAuthStringEndsInPath(url)]));
}

/// <param name="Url">Should probably end with a `/`</param>
public record class AuthServerInfo(UrlFallbackSet UrlSet)
{
    // This can be overriden if i ever add the ability to add authservers that have custom URL paths for all this bs
    public virtual UrlFallbackSet GetAuthUrl() => UrlSet + "api/auth/authenticate";
    public virtual UrlFallbackSet GetRegisterUrl() => UrlSet + "api/auth/register";
    public virtual UrlFallbackSet GetResetPasswordUrl() => UrlSet + "api/auth/resetPassword";
    public virtual UrlFallbackSet GetResendConfirmationUrl() => UrlSet + "api/auth/resendConfirmation";
    public virtual UrlFallbackSet GetRefreshUrl() => UrlSet + "api/auth/refresh";
    public virtual UrlFallbackSet GetLogoutUrl() => UrlSet + "api/auth/logout";
    public virtual UrlFallbackSet GetPingUrl() => UrlSet + "api/auth/ping";
}
