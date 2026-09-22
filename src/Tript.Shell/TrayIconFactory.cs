// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tript.App;

namespace Tript.Shell;

internal readonly record struct BadgeLayout(RectangleF Status, RectangleF Alert, float Outline);

internal readonly record struct BadgePaint(Color? Status, bool StatusFilled, Color? Alert);

internal static class TrayIconFactory
{
    private static readonly Color Plate = Color.FromArgb(0x10, 0x11, 0x16);
    private static readonly Color Recording = Color.FromArgb(0xF8, 0x71, 0x71);
    private static readonly Color Buffering = Color.FromArgb(0xA7, 0x9B, 0xE3);
    private static readonly Color Warning = Color.FromArgb(0xF0, 0xB4, 0x29);

    internal static BadgeLayout Layout(int size)
    {
        var badge = size * 0.5f;
        var outline = Math.Max(1f, size / 16f);
        return new BadgeLayout(
            new RectangleF(size - badge, size - badge, badge, badge),
            new RectangleF(0f, 0f, badge, badge),
            outline);
    }

    internal static BadgePaint Paint(TrayActivity activity, TrayAlert alert)
    {
        var status = activity switch
        {
            TrayActivity.Recording => (Color?)Recording,
            TrayActivity.Buffering => Buffering,
            _ => null,
        };

        var alertColour = alert switch
        {
            TrayAlert.Error => (Color?)Recording,
            TrayAlert.Warning => Warning,
            _ => null,
        };

        return new BadgePaint(status, activity == TrayActivity.Recording, alertColour);
    }

    // The returned HICON is owned by the caller and must be released with DestroyIcon.
    [SupportedOSPlatform("windows")]
    internal static IntPtr Create(string iconPath, int size, TrayActivity activity, TrayAlert alert)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using (var source = new Icon(iconPath, size, size))
            using (var art = source.ToBitmap())
                graphics.DrawImage(art, new Rectangle(0, 0, size, size));

            var layout = Layout(size);
            var paint = Paint(activity, alert);
            if (paint.Status is { } dot)
                DrawDot(graphics, layout.Status, layout.Outline, dot, paint.StatusFilled);
            if (paint.Alert is { } triangle)
                DrawTriangle(graphics, layout.Alert, layout.Outline, triangle);
        }

        return bitmap.GetHicon();
    }

    [SupportedOSPlatform("windows")]
    private static void DrawDot(Graphics graphics, RectangleF bounds, float outline, Color colour, bool filled)
    {
        // A dark disc under the badge keeps the ring's hole from filling with viewfinder artwork,
        // which is what tells a buffer-only run apart from a recording one at 16px.
        using (var plate = new SolidBrush(Plate))
            graphics.FillEllipse(plate, bounds);

        var inner = RectangleF.Inflate(bounds, -outline, -outline);
        using var brush = new SolidBrush(colour);
        if (filled)
        {
            graphics.FillEllipse(brush, inner);
            return;
        }

        using var ring = new Pen(colour, outline * 1.3f);
        graphics.DrawEllipse(ring, RectangleF.Inflate(inner, -outline * 0.65f, -outline * 0.65f));
    }

    [SupportedOSPlatform("windows")]
    private static void DrawTriangle(Graphics graphics, RectangleF bounds, float outline, Color colour)
    {
        var inner = RectangleF.Inflate(bounds, -outline, -outline);
        var points = new[]
        {
            new PointF(inner.Left + inner.Width / 2f, inner.Top),
            new PointF(inner.Right, inner.Bottom),
            new PointF(inner.Left, inner.Bottom),
        };

        using var plate = new Pen(Plate, outline * 2f) { LineJoin = LineJoin.Round };
        graphics.DrawPolygon(plate, points);
        using var brush = new SolidBrush(colour);
        graphics.FillPolygon(brush, points);
    }

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    internal const int SmCxSmIcon = 49;

    [SupportedOSPlatform("windows")]
    internal static int TrayIconSize()
    {
        var size = GetSystemMetrics(SmCxSmIcon);
        return size >= 8 ? size : 16;
    }
}
