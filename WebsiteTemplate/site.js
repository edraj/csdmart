/* dmart website shell. Plain ES2019, no modules, no dependencies.
 *
 * Loaded synchronously in <head> so the theme class is on <html> before the
 * first paint. Everything that touches the body waits for DOMContentLoaded;
 * mermaid (when the page has diagrams) is a deferred script, so it has run by
 * then. The page is served with a script-src that forbids inline script, so
 * every behaviour lives here and is wired with addEventListener. */
(function () {
  'use strict';

  var root = document.documentElement;
  var THEME_KEY = 'theme';
  var DARK_QUERY = '(prefers-color-scheme: dark)';

  function storedTheme() {
    try {
      var value = window.localStorage.getItem(THEME_KEY);
      return value === 'dark' || value === 'light' ? value : null;
    } catch (e) {
      return null;
    }
  }

  function systemPrefersDark() {
    try {
      return !!(window.matchMedia && window.matchMedia(DARK_QUERY).matches);
    } catch (e) {
      return false;
    }
  }

  function isDark() {
    return root.classList.contains('dark');
  }

  // `dark` is the contract. `light` is set alongside it so the CSS fallback
  // (prefers-color-scheme with no class, i.e. scripts disabled) can be
  // switched off once a choice has been made here.
  function applyTheme(dark) {
    root.classList.toggle('dark', dark);
    root.classList.toggle('light', !dark);
  }

  // (a) Before first paint.
  try {
    var initial = storedTheme();
    applyTheme(initial ? initial === 'dark' : systemPrefersDark());
  } catch (e) {
    /* leave the CSS media-query fallback in charge */
  }
  root.classList.add('js');

  /* ── Mermaid ─────────────────────────────────────────────────────────── */

  var diagrams = [];
  var diagramQueue = null;

  // A diagram that cannot be drawn (no mermaid, or a syntax error) shows its
  // source instead of staying blank; see `pre.mermaid` in site.css.
  function markDiagramsAsSource() {
    for (var i = 0; i < diagrams.length; i++) {
      if (!diagrams[i].querySelector('svg')) diagrams[i].classList.add('mermaid-source');
    }
  }

  function setPending(pending) {
    for (var i = 0; i < diagrams.length; i++) {
      diagrams[i].classList.toggle('mermaid-pending', pending);
    }
  }

  function renderDiagramsNow() {
    var mermaid = window.mermaid;
    if (!mermaid || typeof mermaid.run !== 'function') {
      markDiagramsAsSource();
      return null;
    }
    for (var i = 0; i < diagrams.length; i++) {
      var el = diagrams[i];
      el.removeAttribute('data-processed');
      el.classList.remove('mermaid-source');
      el.textContent = el.getAttribute('data-original');
    }
    setPending(true);
    function done() {
      setPending(false);
    }
    function failed() {
      setPending(false);
      markDiagramsAsSource();
    }
    try {
      mermaid.initialize({
        startOnLoad: false,
        theme: isDark() ? 'dark' : 'neutral',
        // Labels in the page's own font, not mermaid's trebuchet/verdana stack.
        fontFamily: diagramFont(),
        // HTML labels only switch to wrapping when their measured width
        // EQUALS the wrapping width, which fractional device-pixel ratios
        // never produce, so long labels were clipped instead (dmart.cc showed
        // "Space: managen"). SVG labels are wrapped by mermaid itself, at word
        // boundaries; the wider limit stops it breaking mid-word.
        htmlLabels: false,
        flowchart: { htmlLabels: false, wrappingWidth: 400 }
      });
      var result = mermaid.run({ nodes: diagrams });
      if (result && typeof result.then === 'function') {
        return result.then(done, failed);
      }
      done();
      return null;
    } catch (e) {
      failed();
      return null;
    }
  }

  // Renders are chained so a quick double toggle cannot interleave two runs
  // over the same nodes.
  function renderDiagrams() {
    if (!diagrams.length) return;
    if (typeof Promise === 'undefined') {
      renderDiagramsNow();
      return;
    }
    diagramQueue = (diagramQueue || Promise.resolve()).then(renderDiagramsNow, renderDiagramsNow);
  }

  function diagramFont() {
    try {
      return window.getComputedStyle(document.body).fontFamily || 'sans-serif';
    } catch (e) {
      return 'sans-serif';
    }
  }

  function initDiagrams() {
    var nodes = document.querySelectorAll('pre.mermaid');
    for (var i = 0; i < nodes.length; i++) {
      nodes[i].setAttribute('data-original', nodes[i].textContent);
      diagrams.push(nodes[i]);
    }
    if (!diagrams.length) return;

    // Measure with the web font, not its fallback: wait (briefly) for it.
    var fonts = document.fonts;
    if (!fonts || typeof fonts.load !== 'function' || typeof Promise === 'undefined') {
      renderDiagrams();
      return;
    }
    var started = false;
    var start = function () {
      if (started) return;
      started = true;
      renderDiagrams();
    };
    window.setTimeout(start, 1500);
    Promise.all([fonts.load('400 16px "IBM Plex Sans"'), fonts.load('600 16px "IBM Plex Sans"')]).then(start, start);
  }

  /* ── Theme toggle ────────────────────────────────────────────────────── */

  function syncToggles() {
    var dark = isDark();
    var toggles = document.querySelectorAll('.theme-toggle');
    for (var i = 0; i < toggles.length; i++) {
      toggles[i].setAttribute('aria-pressed', dark ? 'true' : 'false');
      toggles[i].setAttribute('title', dark ? 'Switch to light mode' : 'Switch to dark mode');
    }
  }

  function setTheme(dark, persist) {
    if (dark === isDark()) return;
    applyTheme(dark);
    if (persist) {
      try {
        window.localStorage.setItem(THEME_KEY, dark ? 'dark' : 'light');
      } catch (e) {
        /* private mode or storage disabled: the choice lasts for this page */
      }
    }
    syncToggles();
    renderDiagrams();
    try {
      window.dispatchEvent(new CustomEvent('themechange', { detail: { dark: dark } }));
    } catch (e) {
      /* very old browsers */
    }
  }

  function initThemeToggle() {
    var toggles = document.querySelectorAll('.theme-toggle');
    for (var i = 0; i < toggles.length; i++) {
      toggles[i].addEventListener('click', function () {
        setTheme(!isDark(), true);
      });
    }
    syncToggles();

    // Follow the OS setting live until the visitor makes a choice.
    try {
      var mq = window.matchMedia && window.matchMedia(DARK_QUERY);
      if (mq) {
        var onChange = function (e) {
          if (!storedTheme()) setTheme(e.matches, false);
        };
        if (mq.addEventListener) mq.addEventListener('change', onChange);
        else if (mq.addListener) mq.addListener(onChange);
      }
    } catch (e) {
      /* ignore */
    }
  }

  /* ── Docs drawer ─────────────────────────────────────────────────────── */

  function initDrawer() {
    var toggle = document.querySelector('.nav-toggle');
    if (!toggle) return;
    var sidebar = document.getElementById(toggle.getAttribute('aria-controls') || 'sidebar');
    if (!sidebar) {
      toggle.hidden = true;
      return;
    }
    var scrim = document.querySelector('.nav-scrim');
    var narrow = window.matchMedia ? window.matchMedia('(max-width: 900px)') : null;

    function isOpen() {
      return toggle.getAttribute('aria-expanded') === 'true';
    }

    function open() {
      toggle.setAttribute('aria-expanded', 'true');
      sidebar.classList.add('open');
      document.body.classList.add('nav-open');
      if (scrim) scrim.hidden = false;
      var target = sidebar.querySelector('a[aria-current="page"]') || sidebar.querySelector('a');
      if (target) {
        // After the drawer becomes visible, or focus() is a no-op.
        window.setTimeout(function () {
          target.focus();
        }, 30);
      }
    }

    function close(restoreFocus) {
      if (!isOpen()) return;
      var hadFocus = sidebar.contains(document.activeElement);
      toggle.setAttribute('aria-expanded', 'false');
      sidebar.classList.remove('open');
      document.body.classList.remove('nav-open');
      if (scrim) scrim.hidden = true;
      if (restoreFocus || hadFocus) toggle.focus();
    }

    toggle.addEventListener('click', function () {
      if (isOpen()) close(true);
      else open();
    });

    if (scrim) {
      scrim.addEventListener('click', function () {
        close(true);
      });
    }

    sidebar.addEventListener('click', function (e) {
      var node = e.target;
      while (node && node !== sidebar) {
        if (node.tagName === 'A') {
          close(false);
          return;
        }
        node = node.parentNode;
      }
    });

    document.addEventListener('keydown', function (e) {
      if ((e.key === 'Escape' || e.key === 'Esc') && isOpen()) {
        close(true);
      }
    });

    // Widening the window past the breakpoint turns the drawer back into a
    // column; do not leave a scrim or a stale aria-expanded behind.
    if (narrow) {
      var onWidth = function (e) {
        if (!e.matches) close(false);
      };
      if (narrow.addEventListener) narrow.addEventListener('change', onWidth);
      else if (narrow.addListener) narrow.addListener(onWidth);
    }
  }

  /* ── Copy buttons ────────────────────────────────────────────────────── */

  // The async clipboard API first; if it refuses, or never answers (seen in
  // embedded and automated browsers), fall back to execCommand.
  function copyText(text) {
    if (navigator.clipboard && window.isSecureContext) {
      return new Promise(function (resolve, reject) {
        var settled = false;
        var fallback = function () {
          if (settled) return;
          settled = true;
          window.clearTimeout(timer);
          execCopy(text).then(resolve, reject);
        };
        var timer = window.setTimeout(fallback, 1000);
        navigator.clipboard.writeText(text).then(function () {
          if (settled) return;
          settled = true;
          window.clearTimeout(timer);
          resolve();
        }, fallback);
      });
    }
    return execCopy(text);
  }

  function execCopy(text) {
    return new Promise(function (resolve, reject) {
      var active = document.activeElement;
      var area = document.createElement('textarea');
      area.value = text;
      area.setAttribute('readonly', '');
      area.className = 'copy-buffer';
      document.body.appendChild(area);
      area.select();
      var ok = false;
      try {
        ok = document.execCommand('copy');
      } catch (e) {
        ok = false;
      }
      document.body.removeChild(area);
      if (active && typeof active.focus === 'function') active.focus();
      if (ok) resolve();
      else reject(new Error('copy failed'));
    });
  }

  function initCopyButtons() {
    var blocks = document.querySelectorAll('.prose pre > code');
    for (var i = 0; i < blocks.length; i++) {
      (function (code) {
        var pre = code.parentNode;
        if (!pre || pre.classList.contains('mermaid')) return;
        if (pre.parentNode && pre.parentNode.classList && pre.parentNode.classList.contains('code-block')) return;

        var wrap = document.createElement('div');
        wrap.className = 'code-block';
        pre.parentNode.insertBefore(wrap, pre);
        wrap.appendChild(pre);

        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'copy-button';
        button.textContent = 'Copy';
        button.setAttribute('aria-label', 'Copy code to clipboard');
        wrap.appendChild(button);

        var timer = 0;
        function flash(label) {
          button.textContent = label;
          window.clearTimeout(timer);
          timer = window.setTimeout(function () {
            button.textContent = 'Copy';
            button.classList.remove('copied');
          }, 1600);
        }

        button.addEventListener('click', function () {
          copyText(code.textContent.replace(/\n$/, '')).then(
            function () {
              button.classList.add('copied');
              flash('Copied');
            },
            function () {
              // Clipboard refused: select the code so a manual copy works.
              try {
                var range = document.createRange();
                range.selectNodeContents(code);
                var selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
              } catch (e) {
                /* nothing more to do */
              }
              flash('Press Ctrl+C');
            }
          );
        });
      })(blocks[i]);
    }
  }

  function onReady() {
    initThemeToggle();
    initDrawer();
    initCopyButtons();
    initDiagrams();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', onReady);
  } else {
    onReady();
  }
})();
