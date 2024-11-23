using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using AnimalSync.Models;

namespace AnimalSync.Core;

public class AnimalSyncHub : Hub
{
    private const string SECRET_TOKEN = "123";
    private readonly ILogger<AnimalSyncHub> logger;
    private static readonly ConcurrentDictionary<string, IHubCallerClients> ClientList = new();
    private static readonly ConcurrentQueue<ConcurrentDictionary<string, string>> ClientQueue = new();
    private static readonly ConcurrentDictionary<string, HashSet<string>> GuildList = new();
    private static readonly ConcurrentDictionary<string, IPlayerList> PlayerList = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> ClientsPlayingList = new();
    private static readonly ConcurrentDictionary<string, bool> MessageHandled = new();
    private static readonly ConcurrentDictionary<string, PlayerState> PlayerStates = new();

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
            await Clients.Caller.SendAsync("error", "Deny permission by invalid credentials!");
            logger.LogWarning("{ConnectionId} provides Invalid secret token!", Context.ConnectionId);
            return;
        }

        try
        {
            ClientList.TryAdd(clientId, Clients);

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
        catch (Exception error)
        {
            logger.LogError(error, "{ConnectionId} has error to connect to server with ID: {ClientId}", Context.ConnectionId, clientId);
            await Clients.Caller.SendAsync("error", $"Error when connect to server: {error.Message}");
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var clientId = Context.GetHttpContext()?.Request.Query["ClientId"].ToString();

        if (string.IsNullOrEmpty(clientId))
        {
            await Clients.Caller.SendAsync("error", "Invalid or missing client ID.");
            return;
        }

        try
        {
            ClientsPlayingList.TryRemove(clientId, out _);
            ClientList.TryRemove(clientId, out _);
            GuildList.TryRemove(clientId, out _);
            PlayerList.TryRemove(clientId, out _);
            PlayerStates.TryRemove(clientId, out _);

            var remainingClients = new ConcurrentQueue<ConcurrentDictionary<string, string>>();
            while (ClientQueue.TryDequeue(out var queueItem))
            {
                var newDict = new ConcurrentDictionary<string, string>();
                foreach (var kvp in queueItem.Where(kvp => kvp.Value != clientId))
                {
                    newDict.TryAdd(kvp.Key, kvp.Value);
                }
                if (!newDict.IsEmpty)
                {
                    remainingClients.Enqueue(newDict);
                }
            }
            while (remainingClients.TryDequeue(out var item))
            {
                ClientQueue.Enqueue(item);
            }

            await Clients.Caller.SendAsync("disconnect", "Disconnect from AnimalSync Hub!");
            logger.LogInformation("Client {ClientId} disconnected and all resources cleared!", clientId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during cleanup for client {ClientId}", clientId);
        }

        if (exception is not null)
            logger.LogError(exception, "Disconnection error for {ClientId}", clientId);
    }

    [HubMethodName("guild_sync")]
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
    [HubMethodName("sync_play")]
    public async Task HandleMsg(string messageId, string voiceChannelId, string guildId, string textChannelId, IEnumerable<string> args)
    {
        if (!MessageHandled.TryAdd(messageId, true))
            return;

        logger.LogInformation("Handling message ID: {MessageId} at server ID: {GuildId}.", messageId, guildId);

        foreach (var clientQueue in ClientQueue)
        {
            foreach (var (connectionId, clientId) in clientQueue)
            {
                if (!GuildList.TryGetValue(clientId, out var clientGuildList) ||
                    !clientGuildList.Contains(guildId))
                    continue;

                if (ClientsPlayingList.TryGetValue(clientId, out var clientPlayingList))
                {
                    if (clientPlayingList.TryGetValue(guildId, out var playingVoiceChannelId) &&
                        playingVoiceChannelId == voiceChannelId)
                    {
                        logger.LogInformation("Handled message ID: {MessageId} at server ID: {GuildId}: Already playing at that channel.", messageId, guildId);
                        await Clients.Client(connectionId).SendAsync("play", new { messageId, guildId, textChannelId, args });
                        return;
                    }

                    if (!clientPlayingList.ContainsKey(guildId))
                    {
                        logger.LogInformation("Assigned message ID: {MessageId} at server ID: {GuildId} to client: {ClientId}.", messageId, guildId, clientId);
                        await Clients.Client(connectionId).SendAsync("play", new { messageId, guildId, textChannelId, args });
                        return;
                    }
                }
                else
                {
                    logger.LogInformation("Assigned message ID: {MessageId} at server ID: {GuildId} to client: {ClientId}.", messageId, guildId, clientId);
                    await Clients.Client(connectionId).SendAsync("play", new { messageId, guildId, textChannelId, args });
                    return;
                }
            }
        }

        logger.LogInformation("No eligible client for message ID: {MessageId} at server ID: {GuildId}.", messageId, guildId);
        await Clients.Caller.SendAsync("no_client", new { messageId, guildId, voiceChannelId });
    }
    [HubMethodName("command_sync")]
    public async Task CommandSync(string messageId, string guildId, string textChannelId, string? voiceChannelId)
    {
        if (!MessageHandled.TryAdd(messageId, true))
            return;


        foreach (var clientQueue in ClientQueue)
        {
            foreach (var (connectionId, clientId) in clientQueue)
            {
                if (!GuildList.TryGetValue(clientId, out var clientGuildList) ||
                    !clientGuildList.Contains(guildId))
                    continue;

                if (ClientsPlayingList.TryGetValue(clientId, out var clientPlayingList))
                {
                    if (clientPlayingList.TryGetValue(guildId, out var playingVoiceChannelId) &&
                        playingVoiceChannelId == voiceChannelId)
                    {
                        logger.LogInformation("Handled message ID: {MessageId} at server ID: {GuildId}: Already playing at that channel.", messageId, guildId);
                        await Clients.Client(connectionId).SendAsync("command", new { messageId, guildId, textChannelId });
                        return;
                    }

                    if (!clientPlayingList.ContainsKey(guildId))
                    {
                        logger.LogInformation("Assigned message ID: {MessageId} at server ID: {GuildId} to client: {ClientId}.", messageId, guildId, clientId);
                        await Clients.Client(connectionId).SendAsync("command", new { messageId, guildId, textChannelId });
                        return;
                    }
                }
                else
                {
                    logger.LogInformation("Assigned message ID: {MessageId} at server ID: {GuildId} to client: {ClientId}.", messageId, guildId, clientId);
                    await Clients.Client(connectionId).SendAsync("command", new { messageId, guildId, textChannelId });
                    return;
                }
            }
        }

        Random random = new();
        var list = new List<ConcurrentDictionary<string, string>>(ClientQueue);
        var randomDict = list[random.Next(0, list.Count)];

        var (connectionIdRandom, clientIdRandom) = randomDict.ElementAtOrDefault(random.Next(0, randomDict.Count));

        logger.LogInformation("Assigned message ID: {MessageId} at server ID: {GuildId} to client: {ClientId}.", messageId, guildId, clientIdRandom);

        await Clients.Client(connectionIdRandom).SendAsync("command", new { messageId, guildId, textChannelId });
    }

    [HubMethodName("player_sync")]
    public async Task PlayerSync(string ClientId, PlayerSyncData data)
    {
        try
        {
            if (!ClientList.ContainsKey(ClientId))
            {
                await Clients.Caller.SendAsync("error", $"{ClientId} is not connected to Animal Hub!");
                return;
            }

            var playerState = PlayerStates.GetOrAdd(ClientId, _ => new PlayerState());

            switch (data.eventExtend)
            {
                case "stats":
                    playerState.UpdateStats(data.Stats);
                    break;

                case "event":
                    HandlePlayerEvent(ClientId, data.Event);
                    break;

                case "playerUpdate":
                    HandlePlayerUpdate(ClientId, data.GuildId, data.State);
                    break;
            }

            PlayerList.AddOrUpdate(ClientId,
                new PlayerList
                {
                    GuildId = data.GuildId,
                    VoiceChannelId = data.VoiceChannelId,
                    MusicList = data.MusicList
                },
                (_, existing) =>
                {
                    existing.GuildId = data.GuildId;
                    existing.VoiceChannelId = data.VoiceChannelId;
                    existing.MusicList = data.MusicList;
                    return existing;
                });

            logger.LogInformation("Updated player state for client {ClientId} in guild {GuildId}",
                ClientId, data.GuildId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing player sync for client {ClientId}", ClientId);
            await Clients.Caller.SendAsync("error", "Error processing player sync");
        }
    }

    private void HandlePlayerEvent(string clientId, PlayerEvent? evt)
    {
        if (evt == null) return;

        switch (evt.Type)
        {
            case "join":
                if (ClientsPlayingList.TryGetValue(clientId, out var playingList))
                {
                    playingList.TryAdd(evt.GuildId, evt.ChannelId);
                    logger.LogInformation("Client {ClientId} joined voice in guild {GuildId}, channel {ChannelId}",
                        clientId, evt.GuildId, evt.ChannelId);
                }
                break;

            case "left":
                if (ClientsPlayingList.TryGetValue(clientId, out var list))
                {
                    list.TryRemove(evt.GuildId, out _);
                    logger.LogInformation("Client {ClientId} left voice in guild {GuildId}",
                        clientId, evt.GuildId);
                }
                break;
        }
    }

    private void HandlePlayerUpdate(string clientId, string guildId, PlayerUpdateState? state)
    {
        if (state == null) return;

        if (!state.Connected)
        {
            if (ClientsPlayingList.TryGetValue(clientId, out var playingList))
            {
                playingList.TryRemove(guildId, out _);
                logger.LogInformation("Client {ClientId} disconnected from guild {GuildId}",
                    clientId, guildId);
            }
        }

        PlayerStates.AddOrUpdate(clientId,
            _ => new PlayerState { Position = state.Position, Timestamp = state.Time },
            (_, existing) =>
            {
                existing.Position = state.Position;
                existing.Timestamp = state.Time;
                return existing;
            });
    }

    private void StartCleanupTask()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(40));
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