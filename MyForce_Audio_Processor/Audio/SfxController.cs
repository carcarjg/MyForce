// %%%%%%    @%%%%%@
// Copyright (C) 2025-2026 NyxTel Wireless / Nyx Gallini
//
// PROJECT_FRAMEWORK.md Phase 2 (extension): the sound-effects (SFX) source. SFX are an input
// into the master output matrix alongside the radios and the entertainment source (§3.5). On
// this PipeWire-as-matrix host each SFX is a short PipeWire stream into the master sink, tagged
// so it mixes with everything else; its loudness is the SFX volume on the 0..25 scale.
//
// Two kinds are supported: stored WAV/audio clips and generated tones (synthesized by ffmpeg's
// lavfi sine source). Playback is fire-and-forget (ffplay -autoexit), so transient effects need
// no lifecycle tracking.

using System.Diagnostics;
using System.Globalization;

/// <summary>A request to play a sound effect (§3.5): a file clip or a generated tone.</summary>
internal sealed record SfxRequest(string? Kind, string? Path, int? FrequencyHz, int? DurationMs);

internal sealed class SfxController
{
	private const string SfxAppName = "myforce-sfx";

	private readonly Action<string, string> _log;

	// SFX volume as a sink-input percent (0..100), from the 0..25 operator scale.
	private int _volumePercent = 100;

	public SfxController(Action<string, string> log)
	{
		ArgumentNullException.ThrowIfNull(log);
		_log = log;
	}

	/// <summary>Set the SFX source volume. Gain is 0..1 (0..100%) on the 0..25 operator scale.</summary>
	public void SetVolume(decimal gain)
	{
		_volumePercent = Math.Clamp((int)Math.Round((double)decimal.Clamp(gain, 0m, 1m) * 100.0), 0, 100);
	}

	/// <summary>
	/// Play a sound effect into the master sink at the current SFX volume. Fire-and-forget; the
	/// ffplay process self-exits when the clip/tone ends.
	/// </summary>
	public void Play(SfxRequest request, string masterSink)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(masterSink);

		if (!OperatingSystem.IsLinux())
		{
			_log("sfx", $"SFX '{request.Kind}' requested (no-op on the non-Linux dev host).");
			return;
		}

		var startInfo = request.Kind?.Trim().ToLowerInvariant() switch
		{
			"file" => BuildFileSfx(request, masterSink),
			"tone" => BuildToneSfx(request, masterSink),
			_ => null
		};

		if (startInfo is null)
		{
			_log("sfx", $"SFX rejected: unsupported kind '{request.Kind}'.");
			return;
		}

		try
		{
			using var process = Process.Start(startInfo);
			_log("sfx", $"Playing {request.Kind} SFX at {_volumePercent}% into '{masterSink}'.");
		}
		catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			_log("sfx", $"SFX playback failed: {ex.Message} (is ffplay installed?).");
		}
	}

	private ProcessStartInfo? BuildFileSfx(SfxRequest request, string masterSink)
	{
		if (string.IsNullOrWhiteSpace(request.Path) || !File.Exists(request.Path))
		{
			_log("sfx", $"SFX file not found: '{request.Path}'.");
			return null;
		}

		var startInfo = CreateFfplay(masterSink);
		startInfo.ArgumentList.Add(request.Path);
		return startInfo;
	}

	private ProcessStartInfo BuildToneSfx(SfxRequest request, string masterSink)
	{
		var frequency = Math.Clamp(request.FrequencyHz ?? 1000, 50, 15000);
		var durationMs = Math.Clamp(request.DurationMs ?? 200, 20, 5000);
		var seconds = (durationMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

		var startInfo = CreateFfplay(masterSink);
		startInfo.ArgumentList.Add("-f");
		startInfo.ArgumentList.Add("lavfi");
		startInfo.ArgumentList.Add("-i");
		startInfo.ArgumentList.Add($"sine=frequency={frequency}:duration={seconds}");
		return startInfo;
	}

	private ProcessStartInfo CreateFfplay(string masterSink)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "ffplay",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true,
			RedirectStandardOutput = true
		};

		LinuxRuntimeEnvironment.Apply(startInfo);
		startInfo.Environment["SDL_AUDIODRIVER"] = "pulseaudio";
		startInfo.Environment["PULSE_PROP"] = $"application.name={SfxAppName}";
		if (!string.IsNullOrWhiteSpace(masterSink) && !string.Equals(masterSink, "@DEFAULT_SINK@", StringComparison.Ordinal))
		{
			startInfo.Environment["PULSE_SINK"] = masterSink;
		}

		startInfo.ArgumentList.Add("-nodisp");
		startInfo.ArgumentList.Add("-autoexit");
		startInfo.ArgumentList.Add("-hide_banner");
		startInfo.ArgumentList.Add("-loglevel");
		startInfo.ArgumentList.Add("error");
		startInfo.ArgumentList.Add("-volume");
		startInfo.ArgumentList.Add(_volumePercent.ToString(CultureInfo.InvariantCulture));
		return startInfo;
	}
}
