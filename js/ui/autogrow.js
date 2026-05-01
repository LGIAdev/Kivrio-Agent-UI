// js/ui/autogrow.js
(() => {
  if (window.__kivrioAutogrow) return;
  window.__kivrioAutogrow = true;

  const MAX = 240;                 // doit matcher le max-height CSS
  const SEL = '#composer-input';

  function mount() {
    const el = document.querySelector(SEL);
    if (!el) return;

    const grow = () => {
      el.style.height = 'auto';
      const h = Math.min(el.scrollHeight, MAX);
      el.style.height = h + 'px';
      el.style.overflowY = (el.scrollHeight > MAX) ? 'auto' : 'hidden';
    };

    // première mesure + écoute
    grow();
    el.addEventListener('input', grow);
    el.addEventListener('change', grow);
  }

  if (document.readyState === 'loading') {
    window.addEventListener('DOMContentLoaded', mount, { once: true });
  } else {
    mount();
  }
})();
