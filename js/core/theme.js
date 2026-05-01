import { qs, on } from './dom.js';

function applyTheme(theme){
  const t = (theme === 'light') ? 'light' : 'dark';
  document.documentElement.setAttribute('data-theme', t);
  try { localStorage.setItem('theme', t); } catch(_) {}
  const rd = qs('#theme-dark');
  const rl = qs('#theme-light');
  if(rd && rl){ rd.checked = (t==='dark'); rl.checked = (t==='light'); }
}

export function initTheme(){
  let saved = null;
  try { saved = localStorage.getItem('theme'); } catch(_) {}
  applyTheme(saved || 'dark');
  const rd = qs('#theme-dark');
  const rl = qs('#theme-light');
  on(rd, 'change', () => rd.checked && applyTheme('dark'));
  on(rl, 'change', () => rl.checked && applyTheme('light'));
}
