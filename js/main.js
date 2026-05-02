'use strict';

import { initTheme } from './core/theme.js';
import { wireUserMenu, wirePromptModal, wireSettingsModal } from './ui/menus.js';
import { wireSendAction, mountStatusPill } from './ui/actions.js';
import { initAuthGate, wireLogout } from './auth/logout.js';
import { wireUploads } from './features/uploads.js';
import { regenerateFromEditedMessage } from './net/ollama.js';
import('./features/math/katex-init.js')
  .then(({ initKatex }) => {
    initKatex();
  })
  .catch((err) => console.warn('[KaTeX] not loaded:', err));
import { mountHistory, Store } from './store/conversations.js';

const SEL = {
  newChatBtn: '#new-chat',
  sendBtn: '#send-btn',
  composer: '#composer-input',
  chatLog: '#chat-log',
  composerForm: '#composer-form',
};

const state = {
  awaitingFirstPrompt: false,
  currentConvId: null,
  chatObserver: null,
};

const SIDEBAR_RESIZE_MAX_CM = 5;
const CSS_CM_TO_PX = 96 / 2.54;

function $(sel) { return document.querySelector(sel); }

window.kivrioSaveMessageEdit = async ({ conversationId, messageId, content }) => {
  if (!conversationId || messageId == null) {
    throw new Error('Message introuvable.');
  }
  return regenerateFromEditedMessage({ conversationId, messageId, content });
};

function hasCurrentConversation() {
  try {
    return !!Store?.currentId?.();
  } catch (_) {
    return false;
  }
}

function makeTitleFromText(txt, max = 60) {
  const clean = (txt || '')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/^([#>*\-]+\s*)+/, '');
  const first = clean.split(/(?<=[.!?])\s+/)[0] || clean;
  return first.length > max ? first.slice(0, max - 1) + '...' : first;
}

function cancelOngoingStream() {
  try {
    if (window.kivrioAbortController && typeof window.kivrioAbortController.abort === 'function') {
      window.kivrioAbortController.abort();
    } else {
      document.dispatchEvent(new CustomEvent('ai:cancel'));
    }
  } catch (e) {
    console.warn('[cancelOngoingStream] noop', e);
  }
}

function clearChatUI() {
  try {
    if (typeof window.clearChat === 'function') {
      window.clearChat();
      return;
    }
  } catch (_) {}
  const log = $(SEL.chatLog);
  if (log) log.innerHTML = '';
  try { window.kivrioClearPendingUploads?.(); } catch (_) {}
}

function getLatestUserTextFromChatLog() {
  const log = $(SEL.chatLog);
  if (!log) return '';
  const userMsgs = log.querySelectorAll('[data-role="user"], .msg.user, .message.user, .bubble.user, .chat-user');
  const last = userMsgs[userMsgs.length - 1];
  if (!last) return '';
  const textEl = last.querySelector('[data-type="text"], .text, .content, p, span');
  const raw = (textEl ? textEl.textContent : last.textContent) || '';
  return raw.trim();
}

async function createConversationWithBestEffort(title) {
  if (window.kivrioEnsureConversationPromise) {
    return window.kivrioEnsureConversationPromise;
  }

  window.kivrioEnsureConversationPromise = (async () => {
    let id = null;
    try {
      if (typeof Store?.create === 'function') {
        const conversation = await Store.create({ title });
        id = conversation?.id || null;
      } else if (typeof window.kivrioCreateConversation === 'function') {
        id = await window.kivrioCreateConversation(title);
      } else {
        document.dispatchEvent(new CustomEvent('conversation:create', { detail: { title } }));
      }
    } finally {
      try { await mountHistory(); } catch (e) { console.warn('[mountHistory] failed', e); }
    }
    return id;
  })();

  try {
    return await window.kivrioEnsureConversationPromise;
  } finally {
    window.kivrioEnsureConversationPromise = null;
  }
}

function wireNewChatButton() {
  const btn = $(SEL.newChatBtn);
  if (!btn) return;

  btn.addEventListener('click', (ev) => {
    ev.preventDefault();
    ev.stopImmediatePropagation();

    cancelOngoingStream();
    clearChatUI();

    try { if (typeof Store?.clearCurrent === 'function') Store.clearCurrent(); } catch (_) {}
    state.currentConvId = null;
    state.awaitingFirstPrompt = true;

    const input = $(SEL.composer);
    if (input) { input.value = ''; input.focus(); }
  }, { capture: true });
}

function wireEnsureConversationAtFirstPromptDelegated() {
  async function ensure() {
    if (!state.awaitingFirstPrompt) return;

    let text = ($(SEL.composer)?.value ?? '').trim();
    if (!text) text = getLatestUserTextFromChatLog();
    if (!text) return;
    if (hasCurrentConversation()) {
      state.currentConvId = Store.currentId?.() || state.currentConvId;
      state.awaitingFirstPrompt = false;
      return;
    }

    const title = makeTitleFromText(text);
    const id = await createConversationWithBestEffort(title);

    state.currentConvId = id || null;
    state.awaitingFirstPrompt = false;
  }

  document.addEventListener('click', (ev) => {
    if (ev.target && ev.target.closest && ev.target.closest(SEL.sendBtn)) {
      ensure();
    }
  }, { capture: true });

  document.addEventListener('keydown', (ev) => {
    if (
      ev.key === 'Enter'
      && !ev.shiftKey
      && (ev.ctrlKey || ev.metaKey)
      && ev.target
      && ev.target.closest
      && ev.target.closest(SEL.composer)
    ) {
      ensure();
    }
  }, { capture: true });

  document.addEventListener('submit', (ev) => {
    if (ev.target && ev.target.matches && ev.target.matches(SEL.composerForm)) {
      ensure();
    }
  }, { capture: true });
}

function startChatLogObserver() {
  const log = $(SEL.chatLog);
  if (!log || state.chatObserver) return;

  const obs = new MutationObserver(() => {
    if (!state.awaitingFirstPrompt) return;
    const text = getLatestUserTextFromChatLog();
    if (!text) return;
    Promise.resolve().then(async () => {
      if (!state.awaitingFirstPrompt) return;
      if (hasCurrentConversation()) {
        state.currentConvId = Store.currentId?.() || state.currentConvId;
        state.awaitingFirstPrompt = false;
        return;
      }
      const title = makeTitleFromText(text);
      const id = await createConversationWithBestEffort(title);
      state.currentConvId = id || null;
      state.awaitingFirstPrompt = false;
    });
  });

  obs.observe(log, { childList: true, subtree: true });
  state.chatObserver = obs;
}

function wireSidebarResize() {
  const app = document.querySelector('.app');
  const sidebar = document.querySelector('.sidebar');
  const handle = document.getElementById('sidebar-resizer');
  const closeBtn = document.getElementById('sidebar-toggle-close');
  const openBtn = document.getElementById('sidebar-toggle-open');
  if (!app || !sidebar || !handle || !closeBtn || !openBtn) return;

  const root = document.documentElement;
  const baseWidth = Math.round(sidebar.getBoundingClientRect().width);
  const minWidth = baseWidth;
  const maxWidth = Math.round(baseWidth + (SIDEBAR_RESIZE_MAX_CM * CSS_CM_TO_PX));
  const MIN_MAIN_WIDTH = 360;

  let drag = null;
  let lastOpenWidth = minWidth;

  function syncToggleState(isOpen) {
    closeBtn.setAttribute('aria-expanded', String(isOpen));
    openBtn.setAttribute('aria-expanded', String(isOpen));
  }

  function applyWidth(width) {
    root.style.setProperty('--sidebar-width', `${width}px`);
    handle.setAttribute('aria-valuemin', String(minWidth));
    handle.setAttribute('aria-valuemax', String(maxWidth));
    handle.setAttribute('aria-valuenow', String(Math.round(width)));
  }

  function effectiveMaxWidth() {
    const viewportBound = Math.max(minWidth, app.clientWidth - MIN_MAIN_WIDTH);
    return Math.max(minWidth, Math.min(maxWidth, viewportBound));
  }

  function clampWidth(width) {
    return Math.min(effectiveMaxWidth(), Math.max(minWidth, width));
  }

  function stopDrag(pointerId = null) {
    if (!drag) return;
    if (pointerId !== null && drag.pointerId !== pointerId) return;

    drag = null;
    document.body.classList.remove('sidebar-resize-active');
    handle.classList.remove('is-active');

    if (typeof handle.releasePointerCapture === 'function' && pointerId !== null && handle.hasPointerCapture(pointerId)) {
      handle.releasePointerCapture(pointerId);
    }
  }

  applyWidth(minWidth);
  syncToggleState(true);

  function closeSidebar() {
    if (app.classList.contains('sidebar-collapsed')) return;
    stopDrag();
    const currentWidth = parseFloat(getComputedStyle(root).getPropertyValue('--sidebar-width')) || minWidth;
    lastOpenWidth = clampWidth(currentWidth);
    applyWidth(lastOpenWidth);
    app.classList.add('sidebar-collapsed');
    syncToggleState(false);
  }

  function openSidebar() {
    if (!app.classList.contains('sidebar-collapsed')) return;
    app.classList.remove('sidebar-collapsed');
    applyWidth(clampWidth(lastOpenWidth));
    syncToggleState(true);
  }

  handle.addEventListener('pointerdown', (event) => {
    if (app.classList.contains('sidebar-collapsed')) return;
    if (event.button !== 0) return;
    drag = {
      pointerId: event.pointerId,
      startX: event.clientX,
      startWidth: sidebar.getBoundingClientRect().width,
    };
    document.body.classList.add('sidebar-resize-active');
    handle.classList.add('is-active');
    if (typeof handle.setPointerCapture === 'function') {
      handle.setPointerCapture(event.pointerId);
    }
    event.preventDefault();
  });

  handle.addEventListener('pointermove', (event) => {
    if (!drag || drag.pointerId !== event.pointerId) return;
    const nextWidth = clampWidth(drag.startWidth + (event.clientX - drag.startX));
    lastOpenWidth = nextWidth;
    applyWidth(nextWidth);
    event.preventDefault();
  });

  handle.addEventListener('pointerup', (event) => {
    stopDrag(event.pointerId);
  });

  handle.addEventListener('pointercancel', (event) => {
    stopDrag(event.pointerId);
  });

  window.addEventListener('resize', () => {
    const currentWidth = app.classList.contains('sidebar-collapsed')
      ? lastOpenWidth
      : (parseFloat(getComputedStyle(root).getPropertyValue('--sidebar-width')) || minWidth);
    const nextWidth = clampWidth(currentWidth);
    lastOpenWidth = nextWidth;
    applyWidth(nextWidth);
  });

  closeBtn.addEventListener('click', () => {
    closeSidebar();
  });

  openBtn.addEventListener('click', () => {
    openSidebar();
  });
}

document.addEventListener('DOMContentLoaded', async () => {
  initTheme();
  const auth = await initAuthGate();

  // A blank startup screen must not silently keep the previous conversation active.
  try { if (typeof Store?.clearCurrent === 'function') Store.clearCurrent(); } catch (_) {}
  state.currentConvId = null;

  if (auth.authenticated) {
    try { await mountHistory(); } catch (e) { console.warn('[mountHistory] failed', e); }
  }
  wireUserMenu();
  wirePromptModal();
  wireSettingsModal();
  wireSendAction();
  mountStatusPill({ authenticated: auth.authenticated });
  wireLogout();
  wireUploads();
  wireSidebarResize();

  wireNewChatButton();
  wireEnsureConversationAtFirstPromptDelegated();
  startChatLogObserver();

  state.awaitingFirstPrompt = true;
});
