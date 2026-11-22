// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/Controls/InputConstraint.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Attached WPF input constraint behavior for numeric, HEX color, and URL TextBoxes.
// -----------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HUDnopy;

/// <summary>
/// Supported input validation modes for <see cref="InputConstraint"/>.
/// </summary>
public enum InputConstraintMode
{
    None,
    Int,
    UDecimal,
    Decimal,
    HexColor,
    Url,
    NoWhitespace
}

/// <summary>
/// Attached WPF behavior that restricts <see cref="System.Windows.Controls.TextBox"/> input
/// to its expected data format on typing, paste, and focus loss.
/// </summary>
public static class InputConstraint
{
    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.RegisterAttached(
            "Mode",
            typeof(InputConstraintMode),
            typeof(InputConstraint),
            new PropertyMetadata(InputConstraintMode.None, OnModeChanged));

    public static readonly DependencyProperty MinProperty =
        DependencyProperty.RegisterAttached(
            "Min",
            typeof(double),
            typeof(InputConstraint),
            new PropertyMetadata(double.NaN));

    public static readonly DependencyProperty MaxProperty =
        DependencyProperty.RegisterAttached(
            "Max",
            typeof(double),
            typeof(InputConstraint),
            new PropertyMetadata(double.NaN));

    public static readonly DependencyProperty AllowEmptyProperty =
        DependencyProperty.RegisterAttached(
            "AllowEmpty",
            typeof(bool),
            typeof(InputConstraint),
            new PropertyMetadata(false));

    public static InputConstraintMode GetMode(DependencyObject obj) => (InputConstraintMode)obj.GetValue(ModeProperty);
    public static void SetMode(DependencyObject obj, InputConstraintMode value) => obj.SetValue(ModeProperty, value);

    public static double GetMin(DependencyObject obj) => (double)obj.GetValue(MinProperty);
    public static void SetMin(DependencyObject obj, double value) => obj.SetValue(MinProperty, value);

    public static double GetMax(DependencyObject obj) => (double)obj.GetValue(MaxProperty);
    public static void SetMax(DependencyObject obj, double value) => obj.SetValue(MaxProperty, value);

    public static bool GetAllowEmpty(DependencyObject obj) => (bool)obj.GetValue(AllowEmptyProperty);
    public static void SetAllowEmpty(DependencyObject obj, bool value) => obj.SetValue(AllowEmptyProperty, value);

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.TextBox tb)
            return;

        tb.PreviewTextInput -= OnPreviewTextInput;
        tb.PreviewKeyDown -= OnPreviewKeyDown;
        tb.PreviewDrop -= OnPreviewDrop;
        tb.TextChanged -= OnTextChanged;
        tb.LostFocus -= OnLostFocus;
        System.Windows.DataObject.RemovePastingHandler(tb, OnPasting);

        var mode = (InputConstraintMode)e.NewValue;
        if (mode == InputConstraintMode.None)
            return;

        if (mode is not (InputConstraintMode.Url or InputConstraintMode.NoWhitespace))
            InputMethod.SetIsInputMethodEnabled(tb, false);

        if (mode == InputConstraintMode.HexColor && tb.MaxLength == 0)
            tb.MaxLength = 7;

        tb.PreviewTextInput += OnPreviewTextInput;
        tb.PreviewKeyDown += OnPreviewKeyDown;
        tb.PreviewDrop += OnPreviewDrop;
        tb.TextChanged += OnTextChanged;
        tb.LostFocus += OnLostFocus;
        System.Windows.DataObject.AddPastingHandler(tb, OnPasting);
    }

    private static bool _sanitizing;

    private static void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Space)
            e.Handled = true;
    }

    private static void OnPreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb)
            return;

        if (!e.Data.GetDataPresent(System.Windows.DataFormats.Text))
        {
            e.Handled = true;
            return;
        }

        string dropped = (e.Data.GetData(System.Windows.DataFormats.Text) as string ?? "").Trim();
        string candidate = BuildCandidateText(tb, dropped);
        if (!IsAllowedCandidate(candidate, GetMode(tb)))
            e.Handled = true;
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sanitizing || sender is not System.Windows.Controls.TextBox tb)
            return;

        var mode = GetMode(tb);
        string text = tb.Text ?? "";
        if (IsAllowedCandidate(text, mode))
            return;

        string sanitized = SanitizeText(text, mode);
        if (sanitized == text)
            return;

        _sanitizing = true;
        try
        {
            int caret = tb.CaretIndex;
            tb.Text = sanitized;
            tb.CaretIndex = Math.Clamp(caret, 0, sanitized.Length);
        }
        finally
        {
            _sanitizing = false;
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb)
            return;

        string candidate = BuildCandidateText(tb, e.Text);
        if (!IsAllowedCandidate(candidate, GetMode(tb)))
            e.Handled = true;
    }

    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb)
            return;

        if (!e.DataObject.GetDataPresent(System.Windows.DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        string pasted = (e.DataObject.GetData(System.Windows.DataFormats.Text) as string ?? "").Trim();
        string candidate = BuildCandidateText(tb, pasted);
        if (!IsAllowedCandidate(candidate, GetMode(tb)))
            e.CancelCommand();
    }

    private static void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb)
            return;

        var mode = GetMode(tb);
        double min = GetMin(tb);
        double max = GetMax(tb);
        bool allowEmpty = GetAllowEmpty(tb);
        string raw = tb.Text.Trim();

        switch (mode)
        {
            case InputConstraintMode.Int:
                if (string.IsNullOrEmpty(raw))
                {
                    if (!allowEmpty && !double.IsNaN(min))
                        tb.Text = ((int)Math.Round(min)).ToString(CultureInfo.InvariantCulture);
                    SetBorderValid(tb, true);
                    return;
                }
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intVal))
                {
                    if (!double.IsNaN(min) && intVal < (int)min) intVal = (int)min;
                    if (!double.IsNaN(max) && intVal > (int)max) intVal = (int)max;
                    string formatted = intVal.ToString(CultureInfo.InvariantCulture);
                    if (tb.Text != formatted)
                        tb.Text = formatted;
                    SetBorderValid(tb, true);
                }
                else
                {
                    SetBorderValid(tb, false);
                }
                break;

            case InputConstraintMode.UDecimal:
            case InputConstraintMode.Decimal:
                if (string.IsNullOrEmpty(raw) || raw is "-" or "." or "," or "-." or "-,")
                {
                    if (allowEmpty)
                        tb.Text = "";
                    else if (!double.IsNaN(min))
                        tb.Text = min.ToString(CultureInfo.InvariantCulture);
                    SetBorderValid(tb, true);
                    return;
                }
                string normalized = raw.Replace(',', '.');
                if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out double dblVal))
                {
                    double clamped = dblVal;
                    if (!double.IsNaN(min) && clamped < min) clamped = min;
                    if (!double.IsNaN(max) && clamped > max) clamped = max;
                    if (Math.Abs(clamped - dblVal) > 1e-9 || raw.Contains(','))
                        tb.Text = clamped.ToString("0.####", CultureInfo.InvariantCulture);
                    SetBorderValid(tb, true);
                }
                else
                {
                    SetBorderValid(tb, false);
                }
                break;

            case InputConstraintMode.HexColor:
                if (string.IsNullOrEmpty(raw))
                {
                    SetBorderValid(tb, allowEmpty);
                    return;
                }
                string hex = raw.StartsWith('#') ? raw.ToUpperInvariant() : "#" + raw.ToUpperInvariant();
                if (hex.Length <= 7 && tb.Text != hex)
                    tb.Text = hex;
                bool validHex = hex.Length == 7 && hex[0] == '#' && hex.Skip(1).All(IsHexDigit);
                SetBorderValid(tb, validHex);
                break;

            case InputConstraintMode.Url:
                if (tb.Text != raw)
                    tb.Text = raw;
                if (string.IsNullOrEmpty(raw))
                {
                    SetBorderValid(tb, true);
                    return;
                }
                bool validUrl = Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
                SetBorderValid(tb, validUrl);
                break;

            case InputConstraintMode.NoWhitespace:
                if (tb.Text != raw)
                    tb.Text = raw;
                SetBorderValid(tb, true);
                break;
        }
    }

    private static void SetBorderValid(System.Windows.Controls.TextBox tb, bool valid) =>
        tb.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, valid ? "HudBorderBrush" : "HudDangerBrush");

    private static string BuildCandidateText(System.Windows.Controls.TextBox tb, string incoming)
    {
        string current = tb.Text ?? "";
        int start = Math.Clamp(tb.SelectionStart, 0, current.Length);
        int length = Math.Clamp(tb.SelectionLength, 0, current.Length - start);
        return current.Remove(start, length).Insert(start, incoming);
    }

    private static bool IsAllowedCandidate(string candidate, InputConstraintMode mode)
    {
        if (string.IsNullOrEmpty(candidate))
            return true;

        switch (mode)
        {
            case InputConstraintMode.Int:
                return candidate.All(static c => c is >= '0' and <= '9');

            case InputConstraintMode.UDecimal:
            {
                int sep = 0;
                foreach (char c in candidate)
                {
                    if (c is >= '0' and <= '9') continue;
                    if ((c == '.' || c == ',') && ++sep <= 1) continue;
                    return false;
                }
                return true;
            }

            case InputConstraintMode.Decimal:
            {
                int sep = 0;
                for (int i = 0; i < candidate.Length; i++)
                {
                    char c = candidate[i];
                    if (c == '-' && i == 0) continue;
                    if (c is >= '0' and <= '9') continue;
                    if ((c == '.' || c == ',') && ++sep <= 1) continue;
                    return false;
                }
                return true;
            }

            case InputConstraintMode.HexColor:
            {
                int start = candidate[0] == '#' ? 1 : 0;
                int digits = candidate.Length - start;
                if (digits > 6) return false;
                for (int i = start; i < candidate.Length; i++)
                {
                    if (!IsHexDigit(candidate[i]))
                        return false;
                }
                return true;
            }

            case InputConstraintMode.Url:
            case InputConstraintMode.NoWhitespace:
                return candidate.All(static c => !char.IsWhiteSpace(c));

            default:
                return true;
        }
    }

    private static string SanitizeText(string text, InputConstraintMode mode)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var chars = new System.Text.StringBuilder(text.Length);
        switch (mode)
        {
            case InputConstraintMode.Int:
                foreach (char c in text)
                    if (c is >= '0' and <= '9') chars.Append(c);
                return chars.ToString();

            case InputConstraintMode.UDecimal:
            {
                bool hasSep = false;
                foreach (char c in text)
                {
                    if (c is >= '0' and <= '9') chars.Append(c);
                    else if ((c == '.' || c == ',') && !hasSep) { hasSep = true; chars.Append(c); }
                }
                return chars.ToString();
            }

            case InputConstraintMode.Decimal:
            {
                bool hasSep = false;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '-' && chars.Length == 0) chars.Append(c);
                    else if (c is >= '0' and <= '9') chars.Append(c);
                    else if ((c == '.' || c == ',') && !hasSep) { hasSep = true; chars.Append(c); }
                }
                return chars.ToString();
            }

            case InputConstraintMode.HexColor:
            {
                int hexCount = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '#' && chars.Length == 0) chars.Append(c);
                    else if (IsHexDigit(c) && hexCount < 6) { hexCount++; chars.Append(c); }
                }
                return chars.ToString();
            }

            case InputConstraintMode.Url:
            case InputConstraintMode.NoWhitespace:
                foreach (char c in text)
                    if (!char.IsWhiteSpace(c)) chars.Append(c);
                return chars.ToString();

            default:
                return text;
        }
    }

    private static bool IsHexDigit(char c) =>
        c is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f');
}
