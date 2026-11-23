// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/Controls/SystemMap2DControl.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Interactive 2D schematic orbital System Map rendered on a WPF Canvas
//              with semantic value coloring and quick body inspection cards.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HUDnopy.Models;
using HUDnopy.Services;

namespace HUDnopy.UI.Controls;

/// <summary>
/// Interactive 2D orbital schematic of the current system's unified ExplorationBodies.
/// Clicking any body raises BodySelected and displays its quick summary card.
/// </summary>
public sealed class SystemMap2DControl : Border
{
    private readonly ScrollViewer _scroll;
    private readonly Canvas _canvas;
    private readonly Border _detailCard;
    private readonly TextBlock _detailTitle;
    private readonly TextBlock _detailMetrics;
    private string _lastSignature = "";

    public event Action<ExplorationBodyItem>? BodySelected;

    public SystemMap2DControl()
    {
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 11, 16));
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "HudBorderBrush");
        Padding = new Thickness(8);

        var root = new StackPanel();
        _canvas = new Canvas { Height = 150, Background = System.Windows.Media.Brushes.Transparent };
        _scroll = new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 250
        };
        root.Children.Add(_scroll);

        _detailTitle = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _detailTitle.SetResourceReference(TextBlock.ForegroundProperty, "HudAccentBrush");
        _detailMetrics = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        _detailMetrics.SetResourceReference(TextBlock.ForegroundProperty, "HudTextBrush");

        var detailStack = new StackPanel();
        detailStack.Children.Add(_detailTitle);
        detailStack.Children.Add(_detailMetrics);

        _detailCard = new Border
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(10, 6, 10, 6),
            BorderThickness = new Thickness(1),
            Child = detailStack
        };
        _detailCard.SetResourceReference(BackgroundProperty, "HudPanelAltBrush");
        _detailCard.SetResourceReference(BorderBrushProperty, "HudBorderBrush");
        root.Children.Add(_detailCard);

        Child = root;
    }

    public void UpdateMap(IReadOnlyList<ExplorationBodyItem> bodies, string? systemName)
    {
        if (bodies is null || bodies.Count == 0)
        {
            _canvas.Children.Clear();
            _detailCard.Visibility = Visibility.Collapsed;
            _lastSignature = "";
            return;
        }

        string sig = $"{systemName}|{bodies.Count}|{string.Join(",", System.Linq.Enumerable.Select(System.Linq.Enumerable.Take(bodies, 60), static b => $"{b.Id}:{b.WasMapped}:{b.BiologicalSignalCount}:{b.DataSource}"))}";
        if (string.Equals(sig, _lastSignature, StringComparison.Ordinal))
            return;
        _lastSignature = sig;

        var layout = SystemMapLayoutBuilder.Build(bodies, systemName);
        var nodes = layout.Nodes;
        _canvas.Children.Clear();
        _canvas.Width = layout.CanvasWidth;
        _canvas.Height = layout.CanvasHeight;

        // Connect root stars vertically on the left, main horizontal bodies per star band,
        // direct moons vertically below their parent, and sub-moons via L-shaped connectors
        var spineBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(75, 255, 255, 255));
        var bracketBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(145, 255, 195, 75));
        int prevRootStarIdx = -1;
        var lastHorizByBand = new Dictionary<int, int>();

        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (n.IsRootStar)
            {
                if (prevRootStarIdx >= 0)
                {
                    var prevStar = nodes[prevRootStarIdx];
                    _canvas.Children.Add(new Line
                    {
                        X1 = prevStar.X, Y1 = prevStar.Y + prevStar.RadiusPx + 4,
                        X2 = n.X, Y2 = n.Y - n.RadiusPx - 4,
                        Stroke = spineBrush, StrokeThickness = 1.4,
                        StrokeDashArray = new DoubleCollection { 3, 3 }
                    });
                }
                prevRootStarIdx = i;
                lastHorizByBand[n.StarBandIndex] = i;
            }
            else if (n.IndentLevel == 0)
            {
                // Planet or orbiting brown dwarf on the main horizontal axis
                if (lastHorizByBand.TryGetValue(n.StarBandIndex, out int prevIdx))
                {
                    var prev = nodes[prevIdx];
                    _canvas.Children.Add(new Line
                    {
                        X1 = prev.X + prev.RadiusPx + (prev.IsStar ? 4 : 0), Y1 = prev.Y,
                        X2 = n.X - n.RadiusPx - (n.IsStar ? 4 : 0), Y2 = n.Y,
                        Stroke = spineBrush, StrokeThickness = 1.2
                    });
                }
                lastHorizByBand[n.StarBandIndex] = i;
            }
            else if (n.ParentIndex >= 0)
            {
                var parent = nodes[n.ParentIndex];
                if (Math.Abs(n.X - parent.X) < 1.0)
                {
                    // Vertical moon connector
                    _canvas.Children.Add(new Line
                    {
                        X1 = parent.X, Y1 = parent.Y + parent.RadiusPx,
                        X2 = n.X, Y2 = n.Y - n.RadiusPx,
                        Stroke = spineBrush, StrokeThickness = 1.1
                    });
                }
                else
                {
                    // L-shaped sub-moon connector (down from parent, then right to sub-moon)
                    _canvas.Children.Add(new Line
                    {
                        X1 = parent.X, Y1 = parent.Y + parent.RadiusPx,
                        X2 = parent.X, Y2 = n.Y,
                        Stroke = spineBrush, StrokeThickness = 1.1
                    });
                    _canvas.Children.Add(new Line
                    {
                        X1 = parent.X, Y1 = n.Y,
                        X2 = n.X - n.RadiusPx, Y2 = n.Y,
                        Stroke = spineBrush, StrokeThickness = 1.1
                    });
                }
            }
        }

        // Binary pair top brackets (┌───┐)
        foreach (var br in layout.Brackets)
        {
            _canvas.Children.Add(new Line { X1 = br.X1, Y1 = br.AnchorY1, X2 = br.X1, Y2 = br.TopY, Stroke = bracketBrush, StrokeThickness = 1.3 });
            _canvas.Children.Add(new Line { X1 = br.X1, Y1 = br.TopY, X2 = br.X2, Y2 = br.TopY, Stroke = bracketBrush, StrokeThickness = 1.3 });
            _canvas.Children.Add(new Line { X1 = br.X2, Y1 = br.TopY, X2 = br.X2, Y2 = br.AnchorY2, Stroke = bracketBrush, StrokeThickness = 1.3 });
        }

        // Draw body nodes
        foreach (var n in nodes)
        {
            var fill = ParseBrush(n.FillHex);
            var stroke = ParseBrush(n.StrokeHex);
            double opacity = n.IsSpanshOnly ? 0.58 : 1.0;

            if (n.IsStar)
            {
                // Soft stellar corona / radial glow behind the star disc
                var glowColor = fill.Color;
                var coronaBrush = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(System.Windows.Media.Color.FromArgb(145, glowColor.R, glowColor.G, glowColor.B), 0.35),
                        new GradientStop(System.Windows.Media.Color.FromArgb(45, glowColor.R, glowColor.G, glowColor.B), 0.72),
                        new GradientStop(System.Windows.Media.Color.FromArgb(0, glowColor.R, glowColor.G, glowColor.B), 1.0)
                    }
                };
                double glowR = n.RadiusPx * 1.48;
                var corona = new Ellipse
                {
                    Width = glowR * 2,
                    Height = glowR * 2,
                    Fill = coronaBrush,
                    Opacity = opacity,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(corona, n.X - glowR);
                Canvas.SetTop(corona, n.Y - glowR);
                _canvas.Children.Add(corona);
            }

            if (!string.IsNullOrEmpty(n.AtmosphereHaloHex))
            {
                var atmColor = ParseBrush(n.AtmosphereHaloHex).Color;
                var atmHalo = new Ellipse
                {
                    Width = (n.RadiusPx + 3.2) * 2,
                    Height = (n.RadiusPx + 3.2) * 2,
                    Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(140, atmColor.R, atmColor.G, atmColor.B)),
                    StrokeThickness = 2.0,
                    Opacity = opacity,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(atmHalo, n.X - n.RadiusPx - 3.2);
                Canvas.SetTop(atmHalo, n.Y - n.RadiusPx - 3.2);
                _canvas.Children.Add(atmHalo);
            }

            if (n.HasBioHalo)
            {
                var halo = new Ellipse
                {
                    Width = (n.RadiusPx + 5.5) * 2,
                    Height = (n.RadiusPx + 5.5) * 2,
                    Stroke = ParseBrush("#00E676"),
                    StrokeThickness = 1.2,
                    StrokeDashArray = new DoubleCollection { 2, 2 },
                    Opacity = 0.85,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(halo, n.X - n.RadiusPx - 5.5);
                Canvas.SetTop(halo, n.Y - n.RadiusPx - 5.5);
                _canvas.Children.Add(halo);
            }

            System.Windows.Media.Brush orbFill = fill;
            if (n.IsStar && !n.FillHex.Equals("#0B0E17", StringComparison.OrdinalIgnoreCase))
            {
                var c = fill.Color;
                var coreColor = n.Body.StarType.ToUpperInvariant() is "L" or "T" or "Y"
                    ? System.Windows.Media.Color.FromRgb((byte)Math.Min(255, c.R + 70), (byte)Math.Min(255, c.G + 45), (byte)Math.Min(255, c.B + 70))
                    : Colors.White;
                orbFill = new RadialGradientBrush
                {
                    GradientOrigin = new System.Windows.Point(0.38, 0.38),
                    GradientStops =
                    {
                        new GradientStop(coreColor, 0.0),
                        new GradientStop(c, 0.52),
                        new GradientStop(System.Windows.Media.Color.FromRgb((byte)(c.R * 0.65), (byte)(c.G * 0.65), (byte)(c.B * 0.65)), 1.0)
                    }
                };
            }
            else if (n.IsEarthLike)
            {
                orbFill = new LinearGradientBrush
                {
                    StartPoint = new System.Windows.Point(0.1, 0.1),
                    EndPoint = new System.Windows.Point(0.9, 0.9),
                    GradientStops =
                    {
                        new GradientStop(System.Windows.Media.Color.FromRgb(235, 245, 255), 0.0),
                        new GradientStop(System.Windows.Media.Color.FromRgb(30, 107, 214), 0.25),
                        new GradientStop(System.Windows.Media.Color.FromRgb(56, 142, 60), 0.58),
                        new GradientStop(System.Windows.Media.Color.FromRgb(24, 90, 180), 0.82),
                        new GradientStop(System.Windows.Media.Color.FromRgb(120, 105, 72), 1.0)
                    }
                };
            }

            var orb = new Ellipse
            {
                Width = n.RadiusPx * 2,
                Height = n.RadiusPx * 2,
                Fill = orbFill,
                Stroke = stroke,
                StrokeThickness = n.IsStar ? 1.0 : n.IsHighGravity ? 2.4 : 1.3,
                Opacity = opacity,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"{n.Body.BodyName} ({n.Body.BodyType}) [{n.Body.DataSource}]"
            };
            var captured = n.Body;
            orb.MouseLeftButtonUp += (_, _) => ShowBodyDetail(captured);
            Canvas.SetLeft(orb, n.X - n.RadiusPx);
            Canvas.SetTop(orb, n.Y - n.RadiusPx);
            _canvas.Children.Add(orb);

            if (n.HasRings)
            {
                // Draw tilted planetary/brown-dwarf ring on top with inner + outer dust bands so it stands out clearly
                var outerRing = new Ellipse
                {
                    Width = n.RadiusPx * 3.3,
                    Height = n.RadiusPx * 0.95,
                    Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(230, 238, 222, 184)),
                    StrokeThickness = n.IsStar ? 2.6 : 2.1,
                    Opacity = opacity,
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(-18),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(outerRing, n.X - outerRing.Width / 2);
                Canvas.SetTop(outerRing, n.Y - outerRing.Height / 2);
                _canvas.Children.Add(outerRing);

                var innerRing = new Ellipse
                {
                    Width = n.RadiusPx * 2.65,
                    Height = n.RadiusPx * 0.72,
                    Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(145, 200, 182, 145)),
                    StrokeThickness = 1.2,
                    Opacity = opacity,
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(-18),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(innerRing, n.X - innerRing.Width / 2);
                Canvas.SetTop(innerRing, n.Y - innerRing.Height / 2);
                _canvas.Children.Add(innerRing);
            }
        }
    }

    private void ShowBodyDetail(ExplorationBodyItem b)
    {
        _detailCard.Visibility = Visibility.Collapsed;
        BodySelected?.Invoke(b);
    }

    private static SolidColorBrush ParseBrush(string hex)
    {
        try
        {
            return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            return new SolidColorBrush(Colors.Gray);
        }
    }
}
