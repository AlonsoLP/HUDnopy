// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        docs/app.js
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Interactive MFD simulator, theme switcher, i18n, release resolver, and starfield.
// -----------------------------------------------------------------------------

// Interactive & Auto-Rotating HUDnopy App Preview Switcher
const MFD_ROTATE_MS = 5500;
const MFD_STATUS_MESSAGES = [
  '✓ Telemetría sincronizada · Web MFD HTTPS activo (PWA)',
  '✓ Mapa 2D sincronizado · Radar Exobiología activo (800m)',
  '✓ Botonera táctil cargada · ★ Favoritos y macros listos',
  '✓ Whisper Offline + Piper Neural TTS · Radio en reproducción',
  '✓ Wishlist Ingeniería y Fleet Carrier actualizados'
];
let mfdCurrentIndex = 0;
let mfdTimerId = null;
let mfdPaused = false;

function switchMfd(index, btn, manual) {
  const screens = document.querySelectorAll('.mfd-screen');
  const tabs = document.querySelectorAll('.mfd-tab');
  if (!screens.length) return;
  mfdCurrentIndex = ((index % screens.length) + screens.length) % screens.length;
  screens.forEach((el, i) => el.classList.toggle('active', i === mfdCurrentIndex));
  tabs.forEach((b, i) => b.classList.toggle('active', btn ? b === btn : i === mfdCurrentIndex));
  const statusEl = document.getElementById('simStatusMsg');
  if (statusEl) statusEl.textContent = MFD_STATUS_MESSAGES[mfdCurrentIndex] || MFD_STATUS_MESSAGES[0];
  restartMfdProgressBar();
  if (manual) scheduleNextMfd();
}

function restartMfdProgressBar() {
  const bar = document.getElementById('mfdProgressBar');
  if (!bar) return;
  bar.style.transition = 'none';
  bar.style.width = '0%';
  if (mfdPaused) return;
  requestAnimationFrame(() => {
    requestAnimationFrame(() => {
      bar.style.transition = `width ${MFD_ROTATE_MS}ms linear`;
      bar.style.width = '100%';
    });
  });
}

function scheduleNextMfd() {
  if (mfdTimerId) clearTimeout(mfdTimerId);
  if (mfdPaused) return;
  mfdTimerId = setTimeout(() => {
    switchMfd(mfdCurrentIndex + 1, null, false);
    scheduleNextMfd();
  }, MFD_ROTATE_MS);
}

(function initMfdAutoRotate() {
  const frame = document.getElementById('hudPreviewFrame');
  if (!frame) return;
  frame.addEventListener('mouseenter', () => {
    mfdPaused = true;
    if (mfdTimerId) clearTimeout(mfdTimerId);
    const bar = document.getElementById('mfdProgressBar');
    if (bar) {
      bar.style.transition = 'none';
      bar.style.width = '0%';
    }
  });
  frame.addEventListener('mouseleave', () => {
    mfdPaused = false;
    restartMfdProgressBar();
    scheduleNextMfd();
  });
  restartMfdProgressBar();
  scheduleNextMfd();
})();

// Live HUD Theme Switcher
function setHudTheme(hex, rgb, secondary, el) {
  document.documentElement.style.setProperty('--hud-accent', hex);
  document.documentElement.style.setProperty('--hud-accent-rgb', rgb);
  document.documentElement.style.setProperty('--hud-secondary', secondary);
  document.querySelectorAll('.swatch').forEach(s => s.classList.remove('active'));
  el.classList.add('active');
}

function setLang(lang) {
  const dict = i18n[lang] || i18n.en;
  document.documentElement.lang = lang;
  const sel = document.getElementById('langSelect');
  if (sel && sel.value !== lang) sel.value = lang;
  document.querySelectorAll('[data-i18n]').forEach(el => {
    const key = el.getAttribute('data-i18n');
    if (dict[key]) el.innerHTML = dict[key];
  });
}

// Dynamic GitHub Release Link Resolver
fetch('https://api.github.com/repos/AlonsoLP/HUDnopy/releases/latest')
  .then(r => r.ok ? r.json() : null)
  .then(rel => {
    if (!rel || !rel.tag_name) return;
    const badge = document.getElementById('releaseBadgeText');
    if (badge) badge.textContent = `RELEASE ${rel.tag_name.toUpperCase()} AVAILABLE · WINDOWS x64`;
    if (Array.isArray(rel.assets)) {
      const exe = rel.assets.find(a => a.name && a.name.endsWith('-Setup.exe'));
      const zip = rel.assets.find(a => a.name && a.name.endsWith('-portable.zip'));
      if (exe && exe.browser_download_url) document.getElementById('btnDownloadSetup').href = exe.browser_download_url;
      if (zip && zip.browser_download_url) document.getElementById('btnDownloadZip').href = zip.browser_download_url;
    }
  })
  .catch(() => {});

// Lightweight Cockpit Starfield Canvas
(function initStarfield() {
  const canvas = document.getElementById('starfield');
  const ctx = canvas.getContext('2d');
  let stars = [];
  function resize() {
    canvas.width = window.innerWidth;
    canvas.height = window.innerHeight;
    stars = Array.from({ length: 90 }, () => ({
      x: Math.random() * canvas.width,
      y: Math.random() * canvas.height,
      r: Math.random() * 1.4 + 0.3,
      a: Math.random() * 0.7 + 0.2,
      s: Math.random() * 0.25 + 0.05
    }));
  }
  window.addEventListener('resize', resize);
  resize();
  function draw() {
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    for (const st of stars) {
      st.y -= st.s;
      if (st.y < 0) st.y = canvas.height;
      ctx.fillStyle = `rgba(242, 245, 250, ${st.a})`;
      ctx.beginPath();
      ctx.arc(st.x, st.y, st.r, 0, Math.PI * 2);
      ctx.fill();
    }
    requestAnimationFrame(draw);
  }
  draw();
})();
