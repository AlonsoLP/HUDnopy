// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Web/app.js
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Live WebSocket client, MFD rendering, and COVAS AI web UI.
// -----------------------------------------------------------------------------

let lastPayload = null;
let lastDeckRenderKey = null;
let hasAlertSnapshot = false;
let sectionLanguage = null;
let sectionTranslations = {};
let dashboardTranslations = null;
let coreAlertTranslations = null;
let materialTranslations = null;
let flightTranslations = null;
let operationsTranslations = null;
let deckTranslations = null;
let explorationTranslations = null;
let inputTranslations = null;
let connectionKey = 'connecting';
const activeSectionByTab = new Map();
const seenAlertIds = new Set();
let alertToastTimer = null;
let alertToastId = null;
let lastSpanshRenderKey = null;
let lastCarrierRenderKey = null;
let lastHeaderModulesKey = null;
let lastStatusMatrixKey = null;
let cachedNumberFormatLang = null;
let cachedNumberFormatter = null;
let selectedGalnetIndex = 0;
let webErrorReportsSent = 0;

function reportWebClientError(message, source, stack) {
  if (webErrorReportsSent >= 10) return;
  webErrorReportsSent++;
  try {
    const cleanStack = String(stack || '').replace(/https?:\/\/[^/\s)]+/gi, '').slice(0, 800);
    fetch('/api/client-error', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        message: String(message || 'Unknown error').slice(0, 300),
        source: String(source || '').slice(0, 160),
        stack: cleanStack
      })
    }).catch(() => { });
  } catch { }
}

window.addEventListener('error', (event) => {
  let srcPath = '';
  try { srcPath = event?.filename ? new URL(event.filename).pathname : ''; } catch { srcPath = ''; }
  reportWebClientError(
    event?.message || 'Uncaught script error',
    `${srcPath}:${event?.lineno || 0}:${event?.colno || 0}`,
    event?.error?.stack || ''
  );
});

window.addEventListener('unhandledrejection', (event) => {
  const reason = event?.reason;
  reportWebClientError(
    reason?.message || String(reason || 'Unhandled promise rejection'),
    'unhandledrejection',
    reason?.stack || ''
  );
});

function escapeHtml(value) {
  return String(value ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}
function switchTab(tabId, btn) {
  document.querySelectorAll('.tab-view').forEach(el => el.classList.remove('active'));
  document.querySelectorAll('nav.tabs button').forEach(el => el.classList.remove('active'));
  document.getElementById('tab-' + tabId).classList.add('active');
  btn.classList.add('active');
  const sections = [...document.querySelectorAll(`#tab-${tabId} .module-section`)];
  sections.forEach((section, index) => {
    section.classList.toggle('active', index === 0);
  });
  activeSectionByTab.set(`tab-${tabId}`, sections[0]?.dataset.sectionTitle || null);
  updateSectionTabs();
}

const SECTION_ICONS = {
  'summary': '📊',
  'alerts-activity': '🔔',
  'status-resources': '🚀',
  'ship-systems': '⚡',
  'ship-modules': '🔧',
  'on-foot': '🧑‍🚀',
  'combat': '⚔️',
  'current-system': '🌌',
  'route': '🧭',
  'exploration': '🔭',
  'surface-nav': '🪐',
  'carrier': '🛸',
  'spansh-services': '🛰️',
  'missions': '📜',
  'passengers': '👥',
  'mining': '⛏️',
  'colonisation': '🏗️',
  'cargo': '📦',
  'materials': '💎',
  'engineers': '⚙️',
  'backpack': '🎒',
  'market': '🏪',
  'outfitting': '🔩',
  'shipyard': '🚢',
  'ai-copilot': '🤖',
  'galnet': '📰',
  'cockpit-radio': '📻',
  'exploration-milestones': '🏆',
  'journal': '📖',
  'commander-profile': '🪪'
};

function updateSectionTabs() {
  const activeTab = document.querySelector('.tab-view.active');
  const sectionTabs = document.getElementById('sectionTabs');
  sectionTabs.replaceChildren();
  const sections = activeTab ? [...activeTab.querySelectorAll('.module-section')] : [];
  sectionTabs.classList.toggle('section-tabs-empty', sections.length <= 1);
  if (sections.length <= 1) return;

  const rememberedSection = activeSectionByTab.get(activeTab?.id);
  const selectedSection = sections.find(section => section.dataset.sectionTitle === rememberedSection)
    || sections.find(section => section.classList.contains('active'))
    || sections[0];

  const activeLabel = document.createElement('span');
  activeLabel.className = 'section-active-label';

  sections.forEach((section, index) => {
    const isSelected = section === selectedSection;
    section.classList.toggle('active', isSelected);
    const key = section.dataset.sectionKey;
    if (key && sectionLanguage && !Object.prototype.hasOwnProperty.call(sectionTranslations, key))
      console.error(`Falta la traducción de la sección: ${key}`);
    const label = sectionTranslations[key] || section.dataset.sectionLabel || section.dataset.sectionTitle || `Sección ${index + 1}`;
    if (isSelected) activeLabel.textContent = label;

    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'section-icon-btn';
    button.textContent = (key && SECTION_ICONS[key]) ? SECTION_ICONS[key] : String(index + 1);
    button.title = label;
    button.setAttribute('aria-label', label);
    if ((key && sectionTranslations[key]) || section.dataset.sectionLabel) button.lang = sectionLanguage;
    button.classList.toggle('active', isSelected);
    button.addEventListener('click', () => {
      activeSectionByTab.set(activeTab.id, section.dataset.sectionTitle);
      sections.forEach(item => item.classList.toggle('active', item === section));
      sectionTabs.querySelectorAll('button').forEach(item => item.classList.toggle('active', item === button));
      activeLabel.textContent = label;
    });
    sectionTabs.append(button);
  });
  sectionTabs.append(activeLabel);
}

function showToast(msg) {
  const t = document.getElementById('toast');
  t.textContent = msg;
  t.classList.add('show');
  clearTimeout(window._toastTimer);
  window._toastTimer = setTimeout(() => t.classList.remove('show'), 2400);
}

function renderTableRows(tableBodyId, items, columns, emptyText) {
  const body = document.getElementById(tableBodyId);
  body.replaceChildren();
  if (!items.length) {
    const row = document.createElement('tr');
    const cell = document.createElement('td');
    cell.colSpan = columns.length;
    cell.textContent = emptyText;
    row.append(cell);
    body.append(row);
    return;
  }

  for (const [index, item] of items.entries()) {
    const row = document.createElement('tr');
    for (const column of columns) {
      const cell = document.createElement('td');
      const value = column(item, index);
      cell.textContent = value === null || value === undefined || value === '' ? '—' : String(value);
      row.append(cell);
    }
    body.append(row);
  }
}

function renderSpansh(view) {
  if (!view) {
    console.error('Falta la presentación de Spansh recibida del servidor.');
    return;
  }
  const renderKey = JSON.stringify([sectionLanguage, view]);
  if (renderKey === lastSpanshRenderKey) return;
  lastSpanshRenderKey = renderKey;

  document.getElementById('spanshTitle').textContent = view.Title;
  document.getElementById('spanshStatus').textContent = view.Status;
  document.getElementById('spanshSummary').textContent = view.Summary;
  document.getElementById('spanshUpdated').textContent = view.Updated;
  document.getElementById('spanshUpdated').hidden = !view.Updated;
  document.getElementById('spanshSourceNote').textContent = view.SourceNote;
  document.getElementById('spanshStationsTitle').textContent = view.StationsTitle;

  const headerLabels = {
    body: view.BodyHeader,
    type: view.TypeHeader,
    arrival: view.ArrivalHeader,
    landable: view.LandableHeader,
    station: view.StationHeader,
    stationBody: view.StationBodyHeader,
    controllingFaction: view.FactionHeader
  };
  document.querySelectorAll('[data-spansh-key]').forEach(header => {
    header.textContent = headerLabels[header.dataset.spanshKey] || '';
    header.lang = sectionLanguage;
  });

  const renderRows = (bodyId, items, fields, emptyText) => {
    const body = document.getElementById(bodyId);
    body.replaceChildren();
    if (!items.length) {
      const row = document.createElement('tr');
      const cell = document.createElement('td');
      cell.colSpan = fields.length;
      cell.textContent = emptyText;
      row.append(cell);
      body.append(row);
      return;
    }

    items.forEach(item => {
      const row = document.createElement('tr');
      fields.forEach(field => {
        const cell = document.createElement('td');
        const value = item[field];
        cell.textContent = value === null || value === undefined || value === '' ? '—' : String(value);
        row.append(cell);
      });
      body.append(row);
    });
  };

  renderRows('spanshStationsTableBody', Array.isArray(view.Stations) ? view.Stations : [],
    ['Name', 'Type', 'BodyName', 'ArrivalDistance', 'ControllingFaction'], view.EmptyStations);
}

function renderCarrier(view) {
  if (!view) {
    console.error('Falta la presentación del portanaves recibida del servidor.');
    return;
  }
  const renderKey = JSON.stringify([sectionLanguage, view]);
  if (renderKey === lastCarrierRenderKey) return;
  lastCarrierRenderKey = renderKey;
  document.getElementById('carrierTitle').textContent = view.Title;
  const status = document.getElementById('carrierStatus');
  status.textContent = view.Status;
  status.hidden = view.HasCarrier;
  document.getElementById('carrierFieldHeader').textContent = view.FieldHeader;
  document.getElementById('carrierValueHeader').textContent = view.ValueHeader;
  const cards = document.getElementById('carrierCards');
  cards.hidden = !view.HasCarrier;
  cards.replaceChildren();
  const addMetric = (parent, data, isFuel = false) => {
    const metric = document.createElement('div');
    metric.className = 'carrier-metric';
    const header = document.createElement('div');
    header.className = 'bar-header';
    const heading = document.createElement('span');
    heading.textContent = data.Title;
    const values = document.createElement('span');
    values.className = 'carrier-metric-values';
    values.textContent = `${data.Left.Value} / ${data.Right.Value}`;
    values.title = `${data.Left.Label}: ${data.Left.Value}; ${data.Right.Label}: ${data.Right.Value}`;
    values.setAttribute('aria-label', values.title);
    header.append(heading, values);
    metric.append(header);
    if (data.Percent != null) {
      const bar = document.createElement('div');
      bar.className = 'bar-track';
      bar.setAttribute('role', 'progressbar');
      bar.setAttribute('aria-valuemin', '0');
      bar.setAttribute('aria-valuemax', '100');
      bar.setAttribute('aria-valuenow', data.Percent);
      bar.setAttribute('aria-label', data.Title);
      const fill = document.createElement('div');
      fill.className = isFuel ? 'bar-fill fuel' : 'bar-fill';
      fill.style.width = `${Math.max(0, Math.min(100, data.Percent))}%`;
      bar.append(fill);
      metric.append(bar);
    }
    parent.append(metric);
  };
  if (view.HasCarrier) {
    const summary = document.createElement('div');
    summary.className = 'carrier-card carrier-metrics';
    addMetric(summary, view.Space);
    addMetric(summary, view.Finance);
    addMetric(summary, view.Fuel, true);
    cards.append(summary);
    if (view.HasJump) {
      const jump = document.createElement('div');
      jump.className = 'carrier-card carrier-jump';
      const heading = document.createElement('div');
      heading.className = 'carrier-card-title';
      heading.textContent = view.JumpTitle;
      jump.append(heading);
      view.Jump.forEach(row => {
        const line = document.createElement('div');
        const label = document.createElement('span');
        const value = document.createElement('span');
        line.className = 'carrier-card-row';
        label.className = 'carrier-card-label';
        value.className = 'carrier-card-value';
        label.textContent = row.Label + ': ';
        value.textContent = row.Value;
        line.append(label, value);
        jump.append(line);
      });
      cards.append(jump);
    }
  }
  document.getElementById('carrierTableContainer').hidden = view.Rows.length === 0;
  renderTableRows('carrierTableBody', view.Rows, [row => row.Label, row => row.Value], '');
}

const formatCount = value => {
  const lang = sectionLanguage || 'en-GB';
  if (cachedNumberFormatLang !== lang || !cachedNumberFormatter) {
    cachedNumberFormatLang = lang;
    cachedNumberFormatter = new Intl.NumberFormat(lang);
  }
  return cachedNumberFormatter.format(value ?? 0);
};

async function sendCmd(actionId) {
  try {
    const res = await fetch('/api/command/' + encodeURIComponent(actionId), { method: 'POST' });
    const data = await res.json();
    showToast(data.message || (inputTranslations ? inputTranslations.commandFallback.replace('{0}', actionId) : 'Comando: ' + actionId));
    if (data.state && lastPayload && data.sectionLanguage === sectionLanguage) {
      lastPayload.state = data.state;
      lastPayload.onFootStatus = data.onFootStatus;
      lastPayload.operationsView = data.operationsView;
      lastPayload.carrierView = data.carrierView;
      renderState(lastPayload);
    }
  } catch (e) {
    console.error('Command request failed:', e);
    showToast(inputTranslations ? inputTranslations.commandError : 'Error al enviar comando');
  }
}

function renderHeaderModules(modules) {
  const enabledModules = modules.filter(item => item.Enabled);
  const renderKey = JSON.stringify(enabledModules);
  if (renderKey === lastHeaderModulesKey) return;
  lastHeaderModulesKey = renderKey;
  const telemetry = document.getElementById('headerTelemetry');
  telemetry.replaceChildren();
  for (const module of enabledModules) {
    const pill = document.createElement('div');
    pill.className = 'telemetry-pill';
    const label = document.createElement('span');
    label.className = 'lbl';
    label.textContent = module.Label;
    const value = document.createElement('span');
    value.className = 'val';
    value.textContent = module.Value;
    pill.append(label, value);
    telemetry.append(pill);
  }
}

async function refreshWebGalnet() {
  try { await fetch('/api/galnet/refresh', { method: 'POST' }); } catch { }
}

async function refreshWebGalaxyStatus() {
  try { await fetch('/api/galaxy-status/refresh', { method: 'POST' }); } catch { }
}

function renderState(payload) {
  lastPayload = payload;
  if (payload.sectionLanguage && payload.sectionLanguage !== sectionLanguage) {
    if (!payload.sectionTranslations || typeof payload.sectionTranslations !== 'object' || Array.isArray(payload.sectionTranslations)
      || !payload.navigationTranslations || typeof payload.navigationTranslations !== 'object' || Array.isArray(payload.navigationTranslations)
      || !payload.dashboardTranslations || typeof payload.dashboardTranslations !== 'object' || Array.isArray(payload.dashboardTranslations)
      || !payload.coreAlertTranslations || typeof payload.coreAlertTranslations !== 'object' || Array.isArray(payload.coreAlertTranslations)
      || !payload.tableHeaders || typeof payload.tableHeaders !== 'object' || Array.isArray(payload.tableHeaders)
      || !payload.materialTranslations || typeof payload.materialTranslations !== 'object' || Array.isArray(payload.materialTranslations)
      || !payload.flightTranslations || typeof payload.flightTranslations !== 'object' || Array.isArray(payload.flightTranslations)
      || !payload.operationsTranslations || typeof payload.operationsTranslations !== 'object' || Array.isArray(payload.operationsTranslations)
      || !payload.explorationTranslations || typeof payload.explorationTranslations !== 'object' || Array.isArray(payload.explorationTranslations)
      || !payload.inputTranslations || typeof payload.inputTranslations !== 'object' || Array.isArray(payload.inputTranslations)
      || !payload.deckTranslations || typeof payload.deckTranslations !== 'object' || Array.isArray(payload.deckTranslations)
      || !Array.isArray(payload.deckBindings)
      || !payload.languageSettings || typeof payload.languageSettings.partialWarning !== 'string') {
      console.error('Faltan las traducciones de navegación recibidas del servidor.');
      return;
    } else {
      sectionLanguage = payload.sectionLanguage;
      document.documentElement.lang = sectionLanguage;
      sectionTranslations = payload.sectionTranslations;
      dashboardTranslations = payload.dashboardTranslations;
      coreAlertTranslations = payload.coreAlertTranslations;
      materialTranslations = payload.materialTranslations;
      flightTranslations = payload.flightTranslations;
      operationsTranslations = payload.operationsTranslations;
      explorationTranslations = payload.explorationTranslations;
      inputTranslations = payload.inputTranslations;
      document.getElementById('connBadge').textContent = inputTranslations[connectionKey];
      document.getElementById('sectionTabs').setAttribute('aria-label', inputTranslations.moduleSections);
      deckTranslations = payload.deckTranslations;
      const deckSearch = document.getElementById('deckSearch');
      deckSearch.placeholder = deckTranslations.search;
      deckSearch.setAttribute('aria-label', deckTranslations.search);
      document.querySelectorAll('[data-operations-key]').forEach(element => {
        const value = operationsTranslations[element.dataset.operationsKey];
        if (typeof value !== 'string' || !value) {
          console.error(`Falta la traducción de operaciones: ${element.dataset.operationsKey}`);
          return;
        }
        element.textContent = value;
        element.lang = sectionLanguage;
      });
      document.querySelectorAll('[data-operations-tooltip-key]').forEach(element => {
        const value = operationsTranslations[element.dataset.operationsTooltipKey];
        if (typeof value !== 'string' || !value) return;
        element.title = value;
        element.setAttribute('aria-label', value);
      });
      document.querySelectorAll('[data-flight-key]').forEach(element => {
        const value = flightTranslations[element.dataset.flightKey];
        if (typeof value !== 'string' || !value) {
          console.error(`Falta la traducción de vuelo: ${element.dataset.flightKey}`);
          return;
        }
        element.textContent = value;
        element.lang = sectionLanguage;
      });
      document.querySelectorAll('[data-flight-tooltip-key]').forEach(element => {
        const value = flightTranslations[element.dataset.flightTooltipKey];
        if (typeof value !== 'string' || !value) return;
        element.title = value;
        element.setAttribute('aria-label', value);
      });
      document.querySelectorAll('[data-flight-placeholder]').forEach(element => {
        const value = flightTranslations[element.dataset.flightPlaceholder];
        if (typeof value === 'string' && value) element.placeholder = value;
      });
      document.querySelectorAll('[data-operations-placeholder]').forEach(element => {
        const value = operationsTranslations[element.dataset.operationsPlaceholder];
        if (typeof value === 'string' && value) element.placeholder = value;
      });
      document.getElementById('materialsTitle').textContent = materialTranslations.title;
      for (const [prefix, searchKey] of [['market', 'marketSearch'], ['profile', 'profileSearch']]) {
        const search = document.getElementById(prefix + 'Search');
        search.placeholder = operationsTranslations[searchKey];
        search.setAttribute('aria-label', operationsTranslations[searchKey]);
        document.getElementById(prefix + 'CategoryFilter').setAttribute('aria-label', materialTranslations.categoryFilter);
      }
      const materialSearch = document.getElementById('materialSearch');
      materialSearch.placeholder = materialTranslations.searchPlaceholder;
      materialSearch.setAttribute('aria-label', materialTranslations.search);
      const categoryFilter = document.getElementById('materialCategoryFilter');
      categoryFilter.setAttribute('aria-label', materialTranslations.categoryFilter);
      ['allCategories', 'raw', 'manufactured', 'encoded'].forEach((key, index) => {
        categoryFilter.options[index].textContent = materialTranslations[key];
      });
      const rarityFilter = document.getElementById('materialRarityFilter');
      rarityFilter.setAttribute('aria-label', materialTranslations.rarityFilter);
      ['allRarities', 'veryCommon', 'common', 'standard', 'rare', 'veryRare', 'unclassified'].forEach((key, index) => {
        rarityFilter.options[index].textContent = index >= 1 && index <= 5
          ? `${materialTranslations[key]} (${index})` : materialTranslations[key];
      });
      document.querySelectorAll('th:not([data-spansh-key]):not([data-carrier-key])').forEach(header => {
        if (!header.dataset.translationKey) {
          const original = header.textContent.trim();
          if (original === '#' || original === 'ID') return;
          console.error(`Falta la clave del encabezado de tabla: ${original}`);
          return;
        }
        const value = payload.tableHeaders[header.dataset.translationKey];
        if (typeof value !== 'string' || !value) {
          console.error(`Falta la traducción del encabezado: ${header.dataset.translationKey}`);
          return;
        }
        header.textContent = value;
        header.lang = sectionLanguage;
      });
      document.querySelectorAll('[data-deck-key]').forEach(element => {
        const value = deckTranslations?.[element.dataset.deckKey];
        if (typeof value !== 'string' || !value) return;
        element.textContent = value;
        element.lang = sectionLanguage;
      });
      const wishlistTargetEl = document.getElementById('webWishlistTarget');
      if (wishlistTargetEl && materialTranslations?.wishlistTargetTip)
        wishlistTargetEl.title = materialTranslations.wishlistTargetTip;
      const wishlistBtnEl = document.getElementById('btnWebToggleWishlist');
      if (wishlistBtnEl && materialTranslations?.wishlistToggle) {
        wishlistBtnEl.title = materialTranslations.wishlistToggle;
        wishlistBtnEl.setAttribute('aria-label', materialTranslations.wishlistToggle);
      }
      document.querySelectorAll('[data-dashboard-key]').forEach(element => {
        const value = dashboardTranslations[element.dataset.dashboardKey];
        if (typeof value !== 'string' || !value) {
          console.error(`Falta la traducción del panel: ${element.dataset.dashboardKey}`);
          return;
        }
        element.textContent = value;
        element.lang = sectionLanguage;
      });
      document.querySelectorAll('[data-dashboard-tooltip-key]').forEach(element => {
        const value = dashboardTranslations[element.dataset.dashboardTooltipKey];
        if (typeof value !== 'string' || !value) return;
        element.title = value;
        element.setAttribute('aria-label', value);
      });
      const warning = document.getElementById('languageWarning');
      warning.lang = sectionLanguage;
      warning.textContent = payload.languageSettings.partialWarning;
      warning.hidden = !payload.languageSettings.partialWarning;
      document.querySelectorAll('nav.tabs button[data-nav-key]').forEach(button => {
        const text = payload.navigationTranslations[button.dataset.navKey];
        if (!text || !text.label || !text.accessibleName || !text.tooltip) {
          console.error(`Falta la traducción de navegación: ${button.dataset.navKey}`);
          return;
        }
        button.querySelector('.nav-label').textContent = text.label;
        button.lang = sectionLanguage;
        button.title = text.tooltip;
        button.setAttribute('aria-label', text.accessibleName);
      });
      updateSectionTabs();
    }
  }
  applyNavigationMenuPosition(payload.navigationMenuPosition);
  document.body.classList.toggle('hide-nav-labels', payload.hideNavigationMenuText === true);
  renderAlerts(payload);
  document.getElementById('spanshSection').hidden = payload.spanshEnabled !== true;
  if (payload.spanshEnabled === true) renderSpansh(payload.spanshView);
  renderCarrier(payload.carrierView);
  const s = payload.state;
  if (!s) return;
  const operations = operationsTranslations;
  const view = payload.operationsView;
  if (!view) {
    console.error('Falta la presentación de operaciones recibida del servidor.');
    return;
  }
  const formatOperation = (key, ...args) =>
    operations[key].replace(/\{(\d+)\}/g, (_, index) => String(args[Number(index)]));

  if (s.ThemeAccentHex) {
    document.documentElement.style.setProperty('--hud-accent', s.ThemeAccentHex);
    const themeMeta = document.getElementById('pwaThemeColor');
    if (themeMeta) themeMeta.setAttribute('content', s.ThemeAccentHex);
  }
  renderWebCockpitRadio(payload.radioStatus, payload.radioStations);

  document.getElementById('onFootStatus').textContent = payload.onFootStatus;
  const telemetry = (value, fractionDigits, multiplier, unit) => value == null
    ? '---' : (value * multiplier).toLocaleString(sectionLanguage, { maximumFractionDigits: fractionDigits, useGrouping: false }) + unit;
  document.getElementById('onFootOxygen').textContent = telemetry(s.Oxygen, 1, 100, '%');
  document.getElementById('onFootHealth').textContent = telemetry(s.Health, 1, 100, '%');
  document.getElementById('onFootTemperature').textContent = telemetry(s.TemperatureKelvin, 2, 1, ' K');
  document.getElementById('onFootGravity').textContent = telemetry(s.Gravity, 2, 1, ' G');
  document.getElementById('onFootWeapon').textContent = s.SelectedWeapon || '---';
  document.getElementById('activeSuit').textContent = s.ActiveSuit || '---';
  document.getElementById('activeSuitLoadout').textContent = s.ActiveSuitLoadout || '---';

  const passengers = s.PassengerManifest || [];
  document.getElementById('passengerStatus').textContent = passengers.length
    ? formatOperation('passengersCount', passengers.length)
    : operations.passengersEmpty;
  renderTableRows('passengerTableBody', passengers, [
    (_, index) => view.passengerTypes[index],
    item => item.Count == null ? '—' : formatCount(item.Count),
    item => item.DestinationSystem,
    item => item.DestinationStation,
    (_, index) => view.passengerExpiries[index],
    item => item.MissionId
  ], operations.passengersEmpty);

  const backpack = s.BackpackInventory || [];
  document.getElementById('backpackStatus').textContent = backpack.length
    ? formatOperation('backpackCount', backpack.length)
    : operations.backpackWaiting;
  renderTableRows('backpackTableBody', backpack, [
    item => item.DisplayName,
    (_, index) => view.backpackTypes[index],
    item => formatCount(item.Count)
  ], operations.backpackEmpty);

  renderMarket(payload);

  document.getElementById('outfittingStatus').textContent = view.outfittingStatus;
  renderTableRows('outfittingTableBody', s.Outfitting?.Items || [], [
    item => item.Id,
    item => item.DisplayName,
    item => `${formatCount(item.BuyPrice)} CR`
  ], operations.outfittingEmpty);

  document.getElementById('shipyardStatus').textContent = view.shipyardStatus;
  renderTableRows('shipyardTableBody', s.Shipyard?.Items || [], [
    item => item.DisplayName,
    item => `${formatCount(item.ShipPrice)} CR`
  ], operations.shipyardEmpty);

  renderProfile(payload);

  const colonisation = s.Colonisation || {};
  document.getElementById('colonisationStatus').textContent = view.constructionStatus;
  document.getElementById('colonisationProgress').textContent = view.constructionProgress;
  renderTableRows('colonisationTableBody', colonisation.Resources || [], [
    item => item.DisplayName,
    item => formatCount(item.RequiredAmount),
    item => formatCount(item.ProvidedAmount),
    item => `${formatCount(item.Payment)} CR`
  ], operations.resourcesEmpty);

  // Full v0.2.0 Desktop <-> Web parity rendering
  if (payload.appVersion) {
    document.title = `HUDnopy v${payload.appVersion} // MFD WEB HUD`;
  }
  const padBanner = document.getElementById('webLandingPadBanner');
  if (padBanner) {
    if (payload.landingPad) {
      const lp = payload.landingPad;
      padBanner.innerHTML = '';
      const title = document.createElement('div');
      title.textContent = `🛬 ${(lp.station || 'STARPORT').toUpperCase()} — ${lp.guidance}`;
      const bay = document.createElement('div');
      bay.style.cssText = 'font-family:Consolas,monospace;color:var(--hud-secondary);font-size:0.84rem;margin-top:4px;';
      bay.textContent = lp.bayIndicator || '';
      padBanner.appendChild(title);
      padBanner.appendChild(bay);
      padBanner.hidden = false;
    } else {
      padBanner.hidden = true;
    }
  }

  // Galaxy Server Status + Last BGS Tick
  const gs = payload.galaxyStatus || {};
  const gsLine = document.getElementById('webGalaxyStatusLine');
  const bgsLine = document.getElementById('webBgsTickInfo');
  const serverMsg = gs.ServerMessage || '---';
  let tickShort = '---';
  let tickFull = (flightTranslations && flightTranslations.bgsTickUnavailable) || '---';
  if (gs.LastBgsTickUtc) {
    const tickDate = new Date(gs.LastBgsTickUtc);
    if (!isNaN(tickDate.getTime())) {
      const diffMin = Math.max(0, Math.floor((Date.now() - tickDate.getTime()) / 60000));
      const elapsed = diffMin >= 60 ? `${Math.floor(diffMin / 60)}h ${String(diffMin % 60).padStart(2, '0')}m` : `${diffMin}m`;
      const utcStr = tickDate.toISOString().slice(0, 16).replace('T', ' ');
      const localStr = tickDate.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
      tickShort = `${localStr} (${elapsed})`;
      tickFull = (flightTranslations && flightTranslations.bgsTickInfo)
        ? flightTranslations.bgsTickInfo.replace('{0}', utcStr).replace('{1}', localStr).replace('{2}', elapsed)
        : `${utcStr} UTC (${localStr} · ${elapsed})`;
    }
  }
  if (gsLine) {
    gsLine.textContent = (flightTranslations && flightTranslations.galaxyServerStatus)
      ? flightTranslations.galaxyServerStatus.replace('{0}', serverMsg).replace('{1}', tickShort)
      : `Elite Servers: ${serverMsg} · BGS Tick: ${tickShort}`;
  }
  if (bgsLine) bgsLine.textContent = tickFull;

  // EDSY / Coriolis ship loadout links
  const loadoutRow = document.getElementById('webLoadoutExportRow');
  if (loadoutRow) {
    const links = payload.loadoutLinks;
    if (links && (links.edsyUrl || links.coriolisUrl)) {
      const edsy = document.getElementById('webEdsyLink');
      const cor = document.getElementById('webCoriolisLink');
      if (edsy && links.edsyUrl) edsy.href = links.edsyUrl;
      if (cor && links.coriolisUrl) cor.href = links.coriolisUrl;
      loadoutRow.hidden = false;
    } else {
      loadoutRow.hidden = true;
    }
  }

  // Carrier countdown, financial runway & cAPI cargo
  const fcCountdown = document.getElementById('webCarrierCountdown');
  if (fcCountdown) {
    const depRaw = s.Carrier && s.Carrier.DepartureTime;
    if (depRaw) {
      const remSec = Math.floor((new Date(depRaw).getTime() - Date.now()) / 1000);
      if (remSec > 0) {
        const m = Math.floor(remSec / 60);
        const sec = remSec % 60;
        fcCountdown.textContent = `⏱ T-${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}`;
        fcCountdown.hidden = false;
      } else {
        fcCountdown.hidden = true;
      }
    } else {
      fcCountdown.hidden = true;
    }
  }
  const fcRunway = document.getElementById('webCarrierUpkeepRunway');
  if (fcRunway) {
    const bal = s.Carrier && s.Carrier.Balance;
    if (bal && bal > 0) {
      const wksBase = Math.floor(bal / 5000000);
      const wksTyp = Math.floor(bal / 15000000);
      const mosTyp = (wksTyp / 4.345).toFixed(1);
      fcRunway.textContent = (flightTranslations && flightTranslations.carrierUpkeepRunway)
        ? flightTranslations.carrierUpkeepRunway.replace('{0}', formatCount(wksBase)).replace('{1}', formatCount(wksTyp)).replace('{2}', mosTyp)
        : `~${wksBase} wks (5M/wk) · ~${wksTyp} wks / ${mosTyp} mos (15M/wk)`;
    } else {
      fcRunway.textContent = '';
    }
  }
  const fcCargoSec = document.getElementById('webCarrierCargoSection');
  const fcCargo = Array.isArray(s.CarrierCargo) ? s.CarrierCargo : [];
  if (fcCargoSec) {
    fcCargoSec.hidden = fcCargo.length === 0;
    if (fcCargo.length > 0) {
      renderTableRows('webCarrierCargoTableBody', fcCargo, [
        item => item.Commodity,
        item => formatCount(item.Quantity),
        item => item.OriginSystem || '---'
      ], '');
    }
  }
  const fcOrdersSec = document.getElementById('webCarrierMarketOrdersSection');
  const fcOrders = Array.isArray(s.CarrierMarketOrders) ? s.CarrierMarketOrders : [];
  if (fcOrdersSec) {
    fcOrdersSec.hidden = fcOrders.length === 0;
    if (fcOrders.length > 0) {
      renderTableRows('webCarrierMarketOrdersTableBody', fcOrders, [
        item => item.OrderType,
        item => item.Commodity,
        item => formatCount(item.Quantity),
        item => item.PriceDisplay || `${formatCount(item.Price)} CR`
      ], '');
    }
  }
  const fcServicesSec = document.getElementById('webCarrierServicesSection');
  const fcServicesText = document.getElementById('webCarrierServicesText');
  const fcServices = Array.isArray(s.CarrierServices) ? s.CarrierServices : [];
  if (fcServicesSec && fcServicesText) {
    fcServicesSec.hidden = fcServices.length === 0;
    if (fcServices.length > 0) fcServicesText.textContent = fcServices.join(' · ');
  }
  // EDSM Discovery Badge + Inara Deep Link
  const edsmCard = document.getElementById('webEdsmDiscoveryCard');
  const edsmBadge = document.getElementById('webEdsmDiscoveryBadge');
  const inaraLink = document.getElementById('webInaraSystemLink');
  if (edsmCard && edsmBadge) {
    const disc = s.EdsmDiscoveryIntel;
    const inaraUrl = payload.inaraSystemUrl || '';
    if (disc && disc.SystemName) {
      edsmBadge.textContent = disc.DiscoveryBadge || '';
      edsmBadge.style.color = disc.IsKnownInEdsm ? 'var(--hud-secondary)' : 'var(--hud-ok)';
    } else {
      edsmBadge.textContent = '';
    }
    if (inaraLink) {
      inaraLink.href = inaraUrl || '#';
      inaraLink.hidden = !inaraUrl;
    }
    edsmCard.hidden = !edsmBadge.textContent && !inaraUrl;
  }
  // Opt-in visibility for Ardent Market & EDAstro DSSA
  const ardentSec = document.getElementById('webArdentMarketSection');
  if (ardentSec) {
    ardentSec.hidden = !(payload.enabledIntegrations && payload.enabledIntegrations.ardentMarketApi);
  }
  const dssaBtn = document.getElementById('btnWebNearestDssa');
  if (dssaBtn) {
    dssaBtn.hidden = !(payload.enabledIntegrations && payload.enabledIntegrations.edastroDssa);
  }
  // EliteBGS Watchlist
  const bgsWatchCard = document.getElementById('webEliteBgsWatchlistCard');
  const bgsWatchItems = Array.isArray(s.WatchedBgsFactions) ? s.WatchedBgsFactions : [];
  const bgsEnabled = !!(payload.enabledIntegrations && payload.enabledIntegrations.eliteBgsWatchlist);
  if (bgsWatchCard) {
    bgsWatchCard.hidden = !bgsEnabled && bgsWatchItems.length === 0;
    if (bgsWatchItems.length > 0) {
      renderTableRows('webEliteBgsTableBody', bgsWatchItems, [
        f => f.FactionName,
        f => f.SystemName,
        f => f.InfluenceDisplay,
        f => f.State,
        f => f.ActiveStates || '—',
        f => f.PendingStates || '—'
      ], '');
    }
  }
  // Editable system note & surface bookmarks sync
  const noteInput = document.getElementById('webSystemNoteInput');
  if (noteInput && document.activeElement !== noteInput) {
    noteInput.value = s.CurrentSystemNote || '';
  }
  if (Array.isArray(payload.surfaceBookmarks)) {
    renderWebSurfaceBookmarks(payload.surfaceBookmarks);
  }

  // Cargo instant sell value + colonisation cross-reference
  const cargoProfitEl = document.getElementById('webCargoTradeProfit');
  if (cargoProfitEl) {
    const cargoList = Array.isArray(s.CargoInventory) ? s.CargoInventory : [];
    const marketItems = (s.Market && Array.isArray(s.Market.Items)) ? s.Market.Items : [];
    const notes = [];
    if (cargoList.length > 0 && marketItems.length > 0) {
      let totalSell = 0, totalProfit = 0;
      for (const c of cargoList) {
        const m = marketItems.find(x => (x.DisplayName || '').toLowerCase() === (c.Name || '').toLowerCase() || (x.Name || '').toLowerCase() === (c.Name || '').toLowerCase());
        if (m && m.SellPrice > 0) {
          totalSell += m.SellPrice * c.Count;
          if (m.MeanPrice > 0 && m.SellPrice > m.MeanPrice) totalProfit += (m.SellPrice - m.MeanPrice) * c.Count;
        }
      }
      if (totalSell > 0) {
        notes.push((operations && operations.cargoMarketSummary)
          ? operations.cargoMarketSummary.replace('{0}', formatCount(totalSell)).replace('{1}', formatCount(totalProfit)).replace('{2}', '')
          : `${formatCount(totalSell)} CR (+${formatCount(totalProfit)} CR)`);
      }
    }
    const colRes = (s.Colonisation && Array.isArray(s.Colonisation.Resources)) ? s.Colonisation.Resources : [];
    if (cargoList.length > 0 && colRes.length > 0) {
      const matches = cargoList
        .filter(c => colRes.some(r => r.RequiredAmount > r.ProvidedAmount && (r.DisplayName || '').toLowerCase() === (c.Name || '').toLowerCase()))
        .map(c => `${c.Name} (${c.Count} t)`);
      if (matches.length > 0) {
        notes.push((operations && operations.cargoColonisationMatches)
          ? operations.cargoColonisationMatches.replace('{0}', matches.join(', '))
          : `★ ${matches.join(', ')}`);
      }
    }
    cargoProfitEl.textContent = notes.join('  |  ');
    cargoProfitEl.hidden = notes.length === 0;
  }

  // Missions grouped by system + missing cargo warning
  const mGroupEl = document.getElementById('webMissionsGrouped');
  const mWarnEl = document.getElementById('webMissionsCargoWarning');
  if (mGroupEl && mWarnEl) {
    const missions = Array.isArray(s.ActiveMissions) ? s.ActiveMissions : [];
    const bySys = new Map();
    const reqByComm = new Map();
    for (const m of missions) {
      if (m.DestinationSystem) {
        const k = m.DestinationSystem;
        const prev = bySys.get(k) || { count: 0, cr: 0, tons: 0 };
        prev.count++;
        prev.cr += m.Reward || 0;
        if (m.Commodity && m.Count) prev.tons += m.Count;
        bySys.set(k, prev);
      }
      if (m.Commodity && m.Count > 0 && m.PassengerMission !== true) {
        const ck = m.Commodity.trim().toLowerCase();
        const prev = reqByComm.get(ck) || { name: m.Commodity.trim(), req: 0 };
        prev.req += m.Count;
        reqByComm.set(ck, prev);
      }
    }
    if (bySys.size > 0) {
      const parts = [...bySys.entries()].sort((a, b) => b[1].count - a[1].count).slice(0, 5)
        .map(([sys, v]) => `${sys} (${v.count} · ${v.cr >= 1e6 ? (v.cr / 1e6).toFixed(1) + 'M CR' : formatCount(v.cr) + ' CR'}${v.tons > 0 ? ' · ' + v.tons + ' t' : ''})`);
      mGroupEl.textContent = (operations && operations.missionsBySystem)
        ? operations.missionsBySystem.replace('{0}', parts.join('  |  '))
        : parts.join('  |  ');
      mGroupEl.hidden = false;
    } else {
      mGroupEl.hidden = true;
    }
    const cargoList = Array.isArray(s.CargoInventory) ? s.CargoInventory : [];
    const missing = [];
    for (const [, info] of reqByComm) {
      const carried = cargoList.filter(c => (c.Name || '').trim().toLowerCase() === info.name.toLowerCase()).reduce((acc, c) => acc + (c.Count || 0), 0);
      if (carried < info.req) missing.push(`${info.name} (${carried}/${info.req} t)`);
    }
    if (missing.length > 0) {
      mWarnEl.textContent = (operations && operations.missionsMissingCargo)
        ? operations.missionsMissingCargo.replace('{0}', missing.slice(0, 4).join(', '))
        : `⚠ ${missing.slice(0, 4).join(', ')}`;
      mWarnEl.hidden = false;
    } else {
      mWarnEl.hidden = true;
    }
  }

  // Engineering Wishlist (live + interactive blueprint planner)
  const wishRows = Array.isArray(payload.engineeringWishlist) ? payload.engineeringWishlist : [];
  const bpSel = document.getElementById('webEngineeringBlueprint');
  if (bpSel && Array.isArray(payload.engineeringCatalog) && bpSel.options.length === 0) {
    bpSel.innerHTML = payload.engineeringCatalog.map(b => `<option value="${escapeHtml(b.Id || b.id || '')}">${escapeHtml(b.DisplayName || b.displayName || '')}</option>`).join('');
  }
  renderTableRows('webWishlistTableBody', wishRows, [
    r => r.MaterialName,
    r => r.Category,
    r => formatCount(r.Required),
    r => formatCount(r.Owned),
    r => r.StatusDisplay
  ], '');

  // Mining filter sync
  const mTargetsEl = document.getElementById('webMiningTargets');
  if (mTargetsEl && document.activeElement !== mTargetsEl && typeof payload.miningTargets === 'string') {
    mTargetsEl.value = payload.miningTargets;
  }
  const mPctEl = document.getElementById('webMiningMinPercent');
  if (mPctEl && document.activeElement !== mPctEl && typeof payload.miningMinPercent === 'number') {
    mPctEl.value = String(payload.miningMinPercent);
  }

  // Market colonisation summary
  const mktColEl = document.getElementById('webMarketColonisationSummary');
  if (mktColEl) {
    const mktItems = (s.Market && Array.isArray(s.Market.Items)) ? s.Market.Items : [];
    const colMatches = mktItems.filter(m => m.IsColonisationResource && m.Stock > 0);
    if (colMatches.length > 0) {
      mktColEl.textContent = `★ Colonisation supplies in stock here (${colMatches.length}): ${colMatches.slice(0, 5).map(m => `${m.DisplayName} (${formatCount(m.Stock)})`).join(', ')}`;
      mktColEl.hidden = false;
    } else {
      mktColEl.hidden = true;
    }
  }

  // Frontier cAPI Stored Ships, Community Goals & Dashboard summary
  const ships = Array.isArray(s.StoredShips) ? s.StoredShips : [];
  const cgs = Array.isArray(s.CommunityGoals) ? s.CommunityGoals : [];
  const storedSec = document.getElementById('webStoredShipsSection');
  if (storedSec) {
    storedSec.hidden = ships.length === 0;
    if (ships.length > 0) {
      renderTableRows('webStoredShipsTableBody', ships, [
        x => x.ShipType,
        x => x.ShipName || '---',
        x => x.StarSystem || '---',
        x => x.StationName || '---',
        x => x.ValueDisplay || `${formatCount(x.Value)} CR`
      ], '');
    }
  }
  const storedModsSec = document.getElementById('webStoredModulesSection');
  const storedMods = Array.isArray(s.StoredModules) ? s.StoredModules : [];
  if (storedModsSec) {
    storedModsSec.hidden = storedMods.length === 0;
    if (storedMods.length > 0) {
      renderTableRows('webStoredModulesTableBody', storedMods, [
        m => m.DisplayName || m.Name,
        m => m.Engineering || '—',
        m => m.StarSystem || '---',
        m => m.StationName || '---',
        m => m.PriceDisplay || '—'
      ], '');
    }
  }
  const cgSec = document.getElementById('webCommunityGoalsSection');
  if (cgSec) {
    cgSec.hidden = cgs.length === 0;
    if (cgs.length > 0) {
      renderTableRows('webCommunityGoalsTableBody', cgs, [
        g => g.Title,
        g => g.StarSystem || '---',
        g => g.StationName || '---',
        g => g.ProgressDisplay || formatCount(g.CurrentTotal),
        g => g.ExpiryDisplay || '---'
      ], '');
    }
  }
  const capiCard = document.getElementById('webCapiSummaryCard');
  const capiBody = document.getElementById('webCapiSummary');
  if (capiCard && capiBody) {
    if (ships.length > 0 || cgs.length > 0) {
      const parts = [];
      if (ships.length > 0) parts.push(`Stored Ships: ${ships.length} (${ships.slice(0, 4).map(x => x.ShipType + (x.StarSystem ? ' @ ' + x.StarSystem : '')).join(', ')})`);
      if (cgs.length > 0) parts.push(`Community Goals: ${cgs.map(g => `${g.Title} [${g.StarSystem}]`).join(' · ')}`);
      capiBody.textContent = parts.join('  |  ');
      capiCard.hidden = false;
    } else {
      capiCard.hidden = true;
    }
  }

  const healthCard = document.getElementById('webServiceHealthCard');
  const healthStrip = document.getElementById('webServiceHealthStrip');
  if (healthCard && healthStrip && Array.isArray(payload.serviceHealth)) {
    const enabledHealth = payload.serviceHealth.filter(h => h.Enabled);
    if (enabledHealth.length > 0) {
      const ledColor = st => st === 'ok' ? 'var(--hud-ok)' : st === 'error' ? 'var(--hud-danger)' : 'var(--hud-secondary)';
      healthStrip.replaceChildren();
      enabledHealth.forEach(h => {
        const pill = document.createElement('span');
        pill.title = `${h.Name}: ${h.Detail}`;
        pill.style.cssText = `display:inline-flex;align-items:center;gap:5px;padding:3px 8px;border:1px solid ${ledColor(h.Status)};border-radius:4px;font-size:0.78rem;background:rgba(255,255,255,0.04);`;
        const dot = document.createElement('span');
        dot.textContent = '●';
        dot.style.color = ledColor(h.Status);
        const lbl = document.createElement('span');
        lbl.textContent = `${h.Name}`;
        lbl.style.fontWeight = '700';
        const det = document.createElement('span');
        det.textContent = h.Detail ? `· ${h.Detail}` : '';
        det.style.color = 'var(--hud-muted)';
        pill.append(dot, lbl, det);
        healthStrip.append(pill);
      });
      healthCard.hidden = false;
    } else {
      healthCard.hidden = true;
    }
  }

  // GalNet News (tab-activity -> galnet)
  const galnetTbody = document.getElementById('webGalnetTableBody');
  const galnetBodyEl = document.getElementById('webGalnetBody');
  const articles = Array.isArray(payload.galnetArticles) ? payload.galnetArticles : [];
  if (articles.length === 0 || selectedGalnetIndex >= articles.length) {
    selectedGalnetIndex = 0;
  }
  if (galnetTbody) {
    galnetTbody.innerHTML = '';
    if (articles.length === 0) {
      const tr = document.createElement('tr');
      const td = document.createElement('td');
      td.colSpan = 2;
      td.style.color = 'var(--hud-muted)';
      td.textContent = '---';
      tr.appendChild(td);
      galnetTbody.appendChild(tr);
    } else {
      articles.forEach((art, idx) => {
        const tr = document.createElement('tr');
        tr.style.cursor = 'pointer';
        tr.classList.toggle('current-system', idx === selectedGalnetIndex);
        tr.onclick = () => {
          selectedGalnetIndex = idx;
          galnetTbody.querySelectorAll('tr').forEach((row, rowIdx) => row.classList.toggle('current-system', rowIdx === idx));
          if (galnetBodyEl) galnetBodyEl.textContent = `${art.Title}\n(${art.Date})\n\n${art.Body || ''}`;
        };
        const tdDate = document.createElement('td');
        tdDate.textContent = art.Date || '';
        const tdTitle = document.createElement('td');
        tdTitle.textContent = art.Title || '';
        tr.appendChild(tdDate);
        tr.appendChild(tdTitle);
        galnetTbody.appendChild(tr);
        if (idx === selectedGalnetIndex && galnetBodyEl && !galnetBodyEl.textContent) {
          galnetBodyEl.textContent = `${art.Title}\n(${art.Date})\n\n${art.Body || ''}`;
        }
      });
    }
  }

  function renderAlerts(payload) {
    const alerts = payload.alerts || [];
    const activeAlerts = payload.activeAlerts || [];
    const activeBanner = document.getElementById('activeAlertBanner');
    if (activeAlerts.length > 0) {
      const current = activeAlerts[0];
      const severityName = ['information', 'warning', 'critical'][current.Severity] || 'information';
      activeBanner.className = `alert-${severityName}`;
      const args = activeAlerts.length === 1 ? [current.Title] : [activeAlerts.length, current.Title];
      const template = activeAlerts.length === 1 ? coreAlertTranslations.activeOne : coreAlertTranslations.activeMany;
      document.getElementById('activeAlertBannerTitle').textContent = template.replace(/\{(\d+)\}/g, (_, index) => args[Number(index)]);
      document.getElementById('activeAlertBannerMessage').textContent = current.Message || '';
      activeBanner.hidden = false;
    } else {
      activeBanner.hidden = true;
    }

    const list = document.getElementById('alertList');
    list.replaceChildren();
    const currentToast = alerts.find(alert => alert.Id === alertToastId);
    if (currentToast && document.getElementById('alertToast').classList.contains('show')) {
      document.getElementById('alertToastTitle').textContent = currentToast.Title || inputTranslations.alertFallback;
      document.getElementById('alertToastMessage').textContent = currentToast.Message;
    }
    if (alerts.length === 0) {
      list.textContent = dashboardTranslations.noAlerts;
      hasAlertSnapshot = true;
      return;
    }

    alerts.slice(0, 8).forEach(alert => {
      const item = document.createElement('div');
      const severity = ['information', 'warning', 'critical'][alert.Severity] || 'information';
      item.className = `alert-item alert-${severity}`;
      const heading = document.createElement('div');
      heading.className = 'alert-item-title';
      heading.textContent = alert.Title || inputTranslations.alertFallback;
      const time = document.createElement('span');
      time.className = 'alert-time';
      time.textContent = new Date(alert.Timestamp).toLocaleTimeString(sectionLanguage);
      heading.append(time);
      const message = document.createElement('div');
      message.className = 'alert-item-message';
      message.textContent = alert.Message || '';
      item.append(heading, message);
      list.append(item);

      if (!seenAlertIds.has(alert.Id)) {
        seenAlertIds.add(alert.Id);
        if (hasAlertSnapshot && alerts[0].Id === alert.Id)
          showAlertToast(alert);
      }
    });
    hasAlertSnapshot = true;
  }

  function showAlertToast(alert) {
    alertToastId = alert.Id;
    document.getElementById('alertToastTitle').textContent = alert.Title || inputTranslations.alertFallback;
    document.getElementById('alertToastMessage').textContent = alert.Message || '';
    const toast = document.getElementById('alertToast');
    const severity = ['information', 'warning', 'critical'][alert.Severity] || 'information';
    toast.className = `alert-${severity}`;
    toast.classList.add('show');
    clearTimeout(alertToastTimer);
    alertToastTimer = setTimeout(() => { toast.classList.remove('show'); alertToastId = null; }, 12000);
  }

  if (s.ThemeSecondaryHex) {
    document.documentElement.style.setProperty('--hud-secondary', s.ThemeSecondaryHex);
  }

  function applyNavigationMenuPosition(position) {
    const workspace = document.getElementById('workspace');
    const allowedPositions = ['left', 'right', 'top', 'bottom'];
    const safePosition = allowedPositions.includes(position) ? position : 'left';
    workspace.classList.remove(...allowedPositions.map(value => `layout-${value}`));
    workspace.classList.add(`layout-${safePosition}`);
  }

  const hasSessionData = s.CommanderName && s.CommanderName !== 'CMDR DESCONOCIDO';
  const text = dashboardTranslations;
  const shipName = !hasSessionData && s.ShipName === 'SIN SEÑAL' ? text.noSignal : (s.ShipName || '---');
  const oneDecimal = value => Number(value).toLocaleString(sectionLanguage, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  renderHeaderModules(payload.headerModules);
  document.getElementById('shipTitle').textContent = `${shipName} [${s.ShipIdent || '---'}]`;
  document.getElementById('shipSubHeader').textContent = `${s.ShipType || '---'} // ${text.profile}: ${payload.presetName || 'ED'}`;
  const modeLabel = s.IsGameDetected ? text.online : text.inactive;
  document.getElementById('modeBadge').textContent = modeLabel;
  document.getElementById('simBadge').textContent = modeLabel;
  document.getElementById('dashCommander').textContent = hasSessionData ? s.CommanderName : text.unknownCommander;
  document.getElementById('dashShip').textContent = `${shipName} [${s.ShipIdent || '---'}] · ${s.ShipType || '---'}`;
  document.getElementById('dashSystem').textContent = s.CurrentSystem || '---';
  document.getElementById('dashLocation').textContent = s.DestinationName
    ? `${text.destination}: ${s.DestinationName}`
    : s.StationName
      ? `${text.station}: ${s.StationName}`
      : s.BodyName
        ? `${text.body}: ${s.BodyName}`
        : hasSessionData ? text.deepSpace : '---';
  const fuelSummary = s.HasFuelCapacity && s.FuelMax > 0
    ? `${oneDecimal(s.FuelMain || 0)}/${oneDecimal(s.FuelMax)} T (${oneDecimal(Math.max(0, Math.min(100, s.FuelMain / s.FuelMax * 100)))}%)`
    : s.HasStatusTelemetry
      ? `${oneDecimal(s.FuelMain || 0)} T (${text.capacityUnconfirmed})`
      : '---';
  const hullSummary = s.HasHullTelemetry
    ? `${text.hull} ${oneDecimal(Math.max(0, Math.min(100, s.HullHealth || 0)))}%`
    : `${text.hull} ---`;
  document.getElementById('dashResources').textContent = `${hullSummary} · ${text.fuel} ${fuelSummary}`;
  document.getElementById('dashCargo').textContent = s.CargoCapacity > 0
    ? `${text.cargo} ${s.CargoUsed || 0} / ${s.CargoCapacity} T`
    : hasSessionData ? `${text.cargo} ${s.CargoUsed || 0} T` : `${text.cargo} ---`;
  const waypoints = s.NavRoute || [];
  const currentWaypointIndex = waypoints.findIndex(waypoint => waypoint.IsCurrentSystem);
  const nextWaypoint = currentWaypointIndex >= 0
    ? waypoints[currentWaypointIndex + 1]
    : waypoints.find(waypoint => !waypoint.IsCurrentSystem);
  const remainingJumps = Math.max(0, waypoints.length - 1 - Math.max(0, currentWaypointIndex));
  document.getElementById('dashRoute').textContent = nextWaypoint
    ? `${nextWaypoint.StarSystem} · ${oneDecimal(nextWaypoint.StepDistanceLy)} ${text.lightYears} · ${text.remainingJumps}: ${remainingJumps}`
    : text.noNextJump;
  const recentEvents = view.journalEvents;
  document.getElementById('dashActivity').textContent = recentEvents.length > 0
    ? `${recentEvents[0].Timestamp} · ${recentEvents[0].EventType}: ${recentEvents[0].Summary}`
    : text.waitingEvents;

  const hull = Math.max(0, Math.min(100, s.HullHealth || 0));
  document.getElementById('lblHull').textContent = s.HasHullTelemetry ? oneDecimal(hull) + '%' : '---';
  document.getElementById('barHull').style.width = hull + '%';
  const shieldBarTrack = document.getElementById('shieldBarTrack');
  const hasShieldTelemetry = s.HasShieldTelemetry === true;
  document.getElementById('lblShield').textContent = !hasShieldTelemetry
    ? '---'
    : s.HasLoadoutTelemetry && !s.HasShieldGenerator
      ? flightTranslations.noGenerator
      : s.ShieldsUp ? flightTranslations.shieldUp : flightTranslations.shieldDown;
  shieldBarTrack.style.display = hasShieldTelemetry ? '' : 'none';
  document.getElementById('barShield').style.width = s.ShieldsUp ? '100%' : '0%';

  const hasFuelCapacity = s.HasFuelCapacity && s.FuelMax > 0;
  const fuelMax = hasFuelCapacity ? s.FuelMax : 0;
  const fuelPct = hasFuelCapacity ? Math.max(0, Math.min(100, (s.FuelMain / fuelMax) * 100)) : 0;
  document.getElementById('lblFuel').textContent = hasFuelCapacity
    ? `${oneDecimal(s.FuelMain || 0)} / ${oneDecimal(fuelMax)} T · ${oneDecimal(fuelPct)}%`
    : s.HasStatusTelemetry
      ? `${oneDecimal(s.FuelMain || 0)} T · ${text.capacityUnconfirmed}`
      : '---';
  document.getElementById('barFuel').style.width = fuelPct + '%';

  const cargoCap = s.CargoCapacity;
  const cargoPct = cargoCap > 0 ? Math.max(0, Math.min(100, ((s.CargoUsed || 0) / cargoCap) * 100)) : 0;
  document.getElementById('lblCargo').textContent = cargoCap > 0
    ? `${s.CargoUsed || 0} / ${cargoCap} T`
    : s.HasStatusTelemetry ? `${s.CargoUsed || 0} T · ${text.capacityUnconfirmed}` : '---';
  document.getElementById('barCargo').style.width = cargoPct + '%';

  document.getElementById('pipSys').textContent = s.HasStatusTelemetry ? oneDecimal(s.PipsSys) : '---';
  document.getElementById('pipEng').textContent = s.HasStatusTelemetry ? oneDecimal(s.PipsEng) : '---';
  document.getElementById('pipWep').textContent = s.HasStatusTelemetry ? oneDecimal(s.PipsWep) : '---';

  const copilotItems = [];
  if (s.JumpTargetSystem) {
    const starNote = s.JumpTargetIsHazardous
      ? flightTranslations.copilotJumpHazard
      : s.JumpTargetIsScoopable
        ? flightTranslations.copilotJumpScoopable
        : flightTranslations.copilotJumpNonScoopable;
    copilotItems.push(flightTranslations.copilotJumpTarget
      .replace('{0}', s.JumpTargetSystem)
      .replace('{1}', s.JumpTargetStarClass || '')
      .replace('{2}', starNote));
  }
  if (typeof s.ApproachBodyGravityG === 'number' && s.ApproachBodyGravityG >= 1.5) {
    copilotItems.push(flightTranslations.copilotHighGravity.replace('{0}', oneDecimal(s.ApproachBodyGravityG)));
  }
  if (s.HasLimpetController) {
    copilotItems.push(s.LimpetCount === 0
      ? flightTranslations.copilotNoLimpets
      : flightTranslations.copilotLimpets.replace('{0}', s.LimpetCount));
  }
  if (typeof s.FsdHealthPercent === 'number') {
    if (s.FsdHealthPercent < 80)
      copilotItems.push(flightTranslations.copilotFsdWear.replace('{0}', Math.round(s.FsdHealthPercent)));
    else if (s.IsFsdSupercharged)
      copilotItems.push(flightTranslations.copilotFsdSupercharged.replace('{0}', Math.round(s.FsdHealthPercent)));
  }
  if (s.SupercruiseOverdriveActive) copilotItems.push(flightTranslations.copilotScoActive);
  if (!s.Docked && !s.Landed && !s.Supercruise && s.HasStatusTelemetry) {
    const blockers = [];
    if (s.LandingGearDown) blockers.push(flightTranslations.gear);
    if (s.CargoScoopDeployed) blockers.push(flightTranslations.scoop);
    if (s.HardpointsDeployed) blockers.push(flightTranslations.hardpoints);
    if (blockers.length > 0) copilotItems.push(flightTranslations.copilotBlockers.replace('{0}', blockers.join(', ')));
  }
  document.getElementById('copilotTacticalStatus').textContent = copilotItems.length > 0
    ? copilotItems.join('  ·  ')
    : flightTranslations.copilotNominal;

  const targetBox = document.getElementById('webTargetCard');
  const target = s.CurrentTarget;
  if (target && target.Ship) {
    const rankPart = target.PilotRank ? ` [${target.PilotRank}]` : '';
    const pilotPart = target.PilotName ? ` · ${target.PilotName}${rankPart}` : '';
    document.getElementById('webTargetHeader').textContent = `${target.Ship}${pilotPart}`;
    const legal = target.IsWanted ? flightTranslations.targetWanted : (target.LegalStatus || flightTranslations.targetClean);
    const bountyPart = target.Bounty > 0 ? ` · ${flightTranslations.targetBounty.replace('{0}', formatCount(target.Bounty))}` : '';
    const factionPart = target.Faction ? ` · ${target.Faction}` : '';
    const stEl = document.getElementById('webTargetStatus');
    stEl.textContent = `${legal}${bountyPart}${factionPart}`;
    stEl.style.color = target.IsWanted ? 'var(--hud-danger)' : 'var(--hud-text)';
    const shTxt = target.ShieldHealth != null ? `${oneDecimal(target.ShieldHealth)}%` : '—';
    const huTxt = target.HullHealth != null ? `${oneDecimal(target.HullHealth)}%` : '—';
    let vitals = flightTranslations.targetShieldHull.replace('{0}', shTxt).replace('{1}', huTxt);
    if (target.Subsystem) {
      const subH = target.SubsystemHealth != null ? `${oneDecimal(target.SubsystemHealth)}%` : '—';
      vitals += ` · ${flightTranslations.targetSubsystem.replace('{0}', target.Subsystem).replace('{1}', subH)}`;
    }
    document.getElementById('webTargetVitals').textContent = vitals;
    targetBox.hidden = false;
  } else {
    targetBox.hidden = true;
  }

  const vehicleBox = document.getElementById('vehicleTelemetryBox');
  if (s.InSRV || s.InFighter) {
    const vParts = [];
    if (s.InSRV) {
      vParts.push(flightTranslations.vehicleSrv);
      vParts.push(`${flightTranslations.srvHandbrake}: ${s.SrvHandbrake ? flightTranslations.active : flightTranslations.inactive}`);
      vParts.push(`${flightTranslations.srvDriveAssist}: ${s.SrvDriveAssist ? flightTranslations.active : flightTranslations.inactive}`);
      vParts.push(`${flightTranslations.srvTurret}: ${s.SrvUsingTurretView ? flightTranslations.active : (s.SrvTurretRetracted ? flightTranslations.srvRetracted : flightTranslations.inactive)}`);
      if (s.SrvHighBeam) vParts.push(flightTranslations.srvHighBeam);
    }
    if (s.InFighter) vParts.push(flightTranslations.vehicleFighter);
    document.getElementById('vehicleStatusText').textContent = vParts.join('  ·  ');
    vehicleBox.hidden = false;
  } else {
    vehicleBox.hidden = true;
  }

  const shipModules = s.ShipModules || [];
  const totalPower = shipModules.filter(m => m.On && m.PowerDrawMw > 0).reduce((a, m) => a + m.PowerDrawMw, 0);
  const byPrio = [1, 2, 3, 4, 5].map(p => {
    const pw = shipModules.filter(m => m.On && m.Priority === p && m.PowerDrawMw > 0).reduce((a, m) => a + m.PowerDrawMw, 0);
    return pw > 0 ? `P${p}: ${pw.toFixed(2)} MW (${totalPower > 0 ? Math.round(pw / totalPower * 100) : 0}%)` : null;
  }).filter(Boolean);
  document.getElementById('modulesPowerSummary').textContent = shipModules.length === 0
    ? flightTranslations.modulesWaiting
    : (byPrio.length > 0
      ? flightTranslations.modulesPowerSummary.replace('{0}', totalPower.toFixed(2)).replace('{1}', byPrio.join(' · '))
      : flightTranslations.modulesCount.replace('{0}', shipModules.length));
  const ammoList = shipModules.filter(m => m.AmmoInClip != null || m.AmmoInHopper != null).slice(0, 8).map(m => `${m.DisplayName}: ${m.AmmoDisplay}`);
  document.getElementById('modulesAmmoSummary').textContent = ammoList.length > 0
    ? flightTranslations.modulesAmmoSummary.replace('{0}', ammoList.join(' · '))
    : '';
  renderTableRows('shipModulesTableBody', shipModules, [
    m => m.Slot, m => m.DisplayName, m => m.HealthDisplay, m => m.PriorityDisplay, m => m.PowerDisplay, m => m.AmmoDisplay, m => m.Engineering || '—'
  ], flightTranslations.modulesWaiting);

  const shipLocker = s.ShipLockerInventory || [];
  window.__lastShipLocker = shipLocker;
  document.getElementById('shipLockerStatus').textContent = shipLocker.length === 0
    ? operations.shipLockerWaiting
    : formatOperation('shipLockerCount', shipLocker.length);
  renderWebShipLockerFilter();

  const engineers = s.Engineers || [];
  document.getElementById('engineersStatus').textContent = engineers.length === 0
    ? operations.engineersWaiting
    : formatOperation('engineersCount', engineers.length);
  renderTableRows('engineersTableBody', engineers, [
    e => e.Name, e => e.Progress || '—', e => e.RankDisplay || '—'
  ], operations.engineersWaiting);

  const asteroid = s.LastProspectedAsteroid;
  document.getElementById('webProspectorSummary').textContent = asteroid
    ? `${asteroid.Timestamp} · ${asteroid.SummaryDisplay} (${oneDecimal(asteroid.Remaining)}%)`
    : operations.prospectorWaiting;
  const mining = s.MiningSession || {};
  const nonLimpet = Math.max(0, (s.CargoUsed || 0) - (s.LimpetCount || 0));
  const fillPct = s.CargoCapacity > 0 ? Math.min(100, Math.round(nonLimpet * 100 / s.CargoCapacity)) : 0;
  document.getElementById('webMiningSessionStats').textContent = operations.miningSessionSummary
    .replace('{0}', mining.TotalRefinedTons || 0)
    .replace('{1}', oneDecimal(mining.TonsPerHour || 0))
    .replace('{2}', mining.AsteroidsProspected || 0)
    .replace('{3}', fillPct);
  renderTableRows('miningRefinedTableBody', mining.RefinedByMineral || [], [
    m => m.Mineral, m => formatCount(m.Tons)
  ], operations.prospectorWaiting);

  const combat = s.CombatSession || {};
  document.getElementById('webCombatSessionStats').textContent = operations.combatSessionSummary
    .replace('{0}', formatCount(combat.TotalKills || 0))
    .replace('{1}', formatCount(combat.PendingCredits || 0))
    .replace('{2}', formatCount(combat.CreditsPerHour || 0));
  renderTableRows('combatFactionsTableBody', combat.ByFaction || [], [
    f => f.Faction, f => formatCount(f.Kills), f => `${formatCount(f.PendingCredits)} CR`
  ], operations.massacreEmpty);
  const stacks = s.MassacreStacks || [];
  if (stacks.length === 0) {
    document.getElementById('webMassacreSummary').textContent = operations.massacreEmpty;
  } else {
    const totalM = stacks.reduce((a, m) => a + (m.MissionCount || 0), 0);
    const byTarget = {};
    stacks.forEach(m => {
      const k = (m.TargetFaction || '').toLowerCase();
      byTarget[k] = Math.max(byTarget[k] || 0, m.RequiredKills || 0);
    });
    const effKills = Object.values(byTarget).reduce((a, b) => a + b, 0);
    const totalR = stacks.reduce((a, m) => a + (m.TotalReward || 0), 0);
    document.getElementById('webMassacreSummary').textContent = operations.massacreSummary
      .replace('{0}', formatCount(totalM))
      .replace('{1}', formatCount(effKills))
      .replace('{2}', formatCount(totalR));
  }
  renderTableRows('massacreStacksTableBody', stacks, [
    m => m.IssuingFaction, m => m.TargetFaction, m => formatCount(m.MissionCount), m => formatCount(m.RequiredKills), m => `${formatCount(m.TotalReward)} CR`
  ], operations.massacreEmpty);

  const sess = s.SessionStats || {};
  const netSign = (sess.NetCredits || 0) >= 0 ? '+' : '';
  const dashT = payload.dashboardTranslations || {};
  if (dashT.sessionStatsSummary) {
    document.getElementById('webSessionStatsSummary').textContent = dashT.sessionStatsSummary
      .replace('{0}', `${netSign}${formatCount(sess.NetCredits || 0)}`)
      .replace('{1}', formatCount(sess.CreditsPerHour || 0))
      .replace('{2}', formatCount(sess.SystemsVisited || 0))
      .replace('{3}', oneDecimal(sess.TotalDistanceLy || 0))
      .replace('{4}', formatCount(sess.BodiesScanned || 0))
      .replace('{5}', formatCount(sess.OrganicsAnalysed || 0))
      .replace('{6}', formatCount(sess.MissionsCompleted || 0));
  }

  const noteCard = document.getElementById('webSystemNoteCard');
  if (noteCard) {
    noteCard.hidden = false;
  }

  document.getElementById('webPowerplaySummary').textContent = s.PowerplaySummary
    ? flightTranslations.bgsPowerplay.replace('{0}', s.PowerplaySummary)
    : flightTranslations.bgsNoPowerplay;
  renderTableRows('webSystemFactionsTableBody', s.SystemFactions || [], [
    f => f.DisplayName || f.Name, f => f.InfluenceDisplay, f => f.FactionState || '—', f => f.Government || '—', f => f.ReputationDisplay
  ], flightTranslations.bgsNoPowerplay);

  const routeLy = parseFloat(document.getElementById('webCarrierRouteLy')?.value);
  const tritEl = document.getElementById('webCarrierTritiumResult');
  if (Number.isFinite(routeLy) && routeLy > 0) {
    const cap = s.Carrier?.Capacity || 0, free = s.Carrier?.FreeSpace || 0;
    const usedCapacity = cap > 0 ? Math.max(0, cap - free) : (s.Carrier?.Cargo || 0);
    const fuel = Math.max(0, s.Carrier?.Fuel || 0);
    const initialExtraMass = usedCapacity + fuel;
    const jumps = Math.ceil(routeLy / 500);
    let rem = routeLy, totalT = 0, simFuel = fuel;
    for (let i = 0; i < jumps; i++) {
      const leg = Math.min(500, rem);
      rem -= leg;
      const stepCost = Math.round(5 + (leg / 4) * (1 + (usedCapacity + simFuel) / 25000));
      totalT += stepCost;
      simFuel = Math.max(0, simFuel - stepCost);
    }
    let depotJumps = 0, depotFuel = fuel;
    while (depotFuel > 0) {
      const legCost = Math.round(5 + (500 / 4) * (1 + (usedCapacity + depotFuel) / 25000));
      if (depotFuel < legCost) break;
      depotFuel -= legCost;
      depotJumps++;
    }
    tritEl.textContent = flightTranslations.tritiumResult
      .replace('{0}', jumps)
      .replace('{1}', formatCount(totalT))
      .replace('{2}', formatCount(fuel))
      .replace('{3}', depotJumps)
      .replace('{4}', formatCount(initialExtraMass));
  } else {
    tritEl.textContent = flightTranslations.tritiumPrompt;
  }

  const fcMats = s.CarrierMaterials || [];
  document.getElementById('webCarrierMaterialsStatus').textContent = fcMats.length === 0
    ? flightTranslations.fcMaterialsWaiting
    : flightTranslations.fcMaterialsCount.replace('{0}', s.CarrierMaterialsStation || 'Carrier').replace('{1}', fcMats.length);
  renderTableRows('webCarrierMaterialsTableBody', fcMats, [
    m => m.DisplayName, m => `${formatCount(m.Price)} CR`, m => formatCount(m.Stock), m => formatCount(m.Demand)
  ], flightTranslations.fcMaterialsWaiting);

  document.getElementById('surfaceLiveCoords').textContent = s.HasLatLong
    ? `${s.BodyName || s.CurrentSystem || '---'} · LAT: ${Number(s.Latitude).toFixed(4)}° · LON: ${Number(s.Longitude).toFixed(4)}° · ALT: ${oneDecimal(s.Altitude)} m · ${flightTranslations.heading}: ${Math.round(s.Heading)}°`
    : flightTranslations.noSurface;
  updateWebSurfaceGuidance(s);
  renderTableRows('surfaceEventsTableBody', s.RecentSurfaceEvents || [], [
    e => e.Timestamp, e => e.EventType, e => e.BodyName || '—', e => e.Details || '—', e => e.CoordinatesDisplay || '—'
  ], flightTranslations.noSurface);

  const matrixKey = `${sectionLanguage}:${s.RawFlags}`;
  if (matrixKey !== lastStatusMatrixKey) {
    lastStatusMatrixKey = matrixKey;
    const flags = [
      { name: flightTranslations.gear, on: s.LandingGearDown },
      { name: flightTranslations.lights, on: s.LightsOn },
      { name: flightTranslations.nightVision, on: s.NightVision },
      { name: flightTranslations.scoop, on: s.CargoScoopDeployed },
      { name: flightTranslations.hardpoints, on: s.HardpointsDeployed },
      { name: flightTranslations.supercruiseStatus, on: s.Supercruise },
      { name: flightTranslations.flightAssist, on: s.FlightAssistOff, danger: true },
      { name: flightTranslations.analysisMode, on: s.AnalysisMode },
      { name: flightTranslations.silentRunning, on: s.SilentRunning, danger: true },
      { name: flightTranslations.shieldsOnline, on: s.ShieldsUp },
      { name: flightTranslations.massLock, on: s.FsdMassLocked, danger: true },
      { name: flightTranslations.docked, on: s.Docked }
    ];

    const matrixEl = document.getElementById('statusMatrix');
    matrixEl.innerHTML = flags.map(f => `
        <div class="flag-btn ${f.on ? 'on' : ''} ${f.danger ? 'danger' : ''}">
          <div style="font-weight:700;font-size:0.84rem;">${f.name}</div>
          <div class="state-dot">${f.on ? '● ' + flightTranslations.active : '○ ' + flightTranslations.inactive}</div>
        </div>
      `).join('');
  }

  // Navegación
  document.getElementById('navSys').textContent = s.CurrentSystem || '---';
  document.getElementById('navSec').textContent = s.SecurityLevel || '---';
  document.getElementById('navEco').textContent = s.Economy || '---';
  document.getElementById('navAll').textContent = s.AllegianceIsFallback ? flightTranslations.independent : (s.Allegiance || '---');
  document.getElementById('navBody').textContent = s.StationName || s.BodyName || '---';
  const surfaceNumber = (value, digits) => value.toLocaleString(sectionLanguage, {
    minimumFractionDigits: digits, maximumFractionDigits: digits, useGrouping: false
  });
  document.getElementById('navCoords').textContent = s.HasLatLong
    ? `LAT: ${surfaceNumber(s.Latitude, 4)}° | LON: ${surfaceNumber(s.Longitude, 4)}° | ALT: ${surfaceNumber(s.Altitude, 1)} m | ${flightTranslations.heading}: ${surfaceNumber(s.Heading, 0)}°`
    : flightTranslations.noSurface;

  const route = s.NavRoute || [];
  let routeLabel = route.length > 0 ? flightTranslations.remaining.replace('{0}', Math.max(0, route.length - 1)) : flightTranslations.noRoute;
  if (route.length > 1) {
    if (s.RouteHasNoFuelStar)
      routeLabel += '  ·  ⚠ ' + flightTranslations.routeNoFuelStar;
    else if (typeof s.JumpsToNextFuelStar === 'number' && s.JumpsToNextFuelStar > 1)
      routeLabel += '  ·  ' + flightTranslations.routeNextFuelStar.replace('{0}', s.JumpsToNextFuelStar);
  }
  document.getElementById('routeCount').textContent = routeLabel;
  renderTableRows('routeTableBody', route, [
    r => r.Index, r => r.StarSystem, r => r.StarClass, r => r.IsFuelStar ? '✓' : '✕',
    r => `${oneDecimal(r.StepDistanceLy)} ${text.lightYears}`,
    r => `${oneDecimal(r.DistanceFromStartLy)} ${text.lightYears}`
  ], flightTranslations.noRouteFile);
  route.forEach((r, index) => {
    const row = document.getElementById('routeTableBody').rows[index];
    row.classList.toggle('current-system', !!r.IsCurrentSystem);
    [0, 2, 3].forEach(column => row.cells[column].classList.add('route-centered'));
    const indicator = document.createElement('span');
    indicator.className = `route-fuel-indicator ${r.IsFuelStar ? 'route-fuel-yes' : 'route-fuel-no'}`;
    indicator.textContent = r.IsFuelStar ? '✓' : '✕';
    row.cells[3].replaceChildren(indicator);
  });
  if (route.length === 0)
    document.querySelector('#routeTableBody td').style.color = 'var(--hud-muted)';

  // Bodega
  const cargo = s.CargoInventory || [];
  renderTableRows('cargoTableBody', cargo, [
    item => item.DisplayName,
    item => `${formatCount(item.Count)} T`,
    (_, index) => view.cargoStates[index]
  ], operations.cargoEmpty);
  cargo.forEach((item, index) => {
    const row = document.getElementById('cargoTableBody').rows[index];
    if (!row) return;
    row.cells[1].style.fontWeight = '700';
    row.cells[1].style.color = 'var(--hud-accent)';
    if (item.Stolen > 0) row.cells[2].style.color = 'var(--hud-danger)';
  });

  renderMaterials(s);

  const missionsBody = document.getElementById('missionsTableBody');
  missionsBody.replaceChildren();
  const missions = s.ActiveMissions || [];
  document.getElementById('missionsStatus').textContent = missions.length === 0
    ? operations.missionsEmpty
    : formatOperation('missionsWeb', missions.length, payload.missionExpiryWarningHours ?? 24, payload.missionExpiryCriticalHours ?? 1);
  if (missions.length === 0) {
    const row = document.createElement('tr');
    const cell = document.createElement('td');
    cell.colSpan = 6;
    cell.textContent = operations.missionsNone;
    row.append(cell);
    missionsBody.append(row);
  } else {
    missions.forEach((mission, index) => {
      const row = document.createElement('tr');
      const expiry = mission.Expiry ? new Date(mission.Expiry) : null;
      const remainingMs = expiry ? expiry.getTime() - Date.now() : null;
      const values = [
        view.missions[index].name,
        mission.DestinationSystem,
        mission.DestinationStation,
        mission.ObjectiveDisplay,
        view.missions[index].expiry,
        view.missions[index].remaining
      ];
      values.forEach(value => {
        const cell = document.createElement('td');
        cell.textContent = value ?? '';
        row.append(cell);
      });
      if (remainingMs !== null && remainingMs <= (payload.missionExpiryCriticalHours ?? 1) * 3600000)
        row.style.color = 'var(--hud-danger)';
      else if (remainingMs !== null && remainingMs <= (payload.missionExpiryWarningHours ?? 24) * 3600000)
        row.style.color = 'var(--hud-secondary)';
      missionsBody.append(row);
    });
  }

  // Diario
  const events = view.journalEvents;
  const explorationBodies = s.ExplorationBodies || [];
  const exploration = explorationTranslations;
  document.getElementById('explorationBodiesStatus').textContent = explorationBodies.length === 0
    ? exploration.empty
    : exploration.bodyCount.replace('{0}', explorationBodies.length).replace('{1}', explorationBodies[0].SystemName);
  const portfolioEl = document.getElementById('explorationPortfolio');
  const summaryParts = [];
  if (typeof s.SystemTotalBodies === 'number' && s.SystemTotalBodies > 0) {
    summaryParts.push(s.SystemAllBodiesFound
      ? exploration.fssProgressComplete.replace('{0}', s.SystemTotalBodies)
      : exploration.fssProgress.replace('{0}', s.SystemScannedBodies || 0).replace('{1}', s.SystemTotalBodies));
  }
  if ((s.UnsoldCartographyCredits || 0) > 0 || (s.UnsoldExobiologyCredits || 0) > 0) {
    summaryParts.push(exploration.unsoldPortfolio
      .replace('{0:N0}', Number(s.UnsoldCartographyCredits || 0).toLocaleString(sectionLanguage))
      .replace('{1:N0}', Number(s.UnsoldExobiologyCredits || 0).toLocaleString(sectionLanguage)));
  }
  portfolioEl.textContent = summaryParts.join('  ·  ');
  portfolioEl.hidden = summaryParts.length === 0;

  const exoCard = document.getElementById('exobiologyTracker');
  const exo = s.ExobiologyTracker;
  if (exo && exo.SamplesCollected > 0 && exo.SamplesCollected < 3) {
    const pct = exo.RequiredDistanceMeters > 0
      ? Math.max(0, Math.min(100, (exo.CurrentDistanceMeters / exo.RequiredDistanceMeters) * 100))
      : 0;
    document.getElementById('exoTrackerTitle').textContent = exploration.exoTrackerTitle
      .replace('{0}', exo.Species || exploration.organicSpeciesFallback)
      .replace('{1}', exo.SamplesCollected);
    document.getElementById('exoTrackerDist').textContent = (exo.CanSampleNext ? exploration.exoTrackerReady : exploration.exoTrackerDistance)
      .replace('{0:N0}', Math.round(exo.CurrentDistanceMeters || 0).toLocaleString(sectionLanguage))
      .replace('{1:N0}', Number(exo.RequiredDistanceMeters || 500).toLocaleString(sectionLanguage));
    document.getElementById('exoTrackerBar').style.width = pct + '%';
    exoCard.hidden = false;
    renderWebExoRadar(exo);
  } else {
    exoCard.hidden = true;
  }
  renderWebSystemMap(payload.systemMap);
  const explorationBodiesBody = document.getElementById('explorationBodiesTableBody');
  explorationBodiesBody.replaceChildren();
  if (explorationBodies.length === 0) {
    const row = document.createElement('tr');
    const cell = document.createElement('td');
    cell.colSpan = 18;
    cell.textContent = exploration.emptyTable;
    row.append(cell);
    explorationBodiesBody.append(row);
  } else {
    explorationBodies.forEach((body, index) => {
      const row = document.createElement('tr');
      const presentation = view.explorationBodies[index];
      [
        body.HierarchyName || body.BodyName,
        body.BodyType,
        presentation.Radius,
        presentation.Gravity,
        presentation.Temperature,
        body.AtmosphereDisplay,
        presentation.Materials,
        presentation.Composition,
        presentation.AtmosphereComposition,
        presentation.Orbital,
        body.RingsDisplay,
        presentation.Interest,
        body.GenusesDisplay,
        body.AnalysedSpeciesDisplay
      ].forEach(value => {
        const cell = document.createElement('td');
        cell.textContent = value || '—';
        row.append(cell);
      });
      [
        body.WasDiscovered,
        body.WasMapped
      ].forEach((value, index) => {
        const cell = document.createElement('td');
        const indicator = document.createElement('span');
        indicator.className = 'exploration-status';
        indicator.textContent = value === true ? '✓' : value === false ? '✗' : '—';
        indicator.style.color = value === true
          ? 'var(--hud-ok)'
          : value === false
            ? 'var(--hud-danger)'
            : 'var(--hud-muted)';
        indicator.title = index === 0 ? presentation.Discovery : presentation.Mapping;
        cell.append(indicator);
        row.append(cell);
      });
      const estimateCell = document.createElement('td');
      estimateCell.textContent = presentation.Estimate;
      row.append(estimateCell);
      const sourceCell = document.createElement('td');
      sourceCell.textContent = body.DataSource || 'Local';
      if (body.DataSource === 'Spansh') sourceCell.style.color = 'var(--hud-muted)';
      else if (body.DataSource === 'Local+Spansh') sourceCell.style.color = 'var(--hud-ok)';
      row.append(sourceCell);
      explorationBodiesBody.append(row);
    });
  }

  const explorationEvents = new Set([
    'FSSDiscoveryScan',
    'FSSAllBodiesFound',
    'SAAScanComplete',
    'ScanOrganic',
    'CodexEntry',
    'SellExplorationData',
    'MultiSellExplorationData'
  ]);
  const milestones = events.filter(ev => explorationEvents.has(ev.EventType));
  const eventColumns = [ev => ev.Timestamp, ev => ev.EventType, ev => ev.Summary];
  renderTableRows('explorationTableBody', milestones, eventColumns, exploration.milestonesEmpty);
  if (!webViewingHistoricalJournal) {
    webCurrentJournalEvents = events;
    renderTableRows('journalTableBody', events, eventColumns, exploration.journalEmpty);
  }
  if (!webJournalFilesLoaded) {
    webJournalFilesLoaded = true;
    refreshWebJournalFiles();
  }
  for (const id of ['explorationTableBody', 'journalTableBody']) {
    for (const row of document.getElementById(id).rows) {
      if (row.cells.length !== 3) continue;
      row.cells[0].style.fontFamily = 'Consolas,monospace';
      row.cells[0].style.color = 'var(--hud-muted)';
      row.cells[1].style.color = 'var(--hud-accent)';
      row.cells[1].style.fontWeight = '700';
    }
  }

  const autoCtxEl = document.getElementById('webAutoContextDeck');
  if (autoCtxEl && typeof payload.autoContextDeck === 'boolean')
    autoCtxEl.checked = payload.autoContextDeck;

  // Deck de controles
  if (Array.isArray(payload.deckTabs) && payload.deckTabs.length > 0) {
    renderCustomDeckTabs(payload.deckTabs);
  } else if (payload.deckBindings) {
    renderDeck(payload.deckBindings, payload.hideUnassignedBindings);
  }
  document.body.classList.add('localized');
}

const filterCategoryKey = value => value && value.trim() ? value : '\u0001';
const marketCategoryKey = item => filterCategoryKey(item.Category?.trim() ? item.Category : item.DisplayCategory);

function updateFilterCategories(id, entries) {
  const select = document.getElementById(id);
  const selected = select.value;
  const oldLabel = select.selectedOptions[0]?.textContent;
  const categories = new Map();
  entries.forEach(([key, label]) => {
    if (!categories.has(key)) categories.set(key, label || '—');
  });
  if (selected && !categories.has(selected) && oldLabel) categories.set(selected, oldLabel);
  const options = [['', materialTranslations.allCategories], ...Array.from(categories)
    .sort((a, b) => a[1].localeCompare(b[1], sectionLanguage))];
  if (select.options.length === options.length &&
    options.every(([value, label], index) => select.options[index].value === value && select.options[index].textContent === label))
    return;
  select.replaceChildren(...options.map(([value, label]) => new Option(label, value)));
  select.value = selected;
}

function renderMarket(payload) {
  const items = payload.state.Market?.Items || [];
  document.getElementById('marketStatus').textContent = payload.operationsView.marketStatus;
  updateFilterCategories('marketCategoryFilter', items.map(item =>
    [marketCategoryKey(item), item.DisplayCategory]));
  const search = document.getElementById('marketSearch').value.trim().toLocaleLowerCase(sectionLanguage);
  const category = document.getElementById('marketCategoryFilter').value;
  const filtered = items.map((item, index) => ({ item, index })).filter(({ item }) =>
    (!search || [item.Name, item.DisplayName].some(value => (value || '').toLocaleLowerCase(sectionLanguage).includes(search))) &&
    (!category || marketCategoryKey(item) === category));
  document.getElementById('marketFilterStatus').textContent =
    operationsTranslations.filterCount.replace('{0}', filtered.length).replace('{1}', items.length);
  renderTableRows('marketTableBody', filtered, [
    row => row.item.DisplayName,
    row => row.item.DisplayCategory,
    row => `${formatCount(row.item.BuyPrice)} CR`,
    row => `${formatCount(row.item.SellPrice)} CR`,
    row => `${formatCount(row.item.MeanPrice)} CR`,
    row => formatCount(row.item.Stock),
    row => row.item.StockBracket > 0 ? row.item.StockBracket : '—',
    row => formatCount(row.item.Demand),
    row => row.item.DemandBracket > 0 ? row.item.DemandBracket : '—',
    row => row.item.ColonisationNeeded > 0 ? `🏗 ${formatCount(row.item.ColonisationNeeded)} t` : '—',
    row => payload.operationsView.marketFlags[row.index]
  ], items.length ? operationsTranslations.filterNoMatches : operationsTranslations.marketEmpty);
}

function renderProfile(payload) {
  const inaraCard = document.getElementById('webInaraProfileCard');
  const inaraText = document.getElementById('webInaraProfileText');
  const inaraLink = document.getElementById('webInaraProfileLink');
  const ip = payload.state && payload.state.InaraProfile;
  const inaraOn = !!(payload.enabledIntegrations && payload.enabledIntegrations.inara);
  if (inaraCard && inaraText) {
    if (inaraOn && ip && ip.CommanderName) {
      const parts = [`INARA · ${ip.CommanderName}`];
      if (ip.SquadronName) parts.push(ip.SquadronRank ? `${ip.SquadronName} (${ip.SquadronRank})` : ip.SquadronName);
      if (ip.PreferredAllegiance) parts.push(ip.PreferredAllegiance);
      inaraText.textContent = parts.join('  ·  ');
      if (inaraLink) inaraLink.href = ip.InaraUrl || '#';
      inaraCard.hidden = false;
    } else {
      inaraCard.hidden = true;
    }
  }
  const rows = payload.operationsView.profile;
  document.getElementById('profileStatus').textContent = rows.length
    ? operationsTranslations.profileCount.replace('{0}', rows.length) : operationsTranslations.profileWaiting;
  updateFilterCategories('profileCategoryFilter', rows.map(row => [filterCategoryKey(row.CategoryId), row.Category]));
  const search = document.getElementById('profileSearch').value.trim().toLocaleLowerCase(sectionLanguage);
  const category = document.getElementById('profileCategoryFilter').value;
  const filtered = rows.filter(row =>
    (!search || [row.Category, row.Name, row.Value, row.SearchName]
      .some(value => (value || '').toLocaleLowerCase(sectionLanguage).includes(search))) &&
    (!category || filterCategoryKey(row.CategoryId) === category));
  document.getElementById('profileFilterStatus').textContent =
    operationsTranslations.filterCount.replace('{0}', filtered.length).replace('{1}', rows.length);
  renderTableRows('commanderProfileTableBody', filtered, [
    item => item.Category, item => item.Name, item => item.Value
  ], rows.length ? operationsTranslations.filterNoMatches : operationsTranslations.profileEmpty);
}

function renderMaterials(state) {
  const body = document.getElementById('materialsTableBody');
  const materials = state.MaterialsInventory || [];
  body.replaceChildren();

  const text = materialTranslations;
  document.getElementById('materialsStatus').textContent = !state.HasMaterialsTelemetry
    ? text.waiting
    : materials.length === 0
      ? text.empty
      : text.inventory.replace('{0}', materials.length);

  const search = document.getElementById('materialSearch').value.trim().toLocaleLowerCase();
  const category = document.getElementById('materialCategoryFilter').value;
  const rarity = document.getElementById('materialRarityFilter').value;
  const filtered = materials.filter(material => {
    const name = `${material.DisplayName || ''} ${material.Name || ''}`.toLocaleLowerCase();
    if (search && !name.includes(search))
      return false;
    if (category && material.Category !== category)
      return false;
    if (rarity === 'unknown' && material.Grade != null)
      return false;
    if (rarity && rarity !== 'unknown' && Number(material.Grade) !== Number(rarity))
      return false;
    return true;
  });

  document.getElementById('materialsFilterStatus').textContent =
    text.filtered.replace('{0}', filtered.length).replace('{1}', materials.length);

  if (!state.HasMaterialsTelemetry || materials.length === 0 || filtered.length === 0) {
    const row = document.createElement('tr');
    const cell = document.createElement('td');
    cell.colSpan = 5;
    cell.textContent = !state.HasMaterialsTelemetry
      ? text.unavailable
      : materials.length === 0
        ? text.emptyInventory
        : text.noMatches;
    row.append(cell);
    body.append(row);
    return;
  }

  const categoryNames = new Map([
    ['En bruto', text.raw], ['Manufacturados', text.manufactured], ['Datos codificados', text.encoded]
  ]);
  let webSelectedMaterialId = window._webSelectedMaterialId || null;
  filtered.forEach(material => {
    const row = document.createElement('tr');
    row.style.cursor = 'pointer';
    if (webSelectedMaterialId && material.Id === webSelectedMaterialId)
      row.classList.add('current-system');
    row.addEventListener('click', () => {
      window._webSelectedMaterialId = material.Id;
      body.querySelectorAll('tr').forEach(r => r.classList.remove('current-system'));
      row.classList.add('current-system');
    });
    const values = [
      material.DisplayName,
      categoryNames.get(material.Category) ?? material.Category,
      [text.unclassified, text.veryCommon, text.common, text.standard, text.rare, text.veryRare][material.Grade] ?? text.unclassified,
      material.CountWithCapacityDisplay || material.Count,
      material.WishlistDisplay || '—'
    ];
    values.forEach(value => {
      const cell = document.createElement('td');
      cell.textContent = value ?? '';
      row.append(cell);
    });
    body.append(row);
  });
}

const webDeckConfirmPending = new Map();

function renderCustomDeckTabs(tabs) {
  const search = document.getElementById('deckSearch').value.trim().toLocaleLowerCase();
  const renderKey = JSON.stringify([sectionLanguage, 'customTabs', tabs, search]);
  if (renderKey === lastDeckRenderKey) return;
  lastDeckRenderKey = renderKey;

  const container = document.getElementById('deckContainer');
  container.replaceChildren();
  let renderedCount = 0;

  for (const tab of tabs) {
    const items = (tab.Buttons || []).filter(b =>
      !search || `${b.Label || ''} ${b.ActionId || ''} ${b.FormattedBind || ''} ${tab.Name || ''}`.toLocaleLowerCase().includes(search));
    if (items.length === 0 && search) continue;
    renderedCount++;

    const section = document.createElement('div');
    section.className = 'deck-category module-section';
    section.classList.toggle('active', activeSectionByTab.get('tab-deck') === tab.Id);
    section.dataset.sectionTitle = tab.Id;
    section.dataset.sectionLabel = tab.IsFavoritesTab ? `★ ${tab.Name}` : `${tab.Icon || '🎛'} ${tab.Name}`;
    const title = document.createElement('h4');
    title.textContent = section.dataset.sectionLabel;
    const grid = document.createElement('div');
    grid.style.display = 'flex';
    grid.style.flexWrap = 'wrap';
    grid.style.gap = '8px';

    for (const b of items) {
      const scale = Math.max(0.5, Math.min(4.0, Number(b.SizeScale || 1.0)));
      const btnW = Math.max(88, Math.min(680, Math.round(156 * scale)));
      const btnH = Math.max(72, Math.min(185, Math.round(88 * (0.70 + scale * 0.30))));

      const button = document.createElement('button');
      button.className = 'deck-button';
      button.style.flex = `0 1 ${btnW}px`;
      button.style.minWidth = `${Math.min(btnW, 130)}px`;
      button.style.minHeight = `${btnH}px`;
      if (b.IsActive) {
        button.style.borderColor = '#22C55E';
        button.style.background = 'rgba(34,197,94,0.22)';
        button.style.borderWidth = '2px';
      } else if (b.AccentHex) {
        button.style.borderColor = b.AccentHex;
        button.style.borderWidth = '2px';
      }

      const iconSpan = document.createElement('span');
      iconSpan.className = 'deck-icon';
      iconSpan.textContent = `${b.Icon || '⚡'}${b.IsFavorite && !tab.IsFavoritesTab ? ' ★' : ''}${b.RequireConfirmation ? ' ⚠' : ''}`;
      const titleSpan = document.createElement('span');
      titleSpan.className = 'deck-title';
      titleSpan.textContent = b.Label || b.ActionId;
      const bindSpan = document.createElement('span');
      bindSpan.className = 'deck-bind';
      bindSpan.textContent = b.IsActive ? `● ON · ${b.FormattedBind}` : b.FormattedBind;
      if (b.IsActive) bindSpan.style.color = '#22C55E';
      button.append(iconSpan, titleSpan, bindSpan);

      button.addEventListener('click', () => {
        if (b.RequireConfirmation) {
          const now = Date.now();
          const exp = webDeckConfirmPending.get(b.Id) || 0;
          if (now > exp) {
            webDeckConfirmPending.set(b.Id, now + 2500);
            titleSpan.textContent = '⚠ PULSA OTRA VEZ';
            button.style.borderColor = '#EF4444';
            setTimeout(() => {
              if ((webDeckConfirmPending.get(b.Id) || 0) <= Date.now()) {
                webDeckConfirmPending.delete(b.Id);
                titleSpan.textContent = b.Label || b.ActionId;
                button.style.borderColor = b.IsActive ? '#22C55E' : (b.AccentHex || '');
              }
            }, 2600);
            return;
          }
          webDeckConfirmPending.delete(b.Id);
          titleSpan.textContent = b.Label || b.ActionId;
        }
        sendCmd(b.ActionId);
      });
      grid.append(button);
    }

    section.append(title, grid);
    container.append(section);
  }

  if (renderedCount === 0) {
    const card = document.createElement('div');
    card.className = 'hud-card module-section active';
    card.dataset.sectionTitle = 'Controles';
    card.dataset.sectionLabel = deckTranslations.emptyTitle;
    const title = document.createElement('h3');
    title.textContent = deckTranslations.emptyTitle;
    const message = document.createElement('p');
    message.style.color = 'var(--hud-muted)';
    message.textContent = search ? deckTranslations.noSearch : deckTranslations.noBindings;
    card.append(title, message);
    container.append(card);
  }
  updateSectionTabs();
}

function renderDeck(bindings, hideUnassigned) {
  const groups = new Map();
  const search = document.getElementById('deckSearch').value.trim().toLocaleLowerCase();
  const renderKey = JSON.stringify([sectionLanguage, bindings, hideUnassigned, search]);
  if (renderKey === lastDeckRenderKey) return;
  lastDeckRenderKey = renderKey;
  const filteredBindings = (bindings || [])
    .filter(b => !hideUnassigned || b.IsAssigned)
    .filter(b => !search || `${b.DisplayName || ''} ${b.Category || ''} ${b.FormattedBind || ''} ${b.SourceDisplayName || ''} ${b.SourceCategory || ''} ${b.SourceFormattedBind || ''} ${b.ActionId || ''}`.toLocaleLowerCase().includes(search));
  filteredBindings.forEach(b => {
    const cat = b.CategoryId;
    if (!groups.has(cat)) groups.set(cat, []);
    groups.get(cat).push(b);
  });

  const container = document.getElementById('deckContainer');
  container.replaceChildren();
  if (filteredBindings.length === 0) {
    const card = document.createElement('div');
    card.className = 'hud-card module-section active';
    card.dataset.sectionTitle = 'Controles';
    card.dataset.sectionLabel = deckTranslations.emptyTitle;
    const title = document.createElement('h3');
    title.textContent = deckTranslations.emptyTitle;
    const message = document.createElement('p');
    message.style.color = 'var(--hud-muted)';
    message.textContent = search ? deckTranslations.noSearch : deckTranslations.noBindings;
    card.append(title, message);
    container.append(card);
    updateSectionTabs();
    return;
  }
  for (const [categoryId, items] of groups) {
    const section = document.createElement('div');
    section.className = 'deck-category module-section';
    section.classList.toggle('active', activeSectionByTab.get('tab-deck') === categoryId);
    section.dataset.sectionTitle = categoryId;
    section.dataset.sectionLabel = items[0].Category;
    const title = document.createElement('h4');
    title.textContent = items[0].Category;
    const grid = document.createElement('div');
    grid.className = 'deck-grid';
    for (const binding of items) {
      const button = document.createElement('button');
      button.className = 'deck-button';
      button.addEventListener('click', () => sendCmd(binding.ActionId));
      for (const [className, value] of [['deck-icon', binding.IconGlyph || '⚙'], ['deck-title', binding.DisplayName], ['deck-bind', binding.FormattedBind]]) {
        const span = document.createElement('span');
        span.className = className;
        span.textContent = value;
        button.append(span);
      }
      grid.append(button);
    }
    section.append(title, grid);
    container.append(section);
  }
  updateSectionTabs();
}

function connectWs() {
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  const ws = new WebSocket(`${proto}//${location.host}/ws`);
  const badge = document.getElementById('connBadge');

  ws.onopen = () => {
    connectionKey = 'connected';
    badge.textContent = inputTranslations ? inputTranslations[connectionKey] : 'WEB CONECTADA';
    badge.style.color = 'var(--hud-accent)';
    badge.style.borderColor = 'var(--hud-accent)';
  };
  ws.onmessage = (ev) => {
    try {
      const data = JSON.parse(ev.data);
      renderState(data);
    } catch (e) { console.error('Invalid state update:', e); }
  };
  ws.onclose = () => {
    connectionKey = 'reconnecting';
    badge.textContent = inputTranslations ? inputTranslations[connectionKey] : 'RECONECTANDO';
    badge.style.color = 'var(--hud-danger)';
    badge.style.borderColor = 'var(--hud-danger)';
    fetch('/api/state', { cache: 'no-store' }).then(response => {
      if (response.status === 401)
        location.replace('/login');
      else
        setTimeout(connectWs, 2500);
    }, () => setTimeout(connectWs, 2500));
  };
}

fetch('/api/state').then(r => r.json()).then(renderState).catch(error => console.error('Unable to load state:', error));
['materialSearch', 'materialCategoryFilter', 'materialRarityFilter'].forEach(id => {
  document.getElementById(id).addEventListener(id === 'materialSearch' ? 'input' : 'change', () => {
    if (lastPayload && lastPayload.state)
      renderMaterials(lastPayload.state);
  });
});
for (const [prefix, render] of [['market', renderMarket], ['profile', renderProfile]]) {
  for (const [suffix, event] of [['Search', 'input'], ['CategoryFilter', 'change']]) {
    document.getElementById(prefix + suffix).addEventListener(event, () => {
      if (lastPayload?.state && lastPayload.operationsView) render(lastPayload);
    });
  }
}
document.getElementById('deckSearch').addEventListener('input', () => {
  if (lastPayload && Array.isArray(lastPayload.deckTabs) && lastPayload.deckTabs.length > 0)
    renderCustomDeckTabs(lastPayload.deckTabs);
  else if (lastPayload && lastPayload.bindings)
    renderDeck(lastPayload.deckBindings, lastPayload.hideUnassignedBindings);
});

function exportWebAiChat() {
  const log = document.getElementById('webAiChatLog');
  if (!log || !log.textContent.trim()) return;
  const blob = new Blob(['# HUDnopy COVAS Chat Log\n\n' + log.textContent], { type: 'text/markdown;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = `HUDnopy-COVAS-${new Date().toISOString().slice(0, 10)}.md`;
  a.click();
  URL.revokeObjectURL(url);
}

async function postWebJson(url, payload) {
  const resp = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload ?? {})
  });
  return resp.json();
}

async function sendWebAiVision() {
  const input = document.getElementById('webAiPrompt');
  const prompt = (input?.value || '').trim();
  if (input) input.value = '';
  const log = document.getElementById('webAiChatLog');
  const append = (who, msg) => {
    if (!log) return;
    log.textContent += `[${new Date().toLocaleTimeString()}] ${who}: ${msg}\n\n`;
    log.scrollTop = log.scrollHeight;
  };
  append('CMDR', prompt ? `👁 ${prompt}` : '👁');
  const btn = document.getElementById('btnWebAiVision');
  if (btn) btn.disabled = true;
  try {
    const data = await postWebJson('/api/ai/vision', { prompt });
    append('COVAS', data.reply || '---');
  } catch (err) {
    append('COVAS', String(err));
  } finally {
    if (btn) btn.disabled = false;
  }
}

async function sendWebAiPrompt(overrideText) {
  const input = document.getElementById('webAiPrompt');
  const prompt = (overrideText ?? input.value).trim();
  if (!prompt) return;
  input.value = '';
  const log = document.getElementById('webAiChatLog');
  const append = (who, msg) => {
    log.textContent += `[${new Date().toLocaleTimeString()}] ${who}: ${msg}\n\n`;
    log.scrollTop = log.scrollHeight;
  };
  append('CMDR', prompt);
  const btn = document.getElementById('btnWebAiSend');
  btn.disabled = true;
  try {
    const data = await postWebJson('/api/ai/ask', { prompt });
    append('COVAS', data.reply || '---');
  } catch (err) {
    append('COVAS', String(err));
  } finally {
    btn.disabled = false;
  }
}

let webAiRecognition = null;
let webAiHandsFreeActive = false;

function openWebInaraCurrentSystem() {
  const sys = lastPayload?.state?.CurrentSystem;
  if (!sys) return;
  window.open(`https://inara.cz/elite/starsystem/?search=${encodeURIComponent(sys)}`, '_blank', 'noopener');
}

function toggleWebAiHandsFree() {
  const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
  if (!SR) { showToast(inputTranslations?.webSpeechUnsupported || 'Web Speech API is not supported in this browser'); return; }
  const btn = document.getElementById('btnWebAiHandsFree');
  if (webAiHandsFreeActive) {
    webAiHandsFreeActive = false;
    if (webAiRecognition) { try { webAiRecognition.stop(); } catch { } }
    if (btn) {
      btn.textContent = '🎙';
      btn.style.background = '';
      btn.style.color = '';
      btn.title = operationsTranslations?.aiHandsFreeBtn || 'Hands-free';
    }
    return;
  }
  webAiHandsFreeActive = true;
  if (btn) {
    btn.textContent = '🟢';
    btn.style.background = 'var(--hud-accent)';
    btn.style.color = '#080A0E';
    btn.title = operationsTranslations?.aiHandsFreeListening || 'Listening...';
  }
  startWebAiHandsFreeLoop();
}

function startWebAiHandsFreeLoop() {
  if (!webAiHandsFreeActive) return;
  const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
  if (!SR) return;
  webAiRecognition = new SR();
  webAiRecognition.lang = sectionLanguage || 'en-GB';
  webAiRecognition.interimResults = false;
  webAiRecognition.onresult = e => {
    const transcript = e.results?.[0]?.[0]?.transcript || '';
    if (transcript) sendWebAiPrompt(transcript);
  };
  webAiRecognition.onend = () => {
    webAiRecognition = null;
    if (webAiHandsFreeActive) setTimeout(startWebAiHandsFreeLoop, 400);
  };
  webAiRecognition.onerror = () => {
    webAiRecognition = null;
    if (webAiHandsFreeActive) setTimeout(startWebAiHandsFreeLoop, 1000);
  };
  try { webAiRecognition.start(); } catch { }
}

function toggleWebAiVoice() {
  const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
  if (!SR) { showToast(inputTranslations?.webSpeechUnsupported || 'Web Speech API is not supported in this browser'); return; }
  const btn = document.getElementById('btnWebAiMic');
  if (webAiRecognition) {
    webAiRecognition.stop();
    return;
  }
  webAiRecognition = new SR();
  webAiRecognition.lang = sectionLanguage || 'en-GB';
  webAiRecognition.interimResults = false;
  btn.textContent = '⏹';
  webAiRecognition.onresult = e => {
    const transcript = e.results?.[0]?.[0]?.transcript || '';
    if (transcript) sendWebAiPrompt(transcript);
  };
  webAiRecognition.onend = () => {
    webAiRecognition = null;
    btn.textContent = '🎤';
  };
  webAiRecognition.onerror = () => {
    webAiRecognition = null;
    btn.textContent = '🎤';
  };
  webAiRecognition.start();
}

async function runWebTableQuery(url, statusId, tbodyId, loadingText, itemsKey, rowHtmlFn, statusFormatter) {
  const status = document.getElementById(statusId);
  const tbody = document.getElementById(tbodyId);
  if (status) status.textContent = loadingText;
  if (tbody) tbody.innerHTML = '';
  try {
    const resp = await fetch(url);
    const data = await resp.json();
    if (status) status.textContent = statusFormatter ? statusFormatter(data) : (data.message || '');
    const items = data[itemsKey];
    if (tbody && Array.isArray(items)) {
      tbody.innerHTML = items.map(rowHtmlFn).join('');
      const wrap = tbody.closest('.table-scroll');
      if (wrap) wrap.hidden = items.length === 0;
    }
  } catch (err) {
    if (status) status.textContent = String(err);
  }
}

async function searchWebSpanshService(serviceKey) {
  await runWebTableQuery(
    `/api/spansh/nearest?service=${encodeURIComponent(serviceKey)}`,
    'webSpanshServiceStatus', 'webSpanshServiceTableBody', 'Spansh...', 'results',
    r => `<tr><td>${escapeHtml(r.systemName || '')}</td><td>${escapeHtml(r.stationName || '')}</td><td>${escapeHtml(r.distanceDisplay || '')}</td><td>${escapeHtml(r.landingPad || '')}</td></tr>`
  );
}

async function searchWebSpanshBodies() {
  const q = (document.getElementById('webSpanshBodyQuery')?.value || '').trim();
  await runWebTableQuery(
    `/api/spansh/bodies?query=${encodeURIComponent(q)}`,
    'webDeepSearchStatus', 'webDeepSearchTableBody', 'Spansh Bodies...', 'results',
    r => `<tr><td>${escapeHtml(r.bodyName || '')}</td><td>${escapeHtml(r.systemName || '')}</td><td>${escapeHtml(r.distanceDisplay || '')}</td><td>${escapeHtml(r.materialsSummary || r.subType || '')}</td></tr>`
  );
}

async function searchWebSpanshCarriers(service) {
  const sel = document.getElementById('webSpanshCarrierService');
  const chosen = service || (sel ? sel.value : 'Vista Genomics');
  await runWebTableQuery(
    `/api/spansh/carriers?service=${encodeURIComponent(chosen)}`,
    'webDeepSearchStatus', 'webDeepSearchTableBody', 'Spansh Carriers...', 'results',
    r => `<tr><td>${escapeHtml(r.callsign || '')}</td><td>${escapeHtml(r.systemName || '')}</td><td>${escapeHtml(r.distanceDisplay || '')}</td><td>${escapeHtml(r.servicesSummary || '')}</td></tr>`
  );
}

async function searchWebSpanshItem() {
  const query = (document.getElementById('webSpanshItemQuery')?.value || '').trim();
  const isShip = document.getElementById('webSpanshItemType')?.value === 'ship';
  const lyr = !!document.getElementById('webSpanshItemLyr')?.checked;
  await runWebTableQuery(
    `/api/spansh/item?query=${encodeURIComponent(query)}&isShip=${isShip}&lyr=${lyr}`,
    'webSpanshServiceStatus', 'webSpanshServiceTableBody', 'Spansh...', 'results',
    r => `<tr><td>${escapeHtml(r.systemName || '')}</td><td>${escapeHtml(r.stationName || '')}</td><td>${escapeHtml(r.distanceDisplay || '')}</td><td>${escapeHtml(r.landingPad || '')}</td></tr>`
  );
}

async function plotWebSpanshRoute() {
  const mode = document.getElementById('webSpanshRouteMode')?.value || 'neutron';
  const dest = (document.getElementById('webSpanshRouteDest')?.value || '').trim();
  const range = document.getElementById('webSpanshRouteRange')?.value || '50';
  await runWebTableQuery(
    `/api/spansh/route?mode=${encodeURIComponent(mode)}&to=${encodeURIComponent(dest)}&range=${encodeURIComponent(range)}`,
    'webSpanshRouteStatus', 'webSpanshRouteTableBody', 'Spansh Route...', 'waypoints',
    w => `<tr><td>${escapeHtml(String(w.index || ''))}</td><td>${escapeHtml(w.systemName || '')}</td><td>${escapeHtml(w.distanceDisplay || '')}</td><td>${escapeHtml(w.details || '')}</td></tr>`
  );
}

async function plotWebSpanshTrade() {
  await runWebTableQuery(
    '/api/spansh/trade',
    'webSpanshTradeStatus', 'webSpanshTradeTableBody', 'Spansh Trade...', 'hops',
    h => `<tr><td>${escapeHtml(String(h.hop || ''))}</td><td>${escapeHtml(h.routeDisplay || '')}</td><td>${escapeHtml(h.commodity || '')}</td><td>${escapeHtml(h.profitDisplay || '')}</td><td>${escapeHtml(h.distanceDisplay || '')}</td></tr>`
  );
}

async function searchWebSpanshMining() {
  const mineral = document.getElementById('webSpanshMiningMineral')?.value || 'Platinum';
  await runWebTableQuery(
    `/api/spansh/mining?mineral=${encodeURIComponent(mineral)}`,
    'webSpanshMiningStatus', 'webSpanshMiningTableBody', 'Spansh Mining...', 'results',
    r => `<tr><td>${escapeHtml(r.systemName || '')}</td><td>${escapeHtml(r.ringName || '')}</td><td>${escapeHtml(r.ringType || '')}</td><td>${escapeHtml(r.distanceDisplay || '')}</td><td>${escapeHtml(r.signals || '')}</td></tr>`
  );
}

async function searchWebColonisationSupplier() {
  await runWebTableQuery(
    '/api/spansh/colonisation',
    'webColonisationSupplierStatus', 'webColonisationSupplierTableBody', 'Spansh Colonisation...', 'results',
    r => `<tr><td>${escapeHtml(r.systemName || '')}</td><td>${escapeHtml(r.stationName || '')}</td><td>${escapeHtml(r.distanceDisplay || '')}</td><td>${escapeHtml(r.landingPad || '')}</td></tr>`,
    data => `${data.commodity ? data.commodity + ' — ' : ''}${data.message || ''}`
  );
}

async function fetchWebEdsmIntel() {
  const el = document.getElementById('webEdsmTrafficText');
  if (!el) return;
  el.hidden = false;
  el.textContent = 'EDSM...';
  try {
    const resp = await fetch('/api/edsm/intel');
    const data = await resp.json();
    if (data.success && data.intel) {
      const i = data.intel;
      const notePart = i.remoteComment ? ` · 📝 ${i.remoteComment}` : '';
      el.textContent = `EDSM ${i.systemName}: Traffic ${i.trafficDay}/24h (${i.trafficWeek}/7d) · Deaths ${i.deathsDay}/24h (${i.deathsWeek}/7d)${notePart}`;
      const noteInput = document.getElementById('webSystemNoteInput');
      if (noteInput && !noteInput.value.trim() && i.remoteComment) {
        noteInput.value = i.remoteComment;
      }
    } else {
      el.textContent = data.message || 'EDSM unavailable';
    }
  } catch (err) {
    el.textContent = String(err);
  }
}

async function saveWebSystemNote() {
  const input = document.getElementById('webSystemNoteInput');
  if (!input) return;
  try {
    const data = await postWebJson('/api/system-note', { note: input.value || '' });
    if (data.success) showToast((inputTranslations?.webNoteSaved || 'Note saved ({0})').replace('{0}', data.system));
  } catch (err) {
    showToast(String(err));
  }
}

function updateWebSurfaceGuidance(stateArg) {
  const s = stateArg || (lastPayload && lastPayload.state);
  const guideEl = document.getElementById('webSurfaceGuidance');
  const tapeEl = document.getElementById('webSurfaceCompassTape');
  if (!guideEl) return;

  const tLat = parseFloat((document.getElementById('webTargetLat')?.value || '').replace(',', '.'));
  const tLon = parseFloat((document.getElementById('webTargetLon')?.value || '').replace(',', '.'));
  if (!Number.isFinite(tLat) || !Number.isFinite(tLon)) {
    if (tapeEl) tapeEl.hidden = true;
    guideEl.textContent = flightTranslations.surfaceGuidancePrompt || '';
    return;
  }

  if (!s || !s.HasLatLong) {
    if (tapeEl) tapeEl.hidden = true;
    guideEl.textContent = flightTranslations.noSurface || '';
    return;
  }

  const toRad = Math.PI / 180;
  const lat1 = s.Latitude * toRad;
  const lat2 = tLat * toRad;
  const dLon = (tLon - s.Longitude) * toRad;
  const dLat = (tLat - s.Latitude) * toRad;
  const y = Math.sin(dLon) * Math.cos(lat2);
  const x = Math.cos(lat1) * Math.sin(lat2) - Math.sin(lat1) * Math.cos(lat2) * Math.cos(dLon);
  const bearing = ((Math.atan2(y, x) * 180 / Math.PI) + 360) % 360;
  const radius = s.PlanetRadius > 1000 ? s.PlanetRadius : 1000000;
  const a = Math.sin(dLat / 2) ** 2 + Math.cos(lat1) * Math.cos(lat2) * Math.sin(dLon / 2) ** 2;
  const distM = radius * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(Math.max(0, 1 - a)));
  const distStr = distM >= 1000 ? `${(distM / 1000).toFixed(2)} km` : `${Math.round(distM)} m`;

  const heading = Number.isFinite(s.Heading) ? s.Heading : 0;
  const delta = ((bearing - heading + 540) % 360) - 180;
  const arrow = delta < -25 ? '◀◀' : delta < -4 ? '◀' : delta <= 4 ? '▲' : delta <= 25 ? '▶' : '▶▶';

  if (tapeEl) {
    const slots = 21;
    const center = Math.floor(slots / 2);
    const chars = Array(slots).fill('·');
    chars[center] = '│';
    const offset = Math.round(Math.max(-center, Math.min(center, delta / 9)));
    chars[center + offset] = '◆';
    const hdgPad = String(Math.round(heading) % 360).padStart(3, '0');
    const brgPad = String(Math.round(bearing) % 360).padStart(3, '0');
    tapeEl.textContent = `${arrow}  [${chars.join('')}]  HDG ${hdgPad}°  ▸  BRG ${brgPad}°`;
    tapeEl.hidden = false;
  }

  guideEl.textContent = `${arrow} ` + (flightTranslations.surfaceGuidanceResult || '{0}° · {1}')
    .replace('{0}', Math.round(bearing))
    .replace('{1}', distStr);
}

document.getElementById('webTargetLat')?.addEventListener('input', () => updateWebSurfaceGuidance());
document.getElementById('webTargetLon')?.addEventListener('input', () => updateWebSurfaceGuidance());

function targetWebLastExoSample() {
  const s = lastPayload && lastPayload.state;
  const exo = s && s.ExobiologyTracker;
  if (!exo || typeof exo.LastSampleLatitude !== 'number' || typeof exo.LastSampleLongitude !== 'number') {
    showToast(inputTranslations?.webNoExoCoords || 'No exobiology sample coordinates.');
    return;
  }
  const latEl = document.getElementById('webTargetLat');
  const lonEl = document.getElementById('webTargetLon');
  if (latEl) latEl.value = exo.LastSampleLatitude.toFixed(4);
  if (lonEl) lonEl.value = exo.LastSampleLongitude.toFixed(4);
  if (typeof updateWebSurfaceGuidance === 'function') updateWebSurfaceGuidance();
}

async function saveWebSurfaceBookmark() {
  const labelEl = document.getElementById('webBookmarkLabel');
  const label = (labelEl?.value || '').trim();
  try {
    const data = await postWebJson('/api/surface/bookmarks', { action: 'add', label });
    if (data.success) {
      if (labelEl) labelEl.value = '';
      renderWebSurfaceBookmarks(data.bookmarks || []);
      showToast(inputTranslations?.webBookmarkSaved || 'Bookmark saved');
    } else {
      showToast(data.message || 'Error');
    }
  } catch (err) {
    showToast(String(err));
  }
}

async function deleteWebSurfaceBookmark(label) {
  try {
    const data = await postWebJson('/api/surface/bookmarks', { action: 'delete', label });
    if (data.success) renderWebSurfaceBookmarks(data.bookmarks || []);
  } catch { }
}

function navigateWebBookmark(lat, lon) {
  const latEl = document.getElementById('webTargetLat');
  const lonEl = document.getElementById('webTargetLon');
  if (latEl) latEl.value = Number(lat).toFixed(4);
  if (lonEl) lonEl.value = Number(lon).toFixed(4);
  if (typeof updateWebSurfaceGuidance === 'function') updateWebSurfaceGuidance();
}

function renderWebSurfaceBookmarks(bookmarks) {
  const tbody = document.getElementById('webSurfaceBookmarksTableBody');
  if (!tbody) return;
  tbody.innerHTML = (bookmarks || []).map(b => {
    const lat = b.latitude ?? b.Latitude ?? 0;
    const lon = b.longitude ?? b.Longitude ?? 0;
    const lbl = b.label ?? b.Label ?? '';
    return `<tr><td>${escapeHtml(b.systemName ?? b.SystemName ?? '')}</td><td>${escapeHtml(b.bodyName ?? b.BodyName ?? '')}</td><td>${escapeHtml(lbl)}</td><td>${lat.toFixed(3)}, ${lon.toFixed(3)}</td><td><button class="deck-button" style="min-height:28px;padding:0 8px;" onclick="navigateWebBookmark(${lat},${lon})">🧭</button> <button class="deck-button" style="min-height:28px;padding:0 8px;" onclick="deleteWebSurfaceBookmark('${escapeHtml(lbl).replace(/'/g, '&#39;')}')">🗑</button></td></tr>`;
  }).join('');
}

async function runWebAiQuick(action) {
  const log = document.getElementById('webAiChatLog');
  if (action === 'clear') {
    await postWebJson('/api/ai/quick', { action: 'clear' });
    if (log) log.textContent = '';
    return;
  }
  try {
    if (log) log.textContent += `[COVAS] (${action})...\n`;
    const data = await postWebJson('/api/ai/quick', { action });
    if (log) {
      log.textContent += `[COVAS] ${data.reply || ''}\n\n`;
      log.scrollTop = log.scrollHeight;
    }
  } catch (err) {
    if (log) log.textContent += `[ERR] ${err}\n`;
  }
}

async function speakWebGalnet(stop) {
  try {
    await postWebJson('/api/galnet/speak', stop ? { stop: true } : { index: selectedGalnetIndex || 0 });
  } catch { }
}

let webViewingHistoricalJournal = false;
let webJournalFilesLoaded = false;
let webCurrentJournalEvents = [];
let webLastExpeditionMarkdown = '';

async function toggleSelectedWebMaterialWishlist() {
  const materialId = window._webSelectedMaterialId;
  if (!materialId) {
    showToast(materialTranslations?.wishlistToggle || 'Select a material row first');
    return;
  }
  const target = Number(document.getElementById('webWishlistTarget')?.value || 50) || 50;
  await fetch('/api/materials/wishlist/toggle', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ materialId, target })
  });
}

async function toggleWebAutoContextDeck(enabled) {
  await fetch('/api/deck/auto-context', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ enabled: Boolean(enabled) })
  });
}

function importWebSpanshCsv(input) {
  const file = input?.files?.[0];
  if (!file) return;
  const reader = new FileReader();
  reader.onload = () => {
    const lines = String(reader.result || '').split(/\r?\n/).map(l => l.trim()).filter(Boolean);
    const tbody = document.getElementById('webSpanshRouteTableBody');
    const status = document.getElementById('webSpanshRouteStatus');
    if (!tbody || lines.length < 2) return;
    const rows = [];
    for (let i = 1; i < lines.length; i++) {
      const cols = lines[i].split(',').map(c => c.replace(/^"|"$/g, '').trim());
      if (!cols[0]) continue;
      rows.push({ index: i, systemName: cols[0], distanceDisplay: cols[1] ? `${cols[1]} LY` : '—', details: cols.slice(2).filter(Boolean).join(' · ') });
    }
    tbody.innerHTML = rows.map(w => `<tr><td>${escapeHtml(String(w.index))}</td><td>${escapeHtml(w.systemName)}</td><td>${escapeHtml(w.distanceDisplay)}</td><td>${escapeHtml(w.details)}</td></tr>`).join('');
    const wrap = tbody.closest('.table-scroll');
    if (wrap) wrap.hidden = rows.length === 0;
    if (status) status.textContent = `${file.name} (${rows.length})`;
    if (rows.length > 0 && document.getElementById('webSpanshAutoCopy')?.checked && navigator.clipboard) {
      navigator.clipboard.writeText(rows[0].systemName).catch(() => { });
    }
  };
  reader.readAsText(file);
  input.value = '';
}

async function resetWebSessionStats() {
  await fetch('/api/session/reset', { method: 'POST' });
  showToast(dashboardTranslations?.sessionReset || 'Session reset');
}

async function refreshWebJournalFiles() {
  const sel = document.getElementById('webJournalHistorySelect');
  if (!sel) return;
  const prev = sel.value || '';
  try {
    const resp = await fetch('/api/journal/files');
    const data = await resp.json();
    sel.replaceChildren();
    const liveOpt = document.createElement('option');
    liveOpt.value = '';
    liveOpt.textContent = operationsTranslations?.journalLiveSession || 'Live session';
    sel.append(liveOpt);
    for (const f of (data.files || [])) {
      const opt = document.createElement('option');
      opt.value = f.name;
      opt.textContent = f.label;
      sel.append(opt);
    }
    if ([...sel.options].some(o => o.value === prev)) sel.value = prev;
  } catch { }
}

async function onWebJournalHistoryChange() {
  const sel = document.getElementById('webJournalHistorySelect');
  if (!sel) return;
  const file = sel.value || '';
  webViewingHistoricalJournal = Boolean(file);
  try {
    const url = file ? `/api/journal/events?file=${encodeURIComponent(file)}` : '/api/journal/events';
    const resp = await fetch(url);
    const data = await resp.json();
    if (data.success && Array.isArray(data.events)) {
      webCurrentJournalEvents = data.events;
      const eventColumns = [ev => ev.Timestamp, ev => ev.EventType, ev => ev.Summary];
      renderTableRows('journalTableBody', data.events, eventColumns, explorationTranslations?.journalEmpty || '');
      for (const row of document.getElementById('journalTableBody').rows) {
        if (row.cells.length !== 3) continue;
        row.cells[0].style.fontFamily = 'Consolas,monospace';
        row.cells[0].style.color = 'var(--hud-muted)';
        row.cells[1].style.color = 'var(--hud-accent)';
        row.cells[1].style.fontWeight = '700';
      }
    }
  } catch { }
}

function exportWebJournal(format) {
  const rows = webCurrentJournalEvents || [];
  if (!rows.length) return;
  let content = '';
  let mime = 'text/plain;charset=utf-8';
  let ext = format === 'json' ? 'json' : 'csv';
  if (format === 'json') {
    content = JSON.stringify(rows, null, 2);
    mime = 'application/json;charset=utf-8';
  } else {
    const esc = v => `"${String(v ?? '').replace(/"/g, '""')}"`;
    content = 'Timestamp,EventType,Summary\n' + rows.map(r => [esc(r.Timestamp), esc(r.EventType), esc(r.Summary)].join(',')).join('\n');
    mime = 'text/csv;charset=utf-8';
  }
  const blob = new Blob([content], { type: mime });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = `hudnopy-journal.${ext}`;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

function exportWebExpeditionMarkdown() {
  if (!webLastExpeditionMarkdown) return;
  const blob = new Blob([webLastExpeditionMarkdown], { type: 'text/markdown;charset=utf-8' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = 'hudnopy-expedition-report.md';
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

async function analyzeWebExpedition() {
  const el = document.getElementById('webExpeditionSummary');
  const mdBtn = document.getElementById('btnWebExportExpeditionMd');
  if (!el) return;
  el.hidden = false;
  el.textContent = inputTranslations?.webAnalyzingJournals || 'Analyzing journals...';
  try {
    const resp = await fetch('/api/expedition/analyze', { method: 'POST' });
    const data = await resp.json();
    if (data.success && data.stats) {
      const st = data.stats;
      el.textContent = `Journals: ${st.journalsScanned} · Jumps: ${st.totalJumps} (${Number(st.totalDistanceLy || 0).toFixed(1)} LY) · Systems: ${st.uniqueSystems} · ELW/WW/AW: ${st.earthLikeWorlds}/${st.waterWorlds}/${st.ammoniaWorlds} · Exo: ${st.organicsAnalysed} · Missions: ${st.missionsCompleted}`;
      webLastExpeditionMarkdown = data.markdown || '';
      if (mdBtn) mdBtn.disabled = !webLastExpeditionMarkdown;
    }
  } catch (err) {
    el.textContent = String(err);
  }
}

async function resetWebCombatSession() {
  await fetch('/api/combat/reset', { method: 'POST' });
  showToast(inputTranslations?.webCombatReset || 'Combat session reset');
}

async function resetWebMiningSession() {
  await fetch('/api/mining/reset', { method: 'POST' });
  showToast(inputTranslations?.webMiningReset || 'Mining session reset');
}

async function saveWebMiningFilter() {
  const targets = document.getElementById('webMiningTargets')?.value || '';
  const minPercent = parseInt(document.getElementById('webMiningMinPercent')?.value || '25', 10) || 25;
  await postWebJson('/api/mining/filter', { targets, minPercent });
  showToast(inputTranslations?.webMiningFilterApplied || 'Mining filter applied');
}

async function updateWebWishlist(action) {
  const bpId = document.getElementById('webEngineeringBlueprint')?.value || '';
  const data = await postWebJson('/api/wishlist/update', { action, blueprintId: bpId });
  if (data.success && Array.isArray(data.rows)) {
    renderTableRows('webWishlistTableBody', data.rows, [
      r => r.materialName ?? r.MaterialName,
      r => r.category ?? r.Category,
      r => formatCount(r.required ?? r.Required),
      r => formatCount(r.owned ?? r.Owned),
      r => r.statusDisplay ?? r.StatusDisplay
    ], '');
  }
}

function findWebWishlistTrader() {
  const navBtn = document.querySelector('nav.tabs button[onclick*="\'nav\'"]');
  if (navBtn) switchTab('nav', navBtn);
  const sec = document.querySelector('#tab-nav .module-section[data-section-key="spansh-services"]');
  if (sec) {
    document.querySelectorAll('#tab-nav .module-section').forEach(el => el.classList.toggle('active', el === sec));
    activeSectionByTab.set('tab-nav', sec.dataset.sectionTitle || null);
    updateSectionTabs();
  }
  searchWebSpanshService('material_trader');
}

function renderWebShipLockerFilter() {
  const items = Array.isArray(window.__lastShipLocker) ? window.__lastShipLocker : [];
  const q = (document.getElementById('webShipLockerSearch')?.value || '').trim().toLowerCase();
  const filtered = q ? items.filter(i => `${i.DisplayName || ''} ${i.Type || ''}`.toLowerCase().includes(q)) : items;
  renderTableRows('shipLockerTableBody', filtered, [
    i => i.DisplayName, i => i.Type, i => formatCount(i.Count)
  ], operationsTranslations.shipLockerWaiting || '');
}

async function fetchWebNearestDssa() {
  await runWebTableQuery(
    '/api/dssa/nearest',
    'webDeepSearchStatus', 'webDeepSearchTableBody', 'EDAstro DSSA...', 'carriers',
    c => `<tr><td>${escapeHtml((c.callsign || '') + ' ' + (c.name || ''))}</td><td>${escapeHtml(c.systemName || '')}</td><td>${escapeHtml(c.distanceDisplay || '')}</td><td>${escapeHtml(c.region || '')}</td></tr>`
  );
}

async function searchWebArdentMarket() {
  const commodity = (document.getElementById('webArdentCommodity')?.value || '').trim();
  const mode = document.getElementById('webArdentMode')?.value || 'sell';
  await runWebTableQuery(
    `/api/ardent/market?commodity=${encodeURIComponent(commodity)}&mode=${encodeURIComponent(mode)}`,
    'webArdentStatus', 'webArdentTableBody', 'Ardent Insight...', 'offers',
    o => `<tr><td>${escapeHtml(o.systemName || '')}</td><td>${escapeHtml(o.stationName || '')}</td><td>${escapeHtml(o.priceDisplay || '')}</td><td>${escapeHtml(o.quantityDisplay || '')}</td><td>${escapeHtml(o.maxLandingPad || '')}</td><td>${escapeHtml(o.distanceDisplay || '')}</td></tr>`
  );
}

async function refreshWebEliteBgs() {
  const status = document.getElementById('webEliteBgsStatus');
  if (status) status.textContent = 'EliteBGS...';
  try {
    const resp = await fetch('/api/bgs/refresh', { method: 'POST' });
    const data = await resp.json();
    if (status) status.textContent = data.message || '';
  } catch (err) {
    if (status) status.textContent = String(err);
  }
}

function sanitizeWebInput(raw, mode) {
  if (!raw) return '';
  if (mode === 'int') return raw.replace(/[^0-9]/g, '');
  let out = '';
  let hasSep = false;
  for (let i = 0; i < raw.length; i++) {
    const ch = raw[i];
    if (mode === 'decimal' && ch === '-' && out.length === 0) {
      out += ch;
    } else if (ch >= '0' && ch <= '9') {
      out += ch;
    } else if ((ch === '.' || ch === ',') && !hasSep) {
      hasSep = true;
      out += '.';
    }
  }
  return out;
}

function initWebInputConstraints() {
  const inputs = document.querySelectorAll('input[data-input-mode]');
  const navKeys = new Set(['Backspace', 'Delete', 'Tab', 'Escape', 'Enter', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End']);
  inputs.forEach(input => {
    const mode = input.dataset.inputMode;
    const allowEmpty = input.dataset.allowEmpty === 'true';
    const minAttr = input.dataset.min ?? input.min;
    const maxAttr = input.dataset.max ?? input.max;
    const min = minAttr !== undefined && minAttr !== '' ? Number(minAttr) : NaN;
    const max = maxAttr !== undefined && maxAttr !== '' ? Number(maxAttr) : NaN;

    input.addEventListener('keydown', e => {
      if (e.ctrlKey || e.metaKey || e.altKey || navKeys.has(e.key) || e.key === 'Unidentified') return;
      const start = input.selectionStart ?? input.value.length;
      const end = input.selectionEnd ?? input.value.length;
      const candidate = input.value.slice(0, start) + e.key + input.value.slice(end);
      const sanitized = sanitizeWebInput(candidate, mode);
      if (sanitized !== candidate.replace(',', '.')) {
        e.preventDefault();
      }
    });

    input.addEventListener('input', () => {
      const sanitized = sanitizeWebInput(input.value, mode);
      if (input.value !== sanitized) {
        const pos = input.selectionStart ?? sanitized.length;
        input.value = sanitized;
        if (typeof input.setSelectionRange === 'function') {
          const clampedPos = Math.min(pos, sanitized.length);
          input.setSelectionRange(clampedPos, clampedPos);
        }
      }
    });

    input.addEventListener('blur', () => {
      const raw = sanitizeWebInput((input.value || '').trim(), mode);
      if (!raw || raw === '-' || raw === '.' || raw === '-.') {
        const fallback = allowEmpty ? '' : (!Number.isNaN(min) ? String(min) : '');
        if (input.value !== fallback) {
          input.value = fallback;
          input.dispatchEvent(new Event('input', { bubbles: true }));
        }
        return;
      }
      let num = Number(raw);
      if (Number.isNaN(num)) {
        input.value = allowEmpty ? '' : (!Number.isNaN(min) ? String(min) : '');
        input.dispatchEvent(new Event('input', { bubbles: true }));
        return;
      }
      if (mode === 'int') num = Math.round(num);
      if (!Number.isNaN(min) && num < min) num = min;
      if (!Number.isNaN(max) && num > max) num = max;
      const formatted = String(num);
      if (input.value !== formatted) {
        input.value = formatted;
        input.dispatchEvent(new Event('input', { bubbles: true }));
      }
    });
  });
}

let lastWebSystemMapSig = '';

function renderWebExoRadar(exo) {
  const canvas = document.getElementById('webExoRadarCanvas');
  if (!canvas || !exo) return;
  const ctx = canvas.getContext('2d');
  if (!ctx) return;

  const w = canvas.width;
  const h = canvas.height;
  const cx = w / 2;
  const cy = h / 2;
  const maxR = Math.min(cx, cy) - 10;
  ctx.clearRect(0, 0, w, h);

  const req = Math.max(50, Number(exo.RequiredDistanceMeters || 500));
  const points = Array.isArray(exo.SamplePoints) ? exo.SamplePoints : [];
  let maxDist = req * 1.4;
  for (const p of points) {
    if (p && p.DistanceMeters > maxDist) maxDist = p.DistanceMeters * 1.15;
  }
  const pxPerM = maxR / maxDist;

  // Concentric range rings
  ctx.strokeStyle = 'rgba(255,255,255,0.12)';
  ctx.lineWidth = 1;
  ctx.setLineDash([3, 3]);
  for (const frac of [0.33, 0.66, 1.0]) {
    ctx.beginPath();
    ctx.arc(cx, cy, maxR * frac, 0, Math.PI * 2);
    ctx.stroke();
  }
  ctx.setLineDash([]);

  // Crosshairs
  ctx.strokeStyle = 'rgba(255,255,255,0.08)';
  ctx.beginPath();
  ctx.moveTo(cx - maxR, cy); ctx.lineTo(cx + maxR, cy);
  ctx.moveTo(cx, cy - maxR); ctx.lineTo(cx, cy + maxR);
  ctx.stroke();

  // Rotated compass N indicator (heading-up)
  const headingRad = ((Number(exo.PlayerHeadingDegrees || 0)) * Math.PI) / 180;
  const nx = cx - Math.sin(headingRad) * (maxR - 2);
  const ny = cy - Math.cos(headingRad) * (maxR - 2);
  ctx.fillStyle = getComputedStyle(document.documentElement).getPropertyValue('--hud-accent') || '#FF7100';
  ctx.font = 'bold 9px Consolas, monospace';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText('N', nx, ny);

  // Sample points & minimum genetic diversity circles
  const minCircleR = req * pxPerM;
  for (const p of points) {
    const sx = cx + Number(p.RelativeX || 0) * pxPerM;
    const sy = cy - Number(p.RelativeY || 0) * pxPerM;
    const cleared = Number(p.DistanceMeters || 0) >= req;

    ctx.beginPath();
    ctx.arc(sx, sy, minCircleR, 0, Math.PI * 2);
    ctx.fillStyle = cleared ? 'rgba(66, 214, 116, 0.14)' : 'rgba(255, 76, 76, 0.18)';
    ctx.fill();
    ctx.strokeStyle = cleared ? '#42D674' : '#FF4C4C';
    ctx.lineWidth = 1.2;
    ctx.stroke();

    ctx.beginPath();
    ctx.arc(sx, sy, 3.5, 0, Math.PI * 2);
    ctx.fillStyle = cleared ? '#42D674' : '#FF4C4C';
    ctx.fill();
  }

  // Center player heading triangle (pointing UP)
  ctx.beginPath();
  ctx.moveTo(cx, cy - 6);
  ctx.lineTo(cx - 4.5, cy + 5);
  ctx.lineTo(cx + 4.5, cy + 5);
  ctx.closePath();
  ctx.fillStyle = '#FFFFFF';
  ctx.fill();
}

function toggleWebSystemMap() {
  const body = document.getElementById('webSystemMapBody');
  const btn = document.getElementById('btnWebToggleSystemMap');
  if (!body || !btn) return;
  const isNowHidden = !body.hidden;
  body.hidden = isNowHidden;
  btn.textContent = isNowHidden ? '🗺 2D SYSTEM MAP  ▾' : '🗺 2D SYSTEM MAP  ▴';
}
window.toggleWebSystemMap = toggleWebSystemMap;

function renderWebSystemMap(mapData) {
  const sec = document.getElementById('webSystemMapSection');
  const svg = document.getElementById('webSystemMapSvg');
  const detail = document.getElementById('webSystemMapDetail');
  if (!sec || !svg) return;

  const nodes = mapData && Array.isArray(mapData.nodes) ? mapData.nodes : [];
  if (nodes.length === 0) {
    sec.hidden = true;
    lastWebSystemMapSig = '';
    return;
  }

  sec.hidden = false;
  const sig = `${mapData.width}x${mapData.height}|` + nodes.map(n => `${n.shortLabel}:${n.fillHex}:${n.isStar ? 1 : 0}:${n.hasBioHalo ? 1 : 0}:${n.isSpanshOnly ? 1 : 0}`).join('|');
  if (sig === lastWebSystemMapSig) return;
  lastWebSystemMapSig = sig;
  if (detail) detail.hidden = true;

  const ns = "http://www.w3.org/2000/svg";
  svg.setAttribute('width', String(mapData.width || 420));
  svg.setAttribute('height', String(mapData.height || 150));
  svg.replaceChildren();

  const makeEl = (tag, attrs) => {
    const el = document.createElementNS(ns, tag);
    for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, String(v));
    return el;
  };

  const defs = makeEl('defs', {});
  svg.appendChild(defs);

  // Connect root stars vertically on the left, main horizontal bodies per star band,
  // direct moons vertically below their parent, and sub-moons via L-shaped connectors
  let prevRootStar = null;
  const lastHorizByBand = new Map();
  for (let i = 0; i < nodes.length; i++) {
    const n = nodes[i];
    const band = n.starBandIndex ?? 0;
    const indent = n.indentLevel ?? 0;
    if (n.isRootStar) {
      if (prevRootStar) {
        svg.appendChild(makeEl('line', {
          x1: prevRootStar.x, y1: prevRootStar.y + prevRootStar.radiusPx + 4,
          x2: n.x, y2: n.y - n.radiusPx - 4,
          stroke: 'rgba(255,255,255,0.22)', 'stroke-width': 1.4, 'stroke-dasharray': '3 3'
        }));
      }
      prevRootStar = n;
      lastHorizByBand.set(band, n);
    } else if (indent === 0) {
      const prev = lastHorizByBand.get(band);
      if (prev) {
        svg.appendChild(makeEl('line', {
          x1: prev.x + prev.radiusPx + (prev.isStar ? 4 : 0), y1: prev.y,
          x2: n.x - n.radiusPx - (n.isStar ? 4 : 0), y2: n.y,
          stroke: 'rgba(255,255,255,0.18)', 'stroke-width': 1.2
        }));
      }
      lastHorizByBand.set(band, n);
    } else if (n.parentIndex >= 0 && nodes[n.parentIndex]) {
      const p = nodes[n.parentIndex];
      if (Math.abs(n.x - p.x) < 1) {
        svg.appendChild(makeEl('line', {
          x1: p.x, y1: p.y + p.radiusPx, x2: n.x, y2: n.y - n.radiusPx,
          stroke: 'rgba(255,255,255,0.18)', 'stroke-width': 1.1
        }));
      } else {
        svg.appendChild(makeEl('line', {
          x1: p.x, y1: p.y + p.radiusPx, x2: p.x, y2: n.y,
          stroke: 'rgba(255,255,255,0.18)', 'stroke-width': 1.1
        }));
        svg.appendChild(makeEl('line', {
          x1: p.x, y1: n.y, x2: n.x - n.radiusPx, y2: n.y,
          stroke: 'rgba(255,255,255,0.18)', 'stroke-width': 1.1
        }));
      }
    }
  }

  // Binary pair top brackets
  const brackets = Array.isArray(mapData.brackets) ? mapData.brackets : [];
  for (const br of brackets) {
    svg.appendChild(makeEl('polyline', {
      points: `${br.x1},${br.anchorY1} ${br.x1},${br.topY} ${br.x2},${br.topY} ${br.x2},${br.anchorY2}`,
      fill: 'none', stroke: 'rgba(255, 195, 75, 0.72)', 'stroke-width': 1.3
    }));
  }

  // Nodes
  nodes.forEach((n, idx) => {
    const g = makeEl('g', { style: 'cursor:pointer;', opacity: n.isSpanshOnly ? '0.52' : '1' });

    let fillAttr = n.fillHex;
    if (n.isStar) {
      const glowId = `starGlow_${idx}`;
      const glowGrad = makeEl('radialGradient', { id: glowId });
      glowGrad.appendChild(makeEl('stop', { offset: '30%', 'stop-color': n.fillHex, 'stop-opacity': '0.65' }));
      glowGrad.appendChild(makeEl('stop', { offset: '70%', 'stop-color': n.fillHex, 'stop-opacity': '0.2' }));
      glowGrad.appendChild(makeEl('stop', { offset: '100%', 'stop-color': n.fillHex, 'stop-opacity': '0' }));
      defs.appendChild(glowGrad);
      g.appendChild(makeEl('circle', {
        cx: n.x, cy: n.y, r: n.radiusPx * 1.48,
        fill: `url(#${glowId})`
      }));

      if ((n.fillHex || '').toUpperCase() !== '#0B0E17') {
        const discId = `starDisc_${idx}`;
        const discGrad = makeEl('radialGradient', { id: discId, cx: '38%', cy: '38%', r: '62%' });
        const isBrownDwarf = (n.badgeText || '').toUpperCase() === 'L' || (n.badgeText || '').toUpperCase() === 'T' || (n.badgeText || '').toUpperCase() === 'Y';
        discGrad.appendChild(makeEl('stop', { offset: '0%', 'stop-color': isBrownDwarf ? '#D966A8' : '#FFFFFF' }));
        discGrad.appendChild(makeEl('stop', { offset: '55%', 'stop-color': n.fillHex }));
        discGrad.appendChild(makeEl('stop', { offset: '100%', 'stop-color': n.fillHex, 'stop-opacity': '0.78' }));
        defs.appendChild(discGrad);
        fillAttr = `url(#${discId})`;
      }
    } else if (n.isEarthLike) {
      const elwId = `elwDisc_${idx}`;
      const elwGrad = makeEl('linearGradient', { id: elwId, x1: '10%', y1: '10%', x2: '90%', y2: '90%' });
      elwGrad.appendChild(makeEl('stop', { offset: '0%', 'stop-color': '#EBF5FF' }));
      elwGrad.appendChild(makeEl('stop', { offset: '25%', 'stop-color': '#1E6BD6' }));
      elwGrad.appendChild(makeEl('stop', { offset: '58%', 'stop-color': '#388E3C' }));
      elwGrad.appendChild(makeEl('stop', { offset: '82%', 'stop-color': '#185AB4' }));
      elwGrad.appendChild(makeEl('stop', { offset: '100%', 'stop-color': '#786948' }));
      defs.appendChild(elwGrad);
      fillAttr = `url(#${elwId})`;
    }

    if (n.atmosphereHaloHex) {
      g.appendChild(makeEl('circle', {
        cx: n.x, cy: n.y, r: n.radiusPx + 3.2,
        fill: 'none', stroke: n.atmosphereHaloHex, 'stroke-width': 2.0, opacity: '0.55'
      }));
    }
    if (n.hasBioHalo) {
      g.appendChild(makeEl('circle', {
        cx: n.x, cy: n.y, r: n.radiusPx + 5.5,
        fill: 'none', stroke: '#42D674', 'stroke-width': 1.5, 'stroke-dasharray': '3 2'
      }));
    }
    g.appendChild(makeEl('circle', {
      cx: n.x, cy: n.y, r: n.radiusPx,
      fill: fillAttr, stroke: n.strokeHex, 'stroke-width': n.isStar ? 1 : (n.isHighGravity ? 2 : 1.2)
    }));
    if (n.hasRings) {
      g.appendChild(makeEl('ellipse', {
        cx: n.x, cy: n.y, rx: n.radiusPx * 1.68, ry: n.radiusPx * 0.48,
        fill: 'none', stroke: 'rgba(238,222,184,0.92)', 'stroke-width': n.isStar ? 2.6 : 2.1,
        transform: `rotate(-18 ${n.x} ${n.y})`
      }));
      g.appendChild(makeEl('ellipse', {
        cx: n.x, cy: n.y, rx: n.radiusPx * 1.34, ry: n.radiusPx * 0.36,
        fill: 'none', stroke: 'rgba(200,182,145,0.6)', 'stroke-width': 1.2,
        transform: `rotate(-18 ${n.x} ${n.y})`
      }));
    }

    const lbl = makeEl('text', {
      x: n.x, y: n.y + n.radiusPx + 13,
      fill: '#D8E2EE', 'font-size': 10, 'font-family': 'Consolas, monospace',
      'text-anchor': 'middle'
    });
    lbl.textContent = n.shortLabel || '';
    g.appendChild(lbl);

    if (n.badgeText) {
      const bdg = makeEl('text', {
        x: n.x, y: n.y - n.radiusPx - 5,
        fill: n.isStar ? n.fillHex : (n.isHighGravity ? '#FF6B6B' : '#42D674'),
        'font-size': 9, 'font-weight': '700', 'font-family': 'Consolas, monospace',
        'text-anchor': 'middle'
      });
      bdg.textContent = n.badgeText;
      g.appendChild(bdg);
    }

    g.addEventListener('click', () => {
      if (!detail) return;
      const parts = [
        `<strong style="color:var(--hud-accent);">${escapeHtml(n.bodyName || n.shortLabel)}</strong>`,
        escapeHtml(n.bodyType || '')
      ];
      if (n.distanceLs != null) parts.push(`${Number(n.distanceLs).toFixed(0)} Ls`);
      if (n.gravityG != null) parts.push(`${n.gravityG} G`);
      if (n.bioSignals > 0) parts.push(`🧬 Bio: ${n.bioSignals}`);
      if (n.geoSignals > 0) parts.push(`🌋 Geo: ${n.geoSignals}`);
      if (n.estimatedValue > 0) parts.push(`💰 ${Number(n.estimatedValue).toLocaleString()} CR`);
      if (n.dataSource) parts.push(`[${escapeHtml(n.dataSource)}]`);
      detail.innerHTML = parts.filter(Boolean).join(' &nbsp;·&nbsp; ');
      detail.hidden = false;
    });

    svg.appendChild(g);
  });
}

let lastWebRadioStationsSig = '';
let webRadioIsPlaying = false;

function renderWebCockpitRadio(status, stations) {
  const sel = document.getElementById('webRadioStation');
  const btn = document.getElementById('btnWebRadioToggle');
  const vol = document.getElementById('webRadioVolume');
  const stEl = document.getElementById('webRadioStatus');
  if (sel && Array.isArray(stations) && stations.length > 0) {
    const sig = stations.map(x => `${x.Id}:${x.Name}`).join('|');
    if (sig !== lastWebRadioStationsSig) {
      lastWebRadioStationsSig = sig;
      sel.replaceChildren();
      for (const x of stations) {
        const opt = document.createElement('option');
        opt.value = x.Id;
        opt.textContent = x.Name;
        sel.appendChild(opt);
      }
    }
  }
  if (status) {
    webRadioIsPlaying = !!status.IsPlaying;
    if (sel && status.StationId && document.activeElement !== sel) sel.value = status.StationId;
    if (btn) btn.textContent = webRadioIsPlaying ? '■ Stop' : '▶ Play';
    if (vol && document.activeElement !== vol) vol.value = String(status.VolumePercent ?? 45);
    if (stEl) stEl.textContent = status.IsDucked ? `${status.StatusMessage} (Ducked)` : (status.StatusMessage || 'Standby');
  }
}

async function toggleWebRadio() {
  const sel = document.getElementById('webRadioStation');
  const stationId = sel ? sel.value : '';
  try {
    await fetch('/api/radio', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ action: webRadioIsPlaying ? 'stop' : 'play', stationId })
    });
  } catch { }
}
window.toggleWebRadio = toggleWebRadio;

async function changeWebRadioStation() {
  if (!webRadioIsPlaying) return;
  const sel = document.getElementById('webRadioStation');
  if (!sel) return;
  try {
    await fetch('/api/radio', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ action: 'play', stationId: sel.value })
    });
  } catch { }
}
window.changeWebRadioStation = changeWebRadioStation;

async function setWebRadioVolume(val) {
  try {
    await fetch('/api/radio', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ action: 'volume', volume: Number(val) })
    });
  } catch { }
}
window.setWebRadioVolume = setWebRadioVolume;

// Do not auto-register Service Worker on self-signed HTTPS localhost certs,
// as Chromium rejects SW registration with SSL certificate warnings on every load.
// PWA installability is still provided via /manifest.json and /icon.svg.
if ('serviceWorker' in navigator && navigator.serviceWorker.getRegistrations) {
  navigator.serviceWorker.getRegistrations()
    .then(regs => regs.forEach(r => r.unregister().catch(() => { })))
    .catch(() => { });
}

initWebInputConstraints();
updateSectionTabs();
connectWs();
