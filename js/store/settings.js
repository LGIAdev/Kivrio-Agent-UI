// settings.js — source de vérité + compat LEGACY
import { DEFAULT_AGENT_PROFILE, normalizeAgentProfile } from '../config/agent-profiles.js';
import { DEFAULT_CODING_AGENT, normalizeCodingAgent } from '../config/coding-agents.js';

const KEY = 'kivrio_settings_v1';
const LEGACY_KEY = 'kivro_settings_v1';
const LEGACY_MODEL_KEY = 'ollamaModel';
const DEFAULT_OPENCODE_WORKSPACE = Object.freeze({
  baseFolder: 'documents',
  workDirectory: 'OpenCode',
  customBasePath: '',
});
const DEFAULT_CLAUDE_WORKSPACE = Object.freeze({
  baseFolder: 'documents',
  workDirectory: 'Claude',
  customBasePath: '',
});

// Lecture sûre localStorage
const getLS = (k) => { try { return localStorage.getItem(k); } catch { return null; } };
const setLS = (k, v) => { try { localStorage.setItem(k, v); } catch {} };

function normalizeWorkspace(value, defaults) {
  const raw = value && typeof value === 'object' ? value : {};
  const allowedBaseFolders = new Set(['documents', 'desktop', 'pictures', 'downloads', 'custom']);
  const baseFolder = allowedBaseFolders.has(String(raw.baseFolder || '').trim().toLowerCase())
    ? String(raw.baseFolder).trim().toLowerCase()
    : defaults.baseFolder;
  const workDirectory = String(raw.workDirectory || '').trim() || defaults.workDirectory;
  const customBasePath = String(raw.customBasePath || '').trim();
  return { baseFolder, workDirectory, customBasePath };
}

function normalizeOpenCodeWorkspace(value) {
  return normalizeWorkspace(value, DEFAULT_OPENCODE_WORKSPACE);
}

function normalizeClaudeWorkspace(value) {
  return normalizeWorkspace(value, DEFAULT_CLAUDE_WORKSPACE);
}

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
    const base = {
      model: null,
      ollama_url: 'http://127.0.0.1:11434',
      agent_profile: DEFAULT_AGENT_PROFILE,
      coding_agent: DEFAULT_CODING_AGENT,
      opencode_workspace: DEFAULT_OPENCODE_WORKSPACE,
      claude_workspace: DEFAULT_CLAUDE_WORKSPACE,
    };
    const st = Object.assign(base, json);
    // Fallback : si pas de modèle en state, lire ancienne clé 'ollamaModel'
    if (!st.model) {
      const legacy = (getLS(LEGACY_MODEL_KEY) || '').trim();
      if (legacy) st.model = legacy;
    }
    st.agent_profile = normalizeAgentProfile(st.agent_profile);
    st.coding_agent = normalizeCodingAgent(st.coding_agent);
    st.opencode_workspace = normalizeOpenCodeWorkspace(st.opencode_workspace);
    st.claude_workspace = normalizeClaudeWorkspace(st.claude_workspace);
    return st;
  } catch {
    // Fallback dur si JSON cassé
    const legacy = (getLS(LEGACY_MODEL_KEY) || '').trim();
    return {
      model: legacy || null,
      ollama_url: 'http://127.0.0.1:11434',
      agent_profile: DEFAULT_AGENT_PROFILE,
      coding_agent: DEFAULT_CODING_AGENT,
      opencode_workspace: DEFAULT_OPENCODE_WORKSPACE,
      claude_workspace: DEFAULT_CLAUDE_WORKSPACE
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

export const getAgentProfile = () => normalizeAgentProfile(state.agent_profile);

export const setAgentProfile = (profile) => {
  state.agent_profile = normalizeAgentProfile(profile);
  persist();
  document.dispatchEvent(new CustomEvent('settings:agent-profile-changed', { detail: state.agent_profile }));
};

export const getCodingAgent = () => normalizeCodingAgent(state.coding_agent);

export const setCodingAgent = (agent) => {
  state.coding_agent = normalizeCodingAgent(agent);
  persist();
  document.dispatchEvent(new CustomEvent('settings:coding-agent-changed', { detail: state.coding_agent }));
};

export const getOpenCodeWorkspace = () => ({ ...normalizeOpenCodeWorkspace(state.opencode_workspace) });

export const setOpenCodeWorkspace = (settings) => {
  state.opencode_workspace = normalizeOpenCodeWorkspace(settings);
  persist();
  document.dispatchEvent(new CustomEvent('settings:opencode-workspace-changed', {
    detail: { ...state.opencode_workspace },
  }));
};

export const getClaudeWorkspace = () => ({ ...normalizeClaudeWorkspace(state.claude_workspace) });

export const setClaudeWorkspace = (settings) => {
  state.claude_workspace = normalizeClaudeWorkspace(settings);
  persist();
  document.dispatchEvent(new CustomEvent('settings:claude-workspace-changed', {
    detail: { ...state.claude_workspace },
  }));
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
      const nextProfile = normalizeAgentProfile(next.agent_profile);
      if (nextProfile !== state.agent_profile) {
        state.agent_profile = nextProfile;
        document.dispatchEvent(new CustomEvent('settings:agent-profile-changed', { detail: state.agent_profile }));
      }
      const nextAgent = normalizeCodingAgent(next.coding_agent);
      if (nextAgent !== state.coding_agent) {
        state.coding_agent = nextAgent;
        document.dispatchEvent(new CustomEvent('settings:coding-agent-changed', { detail: state.coding_agent }));
      }
      const nextWorkspace = normalizeOpenCodeWorkspace(next.opencode_workspace);
      if (JSON.stringify(nextWorkspace) !== JSON.stringify(state.opencode_workspace)) {
        state.opencode_workspace = nextWorkspace;
        document.dispatchEvent(new CustomEvent('settings:opencode-workspace-changed', {
          detail: { ...state.opencode_workspace },
        }));
      }
      const nextClaudeWorkspace = normalizeClaudeWorkspace(next.claude_workspace);
      if (JSON.stringify(nextClaudeWorkspace) !== JSON.stringify(state.claude_workspace)) {
        state.claude_workspace = nextClaudeWorkspace;
        document.dispatchEvent(new CustomEvent('settings:claude-workspace-changed', {
          detail: { ...state.claude_workspace },
        }));
      }
    } catch {}
  }
});
