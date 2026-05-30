namespace MyForce.Models;

public sealed record SystemComponent(
    string Name,
    string Plane,
    string Role,
    string Transport,
    string Status,
    bool IsCore);
