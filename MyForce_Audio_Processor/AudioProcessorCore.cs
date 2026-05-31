using System.Buffers;
using System.Collections.ObjectModel;
using System.Text.Json;
using MQTTnet;

internal sealed class AudioProcessorCoordinator : IAsyncDisposable
{
    private readonly AudioProcessorRegistry _registry;
    private readonly AudioProcessorRoutingState _routingState;
    private readonly MqttServiceRuntime _mqttRuntime;
    private readonly AudioProcessorTopicFactory _topics;
    private readonly TxController _txController;

    public AudioProcessorCoordinator(MqttServiceRuntime mqttRuntime, AudioProcessorTopicFactory topics)
    {
        ArgumentNullException.ThrowIfNull(mqttRuntime);
        ArgumentNullException.ThrowIfNull(topics);

        _mqttRuntime = mqttRuntime;
        _topics = topics;
        _registry = AudioProcessorRegistry.CreateDefault();
        _routingState = AudioProcessorRoutingState.CreateDefault(_registry.RadioIds);
        _txController = new TxController(_registry.RadioIds);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _mqttRuntime.SubscribeAsync(_topics.AllCommandsTopicFilter, cancellationToken).ConfigureAwait(false);
        await PublishBirthSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var topic = args.ApplicationMessage.Topic;
        if (string.IsNullOrWhiteSpace(topic))
        {
            return;
        }

        if (string.Equals(topic, _topics.ManualPttRequestTopic, StringComparison.OrdinalIgnoreCase))
        {
            var request = AudioProcessorJson.Deserialize<ManualPttRequest>(args.ApplicationMessage.Payload);
            if (request is null)
            {
                return;
            }

            ApplyManualPtt(request);
            await PublishStatusAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private void ApplyManualPtt(ManualPttRequest request)
    {
        if (request.IsPressed)
        {
            _txController.BeginManualTransmit(request.RadioId);
            _routingState.SetOperatorMicTarget(request.RadioId);
            return;
        }

        _txController.EndManualTransmit(request.RadioId);
        _routingState.ClearOperatorMicTarget(request.RadioId);
    }

    private async Task PublishBirthSnapshotAsync(CancellationToken cancellationToken)
    {
        await _mqttRuntime.PublishAsync(
            _topics.ServiceRegistryTopic,
            AudioProcessorJson.Serialize(ServiceRegistryPayload.Create(_registry)),
            retain: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await _mqttRuntime.PublishAsync(
            _topics.RoutingStateTopic,
            AudioProcessorJson.Serialize(RoutingStatePayload.Create(_routingState.CurrentSnapshot)),
            retain: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await PublishStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishStatusAsync(CancellationToken cancellationToken)
    {
        await _mqttRuntime.PublishAsync(
            _topics.ServiceStatusTopic,
            AudioProcessorJson.Serialize(
                ServiceStatusPayload.CreateRunning(
                    serviceId: "audio-processor",
                    radioCount: _registry.RadioIds.Count,
                    bridgeCount: _registry.Bridges.Count,
                    activeManualTransmitRadioId: _txController.ActiveManualTransmitRadioId?.Value)),
            retain: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class AudioProcessorRegistry
{
    public AudioProcessorRegistry(IReadOnlyList<RadioId> radioIds, IReadOnlyList<BridgeDefinition> bridges)
    {
        ArgumentNullException.ThrowIfNull(radioIds);
        ArgumentNullException.ThrowIfNull(bridges);

        RadioIds = radioIds;
        Bridges = bridges;
    }

    public IReadOnlyList<RadioId> RadioIds { get; }

    public IReadOnlyList<BridgeDefinition> Bridges { get; }

    public static AudioProcessorRegistry CreateDefault()
    {
        var radios = new List<RadioId>
        {
            new("barrett"),
            new("xpr"),
            new("mtm5400"),
            new("apx-xtl"),
            new("harris"),
            new("4w")
        };

        return new AudioProcessorRegistry(radios.AsReadOnly(), Array.Empty<BridgeDefinition>());
    }
}

internal sealed class AudioProcessorRoutingState
{
    private RoutingSnapshot _currentSnapshot;

    private AudioProcessorRoutingState(RoutingSnapshot currentSnapshot)
    {
        _currentSnapshot = currentSnapshot;
    }

    public RoutingSnapshot CurrentSnapshot => _currentSnapshot;

    public static AudioProcessorRoutingState CreateDefault(IEnumerable<RadioId> radioIds)
    {
        ArgumentNullException.ThrowIfNull(radioIds);

        var crosspoints = radioIds
            .Select(static radioId => new RoutingCrosspoint(SourceEndpoint.OperatorMic, SinkEndpoint.ForRadioTx(radioId), 1.0m, false))
            .ToArray();

        return new AudioProcessorRoutingState(new RoutingSnapshot(new ReadOnlyCollection<RoutingCrosspoint>(crosspoints), SinkEndpoint.Speaker, null));
    }

    public void SetOperatorMicTarget(RadioId radioId)
    {
        ArgumentNullException.ThrowIfNull(radioId);

        _currentSnapshot = _currentSnapshot with { ActiveOperatorTarget = radioId };
    }

    public void ClearOperatorMicTarget(RadioId radioId)
    {
        ArgumentNullException.ThrowIfNull(radioId);

        if (_currentSnapshot.ActiveOperatorTarget == radioId)
        {
            _currentSnapshot = _currentSnapshot with { ActiveOperatorTarget = null };
        }
    }
}

internal sealed class TxController
{
    private readonly HashSet<RadioId> _knownRadioIds;

    public TxController(IEnumerable<RadioId> radioIds)
    {
        ArgumentNullException.ThrowIfNull(radioIds);
        _knownRadioIds = new HashSet<RadioId>(radioIds);
    }

    public RadioId? ActiveManualTransmitRadioId { get; private set; }

    public void BeginManualTransmit(RadioId radioId)
    {
        ArgumentNullException.ThrowIfNull(radioId);

        if (!_knownRadioIds.Contains(radioId))
        {
            throw new InvalidOperationException($"Unknown radio id '{radioId.Value}'.");
        }

        ActiveManualTransmitRadioId = radioId;
    }

    public void EndManualTransmit(RadioId radioId)
    {
        ArgumentNullException.ThrowIfNull(radioId);

        if (ActiveManualTransmitRadioId == radioId)
        {
            ActiveManualTransmitRadioId = null;
        }
    }
}

internal sealed class AudioProcessorTopicFactory
{
    private const string RootTopic = "myforce/ap";

    public string AllCommandsTopicFilter => $"{RootTopic}/cmd/#";

    public string ManualPttRequestTopic => $"{RootTopic}/cmd/manual-ptt";

    public string RoutingStateTopic => $"{RootTopic}/state/routing";

    public string ServiceRegistryTopic => $"{RootTopic}/registry/service";

    public string ServiceStatusTopic => $"{RootTopic}/status/service";
}

internal sealed record RadioId(string Value)
{
    public override string ToString() => Value;
}

internal sealed record BridgeId(string Value)
{
    public override string ToString() => Value;
}

internal sealed record BridgeDefinition(BridgeId Id, ReadOnlyCollection<RadioId> Members);

internal sealed record RoutingSnapshot(
    ReadOnlyCollection<RoutingCrosspoint> Crosspoints,
    SinkEndpoint SpeakerSink,
    RadioId? ActiveOperatorTarget);

internal sealed record RoutingCrosspoint(
    SourceEndpoint Source,
    SinkEndpoint Sink,
    decimal Gain,
    bool Enabled);

internal sealed record SourceEndpoint(string Kind, string? RadioId = null)
{
    public static SourceEndpoint OperatorMic { get; } = new("operator-mic");

    public static SourceEndpoint ForRadioRx(RadioId radioId)
    {
        ArgumentNullException.ThrowIfNull(radioId);
        return new SourceEndpoint("radio-rx", radioId.Value);
    }
}

internal sealed record SinkEndpoint(string Kind, string? RadioId = null)
{
    public static SinkEndpoint Speaker { get; } = new("speaker");

    public static SinkEndpoint ForRadioTx(RadioId radioId)
    {
        ArgumentNullException.ThrowIfNull(radioId);
        return new SinkEndpoint("radio-tx", radioId.Value);
    }
}

internal sealed record ManualPttRequest(RadioId RadioId, bool IsPressed);

internal sealed record ServiceRegistryPayload(
    string ServiceId,
    string DisplayName,
    IReadOnlyList<string> RadioIds,
    IReadOnlyList<string> BridgeIds)
{
    public static ServiceRegistryPayload Create(AudioProcessorRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return new ServiceRegistryPayload(
            ServiceId: "audio-processor",
            DisplayName: "Audio Processor",
            RadioIds: registry.RadioIds.Select(static radioId => radioId.Value).ToArray(),
            BridgeIds: registry.Bridges.Select(static bridge => bridge.Id.Value).ToArray());
    }
}

internal sealed record ServiceStatusPayload(
    string ServiceId,
    AudioProcessorServiceState State,
    int RadioCount,
    int BridgeCount,
    string? ActiveManualTransmitRadioId)
{
    public static ServiceStatusPayload CreateRunning(string serviceId, int radioCount, int bridgeCount, string? activeManualTransmitRadioId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        return new ServiceStatusPayload(serviceId, AudioProcessorServiceState.Running, radioCount, bridgeCount, activeManualTransmitRadioId);
    }

    public static ServiceStatusPayload CreateStopped(string serviceId, int radioCount, int bridgeCount, string? activeManualTransmitRadioId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);

        return new ServiceStatusPayload(serviceId, AudioProcessorServiceState.Stopped, radioCount, bridgeCount, activeManualTransmitRadioId);
    }
}

internal sealed record RoutingStatePayload(
    string? ActiveOperatorTarget,
    IReadOnlyList<RoutingCrosspointPayload> Crosspoints)
{
    public static RoutingStatePayload Create(RoutingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new RoutingStatePayload(
            ActiveOperatorTarget: snapshot.ActiveOperatorTarget?.Value,
            Crosspoints: snapshot.Crosspoints
                .Select(static crosspoint => new RoutingCrosspointPayload(
                    crosspoint.Source.Kind,
                    crosspoint.Source.RadioId,
                    crosspoint.Sink.Kind,
                    crosspoint.Sink.RadioId,
                    crosspoint.Gain,
                    crosspoint.Enabled))
                .ToArray());
    }
}

internal sealed record RoutingCrosspointPayload(
    string SourceKind,
    string? SourceRadioId,
    string SinkKind,
    string? SinkRadioId,
    decimal Gain,
    bool Enabled);

internal enum AudioProcessorServiceState
{
    Stopped = 0,
    Running = 1
}

internal static class AudioProcessorJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, SerializerOptions);
    }

    public static T? Deserialize<T>(ReadOnlySequence<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(payload.ToArray(), SerializerOptions);
    }
}
