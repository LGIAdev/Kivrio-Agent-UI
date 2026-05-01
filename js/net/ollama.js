// js/net/ollama.js
// Flux reseau Ollama + rendu des messages (compatible KaTeX)

import { bindMessageRecord, renderMsg, updateBubbleContent } from '../chat/render.js';
import { Store, fmtTitle, mountHistory } from '../store/conversations.js';
import { qs } from '../core/dom.js';
import {
  detachPendingUploads,
  getPendingUploads,
  preparePendingUploadsForSend,
  releaseUploadItems,
  restorePendingUploads,
} from '../features/uploads.js';
import {
  getSystemPrompt,
  saveSystemPrompt,
  uploadConversationAttachments,
} from './conversationsApi.js';

const LS = { base: 'ollamaBase', model: 'ollamaModel' };
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
  const v = (getRaw(LS.model) || '').trim();
  return v || 'gpt-oss:20b';
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

    const model = readModel();
    const base = readBase();
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
    const assistantState = createAssistantStreamState();

    try {
      for await (const chunk of streamChat({
        base,
        model,
        sys,
        prompt: lastMessage.content,
        convId: conversationId,
        images: [],
      })) {
        mergeAssistantStreamChunk(assistantState, chunk);
        const livePayload = buildAssistantPayload(assistantState, { live: true });
        if (!livePayload.answerText.trim() && !livePayload.reasoningText.trim()) continue;
        renderAssistantChunk(aiB, livePayload, { model });
      }

      const finalPayload = finalizeAssistantStreamState(assistantState);
      if (finalPayload.answerText.trim() || finalPayload.reasoningText.trim()) {
        renderAssistantChunk(aiB, finalPayload, { model });
      }
      if (finalPayload.answerText.trim() || finalPayload.reasoningText.trim()) {
        const savedAssistantMessage = await Store.addMsg(conversationId, 'assistant', finalPayload.answerText, {
          reasoningText: finalPayload.reasoningText,
          model,
          reasoningDurationMs: finalPayload.reasoningDurationMs,
        });
        bindMessageRecord(aiB, savedAssistantMessage);
      }
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
  const model = readModel();
  let sys = '';
  try {
    await loadSystemPrompt();
    sys = readSys();
  } catch (err) {
    alert('Impossible de charger le prompt systeme: ' + (err?.message || err));
    return;
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

    const base = readBase();
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
      const conversation = await Store.create('Nouvelle conversation');
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
    const assistantState = createAssistantStreamState();
    try {
      for await (const chunk of streamChat({
        base,
        model,
        sys,
        prompt: prepared.promptText || text,
        convId,
        images: prepared.imagePayloads || [],
      })) {
        mergeAssistantStreamChunk(assistantState, chunk);
        const livePayload = buildAssistantPayload(assistantState, { live: true });
        if (!livePayload.answerText.trim() && !livePayload.reasoningText.trim()) continue;
        renderAssistantChunk(aiB, livePayload, { model });
      }
      const finalPayload = finalizeAssistantStreamState(assistantState);
      if (finalPayload.answerText.trim() || finalPayload.reasoningText.trim()) {
        renderAssistantChunk(aiB, finalPayload, { model });
      }
      if (convId && (finalPayload.answerText.trim() || finalPayload.reasoningText.trim())) {
        const savedAssistantMessage = await Store.addMsg(convId, 'assistant', finalPayload.answerText, {
          reasoningText: finalPayload.reasoningText,
          model,
          reasoningDurationMs: finalPayload.reasoningDurationMs,
        });
        bindMessageRecord(aiB, savedAssistantMessage);
      }
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
