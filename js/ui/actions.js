import { qs } from '../core/dom.js';
import { sendCurrent, readBase, readModel, readCodingAgent, ping } from '../net/ollama.js';
import { getAgentStatus } from '../net/conversationsApi.js';

export function wireSendAction(){
  const ta = qs('#composer-input'); const btn = qs('#send-btn');
  if(ta){ ta.addEventListener('keydown', (e)=>{ if(e.key==='Enter' && (e.ctrlKey || e.metaKey)){ e.preventDefault(); sendCurrent(); } }); }
  if(btn){ btn.addEventListener('click', (e)=>{ e.preventDefault(); sendCurrent(); }); }
}

export function mountStatusPill({ authenticated = false } = {}){
  const label = document.querySelector('#model-label');
  if(!label) return;
  const modelPill = document.createElement('span'); modelPill.className='status-pill';
  const codexPill = document.createElement('span'); codexPill.className='status-pill';
  modelPill.setAttribute('role', 'status');
  modelPill.setAttribute('aria-live', 'polite');
  codexPill.setAttribute('role', 'status');
  codexPill.setAttribute('aria-live', 'polite');
  const setPill = (pill, state, txt, title = '')=>{
    pill.textContent='';
    const dot=document.createElement('span');
    dot.textContent='\u25CF';
    dot.className = state === 'ok' ? 'status-ok' : (state === 'wait' ? 'status-wait' : 'status-bad');
    const t=document.createElement('span');
    t.textContent = txt;
    pill.append(dot,t);
    pill.title = title || txt;
  };
  const refreshModelTitle = ()=>{
    const base = readBase();
    const model = readModel();
    label.title = `Base: ${base}\nModele: ${model}`;
    modelPill.title = `Base: ${base}\nModele: ${model}`;
  };
  const titleFromStatus = (status)=>[
    `Agent: ${status.agentLabel || 'Codex CLI'}`,
    `Provider: ${status.providerLabel || 'Ollama'}`,
    `Mode agent: ${status.agentMode || 'oss-local'}`,
    `Modele par defaut: ${status.defaultModel || '-'}`,
    `Modele app-server: ${status.processModel || '-'}`,
    `Etat: ${status.ready && status.healthy ? 'pret' : status.mode || 'indisponible'}`,
    `Mode: ${status.mode || '-'}`,
    `Port: ${status.port || '-'}`,
    `Chemin: ${status.agentPath || status.codexPath || '-'}`,
    status.wslFound !== undefined ? `WSL: ${status.wslFound ? (status.wslDistribution || 'detecte') : 'indisponible'}` : '',
    status.wslCommandPath ? `Chemin WSL: ${status.wslCommandPath}` : '',
    status.dialogueConnected === false ? 'Dialogue: non connecte' : '',
    status.lastError ? `Erreur: ${status.lastError}` : ''
  ].filter(Boolean).join('\n');
  const agentReadyLabel = (status, suffix)=>`${status?.providerLabel || 'Ollama'} ${status?.agentLabel || 'Codex CLI'} ${suffix}`;
  const modelNamesFromTags = (payload)=>{
    const arr = Array.isArray(payload) ? payload : (payload?.models || []);
    return arr.map((m)=>m?.name || m?.model).filter(Boolean);
  };
  const refresh = async ()=>{
    refreshModelTitle();
    try{
      const tags = await ping(readBase());
      const models = modelNamesFromTags(tags);
      const currentModel = readModel();
      if(models.length && !models.includes(currentModel)){
        throw new Error(`Modele Ollama indisponible: ${currentModel}`);
      }
      setPill(modelPill, 'ok','OK');
      modelPill.hidden = false;
    }catch(e){
      setPill(modelPill, 'bad','Indisponible', e?.message || 'Modele Ollama indisponible');
      modelPill.hidden = false;
      label.title = `${label.title}\nModele indisponible: ${e?.message || e}`;
    }

    try{
      const status = await getAgentStatus(readCodingAgent());
      const title = titleFromStatus(status || {});
      if(status?.ready && status?.healthy){
        setPill(codexPill, 'ok', agentReadyLabel(status, 'pr\u00EAt'), title);
      }else if(status?.running){
        setPill(codexPill, 'wait', agentReadyLabel(status, 'd\u00E9marre'), title);
      }else if(status?.mode === 'wsl-detected' || (status?.agentFound === true && status?.dialogueConnected === false)){
        setPill(codexPill, 'wait', agentReadyLabel(status, 'd\u00E9tect\u00E9 via WSL'), title);
      }else if(status?.agentFound === false){
        setPill(codexPill, 'bad', agentReadyLabel(status, 'introuvable'), title);
      }else if(status?.codexFound === false){
        setPill(codexPill, 'bad', agentReadyLabel(status, 'introuvable'), title);
      }else{
        setPill(codexPill, 'bad', agentReadyLabel(status, 'indisponible'), title);
      }
    }catch(e){
      setPill(codexPill, 'bad','Agent indisponible', e?.message || 'Agent indisponible');
    }
  };
  const holder = document.createElement('span');
holder.style.display = 'inline-flex';
holder.style.alignItems = 'center';
holder.style.gap = '6px';
holder.style.whiteSpace = 'nowrap';
label.parentNode.insertBefore(holder, label);
holder.append(label, modelPill, codexPill);
  modelPill.hidden = true;
  setPill(codexPill, authenticated ? 'wait' : 'bad', authenticated ? 'Ollama Codex CLI...' : 'Connexion requise');
  modelPill.addEventListener('click', refresh);
  codexPill.addEventListener('click', refresh);
  window.addEventListener('kivro:auth-success', refresh);
  document.addEventListener('settings:coding-agent-changed', refresh);
  document.addEventListener('conversation:current-changed', refresh);
  if(authenticated) refresh();
}
