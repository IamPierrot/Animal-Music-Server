namespace AnimalSync.Models;

public interface IPlayerList
{
    string VoiceChannelId { get; set; }
    string GuildId { get; set; }
    IEnumerable<string> MusicList { get; set; }
}

public class PlayerList : IPlayerList
{
    public required string VoiceChannelId { get; set; }
    public required string GuildId { get; set; }
    public required IEnumerable<string> MusicList { get; set; }
}

public class PlayerState
{
    public long Position { get; set; }
    public long Timestamp { get; set; }
    public Dictionary<string, object> Stats { get; private set; } = new();

    public void UpdateStats(Dictionary<string, object> stats)
    {
        Stats = stats;
    }
}

public class PlayerSyncData
{
    public string eventExtend { get; set; } = string.Empty;
    public string GuildId { get; set; } = string.Empty;
    public string VoiceChannelId { get; set; } = string.Empty;
    public IEnumerable<string> MusicList { get; set; } = [];
    public Dictionary<string, object> Stats { get; set; } = [];
    public PlayerEvent? Event { get; set; }
    public PlayerUpdateState? State { get; set; }
}

public class PlayerEvent
{
    public string Type { get; set; } = string.Empty;
    public string GuildId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
}

public class PlayerUpdateState
{
    public bool Connected { get; set; }
    public long Position { get; set; }
    public long Time { get; set; }
}

// Event arguments
public class PlayerDisconnectedEventArgs
{
    public string ClientId { get; set; } = string.Empty;
    public string GuildId { get; set; } = string.Empty;
}

public class PlayerStateUpdateEventArgs
{
    public string ClientId { get; set; } = string.Empty;
    public PlayerState State { get; set; } = new();
}

public class VoiceChannelEventArgs
{
    public string ClientId { get; set; } = string.Empty;
    public string GuildId { get; set; } = string.Empty;
    public string? ChannelId { get; set; }
}