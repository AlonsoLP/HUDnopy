// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/Controls/SectionNavigationPanel.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Reusable WPF navigation strip for switching active sub-sections.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Windows;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace HUDnopy;

/// <summary>
/// Reusable WPF navigation strip for switching active sub-sections.
/// </summary>
public sealed class SectionNavigationPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));

        var rows = GetRows(availableSize.Width);
        double width = 0;
        double height = 0;
        foreach (var row in rows)
        {
            width = Math.Max(width, row.Width);
            height += row.Height;
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double top = 0;
        foreach (var row in GetRows(finalSize.Width))
        {
            double extraWidth = Math.Max(0, finalSize.Width - row.Width) / row.Children.Count;
            double left = 0;
            foreach (UIElement child in row.Children)
            {
                double width = child.DesiredSize.Width + extraWidth;
                child.Arrange(new Rect(left, top, width, row.Height));
                left += width;
            }
            top += row.Height;
        }

        return finalSize;
    }

    private List<Row> GetRows(double width)
    {
        var rows = new List<Row>();
        Row? row = null;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            if (row == null || row.Width + child.DesiredSize.Width > width && row.Children.Count > 0)
            {
                row = new Row();
                rows.Add(row);
            }

            row.Children.Add(child);
            row.Width += child.DesiredSize.Width;
            row.Height = Math.Max(row.Height, child.DesiredSize.Height);
        }
        return rows;
    }

    private sealed class Row
    {
        public List<UIElement> Children { get; } = new();
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
