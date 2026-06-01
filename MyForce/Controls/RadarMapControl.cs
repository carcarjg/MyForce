using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MyForce.Models;
using MyForce.Services;

namespace MyForce.Controls;

public sealed class RadarMapControl : Control, IDisposable
{
	public static readonly StyledProperty<double> LatitudeProperty = AvaloniaProperty.Register<RadarMapControl, double>(nameof(Latitude));
	public static readonly StyledProperty<double> LongitudeProperty = AvaloniaProperty.Register<RadarMapControl, double>(nameof(Longitude));
	public static readonly StyledProperty<int> ZoomLevelProperty = AvaloniaProperty.Register<RadarMapControl, int>(nameof(ZoomLevel), 8);
	public static readonly StyledProperty<double> HeadingDegreesProperty = AvaloniaProperty.Register<RadarMapControl, double>(nameof(HeadingDegrees));

	private readonly MapTileService _mapTileService = new();
	private readonly WeatherAlertService _weatherAlertService = new();
	private readonly Dictionary<(int Zoom, int X, int Y), Bitmap?> _visibleTiles = [];
	private readonly DispatcherTimer _refreshTimer;
	private IReadOnlyList<WeatherAlertPolygon> _alertPolygons = Array.Empty<WeatherAlertPolygon>();
	private CancellationTokenSource? _refreshCts;
	private bool _disposed;
	private bool _isAttachedToVisualTree;
	private bool _isPointerCaptured;
	private bool _isMapLoading = true;
	private string? _statusMessage = "Loading map tiles...";
	private double _lastAlertLatitude = double.NaN;
	private double _lastAlertLongitude = double.NaN;
	private double _viewportLatitude = double.NaN;
	private double _viewportLongitude = double.NaN;
	private Point _lastPointerPosition;

	static RadarMapControl()
	{
		AffectsRender<RadarMapControl>(LatitudeProperty, LongitudeProperty, ZoomLevelProperty, HeadingDegreesProperty);
		LatitudeProperty.Changed.AddClassHandler<RadarMapControl>((control, _) => control.OnViewportChanged());
		LongitudeProperty.Changed.AddClassHandler<RadarMapControl>((control, _) => control.OnViewportChanged());
		ZoomLevelProperty.Changed.AddClassHandler<RadarMapControl>((control, _) => control.OnViewportChanged());
		HeadingDegreesProperty.Changed.AddClassHandler<RadarMapControl>((control, _) => control.InvalidateVisual());
	}

	public RadarMapControl()
	{
		ClipToBounds = true;
		_refreshTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(60),
		};

		_refreshTimer.Tick += OnRefreshTimerTick;
	}

	public double Latitude
	{
		get => GetValue(LatitudeProperty);
		set => SetValue(LatitudeProperty, value);
	}

	public double Longitude
	{
		get => GetValue(LongitudeProperty);
		set => SetValue(LongitudeProperty, value);
	}

	public int ZoomLevel
	{
		get => GetValue(ZoomLevelProperty);
		set => SetValue(ZoomLevelProperty, value);
	}

	public double HeadingDegrees
	{
		get => GetValue(HeadingDegreesProperty);
		set => SetValue(HeadingDegreesProperty, value);
	}

	public override void Render(DrawingContext context)
	{
		base.Render(context);

		Rect bounds = new(Bounds.Size);
		context.FillRectangle(new SolidColorBrush(Color.Parse("#111111")), bounds);
		if (bounds.Width <= 0 || bounds.Height <= 0)
		{
			return;
		}

		EnsureViewportCenterInitialized();
		GeoPixelCoordinate centerPixel = Project(_viewportLatitude, _viewportLongitude, ZoomLevel);
		double left = centerPixel.X - bounds.Width / 2d;
		double top = centerPixel.Y - bounds.Height / 2d;
		int tileSize = 256;
		int minTileX = (int)Math.Floor(left / tileSize);
		int maxTileX = (int)Math.Floor((left + bounds.Width) / tileSize);
		int minTileY = (int)Math.Floor(top / tileSize);
		int maxTileY = (int)Math.Floor((top + bounds.Height) / tileSize);
		int mapSizeInTiles = 1 << ZoomLevel;

		for (int tileX = minTileX; tileX <= maxTileX; tileX++)
		{
			for (int tileY = minTileY; tileY <= maxTileY; tileY++)
			{
				if (tileY < 0 || tileY >= mapSizeInTiles)
				{
					continue;
				}

				int wrappedTileX = Mod(tileX, mapSizeInTiles);
				Rect destination = new(
					tileX * tileSize - left,
					tileY * tileSize - top,
					tileSize,
					tileSize);

				if (_visibleTiles.TryGetValue((ZoomLevel, wrappedTileX, tileY), out Bitmap? tileBitmap) && tileBitmap is not null)
				{
					context.DrawImage(tileBitmap, new Rect(0, 0, tileBitmap.Size.Width, tileBitmap.Size.Height), destination);
				}
				else
				{
					context.FillRectangle(new SolidColorBrush(Color.Parse("#1E1E1E")), destination);
					context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#242424"))), destination);
				}
			}
		}

		DrawAlertPolygons(context, bounds, left, top);
		DrawLocationMarker(context, left, top);
		DrawStatusOverlay(context, bounds);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_refreshCts?.Cancel();
		_refreshCts?.Dispose();
		_refreshTimer.Stop();
		_refreshTimer.Tick -= OnRefreshTimerTick;
		_mapTileService.Dispose();
	}

	protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		_isAttachedToVisualTree = true;
		EnsureViewportCenterInitialized();
		_refreshTimer.Start();
		OnViewportChanged();
	}

	protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		_isAttachedToVisualTree = false;
		_refreshTimer.Stop();
		base.OnDetachedFromVisualTree(e);
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		if (_disposed)
		{
			return;
		}

		_lastPointerPosition = e.GetPosition(this);
		_isPointerCaptured = true;
		e.Pointer.Capture(this);
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		if (!_isPointerCaptured)
		{
			return;
		}

		Point currentPosition = e.GetPosition(this);
		Vector delta = currentPosition - _lastPointerPosition;
		_lastPointerPosition = currentPosition;
		PanViewport(delta);
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		ReleasePointerCapture(e.Pointer);
	}

	protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
	{
		base.OnPointerCaptureLost(e);
		_isPointerCaptured = false;
	}

	protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
	{
		base.OnPointerWheelChanged(e);
		if (_disposed)
		{
			return;
		}

		int newZoomLevel = Math.Clamp(ZoomLevel + (e.Delta.Y > 0 ? 1 : -1), 3, 16);
		if (newZoomLevel == ZoomLevel)
		{
			return;
		}

		ZoomLevel = newZoomLevel;
		e.Handled = true;
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		OnViewportChanged();
		return base.ArrangeOverride(finalSize);
	}

	private void OnViewportChanged()
	{
		if (!_isAttachedToVisualTree || Bounds.Width <= 0 || Bounds.Height <= 0 || _disposed)
		{
			return;
		}

		if (double.IsNaN(_viewportLatitude) || double.IsNaN(_viewportLongitude))
		{
			_viewportLatitude = Latitude;
			_viewportLongitude = Longitude;
		}

		_ = RefreshAsync();
	}

	private async Task RefreshAsync()
	{
		_refreshCts?.Cancel();
		_refreshCts?.Dispose();
		_refreshCts = new CancellationTokenSource();
		CancellationToken cancellationToken = _refreshCts.Token;

		try
		{
			_isMapLoading = true;
			_statusMessage = "Loading map tiles...";
			await Dispatcher.UIThread.InvokeAsync(InvalidateVisual);
			await LoadVisibleTilesAsync(cancellationToken).ConfigureAwait(false);
			await LoadAlertsAsync(cancellationToken).ConfigureAwait(false);
			_isMapLoading = false;
			_statusMessage = _visibleTiles.Values.Any(bitmap => bitmap is not null)
				? null
				: "Map tiles unavailable";
			await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(InvalidateVisual);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception)
		{
			_isMapLoading = false;
			_statusMessage = "Unable to refresh map data";
			await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(InvalidateVisual);
		}
	}

	private async Task LoadVisibleTilesAsync(CancellationToken cancellationToken)
	{
		Rect bounds = new(Bounds.Size);
		GeoPixelCoordinate centerPixel = Project(_viewportLatitude, _viewportLongitude, ZoomLevel);
		double left = centerPixel.X - bounds.Width / 2d;
		double top = centerPixel.Y - bounds.Height / 2d;
		int tileSize = 256;
		int minTileX = (int)Math.Floor(left / tileSize);
		int maxTileX = (int)Math.Floor((left + bounds.Width) / tileSize);
		int minTileY = (int)Math.Floor(top / tileSize);
		int maxTileY = (int)Math.Floor((top + bounds.Height) / tileSize);
		int mapSizeInTiles = 1 << ZoomLevel;
		HashSet<(int Zoom, int X, int Y)> requiredKeys = [];

		for (int tileX = minTileX; tileX <= maxTileX; tileX++)
		{
			for (int tileY = minTileY; tileY <= maxTileY; tileY++)
			{
				if (tileY < 0 || tileY >= mapSizeInTiles)
				{
					continue;
				}

				int wrappedTileX = Mod(tileX, mapSizeInTiles);
				requiredKeys.Add((ZoomLevel, wrappedTileX, tileY));
			}
		}

		foreach ((int zoom, int x, int y) in requiredKeys)
		{
			if (_visibleTiles.ContainsKey((zoom, x, y)))
			{
				continue;
			}

			_visibleTiles[(zoom, x, y)] = await _mapTileService.GetTileAsync(zoom, x, y, cancellationToken).ConfigureAwait(false);
			await Dispatcher.UIThread.InvokeAsync(InvalidateVisual);
		}

		foreach ((int Zoom, int X, int Y) key in _visibleTiles.Keys.Where(key => !requiredKeys.Contains(key)).ToArray())
		{
			_visibleTiles.Remove(key);
		}
	}

	private async Task LoadAlertsAsync(CancellationToken cancellationToken)
	{
		if (Math.Abs(Latitude - _lastAlertLatitude) < 0.05 && Math.Abs(Longitude - _lastAlertLongitude) < 0.05 && _alertPolygons.Count > 0)
		{
			return;
		}

		_alertPolygons = await _weatherAlertService.GetActiveAlertsAsync(new GeoCoordinate(Latitude, Longitude), cancellationToken).ConfigureAwait(false);
		_lastAlertLatitude = Latitude;
		_lastAlertLongitude = Longitude;
	}

	private void DrawLocationMarker(DrawingContext context, double left, double top)
	{
		GeoPixelCoordinate vehiclePixel = Project(Latitude, Longitude, ZoomLevel);
		Point center = new(vehiclePixel.X - left, vehiclePixel.Y - top);
		StreamGeometry geometry = new();
		using (StreamGeometryContext geometryContext = geometry.Open())
		{
			geometryContext.BeginFigure(new Point(0, -18), true);
			geometryContext.LineTo(new Point(10, 12));
			geometryContext.LineTo(new Point(0, 6));
			geometryContext.LineTo(new Point(-10, 12));
			geometryContext.EndFigure(true);
		}

		using (context.PushTransform(Matrix.CreateTranslation(center.X, center.Y) * Matrix.CreateRotation(HeadingDegrees * Math.PI / 180d)))
		{
			context.DrawGeometry(new SolidColorBrush(Color.Parse("#4DE1FF")), new Pen(new SolidColorBrush(Color.Parse("#FFFFFF")), 2), geometry);
		}
	}

	private void DrawStatusOverlay(DrawingContext context, Rect bounds)
	{
		if (string.IsNullOrWhiteSpace(_statusMessage) && !_isMapLoading)
		{
			return;
		}

		string text = _isMapLoading ? "Loading map tiles and alerts..." : _statusMessage ?? string.Empty;
		Rect overlayBounds = new(16, bounds.Height - 44, Math.Max(220, Math.Min(bounds.Width - 32, 360)), 28);
		context.FillRectangle(new SolidColorBrush(Color.Parse("#88000000")), overlayBounds, 6);

		FormattedText formattedText = new(
			text,
			CultureInfo.InvariantCulture,
			FlowDirection.LeftToRight,
			new Typeface("Segoe UI"),
			14,
			new SolidColorBrush(Color.Parse("#F8F8F8")));

		context.DrawText(formattedText, new Point(overlayBounds.X + 10, overlayBounds.Y + 6));
	}

	private void DrawAlertPolygons(DrawingContext context, Rect bounds, double left, double top)
	{
		foreach (WeatherAlertPolygon polygon in _alertPolygons)
		{
			if (polygon.Coordinates.Count < 3)
			{
				continue;
			}

			List<Point> points = [];
			foreach (GeoCoordinate coordinate in polygon.Coordinates)
			{
				GeoPixelCoordinate projected = Project(coordinate.Latitude, coordinate.Longitude, ZoomLevel);
				points.Add(new Point(projected.X - left, projected.Y - top));
			}

			StreamGeometry geometry = new();
			using (StreamGeometryContext geometryContext = geometry.Open())
			{
				geometryContext.BeginFigure(points[0], true);
				foreach (Point point in points.Skip(1))
				{
					geometryContext.LineTo(point);
				}

				geometryContext.EndFigure(true);
			}

			Color severityColor = polygon.Severity.ToUpperInvariant() switch
			{
				"EXTREME" => Color.Parse("#FF3B30"),
				"SEVERE" => Color.Parse("#FF9500"),
				"MODERATE" => Color.Parse("#FFD60A"),
				_ => Color.Parse("#4DE1FF"),
			};

			context.DrawGeometry(new SolidColorBrush(severityColor, 0.18), new Pen(new SolidColorBrush(severityColor), 2), geometry);
		}
	}

	private static GeoPixelCoordinate Project(double latitude, double longitude, int zoomLevel)
	{
		double clippedLatitude = Math.Clamp(latitude, -85.05112878d, 85.05112878d);
		double mapSize = 256d * (1 << zoomLevel);
		double x = (longitude + 180d) / 360d * mapSize;
		double latitudeRadians = clippedLatitude * Math.PI / 180d;
		double y = (1d - Math.Log(Math.Tan(latitudeRadians) + 1d / Math.Cos(latitudeRadians)) / Math.PI) / 2d * mapSize;
		return new GeoPixelCoordinate(x, y);
	}

	private static GeoCoordinate Unproject(double x, double y, int zoomLevel)
	{
		double mapSize = 256d * (1 << zoomLevel);
		double longitude = x / mapSize * 360d - 180d;
		double mercator = Math.PI - 2d * Math.PI * y / mapSize;
		double latitude = 180d / Math.PI * Math.Atan(Math.Sinh(mercator));
		return new GeoCoordinate(latitude, longitude);
	}

	private static int Mod(int value, int modulus)
	{
		int result = value % modulus;
		return result < 0 ? result + modulus : result;
	}

	private void EnsureViewportCenterInitialized()
	{
		if (!double.IsNaN(_viewportLatitude) && !double.IsNaN(_viewportLongitude))
		{
			return;
		}

		_viewportLatitude = Latitude;
		_viewportLongitude = Longitude;
	}

	private void PanViewport(Vector delta)
	{
		EnsureViewportCenterInitialized();
		GeoPixelCoordinate centerPixel = Project(_viewportLatitude, _viewportLongitude, ZoomLevel);
		GeoCoordinate newCenter = Unproject(centerPixel.X - delta.X, centerPixel.Y - delta.Y, ZoomLevel);
		_viewportLatitude = newCenter.Latitude;
		_viewportLongitude = newCenter.Longitude;
		OnViewportChanged();
	}

	private void OnRefreshTimerTick(object? sender, EventArgs e)
	{
		if (_disposed)
		{
			return;
		}

		_visibleTiles.Clear();
		_lastAlertLatitude = double.NaN;
		_lastAlertLongitude = double.NaN;
		_statusMessage = "Refreshing map data...";
		OnViewportChanged();
	}

	private void ReleasePointerCapture(IPointer pointer)
	{
		if (!_isPointerCaptured)
		{
			return;
		}

		_isPointerCaptured = false;
		pointer.Capture(null);
	}

	private readonly record struct GeoPixelCoordinate(double X, double Y);
}
