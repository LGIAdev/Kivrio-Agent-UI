import { mountHistory, Store } from '../store/conversations.js';
import { getAuthStatus, login, logout, setupPassword } from '../net/conversationsApi.js';

const OVERLAY_ID = 'kivrio-login-overlay';
let authEnabled = true;
let setupRequired = false;

function ensureOverlay() {
  let overlay = document.getElementById(OVERLAY_ID);
  if (overlay) return overlay;

  overlay = document.createElement('div');
  overlay.id = OVERLAY_ID;
  overlay.className = 'modal-overlay';
  overlay.style.display = 'grid';
  overlay.style.position = 'fixed';
  overlay.style.inset = '0';
  overlay.style.zIndex = '9999';
  overlay.style.background = 'rgba(3, 8, 20, 0.86)';
  overlay.innerHTML = ''
    + '<div class="login-splash" role="document">'
    + '  <div class="login-card">'
    + '    <div class="login-brand">Kivrio Agent UI</div>'
    + '    <div id="login-title" class="login-title">Connectez-vous a Kivrio Agent UI</div>'
    + '    <p id="login-hint" class="login-hint" hidden></p>'
    + '    <form id="login-form" class="login-form">'
    + '      <div class="login-field">'
    + '        <label class="login-label" for="login-password">Mot de passe</label>'
    + '        <input id="login-password" class="login-input" name="password" type="password" autocomplete="current-password" />'
    + '      </div>'
    + '      <div id="login-confirm-wrap" class="login-field" hidden>'
    + '        <label class="login-label" for="login-password-confirm">Confirmer le mot de passe</label>'
    + '        <input id="login-password-confirm" class="login-input" name="password_confirm" type="password" autocomplete="new-password" />'
    + '      </div>'
    + '      <p id="login-error" class="login-error" hidden></p>'
    + '      <button id="login-btn" class="login-btn" type="submit" aria-label="Connexion">Connexion</button>'
    + '    </form>'
    + '  </div>'
    + '</div>';

  document.body.appendChild(overlay);
  return overlay;
}

function setLoginError(message = '') {
  const node = document.getElementById('login-error');
  if (!node) return;
  node.textContent = message;
  node.hidden = !message;
}

function hideOverlay() {
  const overlay = document.getElementById(OVERLAY_ID);
  if (overlay) overlay.style.display = 'none';
}

function applyOverlayMode() {
  const title = document.getElementById('login-title');
  const hint = document.getElementById('login-hint');
  const password = document.getElementById('login-password');
  const confirmWrap = document.getElementById('login-confirm-wrap');
  const confirmInput = document.getElementById('login-password-confirm');
  const button = document.getElementById('login-btn');

  if (title) {
    title.textContent = setupRequired ? 'Creez votre mot de passe Kivrio Agent UI' : 'Connectez-vous a Kivrio Agent UI';
  }

  if (hint) {
    hint.hidden = !setupRequired;
    hint.textContent = setupRequired
      ? 'Premier lancement: choisissez un mot de passe personnel pour proteger Kivrio Agent UI sur cet appareil.'
      : '';
  }

  if (confirmWrap) {
    confirmWrap.hidden = !setupRequired;
  }

  if (password instanceof HTMLInputElement) {
    password.autocomplete = setupRequired ? 'new-password' : 'current-password';
  }

  if (confirmInput instanceof HTMLInputElement) {
    confirmInput.value = '';
  }

  if (button) {
    button.textContent = setupRequired ? 'Enregistrer' : 'Connexion';
    button.setAttribute('aria-label', setupRequired ? 'Creer le mot de passe' : 'Connexion');
  }
}

async function completeAuthSuccess() {
  hideOverlay();
  try { await mountHistory(); } catch (_) {}
  window.dispatchEvent(new CustomEvent('kivro:auth-success'));
}

export function renderLoginSplash(message = '') {
  if (!authEnabled) return;

  const overlay = ensureOverlay();
  overlay.style.display = 'grid';
  applyOverlayMode();
  setLoginError(message);

  const form = document.getElementById('login-form');
  const passwordInput = document.getElementById('login-password');
  const confirmInput = document.getElementById('login-password-confirm');
  const button = document.getElementById('login-btn');

  if (form && !form.dataset.bound) {
    form.dataset.bound = '1';
    form.addEventListener('submit', async (ev) => {
      ev.preventDefault();
      if (!(passwordInput instanceof HTMLInputElement) || !(button instanceof HTMLButtonElement)) return;

      const password = passwordInput.value;
      const passwordConfirm = confirmInput instanceof HTMLInputElement ? confirmInput.value : '';
      button.disabled = true;
      setLoginError('');

      try {
        if (setupRequired) {
          if (password !== passwordConfirm) {
            throw new Error('Les mots de passe ne correspondent pas.');
          }
          await setupPassword(password);
          setupRequired = false;
        } else {
          await login(password);
        }

        passwordInput.value = '';
        if (confirmInput instanceof HTMLInputElement) {
          confirmInput.value = '';
        }
        await completeAuthSuccess();
      } catch (err) {
        const messageText = err?.message || 'Connexion impossible.';
        if (messageText === 'Password setup required.') {
          setupRequired = true;
          applyOverlayMode();
          setLoginError('Choisissez d abord votre mot de passe.');
        } else {
          setLoginError(messageText);
        }
      } finally {
        button.disabled = false;
        passwordInput.focus();
      }
    });
  }

  if (passwordInput instanceof HTMLInputElement) {
    passwordInput.focus();
    passwordInput.select();
  }
}

export function shouldShowLoggedOutSplash() {
  return authEnabled;
}

export async function initAuthGate() {
  try {
    const status = await getAuthStatus();
    authEnabled = Boolean(status?.enabled);
    setupRequired = Boolean(status?.setupRequired);
    if (authEnabled && (setupRequired || !status?.authenticated)) {
      renderLoginSplash();
    } else {
      hideOverlay();
    }
    return {
      enabled: authEnabled,
      authenticated: !authEnabled || Boolean(status?.authenticated),
    };
  } catch (_) {
    authEnabled = true;
    setupRequired = false;
    renderLoginSplash('Serveur indisponible.');
    return {
      enabled: true,
      authenticated: false,
    };
  }
}

export function wireLogout() {
  const le = document.getElementById('logout-entry');
  if (!le) return;

  window.addEventListener('kivro:auth-required', (event) => {
    setupRequired = false;
    renderLoginSplash(event?.detail?.message || 'Session requise.');
  });

  const doLogout = async (e) => {
    if (e) e.preventDefault();
    try { await logout(); } catch (_) {}
    try { Store.clearCurrent(); } catch (_) {}
    setupRequired = false;
    renderLoginSplash('Session fermee.');
  };

  le.addEventListener('click', doLogout);
  le.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' || e.key === ' ') {
      doLogout(e);
    }
  });
}
