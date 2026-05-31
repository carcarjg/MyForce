namespace MyForce.Models;

public sealed record InternetRadioStation(
	string StreamUrl,
	string DisplayName,
	string Genre,
	string Language,
	int Bitrate,
	bool IsEnabled);
