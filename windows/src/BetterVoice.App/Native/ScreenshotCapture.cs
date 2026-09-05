using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BetterVoice.Core;

namespace BetterVoice.App.Native;

public static class ScreenshotCapture
{
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const double CropPaddingRatio = 0.10;
    private const int MinimumCropPadding = 8;
    private const int MaximumCropPadding = 24;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    public static void Capture(
        CircleGesture gesture,
        string destinationPath,
        ScreenContextCaptureMode captureMode)
    {
        var targetPoint = new Point((int)Math.Round(gesture.Center.X), (int)Math.Round(gesture.Center.Y));
        var screen = Screen.FromPoint(targetPoint);
        var screenBounds = screen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        Rectangle captureBounds = GetCaptureBounds(gesture, screenBounds, captureMode);

        using var bitmap = CaptureScreenBitmap(
            captureBounds.X,
            captureBounds.Y,
            captureBounds.Width,
            captureBounds.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            float relX = (float)(gesture.Center.X - captureBounds.X);
            float relY = (float)(gesture.Center.Y - captureBounds.Y);
            float radiusX = (float)Math.Max(24.0, gesture.HalfWidth > 0 ? gesture.HalfWidth : gesture.Radius);
            float radiusY = (float)Math.Max(24.0, gesture.HalfHeight > 0 ? gesture.HalfHeight : gesture.Radius);

            g.SmoothingMode = SmoothingMode.AntiAlias;

            // A precise, restrained keyline keeps the target legible without
            // tinting the referenced content or adding a decorative halo.
            float strokeWidth = Math.Clamp(Math.Max(radiusX, radiusY) * 0.025f, 2.0f, 3.5f);
            using var pen = new Pen(Color.FromArgb(220, 111, 143, 184), strokeWidth);
            g.DrawEllipse(pen, relX - radiusX, relY - radiusY, radiusX * 2, radiusY * 2);

            if (captureMode == ScreenContextCaptureMode.CroppedSelection)
            {
                // The output itself carries a subtle crop boundary so the referenced
                // component remains obvious when the image is pasted into another app.
                using var cropPen = new Pen(Color.FromArgb(160, 111, 143, 184), 2f)
                {
                    Alignment = PenAlignment.Inset
                };
                g.DrawRectangle(cropPen, 1, 1, bitmap.Width - 2, bitmap.Height - 2);
            }
        }

        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        SaveBitmap(bitmap, destinationPath);
    }

    public static int GetCropPadding(double gestureRadius) =>
        Math.Clamp(
            (int)Math.Ceiling(Math.Max(24.0, gestureRadius) * CropPaddingRatio),
            MinimumCropPadding,
            MaximumCropPadding);

    public static Rectangle GetCaptureBounds(
        CircleGesture gesture,
        Rectangle screenBounds,
        ScreenContextCaptureMode captureMode) =>
        captureMode == ScreenContextCaptureMode.CroppedSelection
            ? GetCropBounds(gesture, screenBounds)
            : screenBounds;

    public static Rectangle GetCropBounds(CircleGesture gesture, Rectangle screenBounds)
    {
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(screenBounds), "Screen bounds must have a positive size.");
        }

        double radiusX = Math.Max(24.0, gesture.HalfWidth > 0 ? gesture.HalfWidth : gesture.Radius);
        double radiusY = Math.Max(24.0, gesture.HalfHeight > 0 ? gesture.HalfHeight : gesture.Radius);
        int padding = GetCropPadding(Math.Max(radiusX, radiusY));
        int left = (int)Math.Floor(gesture.Center.X - radiusX - padding);
        int top = (int)Math.Floor(gesture.Center.Y - radiusY - padding);
        int right = (int)Math.Ceiling(gesture.Center.X + radiusX + padding);
        int bottom = (int)Math.Ceiling(gesture.Center.Y + radiusY + padding);

        left = Math.Clamp(left, screenBounds.Left, screenBounds.Right - 1);
        top = Math.Clamp(top, screenBounds.Top, screenBounds.Bottom - 1);
        right = Math.Clamp(right, left + 1, screenBounds.Right);
        bottom = Math.Clamp(bottom, top + 1, screenBounds.Bottom);

        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static void SaveBitmap(Bitmap bitmap, string destinationPath)
    {
        string extension = Path.GetExtension(destinationPath);
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            ImageCodecInfo jpegCodec = ImageCodecInfo.GetImageEncoders()
                .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            using var encoderParameters = new EncoderParameters(1);
            encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, 88L);
            bitmap.Save(destinationPath, jpegCodec, encoderParameters);
            return;
        }

        bitmap.Save(destinationPath, ImageFormat.Png);
    }

    private static Bitmap CaptureScreenBitmap(int x, int y, int width, int height)
    {
        IntPtr hDesk = GetDC(IntPtr.Zero);
        if (hDesk != IntPtr.Zero)
        {
            try
            {
                IntPtr hMemDC = CreateCompatibleDC(hDesk);
                if (hMemDC != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr hBmp = CreateCompatibleBitmap(hDesk, width, height);
                        if (hBmp != IntPtr.Zero)
                        {
                            try
                            {
                                IntPtr hOld = SelectObject(hMemDC, hBmp);
                                bool success = BitBlt(hMemDC, 0, 0, width, height, hDesk, x, y, SRCCOPY | CAPTUREBLT);
                                SelectObject(hMemDC, hOld);

                                if (success)
                                {
                                    using var temp = Image.FromHbitmap(hBmp);
                                    return new Bitmap(temp);
                                }
                            }
                            finally
                            {
                                DeleteObject(hBmp);
                            }
                        }
                    }
                    finally
                    {
                        DeleteDC(hMemDC);
                    }
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hDesk);
            }
        }

        // Fallback placeholder bitmap if screen DC is unavailable in current session
        var fallback = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(fallback))
        {
            g.Clear(Color.FromArgb(30, 30, 35));
        }
        return fallback;
    }
}
