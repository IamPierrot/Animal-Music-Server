using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using AnimalSync.Models;
using System.Threading.Channels;

namespace AnimalSync.Core;

public class AnimalSyncHub(IHubContext<AnimalSyncHub> hubContext) : Hub
{
    private const string SECRET_TOKEN = "123";
    private static readonly ILogger<AnimalSyncHub> logger = LoggerFactory.Create(configure => configure.AddConsole()).CreateLogger<AnimalSyncHub>();
    private static readonly ConcurrentDictionary<string, string> ClientList = new();
    private static readonly List<string> ClientQueue = []; // Initialize with a dynamic size
    private static readonly ConcurrentDictionary<string, HashSet<string>> GuildList = new();
    private static readonly ConcurrentDictionary<string, IPlayerList> PlayerList = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> ClientsPlayingList = new();
    private static readonly ConcurrentDictionary<string, PlayerState> PlayerStates = new();
    private static readonly ConcurrentDictionary<string, bool> ProcessedMessages = new();

    private readonly IHubContext<AnimalSyncHub> _hubContext = hubContext;

    private static readonly Task _lmao = Task.Run(CleanupProcessedMessages);

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
            ClientList.TryAdd(clientId, Context.ConnectionId);

            if (!ClientsPlayingList.ContainsKey(clientId))
            {
                ClientsPlayingList.TryAdd(clientId, new ConcurrentDictionary<string, string>());
                if (int.TryParse(clientId, out int clientIdInt))
                {
                    if (clientIdInt - 1 >= 0 && clientIdInt - 1 < ClientQueue.Count)
                    {
                        ClientQueue.Insert(clientIdInt - 1, clientId);
                    }
                    else
                    {
                        ClientQueue.Add(clientId);
                    }
                }
                else throw new Exception("Invalid ClientId! ClientId should be a number");
            }

            await Clients.Caller.SendAsync("connection", Context.ConnectionId);
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
            RemoveClientResources(clientId);

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

        GuildList.AddOrUpdate(ClientId, _ => guildIds.ToHashSet(),
            (_, existing) => existing.Union(guildIds).ToHashSet());

        logger.LogInformation("{ClientId} has synchronized {GuildCount} guilds to server!", ClientId, guildIds.Count());
    }

    private record ClientEligibility(bool IsEligible, bool IsPlaying);

    [HubMethodName("sync_play")]
    public async Task HandleMsg(string messageId, string voiceChannelId, string guildId, string textChannelId, IEnumerable<string> args)
    {
        await EnqueueMessage("play", messageId, guildId, textChannelId, voiceChannelId);
    }

    [HubMethodName("command_sync")]
    public async Task CommandSync(string messageId, string guildId, string textChannelId, string? voiceChannelId)
    {
        await EnqueueMessage("command", messageId, guildId, textChannelId, voiceChannelId);
    }

    private async Task ProcessMessageInternal(string action, string messageId, string guildId, string textChannelId, string? voiceChannelId)
    {
        try
        {
            if (action == "command" && string.IsNullOrEmpty(voiceChannelId))
            {
                await AssignMessageToRandomClient(action, messageId, guildId, textChannelId);
                return;
            }

            var eligibleClients = GetEligibleClients(guildId, voiceChannelId);

            foreach (var (connectionId, eligibility) in eligibleClients)
            {
                if (eligibility.IsEligible)
                {
                    await _hubContext.Clients.Client(connectionId).SendAsync(action, new
                    {
                        messageId,
                        guildId,
                        textChannelId,
                        connectionId
                    });

                    logger.LogInformation("Message ID: {MessageId} sent to connection ID: {ConnectionId}", messageId, connectionId);
                    return;
                }
            }

            if (action == "play")
            {
                logger.LogInformation("No eligible client for message ID: {MessageId} at server ID: {GuildId}.", messageId, guildId);
                await AssignMessageToRandomClient("no_client", messageId, guildId, textChannelId);
            }
            else
            {
                await AssignMessageToRandomClient("command", messageId, guildId, textChannelId);
            }

        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing message ID: {MessageId} at server ID: {GuildId}.", messageId, guildId);
            await AssignMessageToRandomClient("error", messageId, guildId, textChannelId);
        }

    }

    private readonly Channel<(string action, string messageId, string guildId, string textChannelId, string? voiceChannelId)> _messageQueue = Channel.CreateUnbounded<(string, string, string, string, string?)>();

    public async Task ProcessMessageQueue()
    {
        await Task.Run(ProcessQueueAsync);
    }

    public async Task EnqueueMessage(string action, string messageId, string guildId, string textChannelId, string? voiceChannelId)
    {
        try
        {
            if (!ProcessedMessages.TryAdd(messageId, false))
            {
                logger.LogInformation("Message ID: {MessageId} is already in the queue or being processed.", messageId);
                return;
            }

            await _messageQueue.Writer.WriteAsync((action, messageId, guildId, textChannelId, voiceChannelId));
            logger.LogInformation("Message ID: {MessageId} enqueued for processing.", messageId);
        }
        finally
        {
            _ = Task.Run(ProcessMessageQueue);
        }
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var (action, messageId, guildId, textChannelId, voiceChannelId) in _messageQueue.Reader.ReadAllAsync())
        {
            await ProcessMessageInternal(action, messageId, guildId, textChannelId, voiceChannelId);
        }
    }

    private static IEnumerable<KeyValuePair<string, ClientEligibility>> GetEligibleClients(string guildId, string? voiceChannelId)
    {
        var eligibleClients = new Dictionary<string, ClientEligibility>();
        foreach (var clientId in ClientQueue)
        {
            ClientList.TryGetValue(clientId, out var connectionId);
            if (connectionId == null) continue;
            eligibleClients[connectionId] = CheckClientEligibility(clientId, guildId, voiceChannelId);
        }

        return eligibleClients
            .OrderByDescending(client => client.Value.IsPlaying)
            .ThenByDescending(client => client.Value.IsEligible);
    }

    private static ClientEligibility CheckClientEligibility(string clientId, string guildId, string? voiceChannelId)
    {
        if (!GuildList.TryGetValue(clientId, out var clientGuildList) || !clientGuildList.Contains(guildId))
            return new ClientEligibility(false, false);

        if (ClientsPlayingList.TryGetValue(clientId, out var clientPlayingList) &&
            clientPlayingList.TryGetValue(guildId, out var playingVoiceChannelId))
        {
            if (playingVoiceChannelId == voiceChannelId)
                return new ClientEligibility(true, true);
            return new ClientEligibility(false, true);
        }

        return new ClientEligibility(true, false);
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
        }
    }

    private static void HandlePlayerEvent(string clientId, PlayerEvent? evt)
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

    private static void HandlePlayerUpdate(string clientId, string guildId, PlayerUpdateState? state)
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

    private async Task AssignMessageToRandomClient(string method, string messageId, string guildId, string textChannelId)
    {
        Random random = new();
        var eligibleClients = ClientQueue.Select(clientId => (ClientList[clientId], clientId))
            .Where(client => CheckClientEligibility(client.clientId, guildId, null).IsEligible)
            .ToList();
        
        if (eligibleClients.Count == 0)
        {
            logger.LogWarning("No eligible clients found for guild ID: {GuildId}.", guildId);
            return;
        }

        var (connectionIdRandom, clientIdRandom) = eligibleClients[random.Next(0, eligibleClients.Count)];

        logger.LogInformation("Assigned message ID: {MessageId} at server ID: {GuildId} to client: {ClientId}.", messageId, guildId, clientIdRandom);

        await _hubContext.Clients.Client(connectionIdRandom).SendAsync(method, new { messageId, guildId, textChannelId, connectionId = connectionIdRandom });
    }

    private static void RemoveClientResources(string clientId)
    {
        ClientsPlayingList.TryRemove(clientId, out _);
        ClientList.TryRemove(clientId, out _);
        GuildList.TryRemove(clientId, out _);
        PlayerList.TryRemove(clientId, out _);
        PlayerStates.TryRemove(clientId, out _);
        ClientQueue.Remove(clientId);

        logger.LogInformation("Client {ClientId} resources removed.", clientId);
    }

    private static async Task CleanupProcessedMessages()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(5));
            var keysToRemove = ProcessedMessages.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();
            int cnt = 0;
            foreach (var key in keysToRemove)
            {
                ProcessedMessages.TryRemove(key, out _);
                cnt++;
            }

            logger.LogInformation($"Cleaned up processed message ({cnt}) IDs.");
        }
    }
}