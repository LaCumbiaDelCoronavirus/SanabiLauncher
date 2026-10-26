using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Toolkit.Mvvm.ComponentModel;
using Sanabi.Framework.Data;
using Sanabi.Framework.Misc.Net;

namespace SS14.Launcher.Models.ServerStatus;

public sealed class ServerStatusData : ObservableObject, IServerStatusData
{
    private static Random SharedRandom = Random.Shared;
    private string? _name;
    private string? _desc;
    private TimeSpan? _ping;
    private int _playerCount;
    private int _softMaxPlayerCount;
    private DateTime? _roundStartTime;
    private GameRoundStatus _roundStatus;
    private ServerStatusCode _status = ServerStatusCode.FetchingStatus;
    private ServerStatusInfoCode _statusInfo = ServerStatusInfoCode.NotFetched;
    private ServerInfoLink[]? _links;
    private string[] _tags = Array.Empty<string>();

    public ServerStatusData(string address)
    {
        Address = address;
    }

    public ServerStatusData(string address, string hubAddress)
    {
        Address = address;
        HubAddress = hubAddress;
    }

    public async Task UpdateMiscData()
    {
        TrueAddress = "Fetching ping…";

        // ICMP can't go through a proxy, and the DNS lookup would happen outside it too.
        if (LazySanabiConfig.ProxyActive)
        {
            TrueAddress = "N/A [hidden while a proxy profile is in use]";
            _baseDisplayedPing = "N/A [pinging disabled while a proxy profile is in use]";

            MiscDataUpdateCallback?.Invoke();
            return;
        }

        // Update IP
        var uri = await UpdateTrueIp();
        MiscDataUpdateCallback?.Invoke();

        if (!LazySanabiConfig.PingServers)
        {
            _baseDisplayedPing = "N/A [pinging disabled]";

            MiscDataUpdateCallback?.Invoke();
            return;
        }

        // Update ping
        if (uri != null)
        {
            var lastStatus = IPStatus.Unknown;

            var successfulAttempts = 0;
            var totalSuccessfulMilliseconds = 0;

            _baseDisplayedPing = "Fetching ping…";
            MiscDataUpdateCallback?.Invoke();

            for (var failedAttempts = 0; failedAttempts < SanabiGlobal.MaximumPingQueryAttempts; failedAttempts++)
            {
                if (LazySanabiConfig.RandomiseServerPingQueryDelay)
                {
                    var next = SharedRandom.NextDouble();
                    var minSeconds = SanabiGlobal.MinPingQueryInterval.TotalSeconds;
                    var maxSeconds = SanabiGlobal.MaxPingQueryInterval.TotalSeconds;

                    await Task.Delay(TimeSpan.FromSeconds(minSeconds + (maxSeconds - minSeconds) * next));
                }
                else
                    await Task.Delay(SanabiGlobal.MinPingQueryInterval);

                // This could definitely use averaging out the ping over multiple attempts
                var (newStatus, timeSpent) = await TryPing(uri);
                lastStatus = newStatus;

                if (lastStatus == IPStatus.Success)
                {
                    successfulAttempts++;
                    totalSuccessfulMilliseconds += timeSpent;

                    continue;
                }
            }

            if (successfulAttempts < SanabiGlobal.MinimumSuccessfulPingQueryAttempts)
                _baseDisplayedPing = $"ERR [IPStatus: {lastStatus}] [{successfulAttempts} pings succeeded, but at least {SanabiGlobal.MinimumSuccessfulPingQueryAttempts} successful pings were required]";
            else
                _baseDisplayedPing = $"avg. {totalSuccessfulMilliseconds / successfulAttempts}ms [{successfulAttempts}/{SanabiGlobal.MaximumPingQueryAttempts} successful pings]";
        }
        else
            _baseDisplayedPing = "ERR [bad URI]";

        MiscDataUpdateCallback?.Invoke();
    }

    private async Task<Uri?> UpdateTrueIp()
    {
        // not dns
        if (!Uri.TryCreate(Address, UriKind.Absolute, out var uri))
        {
            TrueAddress = "ERR [bad URI]";
            return null;
        }

        if (await NetHelpers.TryParseHostToIp(uri.Host, forgiving: true) is not { } ipAddress)
        {
            TrueAddress = "ERR [couldn't get host IP]";
            return uri;
        }

        if (uri.Port == -1)
            TrueAddress = $"{ipAddress}:{Global.DefaultServerPort} [unspecified port; defaulting to {Global.DefaultServerPort}]";
        else
            TrueAddress = $"{ipAddress}:{uri.Port}";

        TrueAddressResolved = true;
        return uri;
    }

    private async Task<(IPStatus, int)> TryPing(Uri uri)
    {
        var (icmpPingSuccess, icmpRoundTripTime, status) = await NetHelpers.TryPingIcmpAsync(uri);

        if (!icmpPingSuccess)
            return (status, 0);

        return (status, icmpRoundTripTime!.Value.Milliseconds);
    }

    public Action? MiscDataUpdateCallback = null;
    public bool TrueAddressResolved = false;
    public string TrueAddress = "Fetching ping…"; // Actual IP, where Address can just point to a site, this points to the host. Resolved when fetching server status

    private string _baseDisplayedPing = "Fetching ping…";
    public string DisplayedPing => _baseDisplayedPing +
        (LazySanabiConfig.PingServers && _status != ServerStatusCode.Online && Status != ServerStatusCode.FetchingStatus ?
            " [server is reachable by IP, but is unjoinable]" :
            "");

    public string Address { get; }
    public string? HubAddress { get; }

    public string? Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string? Description
    {
        get => _desc;
        set => SetProperty(ref _desc, value);
    }

    private string _mapName = "Undetermined (check status on serverlist)";
    public string MapName
    {
        get => _mapName;
        set => SetProperty(ref _mapName, value);
    }

    private string _preset = "Undetermined (check status on serverlist)";
    public string Preset
    {
        get => _preset;
        set => SetProperty(ref _preset, value);
    }

    /// <summary>
    ///     Round-trip-time between the client and server.
    /// </summary>
    public TimeSpan? Ping
    {
        get => _ping;
        set => SetProperty(ref _ping, value);
    }

    public ServerStatusCode Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public ServerStatusInfoCode StatusInfo
    {
        get => _statusInfo;
        set => SetProperty(ref _statusInfo, value);
    }

    public int PlayerCount
    {
        get => _playerCount;
        set => SetProperty(ref _playerCount, value);
    }

    /// <summary>
    /// 0 means there's no maximum.
    /// </summary>
    public int SoftMaxPlayerCount
    {
        get => _softMaxPlayerCount;
        set => SetProperty(ref _softMaxPlayerCount, value);
    }

    public DateTime? RoundStartTime
    {
        get => _roundStartTime;
        set => SetProperty(ref _roundStartTime, value);
    }

    public GameRoundStatus RoundStatus
    {
        get => _roundStatus;
        set => SetProperty(ref _roundStatus, value);
    }

    public ServerInfoLink[]? Links
    {
        get => _links;
        set => SetProperty(ref _links, value);
    }

    public string[] Tags
    {
        get => _tags;
        set => SetProperty(ref _tags, value);
    }

    public CancellationTokenSource? InfoCancel;
}
