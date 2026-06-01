// %%%%%%    @%%%%%@
//%%%%%%%%   %%%%%%%@
//@%%%%%%%@  %%%%%%%%%        @@      @@  @@@      @@@ @@@     @@@ @@@@@@@@@@   @@@@@@@@@
//%%%%%%%%@ @%%%%%%%%       @@@@@   @@@@ @@@@@   @@@@ @@@@   @@@@ @@@@@@@@@@@@@@@@@@@@@@@ @@@@
// @%%%%%%%%  %%%%%%%%%      @@@@@@  @@@@  @@@@  @@@@   @@@@@@@@@     @@@@    @@@@         @@@@
//  %%%%%%%%%  %%%%%%%%@     @@@@@@@ @@@@   @@@@@@@@     @@@@@@       @@@@    @@@@@@@@@@@  @@@@
//   %%%%%%%%@  %%%%%%%%%    @@@@@@@@@@@@     @@@@        @@@@@       @@@@    @@@@@@@@@@@  @@@@
//    %%%%%%%%@ @%%%%%%%%    @@@@ @@@@@@@     @@@@      @@@@@@@@      @@@@    @@@@         @@@@
//    @%%%%%%%%% @%%%%%%%%   @@@@   @@@@@     @@@@     @@@@@ @@@@@    @@@@    @@@@@@@@@@@@ @@@@@@@@@@
//     @%%%%%%%%  %%%%%%%%@  @@@@    @@@@     @@@@    @@@@     @@@@   @@@@    @@@@@@@@@@@@ @@@@@@@@@@@
//      %%%%%%%%@ @%%%%%%%%
//      @%%%%%%%%  @%%%%%%%%
//       %%%%%%%%   %%%%%%%@
//         %%%%%      %%%%
//
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MyForce.Models;

namespace MyForce.Services;

internal sealed class WeatherAlertService
{
	private static readonly HttpClient HttpClient = CreateHttpClient();

	public async Task<IReadOnlyList<WeatherAlertPolygon>> GetActiveAlertsAsync(GeoCoordinate center, CancellationToken cancellationToken)
	{
		string requestUri = FormattableString.Invariant($"https://api.weather.gov/alerts/active?point={center.Latitude.ToString(CultureInfo.InvariantCulture)},{center.Longitude.ToString(CultureInfo.InvariantCulture)}");
		using HttpResponseMessage response = await HttpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			return Array.Empty<WeatherAlertPolygon>();
		}

		await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);
		if (!document.RootElement.TryGetProperty("features", out JsonElement featuresElement) || featuresElement.ValueKind != JsonValueKind.Array)
		{
			return Array.Empty<WeatherAlertPolygon>();
		}

		List<WeatherAlertPolygon> polygons = [];
		foreach (JsonElement feature in featuresElement.EnumerateArray())
		{
			if (!feature.TryGetProperty("geometry", out JsonElement geometryElement) || geometryElement.ValueKind == JsonValueKind.Null)
			{
				continue;
			}

			if (!feature.TryGetProperty("properties", out JsonElement propertiesElement))
			{
				continue;
			}

			string eventName = propertiesElement.TryGetProperty("event", out JsonElement eventElement)
				? eventElement.GetString() ?? "ALERT"
				: "ALERT";

			string severity = propertiesElement.TryGetProperty("severity", out JsonElement severityElement)
				? severityElement.GetString() ?? "UNKNOWN"
				: "UNKNOWN";

			AppendPolygons(polygons, geometryElement, eventName, severity);
		}

		return polygons;
	}

	private static void AppendPolygons(List<WeatherAlertPolygon> polygons, JsonElement geometryElement, string eventName, string severity)
	{
		if (!geometryElement.TryGetProperty("type", out JsonElement typeElement))
		{
			return;
		}

		string geometryType = typeElement.GetString() ?? string.Empty;
		if (!geometryElement.TryGetProperty("coordinates", out JsonElement coordinatesElement))
		{
			return;
		}

		switch (geometryType)
		{
			case "Polygon":
				AppendPolygon(polygons, coordinatesElement, eventName, severity);
				break;

			case "MultiPolygon":
				foreach (JsonElement polygonElement in coordinatesElement.EnumerateArray())
				{
					AppendPolygon(polygons, polygonElement, eventName, severity);
				}
				break;
		}
	}

	private static void AppendPolygon(List<WeatherAlertPolygon> polygons, JsonElement polygonElement, string eventName, string severity)
	{
		if (polygonElement.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		foreach (JsonElement ringElement in polygonElement.EnumerateArray())
		{
			if (ringElement.ValueKind != JsonValueKind.Array)
			{
				continue;
			}

			List<GeoCoordinate> coordinates = [];
			foreach (JsonElement pointElement in ringElement.EnumerateArray())
			{
				if (pointElement.ValueKind != JsonValueKind.Array)
				{
					continue;
				}

				using JsonElement.ArrayEnumerator pointValues = pointElement.EnumerateArray();
				if (!pointValues.MoveNext())
				{
					continue;
				}

				double longitude = pointValues.Current.GetDouble();
				if (!pointValues.MoveNext())
				{
					continue;
				}

				double latitude = pointValues.Current.GetDouble();
				coordinates.Add(new GeoCoordinate(latitude, longitude));
			}

			if (coordinates.Count >= 3)
			{
				polygons.Add(new WeatherAlertPolygon(eventName, severity, coordinates));
			}

			break;
		}
	}

	private static HttpClient CreateHttpClient()
	{
		HttpClient client = new();
		client.DefaultRequestHeaders.UserAgent.ParseAdd("MyForce/1.0");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/geo+json");
		client.Timeout = TimeSpan.FromSeconds(10);
		return client;
	}
}