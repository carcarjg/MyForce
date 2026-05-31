namespace MyForce.Models;

public sealed record SystemConnection(
    string Source,
    string Target,
    string Transport,
    string Plane);

public sealed record MqttConnectionSettings(
    string Host,
    int Port,
    string ClientId,
    bool UseTls = false,
    string? Username = null,
    string? Password = null);

public sealed record MqttConnectionState(
    bool IsConnected,
    string Status,
    string Endpoint,
    string Detail);
