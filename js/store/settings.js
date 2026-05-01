// settings.js — source de vérité + compat LEGACY
const KEY = 'kivrio_settings_v1';
const LEGACY_KEY = 'kivro_settings_v1';
const LEGACY_MODEL_KEY = 'ollamaModel';

// Lecture sûre localStorage
const getLS = (k) => { try { return localStorage.getItem(k); } catch { return null; } };
const setLS = (k, v) => { try { localStorage.setItem(k, v); } catch {} };

function readSettingsJson(preferredKey = KEY) {
  const raw = getLS(preferredKey);
  if (raw) return raw;
  if (preferredKey !== KEY) {
    const next = getLS(KEY);
    if (next) return next;
  }
  return getLS(LEGACY_KEY) || '{}';
}

// État initial : JSON + fallback legacy
const initial = (() => {
  try {
    const json = JSON.parse(readSettingsJson());
    const base = { model: null, ollama_url: 'http://127.0.0.1:11434' };
    const st = Object.assign(base, json);
    // Fallback : si pas de modèle en state, lire ancienne clé 'ollamaModel'
    if (!st.model) {
      const legacy = (getLS(LEGACY_MODEL_KEY) || '').trim();
      if (legacy) st.model = legacy;
    }
    return st;
  } catch {
    // Fallback dur si JSON cassé
    const legacy = (getLS(LEGACY_MODEL_KEY) || '').trim();
    return {
      model: legacy || null,
      ollama_url: 'http://127.0.0.1:11434'
    };
  }
})();

const state = { ...initial };

// Persistance : JSON + miroir legacy pour compat avec ollama.js
const persist = () => {
  setLS(KEY, JSON.stringify(state));
  if (typeof state.model === 'string' && state.model.trim()) {
    setLS(LEGACY_MODEL_KEY, state.model.trim()); // 🔁 compat historique
  }
};

persist();

// API publique
export const getModel = () => state.model;

export const setModel = (m) => {
  state.model = (m || '').trim() || null;
  persist();
  // Notifie toute l’UI (sélecteur, badge Ok, etc.)
  document.dispatchEvent(new CustomEvent('settings:model-changed', { detail: state.model }));
};

export const getOllamaUrl = () => state.ollama_url.replace(/\/+$/, '');

export const setOllamaUrl = (u) => {
  state.ollama_url = (u || '').trim() || 'http://127.0.0.1:11434';
  persist();
};

// (optionnel) Sync inter-onglets si besoin
window.addEventListener?.('storage', (ev) => {
  if (ev.key === KEY || ev.key === LEGACY_KEY || ev.key === LEGACY_MODEL_KEY) {
    try {
      const next = JSON.parse(readSettingsJson(ev.key === LEGACY_KEY ? LEGACY_KEY : KEY));
      if (next.model && next.model !== state.model) {
        state.model = next.model;
        document.dispatchEvent(new CustomEvent('settings:model-changed', { detail: state.model }));
      } else if (!next.model) {
        const legacy = (getLS(LEGACY_MODEL_KEY) || '').trim();
        if (legacy && legacy !== state.model) {
          state.model = legacy;
          document.dispatchEvent(new CustomEvent('settings:model-changed', { detail: state.model }));
        }
      }
    } catch {}
  }
});
