namespace MyForce.Models;

public sealed record MqttTopicGroup(
    string Name,
    string Topic,
    string Retain,
    string Qos,
    string Purpose)
{
    public string RetainLabel => $"Retain: {Retain}";

    public string QosLabel => $"QoS: {Qos}";
}
