// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/DeckEditorWindow.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Visual Control Deck Editor modal allowing commanders to add,
//              reorder, style, and toggle custom pinned deck buttons synced
//              between Desktop WPF and Web HUD.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HUDnopy.Models;
using HUDnopy.Services;

namespace HUDnopy.UI;

/// <summary>
/// Dark HUD modal to customize pinned Control Deck buttons persisted in AppConfig.CustomDeckButtons.
/// </summary>
public sealed class DeckEditorWindow : Window
{
    private readonly ConfigService _configService;
    private readonly ObservableCollection<CustomDeckButtonItem> _items;
    private readonly System.Windows.Controls.ListBox _listBox;
    private readonly System.Windows.Controls.ComboBox _cmbAction;
    private readonly System.Windows.Controls.TextBox _txtLabel;
    private readonly System.Windows.Controls.TextBox _txtIcon;
    private readonly System.Windows.Controls.TextBox _txtAccent;

    public DeckEditorWindow(ConfigService configService, BindingsService bindingsService, Window? owner = null)
    {
        _configService = configService;
        if (owner is not null) Owner = owner;

        Title = "HUDnopy — Control Deck Editor";
        Width = 660;
        Height = 470;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = owner is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "HudBgBrush");
        SetResourceReference(ForegroundProperty, "HudTextBrush");

        _items = new ObservableCollection<CustomDeckButtonItem>(
            (_configService.Current.CustomDeckButtons ?? new List<CustomDeckButtonItem>())
                .OrderBy(static b => b.Order)
                .Select(static b => new CustomDeckButtonItem
                {
                    Id = b.Id,
                    Label = b.Label,
                    Icon = b.Icon,
                    ActionId = b.ActionId,
                    AccentHex = b.AccentHex,
                    Order = b.Order,
                    IsVisible = b.IsVisible
                }));

        var root = new DockPanel { Margin = new Thickness(16) };

        var header = new TextBlock
        {
            Text = "🎛 CUSTOM CONTROL DECK EDITOR (DESKTOP + WEB HUD)",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 10)
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "HudAccentBrush");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // Footer Save / Cancel
        var footer = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var btnCancel = MakeButton("Cancel");
        btnCancel.Margin = new Thickness(0, 0, 8, 0);
        btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        var btnSave = MakeButton("✓ Save Deck");
        btnSave.Click += (_, _) => SaveAndClose();
        footer.Children.Add(btnCancel);
        footer.Children.Add(btnSave);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // Add new button panel
        var addGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _cmbAction = new System.Windows.Controls.ComboBox { MinHeight = 38, Margin = new Thickness(0, 0, 6, 0) };
        foreach (var b in bindingsService.Bindings.OrderBy(static x => x.DisplayName))
            _cmbAction.Items.Add(new ComboBoxItem { Content = $"{b.DisplayName} ({b.ActionId})", Tag = b });
        if (_cmbAction.Items.Count > 0)
            _cmbAction.SelectedIndex = 0;

        _txtLabel = MakeInput("Custom Label");
        _txtLabel.Margin = new Thickness(0, 0, 6, 0);
        _txtIcon = MakeInput("⚡");
        _txtIcon.Margin = new Thickness(0, 0, 6, 0);
        _txtAccent = MakeInput("");
        _txtAccent.ToolTip = "Optional HEX (e.g. #00E5FF)";
        _txtAccent.Margin = new Thickness(0, 0, 6, 0);

        _cmbAction.SelectionChanged += (_, _) =>
        {
            if (_cmbAction.SelectedItem is ComboBoxItem { Tag: GameBinding gb })
                _txtLabel.Text = gb.DisplayName;
        };
        if (_cmbAction.SelectedItem is ComboBoxItem { Tag: GameBinding initGb })
            _txtLabel.Text = initGb.DisplayName;

        var btnAdd = MakeButton("+ Add");
        btnAdd.Click += (_, _) => AddSelectedAction();

        Grid.SetColumn(_cmbAction, 0);
        Grid.SetColumn(_txtLabel, 1);
        Grid.SetColumn(_txtIcon, 2);
        Grid.SetColumn(_txtAccent, 3);
        Grid.SetColumn(btnAdd, 4);
        addGrid.Children.Add(_cmbAction);
        addGrid.Children.Add(_txtLabel);
        addGrid.Children.Add(_txtIcon);
        addGrid.Children.Add(_txtAccent);
        addGrid.Children.Add(btnAdd);
        DockPanel.SetDock(addGrid, Dock.Top);
        root.Children.Add(addGrid);

        // Main list + reorder/delete toolbar
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _listBox = new System.Windows.Controls.ListBox
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 14, 20)),
            BorderThickness = new Thickness(1)
        };
        _listBox.SetResourceReference(ForegroundProperty, "HudTextBrush");
        _listBox.SetResourceReference(BorderBrushProperty, "HudBorderBrush");
        RefreshListBox();
        Grid.SetColumn(_listBox, 0);
        body.Children.Add(_listBox);

        var sideTools = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        var btnUp = MakeButton("▲ Up");
        btnUp.Margin = new Thickness(0, 0, 0, 6);
        btnUp.Click += (_, _) => MoveSelected(-1);
        var btnDown = MakeButton("▼ Down");
        btnDown.Margin = new Thickness(0, 0, 0, 6);
        btnDown.Click += (_, _) => MoveSelected(1);
        var btnToggle = MakeButton("👁 Show/Hide");
        btnToggle.Margin = new Thickness(0, 0, 0, 6);
        btnToggle.Click += (_, _) => ToggleSelectedVisibility();
        var btnRemove = MakeButton("🗑 Remove");
        btnRemove.Click += (_, _) => RemoveSelected();

        sideTools.Children.Add(btnUp);
        sideTools.Children.Add(btnDown);
        sideTools.Children.Add(btnToggle);
        sideTools.Children.Add(btnRemove);
        Grid.SetColumn(sideTools, 1);
        body.Children.Add(sideTools);

        root.Children.Add(body);
        Content = root;
    }

    private void AddSelectedAction()
    {
        if (_cmbAction.SelectedItem is not ComboBoxItem { Tag: GameBinding gb })
            return;
        string label = string.IsNullOrWhiteSpace(_txtLabel.Text) ? gb.DisplayName : _txtLabel.Text.Trim();
        string icon = string.IsNullOrWhiteSpace(_txtIcon.Text) ? "⚡" : _txtIcon.Text.Trim();
        string accent = _txtAccent.Text.Trim();
        if (!string.IsNullOrEmpty(accent) && !accent.StartsWith('#'))
            accent = "#" + accent;

        _items.Add(new CustomDeckButtonItem
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Label = label,
            Icon = icon,
            ActionId = gb.ActionId,
            AccentHex = accent,
            Order = _items.Count,
            IsVisible = true
        });
        RefreshListBox(_items.Count - 1);
    }

    private void MoveSelected(int delta)
    {
        int idx = _listBox.SelectedIndex;
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= _items.Count)
            return;
        var item = _items[idx];
        _items.RemoveAt(idx);
        _items.Insert(target, item);
        RefreshListBox(target);
    }

    private void ToggleSelectedVisibility()
    {
        int idx = _listBox.SelectedIndex;
        if (idx < 0 || idx >= _items.Count)
            return;
        _items[idx].IsVisible = !_items[idx].IsVisible;
        RefreshListBox(idx);
    }

    private void RemoveSelected()
    {
        int idx = _listBox.SelectedIndex;
        if (idx < 0 || idx >= _items.Count)
            return;
        _items.RemoveAt(idx);
        RefreshListBox(Math.Min(idx, _items.Count - 1));
    }

    private void RefreshListBox(int selectIdx = -1)
    {
        _listBox.Items.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            var b = _items[i];
            string vis = b.IsVisible ? "●" : "○ (Hidden)";
            string colorNote = string.IsNullOrWhiteSpace(b.AccentHex) ? "" : $" [{b.AccentHex}]";
            _listBox.Items.Add($"{i + 1}. {b.Icon} {b.Label}  —  {b.ActionId}{colorNote}  {vis}");
        }
        if (selectIdx >= 0 && selectIdx < _listBox.Items.Count)
            _listBox.SelectedIndex = selectIdx;
    }

    private void SaveAndClose()
    {
        for (int i = 0; i < _items.Count; i++)
            _items[i].Order = i;
        _configService.Current.CustomDeckButtons = _items.ToList();
        _configService.Save();
        DialogResult = true;
        Close();
    }

    private static System.Windows.Controls.Button MakeButton(string text)
    {
        var b = new System.Windows.Controls.Button
        {
            Content = text,
            MinHeight = 38,
            MinWidth = 92,
            Padding = new Thickness(12, 6, 12, 6)
        };
        b.SetResourceReference(StyleProperty, "HudButtonStyle");
        return b;
    }

    private static System.Windows.Controls.TextBox MakeInput(string text)
    {
        var tb = new System.Windows.Controls.TextBox
        {
            Text = text,
            MinHeight = 38,
            Padding = new Thickness(8, 5, 8, 5),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 10, 14)),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        tb.SetResourceReference(ForegroundProperty, "HudTextBrush");
        tb.SetResourceReference(BorderBrushProperty, "HudBorderBrush");
        return tb;
    }
}
