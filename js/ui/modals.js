import { qs } from '../core/dom.js';

export function wireAddModal(){
  const addBtn = qs('#add-btn'); const modal = qs('#add-modal');
  if(addBtn && modal){
    addBtn.addEventListener('click', (e)=>{ e.preventDefault(); modal.style.display='flex'; modal.setAttribute('aria-hidden','false'); });
    modal.addEventListener('click', (e)=>{ if(e.target===modal){ modal.style.display='none'; modal.setAttribute('aria-hidden','true'); } });
  }
}
