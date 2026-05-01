import { qs } from '../core/dom.js';
import { renderMsg, clearChat } from '../chat/render.js';
import {
  addConversationMessage,
  createConversation,
  createFolder as createFolderRequest,
  deleteConversation,
  deleteFolder as deleteFolderRequest,
  getConversation,
  listConversations,
  listFolders,
  updateConversation,
  updateConversationMessage,
  updateFolder as updateFolderRequest,
} from '../net/conversationsApi.js';

const K_CUR = 'mpai.current.v1';
let cache = [];
let foldersCache = [];
let openFolders = new Set();
let activeMenu = null;
let menuEventsBound = false;

function sortCache() {
  cache.sort((a, b) => (b.updatedAt - a.updatedAt) || (b.createdAt - a.createdAt));
}

function sortFolders() {
  foldersCache.sort((a, b) => {
    const byName = a.name.localeCompare(b.name, 'fr', { sensitivity: 'base' });
    if (byName !== 0) return byName;
    return (b.updatedAt - a.updatedAt) || (b.createdAt - a.createdAt);
  });
}

function normalizeAttachment(raw) {
  if (!raw) return null;
  return {
    id: raw.id ?? null,
    conversationId: raw.conversationId ?? raw.conversation_id ?? null,
    messageId: raw.messageId ?? raw.message_id ?? null,
    filename: String(raw.filename ?? 'fichier'),
    mimeType: String(raw.mimeType ?? raw.mime_type ?? 'application/octet-stream'),
    sizeBytes: Number(raw.sizeBytes ?? raw.size_bytes ?? 0),
    url: raw.url ?? null,
    previewUrl: raw.previewUrl ?? raw.preview_url ?? raw.url ?? null,
    isImage: Boolean(raw.isImage ?? raw.is_image ?? String(raw.mimeType ?? raw.mime_type ?? '').startsWith('image/')),
    status: String(raw.status ?? 'stored'),
  };
}

function normalizeMessage(raw) {
  if (!raw) return null;
  return {
    id: raw.id ?? null,
    conversationId: raw.conversationId ?? raw.conversation_id ?? null,
    role: (raw.role || '').toLowerCase(),
    content: String(raw.content ?? raw.text ?? ''),
    reasoningText: raw.reasoningText == null && raw.reasoning_text == null
      ? null
      : String(raw.reasoningText ?? raw.reasoning_text ?? ''),
    model: raw.model == null ? null : String(raw.model),
    reasoningDurationMs: Number(raw.reasoningDurationMs ?? raw.reasoning_duration_ms ?? 0) || null,
    createdAt: Number(raw.createdAt ?? raw.created_at ?? Date.now()),
    position: Number(raw.position ?? 0),
    attachments: Array.isArray(raw.attachments) ? raw.attachments.map(normalizeAttachment).filter(Boolean) : [],
  };
}

function normalizeFolder(raw) {
  return {
    id: raw?.id ?? null,
    name: String(raw?.name ?? 'Nouveau dossier'),
    createdAt: Number(raw?.createdAt ?? raw?.created_at ?? Date.now()),
    updatedAt: Number(raw?.updatedAt ?? raw?.updated_at ?? Date.now()),
    conversationCount: Number(raw?.conversationCount ?? raw?.conversation_count ?? 0),
  };
}

function normalizeConversation(raw) {
  const messages = Array.isArray(raw?.messages)
    ? raw.messages.map(normalizeMessage).filter(Boolean)
    : [];

  return {
    id: raw?.id ?? null,
    title: String(raw?.title ?? 'Nouvelle conversation'),
    folderId: raw?.folderId ?? raw?.folder_id ?? null,
    createdAt: Number(raw?.createdAt ?? raw?.created_at ?? Date.now()),
    updatedAt: Number(raw?.updatedAt ?? raw?.updated_at ?? Date.now()),
    archived: Number(raw?.archived ?? 0),
    messageCount: Number(raw?.messageCount ?? raw?.message_count ?? messages.length ?? 0),
    messages,
    messagesLoaded: Array.isArray(raw?.messages) || Boolean(raw?.messagesLoaded),
  };
}

function upsertConversation(raw) {
  const normalized = normalizeConversation(raw);
  if (!normalized.id) return null;

  const idx = cache.findIndex((item) => item.id === normalized.id);
  if (idx >= 0) {
    const existing = cache[idx];
    const merged = {
      ...existing,
      ...normalized,
      messages: normalized.messagesLoaded ? normalized.messages : (existing.messages || []),
      messagesLoaded: normalized.messagesLoaded || existing.messagesLoaded,
    };
    merged.messageCount = normalized.messagesLoaded
      ? (Array.isArray(merged.messages) ? merged.messages.length : 0)
      : Math.max(
        Number(merged.messageCount || 0),
        Array.isArray(merged.messages) ? merged.messages.length : 0,
      );
    cache[idx] = merged;
    sortCache();
    return merged;
  }

  normalized.messageCount = normalized.messagesLoaded
    ? normalized.messages.length
    : Math.max(
      Number(normalized.messageCount || 0),
      Array.isArray(normalized.messages) ? normalized.messages.length : 0,
    );
  cache.push(normalized);
  sortCache();
  return normalized;
}

function replaceCacheFromList(list) {
  const byId = new Map(cache.map((item) => [item.id, item]));
  cache = (list || []).map((item) => {
    const normalized = normalizeConversation(item);
    const existing = byId.get(normalized.id);
    if (existing?.messagesLoaded) {
      normalized.messages = existing.messages;
      normalized.messagesLoaded = true;
      normalized.messageCount = existing.messages.length;
    }
    return normalized;
  });
  sortCache();
  return cache;
}

function replaceFoldersFromList(list) {
  foldersCache = (list || []).map((item) => normalizeFolder(item));
  sortFolders();
  return foldersCache;
}

function upsertConversationPayload(payload) {
  return upsertConversation({
    ...(payload?.conversation || {}),
    messages: payload?.messages || [],
    message_count: Array.isArray(payload?.messages) ? payload.messages.length : 0,
  });
}

function upsertMessageInConversation(conversation, message) {
  if (!conversation || !message) return null;
  if (!Array.isArray(conversation.messages)) conversation.messages = [];
  const index = conversation.messages.findIndex((item) => item.id === message.id);
  if (index >= 0) {
    conversation.messages[index] = {
      ...conversation.messages[index],
      ...message,
      attachments: Array.isArray(message.attachments) ? message.attachments : (conversation.messages[index].attachments || []),
    };
  } else {
    conversation.messages.push(message);
  }
  conversation.messagesLoaded = true;
  conversation.messageCount = conversation.messages.length;
  return conversation;
}

function preserveConversationShape(updated, existing) {
  if (!updated) return updated;
  updated.messages = existing?.messages || [];
  updated.messagesLoaded = existing?.messagesLoaded || false;
  updated.messageCount = Math.max(
    Number(updated.messageCount || 0),
    Number(existing?.messageCount || 0),
    Array.isArray(updated.messages) ? updated.messages.length : 0,
  );
  return updated;
}

function closeActiveMenu() {
  if (!activeMenu) return;
  activeMenu.row.classList.remove('menu-open');
  activeMenu.button.setAttribute('aria-expanded', 'false');
  activeMenu.menu.classList.remove('open');
  activeMenu = null;
}

function openMenu(row, button, menu) {
  if (
    activeMenu
    && activeMenu.row === row
    && activeMenu.button === button
    && activeMenu.menu === menu
  ) {
    closeActiveMenu();
    return;
  }

  closeActiveMenu();
  row.classList.add('menu-open');
  button.setAttribute('aria-expanded', 'true');
  menu.classList.add('open');
  activeMenu = { row, button, menu };
}

function ensureMenuEvents() {
  if (menuEventsBound) return;
  menuEventsBound = true;

  document.addEventListener('click', (event) => {
    if (!activeMenu) return;
    const target = event.target;
    if (!target) return;
    if (activeMenu.row.contains(target)) return;
    closeActiveMenu();
  });

  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') closeActiveMenu();
  });
}

async function openConversation(id) {
  if (!id) return;
  Store.setCurrent(id);
  const fullConversation = await Store.fetch(id);
  clearChat();
  for (const message of (fullConversation?.messages || [])) {
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

export const Store = {
  load() {
    return cache.slice();
  },

  folders() {
    return foldersCache.slice();
  },

  currentId() {
    try {
      return localStorage.getItem(K_CUR);
    } catch (_) {
      return null;
    }
  },

  setCurrent(id) {
    try {
      localStorage.setItem(K_CUR, id);
    } catch (_) {}
  },

  clearCurrent() {
    try {
      localStorage.removeItem(K_CUR);
    } catch (_) {}
  },

  async refresh() {
    const [conversations, folders] = await Promise.all([
      listConversations(),
      listFolders(),
    ]);
    replaceCacheFromList(conversations);
    replaceFoldersFromList(folders);
    return this.load();
  },

  get(id) {
    return cache.find((item) => item.id === id) || null;
  },

  async fetch(id) {
    if (!id) return null;
    const payload = await getConversation(id);
    return upsertConversation({
      ...payload.conversation,
      messages: payload.messages,
      message_count: Array.isArray(payload.messages) ? payload.messages.length : 0,
    });
  },

  async ensureLoaded(id) {
    if (!id) return null;
    const existing = this.get(id);
    if (existing?.messagesLoaded) return existing;
    return this.fetch(id);
  },

  async create(input) {
    const payload = typeof input === 'string' ? { title: input } : (input || {});
    const created = normalizeConversation(await createConversation(payload));
    created.messages = [];
    created.messagesLoaded = true;
    upsertConversation(created);
    this.setCurrent(created.id);
    return created;
  },

  async createFolder(input) {
    const payload = typeof input === 'string' ? { name: input } : (input || {});
    const created = normalizeFolder(await createFolderRequest(payload));
    foldersCache.push(created);
    sortFolders();
    return created;
  },

  async addMsg(id, role, content, options = {}) {
    const message = normalizeMessage(await addConversationMessage(id, {
      role,
      content,
      attachment_ids: options.attachmentIds || [],
      reasoning_text: options.reasoningText || null,
      model: options.model || null,
      reasoning_duration_ms: options.reasoningDurationMs || null,
    }));
    let conversation = this.get(id);
    if (!conversation) {
      conversation = await this.fetch(id);
    }
    if (!conversation) return null;

    upsertMessageInConversation(conversation, message);
    conversation.updatedAt = message.createdAt;
    upsertConversation(conversation);
    return message;
  },

  async rewriteFromMessage(conversationId, messageId, payload) {
    const response = await updateConversationMessage(conversationId, messageId, payload || {});
    return upsertConversationPayload(response);
  },

  async renameIfDefault(id, title) {
    let conversation = this.get(id);
    if (!conversation) {
      conversation = await this.fetch(id);
    }
    if (!conversation) return null;

    if (!conversation.title || /^Nouvelle conversation/i.test(conversation.title)) {
      const updated = normalizeConversation(await updateConversation(id, { title }));
      return upsertConversation(preserveConversationShape(updated, conversation));
    }
    return conversation;
  },

  async update(id, payload) {
    const conversation = this.get(id);
    const updated = normalizeConversation(await updateConversation(id, payload));
    return upsertConversation(preserveConversationShape(updated, conversation));
  },

  async updateFolder(id, payload) {
    const updated = normalizeFolder(await updateFolderRequest(id, payload || {}));
    const idx = foldersCache.findIndex((item) => item.id === updated.id);
    if (idx >= 0) foldersCache[idx] = updated;
    else foldersCache.push(updated);
    sortFolders();
    return updated;
  },

  async remove(id) {
    await deleteConversation(id);
    cache = cache.filter((item) => item.id !== id);
    if (this.currentId() === id) this.clearCurrent();
  },

  async removeFolder(id) {
    await deleteFolderRequest(id);
    foldersCache = foldersCache.filter((item) => item.id !== id);
    openFolders.delete(id);
    cache = cache.map((item) => (
      item.folderId === id
        ? { ...item, folderId: null }
        : item
    ));
  },
};

function groupLabel(ts) {
  const now = new Date();
  const startToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const startYesterday = startToday - 86400000;
  if (ts >= startToday) return "Aujourd'hui";
  if (ts >= startYesterday) return 'Hier';
  return 'Jours precedents';
}

export function fmtTitle(s) {
  s = (s || '').replace(/\s+/g, ' ').trim();
  return s ? s.slice(0, 64) : 'Nouvelle conversation';
}

function fmtFolderName(s) {
  const clean = (s || '').replace(/\s+/g, ' ').trim();
  return clean ? clean.slice(0, 80) : '';
}

function normalizeFolderLookupKey(value) {
  return String(value || '')
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .replace(/[^\p{L}\p{N}]+/gu, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .toLocaleLowerCase('fr');
}

function resolveFolderFromInput(folders, rawInput) {
  const formatted = fmtFolderName(rawInput);
  if (!formatted) {
    return { status: 'empty', folder: null, formatted };
  }

  const exact = folders.filter((item) => item.name.localeCompare(formatted, 'fr', { sensitivity: 'base' }) === 0);
  if (exact.length === 1) {
    return { status: 'match', folder: exact[0], formatted };
  }

  const key = normalizeFolderLookupKey(formatted);
  const normalizedMatches = folders.filter((item) => normalizeFolderLookupKey(item.name) === key);
  if (normalizedMatches.length === 1) {
    return { status: 'match', folder: normalizedMatches[0], formatted };
  }
  if (normalizedMatches.length > 1) {
    return { status: 'ambiguous', folder: null, formatted };
  }

  return { status: 'missing', folder: null, formatted };
}

function conversationCount(conversation) {
  return Math.max(Number(conversation?.messageCount || 0), (conversation?.messages || []).length);
}

function createConversationIcon() {
  const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  icon.setAttribute('class', 'ico');
  icon.setAttribute('viewBox', '0 0 24 24');
  icon.setAttribute('fill', 'none');
  icon.innerHTML = '<path d="M5 12h14M5 6h14M5 18h8" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/>';
  return icon;
}

function createFolderIcon() {
  const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  icon.setAttribute('class', 'ico');
  icon.setAttribute('viewBox', '0 0 24 24');
  icon.setAttribute('fill', 'none');
  icon.innerHTML = '<path d="M3.75 7.5a2.25 2.25 0 0 1 2.25-2.25h4.082a2.25 2.25 0 0 1 1.59.659l1.244 1.244a2.25 2.25 0 0 0 1.59.659H18a2.25 2.25 0 0 1 2.25 2.25v6.193A2.25 2.25 0 0 1 18 18.5H6a2.25 2.25 0 0 1-2.25-2.25V7.5Z" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"/>';
  return icon;
}

function createChevronIcon(open) {
  const icon = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  icon.setAttribute('class', `folder-chevron${open ? ' open' : ''}`);
  icon.setAttribute('viewBox', '0 0 24 24');
  icon.setAttribute('fill', 'none');
  icon.setAttribute('aria-hidden', 'true');
  icon.innerHTML = '<path d="M9 6l6 6-6 6" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>';
  return icon;
}

function createMenuButton(label, { danger = false, onClick }) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = danger ? 'conv-menu-btn danger' : 'conv-menu-btn';
  button.setAttribute('role', 'menuitem');
  button.textContent = label;
  button.addEventListener('click', onClick);
  return button;
}

function listFolderNames() {
  return Store.folders().map((folder) => folder.name);
}

export async function mountHistory() {
  const cont = qs('#history');
  if (!cont) return;
  ensureMenuEvents();

  const render = async ({ refresh = true } = {}) => {
    if (refresh) {
      try {
        await Store.refresh();
      } catch (err) {
        console.warn('[mountHistory] refresh failed', err);
      }
    }

    const folderBtn = qs('#new-folder');
    if (folderBtn) {
      folderBtn.onclick = async (event) => {
        event.preventDefault();
        event.stopPropagation();
        const nextName = window.prompt('Nom du nouveau dossier');
        if (nextName == null) return;
        const formatted = fmtFolderName(nextName);
        if (!formatted) return;
        try {
          await Store.createFolder({ name: formatted });
          await render();
        } catch (err) {
          window.alert(err?.message || 'Impossible de creer le dossier.');
        }
      };
    }

    closeActiveMenu();
    cont.innerHTML = '';

    const allFolders = Store.folders();
    const visibleConversations = Store.load()
      .filter((conversation) => !conversation.archived && conversationCount(conversation) > 0)
      .sort((a, b) => b.updatedAt - a.updatedAt);
    const folderMap = new Map(allFolders.map((folder) => [folder.id, folder]));
    const rootConversations = visibleConversations.filter((conversation) => !conversation.folderId || !folderMap.has(conversation.folderId));

    function buildMovePrompt(conversation) {
      const names = listFolderNames();
      const currentFolder = conversation.folderId ? folderMap.get(conversation.folderId) : null;
      const lines = names.length ? names.map((name) => `- ${name}`).join('\n') : '(aucun dossier)';
      const current = currentFolder?.name || '';
      return window.prompt(
        [
          current
            ? `Nom du dossier de destination (actuel : ${current}).`
            : 'Nom du dossier de destination.',
          'Laissez vide pour annuler.',
          '',
          'Dossiers disponibles :',
          lines,
        ].join('\n'),
        current,
      );
    }

    async function handleMoveConversation(conversation) {
      if (allFolders.length === 0) {
        window.alert('Creez d’abord un dossier.');
        return;
      }
      const target = buildMovePrompt(conversation);
      if (target == null) return;
      const resolution = resolveFolderFromInput(allFolders, target);
      if (resolution.status === 'empty') return;
      if (resolution.status === 'ambiguous') {
        window.alert(
          [
            'Plusieurs dossiers ressemblent a ce nom.',
            'Entrez exactement l’un des dossiers disponibles :',
            ...listFolderNames().map((name) => `- ${name}`),
          ].join('\n'),
        );
        return;
      }
      if (resolution.status === 'missing' || !resolution.folder) {
        window.alert(
          [
            `Dossier introuvable : "${resolution.formatted}".`,
            'La conversation reste dans la sidebar, sans changement.',
            '',
            'Dossiers disponibles :',
            ...listFolderNames().map((name) => `- ${name}`),
          ].join('\n'),
        );
        return;
      }
      openFolders.add(resolution.folder.id);
      await Store.update(conversation.id, { folder_id: resolution.folder.id });
      await render({ refresh: false });
      try {
        await Store.refresh();
      } catch (err) {
        console.warn('[history] refresh after move failed', err);
      }
      await render({ refresh: false });
    }

    function buildConversationRow(conversation, { nested = false } = {}) {
      const titleLabel = conversation.title || 'Nouvelle conversation';
      const row = document.createElement('div');
      row.className = nested ? 'conv-row nested' : 'conv-row';

      const link = document.createElement('a');
      link.className = 'conv conv-link';
      link.href = '#';
      link.dataset.id = conversation.id;
      link.title = titleLabel;
      if (Store.currentId() === conversation.id) link.classList.add('selected');

      const title = document.createElement('span');
      title.className = 'conv-title';
      title.textContent = titleLabel;

      link.append(createConversationIcon(), title);
      link.addEventListener('click', async (event) => {
        event.preventDefault();
        try {
          closeActiveMenu();
          await openConversation(conversation.id);
          await render();
        } catch (err) {
          console.warn('[history] open conversation failed', err);
        }
      });

      const actions = document.createElement('button');
      actions.type = 'button';
      actions.className = 'conv-actions-btn';
      actions.setAttribute('aria-label', `Actions pour ${titleLabel}`);
      actions.setAttribute('aria-haspopup', 'menu');
      actions.setAttribute('aria-expanded', 'false');
      actions.innerHTML = '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><circle cx="6" cy="12" r="1.7"></circle><circle cx="12" cy="12" r="1.7"></circle><circle cx="18" cy="12" r="1.7"></circle></svg>';

      const menu = document.createElement('div');
      menu.className = 'conv-menu';
      menu.setAttribute('role', 'menu');

      actions.addEventListener('click', (event) => {
        event.preventDefault();
        event.stopPropagation();
        openMenu(row, actions, menu);
      });

      const renameBtn = createMenuButton('Modifier', {
        onClick: async (event) => {
          event.preventDefault();
          event.stopPropagation();
          closeActiveMenu();

          const nextTitle = window.prompt('Modifier le nom de la conversation', titleLabel);
          if (nextTitle == null) return;

          const formatted = fmtTitle(nextTitle);
          if (!formatted) return;

          try {
            await Store.update(conversation.id, { title: formatted });
            await render();
          } catch (err) {
            console.warn('[history] rename conversation failed', err);
          }
        },
      });

      const moveBtn = createMenuButton('Déplacer vers un dossier', {
        onClick: async (event) => {
          event.preventDefault();
          event.stopPropagation();
          closeActiveMenu();
          try {
            await handleMoveConversation(conversation);
          } catch (err) {
            console.warn('[history] move conversation failed', err);
          }
        },
      });

      const deleteBtn = createMenuButton('Supprimer', {
        danger: true,
        onClick: async (event) => {
          event.preventDefault();
          event.stopPropagation();
          closeActiveMenu();

          const ok = window.confirm(`Supprimer la conversation "${titleLabel}" ?`);
          if (!ok) return;

          const wasCurrent = Store.currentId() === conversation.id;
          try {
            await Store.remove(conversation.id);
            if (wasCurrent) clearChat();
            await render();
          } catch (err) {
            console.warn('[history] delete conversation failed', err);
          }
        },
      });

      menu.append(renameBtn, moveBtn, deleteBtn);
      row.append(link, actions, menu);
      return row;
    }

    if (allFolders.length > 0) {
      const head = document.createElement('div');
      head.className = 'side-title';
      head.textContent = 'Dossiers';
      cont.appendChild(head);

      for (const folder of allFolders) {
        const folderConversations = visibleConversations.filter((conversation) => conversation.folderId === folder.id);
        const isOpen = openFolders.has(folder.id);
        const block = document.createElement('div');
        block.className = 'folder-block';
        if (isOpen) block.classList.add('open');

        const row = document.createElement('div');
        row.className = 'conv-row folder-row';

        const summary = document.createElement('button');
        summary.type = 'button';
        summary.className = 'conv conv-link folder-summary folder-toggle';
        summary.title = folder.name;
        summary.setAttribute('aria-expanded', String(isOpen));
        summary.setAttribute('aria-controls', `folder-list-${folder.id}`);
        summary.addEventListener('click', (event) => {
          event.preventDefault();
          event.stopPropagation();
          if (openFolders.has(folder.id)) openFolders.delete(folder.id);
          else openFolders.add(folder.id);
          render();
        });

        const title = document.createElement('span');
        title.className = 'conv-title';
        title.textContent = folder.name;

        const count = document.createElement('span');
        count.className = 'folder-meta';
        count.textContent = String(folderConversations.length);

        summary.append(createChevronIcon(isOpen), createFolderIcon(), title, count);

        const actions = document.createElement('button');
        actions.type = 'button';
        actions.className = 'conv-actions-btn';
        actions.setAttribute('aria-label', `Actions pour le dossier ${folder.name}`);
        actions.setAttribute('aria-haspopup', 'menu');
        actions.setAttribute('aria-expanded', 'false');
        actions.innerHTML = '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><circle cx="6" cy="12" r="1.7"></circle><circle cx="12" cy="12" r="1.7"></circle><circle cx="18" cy="12" r="1.7"></circle></svg>';

        const menu = document.createElement('div');
        menu.className = 'conv-menu';
        menu.setAttribute('role', 'menu');

        actions.addEventListener('click', (event) => {
          event.preventDefault();
          event.stopPropagation();
          openMenu(row, actions, menu);
        });

        const renameBtn = createMenuButton('Renommer le dossier', {
          onClick: async (event) => {
            event.preventDefault();
            event.stopPropagation();
            closeActiveMenu();

            const nextName = window.prompt('Renommer le dossier', folder.name);
            if (nextName == null) return;
            const formatted = fmtFolderName(nextName);
            if (!formatted) return;

            try {
              await Store.updateFolder(folder.id, { name: formatted });
              await render();
            } catch (err) {
              window.alert(err?.message || 'Impossible de renommer le dossier.');
            }
          },
        });

        const deleteBtn = createMenuButton('Supprimer le dossier', {
          danger: true,
          onClick: async (event) => {
            event.preventDefault();
            event.stopPropagation();
            closeActiveMenu();

            const ok = window.confirm(`Supprimer le dossier "${folder.name}" ? Les conversations reviendront hors dossier.`);
            if (!ok) return;

            try {
              await Store.removeFolder(folder.id);
              await render();
            } catch (err) {
              window.alert(err?.message || 'Impossible de supprimer le dossier.');
            }
          },
        });

        menu.append(renameBtn, deleteBtn);
        row.append(summary, actions, menu);
        block.appendChild(row);

        if (folderConversations.length > 0 && isOpen) {
          const list = document.createElement('div');
          list.className = 'folder-conversation-list';
          list.id = `folder-list-${folder.id}`;
          for (const conversation of folderConversations) {
            list.appendChild(buildConversationRow(conversation, { nested: true }));
          }
          block.appendChild(list);
        }

        cont.appendChild(block);
      }
    }

    const groups = { "Aujourd'hui": [], Hier: [], 'Jours precedents': [] };
    for (const conversation of rootConversations) {
      groups[groupLabel(conversation.updatedAt)].push(conversation);
    }

    for (const label of ["Aujourd'hui", 'Hier', 'Jours precedents']) {
      const arr = groups[label];
      if (arr.length === 0) continue;

      const head = document.createElement('div');
      head.className = 'side-title';
      head.textContent = label;
      cont.appendChild(head);

      for (const conversation of arr) {
        cont.appendChild(buildConversationRow(conversation));
      }
    }

    if (allFolders.length === 0 && rootConversations.length === 0) {
      const head = document.createElement('div');
      head.className = 'side-title';
      head.textContent = "Aujourd'hui";
      cont.appendChild(head);

      const empty = document.createElement('div');
      empty.style.color = 'var(--muted)';
      empty.style.padding = '6px 12px';
      empty.style.fontSize = '.9rem';
      empty.textContent = 'Aucune conversation';
      cont.appendChild(empty);
    }
  };

  await render();
}
