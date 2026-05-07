import { qs } from '../core/dom.js';
import { CODING_AGENTS } from '../config/coding-agents.js';
import {
  getClaudeWorkspace,
  getCodingAgent,
  getOpenCodeWorkspace,
  setClaudeWorkspace,
  setCodingAgent,
  setOpenCodeWorkspace,
} from '../store/settings.js';
import {
  loadSystemPrompt,
  readSys,
  saveSystemPromptValue,
} from '../net/ollama.js';
import { resolveCodingAgentWorkspace } from '../net/conversationsApi.js';

const BASE_FOLDER_LABELS = {
  documents: 'Documents',
  desktop: 'Desktop',
  pictures: 'Pictures',
  downloads: 'Downloads',
  custom: '',
};

const WORKSPACE_AGENTS = {
  opencode: {
    label: 'OpenCode',
    defaultDirectory: 'OpenCode',
    getSettings: getOpenCodeWorkspace,
    setSettings: setOpenCodeWorkspace,
    showWslPreview: true,
  },
  claude: {
    label: 'Claude Code',
    defaultDirectory: 'Claude',
    getSettings: getClaudeWorkspace,
    setSettings: setClaudeWorkspace,
    showWslPreview: false,
  },
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
    populateAgentSettings();
    sm.style.display='flex';
    refreshCurrentWorkspaceStatus(false).catch(() => {});
  };
  se.addEventListener('click', open);
  se.addEventListener('keydown', (e)=>{ if(e.key==='Enter' || e.key===' '){ open(e); } });
  sm.addEventListener('click', (e)=>{ if(e.target===sm) sm.style.display='none'; });
  wireSettingsTabs();
  wireSettingsAgentChoice();
  wireWorkspaceSettings('opencode');
  wireWorkspaceSettings('claude');
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

function wireSettingsAgentChoice(){
  const selector = qs('#settings-coding-agent-select');
  if(!selector) return;
  mountSettingsAgentOptions(selector);
  selector.addEventListener('change', async () => {
    const agent = String(selector.value || '').trim() || 'codex';
    setCodingAgent(agent);
    syncAgentSettingsPanels(agent);
    await refreshCurrentWorkspaceStatus(false).catch(() => {});
  });
  document.addEventListener('settings:coding-agent-changed', () => {
    selector.value = getCodingAgent();
    syncAgentSettingsPanels(selector.value);
  });
}

function mountSettingsAgentOptions(selector){
  selector.replaceChildren();
  for(const agent of CODING_AGENTS){
    const option = document.createElement('option');
    option.value = agent.id;
    option.textContent = agent.label;
    selector.appendChild(option);
  }
}

function wireWorkspaceSettings(agent){
  const base = qs(`#${agent}-base-folder`);
  const custom = qs(`#${agent}-custom-base`);
  const directory = qs(`#${agent}-work-directory`);
  const test = qs(`#${agent}-test-workspace`);
  const save = qs(`#${agent}-save-workspace`);
  if(!base || !directory || !test || !save) return;

  const update = () => {
    syncCustomBaseVisibility(agent);
    renderWorkspacePreview(agent);
    setWorkspaceStatus(agent, `Dossier ${WORKSPACE_AGENTS[agent].label} pret a etre verifie.`, '');
  };

  base.addEventListener('change', update);
  custom?.addEventListener('input', update);
  directory.addEventListener('input', update);
  test.addEventListener('click', async () => {
    try { await refreshWorkspaceStatus(agent, false); } catch (_) {}
  });
  save.addEventListener('click', async () => {
    const settings = readWorkspaceForm(agent);
    try{
      const result = await refreshWorkspaceStatus(agent, true);
      WORKSPACE_AGENTS[agent].setSettings(settings);
      renderWorkspacePreview(agent, result);
    }catch(_){
    }
  });
}

function populateAgentSettings(){
  const selector = qs('#settings-coding-agent-select');
  if(selector){
    mountSettingsAgentOptions(selector);
    selector.value = getCodingAgent();
  }
  populateWorkspaceSettings('opencode');
  populateWorkspaceSettings('claude');
  syncAgentSettingsPanels(getCodingAgent());
}

function populateWorkspaceSettings(agent){
  const config = WORKSPACE_AGENTS[agent];
  const settings = config.getSettings();
  const base = qs(`#${agent}-base-folder`);
  const custom = qs(`#${agent}-custom-base`);
  const directory = qs(`#${agent}-work-directory`);
  if(base) base.value = settings.baseFolder || 'documents';
  if(custom) custom.value = settings.customBasePath || '';
  if(directory) directory.value = settings.workDirectory || config.defaultDirectory;
  syncCustomBaseVisibility(agent);
  renderWorkspacePreview(agent);
  setWorkspaceStatus(agent, `Dossier ${config.label} pret a etre verifie.`, '');
}

function syncAgentSettingsPanels(agent){
  const value = String(agent || 'codex').trim();
  document.querySelectorAll('[data-agent-settings-panel]').forEach((panel) => {
    panel.hidden = panel.getAttribute('data-agent-settings-panel') !== value;
  });
}

function currentWorkspaceAgent(){
  const selector = qs('#settings-coding-agent-select');
  const value = String(selector?.value || getCodingAgent() || '').trim();
  return WORKSPACE_AGENTS[value] ? value : '';
}

function readWorkspaceForm(agent){
  const config = WORKSPACE_AGENTS[agent];
  const base = qs(`#${agent}-base-folder`);
  const custom = qs(`#${agent}-custom-base`);
  const directory = qs(`#${agent}-work-directory`);
  return {
    baseFolder: String(base?.value || 'documents').trim(),
    customBasePath: String(custom?.value || '').trim(),
    workDirectory: String(directory?.value || '').trim() || config.defaultDirectory,
  };
}

function syncCustomBaseVisibility(agent){
  const settings = readWorkspaceForm(agent);
  const row = qs(`#${agent}-custom-base-row`);
  if(row) row.hidden = settings.baseFolder !== 'custom';
}

function renderWorkspacePreview(agent, result = null){
  const settings = readWorkspaceForm(agent);
  const windows = qs(`#${agent}-windows-preview`);
  const wsl = qs(`#${agent}-wsl-preview`);
  if(windows) windows.textContent = anonymizeWindowsPath(result?.windowsPath) || buildWindowsPreview(settings);
  if(wsl) wsl.textContent = anonymizeWslPath(result?.wslPath) || buildWslPreview(settings);
}

function buildWindowsPreview(settings){
  const workDirectory = settings.workDirectory || 'OpenCode';
  if(settings.baseFolder === 'custom'){
    const customBase = settings.customBasePath || 'Chemin personnalise';
    return `${customBase.replace(/[\\\/]+$/, '')}\\${workDirectory}`;
  }
  const label = BASE_FOLDER_LABELS[settings.baseFolder] || BASE_FOLDER_LABELS.documents;
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

function setWorkspaceStatus(agent, message, type){
  const status = qs(`#${agent}-workspace-status`);
  if(!status) return;
  status.textContent = message;
  status.classList.remove('ok', 'warn', 'error');
  if(type) status.classList.add(type);
}

async function refreshCurrentWorkspaceStatus(create){
  const agent = currentWorkspaceAgent();
  if(!agent) return null;
  return refreshWorkspaceStatus(agent, create);
}

async function refreshWorkspaceStatus(agent, create){
  const config = WORKSPACE_AGENTS[agent];
  const settings = readWorkspaceForm(agent);
  setWorkspaceStatus(agent, create ? `Creation du dossier ${config.label}...` : `Verification du dossier ${config.label}...`, '');
  try{
    const result = await resolveCodingAgentWorkspace(agent, settings, { create });
    renderWorkspacePreview(agent, result);
    if(result?.exists){
      setWorkspaceStatus(agent, result?.message || `Dossier ${config.label} valide.`, 'ok');
    }else{
      setWorkspaceStatus(agent, result?.message || 'Dossier introuvable. Il sera cree a l enregistrement.', 'warn');
    }
    return result;
  }catch(err){
    const message = err?.message || `Dossier ${config.label} invalide.`;
    setWorkspaceStatus(agent, message, 'error');
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
