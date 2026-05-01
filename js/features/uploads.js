import { canModelReadFiles } from '../config/file-capable-models.js';

const LIMITS = {
  image: 10 * 1024 * 1024,
  pdf: 20 * 1024 * 1024,
  text: 2 * 1024 * 1024,
  total: 25 * 1024 * 1024,
  count: 5,
};

const state = {
  items: [],
  addBtn: null,
  fileInput: null,
  list: null,
  error: null,
};

function makeId() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `att-${Date.now()}-${Math.random().toString(16).slice(2, 10)}`;
}

function getExt(name) {
  const parts = String(name || '').split('.');
  return parts.length > 1 ? parts.at(-1).toLowerCase() : '';
}

function kindForFile(file) {
  const type = String(file?.type || '').toLowerCase();
  const ext = getExt(file?.name || '');
  if (type.startsWith('image/') || ['jpg', 'jpeg', 'png', 'webp'].includes(ext)) return 'image';
  if (type === 'application/pdf' || ext === 'pdf') return 'pdf';
  if (type.startsWith('text/') || ['txt', 'md'].includes(ext)) return 'text';
  return 'unsupported';
}

function formatBytes(bytes) {
  const value = Number(bytes || 0);
  if (value < 1024) return `${value} o`;
  if (value < 1024 * 1024) return `${Math.round(value / 1024)} Ko`;
  return `${(value / (1024 * 1024)).toFixed(value >= 10 * 1024 * 1024 ? 0 : 1)} Mo`;
}

function totalBytes(items = state.items) {
  return items.reduce((sum, item) => sum + Number(item.file?.size || 0), 0);
}

function setError(message = '') {
  if (!state.error) return;
  state.error.textContent = message;
  state.error.hidden = !message;
}

function statusLabel(item) {
  if (item.status === 'uploading') return 'Televersement...';
  if (item.status === 'error') return item.error || 'Erreur';
  if (item.kind === 'pdf') return 'PDF bientot disponible';
  return 'Pret a envoyer';
}

function makeFileBadge(item) {
  const badge = document.createElement('div');
  badge.className = 'pending-attachment-thumb';
  if (item.kind === 'image' && item.objectUrl) {
    const img = new Image();
    img.src = item.objectUrl;
    img.alt = item.file.name;
    badge.appendChild(img);
    return badge;
  }

  const label = document.createElement('span');
  label.textContent = (getExt(item.file.name || '') || 'FILE').slice(0, 4).toUpperCase();
  badge.appendChild(label);
  return badge;
}

function renderPendingUploads() {
  if (!state.list) return;
  state.list.innerHTML = '';
  for (const item of state.items) {
    const card = document.createElement('div');
    card.className = 'pending-attachment';
    if (item.status === 'error') card.classList.add('is-error');

    const preview = makeFileBadge(item);
    const meta = document.createElement('div');
    meta.className = 'pending-attachment-meta';

    const name = document.createElement('div');
    name.className = 'pending-attachment-name';
    name.textContent = item.file.name;

    const info = document.createElement('div');
    info.className = 'pending-attachment-info';
    info.textContent = `${item.kind.toUpperCase()} - ${formatBytes(item.file.size)}`;

    const status = document.createElement('div');
    status.className = 'pending-attachment-status';
    status.textContent = statusLabel(item);

    const remove = document.createElement('button');
    remove.type = 'button';
    remove.className = 'pending-attachment-remove';
    remove.dataset.id = item.id;
    remove.textContent = 'Supprimer';
    remove.disabled = item.status === 'uploading';

    meta.append(name, info, status);
    card.append(preview, meta, remove);
    state.list.appendChild(card);
  }
}

function pushFiles(files) {
  const next = [...state.items];
  let error = '';

  for (const file of files) {
    const kind = kindForFile(file);
    if (kind === 'unsupported') {
      error = `Type non pris en charge : ${file.name}`;
      continue;
    }
    if (next.length >= LIMITS.count) {
      error = `Maximum ${LIMITS.count} fichiers par message.`;
      break;
    }
    if (file.size > LIMITS[kind]) {
      error = `Fichier trop volumineux : ${file.name}`;
      continue;
    }
    if (totalBytes([...next, { file }]) > LIMITS.total) {
      error = 'Le total des fichiers depasse la limite autorisee.';
      break;
    }
    next.push({
      id: makeId(),
      file,
      kind,
      status: 'selected',
      error: '',
      objectUrl: kind === 'image' ? URL.createObjectURL(file) : null,
    });
  }

  state.items = next;
  setError(error);
  renderPendingUploads();
}

function removeItem(id) {
  const idx = state.items.findIndex((item) => item.id === id);
  if (idx < 0) return;
  const [removed] = state.items.splice(idx, 1);
  if (removed?.objectUrl) URL.revokeObjectURL(removed.objectUrl);
  setError('');
  renderPendingUploads();
}

function readFileAsBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const raw = String(reader.result || '');
      const comma = raw.indexOf(',');
      resolve(comma >= 0 ? raw.slice(comma + 1) : raw);
    };
    reader.onerror = () => reject(reader.error || new Error(`Lecture impossible: ${file.name}`));
    reader.readAsDataURL(file);
  });
}

function appendPromptBlock(promptText, label, blocks) {
  if (!blocks.length) return promptText;
  const prefix = promptText ? '\n\n' : '';
  return `${promptText}${prefix}${label}\n\n${blocks.join('\n\n---\n\n')}`;
}

function textFragmentsToPromptBlocks(textFragments) {
  return textFragments.map((item) => ['Fichier: ' + item.name, item.content].join('\n'));
}

function defaultPromptForAttachments({ mode, userText }) {
  const trimmed = String(userText || '').trim();
  if (trimmed) return trimmed;
  if (mode === 'image') return 'Analyse le fichier joint et aide-moi a resoudre le probleme.';
  return 'Analyse le document joint.';
}

export function wireUploads() {
  state.addBtn = document.getElementById('add-btn');
  state.fileInput = document.getElementById('file-input');
  state.list = document.getElementById('composer-attachments');
  state.error = document.getElementById('composer-upload-error');
  if (!state.addBtn || !state.fileInput || !state.list || !state.error) return;

  state.addBtn.addEventListener('click', () => {
    if (typeof state.fileInput.showPicker === 'function') {
      state.fileInput.showPicker();
      return;
    }
    state.fileInput.click();
  });
  state.fileInput.addEventListener('change', () => {
    const files = Array.from(state.fileInput.files || []);
    if (files.length) pushFiles(files);
    state.fileInput.value = '';
  });
  state.list.addEventListener('click', (event) => {
    const target = event.target;
    if (!(target instanceof HTMLElement)) return;
    const id = target.dataset.id;
    if (!id) return;
    removeItem(id);
  });

  renderPendingUploads();
}

export function hasPendingUploads() {
  return state.items.length > 0;
}

export function getPendingUploads() {
  return state.items.slice();
}

export function detachPendingUploads() {
  const items = state.items.slice();
  state.items = [];
  setError('');
  renderPendingUploads();
  return items;
}

export function restorePendingUploads(items, error = '') {
  state.items = Array.isArray(items) ? items.slice() : [];
  setError(error);
  renderPendingUploads();
}

export function releaseUploadItems(items) {
  for (const item of (items || [])) {
    if (item?.objectUrl) URL.revokeObjectURL(item.objectUrl);
  }
}

export function clearPendingUploads() {
  for (const item of state.items) {
    if (item.objectUrl) URL.revokeObjectURL(item.objectUrl);
  }
  state.items = [];
  setError('');
  renderPendingUploads();
}

export function setPendingUploadsState(status, error = '') {
  state.items = state.items.map((item) => ({
    ...item,
    status,
    error: status === 'error' ? error : '',
  }));
  setError(status === 'error' ? error : '');
  renderPendingUploads();
}

export async function preparePendingUploadsForSend({ model, userText, onStatus, items: providedItems }) {
  const items = Array.isArray(providedItems) ? providedItems.slice() : getPendingUploads();
  if (!items.length) {
    return {
      ok: true,
      promptText: String(userText || '').trim(),
      imagePayloads: [],
      suggestedTitle: String(userText || '').trim(),
    };
  }

  const hasPdf = items.some((item) => item.kind === 'pdf');
  if (hasPdf) {
    return {
      ok: false,
      message: 'Les PDF ne sont pas encore pris en charge dans ce MVP.',
    };
  }

  const imageItems = items.filter((item) => item.kind === 'image');
  const textItems = items.filter((item) => item.kind === 'text');
  const textFragments = [];
  for (const item of textItems) {
    const content = (await item.file.text()).trim();
    if (!content) continue;
    textFragments.push({ name: item.file.name, content });
  }

  const imagePayloads = [];
  let promptText = String(userText || '').trim();

  if (canModelReadFiles(model)) {
    for (const item of imageItems) {
      imagePayloads.push(await readFileAsBase64(item.file));
    }
    if (!promptText && (imagePayloads.length || textFragments.length)) {
      promptText = defaultPromptForAttachments({
        mode: imagePayloads.length ? 'image' : 'text',
        userText,
      });
    }
  } else if (imageItems.length) {
    return {
      ok: false,
      message: 'Les images jointes ne sont plus traitees via OCR local. Utilisez un modele multimodal pour analyser un fichier image.',
    };
  } else if (!promptText && textFragments.length) {
    promptText = defaultPromptForAttachments({ mode: 'text', userText });
  }

  if (textFragments.length) {
    promptText = appendPromptBlock(
      promptText,
      'Contenu des fichiers joints:',
      textFragmentsToPromptBlocks(textFragments),
    );
  }

  return {
    ok: true,
    promptText,
    imagePayloads,
    suggestedTitle: String(userText || '').trim() || items[0]?.file?.name || 'Piece jointe',
  };
}

if (typeof window !== 'undefined') {
  window.kivrioClearPendingUploads = clearPendingUploads;
}
