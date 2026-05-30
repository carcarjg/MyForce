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
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Threading;

namespace MyForce.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly DispatcherTimer _clockTimer;
	private string _clock = string.Empty;
	private string _date = string.Empty;

	public MainWindowViewModel()
	{
		_clockTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1),
		};

		_clockTimer.Tick += OnClockTimerTick;
		UpdateClock();
		_clockTimer.Start();
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	public string Title => "MyForce Main Console";

	public string SpeedLabel => "SPD";

	public string SpeedValue => "55";

	public string LocationLabel => "LOCATION";

	public string LocationValue => "30.5422, -97.6384";

	public string TalkRadio => "TALK RADIO: APX7500 V/8";

	public string RadioChannel => "RADIO CH: CT OPS 800";

	public string AlertStatus => "ALERT L/S :  CODE 1\nDIRECTIONAL :  RIGHT\nSCENE :  LA / TD / RA\nSIREN :  DISABLED";

	public string CadMessage => "DISPATCH LINK READY";

	public string Clock
	{
		get => _clock;
		private set => SetProperty(ref _clock, value);
	}

	public string Date
	{
		get => _date;
		private set => SetProperty(ref _date, value);
	}

	public string Volume => "13";

	public void Dispose()
	{
		_clockTimer.Stop();
		_clockTimer.Tick -= OnClockTimerTick;
	}

	private void OnClockTimerTick(object? sender, EventArgs e)
	{
		UpdateClock();
	}

	private void UpdateClock()
	{
		var now = DateTime.Now;
		Clock = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
		Date = now.ToString("dd MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
	}

	private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (Equals(field, value))
		{
			return;
		}

		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
