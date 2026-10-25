using System.Collections.Concurrent;
using System.Text;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;

namespace ScreenTimeService;

/// <summary>
/// The latest values this PC has heard from Home Assistant and the other
/// machines. Written from MQTTnet's thread, read from the enforcement loop,
/// so everything is a ConcurrentDictionary (last write wins, as on the Mac).
/// </summary>
public sealed class SharedState
{
    public ConcurrentDictionary<string, bool> Allowed { get; } = new();
    public ConcurrentDictionary<string, double> Budget { get; } = new();
    public ConcurrentDictionary<string, double> Bonus { get; } = new();
    public ConcurrentDictionary<string, double> MaxBonus { get; } = new();

    /// <summary>Offline Mode / Offline Grace Minutes from HA, per kid, saved to disk.</summary>
    public Dictionary<string, KidSettings> Settings { get; } = new();

    /// <summary>child -> device id -> (minutes, date) for every machine, this one's own echo included.</summary>
    public ConcurrentDictionary<string, ConcurrentDictionary<string, (int Minutes, string? Date)>> SiblingMinutes { get; } = new();
}

/// <summary>
/// One MQTT connection for the whole PC (one broker user per machine, same
/// as the Mac daemon). MQTTnet's managed client reconnects by itself and
/// keeps the subscriptions; discovery and availability are re-sent on every
/// connect, as on the Mac.
/// </summary>
public sealed class MqttLink : IAsyncDisposable
{
    public const double DefaultMaxBonusMinutes = 60;

    private readonly ServiceConfig _config;
    private readonly ManagedUserRegistry _registry;
    private readonly SharedState _state;
    private readonly IManagedMqttClient _client;
    private readonly Dictionary<string, string> _allowedTopicToChild = new();
    private readonly Dictionary<string, (string Child, ConcurrentDictionary<string, double> Target, string Label)> _numericTopics = new();
    private readonly Dictionary<string, string> _prefixToChild = new();
    private readonly Dictionary<string, string> _offlineModeTopics = new();
    private readonly Dictionary<string, string> _offlineGraceTopics = new();

    public MqttLink(ServiceConfig config, ManagedUserRegistry registry, SharedState state)
    {
        _config = config;
        _registry = registry;
        _state = state;
        foreach (var child in registry.AllChildren)
        {
            var prefix = registry.PrefixFor(child);
            _allowedTopicToChild[Topics.Allowed(prefix)] = child;
            _prefixToChild[prefix] = child;
            _numericTopics[Topics.DailyBudget(child)] = (child, state.Budget, "Budget");
            _numericTopics[Topics.BonusMinutes(child)] = (child, state.Bonus, "Bonus");
            _numericTopics[Topics.MaxBonusMinutes(child)] = (child, state.MaxBonus, "Max bonus");
            _offlineModeTopics[Topics.OfflineMode(child)] = child;
            _offlineGraceTopics[Topics.OfflineGraceMinutes(child)] = child;
            state.Settings[child] = new KidSettings(Path.Combine(Paths.StateDir, $"{child}.settings.json"));
        }
        _client = new MqttFactory().CreateManagedMqttClient();
        _client.ConnectedAsync += OnConnected;
        _client.DisconnectedAsync += e =>
        {
            if (e.ClientWasConnected)
                Log.Warn($"Unexpected MQTT disconnect ({e.Reason}).");
            return Task.CompletedTask;
        };
        _client.ConnectingFailedAsync += e =>
        {
            Log.Error($"MQTT connection failed: {e.Exception?.Message}");
            return Task.CompletedTask;
        };
        _client.ApplicationMessageReceivedAsync += OnMessage;
    }

    public bool IsConnected => _client.IsConnected;

    public async Task StartAsync()
    {
        var clientOptions = new MqttClientOptionsBuilder()
            .WithClientId($"ha-win-service-{_config.DeviceId}")
            .WithTcpServer(_config.MqttHost, _config.MqttPort)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(60));
        if (_config.MqttUsername != null)
            clientOptions = clientOptions.WithCredentials(_config.MqttUsername, _config.MqttPassword);
        if (_config.MqttTls)
            clientOptions = clientOptions.WithTlsOptions(o => o.UseTls());

        var options = new ManagedMqttClientOptionsBuilder()
            .WithClientOptions(clientOptions.Build())
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
            .WithMaxPendingMessages(1000)
            .WithPendingMessagesOverflowStrategy(MQTTnet.Server.MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage)
            .Build();

        // Retained topics: subscribing delivers the current values right away.
        var subscriptions = new List<MQTTnet.Packets.MqttTopicFilter>();
        foreach (var child in _registry.AllChildren)
        {
            var prefix = _registry.PrefixFor(child);
            var topics = new List<string>
            {
                Topics.Allowed(prefix),
                Topics.DailyBudget(child),
                Topics.BonusMinutes(child),
                Topics.MaxBonusMinutes(child),
                Topics.OfflineMode(child),
                Topics.OfflineGraceMinutes(child),
            };
            topics.AddRange(Topics.MinutesWildcards(prefix));
            subscriptions.AddRange(topics.Select(t =>
                new MqttTopicFilterBuilder().WithTopic(t).WithAtLeastOnceQoS().Build()));
        }
        await _client.SubscribeAsync(subscriptions);

        Log.Info($"Connecting to MQTT {_config.MqttHost}:{_config.MqttPort} as device '{_config.DeviceId}'.");
        await _client.StartAsync(options);
    }

    /// <summary>Queues a publish; the managed client sends it once connected.</summary>
    public void Publish(string topic, string payload, bool retain, int qos = 1)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
            .WithRetainFlag(retain)
            .Build();
        _client.EnqueueAsync(message).GetAwaiter().GetResult();
    }

    private Task OnConnected(MqttClientConnectedEventArgs e)
    {
        Log.Info("Connected to MQTT broker.");
        // One service covers every managed kid, so all of them are marked
        // online, not just whoever is signed in.
        foreach (var child in _registry.AllChildren)
        {
            var prefix = _registry.PrefixFor(child);
            Publish(Topics.Availability(prefix, _config.DeviceId), "online", retain: true);
            foreach (var (topic, payload) in Discovery.Messages(_config, child, prefix))
                Publish(topic, payload, retain: true);
        }
        return Task.CompletedTask;
    }

    private Task OnMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

        if (_offlineModeTopics.TryGetValue(topic, out var modeChild))
        {
            var mode = KidSettings.ParseOfflineMode(payload);
            if (mode == null)
                Log.Warn($"Received invalid offline mode '{payload}' on {topic}");
            else if (_state.Settings[modeChild].SetFailMode(mode))
                Log.Info($"Offline mode for '{modeChild}' set to {mode} from HA.");
            return Task.CompletedTask;
        }

        if (_offlineGraceTopics.TryGetValue(topic, out var graceChild))
        {
            if (!double.TryParse(payload, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var grace))
                Log.Warn($"Received invalid offline grace minutes '{payload}' on {topic}");
            else if (_state.Settings[graceChild].SetFailGraceMinutes(Math.Max(0, grace)))
                Log.Info($"Offline grace for '{graceChild}' set to {Math.Max(0, grace)} minutes from HA.");
            return Task.CompletedTask;
        }

        if (topic.EndsWith("/minutes_today", StringComparison.Ordinal))
        {
            if (Topics.TryParseSiblingMinutesTopic(topic, out var prefix, out var device)
                && _prefixToChild.TryGetValue(prefix, out var sibChild)
                && Topics.TryParseMinutesPayload(payload, out var minutes, out var date))
            {
                _state.SiblingMinutes.GetOrAdd(sibChild, _ => new())[device] = (minutes, date);
            }
            return Task.CompletedTask;
        }

        if (_numericTopics.TryGetValue(topic, out var numeric))
        {
            if (!double.TryParse(payload, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                Log.Warn($"Received invalid {numeric.Label.ToLowerInvariant()} payload '{payload}' on {topic}");
                return Task.CompletedTask;
            }
            value = Math.Max(0, value);
            var changed = !numeric.Target.TryGetValue(numeric.Child, out var previous) || previous != value;
            numeric.Target[numeric.Child] = value;
            if (changed)
                Log.Info($"{numeric.Label} for '{numeric.Child}' updated to {value} minutes.");
            return Task.CompletedTask;
        }

        if (_allowedTopicToChild.TryGetValue(topic, out var child))
        {
            var allowed = Topics.AsBool(payload);
            if (allowed == null)
            {
                Log.Warn($"Received invalid allowed payload '{payload}' on {topic}");
                return Task.CompletedTask;
            }
            var changed = !_state.Allowed.TryGetValue(child, out var previous) || previous != allowed.Value;
            _state.Allowed[child] = allowed.Value;
            if (changed)
                Log.Info($"Allowed state for '{child}' updated to {allowed.Value}.");
        }
        return Task.CompletedTask;
    }

    /// <summary>Gives queued messages (the final "offline") a moment to go out, then disconnects.</summary>
    public async ValueTask DisposeAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (_client.PendingApplicationMessagesCount > 0 && _client.IsConnected && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        await _client.StopAsync();
        _client.Dispose();
    }
}
