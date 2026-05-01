const FILE_CAPABLE_MODELS = new Set([
  'qwen3-vl:30b-a3b-instruct',
]);

function normalizeModelName(model) {
  return String(model || '').trim().toLowerCase();
}

export function canModelReadFiles(model) {
  return FILE_CAPABLE_MODELS.has(normalizeModelName(model));
}

export function getFileSupportErrorMessage() {
  return 'Le modele selectionne ne lit pas directement les fichiers joints.';
}
