using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyForce.Contracts.Radio;

/// <summary>
/// Defines the shared Audio Processor to Radio Module contract version.
/// </summary>
public static class RadioContract
{
	public const int Version = 1;
}

/// <summary>
/// Describes a radio module factory that the Audio Processor can discover and instantiate.
/// </summary>
public interface IRadioModuleFactory
{
	string TypeId { get; }

	string DisplayName { get; }

	string Version { get; }

	int ContractVersion { get; }

	string ConfigSchema { get; }

	RadioCapabilities Capabilities { get; }

	IRadioModule Create(IModuleHost host);
}

/// <summary>
/// Describes the runtime behavior for a single radio module instance.
/// </summary>
public interface IRadioModule : IAsyncDisposable
{
	Task<OperationResult> ApplyConfigAsync(JsonObject configuration, CancellationToken cancellationToken);

	JsonObject GetConfig();

	Task StartAsync(CancellationToken cancellationToken);

	Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Provides host services that a radio module can use for AP-mediated interactions.
/// </summary>
public interface IModuleHost
{
	IControlTransport? ControlTransport { get; }

	float GetRxLevel();

	Task ReportStateAsync(RadioStateReport state, CancellationToken cancellationToken);

	Task ReportDetectAsync(bool isDetected, CancellationToken cancellationToken);

	Task EmitEventAsync(string name, JsonObject? data, CancellationToken cancellationToken);

	void Log(LogLevel level, string message);
}

/// <summary>
/// Provides the optional shared serial transport used when the AP owns a combined keying and CAT port.
/// </summary>
public interface IControlTransport
{
	Task WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

	Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>
/// Provides in-process RM-owned keying for radios that do not use AP relay keying.
/// </summary>
public interface IKeyingProvider
{
	Task<KeyingResult> KeyAsync(bool isPressed, CancellationToken cancellationToken);
}

/// <summary>
/// Exposes ARM-owned audio exchange hooks for radios that provide their own audio.
/// </summary>
public interface IAudioProvider
{
	IAudioExchange AudioExchange { get; }
}

/// <summary>
/// Provides PCM exchange methods between an ARM and the AP audio engine.
/// </summary>
public interface IAudioExchange
{
	void WriteRxPcm(ReadOnlyMemory<float> samples);

	int ReadTxPcm(Memory<float> samples);
}

/// <summary>
/// Captures the static capabilities a radio module advertises.
/// </summary>
public sealed record RadioCapabilities(
	IReadOnlyList<KeyingMethod> Keying,
	IReadOnlyList<DetectMethod> Detect,
	bool ProvidesAudio,
	IReadOnlyList<string> Controls);

/// <summary>
/// Describes the operator-selected keying settings for a radio instance.
/// </summary>
public sealed record KeyingConfig(
	KeyingMethod Method,
	RelayBinding? Relay,
	int PttLeadMs,
	int PttTailMs,
	bool TalkPermit);

/// <summary>
/// Describes the operator-selected detection settings for a radio instance.
/// </summary>
public sealed record DetectConfig(
	DetectMethod Method,
	VoxConfig? Vox);

/// <summary>
/// Describes the AP soundcard binding for a radio that does not provide audio through an ARM.
/// </summary>
public sealed record DeviceBindingConfig(string? Soundcard);

/// <summary>
/// Combines the AP-owned common radio config sections with RM-owned settings.
/// </summary>
public sealed record RadioModuleInstanceConfig(
	KeyingConfig Keying,
	DetectConfig Detect,
	DeviceBindingConfig? Device,
	JsonObject Settings);

/// <summary>
/// Describes a relay channel binding owned by the Audio Processor.
/// </summary>
public sealed record RelayBinding(string RelaySet, int Channel);

/// <summary>
/// Describes the AP-owned VOX timing and threshold settings.
/// </summary>
public sealed record VoxConfig(double ThresholdDb, int AttackMs, int HangMs);

/// <summary>
/// Describes a module-reported runtime state snapshot.
/// </summary>
public sealed record RadioStateReport(
	ChannelInfo? Channel,
	ZoneInfo? Zone,
	string? Mode,
	SignalInfo? Signal,
	bool? Ready);

/// <summary>
/// Describes a channel identity reported by a radio module.
/// </summary>
public sealed record ChannelInfo(int Index, string? Label);

/// <summary>
/// Describes a zone identity reported by a radio module.
/// </summary>
public sealed record ZoneInfo(int Index, string? Label);

/// <summary>
/// Describes signal metadata reported by a radio module.
/// </summary>
public sealed record SignalInfo(int? RssiDbm);

/// <summary>
/// Describes the result of a keying operation initiated by the TX Controller.
/// </summary>
public sealed record KeyingResult(bool Ready, string? Detail = null);

/// <summary>
/// Describes the result of a config or control operation.
/// </summary>
public sealed record OperationResult(OperationStatus Status, IReadOnlyList<FieldError>? Errors = null)
{
	public static OperationResult Ok() => new(OperationStatus.Ok);

	public static OperationResult Rejected(IReadOnlyList<FieldError> errors) => new(OperationStatus.Rejected, errors);

	public static OperationResult Error(string message) => new(OperationStatus.Error, [new FieldError(null, "error", message)]);
}

/// <summary>
/// Describes a validation or execution issue associated with a field.
/// </summary>
public sealed record FieldError(string? Field, string Code, string Message);

public enum OperationStatus
{
	Ok,
	Rejected,
	Error
}

public enum KeyingMethod
{
	Relay,
	Rm
}

public enum DetectMethod
{
	Vox,
	Rm
}

public enum LogLevel
{
	Trace,
	Debug,
	Info,
	Warning,
	Error
}

/// <summary>
/// Builds the AP-owned common radio config schema from capabilities and RM settings schema.
/// </summary>
public static class RadioModuleSchemaBuilder
{
	public static JsonObject BuildInstanceSchema(RadioCapabilities capabilities, string settingsSchemaJson)
	{
		ArgumentNullException.ThrowIfNull(capabilities);
		ArgumentException.ThrowIfNullOrWhiteSpace(settingsSchemaJson);

		var settingsSchemaNode = JsonNode.Parse(settingsSchemaJson) as JsonObject
			?? throw new JsonException("The settings schema must be a JSON object.");

		var schema = new JsonObject
		{
			["$schema"] = "https://json-schema.org/draft/2020-12/schema",
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["keying"] = BuildKeyingSchema(capabilities),
				["detect"] = BuildDetectSchema(capabilities),
				["settings"] = settingsSchemaNode.DeepClone()
			},
			["required"] = new JsonArray("keying", "detect", "settings")
		};

		if (!capabilities.ProvidesAudio)
		{
			((JsonObject)schema["properties"]!).Add("device", BuildDeviceSchema());
			((JsonArray)schema["required"]!).Add("device");
		}

		return schema;
	}

	private static JsonObject BuildKeyingSchema(RadioCapabilities capabilities)
	{
		return new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["method"] = BuildEnumSchema(capabilities.Keying.Select(static method => method.ToString().ToLowerInvariant())),
				["relay"] = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject
					{
						["relay_set"] = new JsonObject { ["type"] = "string" },
						["channel"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 }
					},
					["required"] = new JsonArray("relay_set", "channel")
				},
				["ptt_lead_ms"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
				["ptt_tail_ms"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
				["talk_permit"] = new JsonObject { ["type"] = "boolean" }
			},
			["required"] = new JsonArray("method", "ptt_lead_ms", "ptt_tail_ms", "talk_permit")
		};
	}

	private static JsonObject BuildDetectSchema(RadioCapabilities capabilities)
	{
		return new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["method"] = BuildEnumSchema(capabilities.Detect.Select(static method => method.ToString().ToLowerInvariant())),
				["vox"] = new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject
					{
						["threshold_db"] = new JsonObject { ["type"] = "number" },
						["attack_ms"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
						["hang_ms"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 }
					},
					["required"] = new JsonArray("threshold_db", "attack_ms", "hang_ms")
				}
			},
			["required"] = new JsonArray("method")
		};
	}

	private static JsonObject BuildDeviceSchema()
	{
		return new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["soundcard"] = new JsonObject { ["type"] = "string" }
			},
			["required"] = new JsonArray("soundcard")
		};
	}

	private static JsonObject BuildEnumSchema(IEnumerable<string> values)
	{
		ArgumentNullException.ThrowIfNull(values);
		var enumValues = new JsonArray(values.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray());

		return new JsonObject
		{
			["type"] = "string",
			["enum"] = enumValues
		};
	}
}
