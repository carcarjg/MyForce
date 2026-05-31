using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MyForce.Models;

namespace MyForce.Services;

internal sealed class InternetRadioCatalogService
{
	private const string StreamDataPrefix = "stream_data[";

	public IReadOnlyList<InternetRadioStation> LoadCatalog(string streamsFilePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(streamsFilePath);

		if (!File.Exists(streamsFilePath))
		{
			return Array.Empty<InternetRadioStation>();
		}

		var stations = new List<InternetRadioStation>();

		foreach (var line in File.ReadLines(streamsFilePath))
		{
			if (!TryParseStation(line, out var station))
			{
				continue;
			}

			if (!station.IsEnabled || string.IsNullOrWhiteSpace(station.DisplayName) || string.Equals(station.DisplayName, "LEGAL", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			stations.Add(station);
		}

		return stations
			.OrderBy(static station => station.DisplayName, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static bool TryParseStation(string? line, out InternetRadioStation station)
	{
		station = default!;
		if (string.IsNullOrWhiteSpace(line))
		{
			return false;
		}

		var trimmed = line.Trim();
		if (!trimmed.StartsWith(StreamDataPrefix, StringComparison.Ordinal))
		{
			return false;
		}

		var separatorIndex = trimmed.IndexOf(':');
		if (separatorIndex < 0)
		{
			return false;
		}

		var firstQuoteIndex = trimmed.IndexOf('"', separatorIndex);
		var lastQuoteIndex = trimmed.LastIndexOf('"');
		if (firstQuoteIndex < 0 || lastQuoteIndex <= firstQuoteIndex)
		{
			return false;
		}

		var payload = trimmed[(firstQuoteIndex + 1)..lastQuoteIndex];
		var parts = payload.Split('|');
		if (parts.Length < 6)
		{
			return false;
		}

		var streamUrl = parts[0].Trim();
		var displayName = parts[1].Trim();
		var genre = parts[2].Trim();
		var language = parts[3].Trim();
		var bitrate = int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBitrate) ? parsedBitrate : 0;
		var isEnabled = parts[5].Trim() == "1";
		if (string.IsNullOrWhiteSpace(streamUrl) || string.IsNullOrWhiteSpace(displayName))
		{
			return false;
		}

		station = new InternetRadioStation(streamUrl, displayName, genre, language, bitrate, isEnabled);
		return true;
	}
}
