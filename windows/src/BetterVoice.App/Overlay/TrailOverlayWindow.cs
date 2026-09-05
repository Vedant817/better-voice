using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BetterVoice.App.Native;
using BetterVoice.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace BetterVoice.App.Overlay;

public sealed class TrailOverlayWindow : Window
{
    private const double TrailLifetimeSeconds = 0.9;
    private readonly List<PointD> _points = [];
    private readonly List<double> _times = [];
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private CircleGesture? _highlightGesture;
    private ScreenContextCaptureMode _highlightMode = ScreenContextCaptureMode.FullDisplayWithHighlight;
    private double _highlightUntil;

    private readonly Pen[] _fadingTrailPens;
    private readonly Pen _circlePen;
    private readonly Brush _circleFill;
    private readonly Pen _cropPen;
    private readonly Brush _cropFill;
    private readonly Brush _outsideDim;
    private readonly Brush _labelBackground;
    private readonly Brush _labelForeground;

    public TrailOverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;

        _fadingTrailPens = new Pen[32];
        for (int index = 0; index < _fadingTrailPens.Length; index++)
        {
            double opacity = (double)index / (_fadingTrailPens.Length - 1);
            byte alpha = (byte)Math.Round(175.0 * opacity);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 111, 143, 184));
            brush.Freeze();
            var pen = new Pen(brush, 2.25)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            _fadingTrailPens[index] = pen;
        }

        var circleStroke = new SolidColorBrush(Color.FromArgb(220, 111, 143, 184));
        circleStroke.Freeze();
        _circlePen = new Pen(circleStroke, 2.5);
        _circlePen.Freeze();

        var fillBrush = new SolidColorBrush(Color.FromArgb(10, 111, 143, 184));
        fillBrush.Freeze();
        _circleFill = fillBrush;

        var cropStroke = new SolidColorBrush(Color.FromArgb(180, 111, 143, 184));
        cropStroke.Freeze();
        _cropPen = new Pen(cropStroke, 1.5);
        _cropPen.Freeze();

        var cropFill = new SolidColorBrush(Color.FromArgb(5, 111, 143, 184));
        cropFill.Freeze();
        _cropFill = cropFill;

        var outsideDim = new SolidColorBrush(Color.FromArgb(64, 17, 19, 23));
        outsideDim.Freeze();
        _outsideDim = outsideDim;

        var labelBackground = new SolidColorBrush(Color.FromArgb(242, 27, 30, 36));
        labelBackground.Freeze();
        _labelBackground = labelBackground;

        var labelForeground = new SolidColorBrush(Color.FromRgb(242, 244, 247));
        labelForeground.Freeze();
        _labelForeground = labelForeground;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        IntPtr hwnd = helper.Handle;

        // WPF window bounds are device-independent pixels. Cursor and capture
        // coordinates stay in physical screen pixels and are converted with
        // PointFromScreen while rendering, keeping the crop preview aligned on
        // scaled displays.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // Set layered, transparent, click-through, non-activating window flags
        int exStyle = (int)Win32Api.GetWindowLongPtr(hwnd, Win32Api.GWL_EXSTYLE);
        exStyle |= Win32Api.WS_EX_LAYERED | Win32Api.WS_EX_TRANSPARENT | Win32Api.WS_EX_NOACTIVATE | Win32Api.WS_EX_TOOLWINDOW | Win32Api.WS_EX_TOPMOST;
        Win32Api.SetWindowLongPtr(hwnd, Win32Api.GWL_EXSTYLE, (IntPtr)exStyle);

        // Exclude this overlay window from any screen capture / screenshots!
        Win32Api.SetWindowDisplayAffinity(hwnd, Win32Api.WDA_EXCLUDEFROMCAPTURE);

        CompositionTarget.Rendering += OnRendering;
    }

    public void AddPoint(PointD point)
    {
        double now = _stopwatch.Elapsed.TotalSeconds;
        _points.Add(point);
        _times.Add(now);
    }

    public void HighlightCircle(CircleGesture gesture, ScreenContextCaptureMode captureMode)
    {
        _highlightGesture = gesture;
        _highlightMode = captureMode;
        _highlightUntil = _stopwatch.Elapsed.TotalSeconds + 1.1;
        InvalidateVisual();
    }

    public void ClearTrail()
    {
        _points.Clear();
        _times.Clear();
        _highlightGesture = null;
        InvalidateVisual();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = _stopwatch.Elapsed.TotalSeconds;

        // Keep enough history visible for the user to see the shape they drew.
        double cutoff = now - TrailLifetimeSeconds;
        int removeCount = 0;
        for (int i = 0; i < _times.Count; i++)
        {
            if (_times[i] < cutoff) removeCount++;
            else break;
        }

        if (removeCount > 0)
        {
            _points.RemoveRange(0, removeCount);
            _times.RemoveRange(0, removeCount);
        }

        if (_highlightGesture != null && now > _highlightUntil)
        {
            _highlightGesture = null;
        }

        if (_points.Count > 0 || _highlightGesture != null)
        {
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double now = _stopwatch.Elapsed.TotalSeconds;

        // Draw gesture circle if active
        if (_highlightGesture is { } gesture)
        {
            var center = PointFromScreen(new Point(gesture.Center.X, gesture.Center.Y));
            double radiusX = Math.Max(24.0, gesture.HalfWidth > 0 ? gesture.HalfWidth : gesture.Radius);
            double radiusY = Math.Max(24.0, gesture.HalfHeight > 0 ? gesture.HalfHeight : gesture.Radius);
            var horizontalEdge = PointFromScreen(new Point(gesture.Center.X + radiusX, gesture.Center.Y));
            var verticalEdge = PointFromScreen(new Point(gesture.Center.X, gesture.Center.Y + radiusY));
            double displayRadiusX = Math.Abs(horizontalEdge.X - center.X);
            double displayRadiusY = Math.Abs(verticalEdge.Y - center.Y);
            var targetPoint = new System.Drawing.Point(
                (int)Math.Round(gesture.Center.X),
                (int)Math.Round(gesture.Center.Y));
            var screenBounds = System.Windows.Forms.Screen.FromPoint(targetPoint).Bounds;
            var physicalCapture = ScreenshotCapture.GetCaptureBounds(gesture, screenBounds, _highlightMode);
            Point captureTopLeft = PointFromScreen(new Point(physicalCapture.Left, physicalCapture.Top));
            Point captureBottomRight = PointFromScreen(new Point(physicalCapture.Right, physicalCapture.Bottom));
            var captureRect = new Rect(captureTopLeft, captureBottomRight);

            var desktopGeometry = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            double cornerRadius = _highlightMode == ScreenContextCaptureMode.CroppedSelection ? 12 : 0;
            var captureGeometry = new RectangleGeometry(captureRect, cornerRadius, cornerRadius);
            var dimGeometry = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                desktopGeometry,
                captureGeometry);
            dc.DrawGeometry(_outsideDim, null, dimGeometry);
            dc.DrawRoundedRectangle(
                _highlightMode == ScreenContextCaptureMode.CroppedSelection ? _cropFill : null,
                _cropPen,
                captureRect,
                cornerRadius,
                cornerRadius);
            dc.DrawEllipse(_circleFill, _circlePen, center, displayRadiusX, displayRadiusY);

            string labelText = _highlightMode == ScreenContextCaptureMode.CroppedSelection
                ? $"Crop · {physicalCapture.Width} × {physicalCapture.Height}"
                : "Full display · target marked";
            var label = new FormattedText(
                labelText,
                CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(
                    new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
                    FontStyles.Normal,
                    FontWeights.SemiBold,
                    FontStretches.Normal),
                11,
                _labelForeground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double labelLeft = Math.Clamp(
                captureRect.Left,
                8,
                Math.Max(8, ActualWidth - label.Width - 28));
            var labelRect = new Rect(
                labelLeft,
                Math.Max(8, captureRect.Top + (_highlightMode == ScreenContextCaptureMode.CroppedSelection ? -label.Height - 14 : 12)),
                label.Width + 20,
                label.Height + 10);
            dc.DrawRoundedRectangle(_labelBackground, _cropPen, labelRect, 5, 5);
            dc.DrawText(label, new Point(labelRect.Left + 10, labelRect.Top + 5));
        }

        if (_points.Count < 2) return;

        var segments = TrailSegments.Calculate(_points, _times);
        foreach (var seg in segments)
        {
            var p1 = PointFromScreen(new Point(_points[seg.From].X, _points[seg.From].Y));
            var p2 = PointFromScreen(new Point(_points[seg.To].X, _points[seg.To].Y));

            // Compute fading alpha based on age
            double age = now - _times[seg.To];
            double alphaRatio = Math.Max(0, 1.0 - (age / TrailLifetimeSeconds));
            int alphaIndex = Math.Clamp(
                (int)Math.Round(alphaRatio * (_fadingTrailPens.Length - 1)),
                0,
                _fadingTrailPens.Length - 1);
            dc.DrawLine(_fadingTrailPens[alphaIndex], p1, p2);
        }
    }
}
