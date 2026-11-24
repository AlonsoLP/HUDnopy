// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/Controls/ExobiologyRadarControl.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Heading-up 2D Exobiology sample radius radar rendered on a WPF Canvas.
// -----------------------------------------------------------------------------

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HUDnopy.Models;

namespace HUDnopy.UI.Controls;

/// <summary>
/// Lightweight 2D heading-up radar showing collected exobiology samples and their minimum
/// genetic diversity radius circles relative to the commander's live position.
/// </summary>
public sealed class ExobiologyRadarControl : Border
{
    private static readonly SolidColorBrush GridBrush = CreateFrozenBrush(45, 255, 255, 255);
    private static readonly SolidColorBrush ClearFillBrush = CreateFrozenBrush(24, 0, 230, 118);
    private static readonly SolidColorBrush DangerFillBrush = CreateFrozenBrush(36, 255, 46, 76);

    private readonly Canvas _canvas;
    private string _lastSignature = "";

    private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public ExobiologyRadarControl()
    {
        Width = 170;
        Height = 170;
        CornerRadius = new CornerRadius(85);
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 12, 18));
        BorderThickness = new Thickness(1.5);
        SetResourceReference(BorderBrushProperty, "HudBorderBrush");
        ClipToBounds = true;

        _canvas = new Canvas { Width = 170, Height = 170 };
        Child = _canvas;
    }

    public void UpdateRadar(ExobiologyTrackerState? tracker)
    {
        if (tracker is null || tracker.SamplesCollected <= 0)
        {
            if (_canvas.Children.Count > 0)
                _canvas.Children.Clear();
            _lastSignature = "";
            return;
        }

        var sigBuilder = new System.Text.StringBuilder(64);
        sigBuilder.Append(tracker.RequiredDistanceMeters.ToString("0")).Append('|')
                  .Append(tracker.SamplesCollected).Append('|')
                  .Append(tracker.PlayerHeadingDegrees.ToString("0")).Append('|');
        foreach (var pt in tracker.SamplePoints)
        {
            sigBuilder.Append(pt.Index).Append(':')
                      .Append(pt.RelativeX.ToString("0.0")).Append(',')
                      .Append(pt.RelativeY.ToString("0.0")).Append(',')
                      .Append(pt.DistanceMeters.ToString("0")).Append(';');
        }

        string sig = sigBuilder.ToString();
        if (string.Equals(sig, _lastSignature, StringComparison.Ordinal))
            return;
        _lastSignature = sig;

        _canvas.Children.Clear();

        const double cx = 85;
        const double cy = 85;
        double req = Math.Max(100, tracker.RequiredDistanceMeters);
        double maxDist = req * 1.65;
        foreach (var pt in tracker.SamplePoints)
            maxDist = Math.Max(maxDist, pt.DistanceMeters + req * 0.4);
        double pxPerMeter = 74.0 / maxDist;

        // Crosshair & reference range ring
        _canvas.Children.Add(new Line { X1 = cx, Y1 = 6, X2 = cx, Y2 = 164, Stroke = GridBrush, StrokeThickness = 1 });
        _canvas.Children.Add(new Line { X1 = 6, Y1 = cy, X2 = 164, Y2 = cy, Stroke = GridBrush, StrokeThickness = 1 });

        double refR = Math.Min(76, req * pxPerMeter);
        var refRing = new Ellipse
        {
            Width = refR * 2,
            Height = refR * 2,
            Stroke = GridBrush,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 }
        };
        Canvas.SetLeft(refRing, cx - refR);
        Canvas.SetTop(refRing, cy - refR);
        _canvas.Children.Add(refRing);

        var okBrush = TryFindResource("HudOkBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LimeGreen;
        var dangerBrush = TryFindResource("HudDangerBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.OrangeRed;
        var accentBrush = TryFindResource("HudAccentBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Orange;

        // Rotated compass N indicator (heading-up) matching Web HUD parity
        double headingRad = tracker.PlayerHeadingDegrees * Math.PI / 180.0;
        double nx = cx - Math.Sin(headingRad) * 74.0;
        double ny = cy - Math.Cos(headingRad) * 74.0;
        var northLbl = new TextBlock
        {
            Text = "N",
            Foreground = accentBrush,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 9.5,
            FontWeight = FontWeights.Bold
        };
        Canvas.SetLeft(northLbl, nx - 3.5);
        Canvas.SetTop(northLbl, ny - 6.0);
        _canvas.Children.Add(northLbl);

        // Sample points & minimum diversity exclusion circles
        foreach (var pt in tracker.SamplePoints)
        {
            double sx = cx + pt.RelativeX * pxPerMeter;
            double sy = cy - pt.RelativeY * pxPerMeter;
            bool clear = pt.DistanceMeters >= req;
            var ringBrush = clear ? okBrush : dangerBrush;

            double radPx = req * pxPerMeter;
            var circle = new Ellipse
            {
                Width = radPx * 2,
                Height = radPx * 2,
                Stroke = ringBrush,
                StrokeThickness = 1.4,
                Fill = clear ? ClearFillBrush : DangerFillBrush
            };
            Canvas.SetLeft(circle, sx - radPx);
            Canvas.SetTop(circle, sy - radPx);
            _canvas.Children.Add(circle);

            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = ringBrush,
                ToolTip = $"Sample #{pt.Index}: {pt.DistanceMeters:0} m / {req:0} m"
            };
            Canvas.SetLeft(dot, sx - 4);
            Canvas.SetTop(dot, sy - 4);
            _canvas.Children.Add(dot);

            var lbl = new TextBlock
            {
                Text = $"#{pt.Index}",
                Foreground = ringBrush,
                FontSize = 9.5,
                FontWeight = FontWeights.Bold
            };
            Canvas.SetLeft(lbl, sx + 5);
            Canvas.SetTop(lbl, sy - 7);
            _canvas.Children.Add(lbl);
        }

        // Player heading arrow at center (heading-up)
        var arrow = new Polygon
        {
            Points = new PointCollection { new(cx, cy - 8), new(cx - 5, cy + 6), new(cx, cy + 3), new(cx + 5, cy + 6) },
            Fill = accentBrush,
            Stroke = System.Windows.Media.Brushes.White,
            StrokeThickness = 0.8
        };
        _canvas.Children.Add(arrow);
    }
}
