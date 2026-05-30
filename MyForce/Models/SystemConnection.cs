namespace MyForce.Models;

public sealed record SystemConnection(
    string Source,
    string Target,
    string Transport,
    string Plane);
