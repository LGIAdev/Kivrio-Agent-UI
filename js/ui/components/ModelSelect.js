import { listModels } from '../../net/ollama.js';
import { getModel, setModel } from '../../store/settings.js';

function setSingleOption(el, value, label) {
  el.replaceChildren();
  const option = document.createElement('option');
  option.value = value;
  option.textContent = label;
  el.appendChild(option);
}

function setModelOptions(el, models) {
  el.replaceChildren();
  for (const model of models) {
    const option = document.createElement('option');
    option.value = model;
    option.textContent = model;
    el.appendChild(option);
  }
}

function setLabel(modelName) {
  const el = document.querySelector('#model-label');
  if (el) el.textContent = modelName || '(modele)';
}

export async function mountModelSelect() {
  const el = document.querySelector('#model-select');
  if (!el) return;

  el.disabled = true;
  el.setAttribute('aria-busy', 'true');
  setSingleOption(el, '', 'Chargement...');

  try {
    const models = await listModels();
    if (!Array.isArray(models) || models.length === 0) {
      setSingleOption(el, '', '(Aucun modele trouve)');
      return;
    }

    setModelOptions(el, models);

    const current = getModel();
    el.value = (current && models.includes(current)) ? current : models[0];

    setModel(el.value);
    setLabel(el.value);
    el.disabled = false;
  } catch (e) {
    console.error('ModelSelect load error:', e);
    setSingleOption(el, '', '(Ollama indisponible)');
    el.disabled = true;
  } finally {
    el.removeAttribute('aria-busy');
  }

  el.addEventListener('change', (ev) => {
    const model = ev.target?.value;
    if (!model) return;
    setModel(model);
    setLabel(model);
  });

  document.addEventListener('keydown', (ev) => {
    if (ev.altKey && (ev.key === 'm' || ev.key === 'M')) {
      ev.preventDefault();
      if (!el.disabled) el.focus();
    }
  });
}
