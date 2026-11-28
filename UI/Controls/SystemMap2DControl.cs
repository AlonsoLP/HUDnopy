// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/Controls/SystemMap2DControl.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Interactive 2D schematic orbital System Map rendered on a WPF Canvas
//              with semantic value coloring, barycentre tree bus, asteroid belts,
//              binary moon brackets, structural station icons, and quick inspection cards.
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
            MaxHeight = 280
        };
        root.Children.Add(_scroll);

        Child = root;
    }

    public void UpdateMap(IReadOnlyList<ExplorationBodyItem> bodies, string? systemName)
    {
        if (bodies is null || bodies.Count == 0)
        {
            _canvas.Children.Clear();
            _lastSignature = "";
            return;
        }

        string sig = $"{systemName}|{bodies.Count}|{string.Join(",", System.Linq.Enumerable.Select(System.Linq.Enumerable.Take(bodies, 60), static b => $"{b.Id}:{b.WasMapped}:{b.BiologicalSignalCount}:{b.DataSource}:{b.Stations.Count}:{b.Rings.Count}"))}";
        if (string.Equals(sig, _lastSignature, StringComparison.Ordinal))
            return;
        _lastSignature = sig;

        var layout = SystemMapLayoutBuilder.Build(bodies, systemName);
        var nodes = layout.Nodes;
        _canvas.Children.Clear();
        _canvas.Width = layout.CanvasWidth;
        _canvas.Height = layout.CanvasHeight;

        var spineBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(85, 255, 255, 255));
        var leftTreeBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(145, 226, 232, 240));
        var bracketBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(145, 255, 195, 75));

        // 1. Left-hand orthogonal stellar barycentre tree bus
        foreach (var sl in layout.SafeSpineLines)
        {
            _canvas.Children.Add(new Line
            {
                X1 = sl.X1,
                Y1 = sl.Y1,
                X2 = sl.X2,
                Y2 = sl.Y2,
                Stroke = leftTreeBrush,
                StrokeThickness = 1.4,
                IsHitTestVisible = false
            });
        }

        // 2. Connect main horizontal bodies per band, and primary moons edge-to-edge along each vertical moon column
        var lastHorizByBand = new Dictionary<int, int>();
        var lastMoonByParent = new Dictionary<int, int>();

        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (n.IsRootStar || n.IsBarycentre)
            {
                lastHorizByBand[n.StarBandIndex] = i;
            }
            else if (n.IndentLevel == 0)
            {
                if (lastHorizByBand.TryGetValue(n.StarBandIndex, out int prevIdx))
                {
                    var prev = nodes[prevIdx];
                    double prevPad = prev.IsStar ? 4 : (prev.IsAsteroidBelt ? -3 : 0);
                    double curPad = n.IsStar ? 4 : (n.IsAsteroidBelt ? -3 : 0);
                    _canvas.Children.Add(new Line
                    {
                        X1 = prev.X + prev.RadiusPx + prevPad,
                        Y1 = prev.Y,
                        X2 = n.X - n.RadiusPx - curPad,
                        Y2 = n.Y,
                        Stroke = spineBrush,
                        StrokeThickness = 1.2
                    });
                }
                lastHorizByBand[n.StarBandIndex] = i;
                lastMoonByParent[i] = i;
            }
            else if (n.ParentIndex >= 0 && n.ParentIndex < nodes.Count)
            {
                var parent = nodes[n.ParentIndex];
                if (Math.Abs(n.X - parent.X) < 1.0)
                {
                    // Edge-to-edge vertical moon connector from the previous body in this column
                    // so the line never passes through the center/fill of intermediate moons!
                    int prevInColIdx = lastMoonByParent.GetValueOrDefault(n.ParentIndex, n.ParentIndex);
                    var prevInCol = nodes[prevInColIdx];
                    _canvas.Children.Add(new Line
                    {
                        X1 = prevInCol.X,
                        Y1 = prevInCol.Y + prevInCol.RadiusPx,
                        X2 = n.X,
                        Y2 = n.Y - n.RadiusPx,
                        Stroke = spineBrush,
                        StrokeThickness = 1.1
                    });
                    lastMoonByParent[n.ParentIndex] = i;
                }
                else if (Math.Abs(n.Y - parent.Y) < 1.0)
                {
                    // Straight horizontal sub-moon connector to the right of its parent moon (same Y)
                    _canvas.Children.Add(new Line
                    {
                        X1 = parent.X + parent.RadiusPx,
                        Y1 = parent.Y,
                        X2 = n.X - n.RadiusPx,
                        Y2 = n.Y,
                        Stroke = spineBrush,
                        StrokeThickness = 1.1
                    });
                    lastMoonByParent[n.ParentIndex] = i;
                }
                else
                {
                    // Additional sibling sub-moons (cIdx > 0) hanging vertically below the first sub-moon
                    int prevSubIdx = lastMoonByParent.GetValueOrDefault(n.ParentIndex, n.ParentIndex);
                    var prevSub = nodes[prevSubIdx];
                    _canvas.Children.Add(new Line
                    {
                        X1 = n.X,
                        Y1 = prevSub.Y + prevSub.RadiusPx,
                        X2 = n.X,
                        Y2 = n.Y - n.RadiusPx,
                        Stroke = spineBrush,
                        StrokeThickness = 1.1
                    });
                    lastMoonByParent[n.ParentIndex] = i;
                }
            }
        }

        // 3. Binary pair top brackets (┌───┐)
        foreach (var br in layout.Brackets)
        {
            _canvas.Children.Add(new Line { X1 = br.X1, Y1 = br.AnchorY1, X2 = br.X1, Y2 = br.TopY, Stroke = bracketBrush, StrokeThickness = 1.3 });
            _canvas.Children.Add(new Line { X1 = br.X1, Y1 = br.TopY, X2 = br.X2, Y2 = br.TopY, Stroke = bracketBrush, StrokeThickness = 1.3 });
            _canvas.Children.Add(new Line { X1 = br.X2, Y1 = br.TopY, X2 = br.X2, Y2 = br.AnchorY2, Stroke = bracketBrush, StrokeThickness = 1.3 });
        }

        // 3b. Binary moon left-side vertical brackets
        foreach (var mb in layout.SafeMoonBrackets)
        {
            _canvas.Children.Add(new Line { X1 = mb.AnchorX1, Y1 = mb.Y1, X2 = mb.LeftX, Y2 = mb.Y1, Stroke = bracketBrush, StrokeThickness = 1.25 });
            _canvas.Children.Add(new Line { X1 = mb.LeftX, Y1 = mb.Y1, X2 = mb.LeftX, Y2 = mb.Y2, Stroke = bracketBrush, StrokeThickness = 1.25 });
            _canvas.Children.Add(new Line { X1 = mb.LeftX, Y1 = mb.Y2, X2 = mb.AnchorX2, Y2 = mb.Y2, Stroke = bracketBrush, StrokeThickness = 1.25 });
        }

        // 4. Draw nodes (Barycentres ×, Asteroid Belts, Stars, Planets, Moons, Sub-moons, and Station icons)
        foreach (var n in nodes)
        {
            if (n.IsBarycentre)
            {
                DrawBarycentreNode(n);
                continue;
            }

            if (n.IsAsteroidBelt)
            {
                DrawAsteroidBeltNode(n);
                continue;
            }

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

            string tipText = $"{n.Body.BodyName} ({n.Body.BodyType}) [{n.Body.DataSource}]";
            if (!string.IsNullOrWhiteSpace(n.StationsSummary))
                tipText += $"\n⬡ {n.StationsSummary}";

            var orb = new Ellipse
            {
                Width = n.RadiusPx * 2,
                Height = n.RadiusPx * 2,
                Fill = orbFill,
                Stroke = stroke,
                StrokeThickness = n.IsStar ? 1.0 : n.IsHighGravity ? 2.4 : 1.3,
                Opacity = opacity,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = tipText
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

            if (n.StationCount > 0)
            {
                DrawStationBadge(n, tipText);
            }
        }
    }

    private void DrawBarycentreNode(SystemMapNode n)
    {
        var crossBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(225, 241, 245, 249));
        const double arm = 5.5;
        _canvas.Children.Add(new Line
        {
            X1 = n.X - arm,
            Y1 = n.Y - arm,
            X2 = n.X + arm,
            Y2 = n.Y + arm,
            Stroke = crossBrush,
            StrokeThickness = 1.8,
            ToolTip = $"{n.Body.BodyName}"
        });
        _canvas.Children.Add(new Line
        {
            X1 = n.X - arm,
            Y1 = n.Y + arm,
            X2 = n.X + arm,
            Y2 = n.Y - arm,
            Stroke = crossBrush,
            StrokeThickness = 1.8,
            ToolTip = $"{n.Body.BodyName}"
        });
    }

    private void DrawAsteroidBeltNode(SystemMapNode n)
    {
        var rockBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(215, 203, 213, 225));
        var dimRockBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 148, 163, 184));
        (double dx, double dy, double r, bool bright)[] rocks =
        [
            (-2.0, -13.0, 1.8, false),
            ( 1.2,  -9.0, 2.3, true),
            (-1.5,  -5.0, 1.9, true),
            ( 2.2,  -1.5, 2.6, true),
            (-1.0,   2.5, 2.2, true),
            ( 1.8,   6.5, 2.4, true),
            (-1.4,  10.0, 1.9, false),
            ( 0.8,  13.2, 1.6, false)
        ];

        foreach (var (dx, dy, r, bright) in rocks)
        {
            var dot = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Fill = bright ? rockBrush : dimRockBrush,
                ToolTip = $"{n.Body.BodyName} ({n.Body.BodyType})"
            };
            Canvas.SetLeft(dot, n.X + dx - r);
            Canvas.SetTop(dot, n.Y + dy - r);
            _canvas.Children.Add(dot);
        }
    }

    private void DrawStationBadge(SystemMapNode n, string tipText)
    {
        // Elite Dangerous-style structural orbital station / planetary settlement hexagon badge (amber/gold)
        double bx = n.X + n.RadiusPx * 0.78 + 4.5;
        double by = n.Y - n.RadiusPx * 0.78 - 3.5;
        const double r = 5.4;

        var hexPoints = new PointCollection(6);
        for (int i = 0; i < 6; i++)
        {
            double angle = (i * 60 - 30) * Math.PI / 180.0;
            hexPoints.Add(new System.Windows.Point(bx + r * Math.Cos(angle), by + r * Math.Sin(angle)));
        }

        var hex = new Polygon
        {
            Points = hexPoints,
            Fill = ParseBrush("#0F172A"),
            Stroke = ParseBrush("#FBBF24"),
            StrokeThickness = 1.4,
            ToolTip = tipText,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var captured = n.Body;
        hex.MouseLeftButtonUp += (_, _) => ShowBodyDetail(captured);
        _canvas.Children.Add(hex);

        var coreDot = new Ellipse
        {
            Width = 3.6,
            Height = 3.6,
            Fill = ParseBrush("#FEF08A"),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(coreDot, bx - 1.8);
        Canvas.SetTop(coreDot, by - 1.8);
        _canvas.Children.Add(coreDot);
    }

    private void ShowBodyDetail(ExplorationBodyItem b)
    {
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
