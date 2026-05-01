// js/core/title.js
export function makeTitleFromText(txt, max = 60){
  const clean = (txt || '').replace(/\s+/g, ' ').trim().replace(/^([#>*\-]+\s*)+/, '');
  const first = clean.split(/(?<=[.!?])\s+/)[0] || clean;
  return first.length > max ? first.slice(0, max - 1) + '…' : first;
}
