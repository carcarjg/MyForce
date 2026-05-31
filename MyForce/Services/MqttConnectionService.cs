using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Formatter;
using MyForce.Models;

namespace MyForce.Services;

internal sealed class MqttConnectionService : IDisposable
{
    private readonly IMqttClient _mqttClient;
    private readonly Lock _syncRoot = new();
    private readonly CancellationTokenSource _lifetimeCancellationTokenSource = new();
    private readonly HashSet<string> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private MqttConnectionState _currentState;
    private MqttClientOptions? _clientOptions;
    private Task? _reconnectTask;
    private bool _isDisposed;
    private bool _isReconnectLoopRunning;

    public MqttConnectionService()
    {
        _mqttClient = new MqttClientFactory().CreateMqttClient();
        _currentState = CreateDisconnectedState("Not connected.", string.Empty);

        _mqttClient.ConnectedAsync += OnConnectedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
        _mqttClient.ApplicationMessageReceivedAsync += OnApplicationMessageReceivedAsync;
    }

    public event EventHandler<MqttConnectionState>? StateChanged;

    public event EventHandler<MqttApplicationMessage>? MessageReceived;

    public MqttConnectionState CurrentState
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentState;
            }
        }
    }

    public async Task ConnectAsync(MqttConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            throw new ArgumentException("MQTT host is required.", nameof(settings));
        }

        var endpoint = $"{settings.Host}:{settings.Port}";
        UpdateState(new MqttConnectionState(false, "CONNECTING", endpoint, "Connecting to broker..."));
        settings = settings with { UseTls = false };

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithClientId(settings.ClientId)
            .WithTcpServer(settings.Host, settings.Port);

        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            optionsBuilder = optionsBuilder.WithCredentials(settings.Username, settings.Password);
        }

        if (settings.UseTls)
        {
            optionsBuilder = optionsBuilder.WithTlsOptions(static tls =>
            {
                tls.UseTls();
            });
        }

        _clientOptions = optionsBuilder.Build();

        try
        {
            await _mqttClient.ConnectAsync(_clientOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            UpdateState(CreateDisconnectedState("Connection canceled.", endpoint));
            throw;
        }
        catch (Exception ex)
        {
            UpdateState(CreateDisconnectedState(ex.Message, endpoint));
        }

        StartReconnectLoop();
    }

    /// <summary>
    /// Subscribes the UI shell to a broker topic for AP state updates.
    /// </summary>
    public async Task SubscribeAsync(string topicFilter, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicFilter);

        lock (_syncRoot)
        {
            _subscriptions.Add(topicFilter);
        }

        if (!_mqttClient.IsConnected)
        {
            var endpoint = CurrentState.Endpoint;
            UpdateState(CreateDisconnectedState("Broker offline. Subscription will resume after reconnect.", endpoint));
            return;
        }

        await SubscribeCoreAsync(topicFilter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes a UI command message to the broker.
    /// </summary>
    public async Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        if (!_mqttClient.IsConnected)
        {
            var endpoint = CurrentState.Endpoint;
            UpdateState(CreateDisconnectedState("Broker offline. Command was not published.", endpoint));
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .Build();

        try
        {
            await _mqttClient.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateState(CreateDisconnectedState(ex.Message, CurrentState.Endpoint));
        }
    }

    public void Dispose()
    {
        Task? reconnectTask;

        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            reconnectTask = _reconnectTask;
        }

        _lifetimeCancellationTokenSource.Cancel();

        _mqttClient.ConnectedAsync -= OnConnectedAsync;
        _mqttClient.DisconnectedAsync -= OnDisconnectedAsync;
        _mqttClient.ApplicationMessageReceivedAsync -= OnApplicationMessageReceivedAsync;

        if (reconnectTask is not null)
        {
            try
            {
                reconnectTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_mqttClient.IsConnected)
        {
            _mqttClient.DisconnectAsync().GetAwaiter().GetResult();
        }

        _mqttClient.Dispose();
        _lifetimeCancellationTokenSource.Dispose();
    }

    private async Task OnConnectedAsync(MqttClientConnectedEventArgs arg)
    {
        var endpoint = CurrentState.Endpoint;
        UpdateState(new MqttConnectionState(true, "ONLINE", endpoint, "Broker connected."));
        await ResubscribeAsync().ConfigureAwait(false);
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
    {
        var endpoint = CurrentState.Endpoint;
        var detail = arg.Exception?.Message ?? arg.ReasonString ?? "Broker disconnected.";
        UpdateState(CreateDisconnectedState(detail, endpoint));
        StartReconnectLoop();
        return Task.CompletedTask;
    }

    private Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
    {
        MessageReceived?.Invoke(this, arg.ApplicationMessage);
        return Task.CompletedTask;
    }

    private async Task SubscribeCoreAsync(string topicFilter, CancellationToken cancellationToken)
    {
        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(topicFilter)
            .Build();

        try
        {
            await _mqttClient.SubscribeAsync(subscribeOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateState(CreateDisconnectedState(ex.Message, CurrentState.Endpoint));
        }
    }

    private async Task ResubscribeAsync()
    {
        string[] subscriptions;

        lock (_syncRoot)
        {
            subscriptions = [.. _subscriptions];
        }

        foreach (var topicFilter in subscriptions)
        {
            await SubscribeCoreAsync(topicFilter, _lifetimeCancellationTokenSource.Token).ConfigureAwait(false);
        }
    }

    private void StartReconnectLoop()
    {
        lock (_syncRoot)
        {
            if (_isDisposed || _isReconnectLoopRunning || _mqttClient.IsConnected || _clientOptions is null)
            {
                return;
            }

            _isReconnectLoopRunning = true;
            _reconnectTask = RunReconnectLoopAsync();
        }
    }

    private async Task RunReconnectLoopAsync()
    {
        try
        {
            while (!_lifetimeCancellationTokenSource.IsCancellationRequested)
            {
                if (_mqttClient.IsConnected)
                {
                    return;
                }

                try
                {
                    ArgumentNullException.ThrowIfNull(_clientOptions);
                    UpdateState(new MqttConnectionState(false, "CONNECTING", CurrentState.Endpoint, "Reconnecting to broker..."));
                    await _mqttClient.ConnectAsync(_clientOptions, _lifetimeCancellationTokenSource.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (_lifetimeCancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    UpdateState(CreateDisconnectedState(ex.Message, CurrentState.Endpoint));
                }

                await Task.Delay(TimeSpan.FromSeconds(1), _lifetimeCancellationTokenSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellationTokenSource.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_syncRoot)
            {
                _isReconnectLoopRunning = false;
                _reconnectTask = null;
            }

            if (!_isDisposed && !_mqttClient.IsConnected)
            {
                StartReconnectLoop();
            }
        }
    }

    private void UpdateState(MqttConnectionState state)
    {
        lock (_syncRoot)
        {
            _currentState = state;
        }

        StateChanged?.Invoke(this, state);
    }

    private static MqttConnectionState CreateDisconnectedState(string detail, string endpoint)
    {
        return new MqttConnectionState(false, "OFFLINE", endpoint, detail);
    }
}
