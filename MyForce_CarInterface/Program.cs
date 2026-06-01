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
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;

using var instanceLock = SingleInstanceLock.TryAcquire("myforce-car-interface");
if (instanceLock is null)
{
	Console.WriteLine("[car-interface] Another Car Interface instance is already running. Exiting to avoid MQTT client id takeover.");
	return;
}

using var cts = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, e) =>
{
	e.Cancel = true;
	cts.Cancel();
};

Console.CancelKeyPress += cancelHandler;

try
{
	var options = VehicleInterfaceOptions.Load();
	await using var app = new VehicleInterfaceProgram(options);
	await app.RunAsync(cts.Token).ConfigureAwait(false);
}
finally
{
	Console.CancelKeyPress -= cancelHandler;
}

internal sealed class VehicleInterfaceProgram : IAsyncDisposable
{
	private const string PttOrigin = "vip";

	private readonly IMqttClient _client;

	private readonly VehicleInterfaceOptions _options;

	private readonly JsonSerializerOptions _jsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = false
	};

	private MqttClientOptions? _clientOptions;

	private bool _isPressed;

	public VehicleInterfaceProgram(VehicleInterfaceOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		_options = options;
		_client = new MqttClientFactory().CreateMqttClient();
		_client.DisconnectedAsync += OnDisconnectedAsync;
	}

	public async Task RunAsync(CancellationToken cancellationToken)
	{
		_clientOptions = BuildClientOptions();
		await ConnectUntilStoppedAsync(cancellationToken).ConfigureAwait(false);

		Console.WriteLine("Vehicle Interface Program ready.");
		Console.WriteLine($"Publishing VIP PTT for radio '{_options.RadioId}' on console '{_options.ConsoleId}'.");
		Console.WriteLine("Press Space to toggle PTT, R to release, Q to quit.");

		while (!cancellationToken.IsCancellationRequested)
		{
			var key = Console.ReadKey(intercept: true);
			if (key.Key == ConsoleKey.Q)
			{
				break;
			}

			if (key.Key == ConsoleKey.Spacebar)
			{
				await PublishPttAsync(!_isPressed, cancellationToken).ConfigureAwait(false);
				continue;
			}

			if (key.Key == ConsoleKey.R && _isPressed)
			{
				await PublishPttAsync(isPressed: false, cancellationToken).ConfigureAwait(false);
			}
		}

		if (_isPressed)
		{
			await PublishPttAsync(isPressed: false, CancellationToken.None).ConfigureAwait(false);
		}
	}

	public async ValueTask DisposeAsync()
	{
		_client.DisconnectedAsync -= OnDisconnectedAsync;
		if (_client.IsConnected)
		{
			await _client.DisconnectAsync().ConfigureAwait(false);
		}

		_client.Dispose();
	}

	private MqttClientOptions BuildClientOptions()
	{
		var builder = new MqttClientOptionsBuilder()
			.WithProtocolVersion(MqttProtocolVersion.V500)
			.WithClientId(_options.ClientId)
			.WithTcpServer(_options.Host, _options.Port)
			.WithCleanSession();

		if (!string.IsNullOrWhiteSpace(_options.Username))
		{
			builder = builder.WithCredentials(_options.Username, _options.Password);
		}

		if (_options.UseTls)
		{
			builder = builder.WithTlsOptions(static tls => tls.UseTls());
		}

		return builder.Build();
	}

	private async Task ConnectUntilStoppedAsync(CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(_clientOptions);

		while (!_client.IsConnected)
		{
			try
			{
				Console.WriteLine($"[car-interface] Connecting to MQTT broker {_options.Host}:{_options.Port} as '{_options.ClientId}'.");
				await _client.ConnectAsync(_clientOptions, cancellationToken).ConfigureAwait(false);
				Console.WriteLine("[car-interface] Connected to MQTT broker.");
				return;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[car-interface] MQTT connect failed: {ex.Message}");
				await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
			}
		}
	}

	private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
	{
		Console.WriteLine($"[car-interface] MQTT disconnected: {arg.Exception?.Message ?? arg.ReasonString ?? "Disconnected."}");
		return Task.CompletedTask;
	}

	private async Task PublishPttAsync(bool isPressed, CancellationToken cancellationToken)
	{
		if (!_client.IsConnected)
		{
			await ConnectUntilStoppedAsync(cancellationToken).ConfigureAwait(false);
		}

		var command = new ConsolePttCommand(
			V: "1",
			Ts: DateTimeOffset.UtcNow,
			MsgId: Guid.NewGuid().ToString("N"),
			Auth: null,
			Origin: PttOrigin,
			Target: _options.RadioId,
			State: isPressed ? "down" : "up",
			Override: false);

		var message = new MqttApplicationMessageBuilder()
			.WithTopic($"myforce/console/{_options.ConsoleId}/cmd/ptt")
			.WithPayload(JsonSerializer.Serialize(command, _jsonOptions))
			.WithRetainFlag(false)
			.Build();

		await _client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
		_isPressed = isPressed;
		Console.WriteLine(isPressed ? "[car-interface] VIP PTT pressed." : "[car-interface] VIP PTT released.");
	}
}

internal sealed record ConsolePttCommand(
	string V,
	DateTimeOffset Ts,
	[property: System.Text.Json.Serialization.JsonPropertyName("msg_id")]
	string MsgId,
	string? Auth,
	string Origin,
	string Target,
	string State,
	bool Override);

internal sealed record VehicleInterfaceOptions(
	string Host,
	int Port,
	string ClientId,
	bool UseTls,
	string? Username,
	string? Password,
	string ConsoleId,
	string RadioId)
{
	public static VehicleInterfaceOptions Load()
	{
		return new VehicleInterfaceOptions(
			Host: GetEnvironmentValue("MYFORCE_MQTT_HOST", "127.0.0.1"),
			Port: GetEnvironmentInt32("MYFORCE_MQTT_PORT", 1883),
			ClientId: GetEnvironmentValue("MYFORCE_MQTT_CLIENT_ID", $"myforce-car-interface-{Environment.MachineName}"),
			UseTls: GetEnvironmentBoolean("MYFORCE_MQTT_USE_TLS"),
			Username: Environment.GetEnvironmentVariable("MYFORCE_MQTT_USERNAME"),
			Password: Environment.GetEnvironmentVariable("MYFORCE_MQTT_PASSWORD"),
			ConsoleId: GetEnvironmentValue("MYFORCE_CONSOLE_ID", "vip"),
			RadioId: GetEnvironmentValue("MYFORCE_PTT_RADIO_ID", "barrett"));
	}

	private static string GetEnvironmentValue(string name, string defaultValue)
	{
		var value = Environment.GetEnvironmentVariable(name);
		return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
	}

	private static int GetEnvironmentInt32(string name, int defaultValue)
	{
		var value = Environment.GetEnvironmentVariable(name);
		return int.TryParse(value, out var result) ? result : defaultValue;
	}

	private static bool GetEnvironmentBoolean(string name)
	{
		var value = Environment.GetEnvironmentVariable(name);
		return bool.TryParse(value, out var result) && result;
	}
}

internal sealed class SingleInstanceLock : IDisposable
{
	private readonly Mutex _mutex;

	private SingleInstanceLock(Mutex mutex)
	{
		_mutex = mutex;
	}

	public static SingleInstanceLock? TryAcquire(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);

		var mutex = new Mutex(initiallyOwned: false, $"Global\\{name}");
		try
		{
			if (!mutex.WaitOne(TimeSpan.Zero, exitContext: false))
			{
				mutex.Dispose();
				return null;
			}

			return new SingleInstanceLock(mutex);
		}
		catch (AbandonedMutexException)
		{
			return new SingleInstanceLock(mutex);
		}
	}

	public void Dispose()
	{
		_mutex.ReleaseMutex();
		_mutex.Dispose();
	}
}