using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sanabi.Framework.Data;

namespace Sanabi.Framework.Proxy;

/// <summary>
///     Wire protocol spoken to a proxy server. Serialized by name, so the explicit values only matter to the
///         UI's protocol picker (which lists them in this order).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SanabiProxyProtocol>))]
public enum SanabiProxyProtocol
{
    /// <summary>Plain-text HTTP proxy, tunnelling with <c>CONNECT</c>.</summary>
    Http = 0,

    /// <summary>HTTP proxy reached over TLS, tunnelling with <c>CONNECT</c>.</summary>
    Https = 1,

    /// <summary>SOCKS4: TCP only, IPv4 only, target resolved locally.</summary>
    Socks4 = 2,

    /// <summary>SOCKS4a: TCP only, target hostname resolved by the proxy.</summary>
    Socks4a = 3,

    /// <summary>SOCKS5: TCP and UDP, target hostname resolved by the proxy. The only protocol the game supports.</summary>
    Socks5 = 4,
}

/// <summary>
///     One proxy server: where it is, how to talk to it, and optional credentials.
/// </summary>
public sealed class SanabiProxyEndpoint
{
    public SanabiProxyProtocol Protocol { get; set; } = SanabiProxyProtocol.Socks5;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1080;

    /// <summary>
    ///     Username for proxy authentication; empty means none. For SOCKS4/4a this is sent as the user ID.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    ///     Password for proxy authentication. Ignored by SOCKS4/4a, which have no passwords.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    public bool HasCredentials => Username.Length > 0;

    /// <summary>
    ///     Whether this protocol can carry UDP, which the game needs.
    /// </summary>
    public bool SupportsUdp => Protocol == SanabiProxyProtocol.Socks5;

    public string Scheme => GetScheme(Protocol);

    /// <summary>
    ///     <c>scheme://host:port</c> with no credentials; safe to show or log.
    /// </summary>
    public string DisplayString => $"{Scheme}://{FormatHost(Host)}:{Port}";

    public static string GetScheme(SanabiProxyProtocol protocol) => protocol switch
    {
        SanabiProxyProtocol.Http => "http",
        SanabiProxyProtocol.Https => "https",
        SanabiProxyProtocol.Socks4 => "socks4",
        SanabiProxyProtocol.Socks4a => "socks4a",
        SanabiProxyProtocol.Socks5 => "socks5",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null),
    };

    /// <summary>
    ///     The proxy URI, in the form <see cref="System.Net.Http.SocketsHttpHandler"/> understands. Credentials
    ///         are deliberately left out; they go through <see cref="GetCredential"/>.
    /// </summary>
    public Uri ToUri() => new(DisplayString);

    public NetworkCredential? GetCredential() => HasCredentials ? new NetworkCredential(Username, Password) : null;

    /// <summary>
    ///     A static <see cref="WebProxy"/> for this endpoint that never bypasses, not even for local addresses.
    /// </summary>
    public WebProxy CreateWebProxy() => new(ToUri())
    {
        BypassProxyOnLocal = false,
        Credentials = GetCredential(),
    };

    /// <summary>
    ///     Whether <paramref name="uri"/> points at this proxy (same host and port), for credential lookup.
    /// </summary>
    public bool Matches(Uri uri)
        => uri.Port == Port && string.Equals(uri.IdnHost, Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Checks the fields are usable. The UI maps <paramref name="error"/> to a localized message.
    /// </summary>
    /// <param name="requireUdp">Whether the endpoint must be able to carry UDP (true for the game proxy).</param>
    public bool IsValid(bool requireUdp, out SanabiProxyValidationError error)
    {
        error = SanabiProxyValidationError.None;

        if (string.IsNullOrWhiteSpace(Host) || Host.Any(char.IsWhiteSpace) || Host.Contains('/') || Host.Contains('@'))
            error = SanabiProxyValidationError.BadHost;
        else if (Port is < 1 or > 65535)
            error = SanabiProxyValidationError.BadPort;
        else if (requireUdp && !SupportsUdp)
            error = SanabiProxyValidationError.NoUdp;
        else if (IsTooLong(Username) || IsTooLong(Password))
            error = SanabiProxyValidationError.CredentialsTooLong;

        return error == SanabiProxyValidationError.None;

        // SOCKS5 length-prefixes credentials with a single byte.
        static bool IsTooLong(string value) => System.Text.Encoding.UTF8.GetByteCount(value) > 255;
    }

    public SanabiProxyEndpoint Clone() => (SanabiProxyEndpoint)MemberwiseClone();

    private static string FormatHost(string host)
        => IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;
}

public enum SanabiProxyValidationError
{
    None,
    BadHost,
    BadPort,
    NoUdp,
    CredentialsTooLong,
}

/// <summary>
///     A named set of proxies. Profiles are shared across accounts; which one an account uses is per-account
///         (see <see cref="SanabiAccountCVars.ProxyProfile"/>).
/// </summary>
public sealed class SanabiProxyProfile
{
    /// <summary>
    ///     Stable identifier that accounts reference; never shown.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Proxy for the launcher's own HTTP(S) traffic (hub, auth, downloads), or null to connect directly.
    /// </summary>
    public SanabiProxyEndpoint? Launcher { get; set; }

    /// <summary>
    ///     SOCKS5 proxy for the game: its UDP game traffic and its HTTP(S) requests. Null to connect directly.
    /// </summary>
    public SanabiProxyEndpoint? Game { get; set; }

    public bool HasAnyProxy => Launcher != null || Game != null;

    public bool IsValid(out SanabiProxyValidationError error)
    {
        error = SanabiProxyValidationError.None;
        return (Launcher?.IsValid(requireUdp: false, out error) ?? true) &&
               (Game?.IsValid(requireUdp: true, out error) ?? true);
    }

    public SanabiProxyProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Launcher = Launcher?.Clone(),
        Game = Game?.Clone(),
    };
}

/// <summary>
///     (De)serialization for the <see cref="SanabiCVars.ProxyProfiles"/> CVar. The config DB only persists
///         primitive-typed CVars, so the profile list rides inside a string as a JSON array.
/// </summary>
public static class SanabiProxyProfilesCVar
{
    public static List<SanabiProxyProfile> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new();

        try
        {
            var profiles = JsonSerializer.Deserialize(raw, SanabiJsonContext.Default.ListSanabiProxyProfile) ?? new();

            // Drop entries a hand-edited or corrupt DB could leave behind, rather than crash on them later.
            profiles.RemoveAll(profile => string.IsNullOrEmpty(profile?.Id));
            return profiles;
        }
        catch (JsonException)
        {
            // Corrupt value: treat as "no profiles" rather than crashing the launcher.
            return new();
        }
    }

    public static string Serialize(IEnumerable<SanabiProxyProfile> profiles)
        => JsonSerializer.Serialize(profiles.ToList(), SanabiJsonContext.Default.ListSanabiProxyProfile);
}
