using System;
using System.IO;
using System.Text.Json;
using MyForce.Models;

namespace MyForce.Services;

/// <summary>
/// Saves and restores the local AM/FM UI shell state between launches.
/// </summary>
internal sealed class AmFmUiStateStore
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	private readonly string _stateFilePath;

	public AmFmUiStateStore()
	{
		var appDataPath = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"MyForce");
		Directory.CreateDirectory(appDataPath);
		_stateFilePath = Path.Combine(appDataPath, "amfm-ui-state.json");
	}

	/// <summary>
	/// Loads the previously persisted AM/FM UI state.
	/// </summary>
	public AmFmUiState Load()
	{
		if (!File.Exists(_stateFilePath))
		{
			return AmFmUiState.Default;
		}

		try
		{
			var json = File.ReadAllText(_stateFilePath);
			return JsonSerializer.Deserialize<AmFmUiState>(json, SerializerOptions) ?? AmFmUiState.Default;
		}
		catch
		{
			return AmFmUiState.Default;
		}
	}

	/// <summary>
	/// Persists the current AM/FM UI state for the next launch.
	/// </summary>
	public void Save(AmFmUiState state)
	{
		ArgumentNullException.ThrowIfNull(state);

		var json = JsonSerializer.Serialize(state, SerializerOptions);
		File.WriteAllText(_stateFilePath, json);
	}
}
