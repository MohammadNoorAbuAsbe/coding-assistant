'use strict';

// ── Bridge ──────────────────────────────────────────────────────────
const bridge = (() => {
  const core = window.chrome && window.chrome.webview;
  return {
    available: !!core,
    post(cmd, payload) {
      const msg = Object.assign({ cmd }, payload);
      if (core) core.postMessage(msg);
      else console.log('[ui]', JSON.stringify(msg));
    },
    onMessage(fn) {
      if (core) core.addEventListener('message', e => fn(e.data));
    }
  };
})();

window.addEventListener('error', e => {
  try {
    bridge.post('ui:log', {
      text: (e.message || 'script error') + (e.filename ? ' @ ' + String(e.filename).split(/[\\/]/).pop() + ':' + e.lineno : '')
    });
  } catch { }
});
window.addEventListener('unhandledrejection', e => {
  try {
    bridge.post('ui:log', { text: 'unhandled rejection: ' + (e.reason && e.reason.message ? e.reason.message : String(e.reason)) });
  } catch { }
});

// ── State ───────────────────────────────────────────────────────────
const state = {
  messages: [],
  activeTurn: null,
  toolCards: new Map(),
  toolCount: 0,
  bus: null,
  changes: [],
  todos: [],
  autopilot: null,
  settings: null,
  meta: null,
  tree: null,
  treeOpen: new Set(),
  viewerPath: null,
  paused: false,
  panel: 'files'
};

const $ = id => document.getElementById(id);

const els = {
  messages: $('messages'),
  input: $('input'),
  composer: $('composer'),
  composerHint: $('composer-hint'),
  slashMenu: $('slash-menu'),
  btnSend: $('btn-send'),
  btnSlash: $('btn-slash'),
  btnModel: $('btn-model'),
  modelChip: $('model-chip-name'),
  sidebar: $('sidebar'),
  resizer: $('sidebar-resizer'),
  btnCollapse: $('btn-collapse-side'),
  fileTree: $('file-tree'),
  fileFilter: $('file-filter'),
  btnRefreshFiles: $('btn-refresh-files'),
  changesList: $('changes-list'),
  changesBadge: $('changes-badge'),
  todosList: $('todos-list'),
  todosProgress: $('todos-progress'),
  todosBarFill: $('todos-bar-fill'),
  todosCount: $('todos-count'),
  workspaceName: $('workspace-name'),
  workspaceLabel: $('workspace-label'),
  btnFolder: $('btn-folder'),
  statusbar: $('statusbar'),
  sbProvider: $('sb-provider'),
  sbModel: $('sb-model'),
  sbContext: $('sb-context'),
  sbStatus: $('sb-status'),
  sbTokens: $('sb-tokens'),
  sbAutopilot: $('sb-autopilot'),
  jumpBottom: $('jump-bottom'),
  viewer: $('viewer'),
  viewerPath: $('viewer-path'),
  viewerLang: $('viewer-lang'),
  viewerContent: $('viewer-content'),
  btnViewerCopy: $('btn-viewer-copy'),
  btnViewerExternal: $('btn-viewer-external'),
  btnViewerClose: $('btn-viewer-close'),
  paletteOverlay: $('palette-overlay'),
  paletteInput: $('palette-input'),
  paletteResults: $('palette-results'),
  historyOverlay: $('history-overlay'),
  historyList: $('history-list'),
  btnHistoryClose: $('btn-history-close'),
  settingsOverlay: $('settings-overlay'),
  settingsProviders: $('settings-providers'),
  settingsModels: $('settings-models'),
  settingsModelsSection: $('settings-models-section'),
  settingsWorkspace: $('settings-workspace'),
  btnSettingsFolder: $('btn-settings-folder'),
  questionOverlay: $('question-overlay'),
  questionText: $('question-text'),
  questionOptions: $('question-options'),
  questionCustom: $('question-custom'),
  questionInput: $('question-input'),
  questionSubmit: $('question-submit'),
  toasts: $('toasts'),
  btnTheme: $('btn-theme'),
  btnSettings: $('btn-settings'),
  app: $('app')
};

// ── Icons ───────────────────────────────────────────────────────────
const ICON_PATHS = {
  send: 'M3 11l18-8-8 18-2.5-7.5L3 11z',
  stop: 'M6 6h12v12H6z',
  spark: 'M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8L12 3zm7 11l.9 2.6L22.5 17l-2.6.9L19 20.5l-.9-2.6-2.6-.9 2.6-.9L19 14z',
  doc: 'M6 3h8l4 4v14H6V3zm8 0v4h4',
  search: 'M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zm10 16l-5.5-5.5',
  edit: 'M4 20h4L18 10l-4-4L4 16v4zM13.5 6.5l4 4',
  patch: 'M14 3v5h5M14 3l6 6-7 7H7v-6l7-7zM4 21h16',
  wrench: 'M14.7 6.3a4 4 0 0 0-5.4 5.4L3 18v3h3l6.3-6.3a4 4 0 0 0 5.4-5.4l-2.8 2.8-2.8-2.8 2.8-2.8z',
  code: 'M8 6l-6 6 6 6M16 6l6 6-6 6M13 4l-3 16',
  file: 'M6 3h8l4 4v14H6V3zm8 0v4h4',
  folder: 'M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z',
  folderPlus: 'M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7zm9 3v4m-2-2h4',
  glob: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zm-9 9h18M12 3c-2.5 2.7-3.5 5.8-3.5 9s1 6.3 3.5 9c2.5-2.7 3.5-5.8 3.5-9s-1-6.3-3.5-9z',
  read: 'M4 5h16v14H4V5zm0 4h16M8 5v14',
  list: 'M4 6h16M4 12h16M4 18h10',
  brain: 'M12 3a3 3 0 0 1 3 3v1.5a3 3 0 0 1 1.5 2.6l1.4 1.4a3 3 0 0 1 0 4.2l-2.2 2.2a3 3 0 0 1-2.1.9h-1.2a3 3 0 0 1-3-3v-1.5A3 3 0 0 1 8 12.5L6.6 11a3 3 0 0 1 0-4.2l2-2A3 3 0 0 1 12 3zm0 0c-1.5 1.5-2 3-2 4.5v5c0 1.5.5 3 2 4.5M12 3c1.5 1.5 2 3 2 4.5v5c0 1.5-.5 3-2 4.5',
  question: 'M12 8a3 3 0 1 0 0 6 3 3 0 0 0 0-6zm0-4a7 7 0 1 0 0 14 7 7 0 0 0 0-14z',
  check: 'M4 12l5 5L20 6',
  chevron: 'M6 9l6 6 6-6',
  external: 'M14 4h6v6M20 4l-9 9M20 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h5',
  copy: 'M8 8h12v12H8zM4 16V4h12',
  x: 'M6 6l12 12M18 6L6 18',
  refresh: 'M4 12a8 8 0 0 1 14-5m2 5a8 8 0 0 1-14 5m0-5H4l-2 0M20 12h-2',
  moon: 'M12 3a9 9 0 1 0 9 9c0-.5-.4-1-1-.7a5 5 0 0 1-6-6c.3-.6-.2-1-.8-1a9 9 0 0 0-1.2-1.3z',
  sun: 'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8zm0-5v2m0 14v2M5.6 5.6l1.4 1.4m9.9 9.9l1.4 1.4M3 12h2m14 0h2M5.6 18.4l1.4-1.4M16.9 7.1l1.4-1.4',
  gear: 'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8zm8.5 4a6.5 6.5 0 0 0-.1-1l2-1.5-2-3.5-2.4 1a6.6 6.6 0 0 0-1.7-1L15.8 3h-4l-.5 2.5a6.6 6.6 0 0 0-1.7 1l-2.4-1-2 3.5 2 1.5a6.5 6.5 0 0 0 0 2l-2 1.5 2 3.5 2.4-1a6.6 6.6 0 0 0 1.7 1l.5 2.5h4l.5-2.5a6.6 6.6 0 0 0 1.7-1l2.4 1 2-3.5-2-1.5a6.5 6.5 0 0 0 .1-1z',
  undo: 'M3 8v6h6M3 14c2.5-2.5 5-3.5 8-3.5 4.5 0 7.5 2.5 10 6',
  dot: 'M12 10a2 2 0 1 0 0 4 2 2 0 0 0 0-4z',
  caret: 'M6 9l6 6 6-6',
  circle: 'M12 4a8 8 0 1 0 0 16 8 8 0 0 0 0-16z',
  book: 'M4 5a2 2 0 0 1 2-2h14v16H6a2 2 0 0 0-2 2V5zm0 16v-2',
  trash: 'M5 7h14M9 7V4h6v3m-8 0v13h10V7M10 11v5m4-5v5',
  clock: 'M12 4a8 8 0 1 0 0 16 8 8 0 0 0 0-16zm0 4v5l3.5 2',
  layers: 'M12 3l9 5-9 5-9-5 9-5zm-9 10l9 5 9-5M3 17l9 5 9-5'
};

function I(name, cls) {
  return '<svg class="ic ' + (cls || '') + '" viewBox="0 0 24 24"><path d="' +
    ICON_PATHS[name] + '"/></svg>';
}

const TOOL_ICONS = {
  Read: 'read',
  Glob: 'search',
  Grep: 'search',
  Edit: 'edit',
  Write: 'doc',
  ApplyPatch: 'patch',
  Bash: 'code',
  Powershell: 'code',
  BashPowershell: 'code',
  Question: 'question',
  AskUser: 'question',
  TodoWrite: 'list',
  WebFetch: 'glob',
  WebSearch: 'glob',
  Task: 'brain',
  UndoJournal: 'undo'
};

const TOOL_CSS = {
  Read: 'read', Glob: 'search', Grep: 'search', Edit: 'edit', Write: 'write',
  ApplyPatch: 'patch', Bash: 'code', Powershell: 'code', BashPowershell: 'code',
  Question: 'question', AskUser: 'question', TodoWrite: 'list',
  WebFetch: 'globe', WebSearch: 'globe', Task: 'brain', UndoJournal: 'undo'
};

const TOOL_FALLBACK_ICON = 'wrench';

// ── Helpers ─────────────────────────────────────────────────────────
function escapeHtml(s) {
  return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

function shortPath(p, n) {
  const parts = String(p).split(/[\\/]/);
  const tail = parts.slice(-(n || 2));
  return tail.join('/');
}

function fileIconClass(name) {
  if (/\.(cs|ts|js|jsx|tsx|py|go|rs|java|c|cpp|h|hpp|fs|kt|swift|sh|ps1|sql|html|htm|css|scss|json|xml|yaml|yml|toml|ini|cfg|md|txt)$/i.test(name)) return 'code-ic';
  if (/\.(png|jpg|jpeg|gif|svg|ico|webp|bmp|mp4|mp3|wav|pdf|zip|7z|tar|gz|exe|dll|ico)$/i.test(name)) return 'media-ic';
  return 'gen-ic';
}

function iconForFile(name) {
  const ext = name.split('.').pop().toLowerCase();
  if (ext === 'dir') return I('folder', 'f-ic dir-ic');
  return I('file', 'f-ic ' + fileIconClass(name));
}

function fmtBytes(n) {
  if (n == null) return '';
  if (n < 1024) return n + ' B';
  if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
  return (n / 1048576).toFixed(1) + ' MB';
}

function fmtTime(ts) {
  if (!ts) return '';
  const d = new Date(ts);
  const h = d.getHours(), m = d.getMinutes();
  return (h < 10 ? '0' : '') + h + ':' + (m < 10 ? '0' : '') + m;
}

function debounce(fn, ms) {
  let t;
  return function () {
    const args = arguments;
    clearTimeout(t);
    t = setTimeout(() => fn.apply(this, args), ms);
  };
}

function toast(msg, kind) {
  const el = document.createElement('div');
  el.className = 'toast' + (kind ? ' ' + kind : '');
  el.textContent = msg;
  els.toasts.appendChild(el);
  setTimeout(() => {
    el.classList.add('out');
    setTimeout(() => el.remove(), 250);
  }, 2600);
}

// ── Markdown ────────────────────────────────────────────────────────
const md = marked.parse;
marked.setOptions({
  gfm: true,
  breaks: false,
  highlight(code, lang) {
    try {
      if (lang && hljs.getLanguage(lang)) return hljs.highlight(code, { language: lang }).value;
      return hljs.highlightAuto(code).value;
    } catch (e) {
      return code;
    }
  }
});

const mdRenderer = new marked.Renderer();

mdRenderer.link = function (href, title, text) {
  let url = href;
  try { url = new URL(href).href; } catch (e) { /* relative */ }
  return '<a href="' + url + '" target="_blank" rel="noopener" data-link="1" title="' +
    escapeHtml(title || '') + '">' + text + '</a>';
};

mdRenderer.image = function (href, title, text) {
  return '<span class="md-img-err">' + (text || 'image') + '</span>';
};

mdRenderer.html = function () {
  return '';
};

marked.use({ renderer: mdRenderer });

function renderMarkdown(src) {
  return md(src);
}

function mdToHtml(text) {
  return renderMarkdown(text);
}

function isDiffContent(content) {
  return /^diff --git|\n@@ /.test(content) || /\n@@ /.test(content) || /^@@ /.test(content);
}

function escapeRawHtml(src) {
  return String(src).replace(/[<>&]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;' }[c]));
}

// ── Message rendering ───────────────────────────────────────────────
function scrollBottom(force) {
  const m = els.messages;
  if (force || m.scrollHeight - m.scrollTop - m.clientHeight < 120) {
    m.scrollTop = m.scrollHeight;
  }
}

function ensureHero() {
  if (!els.messages.querySelector('.hero')) {
    const hero = document.createElement('div');
    hero.className = 'hero';
    hero.innerHTML =
      '<div class="hero-logo">' + I('spark') + '</div>' +
      '<h1>Ask anything about this project</h1>' +
      '<p>I can read and edit your code, run commands, search the web, and manage your tasks. Start typing below or pick a suggestion.</p>' +
      '<div class="hero-chips">' +
      '<button class="chip-suggest" data-s="What does this project do?">What does this project do?</button>' +
      '<button class="chip-suggest" data-s="Explain the architecture">Explain the architecture</button>' +
      '<button class="chip-suggest" data-s="Run the tests">Run the tests</button>' +
      '</div>';
    els.messages.appendChild(hero);
  }
}

function userMessage(parts) {
  const hero = els.messages.querySelector('.hero');
  if (hero) hero.remove();
  const row = document.createElement('div');
  row.className = 'msg';
  row.innerHTML =
    '<div class="avatar user">' + I('doc') + '</div>' +
    '<div class="msg-body user-msg"><div class="user-bubble"></div></div>';
  const bubble = row.querySelector('.user-bubble');
  const text = parts.map(p => p.text).join('\n');
  bubble.textContent = text;
  els.messages.appendChild(row);
  scrollBottom(true);
}

function newTurn() {
  const turn = {
    el: null,
    row: null,
    body: null,
    content: null,
    reasoning: null,
    reasoningOpen: false,
    tools: new Map(),
    meta: null,
    lastIter: null,
    error: null
  };

  const row = document.createElement('div');
  row.className = 'msg assistant';
  row.innerHTML =
    '<div class="avatar assistant">' + I('spark') + '</div>' +
    '<div class="msg-body"></div>';
  const body = row.querySelector('.msg-body');

  const content = document.createElement('div');
  content.className = 'content';
  body.appendChild(content);

  turn.el = row;
  turn.row = row;
  turn.body = body;
  turn.content = content;
  els.messages.appendChild(row);

  state.activeTurn = turn;
  scrollBottom(true);
  return turn;
}

function spinner() {
  const s = document.createElement('div');
  s.className = 'spinner';
  for (let i = 0; i < 3; i++) s.appendChild(document.createElement('span'));
  return s;
}

function appendStream(text, render) {
  const turn = state.activeTurn;
  if (!turn) return;
  if (turn.content.querySelector('.empty-stream')) {
    turn.content.querySelector('.empty-stream').remove();
  }
  if (turn.streamed === undefined) {
    turn.streamed = '';
    turn.content.innerHTML = '';
    turn.mdEl = document.createElement('div');
    turn.mdEl.className = 'md';
    turn.content.appendChild(turn.mdEl);
  }
  turn.streamed += text;
  turn.renderMode = render;
  scheduleStreamRender(turn);
}

// Re-rendering the whole accumulated markdown on every token drowns the
// renderer (marked + highlight.js per chunk), so the UI freezes and the
// response appears only at the end. Debounce to a few renders per second.
function scheduleStreamRender(turn) {
  if (turn.renderTimer) clearTimeout(turn.renderTimer);
  turn.renderTimer = setTimeout(() => {
    turn.renderTimer = null;
    renderStreamNow(turn);
  }, 80);
}

function renderStreamNow(turn) {
  if (!turn || !turn.mdEl) return;
  turn.mdEl.innerHTML = turn.renderMode ? renderMarkdown(turn.streamed) : escapeHtml(turn.streamed);
  turn.mdEl.querySelectorAll('pre code').forEach(b => {
    if (b.dataset.hl !== '1') {
      b.dataset.hl = '1';
      try { hljs.highlightElement(b); } catch (e) { /* noop */ }
    }
  });
  turn.mdEl.querySelectorAll('a[data-link]').forEach(a => {
    if (!a.dataset.bound) {
      a.dataset.bound = '1';
      a.addEventListener('click', e => {
        e.preventDefault();
        bridge.post('open:external', { url: a.href });
      });
    }
  });
  scrollBottom();
}

function startReasoning(turn) {
  if (turn.reasoningEl) return;
  const block = document.createElement('div');
  block.className = 'reasoning';
  block.innerHTML =
    '<button class="reasoning-head"><span class="reasoning-toggle">' + I('caret') + '</span>' +
    '<span class="reasoning-label">Thinking</span><span class="reasoning-dots">' + I('circle') + '</span></button>' +
    '<div class="reasoning-body"></div>';
  turn.body.insertBefore(block, turn.body.firstChild);
  turn.reasoningEl = block;
  turn.reasoningBody = block.querySelector('.reasoning-body');
  turn.reasoningDots = block.querySelector('.reasoning-dots');
  turn.reasoningOpen = true;
  block.querySelector('.reasoning-toggle').style.transform = 'rotate(90deg)';
  block.querySelector('.reasoning-head').addEventListener('click', () => {
    turn.reasoningOpen = !turn.reasoningOpen;
    turn.reasoningBody.classList.toggle('hidden', !turn.reasoningOpen);
    block.querySelector('.reasoning-toggle').style.transform =
      turn.reasoningOpen ? 'rotate(90deg)' : '';
  });
}

function appendReasoning(text) {
  const turn = state.activeTurn;
  if (!turn) return;
  startReasoning(turn);
  turn.reasoningBody.appendChild(document.createTextNode(text));
}

function endReasoning() {
  const turn = state.activeTurn;
  if (turn && turn.reasoningEl) {
    turn.reasoningEl.querySelector('.reasoning-dots').remove();
    const label = turn.reasoningEl.querySelector('.reasoning-label');
    label.textContent = 'Thought for ' + fmtTime(Date.now());
  }
}

function toolCard(turn, id, name) {
  let card = turn.tools.get(id);
  if (!card) {
    card = document.createElement('div');
    card.className = 'tool-card';
    card.dataset.id = id;
    const iconName = TOOL_ICONS[name] || TOOL_FALLBACK_ICON;
    const cssClass = TOOL_CSS[name] || 'other';
    card.innerHTML =
      '<div class="tool-head">' +
      '<span class="tool-ic ' + cssClass + '">' + I(iconName) + '</span>' +
      '<span class="tool-name"></span>' +
      '<span class="tool-state"><span class="spinner-dot"></span><span class="tool-state-label">running</span></span>' +
      '</div>' +
      '<div class="tool-args hidden"><div class="tool-args-title">Arguments</div><pre class="tool-args-code"></pre></div>' +
      '<div class="tool-out hidden"><div class="tool-out-inner"></div></div>' +
      '<div class="tool-tip hidden">' + I('copy') + '<span>Copy</span></div>';
    card.querySelector('.tool-head').addEventListener('click', e => {
      if (e.target.closest('.tool-tip')) return;
      card.classList.toggle('open');
      const args = card.querySelector('.tool-args');
      if (args && args.textContent.trim()) args.classList.toggle('hidden', !card.classList.contains('open'));
    });
    card.querySelector('.tool-tip').addEventListener('click', () => {
      copyText(card.querySelector('.tool-args-code').textContent);
      toast('Arguments copied');
    });
    turn.body.appendChild(card);
    turn.tools.set(id, card);
  }
  card.querySelector('.tool-name').textContent = name;
  return card;
}

function setToolState(card, stateLabel, cls) {
  const el = card.querySelector('.tool-state');
  el.className = 'tool-state ' + (cls || '');
  el.innerHTML = cls === 'done' ? I('check') + '<span class="tool-state-label">' + stateLabel + '</span>'
    : cls === 'error' ? '<span class="tool-state-label err">' + stateLabel + '</span>'
    : '<span class="spinner-dot"></span><span class="tool-state-label">' + stateLabel + '</span>';
}

function renderToolArgs(card, args) {
  const pre = card.querySelector('.tool-args-code');
  const argsText = args && args.arguments !== undefined ? args.arguments
    : (typeof args === 'string' ? args : JSON.stringify(args, null, 2));
  pre.textContent = typeof argsText === 'string' ? argsText : JSON.stringify(argsText, null, 2);
}

function renderToolOut(card, out) {
  const wrap = card.querySelector('.tool-out');
  if (out == null || out === '') return;
  wrap.classList.remove('hidden');
  const inner = wrap.querySelector('.tool-out-inner');
  inner.textContent = String(out).length > 4000 ? String(out).slice(0, 4000) + '\n… (truncated)' : String(out);
  card.classList.add('has-out');
}

function renderTurnMeta(turn) {
  if (!turn || !turn.meta) return;
  if (!turn.metaEl) {
    const meta = document.createElement('div');
    meta.className = 'turn-meta';
    turn.body.appendChild(meta);
    turn.metaEl = meta;
  }
  const m = turn.meta;
  turn.metaEl.textContent = [
    m.total_duration ? (m.total_duration / 1e9).toFixed(1) + 's' : '',
    m.tokens ? (m.tokens.input || '') + ' in · ' + (m.tokens.output || '') + ' out' : ''
  ].filter(Boolean).join(' · ');
}

function finishTurn() {
  const turn = state.activeTurn;
  if (!turn) return;
  if (turn.streamed === undefined && !turn.reasoningEl && turn.tools.size === 0 && !turn.error) {
    turn.content.innerHTML = '<div class="empty-stream">The model returned an empty response.</div>';
  }
  if (turn.streamed !== undefined) {
    if (turn.renderTimer) {
      clearTimeout(turn.renderTimer);
      turn.renderTimer = null;
    }
    renderStreamNow(turn);
  }
  endReasoning();
  renderTurnMeta(turn);
  if (turn.error) {
    const err = document.createElement('div');
    err.className = 'err-banner';
    err.innerHTML = I('x') + '<span></span>';
    err.querySelector('span').textContent = turn.error;
    turn.body.appendChild(err);
  }
  const stop = turn.body.querySelector('.stop-btn');
  if (stop) stop.remove();
  state.activeTurn = null;
  scrollBottom(true);
}

// ── Event handlers ──────────────────────────────────────────────────
const handlers = {
  meta(p) {
    state.meta = p;
    if (p.workspace) {
      els.workspaceName.textContent = p.workspace.replace(/\\/g, '/');
      els.workspaceLabel.title = p.workspace;
      els.settingsWorkspace.textContent = p.workspace;
    }
    if (p.provider || p.model) updateProviderDisplay(p);
    if (p.context) els.sbContext.textContent = p.context + ' ctx';
    else if (p.context === 0 || p.context == null) els.sbContext.textContent = '';
    if (state.tree === null) bridge.post('files:list', {});
  },

  stream(p) {
    const turn = state.activeTurn || newTurn();
    if (turn.streamed === undefined && turn.reasoningOpen === undefined) {
      turn.content.innerHTML = '<div class="empty-stream"></div>';
    }
    appendStream(p.text, true);
  },

  reasoning(p) {
    const turn = state.activeTurn || newTurn();
    appendReasoning(p.text);
  },

  'tool:start'(p) {
    const turn = state.activeTurn || newTurn();
    state.toolCount++;
    const id = p.id || 'call-' + state.toolCount;
    const card = toolCard(turn, id, p.name);
    if (p.args) renderToolArgs(card, p.args);
    if (p.display) card.querySelector('.tool-name').textContent = p.display;
    setToolState(card, 'running');
    if (turn.streamed === undefined && !turn.content.innerHTML) {
      turn.content.innerHTML = '<div class="empty-stream"></div>';
    }
    if (turn.content.querySelector('.empty-stream')) {
      turn.content.querySelector('.empty-stream').classList.add('hidden');
    }
  },

  'tool:args'(p) {
    const turn = state.activeTurn;
    if (!turn) return;
    const card = turn.tools.get(p.id);
    if (card) {
      renderToolArgs(card, p.args);
      const args = card.querySelector('.tool-args');
      if (card.classList.contains('open')) args.classList.remove('hidden');
    }
  },

  'tool:end'(p) {
    const turn = state.activeTurn;
    if (!turn) return;
    const card = turn.tools.get(p.id);
    if (card) {
      setToolState(card, p.error ? 'failed' : 'done', p.error ? 'error' : 'done');
      if (p.out) renderToolOut(card, p.out);
    }
  },

  iter(p) {
    const turn = state.activeTurn;
    if (turn) {
      turn.lastIter = p;
      const label = turn.el.querySelector('.tool-state-label');
      if (label) label.textContent = 'step ' + (p.step || '');
    }
  },

  error(p) {
    const turn = state.activeTurn;
    if (turn) turn.error = p.message || p.error || 'Something went wrong';
    else {
      const t = newTurn();
      t.error = p.message || p.error || 'Something went wrong';
      finishTurn();
    }
    setBusy(false);
    toast(p.message || p.error || 'Error', 'error');
  },

  status(p) {
    const s = els.sbStatus;
    if (p && p.text) {
      s.innerHTML = (p.icon ? I(p.icon) : '') + '<span>' + escapeHtml(p.text) + '</span>';
      s.classList.remove('hidden');
      s.classList.toggle('busy', !!p.busy);
      if (p.busy) setBusy(true);
    } else {
      // Empty payload = the host signals the run is over — finalize the
      // turn so the next run starts a fresh assistant row instead of
      // appending into the previous turn.
      finishTurn();
      s.classList.add('hidden');
      setBusy(false);
    }
  },

  telemetry(p) {
    const t = (p && p.tokens) || p;
    if (t && (t.input || t.output)) {
      els.sbTokens.textContent = 'in ' + (t.input || 0) + ' · out ' + (t.output || 0);
    }
  },

  todos(p) {
    state.todos = p.todos || [];
    renderTodos();
  },

  changes(p) {
    state.changes = p.changes || p.items || [];
    renderChanges();
  },

  toast(p) {
    toast(p.text, p.kind);
  },

  autopilot(p) {
    state.autopilot = p;
    els.sbAutopilot.classList.toggle('hidden', !(p && p.active));
    if (p && p.message) {
      const s = els.sbStatus;
      s.innerHTML = I('spark') + '<span>' + escapeHtml(p.message) + '</span>';
      s.classList.remove('hidden');
      s.classList.add('busy');
    }
  },

  question(p) {
    showQuestion(p);
  },

  settings(p) {
    state.settings = p;
    updateProviderDisplay(p);
    renderSettingsProviders();
    renderSettingsModels();
  },

  'files:list'(p) {
    state.tree = p.tree || p;
    if (state.panel !== 'files') return;
    if (!els.fileFilter.value) renderTree(state.tree, els.fileTree, 0);
    else applyFileFilter();
  },

  'files:expand'(p) {
    if (p.error) {
      toast(p.error, 'error');
      return;
    }
    addTreeChildren(p.path, p.nodes);
  },

  'file:read'(p) {
    if (p.error) {
      els.viewerContent.innerHTML = '<div class="viewer-loading">' + escapeHtml(p.error) + '</div>';
      return;
    }
    const pre = document.createElement('pre');
    const code = document.createElement('code');
    const ext = els.viewerLang.textContent;
    let content = p.content || '';
    if (p.truncated) content += '\n\n… (file truncated, showing first ' + fmtBytes(p.readBytes) + ')';
    code.textContent = content;
    if (ext && hljs.getLanguage(ext)) {
      code.className = 'language-' + ext;
      try { hljs.highlightElement(code); } catch (e) { /* noop */ }
    }
    pre.appendChild(code);
    els.viewerContent.innerHTML = '';
    els.viewerContent.appendChild(pre);
    els.viewerPath.textContent = p.path || state.viewerPath;
  },

  sessions(p) {
    renderHistoryList(p.items || []);
  },

  'session:messages'(p) {
    renderTranscript(p.items || []);
  }
};

function updateProviderDisplay(p) {
  const provider = p.provider || (state.settings && state.settings.provider);
  const model = p.model || (state.settings && state.settings.model);
  els.sbProvider.textContent = provider || '—';
  els.sbModel.textContent = model || '—';
  els.modelChip.textContent = model || provider || '—';
  const btn = els.btnModel;
  btn.title = 'Provider: ' + (provider || '?') + ' · Model: ' + (model || '?');
}

// ── Todos / changes panels ──────────────────────────────────────────
function renderTodos() {
  const list = els.todosList;
  list.innerHTML = '';
  const todos = state.todos;
  if (!todos.length) {
    list.innerHTML = '<div class="empty-hint">The agent\u2019s task list will appear here.</div>';
    els.todosProgress.classList.add('hidden');
    return;
  }
  const done = todos.filter(t => t.status === 'completed').length;
  els.todosProgress.classList.remove('hidden');
  els.todosBarFill.style.width = todos.length ? (done / todos.length * 100) + '%' : '0%';
  els.todosCount.textContent = done + '/' + todos.length;
  todos.forEach(t => {
    const item = document.createElement('div');
    item.className = 'todo-item ' + (t.status === 'completed' ? 'done' : '');
    const icon = t.status === 'completed' ? I('check', 't-status done') :
      (t.status === 'in_progress' || t.status === 'running' ? '<span class="t-status progress">' + I('circle') + '</span>' :
        '<span class="t-status todo">' + I('circle') + '</span>');
    item.innerHTML = icon + '<span class="t-text">' + escapeHtml(t.content || t.text || '') + '</span>' +
      (t.priority ? '<span class="t-pri ' + escapeHtml(t.priority) + '">' + escapeHtml(t.priority) + '</span>' : '');
    list.appendChild(item);
  });
}

function renderChanges() {
  const list = els.changesList;
  list.innerHTML = '';
  const changes = state.changes;
  const count = changes.length;
  els.changesBadge.textContent = count;
  els.changesBadge.classList.toggle('hidden', count === 0);
  if (!count) {
    list.innerHTML = '<div class="empty-hint">File changes made by the agent will appear here.</div>';
    return;
  }
  changes.forEach((c, idx) => {
    const item = document.createElement('div');
    item.className = 'change-item';
    const isMod = c.existed !== false;
    item.innerHTML =
      '<span class="c-dot ' + (isMod ? 'mod' : 'new') + '"></span>' +
      '<div class="c-main">' +
      '<div class="c-path" title="' + escapeHtml(c.path || '') + '">' + escapeHtml(shortPath(c.path, 2)) + '</div>' +
      '<div class="c-meta">' + fmtTime(c.timestamp || c.time) + ' · ' +
      (c.action === 'reverted' ? 'reverted' : (isMod ? 'modified' : 'created')) + '</div>' +
      '</div>' +
      '<span class="c-tag ' + (isMod ? '' : 'new') + '">' + (isMod ? 'MOD' : 'NEW') + '</span>' +
      '<button class="c-revert" title="Revert this change">' + I('undo') + '</button>';
    const revert = item.querySelector('.c-revert');
    revert.addEventListener('click', () => {
      if (c.action === 'reverted') return;
      revert.disabled = true;
      bridge.post('undo:revert', { id: c.id, path: c.path, index: idx });
    });
    list.appendChild(item);
  });
}

// ── Sessions / history ─────────────────────────────────────────
function fmtWhen(ts) {
  if (!ts) return '';
  const diff = Date.now() - ts;
  if (diff < 60e3) return 'just now';
  if (diff < 3600e3) return Math.floor(diff / 60e3) + 'm ago';
  if (diff < 86400e3) return Math.floor(diff / 3600e3) + 'h ago';
  if (diff < 7 * 86400e3) return Math.floor(diff / 86400e3) + 'd ago';
  const d = new Date(ts);
  return (d.getMonth() + 1) + '/' + d.getDate() + '/' + d.getFullYear();
}

function renderTranscript(items) {
  els.messages.innerHTML = '';
  state.activeTurn = null;
  state.toolCards.clear();
  state.toolCount = 0;
  if (!items || !items.length) {
    ensureHero();
    scrollBottom(true);
    return;
  }
  items.forEach(m => {
    if (m.role === 'user') {
      userMessage([{ text: m.text || '' }]);
    } else if (m.role === 'assistant') {
      const turn = newTurn();
      if (m.text) appendStream(m.text, true);
      const tools = m.tools || [];
      if (tools.length) {
        const wrap = document.createElement('div');
        wrap.className = 'transcript-tools';
        tools.forEach(t => {
          const chip = document.createElement('span');
          chip.className = 'tool-chip';
          chip.textContent = (t.name || 'tool') + (t.arg ? ' · ' + t.arg : '');
          chip.title = chip.textContent;
          wrap.appendChild(chip);
        });
        turn.body.appendChild(wrap);
      }
      if (!m.text) {
        turn.content.innerHTML = '<div class="empty-stream"></div>';
      }
      finishTurn();
    }
  });
  scrollBottom(true);
}

function openHistory() {
  els.historyOverlay.classList.remove('hidden');
  els.historyList.innerHTML = '<div class="history-empty">Loading sessions…</div>';
  bridge.post('session:history', {});
}

function closeHistory() {
  els.historyOverlay.classList.add('hidden');
}

function renderHistoryList(items) {
  const list = els.historyList;
  list.innerHTML = '';
  if (!items.length) {
    list.innerHTML = '<div class="history-empty">No saved sessions yet — conversations are saved automatically.</div>';
    return;
  }
  items.forEach(s => {
    const row = document.createElement('div');
    row.className = 'history-item';
    row.innerHTML =
      '<span class="h-ic">' + I('clock') + '</span>' +
      '<div class="h-main">' +
      '<div class="h-title" title="' + escapeHtml(s.title || '') + '">' + escapeHtml(s.title || 'Untitled') + '</div>' +
      '<div class="h-meta">' + fmtWhen(s.updated) +
      (s.turns ? ' · ' + s.turns + ' turns' : '') +
      (s.workspace ? ' · ' + escapeHtml(shortPath(s.workspace, 2)) : '') + '</div>' +
      '</div>' +
      '<button class="h-open" title="Resume this session">' + I('doc') + '</button>' +
      '<button class="h-del" title="Delete this session">' + I('trash') + '</button>';
    row.querySelector('.h-open').addEventListener('click', e => {
      e.stopPropagation();
      bridge.post('session:resume', { id: s.id });
      closeHistory();
    });
    row.querySelector('.h-del').addEventListener('click', e => {
      e.stopPropagation();
      bridge.post('session:delete', { id: s.id });
    });
    row.addEventListener('click', () => {
      bridge.post('session:resume', { id: s.id });
      closeHistory();
    });
    list.appendChild(row);
  });
}

// ── Composer ────────────────────────────────────────────────────────
const SLASH_COMMANDS = [
  { name: 'help', desc: 'Show help and usage', icon: 'question' },
  { name: 'new', desc: 'Start a new session', icon: 'doc' },
  { name: 'undo', desc: 'Undo the last change', icon: 'undo' },
  { name: 'history', desc: 'Show recent sessions', icon: 'clock' },
  { name: 'autopilot', desc: 'Toggle autopilot mode', icon: 'spark' },
  { name: 'theme', desc: 'Toggle dark / light theme', icon: 'moon' },
  { name: 'exit', desc: 'Exit the app', icon: 'x' }
];

function setBusy(busy) {
  state.busy = busy;
  els.btnSend.innerHTML = busy ? I('stop') : I('send');
  els.btnSend.title = busy ? 'Stop (Esc)' : 'Send (Enter)';
  els.composer.classList.toggle('busy', busy);
}

function sendPrompt() {
  const text = els.input.value.trim();
  if (!text || state.busy) return;
  if (state.activeTurn) finishTurn();
  if (text.startsWith('/')) {
    runSlashCommand(text);
    return;
  }
  userMessage([{ text }]);
  els.input.value = '';
  autoGrow();
  hideSlashMenu();
  setBusy(true);
  bridge.post('send', { text });
}

function stopTurn() {
  if (!state.busy) return;
  setBusy(false);
  bridge.post('stop', {});
}

function runSlashCommand(raw) {
  const name = raw.replace(/^\//, '').trim().toLowerCase();
  hideSlashMenu();
  switch (name) {
    case 'help':
      userMessage([{ text: '/help' }]);
      sendHelp();
      break;
    case 'new':
      bridge.post('session:new', {});
      toast('Started a new session');
      break;
    case 'undo':
      bridge.post('undo:revert', { latest: true });
      break;
    case 'history':
      openHistory();
      break;
    case 'autopilot':
      bridge.post('autopilot:toggle', {});
      break;
    case 'theme':
      toggleTheme();
      break;
    case 'exit':
      bridge.post('exit', {});
      break;
    default:
      bridge.post('send', { text: raw });
  }
}

function sendHelp() {
  const help = [
    '**Coding Assistant** — an AI coding agent.',
    '',
    '**Commands**',
    '- `/help` — this help',
    '- `/new` — start a new session',
    '- `/undo` — revert the latest file change',
    '- `/history` — resume a past session',
    '- `/autopilot` — toggle autopilot mode',
    '- `/theme` — toggle dark / light',
    '- `/exit` — close the app',
    '',
    '**Shortcuts**',
    '- `Enter` send · `Shift+Enter` newline',
    '- `Ctrl+B` toggle sidebar · `Ctrl+O` open folder',
    '- `Ctrl+K` command palette · `Ctrl+,` settings',
    '- `Esc` stop the current turn',
    '- `Ctrl+N` new session'
  ].join('\n');
  const turn = newTurn();
  appendStream(help, true);
  finishTurn();
}

function showSlashMenu(filter) {
  const list = els.slashMenu;
  list.innerHTML = '';
  const f = (filter || '').toLowerCase();
  const cmds = SLASH_COMMANDS.filter(c => c.name.includes(f));
  if (!cmds.length) {
    list.classList.add('hidden');
    return;
  }
  cmds.forEach(c => {
    const row = document.createElement('button');
    row.className = 'slash-item';
    row.innerHTML = '<span class="slash-ic">' + I(c.icon) + '</span>' +
      '<span class="slash-name">/' + c.name + '</span>' +
      '<span class="slash-desc">' + c.desc + '</span>';
    row.addEventListener('mousedown', e => {
      e.preventDefault();
      els.input.value = '/' + c.name + ' ';
      els.input.focus();
      hideSlashMenu();
    });
    list.appendChild(row);
  });
  list.classList.remove('hidden');
}

function hideSlashMenu() {
  els.slashMenu.classList.add('hidden');
  els.slashMenu.innerHTML = '';
}

function autoGrow() {
  els.input.style.height = 'auto';
  els.input.style.height = Math.min(els.input.scrollHeight, 160) + 'px';
}

function copyText(t) {
  if (navigator.clipboard && navigator.clipboard.writeText) {
    navigator.clipboard.writeText(t);
  } else {
    const ta = document.createElement('textarea');
    ta.value = t;
    document.body.appendChild(ta);
    ta.select();
    document.execCommand('copy');
    ta.remove();
  }
}

// ── Theme / font ────────────────────────────────────────────────────
const THEMES = ['dark', 'light'];

function applyTheme(theme) {
  document.documentElement.setAttribute('data-theme', theme);
  els.btnTheme.innerHTML = I(theme === 'dark' ? 'sun' : 'moon');
  document.querySelectorAll('.seg-btn[data-theme]').forEach(b => {
    b.classList.toggle('active', b.dataset.theme === theme);
  });
}

function toggleTheme() {
  const cur = document.documentElement.getAttribute('data-theme') || 'dark';
  const next = cur === 'dark' ? 'light' : 'dark';
  applyTheme(next);
  localStorage.setItem('ca-theme', next);
}

function applyFont(px) {
  const base = 14;
  els.app.style.zoom = (px / base).toFixed(2);
  document.querySelectorAll('.seg-btn[data-font]').forEach(b => {
    b.classList.toggle('active', Number(b.dataset.font) === px);
  });
}

// ── Sidebar ─────────────────────────────────────────────────────────
function switchPanel(name) {
  state.panel = name;
  document.querySelectorAll('.side-tab').forEach(t => t.classList.toggle('active', t.dataset.panel === name));
  document.querySelectorAll('.side-panel').forEach(p => p.classList.toggle('active', p.id === 'panel-' + name));
  if (name === 'files' && state.tree === null) bridge.post('files:list', {});
}

function renderTree(nodes, container, depth) {
  container.innerHTML = '';
  (nodes || []).forEach(node => {
    const isDir = node.kind === 'dir' || !!node.children;
    const row = document.createElement('div');
    row.className = 'tree-row' + (isDir ? ' dir' : '');
    row.dataset.path = node.path;
    row.dataset.kind = isDir ? 'dir' : 'file';
    if (depth) row.style.paddingLeft = (depth * 12) + 'px';
    row.innerHTML =
      '<span class="chev">' + I('caret') + '</span>' +
      (isDir ? iconForFile('dir') : iconForFile(node.name)) +
      '<span class="t-label" title="' + escapeHtml(node.path) + '">' + escapeHtml(node.name) + '</span>';
    const chev = row.querySelector('.chev');
    if (!isDir) chev.style.visibility = 'hidden';
    row.addEventListener('click', () => {
      if (isDir) {
        row.classList.toggle('open');
        const children = container.querySelector('div[data-parent="' + CSS.escape(node.path) + '"]');
        if (children) {
          children.classList.toggle('hidden');
          return;
        }
        bridge.post('files:expand', { path: node.path });
      } else {
        document.querySelectorAll('.tree-row.selected').forEach(r => r.classList.remove('selected'));
        row.classList.add('selected');
        openViewer(node.path, node.name);
      }
    });
    container.appendChild(row);
  });
}

function addTreeChildren(parentPath, nodes) {
  let holder = els.fileTree.querySelector('div[data-parent="' + CSS.escape(parentPath) + '"]');
  if (!holder) {
    holder = document.createElement('div');
    holder.className = 'tree-children';
    holder.dataset.parent = parentPath;
    els.fileTree.appendChild(holder);
  }
  renderTree(nodes, holder, depthOf(holder));
  const parentRow = els.fileTree.querySelector('.tree-row[data-path="' + CSS.escape(parentPath) + '"]');
  if (parentRow) parentRow.classList.add('open');
}

function depthOf(holder) {
  let d = 1;
  let el = holder;
  while (el && el !== els.fileTree) {
    el = el.parentElement;
    if (el && el.classList && el.classList.contains('tree-children')) d++;
  }
  return d;
}

function applyFileFilter() {
  const q = (els.fileFilter.value || '').toLowerCase();
  if (!q) {
    if (state.tree) renderTree(state.tree, els.fileTree, 0);
    return;
  }
  const filterNodes = nodes => (nodes || []).filter(n => {
    const match = n.name.toLowerCase().includes(q);
    if (n.children) {
      const kids = filterNodes(n.children);
      return match || kids.length > 0;
    }
    return match;
  });
  renderTree(filterNodes(state.tree), els.fileTree, 0);
}

// ── Viewer drawer ───────────────────────────────────────────────────
function openViewer(path, name) {
  state.viewerPath = path;
  els.viewerPath.textContent = path;
  els.viewer.classList.remove('hidden');
  els.viewerContent.innerHTML = '<div class="viewer-loading">Loading…</div>';
  const ext = (name || '').split('.').pop().toLowerCase();
  els.viewerLang.textContent = ext;
  bridge.post('file:read', { path, maxBytes: 262144 });
}

function closeViewer() {
  state.viewerPath = null;
  els.viewer.classList.add('hidden');
  els.viewerContent.innerHTML = '';
}

// ── Settings ────────────────────────────────────────────────────────
function renderSettingsProviders() {
  const providers = (state.settings && state.settings.providers) || [];
  const cur = state.settings && state.settings.provider;
  els.settingsProviders.innerHTML = '';
  providers.forEach(p => {
    const row = document.createElement('button');
    row.className = 'provider-item' + (p === cur ? ' active' : '');
    row.textContent = p;
    row.addEventListener('click', () => {
      bridge.post('settings:set', { provider: p });
      updateProviderDisplay({ provider: p });
      renderSettingsProviders();
    });
    els.settingsProviders.appendChild(row);
  });
}

function renderSettingsModels() {
  const models = (state.settings && state.settings.models) || [];
  const cur = state.settings && state.settings.model;
  els.settingsModelsSection.classList.toggle('hidden', !models.length);
  els.settingsModels.innerHTML = '';
  models.forEach(m => {
    const row = document.createElement('button');
    row.className = 'model-item' + (m === cur ? ' active' : '');
    row.textContent = m;
    row.addEventListener('click', () => {
      bridge.post('settings:set', { model: m });
      updateProviderDisplay({ model: m });
      renderSettingsModels();
    });
    els.settingsModels.appendChild(row);
  });
}

function openSettings() {
  els.settingsOverlay.classList.remove('hidden');
  if (state.settings) {
    renderSettingsProviders();
    renderSettingsModels();
  } else {
    bridge.post('settings:get', {});
  }
}

function closeSettings() {
  els.settingsOverlay.classList.add('hidden');
}

// ── Question modal ──────────────────────────────────────────────────
let pendingQuestion = null;

function showQuestion(p) {
  pendingQuestion = p;
  els.questionText.textContent = p.question || '';
  els.questionOptions.innerHTML = '';
  const allowCustom = p.allowCustom !== false;
  (p.options || []).forEach((opt, i) => {
    const btn = document.createElement('button');
    btn.className = 'question-opt';
    btn.textContent = typeof opt === 'string' ? opt : (opt.label || opt);
    btn.addEventListener('click', () => answerQuestion(i, opt.value !== undefined ? opt.value : opt));
    els.questionOptions.appendChild(btn);
  });
  els.questionCustom.classList.toggle('hidden', !allowCustom);
  els.questionOverlay.classList.remove('hidden');
  if (allowCustom) setTimeout(() => els.questionInput.focus(), 50);
}

function answerQuestion(index, value) {
  els.questionOverlay.classList.add('hidden');
  const q = pendingQuestion;
  pendingQuestion = null;
  if (!q) return;
  bridge.post('question:answer', { id: q.id, index, value: String(value) });
}

// ── Palette ─────────────────────────────────────────────────────────
function openPalette() {
  els.paletteOverlay.classList.remove('hidden');
  els.paletteInput.value = '';
  els.paletteResults.innerHTML = '';
  renderPalette('');
  setTimeout(() => els.paletteInput.focus(), 30);
}

function closePalette() {
  els.paletteOverlay.classList.add('hidden');
}

function paletteItems(filter) {
  const f = (filter || '').toLowerCase();
  const items = SLASH_COMMANDS.map(c => ({
    label: '/' + c.name, desc: c.desc, icon: c.icon, run: () => runSlashCommand('/' + c.name)
  }));
  if (!f) return items;
  return items.filter(i => i.label.toLowerCase().includes(f) || i.desc.toLowerCase().includes(f));
}

function renderPalette(filter) {
  els.paletteResults.innerHTML = '';
  paletteItems(filter).forEach(item => {
    const row = document.createElement('button');
    row.className = 'palette-item';
    row.innerHTML = '<span class="palette-ic">' + I(item.icon) + '</span>' +
      '<span class="palette-label">' + escapeHtml(item.label) + '</span>' +
      '<span class="palette-desc">' + escapeHtml(item.desc) + '</span>';
    row.addEventListener('click', () => {
      closePalette();
      item.run();
    });
    els.paletteResults.appendChild(row);
  });
}

// ── Tabs / shortcuts ────────────────────────────────────────────────
function onKeydown(e) {
  const tag = document.activeElement && document.activeElement.tagName;
  const inInput = tag === 'INPUT' || tag === 'TEXTAREA';

  if (e.key === 'Escape') {
    if (!els.questionOverlay.classList.contains('hidden')) return;
    if (state.busy) { e.preventDefault(); stopTurn(); return; }
    if (!els.historyOverlay.classList.contains('hidden')) { closeHistory(); return; }
    if (state.viewerPath) { closeViewer(); return; }
    closePalette();
    closeSettings();
    return;
  }

  const mod = e.ctrlKey || e.metaKey;
  if (mod && e.key.toLowerCase() === 'b') {
    e.preventDefault();
    els.sidebar.classList.toggle('collapsed');
    localStorage.setItem('ca-sidebar', els.sidebar.classList.contains('collapsed') ? '0' : '1');
  }
  if (mod && e.key.toLowerCase() === 'o') {
    e.preventDefault();
    bridge.post('folder:pick', {});
  }
  if (mod && e.key.toLowerCase() === 'k') {
    e.preventDefault();
    openPalette();
  }
  if (mod && e.key === ',') {
    e.preventDefault();
    openSettings();
  }
  if (mod && e.key.toLowerCase() === 'n') {
    e.preventDefault();
    bridge.post('session:new', {});
  }
  if (mod && e.key.toLowerCase() === '`') {
    e.preventDefault();
    els.input.focus();
  }
  if (e.key === 'Enter' && inInput && !e.shiftKey && document.activeElement === els.input) {
    e.preventDefault();
    sendPrompt();
  }
}

function onComposerKeydown(e) {
  const value = els.input.value;
  if (e.key === 'Escape') {
    hideSlashMenu();
    return;
  }
  if (value.startsWith('/') && !value.includes(' ')) {
    showSlashMenu(value.slice(1));
  } else {
    hideSlashMenu();
  }
}

// ── Init ────────────────────────────────────────────────────────────
function init() {
  const theme = localStorage.getItem('ca-theme') || 'dark';
  applyTheme(theme);

  const font = Number(localStorage.getItem('ca-font')) || 14;
  applyFont(font);

  if (localStorage.getItem('ca-sidebar') === '0') els.sidebar.classList.add('collapsed');
  else els.sidebar.classList.remove('collapsed');

  ensureHero();

  els.btnSend.addEventListener('click', () => (state.busy ? stopTurn() : sendPrompt()));
  els.btnSlash.addEventListener('click', () => {
    els.input.focus();
    showSlashMenu(els.input.value.replace(/^\//, ''));
  });
  els.btnModel.addEventListener('click', openSettings);
  els.btnFolder.addEventListener('click', () => bridge.post('folder:pick', {}));
  els.btnTheme.addEventListener('click', () => { toggleTheme(); });
  els.btnSettings.addEventListener('click', openSettings);
  els.btnCollapse.addEventListener('click', () => {
    els.sidebar.classList.toggle('collapsed');
    localStorage.setItem('ca-sidebar', els.sidebar.classList.contains('collapsed') ? '0' : '1');
  });

  els.input.addEventListener('input', () => { autoGrow(); onComposerKeydown({ key: 'input', target: els.input }); });
  els.input.addEventListener('keydown', onComposerKeydown);
  els.input.addEventListener('keydown', onKeydown);

  document.querySelectorAll('.side-tab').forEach(t => {
    t.addEventListener('click', () => switchPanel(t.dataset.panel));
  });

  els.btnRefreshFiles.addEventListener('click', () => bridge.post('files:list', {}));
  els.fileFilter.addEventListener('input', debounce(applyFileFilter, 180));
  els.btnViewerClose.addEventListener('click', closeViewer);
  els.btnViewerCopy.addEventListener('click', () => {
    const text = els.viewerContent.innerText || els.viewerContent.textContent;
    if (text) { copyText(text); toast('Copied file contents'); }
  });
  els.btnViewerExternal.addEventListener('click', () => {
    if (state.viewerPath) bridge.post('open:external', { path: state.viewerPath });
  });

  els.resizer.addEventListener('mousedown', e => {
    e.preventDefault();
    els.resizer.classList.add('dragging');
    const startX = e.clientX;
    const startW = els.sidebar.offsetWidth;
    const move = ev => {
      const w = Math.max(160, Math.min(520, startW + ev.clientX - startX));
      els.sidebar.style.width = w + 'px';
    };
    const up = () => {
      els.resizer.classList.remove('dragging');
      document.removeEventListener('mousemove', move);
      document.removeEventListener('mouseup', up);
      localStorage.setItem('ca-sidebar-w', els.sidebar.style.width);
    };
    document.addEventListener('mousemove', move);
    document.addEventListener('mouseup', up);
  });
  const savedW = localStorage.getItem('ca-sidebar-w');
  if (savedW) els.sidebar.style.width = savedW;

  els.jumpBottom.addEventListener('click', () => scrollBottom(true));

  els.messages.addEventListener('scroll', () => {
    const m = els.messages;
    const near = m.scrollHeight - m.scrollTop - m.clientHeight < 120;
    els.jumpBottom.classList.toggle('hidden', near);
  });

  els.messages.addEventListener('click', e => {
    const link = e.target.closest('a[data-link]');
    if (link) {
      e.preventDefault();
      bridge.post('open:external', { url: link.href });
    }
  });

  document.querySelectorAll('.chip-suggest').forEach(c => {
    c.addEventListener('click', () => {
      els.input.value = c.dataset.s;
      sendPrompt();
    });
  });

  document.querySelectorAll('.seg-btn[data-theme]').forEach(b => {
    b.addEventListener('click', () => {
      const t = b.dataset.theme;
      applyTheme(t);
      localStorage.setItem('ca-theme', t);
    });
  });
  document.querySelectorAll('.seg-btn[data-font]').forEach(b => {
    b.addEventListener('click', () => {
      const px = Number(b.dataset.font);
      applyFont(px);
      localStorage.setItem('ca-font', String(px));
    });
  });

  els.btnSettingsFolder.addEventListener('click', () => {
    closeSettings();
    bridge.post('folder:pick', {});
  });
  document.querySelectorAll('#settings-overlay, #palette-overlay, #history-overlay').forEach(ov => {
    ov.addEventListener('mousedown', e => {
      if (e.target === ov) {
        if (ov === els.settingsOverlay) closeSettings();
        else if (ov === els.paletteOverlay) closePalette();
        else closeHistory();
      }
    });
  });
  document.addEventListener('keydown', onKeydown);

  els.btnHistoryClose.addEventListener('click', closeHistory);

  els.questionOverlay.addEventListener('mousedown', e => {
    if (e.target === els.questionOverlay && !state.busy) {
      els.questionOverlay.classList.add('hidden');
    }
  });
  els.questionInput.addEventListener('keydown', e => {
    if (e.key === 'Enter') {
      e.preventDefault();
      const v = els.questionInput.value.trim();
      if (v) answerQuestion(-1, v);
    }
  });
  els.questionSubmit.addEventListener('click', () => {
    const v = els.questionInput.value.trim();
    if (v) answerQuestion(-1, v);
  });

  document.addEventListener('mousemove', () => { /* keep active */ });

  bridge.onMessage(e => {
    if (e && e.type && handlers[e.type]) {
      try {
        handlers[e.type](e.payload);
      } catch (err) {
        console.error('[ui] handler error for ' + e.type, err);
        bridge.post('ui:log', { text: 'handler error for ' + e.type + ': ' + (err && err.message ? err.message : String(err)) });
      }
    }
  });

  if (!bridge.available) {
    toast('Host bridge unavailable — the UI cannot talk to the engine.', 'error');
  }

  bridge.post('ui:ready', {});
  bridge.post('settings:get', {});
  bridge.post('files:list', {});
}

document.addEventListener('DOMContentLoaded', init);

