// js/net/ollama.js
// Flux reseau Ollama + rendu des messages (compatible KaTeX)

import { bindMessageRecord, renderMsg, updateBubbleContent } from '../chat/render.js';
import { Store, fmtTitle, mountHistory } from '../store/conversations.js';
import { getAgentProfile, getCodingAgent } from '../store/settings.js';
import { getAgentProfileDefinition } from '../config/agent-profiles.js';
import { getCodingAgentDefinition, normalizeCodingAgent } from '../config/coding-agents.js';
import { qs } from '../core/dom.js';
import {
  detachPendingUploads,
  getPendingUploads,
  preparePendingUploadsForSend,
  releaseUploadItems,
  restorePendingUploads,
} from '../features/uploads.js';
import {
  getAgentDiagnostic,
  getSystemPrompt,
  saveSystemPrompt,
  sendAgentChat,
  uploadConversationAttachments,
} from './conversationsApi.js';

const LS = { base: 'ollamaBase', model: 'ollamaModel' };
const DEFAULT_MODEL = 'gpt-oss:20b';
const THINK_START_TAG = '<think>';
const THINK_END_TAG = '</think>';
const CHAT_REASONING_PATHS = [
  'message.thinking',
  'message.reasoning',
  'message.reasoning_content',
  'message.thought',
  'thinking',
  'reasoning',
  'reasoning_content',
  'thought',
];
const CHAT_ANSWER_PATHS = [
  'message.content',
  'response',
];
const GENERATE_REASONING_PATHS = [
  'thinking',
  'reasoning',
  'reasoning_content',
  'message.thinking',
  'message.reasoning',
  'message.reasoning_content',
];
const GENERATE_ANSWER_PATHS = [
  'response',
  'message.content',
];
let isSendInFlight = false;
let systemPrompt = '';
let systemPromptLoadPromise = null;
const getRaw = (k) => { try { return localStorage.getItem(k); } catch (_) { return null; } };
const setLS = (k, v) => { try { localStorage.setItem(k, v); } catch (_) {} };

function readSettingsModel() {
  try {
    const raw = localStorage.getItem('kivrio_settings_v1')
      || localStorage.getItem('kivro_settings_v1')
      || '';
    if (!raw) return '';
    const data = JSON.parse(raw);
    return String(data?.model || '').trim();
  } catch (_) {
    return '';
  }
}

function readSelectorProfile() {
  try {
    const selector = document.querySelector('#agent-profile-select');
    if (!(selector instanceof HTMLSelectElement)) return '';
    return String(selector.value || '').trim();
  } catch (_) {
    return '';
  }
}

function readSelectorAgent() {
  try {
    const selector = document.querySelector('#coding-agent-select');
    if (!(selector instanceof HTMLSelectElement)) return '';
    return String(selector.value || '').trim();
  } catch (_) {
    return '';
  }
}

function readAgentProfile() {
  return readSelectorProfile() || getAgentProfile();
}

export function readCodingAgent() {
  try {
    const currentId = Store.currentId?.();
    const conversation = currentId ? Store.get(currentId) : null;
    const hasMessages = Number(conversation?.messageCount || 0) > 0
      || (Array.isArray(conversation?.messages) && conversation.messages.length > 0);
    if (conversation?.agent && hasMessages) return normalizeCodingAgent(conversation.agent);
  } catch (_) {}
  return normalizeCodingAgent(readSelectorAgent() || getCodingAgent());
}

function readSelectorModel() {
  try {
    const selector = document.querySelector('#model-select');
    if (!(selector instanceof HTMLSelectElement)) return '';
    const selectedValue = String(selector.value || '').trim();
    if (selectedValue) return selectedValue;
    const selectedOption = selector.selectedOptions && selector.selectedOptions[0];
    return String(selectedOption?.value || selectedOption?.textContent || '').trim();
  } catch (_) {
    return '';
  }
}

function readModelLabel() {
  try {
    const label = document.querySelector('#model-label');
    return String(label?.textContent || '').trim();
  } catch (_) {
    return '';
  }
}

function readModelSources() {
  const selectorModel = readSelectorModel();
  const settingsModel = readSettingsModel();
  const legacyModel = (getRaw(LS.model) || '').trim();
  const labelModel = readModelLabel();
  return {
    selectorModel,
    settingsModel,
    legacyModel,
    labelModel,
    agentProfile: readAgentProfile(),
    codingAgent: readCodingAgent(),
    selectedModel: selectorModel || settingsModel || legacyModel || labelModel || DEFAULT_MODEL,
  };
}

export function readBase() {
  const v = (getRaw(LS.base) || '').trim();
  if (!v || !/^(https?:)?\/\//i.test(v)) return 'http://127.0.0.1:11434';
  return v.replace(/\/+$/, '');
}

export async function listModels() {
  const base = readBase();
  const res = await fetch(`${base}/api/tags`, { headers: { Accept: 'application/json' } });
  if (!res.ok) throw new Error('/api/tags ' + res.status);

  const data = await res.json();
  const arr = Array.isArray(data) ? data : (data.models || []);
  return [...new Set(arr.map((m) => m.name || m.model).filter(Boolean))]
    .sort((a, b) => a.localeCompare(b));
}

export function readModel() {
  return readModelSources().selectedModel;
}

export function readSys() {
  return systemPrompt;
}

export async function loadSystemPrompt(force = false) {
  if (!force && systemPromptLoadPromise) return systemPromptLoadPromise;

  systemPromptLoadPromise = (async () => {
    const payload = await getSystemPrompt();
    systemPrompt = String(payload?.prompt || '');
    return systemPrompt;
  })();

  try {
    return await systemPromptLoadPromise;
  } catch (err) {
    systemPromptLoadPromise = null;
    throw err;
  }
}

export async function saveSystemPromptValue(prompt) {
  const payload = await saveSystemPrompt(prompt);
  systemPrompt = String(payload?.prompt || '');
  systemPromptLoadPromise = Promise.resolve(systemPrompt);
  return systemPrompt;
}

function readPathValue(obj, path) {
  return String(path || '')
    .split('.')
    .filter(Boolean)
    .reduce((acc, key) => (acc == null ? undefined : acc[key]), obj);
}

function coerceTextValue(value) {
  if (typeof value === 'string') return value;
  if (Array.isArray(value)) {
    return value.map((item) => coerceTextValue(item)).filter(Boolean).join('');
  }
  if (value && typeof value === 'object') {
    if (typeof value.text === 'string') return value.text;
    if (typeof value.content === 'string') return value.content;
  }
  return '';
}

function pickFirstString(obj, paths) {
  for (const path of (paths || [])) {
    const value = coerceTextValue(readPathValue(obj, path));
    if (value) return value;
  }
  return '';
}

function normalizeStreamChunk(obj, kind) {
  const reasoningChunk = pickFirstString(
    obj,
    kind === 'generate' ? GENERATE_REASONING_PATHS : CHAT_REASONING_PATHS,
  );
  const answerChunk = pickFirstString(
    obj,
    kind === 'generate' ? GENERATE_ANSWER_PATHS : CHAT_ANSWER_PATHS,
  );

  return {
    reasoningChunk,
    answerChunk,
  };
}

function createAssistantStreamState() {
  return {
    answerText: '',
    reasoningText: '',
    reasoningStartedAt: null,
    reasoningEndedAt: null,
    tagMode: 'answer',
    tagBuffer: '',
    nativeReasoningSeen: false,
  };
}

function markReasoningStarted(state) {
  if (state.reasoningStartedAt === null) state.reasoningStartedAt = Date.now();
}

function markReasoningEnded(state) {
  if (state.reasoningStartedAt !== null && state.reasoningEndedAt === null) {
    state.reasoningEndedAt = Date.now();
  }
}

function appendReasoningText(state, text) {
  const value = String(text || '');
  if (!value) return;
  markReasoningStarted(state);
  state.reasoningText += value;
}

function appendAnswerText(state, text) {
  const value = String(text || '');
  if (!value) return;
  if (state.reasoningStartedAt !== null && state.reasoningEndedAt === null) {
    markReasoningEnded(state);
  }
  state.answerText += value;
}

function partialTagSuffixLength(text, tag) {
  const source = String(text || '');
  for (let len = Math.min(source.length, tag.length - 1); len > 0; len -= 1) {
    if (tag.startsWith(source.slice(-len))) return len;
  }
  return 0;
}

function consumeTaggedAnswerChunk(state, chunk) {
  let input = state.tagBuffer + String(chunk || '');
  state.tagBuffer = '';
  let cursor = 0;

  while (cursor < input.length) {
    if (state.tagMode === 'reasoning') {
      const closeIdx = input.indexOf(THINK_END_TAG, cursor);
      if (closeIdx === -1) {
        const partialLength = partialTagSuffixLength(input.slice(cursor), THINK_END_TAG);
        const end = input.length - partialLength;
        appendReasoningText(state, input.slice(cursor, end));
        state.tagBuffer = input.slice(end);
        break;
      }

      appendReasoningText(state, input.slice(cursor, closeIdx));
      cursor = closeIdx + THINK_END_TAG.length;
      state.tagMode = 'answer';
      markReasoningEnded(state);
      continue;
    }

    const openIdx = input.indexOf(THINK_START_TAG, cursor);
    if (openIdx === -1) {
      const partialLength = partialTagSuffixLength(input.slice(cursor), THINK_START_TAG);
      const end = input.length - partialLength;
      appendAnswerText(state, input.slice(cursor, end));
      state.tagBuffer = input.slice(end);
      break;
    }

    appendAnswerText(state, input.slice(cursor, openIdx));
    cursor = openIdx + THINK_START_TAG.length;
    state.tagMode = 'reasoning';
  }
}

function mergeAssistantStreamChunk(state, chunk) {
  if (!chunk) return;

  if (chunk.reasoningChunk) {
    state.nativeReasoningSeen = true;
    appendReasoningText(state, chunk.reasoningChunk);
  }

  if (!chunk.answerChunk) return;
  if (state.nativeReasoningSeen) {
    appendAnswerText(state, chunk.answerChunk);
    return;
  }
  consumeTaggedAnswerChunk(state, chunk.answerChunk);
}

function buildAssistantPayload(state, { live = false } = {}) {
  const reasoningText = String(state?.reasoningText || '');
  const answerText = String(state?.answerText || '');
  let durationMs = null;
  if (state?.reasoningStartedAt !== null) {
    const endedAt = state.reasoningEndedAt ?? (live ? Date.now() : null);
    if (endedAt !== null) {
      durationMs = Math.max(1, endedAt - state.reasoningStartedAt);
    }
  }
  return {
    answerText,
    reasoningText,
    reasoningDurationMs: durationMs,
  };
}

function finalizeAssistantStreamState(state) {
  if (state.tagBuffer) {
    if (state.tagMode === 'reasoning') {
      appendReasoningText(state, state.tagBuffer);
    } else {
      appendAnswerText(state, state.tagBuffer);
    }
    state.tagBuffer = '';
  }
  if (state.reasoningStartedAt !== null && state.reasoningEndedAt === null) {
    markReasoningEnded(state);
  }
  return buildAssistantPayload(state);
}

export async function ping(base) {
  const res = await fetch(base + '/api/tags', { method: 'GET' });
  if (!res.ok) throw new Error('HTTP ' + res.status);
  return res.json();
}

function readHistory(convId) {
  const conversation = Store.get(convId);
  return Array.isArray(conversation?.messages) ? conversation.messages : [];
}

function toChatHistory(arr) {
  return (arr || [])
    .map((m) => {
      const role = (m.role || m.r || '').toLowerCase();
      const content = (m.content ?? m.text ?? '').toString();
      if (role === 'user' || role === 'assistant') return { role, content };
      return null;
    })
    .filter(Boolean);
}

function buildEffectiveSystemPrompt(sys, userText, convId, extraGuidance = '') {
  const base = String(sys || '').trim();
  const addition = String(extraGuidance || '').trim();
  if (!base && !addition) return '';
  return [base, addition].filter(Boolean).join('\n\n');
}

function buildChatMessages({ sys, convId, userText, maxPast = 16, images = [], extraSystemGuidance = '' }) {
  const out = [];
  const history = toChatHistory(readHistory(convId));
  const effectiveSys = buildEffectiveSystemPrompt(sys, userText, convId, extraSystemGuidance);

  let hist = history.slice();
  if (hist.length) {
    const last = hist[hist.length - 1];
    if (last.role === 'user' && last.content === userText) {
      hist = hist.slice(0, -1);
    }
  }

  const trimmed = hist.slice(-maxPast);

  if (effectiveSys) out.push({ role: 'system', content: effectiveSys });
  for (const message of trimmed) out.push({ role: message.role, content: message.content });

  const current = { role: 'user', content: userText };
  if (images.length) current.images = images;
  out.push(current);
  return out;
}

function buildGeneratePrompt({ sys, convId, userText, maxPast = 16, extraSystemGuidance = '' }) {
  const history = toChatHistory(readHistory(convId)).slice(-maxPast);
  const parts = [];
  const effectiveSys = buildEffectiveSystemPrompt(sys, userText, convId, extraSystemGuidance);
  if (effectiveSys) parts.push(`System:\n${effectiveSys}`);
  for (const message of history) {
    parts.push((message.role === 'user' ? 'User' : 'Assistant') + ':\n' + message.content);
  }
  parts.push('User:\n' + userText);
  parts.push('Assistant:');
  return parts.join('\n\n');
}

function clipAgentContextText(value, maxLength = 2500) {
  const text = String(value || '').trim();
  if (text.length <= maxLength) return text;
  return text.slice(0, maxLength).trimEnd() + '\n[...contexte tronque par Kivrio Agent UI...]';
}

function buildCodexAgentPrompt({ convId, userText, maxPast = 8 }) {
  let history = toChatHistory(readHistory(convId));
  if (history.length) {
    const last = history[history.length - 1];
    if (last.role === 'user' && last.content === userText) {
      history = history.slice(0, -1);
    }
  }

  const trimmed = history.slice(-maxPast);
  if (!trimmed.length) return userText;

  const parts = [
    'Contexte recent de la conversation Kivrio Agent UI:',
    'Utilise ce contexte pour comprendre les confirmations courtes comme "accord" ou "je confirme".',
    'Si le contexte ne permet pas d identifier clairement le fichier ou le dossier cible, demande une clarification.',
  ];

  for (const message of trimmed) {
    const label = message.role === 'user' ? 'Utilisateur' : 'Assistant';
    parts.push(`${label}:\n${clipAgentContextText(message.content)}`);
  }

  parts.push(`Message utilisateur actuel:\n${clipAgentContextText(userText, 6000)}`);
  return parts.join('\n\n');
}

export async function* streamChat({ base, model, sys, prompt, convId, maxPast = 16, images = [], extraSystemGuidance = '' }) {
  const body = {
    model,
    messages: buildChatMessages({ sys, convId, userText: prompt, maxPast, images, extraSystemGuidance }),
    stream: true,
  };
  const res = await fetch(base + '/api/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });

  if ((res.status === 404 || res.status === 400) && !images.length) {
    return yield* streamGenerate({ base, model, sys, prompt, convId, maxPast, extraSystemGuidance });
  }
  if (!res.ok) throw new Error('HTTP ' + res.status);

  const reader = res.body.getReader();
  const dec = new TextDecoder();
  let buf = '';

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    let idx;
    while ((idx = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, idx).trim();
      buf = buf.slice(idx + 1);
      if (!line) continue;
      try {
        const obj = JSON.parse(line);
        yield normalizeStreamChunk(obj, 'chat');
        if (obj.done) return;
      } catch (_) {}
    }
  }

  const tail = buf.trim();
  if (!tail) return;
  try {
    yield normalizeStreamChunk(JSON.parse(tail), 'chat');
  } catch (_) {}
}

export async function* streamGenerate({ base, model, sys, prompt, convId, maxPast = 16, extraSystemGuidance = '' }) {
  const effectiveSys = buildEffectiveSystemPrompt(sys, prompt, convId, extraSystemGuidance);
  const res = await fetch(base + '/api/generate', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      model,
      system: effectiveSys || undefined,
      prompt: buildGeneratePrompt({ sys, convId, userText: prompt, maxPast, extraSystemGuidance }),
      stream: true,
    }),
  });
  if (!res.ok) throw new Error('HTTP ' + res.status + ' (/api/generate)');

  const reader = res.body.getReader();
  const dec = new TextDecoder();
  let buf = '';

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    let idx;
    while ((idx = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, idx).trim();
      buf = buf.slice(idx + 1);
      if (!line) continue;
      try {
        const obj = JSON.parse(line);
        yield normalizeStreamChunk(obj, 'generate');
        if (obj.done) return;
      } catch (_) {}
    }
  }

  const tail = buf.trim();
  if (!tail) return;
  try {
    yield normalizeStreamChunk(JSON.parse(tail), 'generate');
  } catch (_) {}
}

function renderAssistantChunk(target, payload, options = {}) {
  const answerText = payload?.answerText ?? '';
  updateBubbleContent(target, 'assistant', answerText, {
    ...options,
    answerText,
    reasoningText: payload?.reasoningText ?? '',
    reasoningDurationMs: payload?.reasoningDurationMs ?? null,
  });
}

function renderConversationSnapshot(conversation) {
  const log = qs('#chat-log');
  if (log) log.innerHTML = '';

  for (const message of (conversation?.messages || [])) {
    renderMsg(message.role, message.content, {
      messageId: message.id,
      conversationId: message.conversationId,
      attachments: message.attachments || [],
      reasoningText: message.reasoningText,
      model: message.model,
      reasoningDurationMs: message.reasoningDurationMs,
    });
  }
}

function setSendButtonBusy(isBusy) {
  const btn = qs('#send-btn');
  if (!(btn instanceof HTMLButtonElement)) return;
  btn.disabled = isBusy;
  btn.classList.toggle('is-busy', isBusy);
  btn.setAttribute('aria-busy', isBusy ? 'true' : 'false');
  btn.title = isBusy ? 'Traitement en cours...' : '';
}

function normalizeIntentText(value) {
  return String(value || '')
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .trim();
}

function isAgentDiagnosticPrompt(text) {
  const value = normalizeIntentText(text);
  if (!value) return false;
  if (value.startsWith('diagnostic kivrio agent ui')) return true;
  if (value.startsWith('diagnostic codex')) return true;
  if (wantsFourLineDiagnostic(value)
    && (value.includes('codex cli') || value.includes('app-server') || value.includes('app server') || value.includes('ollama'))) {
    return true;
  }

  const shortQuestion = value.length <= 120 && value.split(/\s+/).filter(Boolean).length <= 16;
  const mentionsRuntime = value.includes('codex cli')
    || value.includes('opencode')
    || value.includes('agent de codage')
    || value.includes('kivrio agent ui')
    || value.includes('app-server')
    || value.includes('app server')
    || value.includes('provider local')
    || value.includes('ollama')
    || value.includes('open source local')
    || value.includes('gpt-oss');
  const asksIdentity = value.includes('etes-vous')
    || value.includes('etes vous')
    || value.includes('qui etes-vous')
    || value.includes('qui etes vous')
    || value.includes('quel modele utilisez-vous')
    || value.includes('quel modele utilisez vous')
    || value.includes('modele utilisez-vous')
    || value.includes('modele utilisez vous')
    || value.includes('provider utilisez-vous')
    || value.includes('provider utilisez vous')
    || value.includes('utilisez-vous ollama')
    || value.includes('utilisez vous ollama')
    || value.includes('quel est votre modele')
    || value.includes('appele via codex')
    || value.includes('appelee via codex')
    || value.includes('canal :')
    || value.includes('regle agents');

  const asksModelIdentity = value.includes('quel modele')
    || value.includes('modele utilisez-vous')
    || value.includes('modele utilisez vous')
    || value.includes('votre modele')
    || value.includes('quel est votre modele')
    || value.includes('model utilisez-vous')
    || value.includes('model utilisez vous')
    || value.includes('your model');

  return shortQuestion && (mentionsRuntime || asksModelIdentity) && asksIdentity;
}

function wantsFourLineDiagnostic(text) {
  const value = normalizeIntentText(text);
  return (value.includes('4 lignes') || value.includes('quatre lignes'))
    && (value.includes('canal') || value.includes('provider') || value.includes('regle agents'));
}

function formatAgentDiagnostic(payload, prompt = '', modelSources = {}) {
  const channel = String(payload?.channel || 'Ollama launch Codex CLI/app-server');
  const agent = getCodingAgentDefinition(modelSources?.codingAgent || payload?.agent || '');
  const selected = String(modelSources?.selectedModel || payload?.selectedModel || '').trim();
  const selectorModel = String(modelSources?.selectorModel || '').trim();
  const settingsModel = String(modelSources?.settingsModel || '').trim();
  const legacyModel = String(modelSources?.legacyModel || '').trim();
  const labelModel = String(modelSources?.labelModel || '').trim();
  const processModel = String(payload?.processModel || '').trim();
  const profile = getAgentProfileDefinition(modelSources?.agentProfile || payload?.profile || '');
  const fallbackModel = String(payload?.effectiveModel || payload?.defaultModel || 'inconnu').trim();
  const model = selected || processModel || fallbackModel;
  const provider = String(payload?.effectiveProvider || 'inconnu');
  const ready = Boolean(payload?.codexReady || payload?.ready || payload?.codexHealthy || payload?.healthy);
  const agentMode = String(payload?.agentMode || 'ollama-launch');
  const source = String(payload?.source || 'Kivrio Agent UI server');
  if (wantsFourLineDiagnostic(prompt)) {
    return [
      `1. Canal : ${channel}`,
      `2. Modele selectionne : ${model}`,
      `3. Provider local : ${provider}`,
      `4. Mode agent : ${agentMode}`,
    ].join('\n');
  }
  const lines = [
    `Canal : ${channel}`,
    `Agent selectionne : ${agent.label}`,
    `Modele selectionne : ${model}`,
    `Modele selecteur UI : ${selectorModel || 'non lu'}`,
    `Modele localStorage : ${settingsModel || legacyModel || 'vide'}`,
    `Modele libelle UI : ${labelModel || 'non lu'}`,
    `Profil agent : ${profile.label}`,
    `Modele app-server : ${processModel || 'non demarre'}`,
    `Provider effectif : ${provider}`,
    `Mode agent : ${agentMode}`,
    `Etat ${payload?.providerLabel || 'Ollama'} ${agent.label} : ${ready ? 'pret' : 'non pret'}`,
    `Source : ${source}`,
  ];
  if (payload?.wslFound !== undefined) {
    lines.push(`WSL : ${payload.wslFound ? (payload.wslDistribution || 'detecte') : 'indisponible'}`);
  }
  if (payload?.wslCommandPath) {
    lines.push(`Chemin agent WSL : ${payload.wslCommandPath}`);
  }
  if (payload?.dialogueConnected === false) {
    lines.push('Dialogue agent : non connecte');
  }
  if (payload?.lastError) {
    lines.push(`Etat detaille : ${payload.lastError}`);
  }
  if (selected && processModel && selected.toLowerCase() !== processModel.toLowerCase()) {
    lines.push("Note : le prochain message relancera l'app-server avec le modele selectionne.");
  }
  return lines.join('\n');
}

async function completeWithLocalDiagnostic({ convId, aiB, prompt = '' }) {
  const modelSources = readModelSources();
  const payload = await getAgentDiagnostic(modelSources.codingAgent);
  const answerText = formatAgentDiagnostic(payload, prompt, modelSources);
  const displayModel = 'Diagnostic local';

  renderAssistantChunk(aiB, {
    answerText,
    reasoningText: '',
    reasoningDurationMs: null,
  }, { model: displayModel });

  if (convId) {
    const savedAssistantMessage = await Store.addMsg(convId, 'assistant', answerText, {
      reasoningText: '',
      model: displayModel,
      reasoningDurationMs: null,
    });
    bindMessageRecord(aiB, savedAssistantMessage);
  }

  return payload;
}

async function completeWithCodexAgent({ prompt, sys, model, convId, aiB }) {
  const startedAt = Date.now();
  const agent = readCodingAgent();
  const profile = readAgentProfile();
  const agentPrompt = buildCodexAgentPrompt({ convId, userText: prompt });
  const payload = await sendAgentChat({
    agent,
    prompt: agentPrompt,
    systemPrompt: sys || '',
    model,
    profile,
    conversationId: convId || null,
  });
  const agentLabel = String(payload?.agentLabel || getCodingAgentDefinition(agent).label || 'Agent').trim();
  const answerText = String(payload?.answer || '').trim() || `${agentLabel} n'a pas retourne de texte.`;
  const reasoningText = String(payload?.reasoning || '').trim();
  const reasoningDurationMs = reasoningText ? Date.now() - startedAt : null;
  const effectiveModel = String(payload?.effectiveModel || payload?.model || model || agentLabel).trim();
  const effectiveProvider = String(payload?.effectiveProvider || payload?.provider || '').trim();
  const displayModel = effectiveProvider ? `${effectiveModel} (${effectiveProvider})` : effectiveModel;

  renderAssistantChunk(aiB, {
    answerText,
    reasoningText,
    reasoningDurationMs,
  }, { model: displayModel });

  if (convId) {
    const savedAssistantMessage = await Store.addMsg(convId, 'assistant', answerText, {
      reasoningText,
      model: displayModel,
      reasoningDurationMs,
    });
    bindMessageRecord(aiB, savedAssistantMessage);
  }

  return payload;
}

export async function regenerateFromEditedMessage({ conversationId, messageId, content }) {
  if (!conversationId || messageId == null) {
    throw new Error('Message introuvable.');
  }
  if (isSendInFlight) {
    throw new Error('Un traitement est deja en cours.');
  }

  isSendInFlight = true;
  setSendButtonBusy(true);

  let aiB = null;
  try {
    const rewrittenConversation = await Store.rewriteFromMessage(conversationId, messageId, {
      content,
      truncate_following: true,
    });
    const conversation = await Store.fetch(conversationId).catch(() => rewrittenConversation);
    renderConversationSnapshot(conversation);
    try { await mountHistory(); } catch (_) {}

    const messages = Array.isArray(conversation?.messages) ? conversation.messages : [];
    const lastMessage = messages[messages.length - 1] || null;
    if (!lastMessage || lastMessage.role !== 'user' || !String(lastMessage.content || '').trim()) {
      return conversation;
    }

    if (isAgentDiagnosticPrompt(lastMessage.content)) {
      const diagnosticModel = 'Diagnostic local';
      aiB = renderMsg('assistant', '', { model: diagnosticModel });
      try {
        await completeWithLocalDiagnostic({
          convId: conversationId,
          aiB,
          prompt: lastMessage.content,
        });
        try { await mountHistory(); } catch (_) {}
        return Store.get(conversationId) || conversation;
      } catch (err) {
        const msg = 'Erreur: ' + (err && err.message ? err.message : String(err));
        renderAssistantChunk(aiB, { answerText: msg, reasoningText: '', reasoningDurationMs: null }, { model: diagnosticModel });
        const savedError = await Store.addMsg(conversationId, 'assistant', msg, { model: diagnosticModel });
        bindMessageRecord(aiB, savedError);
        try { await mountHistory(); } catch (_) {}
        return Store.get(conversationId) || conversation;
      }
    }

    const model = readModel();
    let sys = '';
    try {
      await loadSystemPrompt();
      sys = readSys();
    } catch (err) {
      aiB = renderMsg('assistant', `Erreur: ${err?.message || err}`, { model });
      const savedError = await Store.addMsg(conversationId, 'assistant', `Erreur: ${err?.message || err}`, { model });
      bindMessageRecord(aiB, savedError);
      try { await mountHistory(); } catch (_) {}
      return Store.get(conversationId) || conversation;
    }

    aiB = renderMsg('assistant', '', { model });
    try {
      await completeWithCodexAgent({
        prompt: lastMessage.content,
        sys,
        model,
        convId: conversationId,
        aiB,
      });
      try { await mountHistory(); } catch (_) {}
      return Store.get(conversationId) || conversation;
    } catch (err) {
      const msg = 'Erreur: ' + (err && err.message ? err.message : String(err));
      renderAssistantChunk(aiB, { answerText: msg, reasoningText: '', reasoningDurationMs: null }, { model });
      const savedError = await Store.addMsg(conversationId, 'assistant', msg, { model });
      bindMessageRecord(aiB, savedError);
      try { await mountHistory(); } catch (_) {}
      return Store.get(conversationId) || conversation;
    }
  } finally {
    isSendInFlight = false;
    setSendButtonBusy(false);
  }
}

export async function sendCurrent() {
  const ta = qs('#composer-input');
  if (!ta) return alert('Zone de saisie introuvable.');
  if (isSendInFlight) return;

  const text = (ta.value || '').trim();
  const pendingUploads = getPendingUploads();
  if (!text && !pendingUploads.length) return;
  const isDiagnosticPrompt = isAgentDiagnosticPrompt(text);
  const model = readModel();
  let sys = '';
  if (!isDiagnosticPrompt) {
    try {
      await loadSystemPrompt();
      sys = readSys();
    } catch (err) {
      alert('Impossible de charger le prompt systeme: ' + (err?.message || err));
      return;
    }
  }
  const detachedUploads = pendingUploads.length ? detachPendingUploads() : [];
  const localAttachments = detachedUploads.map((item) => ({
    filename: item?.file?.name || 'Piece jointe',
    mimeType: item?.file?.type || '',
    sizeBytes: Number(item?.file?.size || 0),
    previewUrl: item?.objectUrl || null,
    url: item?.objectUrl || null,
    isImage: item?.kind === 'image',
  }));
  let shouldReleaseDetachedUploads = true;

  isSendInFlight = true;
  setSendButtonBusy(true);

  try {
    const userBubble = renderMsg('user', text, { attachments: localAttachments });
    ta.value = '';

    if (window.kivrioEnsureConversationPromise) {
      try { await window.kivrioEnsureConversationPromise; } catch (_) {}
    }

    let aiB = null;

    let convId = Store.currentId?.() || null;
    if (convId) {
      try {
        const existingConversation = await Store.ensureLoaded(convId);
        if (!existingConversation?.id) throw new Error('Conversation not found');
      } catch (_) {
        try { Store.clearCurrent?.(); } catch (_) {}
        convId = null;
      }
    }
    if (!convId && Store.create) {
      const conversation = await Store.create({ title: 'Nouvelle conversation', agent: readCodingAgent() });
      convId = conversation.id;
    }
    if (!convId) {
      const message = 'Impossible de creer la conversation.';
      if (detachedUploads.length) {
        restorePendingUploads(detachedUploads, message);
        shouldReleaseDetachedUploads = false;
      }
      alert(message);
      return;
    }

    try {
      const activeConversation = Store.get(convId);
      const hasMessages = Number(activeConversation?.messageCount || 0) > 0
        || (Array.isArray(activeConversation?.messages) && activeConversation.messages.length > 0);
      if (activeConversation && (!activeConversation.agent || !hasMessages)) {
        await Store.update(convId, { agent: readCodingAgent() });
      }
    } catch (_) {}

    let uploadedAttachments = [];
    if (detachedUploads.length) {
      try {
        uploadedAttachments = await uploadConversationAttachments(convId, detachedUploads.map((item) => item.file));
      } catch (err) {
        const message = err?.message || 'Televersement impossible.';
        restorePendingUploads(detachedUploads, message);
        shouldReleaseDetachedUploads = false;
        if (aiB) {
          renderAssistantChunk(aiB, { answerText: message }, { model });
        } else {
          alert(message);
        }
        return;
      }
    }

    try {
      const savedUserMessage = await Store.addMsg(convId, 'user', text, {
        attachmentIds: uploadedAttachments.map((item) => item.id),
      });
      bindMessageRecord(userBubble, savedUserMessage);
    } catch (_) {
    }

    if (isDiagnosticPrompt) {
      const diagnosticModel = 'Diagnostic local';
      try {
        await Store.renameIfDefault(convId, fmtTitle(text));
      } catch (_) {}
      try {
        await mountHistory();
      } catch (_) {}
      aiB = renderMsg('assistant', '', { model: diagnosticModel });
      try {
        await completeWithLocalDiagnostic({
          convId,
          aiB,
          prompt: text,
        });
        try { await mountHistory(); } catch (_) {}
      } catch (err) {
        const msg = 'Erreur: ' + (err && err.message ? err.message : String(err));
        renderAssistantChunk(aiB, { answerText: msg, reasoningText: '', reasoningDurationMs: null }, { model: diagnosticModel });
        if (convId) {
          const savedError = await Store.addMsg(convId, 'assistant', msg, { model: diagnosticModel });
          bindMessageRecord(aiB, savedError);
        }
        try { await mountHistory(); } catch (_) {}
      }
      return;
    }

    const prepared = await preparePendingUploadsForSend({
      model,
      userText: text,
      items: detachedUploads,
    });
    if (!prepared.ok) {
      const message = prepared.message || 'Les fichiers joints ne peuvent pas etre envoyes.';
      if (aiB) {
        renderAssistantChunk(aiB, { answerText: message }, { model });
      } else {
        alert(message);
      }
      return;
    }

    try {
      await Store.renameIfDefault(convId, fmtTitle(prepared.suggestedTitle || text || 'Piece jointe'));
    } catch (_) {}
    try {
      await mountHistory();
    } catch (_) {}

    if (!aiB) aiB = renderMsg('assistant', '', { model });
    try {
      await completeWithCodexAgent({
        prompt: prepared.promptText || text,
        sys,
        model,
        convId,
        aiB,
      });
      try { await mountHistory(); } catch (_) {}
    } catch (err) {
      const msg = 'Erreur: ' + (err && err.message ? err.message : String(err));
      renderAssistantChunk(aiB, { answerText: msg, reasoningText: '', reasoningDurationMs: null }, { model });
      if (convId) await Store.addMsg(convId, 'assistant', msg, { model });
      try { await mountHistory(); } catch (_) {}
      console.warn('Fetch error', err);
    }
  } finally {
    if (shouldReleaseDetachedUploads) releaseUploadItems(detachedUploads);
    isSendInFlight = false;
    setSendButtonBusy(false);
  }
}

document.addEventListener('settings:model-changed', (e) => {
  const model = (e.detail || '').trim();
  if (model) setLS(LS.model, model);
});
