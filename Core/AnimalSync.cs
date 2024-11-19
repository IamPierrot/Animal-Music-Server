using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace AnimalSync.Core;

interface IPlayerList
{
    string VoiceChannelId { get; set; }
    string GuildId { get; set; }
    IEnumerable<string> MusicList { get; set; }
}

public class AnimalSyncHub : Hub
{
    private readonly ILogger<AnimalSyncHub> logger;
    private const string SECRET_TOKEN = "123";
    private static readonly ConcurrentDictionary<string, IHubCallerClients> ClientList = new();
    private static readonly ConcurrentQueue<ConcurrentDictionary<string, string>> ClientQueue = new();
    private static readonly ConcurrentDictionary<string, HashSet<string>> GuildList = new();
    private static readonly ConcurrentDictionary<string, IPlayerList> PlayerList = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> ClientsPlayingList = new();
    private static readonly ConcurrentDictionary<string, bool> MessageHandled = new();

    public AnimalSyncHub(ILogger<AnimalSyncHub> logger)
    {
        this.logger = logger;
        StartCleanupTask();
    }

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext is null) return;
        var clientId = httpContext.Request.Query["ClientId"].ToString();
        var secretToken = httpContext.Request.Headers["Secret"].ToString();

        if (string.IsNullOrEmpty(clientId))
        {
            await Clients.Caller.SendAsync("error", "Provide Invalid ClientId!");
            logger.LogWarning("{ConnectionId} provides Invalid ClientId!", Context.ConnectionId);
            return;
        }

        if (string.IsNullOrEmpty(secretToken) || secretToken != SECRET_TOKEN)
        {
            await Clients.Caller.SendAsync("error", "Deny permission by wrong secret!");
            logger.LogWarning("{ConnectionId} provides Invalid secret token!", Context.ConnectionId);
            return;
        }

        try
        {
            ClientList.TryAdd(clientId, Clients);
        }
        catch (Exception error)
        {
            logger.LogError("{ConnectionId} has error to connect to server with ID: {ClientId}", Context.ConnectionId, clientId);
            logger.LogError("{}", error.StackTrace);
            await Clients.Caller.SendAsync("error", $"Error when connect to server: {error.Message}");
            return;
        }

        if (!ClientsPlayingList.ContainsKey(clientId))
        {
            ClientsPlayingList.TryAdd(clientId, new ConcurrentDictionary<string, string>());
            var queue = new ConcurrentDictionary<string, string>();
            queue.TryAdd(Context.ConnectionId, clientId);
            ClientQueue.Enqueue(queue);
        }

        await Clients.Caller.SendAsync("connection", "Successfully connect to Animal Hub!");
        logger.LogInformation("{ConnectionId} connect to server with ID: {ClientId}", Context.ConnectionId, clientId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var clientId = Context.GetHttpContext()?.Request.Query["ClientId"].ToString();

        if (string.IsNullOrEmpty(clientId) || !ClientList.ContainsKey(clientId))
        {
            await Clients.Caller.SendAsync("error", "Invalid or missing client ID.");
            return;
        }

        ClientsPlayingList.TryRemove(clientId, out _);
        ClientList.TryRemove(clientId, out _);
        GuildList.TryRemove(clientId, out _);

        await Clients.Caller.SendAsync("disconnect", "Disconnect from AnimalSync Hub!");
        logger.LogInformation("{ClientId} disconnected from server!", clientId);

        if (exception is not null)
            logger.LogError(exception, "Disconnection error for {ClientId}", clientId);
    }

    public async Task GuildSync(string ClientId, IEnumerable<string> guildIds)
    {
        if (!ClientList.ContainsKey(ClientId))
        {
            await Clients.Caller.SendAsync("error", $"{ClientId} does not connect to Animal Hub!");
            return;
        }

        GuildList.AddOrUpdate(ClientId, _ => [.. guildIds],
            (_, existing) => [.. guildIds]);

        logger.LogInformation("{ClientId} has synchronized {GuildCount} guilds to server!", ClientId, guildIds.Count());
    }

    [HubMethodName("player_sync")]
    public void PlayerSync(string ClientId, string VoiceChannelId, string GuildId, IEnumerable<string> data)
    {
        PlayerList.AddOrUpdate(ClientId,
            new PlayerList { GuildId = GuildId, VoiceChannelId = VoiceChannelId, MusicList = data },
            (_, existing) =>
            {
                existing.GuildId = GuildId;
                existing.VoiceChannelId = VoiceChannelId;
                existing.MusicList = data;
                return existing;
            });

        if (ClientsPlayingList.TryGetValue(ClientId, out var clientPlayingList))
        {
            if (clientPlayingList.TryAdd(GuildId, VoiceChannelId))
            {
                logger.LogInformation("Client {ClientId} has started playing at {GuildId}.", ClientId, GuildId);
            }
        }
    }

    [HubMethodName("sync_msg")]
    public async Task HandleMsg(string messageId, string voiceChannelId, string guildId, string textChannelId, IEnumerable<string> args)
    {
        if (!MessageHandled.TryAdd(messageId, true)) return;

        logger.LogInformation("Handling message ID: {MessageId} at server ID: {GuildId}.", messageId, guildId);

        var sent = false;

        while (ClientQueue.TryPeek(out var clientQueue))
        {
            foreach (var (connectionId, clientId) in clientQueue)
            {
                if (GuildList.TryGetValue(clientId, out var clientGuildList) &&
                    clientGuildList != null && clientGuildList.Contains(guildId))
                {
                    if (ClientsPlayingList.TryGetValue(clientId, out var clientPlayingList))
                    {
                        if (clientPlayingList.TryGetValue(guildId, out var playingVoiceChannelId) &&
                            playingVoiceChannelId == voiceChannelId)
                        {
                            logger.LogInformation("Handled message ID: {MessageId} at server ID: {GuildId}: Already playing at that channel.", messageId, guildId);
                            await Clients.Client(connectionId).SendAsync("msg", new { messageId, guildId, textChannelId, args });
                            sent = true;
                            break;
                        }

                        if (!clientPlayingList.ContainsKey(guildId))
                        {
                            logger.LogInformation("Assigned message ID: {MessageId} at server ID: {GuildId} to client: {ClientId}.", messageId, guildId, clientId);
                            await Clients.Client(connectionId).SendAsync("msg", new { messageId, guildId, textChannelId, args });
                            sent = true;
                            break;
                        }
                    }
                }
            }

            if (sent) break;
        }

        if (!sent)
        {
            logger.LogInformation("No eligible client for message ID: {MessageId} at server ID: {GuildId}.", messageId, guildId);
            await Clients.Caller.SendAsync("handle_no_client", new { messageId, guildId, voiceChannelId });
        }
    }

    private void StartCleanupTask()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(30));
                var halfCount = MessageHandled.Count / 2;
                var cleanedUpMessages = MessageHandled.Keys.Take(halfCount).ToArray();

                foreach (var msg in cleanedUpMessages)
                {
                    MessageHandled.TryRemove(msg, out _);
                }

                logger.LogInformation("Cleaned up {CleanedCount} message ID(s)", cleanedUpMessages.Length);
            }
        });
    }
}

public class PlayerList : IPlayerList
{
    public required string VoiceChannelId { get; set; }
    public required string GuildId { get; set; }
    public required IEnumerable<string> MusicList { get; set; }
}
