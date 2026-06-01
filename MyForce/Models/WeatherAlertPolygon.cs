using System.Collections.Generic;

namespace MyForce.Models;

internal sealed record WeatherAlertPolygon(
	string EventName,
	string Severity,
	IReadOnlyList<GeoCoordinate> Coordinates);
