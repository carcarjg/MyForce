using System;
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
    private MqttConnectionState _currentState;
    private bool _isDisposed;

    public MqttConnectionService()
    {
        _mqttClient = new MqttClientFactory().CreateMqttClient();
        _currentState = CreateDisconnectedState("Not connected.", string.Empty);

        _mqttClient.ConnectedAsync += OnConnectedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
    }

    public event EventHandler<MqttConnectionState>? StateChanged;

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

        try
        {
            await _mqttClient.ConnectAsync(optionsBuilder.Build(), cancellationToken).ConfigureAwait(false);
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
    }

   public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _mqttClient.ConnectedAsync -= OnConnectedAsync;
        _mqttClient.DisconnectedAsync -= OnDisconnectedAsync;

        _mqttClient.Dispose();
    }

    private Task OnConnectedAsync(MqttClientConnectedEventArgs arg)
    {
        var endpoint = CurrentState.Endpoint;
        UpdateState(new MqttConnectionState(true, "ONLINE", endpoint, "Broker connected."));
        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
    {
        var endpoint = CurrentState.Endpoint;
        var detail = arg.Exception?.Message ?? "Broker disconnected.";
        UpdateState(CreateDisconnectedState(detail, endpoint));
        return Task.CompletedTask;
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
