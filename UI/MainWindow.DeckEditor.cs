// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        UI/MainWindow.DeckEditor.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Settings -> Control Deck Editor (tabs, Favorites, per-button
//              0.5x-4.0x scaling, multi-step macros, double-tap safety, JSON
//              export/import) and live Desktop Control Deck renderer.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HUDnopy.Models;
using HUDnopy.Services;
using Forms = System.Windows.Forms;

namespace HUDnopy;

public partial class MainWindow
{
    private readonly Dictionary<string, DateTimeOffset> _deckConfirmPendingUtc = new(StringComparer.OrdinalIgnoreCase);
    private string _lastDesktopDeckLiveSig = "";
    private bool _suppressDeckEditorEvents;
    private double _editorSelectedSizeScale = 1.0;

    // =========================================================================
    // Live Desktop Control Deck Renderer (Favorites + Custom Tabs + Live State)
    // =========================================================================

    private void RenderResolvedDesktopDeck()
    {
        if (DeckButtonsWrapPanel is null || _configService is null || _bindingsService is null || _fileWatcher is null)
            return;

        string query = TxtSearchBinds?.Text?.Trim() ?? "";
        bool hideUnassigned = ChkHideUnassignedBindings?.IsChecked == true;
        var tabs = DeckLayoutService.BuildResolvedTabs(
            _configService.Current, _bindingsService, _fileWatcher.State, _uiLanguage, hideUnassigned, query);

        ResolvedDeckTab? activeTab = !string.IsNullOrEmpty(_selectedBindingCategory)
            ? tabs.FirstOrDefault(t => string.Equals(t.Id, _selectedBindingCategory, StringComparison.OrdinalIgnoreCase))
            : null;
        activeTab ??= tabs.FirstOrDefault();

        DeckButtonsWrapPanel.Children.Clear();
        if (activeTab is null)
            return;

        _lastDesktopDeckLiveSig = BuildDeckLiveSignature(activeTab);

        foreach (var item in activeTab.Buttons)
        {
            double scale = DeckLayoutService.ClampSizeScale(item.SizeScale);
            double btnWidth = Math.Clamp(Math.Round(168.0 * scale), 88.0, 680.0);
            double btnHeight = Math.Clamp(Math.Round(96.0 * (0.70 + scale * 0.30)), 72.0, 185.0);

            var stack = new StackPanel
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var topBadges = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            topBadges.Children.Add(new TextBlock
            {
                Text = item.Icon,
                FontSize = Math.Clamp(18.0 * (0.85 + scale * 0.15), 14.0, 28.0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            });
            if (item.IsFavorite && !activeTab.IsFavoritesTab)
            {
                topBadges.Children.Add(new TextBlock
                {
                    Text = " ★",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Top,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(250, 204, 21))
                });
            }
            if (item.RequireConfirmation)
            {
                topBadges.Children.Add(new TextBlock
                {
                    Text = " ⚠",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Top,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113))
                });
            }
            stack.Children.Add(topBadges);

            var lbl = new TextBlock
            {
                Text = item.Label,
                FontWeight = FontWeights.Bold,
                FontSize = Math.Clamp(11.5 * (0.88 + scale * 0.12), 10.0, 17.0),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4)
            };
            stack.Children.Add(lbl);

            var bindText = new TextBlock
            {
                Text = item.IsActive ? $"● ON · {item.FormattedBind}" : item.FormattedBind,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 10.5,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            bindText.SetResourceReference(TextBlock.ForegroundProperty, item.IsActive ? "HudOkBrush" : "HudAccentBrush");

            var bindBadge = new Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(7, 9, 12)),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(51, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Child = bindText
            };
            stack.Children.Add(bindBadge);

            var btn = new System.Windows.Controls.Button
            {
                Width = btnWidth,
                MinHeight = btnHeight,
                Margin = new Thickness(5),
                Padding = new Thickness(8, 6, 8, 6),
                Content = stack,
                ToolTip = item.RequireConfirmation
                    ? $"{item.Label} ({item.ActionId}) — Double-tap to confirm"
                    : $"{item.Label} ({item.ActionId})"
            };
            btn.SetResourceReference(StyleProperty, "HudButtonStyle");

            if (item.IsActive)
            {
                btn.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(55, 34, 197, 94));
                btn.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));
                btn.BorderThickness = new Thickness(2);
            }
            else if (!string.IsNullOrWhiteSpace(item.AccentHex))
            {
                try
                {
                    var customBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(item.AccentHex));
                    btn.BorderBrush = customBrush;
                    btn.BorderThickness = new Thickness(1.8);
                }
                catch { }
            }

            var captured = item;
            var capturedLbl = lbl;
            var capturedBtn = btn;
            btn.Click += async (_, _) => await RunUiAsync($"Deck ({captured.ActionId})", async () =>
            {
                if (captured.RequireConfirmation)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (!_deckConfirmPendingUtc.TryGetValue(captured.Id, out var expiresUtc) || now > expiresUtc)
                    {
                        _deckConfirmPendingUtc[captured.Id] = now.AddSeconds(2.5);
                        capturedLbl.Text = "⚠ PULSA OTRA VEZ";
                        capturedBtn.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                        SetStatusBarMessage($"⚠ Confirmar acción: pulsa '{captured.Label}' otra vez en 2.5 s.");
                        _ = Task.Delay(2600).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
                        {
                            if (_deckConfirmPendingUtc.TryGetValue(captured.Id, out var exp) && DateTimeOffset.UtcNow > exp)
                            {
                                _deckConfirmPendingUtc.Remove(captured.Id);
                                capturedLbl.Text = captured.Label;
                            }
                        }));
                        return;
                    }
                    _deckConfirmPendingUtc.Remove(captured.Id);
                    capturedLbl.Text = captured.Label;
                }

                var (ok, msg) = await _inputSimulator.ExecuteActionAsync(captured.ActionId);
                SetStatusBarMessage(ok ? $"✓ {msg}" : $"⚠ {msg}");
            });

            DeckButtonsWrapPanel.Children.Add(btn);
        }

        ApplyDesktopFontScale(_desktopFontScale, DeckButtonsWrapPanel);
    }

    private void RefreshDesktopDeckLiveStatesIfVisible(ShipState s)
    {
        if (ViewControls?.Visibility != Visibility.Visible || DeckButtonsWrapPanel is null)
            return;

        if (_configService.Current.AutoContextDeck)
        {
            var allTabs = DeckLayoutService.BuildResolvedTabs(
                _configService.Current, _bindingsService, s, _uiLanguage, ChkHideUnassignedBindings?.IsChecked == true);
            string? autoTabId = DeckLayoutService.ResolveAutoContextTabId(allTabs, s);
            if (!string.IsNullOrEmpty(autoTabId) &&
                !string.Equals(autoTabId, _selectedBindingCategory, StringComparison.OrdinalIgnoreCase))
            {
                _selectedBindingCategory = autoTabId;
                BuildBindingCategoryNavigation();
                return;
            }
        }

        string query = TxtSearchBinds?.Text?.Trim() ?? "";
        var tabs = DeckLayoutService.BuildResolvedTabs(
            _configService.Current, _bindingsService, s, _uiLanguage, ChkHideUnassignedBindings?.IsChecked == true, query);
        var activeTab = tabs.FirstOrDefault(t => string.Equals(t.Id, _selectedBindingCategory, StringComparison.OrdinalIgnoreCase))
                        ?? tabs.FirstOrDefault();
        if (activeTab is null)
            return;

        string sig = BuildDeckLiveSignature(activeTab);
        if (!string.Equals(sig, _lastDesktopDeckLiveSig, StringComparison.Ordinal))
            RenderResolvedDesktopDeck();
    }

    private static string BuildDeckLiveSignature(ResolvedDeckTab tab) =>
        $"{tab.Id}|{string.Join(",", tab.Buttons.Select(static b => $"{b.Id}:{(b.IsActive ? 1 : 0)}:{b.SizeScale}"))}";

    // =========================================================================
    // Settings -> Control Deck Editor (Tabs, Buttons, Macros, Size 0.5x-4.0x)
    // =========================================================================

    private void InitializeDeckEditorUi()
    {
        if (CmbDeckBtnAction is null || LstDeckEditorTabs is null)
            return;

        _suppressDeckEditorEvents = true;
        CmbDeckBtnAction.Items.Clear();
        foreach (var b in _bindingsService.Bindings.OrderBy(static x => x.DisplayName))
        {
            var view = Localization.DeckTranslations.Present(b, _uiLanguage);
            CmbDeckBtnAction.Items.Add(new ComboBoxItem { Content = $"{view.DisplayName} ({b.ActionId})", Tag = b });
        }
        if (CmbDeckBtnAction.Items.Count > 0)
            CmbDeckBtnAction.SelectedIndex = 0;

        _editorSelectedSizeScale = 1.0;
        if (TxtDeckBtnSizeDisplay is not null) TxtDeckBtnSizeDisplay.Text = "1.0×";
        _suppressDeckEditorEvents = false;

        RefreshDeckEditorTabsList(selectIndex: 0);
        UpdateDeckButtonPreview();
    }

    private List<DeckTabDefinition> GetEditableDeckTabs() =>
        DeckLayoutService.EnsureEditableTabs(_configService.Current, _bindingsService);

    private DeckTabDefinition? GetSelectedEditorTab()
    {
        var tabs = GetEditableDeckTabs();
        int idx = LstDeckEditorTabs?.SelectedIndex ?? -1;
        return idx >= 0 && idx < tabs.Count ? tabs[idx] : null;
    }

    private void RefreshDeckEditorTabsList(int selectIndex = -1)
    {
        if (LstDeckEditorTabs is null)
            return;

        var tabs = GetEditableDeckTabs();
        int prevIdx = selectIndex >= 0 ? selectIndex : LstDeckEditorTabs.SelectedIndex;

        _suppressDeckEditorEvents = true;
        LstDeckEditorTabs.Items.Clear();
        for (int i = 0; i < tabs.Count; i++)
        {
            var t = tabs[i];
            string vis = t.IsVisible ? "●" : "○";
            string ctx = string.IsNullOrWhiteSpace(t.AutoContext) ? "" : $" [{t.AutoContext}]";
            int favCount = t.Buttons.Count(static b => b.IsFavorite);
            string favNote = favCount > 0 ? $" ★{favCount}" : "";
            LstDeckEditorTabs.Items.Add($"{i + 1}. {t.Icon} {t.Name} ({t.Buttons.Count}){ctx}{favNote}  {vis}");
        }
        if (tabs.Count > 0)
            LstDeckEditorTabs.SelectedIndex = Math.Clamp(prevIdx < 0 ? 0 : prevIdx, 0, tabs.Count - 1);
        _suppressDeckEditorEvents = false;

        PopulateSelectedTabEditorFields();
        RefreshDeckEditorButtonsList();
    }

    private void PopulateSelectedTabEditorFields()
    {
        var tab = GetSelectedEditorTab();
        if (tab is null) return;
        _suppressDeckEditorEvents = true;
        if (TxtDeckTabName is not null) TxtDeckTabName.Text = tab.Name;
        if (TxtDeckTabIcon is not null) TxtDeckTabIcon.Text = tab.Icon;
        if (CmbDeckTabAutoContext is not null) CmbDeckTabAutoContext.SelectedValue = tab.AutoContext ?? "";
        _suppressDeckEditorEvents = false;
    }

    private void RefreshDeckEditorButtonsList(int selectIndex = -1)
    {
        if (LstDeckEditorButtons is null)
            return;

        var tab = GetSelectedEditorTab();
        int prevIdx = selectIndex >= 0 ? selectIndex : LstDeckEditorButtons.SelectedIndex;

        _suppressDeckEditorEvents = true;
        LstDeckEditorButtons.Items.Clear();
        if (tab is not null)
        {
            for (int i = 0; i < tab.Buttons.Count; i++)
            {
                var b = tab.Buttons[i];
                string fav = b.IsFavorite ? "★ " : "";
                string conf = b.RequireConfirmation ? "⚠ " : "";
                string size = $"{DeckLayoutService.ClampSizeScale(b.SizeScale).ToString("0.##", CultureInfo.InvariantCulture)}×";
                string col = string.IsNullOrWhiteSpace(b.AccentHex) ? "" : $" [{b.AccentHex}]";
                LstDeckEditorButtons.Items.Add($"{i + 1}. {fav}{conf}{b.Icon} {b.Label}  ({size}){col}");
            }
            if (tab.Buttons.Count > 0 && prevIdx >= 0)
                LstDeckEditorButtons.SelectedIndex = Math.Clamp(prevIdx, 0, tab.Buttons.Count - 1);
        }
        _suppressDeckEditorEvents = false;
    }

    private async Task CommitDeckEditorChangesAsync(string statusMessage)
    {
        var tabs = GetEditableDeckTabs();
        for (int i = 0; i < tabs.Count; i++)
        {
            tabs[i].Order = i;
            for (int j = 0; j < tabs[i].Buttons.Count; j++)
                tabs[i].Buttons[j].Order = j;
        }
        _configService.Current.UseCustomDeckLayout = true;
        _configService.Save();
        SetStatusBarMessage($"✓ {statusMessage}");
        if (NavControls.IsChecked == true)
            BuildBindingCategoryNavigation();
        await _webServer.BroadcastStateAsync();
    }

    private void LstDeckEditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeckEditorEvents) return;
        PopulateSelectedTabEditorFields();
        RefreshDeckEditorButtonsList(selectIndex: 0);
    }

    private void LstDeckEditorButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeckEditorEvents) return;
        var tab = GetSelectedEditorTab();
        int idx = LstDeckEditorButtons?.SelectedIndex ?? -1;
        if (tab is null || idx < 0 || idx >= tab.Buttons.Count) return;

        var b = tab.Buttons[idx];
        _suppressDeckEditorEvents = true;
        if (TxtDeckBtnLabel is not null) TxtDeckBtnLabel.Text = b.Label;
        if (TxtDeckBtnIcon is not null) TxtDeckBtnIcon.Text = b.Icon;
        if (TxtDeckBtnAccentHex is not null) TxtDeckBtnAccentHex.Text = b.AccentHex ?? "";
        if (ChkDeckBtnFavorite is not null) ChkDeckBtnFavorite.IsChecked = b.IsFavorite;
        if (ChkDeckBtnConfirm is not null) ChkDeckBtnConfirm.IsChecked = b.RequireConfirmation;
        _editorSelectedSizeScale = DeckLayoutService.ClampSizeScale(b.SizeScale);
        if (TxtDeckBtnSizeDisplay is not null)
            TxtDeckBtnSizeDisplay.Text = $"{_editorSelectedSizeScale.ToString("0.##", CultureInfo.InvariantCulture)}×";
        bool isSeq = b.ActionId.Contains(',', StringComparison.Ordinal) || b.ActionId.StartsWith("Seq:", StringComparison.OrdinalIgnoreCase);
        if (TxtDeckBtnMacroSeq is not null) TxtDeckBtnMacroSeq.Text = isSeq ? b.ActionId : "";
        _suppressDeckEditorEvents = false;
        UpdateDeckButtonPreview();
    }

    private void CmbDeckBtnAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressDeckEditorEvents) return;
        if (CmbDeckBtnAction?.SelectedItem is ComboBoxItem { Tag: GameBinding gb })
        {
            var view = Localization.DeckTranslations.Present(gb, _uiLanguage);
            if (TxtDeckBtnLabel is not null) TxtDeckBtnLabel.Text = view.DisplayName;
            if (TxtDeckBtnIcon is not null) TxtDeckBtnIcon.Text = string.IsNullOrWhiteSpace(gb.IconGlyph) ? "⚡" : gb.IconGlyph;
            UpdateDeckButtonPreview();
        }
    }

    private void BtnDeckQuickIcon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string glyph } && TxtDeckBtnIcon is not null)
        {
            TxtDeckBtnIcon.Text = glyph;
            UpdateDeckButtonPreview();
        }
    }

    private void BtnDeckQuickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string hex } && TxtDeckBtnAccentHex is not null)
        {
            TxtDeckBtnAccentHex.Text = hex;
            UpdateDeckButtonPreview();
        }
    }

    private void BtnDeckSizeStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string rawDelta } || !int.TryParse(rawDelta, out int delta))
            return;
        var presets = DeckLayoutService.SizeScalePresets;
        int nearestIdx = 0;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < presets.Length; i++)
        {
            double diff = Math.Abs(presets[i] - _editorSelectedSizeScale);
            if (diff < bestDiff) { bestDiff = diff; nearestIdx = i; }
        }
        int nextIdx = Math.Clamp(nearestIdx + delta, 0, presets.Length - 1);
        _editorSelectedSizeScale = presets[nextIdx];
        if (TxtDeckBtnSizeDisplay is not null)
            TxtDeckBtnSizeDisplay.Text = $"{_editorSelectedSizeScale.ToString("0.##", CultureInfo.InvariantCulture)}×";
        UpdateDeckButtonPreview();
    }

    private void DeckEditorPreviewField_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_suppressDeckEditorEvents)
            UpdateDeckButtonPreview();
    }

    private void DeckEditorPreviewCheck_Click(object sender, RoutedEventArgs e) => UpdateDeckButtonPreview();

    private void UpdateDeckButtonPreview()
    {
        if (DeckButtonPreviewBorder is null || TxtDeckPreviewIcon is null || TxtDeckPreviewLabel is null || TxtDeckPreviewMeta is null)
            return;
        string icon = string.IsNullOrWhiteSpace(TxtDeckBtnIcon?.Text) ? "⚡" : TxtDeckBtnIcon.Text.Trim();
        string label = string.IsNullOrWhiteSpace(TxtDeckBtnLabel?.Text) ? "Vista previa" : TxtDeckBtnLabel.Text.Trim();
        string badges = (ChkDeckBtnFavorite?.IsChecked == true ? " ★" : "") + (ChkDeckBtnConfirm?.IsChecked == true ? " ⚠" : "");
        TxtDeckPreviewIcon.Text = icon + badges;
        TxtDeckPreviewLabel.Text = label;
        TxtDeckPreviewMeta.Text = $"{_editorSelectedSizeScale.ToString("0.##", CultureInfo.InvariantCulture)}×";
        DeckButtonPreviewBorder.Width = Math.Clamp(Math.Round(135.0 * _editorSelectedSizeScale), 96.0, 320.0);

        string hex = TxtDeckBtnAccentHex?.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(hex))
        {
            if (!hex.StartsWith('#')) hex = "#" + hex;
            try
            {
                DeckButtonPreviewBorder.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
                return;
            }
            catch { }
        }
        DeckButtonPreviewBorder.SetResourceReference(Border.BorderBrushProperty, "HudAccentBrush");
    }

    private async void BtnDeckTabAdd_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Deck Add Tab", async () =>
        {
            var tabs = GetEditableDeckTabs();
            tabs.Add(new DeckTabDefinition
            {
                Id = $"tab_{Guid.NewGuid():N}"[..12],
                Name = $"Custom {tabs.Count + 1}",
                Icon = "🎛",
                AutoContext = "",
                Order = tabs.Count,
                IsVisible = true
            });
            RefreshDeckEditorTabsList(tabs.Count - 1);
            await CommitDeckEditorChangesAsync("New deck tab added.");
        });

    private async void BtnDeckTabSave_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Deck Save Tab", async () =>
        {
            var tab = GetSelectedEditorTab();
            if (tab is null) return;
            if (!string.IsNullOrWhiteSpace(TxtDeckTabName?.Text))
                tab.Name = TxtDeckTabName.Text.Trim();
            if (!string.IsNullOrWhiteSpace(TxtDeckTabIcon?.Text))
                tab.Icon = TxtDeckTabIcon.Text.Trim();
            tab.AutoContext = CmbDeckTabAutoContext?.SelectedValue as string ?? "";
            RefreshDeckEditorTabsList();
            await CommitDeckEditorChangesAsync($"Tab '{tab.Name}' updated.");
        });

    private async void BtnDeckTabUp_Click(object sender, RoutedEventArgs e) =>
        await MoveDeckTabAsync(-1);

    private async void BtnDeckTabDown_Click(object sender, RoutedEventArgs e) =>
        await MoveDeckTabAsync(1);

    private async Task MoveDeckTabAsync(int delta) =>
        await RunUiAsync("Deck Move Tab", async () =>
        {
            var tabs = GetEditableDeckTabs();
            int idx = LstDeckEditorTabs?.SelectedIndex ?? -1;
            int target = idx + delta;
            if (idx < 0 || target < 0 || target >= tabs.Count) return;
            var item = tabs[idx];
            tabs.RemoveAt(idx);
            tabs.Insert(target, item);
            RefreshDeckEditorTabsList(target);
            await CommitDeckEditorChangesAsync("Tab order updated.");
        });

    private async void BtnDeckTabToggleVis_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Deck Toggle Tab Visibility", async () =>
        {
            var tab = GetSelectedEditorTab();
            if (tab is null) return;
            tab.IsVisible = !tab.IsVisible;
            RefreshDeckEditorTabsList();
            await CommitDeckEditorChangesAsync($"Tab '{tab.Name}' {(tab.IsVisible ? "shown" : "hidden")}.");
        });

    private async void BtnDeckTabDelete_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Deck Delete Tab", async () =>
        {
            var tabs = GetEditableDeckTabs();
            int idx = LstDeckEditorTabs?.SelectedIndex ?? -1;
            if (idx < 0 || idx >= tabs.Count) return;
            string removed = tabs[idx].Name;
            tabs.RemoveAt(idx);
            RefreshDeckEditorTabsList(Math.Max(0, idx - 1));
            await CommitDeckEditorChangesAsync($"Tab '{removed}' removed.");
        });

    private CustomDeckButtonItem BuildEditorButtonItem(string? existingId = null)
    {
        string macroSeq = TxtDeckBtnMacroSeq?.Text?.Trim() ?? "";
        string actionId = !string.IsNullOrWhiteSpace(macroSeq)
            ? macroSeq
            : (CmbDeckBtnAction?.SelectedItem is ComboBoxItem { Tag: GameBinding gb } ? gb.ActionId : "");

        string label = TxtDeckBtnLabel?.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(label))
            label = actionId;
        string icon = string.IsNullOrWhiteSpace(TxtDeckBtnIcon?.Text) ? "⚡" : TxtDeckBtnIcon.Text.Trim();
        string hex = TxtDeckBtnAccentHex?.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(hex) && !hex.StartsWith('#'))
            hex = "#" + hex;

        double sizeScale = DeckLayoutService.ClampSizeScale(_editorSelectedSizeScale);

        return new CustomDeckButtonItem
        {
            Id = existingId ?? Guid.NewGuid().ToString("N")[..8],
            Label = label,
            Icon = icon,
            ActionId = actionId,
            AccentHex = hex,
            IsVisible = true,
            IsFavorite = ChkDeckBtnFavorite?.IsChecked == true,
            RequireConfirmation = ChkDeckBtnConfirm?.IsChecked == true,
            SizeScale = sizeScale
        };
    }

    private async void BtnDeckBtnAdd_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Deck Add Button", async () =>
        {
            var tab = GetSelectedEditorTab();
            if (tab is null) return;
            var btn = BuildEditorButtonItem();
            if (string.IsNullOrWhiteSpace(btn.ActionId)) return;
            btn.Order = tab.Buttons.Count;
            tab.Buttons.Add(btn);
            RefreshDeckEditorTabsList();
            RefreshDeckEditorButtonsList(tab.Buttons.Count - 1);
            await CommitDeckEditorChangesAsync($"Added '{btn.Label}' to '{tab.Name}'.");
        });

    private async void BtnDeckBtnUpdate_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Deck Update Button", async () =>
        {
            var tab = GetSelectedEditorTab();
            int idx = LstDeckEditorButtons?.SelectedIndex ?? -1;
            if (tab is null || idx < 0 || idx >= tab.Buttons.Count) return;

            var existing = tab.Buttons[idx];
            var updated = BuildEditorButtonItem(existing.Id);
            if (string.IsNullOrWhiteSpace(updated.ActionId))
                updated.ActionId = existing.ActionId;
            updated.Order = idx;
            tab.Buttons[idx] = updated;
            RefreshDeckEditorTabsList();
            RefreshDeckEditorButtonsList(idx);
            await CommitDeckEditorChangesAsync($"Updated '{updated.Label}' ({updated.SizeScale:0.##}×).");
        });

    private async void BtnDeckBtnUp_Click(object sender, RoutedEventArgs e) =>
        await MoveDeckButtonAsync(-1);

    private async void BtnDeckBtnDown_Click(object sender, RoutedEventArgs e) =>
        await MoveDeckButtonAsync(1);

    private async Task MoveDeckButtonAsync(int delta) =>
        await RunUiAsync("Deck Move Button", async () =>
        {
            var tab = GetSelectedEditorTab();
            int idx = LstDeckEditorButtons?.SelectedIndex ?? -1;
            int target = idx + delta;
            if (tab is null || idx < 0 || target < 0 || target >= tab.Buttons.Count) return;
            var item = tab.Buttons[idx];
            tab.Buttons.RemoveAt(idx);
            tab.Buttons.Insert(target, item);
            RefreshDeckEditorButtonsList(target);
            await CommitDeckEditorChangesAsync("Button order updated.");
        });

    private async void BtnDeckBtnToggleFav_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Deck Toggle Favorite", async () =>
        {
            var tab = GetSelectedEditorTab();
            int idx = LstDeckEditorButtons?.SelectedIndex ?? -1;
            if (tab is null || idx < 0 || idx >= tab.Buttons.Count) return;
            tab.Buttons[idx].IsFavorite = !tab.Buttons[idx].IsFavorite;
            RefreshDeckEditorTabsList();
            RefreshDeckEditorButtonsList(idx);
            await CommitDeckEditorChangesAsync($"★ Favorite {(tab.Buttons[idx].IsFavorite ? "enabled" : "removed")} for '{tab.Buttons[idx].Label}'.");
        });

    private async void BtnDeckBtnDelete_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Deck Remove Button", async () =>
        {
            var tab = GetSelectedEditorTab();
            int idx = LstDeckEditorButtons?.SelectedIndex ?? -1;
            if (tab is null || idx < 0 || idx >= tab.Buttons.Count) return;
            string label = tab.Buttons[idx].Label;
            tab.Buttons.RemoveAt(idx);
            RefreshDeckEditorTabsList();
            RefreshDeckEditorButtonsList(Math.Max(0, idx - 1));
            await CommitDeckEditorChangesAsync($"Removed '{label}'.");
        });

    private async void BtnDeckResetDefaults_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Deck Reset Defaults", async () =>
        {
            _configService.Current.DeckTabs = DeckLayoutService.CreateDefaultTabs(_bindingsService, _uiLanguage);
            _configService.Current.UseCustomDeckLayout = false;
            _configService.Save();
            RefreshDeckEditorTabsList(selectIndex: 0);
            SetStatusBarMessage("✓ Control Deck restored to default layout.");
            if (NavControls.IsChecked == true)
                BuildBindingCategoryNavigation();
            await _webServer.BroadcastStateAsync();
        });

    private async void BtnDeckExportJson_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Deck Export JSON", async () =>
        {
            using var dlg = new Forms.SaveFileDialog
            {
                Title = "Export Control Deck Profile",
                Filter = "JSON files (*.json)|*.json",
                FileName = "hudnopy-deck-profile.json"
            };
            if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
            string json = DeckLayoutService.ExportTabsJson(GetEditableDeckTabs());
            await File.WriteAllTextAsync(dlg.FileName, json);
            SetStatusBarMessage($"✓ Deck profile exported to {Path.GetFileName(dlg.FileName)}.");
        });

    private async void BtnDeckImportJson_Click(object sender, RoutedEventArgs e) =>
        await RunUiAsync("Deck Import JSON", async () =>
        {
            using var dlg = new Forms.OpenFileDialog
            {
                Title = "Import Control Deck Profile",
                Filter = "JSON files (*.json)|*.json"
            };
            if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
            string json = await File.ReadAllTextAsync(dlg.FileName);
            if (!DeckLayoutService.TryImportTabsJson(json, out var imported, out string error))
            {
                SetStatusBarMessage($"⚠ {error}");
                return;
            }
            _configService.Current.DeckTabs = imported;
            RefreshDeckEditorTabsList(selectIndex: 0);
            await CommitDeckEditorChangesAsync($"Imported {imported.Count} tabs from {Path.GetFileName(dlg.FileName)}.");
        });
}
