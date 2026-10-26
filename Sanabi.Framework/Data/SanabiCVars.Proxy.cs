using SS14.Common.Data.CVars;

namespace Sanabi.Framework.Data;

public static partial class SanabiCVars
{
    /// <summary>
    ///     All proxy profiles, shared across accounts, as a JSON array of <see cref="Proxy.SanabiProxyProfile"/>
    ///         (see <see cref="Proxy.SanabiProxyProfilesCVar"/>). Which one is in use is per-account:
    ///         <see cref="SanabiAccountCVars.ProxyProfile"/>.
    /// </summary>
    public static readonly CVarDef<string> ProxyProfiles = CVarDef.Create("ProxyProfiles", "[]");

    /// <summary>
    ///     Profile id used while no account is active: whatever the last active account used. Without this,
    ///         logging out (or logging into a new account) would send the auth requests out directly.
    /// </summary>
    public static readonly CVarDef<string> LoggedOutProxyProfile = CVarDef.Create("LoggedOutProxyProfile", string.Empty);
}
