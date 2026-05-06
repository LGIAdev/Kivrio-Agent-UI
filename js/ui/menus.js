import { qs } from '../core/dom.js';
import { getOpenCodeWorkspace, setOpenCodeWorkspace } from '../store/settings.js';
import {
  loadSystemPrompt,
  readSys,
  saveSystemPromptValue,
} from '../net/ollama.js';
import { resolveOpenCodeWorkspace } from '../net/conversationsApi.js';

const OPENCODE_BASE_LABELS = {
  documents: 'Documents',
  desktop: 'Desktop',
  pictures: 'Pictures',
  downloads: 'Downloads',
  custom: '',
};

export function wireUserMenu(){
  const user = qs('#user-entry'); const menu = qs('#user-menu');
  if(!user || !menu) return;
  const toggle = (e)=>{ if(e) e.preventDefault(); const isOpen = menu.classList.toggle('open'); menu.setAttribute('aria-hidden', isOpen?'false':'true'); };
  user.style.cursor = 'pointer';
  user.addEventListener('click', toggle);
  user.addEventListener('keydown', (e)=>{ if(e.key==='Enter' || e.key===' '){ toggle(e); } });
  document.addEventListener('click', (e)=>{ if(menu && !menu.contains(e.target) && !user.contains(e.target)){ menu.classList.remove('open'); menu.setAttribute('aria-hidden','true'); } });
}

export function wireSettingsModal(){
  const se = qs('#settings-entry'); const sm = qs('#settings-modal');
  if(!se || !sm) return;
  const open = (e)=>{
    if(e) e.preventDefault();
    qs('#user-menu')?.classList.remove('open');
    populateOpenCodeWorkspaceSettings();
    sm.style.display='flex';
    refreshOpenCodeWorkspaceStatus(false).catch(() => {});
  };
  se.addEventListener('click', open);
  se.addEventListener('keydown', (e)=>{ if(e.key==='Enter' || e.key===' '){ open(e); } });
  sm.addEventListener('click', (e)=>{ if(e.target===sm) sm.style.display='none'; });
  wireSettingsTabs();
  wireOpenCodeWorkspaceSettings();
}

function wireSettingsTabs(){
  const tabs = Array.from(document.querySelectorAll('[data-settings-panel]'));
  const panels = Array.from(document.querySelectorAll('[data-settings-panel-content]'));
  if(!tabs.length || !panels.length) return;
  tabs.forEach((tab) => {
    tab.addEventListener('click', () => {
      const target = tab.getAttribute('data-settings-panel') || 'agents';
      tabs.forEach((item) => {
        const active = item === tab;
        item.classList.toggle('active', active);
        item.setAttribute('aria-selected', active ? 'true' : 'false');
      });
      panels.forEach((panel) => {
        panel.hidden = panel.getAttribute('data-settings-panel-content') !== target;
      });
    });
  });
}

function wireOpenCodeWorkspaceSettings(){
  const base = qs('#opencode-base-folder');
  const custom = qs('#opencode-custom-base');
  const directory = qs('#opencode-work-directory');
  const test = qs('#opencode-test-workspace');
  const save = qs('#opencode-save-workspace');
  if(!base || !directory || !test || !save) return;

  const update = () => {
    syncOpenCodeCustomBaseVisibility();
    renderOpenCodeWorkspacePreview();
    setOpenCodeWorkspaceStatus('Dossier OpenCode prêt à être vérifié.', '');
  };

  base.addEventListener('change', update);
  custom?.addEventListener('input', update);
  directory.addEventListener('input', update);
  test.addEventListener('click', async () => {
    try { await refreshOpenCodeWorkspaceStatus(false); } catch (_) {}
  });
  save.addEventListener('click', async () => {
    const settings = readOpenCodeWorkspaceForm();
    try{
      const result = await refreshOpenCodeWorkspaceStatus(true);
      setOpenCodeWorkspace(settings);
      renderOpenCodeWorkspacePreview(result);
    }catch(_){
    }
  });
}

function populateOpenCodeWorkspaceSettings(){
  const settings = getOpenCodeWorkspace();
  const base = qs('#opencode-base-folder');
  const custom = qs('#opencode-custom-base');
  const directory = qs('#opencode-work-directory');
  if(base) base.value = settings.baseFolder || 'documents';
  if(custom) custom.value = settings.customBasePath || '';
  if(directory) directory.value = settings.workDirectory || 'OpenCode';
  syncOpenCodeCustomBaseVisibility();
  renderOpenCodeWorkspacePreview();
  setOpenCodeWorkspaceStatus('Dossier OpenCode prêt à être vérifié.', '');
}

function readOpenCodeWorkspaceForm(){
  const base = qs('#opencode-base-folder');
  const custom = qs('#opencode-custom-base');
  const directory = qs('#opencode-work-directory');
  return {
    baseFolder: String(base?.value || 'documents').trim(),
    customBasePath: String(custom?.value || '').trim(),
    workDirectory: String(directory?.value || '').trim() || 'OpenCode',
  };
}

function syncOpenCodeCustomBaseVisibility(){
  const settings = readOpenCodeWorkspaceForm();
  const row = qs('#opencode-custom-base-row');
  if(row) row.hidden = settings.baseFolder !== 'custom';
}

function renderOpenCodeWorkspacePreview(result = null){
  const settings = readOpenCodeWorkspaceForm();
  const windows = qs('#opencode-windows-preview');
  const wsl = qs('#opencode-wsl-preview');
  if(windows) windows.textContent = anonymizeWindowsPath(result?.windowsPath) || buildWindowsPreview(settings);
  if(wsl) wsl.textContent = anonymizeWslPath(result?.wslPath) || buildWslPreview(settings);
}

function buildWindowsPreview(settings){
  const workDirectory = settings.workDirectory || 'OpenCode';
  if(settings.baseFolder === 'custom'){
    const customBase = settings.customBasePath || 'Chemin personnalisé';
    return `${customBase.replace(/[\\\/]+$/, '')}\\${workDirectory}`;
  }
  const label = OPENCODE_BASE_LABELS[settings.baseFolder] || OPENCODE_BASE_LABELS.documents;
  return `C:\\Users\\Utilisateur\\${label}\\${workDirectory}`;
}

function buildWslPreview(settings){
  const windowsPath = buildWindowsPreview(settings);
  const match = windowsPath.match(/^([A-Za-z]):\\(.+)$/);
  if(!match) return '/mnt/...';
  return `/mnt/${match[1].toLowerCase()}/${match[2].replace(/\\/g, '/')}`;
}

function anonymizeWindowsPath(path){
  const value = String(path || '').trim();
  if(!value) return '';
  return value.replace(/^([A-Za-z]:\\Users\\)[^\\]+(\\.*)$/i, '$1Utilisateur$2');
}

function anonymizeWslPath(path){
  const value = String(path || '').trim();
  if(!value) return '';
  return value.replace(/^(\/mnt\/[a-z]\/Users\/)[^/]+(\/.*)$/i, '$1Utilisateur$2');
}

function setOpenCodeWorkspaceStatus(message, type){
  const status = qs('#opencode-workspace-status');
  if(!status) return;
  status.textContent = message;
  status.classList.remove('ok', 'warn', 'error');
  if(type) status.classList.add(type);
}

async function refreshOpenCodeWorkspaceStatus(create){
  const settings = readOpenCodeWorkspaceForm();
  setOpenCodeWorkspaceStatus(create ? 'Création du dossier OpenCode...' : 'Vérification du dossier OpenCode...', '');
  try{
    const result = await resolveOpenCodeWorkspace(settings, { create });
    renderOpenCodeWorkspacePreview(result);
    if(result?.exists){
      setOpenCodeWorkspaceStatus(result?.message || 'Dossier OpenCode valide.', 'ok');
    }else{
      setOpenCodeWorkspaceStatus(result?.message || 'Dossier introuvable. Il sera créé à l’enregistrement.', 'warn');
    }
    return result;
  }catch(err){
    const message = err?.message || 'Dossier OpenCode invalide.';
    setOpenCodeWorkspaceStatus(message, 'error');
    throw err;
  }
}

export function wirePromptModal(){
  const pe = qs('#prompt-entry'); const pm = qs('#prompt-modal'); const pt = qs('#prompt-text'); const ps = qs('#prompt-save');
  if(!pe || !pm || !pt || !ps) return;
  const open = async (e)=>{
    if(e) e.preventDefault();
    pm.style.display='flex';
    pt.value = readSys();
    try{
      await loadSystemPrompt(true);
      pt.value = readSys();
    }catch(err){
      alert(err?.message || 'Impossible de charger le prompt systeme.');
    }
  };
  const save = async (e)=>{
    if(e) e.preventDefault();
    try{
      await saveSystemPromptValue(pt.value || '');
      pm.style.display = 'none';
    }catch(err){
      alert(err?.message || 'Impossible d enregistrer le prompt systeme.');
    }
  };
  pe.addEventListener('click', open);
  pe.addEventListener('keydown', (e)=>{ if(e.key==='Enter' || e.key===' '){ open(e); } });
  ps.addEventListener('click', save);
  pm.addEventListener('click', (e)=>{ if(e.target===pm) pm.style.display='none'; });
}
