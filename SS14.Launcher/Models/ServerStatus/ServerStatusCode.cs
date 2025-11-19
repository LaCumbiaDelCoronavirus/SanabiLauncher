namespace SS14.Launcher.Models.ServerStatus;

public enum ServerStatusCode
{
    Offline, // everything that isnt HostErr
    HostErr, // we don't recognise this host; i.e., we can't get this server's address
    FetchingStatus,
    Online
}

public enum ServerStatusInfoCode
{
    NotFetched,
    Fetching,
    Error,
    Fetched
}

public enum GameRoundStatus
{
    Unknown,
    InLobby,
    InRound,
}
