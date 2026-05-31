using MQTTnet;
using MQTTnet.Formatter;

await using var processor = new AudioProcessorMqttApp();
await processor.RunAsync();

internal sealed class AudioProcessorMqttApp : IAsyncDisposable
{
    private readonly MqttServiceRuntime _mqttRuntime;
    private readonly AudioProcessorCoordinator _coordinator;

    public AudioProcessorMqttApp()
    {
        var topics = new AudioProcessorTopicFactory();
        var lastWillPayload = AudioProcessorJson.Serialize(
            ServiceStatusPayload.CreateStopped(
                serviceId: "audio-processor",
                radioCount: 0,
                bridgeCount: 0,
                activeManualTransmitRadioId: null));

        _mqttRuntime = new MqttServiceRuntime(
            serviceName: "audio-processor",
            lastWillMessage: new MqttLastWillMessage(topics.ServiceStatusTopic, lastWillPayload, true));
        _coordinator = new AudioProcessorCoordinator(_mqttRuntime, topics);
        _mqttRuntime.SetMessageHandler(_coordinator.HandleMessageAsync);
    }

    public async Task RunAsync()
    {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;

        try
        {
            await _mqttRuntime.ConnectAsync(cts.Token);
            await _coordinator.StartAsync(cts.Token);
            Console.WriteLine("Audio Processor basics ready.");
            await _mqttRuntime.RunUntilStoppedAsync(cts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _coordinator.DisposeAsync();
        await _mqttRuntime.DisposeAsync();
    }
}

internal sealed class MqttServiceRuntime : IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly MqttLastWillMessage? _lastWillMessage;
    private readonly MqttServiceOptions _options;
    private readonly string _serviceName;
    private Func<MqttApplicationMessageReceivedEventArgs, Task>? _messageHandler;

    public MqttServiceRuntime(string serviceName, MqttLastWillMessage? lastWillMessage = null)
    {
        _serviceName = serviceName;
        _lastWillMessage = lastWillMessage;
        _options = MqttServiceOptions.FromEnvironment(serviceName);
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ConnectedAsync += OnConnectedAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithClientId(_options.ClientId)
            .WithTcpServer(_options.Host, _options.Port)
            .WithCleanSession();

        if (_lastWillMessage is not null)
        {
            optionsBuilder = optionsBuilder
                .WithWillTopic(_lastWillMessage.Topic)
                .WithWillPayload(_lastWillMessage.Payload)
                .WithWillRetain(_lastWillMessage.Retain);
        }

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            optionsBuilder = optionsBuilder.WithCredentials(_options.Username, _options.Password);
        }

        if (_options.UseTls)
        {
            optionsBuilder = optionsBuilder.WithTlsOptions(static builder => builder.UseTls());
        }

        await _client.ConnectAsync(optionsBuilder.Build(), cancellationToken);
    }

    public void SetMessageHandler(Func<MqttApplicationMessageReceivedEventArgs, Task> messageHandler)
    {
        ArgumentNullException.ThrowIfNull(messageHandler);
        _messageHandler = messageHandler;
    }

    public async Task SubscribeAsync(string topicFilter, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicFilter);

        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(topicFilter)
            .Build();

        await _client.SubscribeAsync(subscribeOptions, cancellationToken);
        Console.WriteLine($"[{_serviceName}] Subscribed: {topicFilter}");
    }

    public async Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .Build();

        await _client.PublishAsync(message, cancellationToken);
        Console.WriteLine($"[{_serviceName}] Published: {topic}");
    }

    public async Task RunUntilStoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.ConnectedAsync -= OnConnectedAsync;
        _client.DisconnectedAsync -= OnDisconnectedAsync;
        _client.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;

        if (_client.IsConnected)
        {
            await _client.DisconnectAsync();
        }

        _client.Dispose();
    }

    private Task OnConnectedAsync(MqttClientConnectedEventArgs arg)
    {
        Console.WriteLine($"[{_serviceName}] Connected to MQTT broker at {_options.Host}:{_options.Port}.");
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
    {
        var detail = arg.Exception?.Message ?? arg.ReasonString ?? "Disconnected.";
        Console.WriteLine($"[{_serviceName}] MQTT disconnected: {detail}");
        return Task.CompletedTask;
    }

    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
    {
        Console.WriteLine($"[{_serviceName}] Received message on topic: {arg.ApplicationMessage.Topic}");

        return _messageHandler is null
            ? Task.CompletedTask
            : _messageHandler(arg);
    }
}

internal sealed record MqttLastWillMessage(string Topic, string Payload, bool Retain);

internal sealed record MqttServiceOptions(
    string Host,
    int Port,
    string ClientId,
    bool UseTls,
    string? Username,
    string? Password)
{
    public static MqttServiceOptions FromEnvironment(string serviceName)
    {
        var normalizedServiceName = serviceName.Replace(' ', '-').ToLowerInvariant();
        var clientId = Environment.GetEnvironmentVariable("MYFORCE_MQTT_CLIENT_ID");

        return new MqttServiceOptions(
            Host: Environment.GetEnvironmentVariable("MYFORCE_MQTT_HOST") ?? "127.0.0.1",
            Port: int.TryParse(Environment.GetEnvironmentVariable("MYFORCE_MQTT_PORT"), out var port) ? port : 1883,
            ClientId: string.IsNullOrWhiteSpace(clientId) ? $"myforce-{normalizedServiceName}-{Environment.MachineName}" : clientId,
            UseTls: bool.TryParse(Environment.GetEnvironmentVariable("MYFORCE_MQTT_TLS"), out var useTls) && useTls,
            Username: Environment.GetEnvironmentVariable("MYFORCE_MQTT_USERNAME"),
            Password: Environment.GetEnvironmentVariable("MYFORCE_MQTT_PASSWORD"));
    }
}
