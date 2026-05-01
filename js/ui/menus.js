import { qs } from '../core/dom.js';
import {
  loadSystemPrompt,
  readSys,
  saveSystemPromptValue,
} from '../net/ollama.js';

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
  const open = (e)=>{ if(e) e.preventDefault(); sm.style.display='flex'; };
  se.addEventListener('click', open);
  se.addEventListener('keydown', (e)=>{ if(e.key==='Enter' || e.key===' '){ open(e); } });
  sm.addEventListener('click', (e)=>{ if(e.target===sm) sm.style.display='none'; });
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
