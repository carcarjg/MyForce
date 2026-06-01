using System;

namespace MyForce.Models;

/// <summary>
/// Persists the last AM/FM source mode and source-specific selections for the local UI shell.
/// </summary>
public sealed record AmFmUiState(
	string LastMode,
	decimal FmFrequency,
	decimal AmFrequency,
	string? BluetoothLabel,
	string? InternetStreamUrl,
	bool IsMuted,
	int Volume,
	decimal?[]? FmPresets,
	decimal?[]? AmPresets,
	string?[]? InternetPresets)
{
	/// <summary>
	/// Gets the default local UI state used when no persisted file exists.
	/// </summary>
	public static AmFmUiState Default { get; } = new(
		LastMode: "Fm1",
		FmFrequency: 97.5m,
		AmFrequency: 87.5m,
		BluetoothLabel: "BT AUDIO",
		InternetStreamUrl: null,
		IsMuted: false,
		Volume: 25,
		FmPresets: Array.Empty<decimal?>(),
		AmPresets: Array.Empty<decimal?>(),
		InternetPresets: Array.Empty<string?>());
}
