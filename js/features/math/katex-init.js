/* js/features/math/katex-init.js
   Initialise KaTeX (offline) et expose window.kivrioRenderMath()
*/
const KATEX_BASE = "assets/vendor/katex";
const KIVRIO_MATH_SOURCE_PROP = "__kivrioMathSourceSignature";
const KIVRIO_MATH_RENDERED_PROP = "__kivrioMathRenderedSignature";

/* Fallback: injecter la feuille CSS si elle n'est pas chargée */
function ensureKatexCss() {
  const hasCss = [...document.styleSheets].some(s => s.href && s.href.includes("/katex.min.css"));
  if (!hasCss) {
    const link = document.createElement("link");
    link.rel = "stylesheet";
    link.href = `${KATEX_BASE}/katex.min.css`; // chemin relatif depuis index.html
    document.head.appendChild(link);
  }
}

/* Charger un script JS une seule fois */
function loadScriptOnce(src) {
  return new Promise((resolve, reject) => {
    // évite les doublons même si src est re-demandé
    const already = [...document.scripts].some(s => s.src && s.src.endsWith(src));
    if (already || document.querySelector(`script[src="${src}"]`)) return resolve();
    const s = document.createElement("script");
    s.src = src;
    s.async = true;
    s.onload = () => resolve();
    s.onerror = () => reject(new Error(`Échec chargement: ${src}`));
    document.head.appendChild(s);
  });
}

/* Initialise KaTeX et définit la fonction de rendu */
export async function initKatex() {
  try {
    ensureKatexCss();

    await loadScriptOnce(`${KATEX_BASE}/katex.min.js`);
    await loadScriptOnce(`${KATEX_BASE}/contrib/auto-render.min.js`);

    // Expose la fonction de rendu
    window.kivrioRenderMath = (root = document) => {
      if (!window.renderMathInElement) return;
      const targets = [];
      if (root instanceof HTMLElement && root.classList.contains("markdown-body")) {
        targets.push(root);
      } else if (root?.querySelectorAll) {
        targets.push(...root.querySelectorAll(".markdown-body"));
      }

      if (!targets.length) targets.push(root);

      for (const target of targets) {
        if (!(target instanceof HTMLElement)) continue;
        const sourceSignature = typeof target[KIVRIO_MATH_SOURCE_PROP] === "string"
          ? target[KIVRIO_MATH_SOURCE_PROP]
          : "";
        if (sourceSignature && target[KIVRIO_MATH_RENDERED_PROP] === sourceSignature) {
          continue;
        }

        window.renderMathInElement(target, {
          delimiters: [
            { left: "$$",  right: "$$",  display: true  },
            { left: "\\[", right: "\\]", display: true  },
            { left: "\\(", right: "\\)", display: false },
            { left: "$",   right: "$",   display: false }
          ],
          throwOnError: false
        });

        if (sourceSignature) {
          target[KIVRIO_MATH_RENDERED_PROP] = sourceSignature;
        }
      }
    };

    // ---- Auto-render sur nouveaux messages / mises à jour (streaming) ----
    (function enableAutoRender(){
      if (!window.MutationObserver) return;
      if (window.__kivrioKatexMO) return; // évite doublons si init appelée plusieurs fois

      const root = document.body; // on observe tout le document
      const isBubble = (el) => el && el.nodeType === 1 && el.classList?.contains('bubble');

      const scheduleRender = (el) => {
        if (!el) return;
        requestAnimationFrame(() => {
          if (window.kivrioRenderMath) window.kivrioRenderMath(el);
        });
      };

      const mo = new MutationObserver((muts) => {
        for (const m of muts) {
          if (m.type === 'childList') {
            m.addedNodes.forEach((n) => {
              if (isBubble(n)) scheduleRender(n);
              n.querySelectorAll?.('.bubble').forEach(scheduleRender);
            });
          }
          if (m.type === 'characterData') {
            const el = m.target?.parentElement?.closest?.('.bubble') || m.target?.parentElement;
            if (isBubble(el)) scheduleRender(el);
          }
        }
      });

      mo.observe(root, { childList: true, subtree: true, characterData: true });
      window.__kivrioKatexMO = mo;
    })();

    // Premier passage (si du contenu existe déjà)
    window.kivrioRenderMath(document);

  } catch (err) {
    console.error("Erreur init KaTeX:", err);
  }
}
