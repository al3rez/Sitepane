namespace Sitepane;

/// <summary>
/// Injected into every top-level document. Adds, inside a closed shadow root:
/// - the Alt drag strip (app-region: drag) left of the window-controls overlay;
/// - an Alt "acrylic" wash (blur + page-color tint) under the overlay buttons only, so they stay
///   legible over the page's own top-right controls;
/// - invisible edge/corner handles that start a native resize. The WebView2 renderer window
///   covers the whole client area, so the host never receives hit-tests at the edges.
/// Host → page: window.__pbSet({ chrome, resizable, controlsWidth, controlsHeight }). The controls size
/// comes from the host: WebView2 does not populate env(titlebar-area-*) / navigator.windowControlsOverlay.
/// Page → host: chrome.webview.postMessage({ pb: 'resize', edge } | { pb: 'tint', r, g, b }).
/// </summary>
internal static class PageScript
{
    public const string Bootstrap = """
        (() => {
          if (window.top !== window || window.__pbSet) return;
          const state = { chrome: false, resizable: false, controlsWidth: 0, controlsHeight: 0 };
          const FEATHER = 24;     // left fade-in of the wash
          const WASH_EXTRA = 24;  // wash extends below the buttons to cover the page's own header controls
          const EDGE = 5, CORNER = 12;
          const edges = {
            n:  `top:0;left:${CORNER}px;right:${CORNER}px;height:${EDGE}px;cursor:ns-resize`,
            s:  `bottom:0;left:${CORNER}px;right:${CORNER}px;height:${EDGE}px;cursor:ns-resize`,
            w:  `left:0;top:${CORNER}px;bottom:${CORNER}px;width:${EDGE}px;cursor:ew-resize`,
            e:  `right:0;top:${CORNER}px;bottom:${CORNER}px;width:${EDGE}px;cursor:ew-resize`,
            nw: `top:0;left:0;width:${CORNER}px;height:${CORNER}px;cursor:nwse-resize`,
            ne: `top:0;right:0;width:${CORNER}px;height:${CORNER}px;cursor:nesw-resize`,
            sw: `bottom:0;left:0;width:${CORNER}px;height:${CORNER}px;cursor:nesw-resize`,
            se: `bottom:0;right:0;width:${CORNER}px;height:${CORNER}px;cursor:nwse-resize`,
          };

          // Page color: <meta name="theme-color"> (media-aware), else the body/html background.
          const probe = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
          const toRgb = (value) => {
            if (!value) return null;
            probe.clearRect(0, 0, 1, 1);
            probe.fillStyle = '#00000000';
            probe.fillStyle = value; // invalid values are ignored and stay transparent
            probe.fillRect(0, 0, 1, 1);
            const [r, g, b, a] = probe.getImageData(0, 0, 1, 1).data;
            return a > 0 ? [r, g, b] : null;
          };
          const pageColor = () => {
            for (const meta of document.querySelectorAll('meta[name="theme-color"]')) {
              const media = meta.getAttribute('media');
              const rgb = (!media || matchMedia(media).matches) && toRgb(meta.content);
              if (rgb) return rgb;
            }
            for (const el of [document.body, document.documentElement]) {
              const rgb = el && toRgb(getComputedStyle(el).backgroundColor);
              if (rgb) return rgb;
            }
            return [0, 0, 0];
          };
          let tint = null;
          const updateTint = () => {
            const [r, g, b] = pageColor();
            host.style.setProperty('--pb-tint', `${r} ${g} ${b}`);
            if (tint === `${r},${g},${b}`) return;
            tint = `${r},${g},${b}`;
            chrome.webview.postMessage({ pb: 'tint', r, g, b }); // host derives button glyph color from it
          };

          let host = null;
          const build = () => {
            host = document.createElement('sitepane-chrome');
            const root = host.attachShadow({ mode: 'closed' });
            root.innerHTML = `<style>
                :host { all: initial; }
                .drag, .edge, .wash { position: fixed; display: none; }
                .drag {
                  z-index: 2147483646;
                  top: 0;
                  left: 0;
                  right: var(--pb-cw);
                  height: var(--pb-ch);
                  -webkit-app-region: drag;
                  app-region: drag;
                }
                .wash {
                  display: block;
                  z-index: 2147483645;
                  pointer-events: none;
                  top: 0;
                  right: 0;
                  width: calc(var(--pb-cw) + ${FEATHER}px);
                  height: calc(var(--pb-ch) + ${WASH_EXTRA}px);
                  background: rgb(var(--pb-tint, 0 0 0) / .6);
                  backdrop-filter: blur(16px) saturate(1.4);
                  mask-image: linear-gradient(to right, transparent, #000 ${FEATHER}px), linear-gradient(to bottom, #000 60%, transparent);
                  mask-composite: intersect;
                  opacity: 0;
                  visibility: hidden;
                  transition: opacity 80ms linear, visibility 0s linear 80ms;
                }
                :host(:not([controls])) .wash { display: none; }
                :host([chrome]) .wash { opacity: 1; visibility: visible; transition: opacity 80ms linear; }
                .edge { z-index: 2147483647; }
                :host([chrome]) .drag, :host([resizable]) .edge { display: block; }
              </style><div class="wash"></div><div class="drag"></div>` +
              Object.entries(edges)
                .map(([edge, css]) => `<div class="edge" data-edge="${edge}" style="${css}"></div>`)
                .join('');
            root.addEventListener('pointerdown', (e) => {
              const edge = e.target.dataset && e.target.dataset.edge;
              if (!edge || e.button !== 0) return;
              e.preventDefault();
              e.stopPropagation();
              chrome.webview.postMessage({ pb: 'resize', edge });
            });
          };

          const apply = () => {
            if (!host) return;
            if (state.chrome && !host.hasAttribute('chrome')) updateTint();
            host.style.setProperty('--pb-cw', `${state.controlsWidth}px`);
            host.style.setProperty('--pb-ch', `${state.controlsHeight}px`);
            host.toggleAttribute('controls', state.controlsWidth > 0);
            host.toggleAttribute('chrome', state.chrome);
            host.toggleAttribute('resizable', state.resizable);
          };
          const ensure = () => {
            const html = document.documentElement;
            if (!html) return;
            if (!host) build();
            if (host.parentNode !== html) html.appendChild(host);
            apply();
          };
          window.__pbSet = (next) => { Object.assign(state, next); apply(); };

          // Re-attach if the page replaces <html> children (e.g. hydration). No subtree observing.
          const watchHtml = new MutationObserver(ensure);
          const watchDoc = new MutationObserver(() => {
            ensure();
            if (document.documentElement) watchHtml.observe(document.documentElement, { childList: true });
          });
          watchDoc.observe(document, { childList: true });
          if (document.documentElement) watchHtml.observe(document.documentElement, { childList: true });
          ensure();
        })();
        """;
}
