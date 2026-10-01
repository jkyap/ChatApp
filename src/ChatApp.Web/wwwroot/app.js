"use strict";

/*
 * Client overview. Polls the server every 2.5s. The server DB is the only
 * source of truth; the browser remembers, per chat, the highest message
 * number it has seen ("what's newer than my bookmark?" heals any failure).
 * Every send carries a browser-made unique id so the server can ignore
 * duplicates.
 */

// Storage: login ticket in sessionStorage (per-tab, so two windows in one
// browser can be two users); chat state in localStorage keyed by user id.
const LS = {
  token: "chat.token",
  cursors: (uid) => `chat.cursors.${uid}`,
};

const state = {
  token: sessionStorage.getItem(LS.token),
  me: null,
  cursors: {},
  peer: null,          // { id, username } of the open conversation
  convs: new Map(),    // peerId -> conversation row from the server
  messages: new Map(), // peerId -> Map<serverId, msg>
};

const POLL_MS = 2500;

const $ = (id) => document.getElementById(id);
const esc = (s) => s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const convKey = (a, b) => { const lo = Math.min(a, b), hi = Math.max(a, b); return lo * 100000000 + hi; };
const uuid = () => (crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(36).slice(2)}`);
const save = (key, val) => localStorage.setItem(key, JSON.stringify(val));

const FETCH_TIMEOUT_MS = 8000;

async function api(path, opts = {}) {
  const headers = { "Content-Type": "application/json", ...(opts.headers || {}) };
  if (state.token) headers["Authorization"] = "Bearer " + state.token;
  // A request that hangs forever would freeze the poll loop (ticking stays
  // true and every later tick is skipped), so abort anything too slow; the
  // next tick retries with the same cursor and nothing is lost.
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), FETCH_TIMEOUT_MS);
  try {
    const res = await fetch(path, { credentials: "same-origin", ...opts, headers, signal: ctrl.signal });
    if (res.status === 401 && state.me) { logout(); throw new Error("session expired"); }
    return res;
  } finally {
    clearTimeout(timer);
  }
}

/* ---------- auth ---------- */

let mode = "login";
$("auth-toggle").addEventListener("click", () => {
  mode = mode === "login" ? "register" : "login";
  $("auth-submit").textContent = mode === "login" ? "Sign in" : "Create account";
  $("auth-toggle").textContent = mode === "login" ? "Create account" : "Sign in";
});

$("auth-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  $("auth-error").textContent = "";
  const body = JSON.stringify({ username: $("auth-username").value, password: $("auth-password").value });
  const res = await fetch(`/api/${mode}`, { method: "POST", headers: { "Content-Type": "application/json" }, credentials: "same-origin", body });
  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    $("auth-error").textContent = err.error || (mode === "login" ? "Invalid username or password." : "Registration failed.");
    return;
  }
  const data = await res.json();
  state.token = data.token;
  sessionStorage.setItem(LS.token, data.token);
  state.me = data.user;
  enterApp();
});

function logout() {
  sessionStorage.removeItem(LS.token);
  state.token = null; state.me = null;
  location.reload();
}
$("logout").addEventListener("click", () => { api("/api/logout", { method: "POST" }); logout(); });

/* ---------- app bootstrap ---------- */

async function enterApp() {
  state.cursors = JSON.parse(localStorage.getItem(LS.cursors(state.me.id)) || "{}");
  $("auth-view").classList.add("hidden");
  $("app-view").classList.remove("hidden");
  $("me-name").textContent = state.me.username;
  await loadConversations();
  startPolling();
}

async function loadConversations() {
  const res = await api("/api/conversations");
  if (!res.ok) return;
  const { conversations } = await res.json();
  // merge so client-only entries (just-looked-up user) survive refresh
  for (const c of conversations) state.convs.set(c.peer.id, { ...state.convs.get(c.peer.id), ...c });
  if (!state.peer && conversations.length) openConversation(conversations[0].peer);
  renderConversationList();
}

/* ---------- poll loop ---------- */

let pollTimer = null;
let ticking = false;
function startPolling() {
  if (pollTimer) return;
  pollTimer = setInterval(pollNow, POLL_MS);
  document.addEventListener("visibilitychange", () => { if (!document.hidden) pollNow(); });
}

async function pollNow() {
  if (ticking || !state.me) return;
  ticking = true;
  try {
    await loadConversations();
    for (const [peerId] of state.convs) {
      await catchUpPeer(peerId, state.cursors[String(convKey(state.me.id, peerId))] ?? 0);
    }
  } catch { /* next tick retries */ }
  finally { ticking = false; }
}

async function catchUpPeer(peerId, since) {
  const res = await api(`/api/messages?peer=${peerId}&since=${since}`);
  if (!res.ok) return;
  const data = await res.json();
  for (const m of data.messages) applyMessage(m);
  const key = String(convKey(state.me.id, peerId));
  // Follow the server exactly (not max): if its DB was rebuilt smaller, an
  // old bigger bookmark must snap back, otherwise every poll stays empty.
  state.cursors[key] = data.latestSeq;
  save(LS.cursors(state.me.id), state.cursors);
}

// opening a chat shows the newest 50 messages
async function loadLatest(peerId) {
  const res = await api(`/api/messages?peer=${peerId}&take=50`);
  if (!res.ok) return;
  const data = await res.json();
  const map = state.messages.get(peerId) || new Map();
  state.messages.set(peerId, map);
  for (const m of data.messages) map.set(m.id, m);
}

/* ---------- messaging ---------- */

function applyMessage(m) {
  const fromPeer = m.from !== state.me.id ? m.from : m.to;
  const map = state.messages.get(fromPeer) || new Map();
  state.messages.set(fromPeer, map);
  map.set(m.id, m); // same id = same message: repeated polls just overwrite

  const c = state.convs.get(fromPeer) || { peer: { id: fromPeer, username: m.fromName } };
  c.lastText = m.text; c.lastAt = m.at; c.lastFrom = m.from; c.lastSeq = m.seq;
  state.convs.set(fromPeer, c);
  renderConversationList();
  if (state.peer && state.peer.id === fromPeer) renderMessages();
}

async function sendText(text) {
  if (!state.peer || !text.trim()) return;
  const entry = { clientMessageId: uuid(), to: state.peer.id, text: text.trim() };

  // show it right away; replaced with the server copy when the reply lands
  applyMessage({
    id: "p-" + entry.clientMessageId, pending: entry.clientMessageId,
    from: state.me.id, fromName: state.me.username, to: entry.to,
    text: entry.text, at: Date.now(), seq: 0,
  });
  try {
    const res = await api("/api/messages", { method: "POST", body: JSON.stringify(entry) });
    const data = await res.json().catch(() => ({}));
    if (res.ok) markSent(entry.clientMessageId, data.message);
    else dropPending(entry.clientMessageId);
  } catch {
    dropPending(entry.clientMessageId);
  }
}

// Replace the temporary bubble (made-up id) with the real server id + number,
// so it dedupes against future polls and sorts into the right place.
function markSent(clientMessageId, serverInfo) {
  for (const [peerId, map] of state.messages) {
    for (const [id, m] of map) {
      if (m.pending === clientMessageId) {
        m.pending = null;
        if (serverInfo && serverInfo.id) {
          map.delete(id);
          m.id = serverInfo.id;
          if (serverInfo.seq) m.seq = serverInfo.seq;
          map.set(m.id, m);
        }
        renderConversationList();
        if (state.peer && state.peer.id === peerId) renderMessages();
        return;
      }
    }
  }
}

function dropPending(clientMessageId) {
  for (const [, map] of state.messages) {
    for (const [id, m] of map) {
      if (m.pending === clientMessageId) { map.delete(id); renderMessages(); return; }
    }
  }
}

/* ---------- rendering ---------- */

function renderConversationList() {
  const ul = $("conversations");
  ul.innerHTML = "";
  const items = [...state.convs.values()].sort((a, b) => (b.lastAt || 0) - (a.lastAt || 0));
  for (const c of items) {
    const li = document.createElement("li");
    if (state.peer && state.peer.id === c.peer.id) li.classList.add("active");
    li.innerHTML = `
      <div class="conv-body">
        <div class="conv-top"><strong>${esc(c.peer.username)}</strong><time>${fmtTime(c.lastAt)}</time></div>
        <div class="conv-preview">${c.lastFrom === state.me.id ? "You: " : ""}${esc(c.lastText || "")}</div>
      </div>`;
    li.addEventListener("click", () => openConversation(c.peer));
    ul.appendChild(li);
  }
}

async function openConversation(peer) {
  state.peer = peer;
  if (!state.convs.has(peer.id)) state.convs.set(peer.id, { peer });
  $("peer-name").textContent = peer.username;
  $("send-input").disabled = false;
  $("send-btn").disabled = false;
  renderConversationList();
  await loadLatest(peer.id);
  renderMessages();
}

function renderMessages() {
  const box = $("messages");
  const map = state.peer && state.messages.get(state.peer.id);
  box.innerHTML = "";
  if (!map) return;
  // Order by server numbers; a just-sent message has none (0) so it shows
  // as newest until confirmed.
  const seqOf = (m) => m.seq || Number.MAX_SAFE_INTEGER;
  const msgs = [...map.values()].sort((a, b) => (seqOf(a) - seqOf(b)) || (a.at - b.at));
  for (const m of msgs) {
    const div = document.createElement("div");
    div.className = "bubble " + (m.from === state.me.id ? "mine" : "theirs");
    div.innerHTML = `<span class="text">${esc(m.text)}</span><time>${fmtTime(m.at)}</time>`;
    box.appendChild(div);
  }
  box.scrollTop = box.scrollHeight;
}

function fmtTime(ms) {
  if (!ms) return "";
  const d = new Date(ms), now = new Date();
  const hm = d.toTimeString().slice(0, 5);
  return d.toDateString() === now.toDateString() ? hm : d.toLocaleDateString() + " " + hm;
}

$("send-form").addEventListener("submit", (e) => {
  e.preventDefault();
  const input = $("send-input");
  sendText(input.value);
  input.value = "";
  input.focus();
});

$("new-chat").addEventListener("submit", async (e) => {
  e.preventDefault();
  const name = $("new-chat-input").value.trim().toLowerCase();
  if (!name) return;
  for (const [, c] of state.convs) {
    if (c.peer.username.toLowerCase() === name) { openConversation(c.peer); $("new-chat-input").value = ""; return; }
  }
  const res = await api(`/api/users/lookup?username=${encodeURIComponent(name)}`);
  if (res.ok) { openConversation(await res.json()); }
  else alert("No such user.");
  $("new-chat-input").value = "";
});

/* ---------- startup: resume session if this tab has a token ---------- */

(async function boot() {
  if (!state.token) { $("auth-view").classList.remove("hidden"); return; }
  const res = await api("/api/me");
  if (!res.ok) { sessionStorage.removeItem(LS.token); state.token = null; $("auth-view").classList.remove("hidden"); return; }
  const { user } = await res.json();
  state.me = user;
  enterApp();
})();
