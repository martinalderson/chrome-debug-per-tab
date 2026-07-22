// tabctl — attaches a CDP session to exactly one tab and relays it to a local daemon.
//
// Two things worth knowing before editing:
//
//  1. The debugger attachment is owned by the *extension*, not by this service worker.
//     MV3 kills an idle worker after 30s, but the CDP session survives that. So on wake we
//     restore from session storage and only re-attach if Chrome says we actually lost it.
//
//  2. WebSocket traffic resets the 30s idle timer (Chrome 116+). The daemon pings every
//     20s, which is what keeps this worker resident while a tab is armed. Without that
//     ping the worker dies mid-session and commands hang.

const DEFAULT_PORT = 9333;
const CDP_VERSION = "1.3";

// Enabled on attach so console/network events start buffering in the daemon without the
// caller having to remember to turn domains on first.
const AUTO_ENABLE = ["Page", "Runtime", "Log", "Network", "DOM"];

const RECONNECT_MIN_MS = 500;
const RECONNECT_MAX_MS = 10000;

// Backstop for one specific hole: if this worker is asleep *and* the daemon restarts, there
// is no event to wake us and the reconnect timer died with the previous worker instance. An
// alarm fires regardless, and waking the worker re-runs restore() below.
const KEEPALIVE_ALARM = "tabctl-keepalive";
const KEEPALIVE_MINUTES = 0.5;

let armedTabId = null;
let ws = null;
let reconnectDelay = RECONNECT_MIN_MS;
let reconnectTimer = null;

// ---------------------------------------------------------------- state persistence

async function saveState() {
  await chrome.storage.session.set({ armedTabId });
}

async function loadState() {
  const { armedTabId: saved } = await chrome.storage.session.get("armedTabId");
  return saved ?? null;
}

async function getPort() {
  const { port } = await chrome.storage.local.get("port");
  return port ?? DEFAULT_PORT;
}

// ---------------------------------------------------------------- badge

function setBadge(state) {
  const badges = {
    armed: { text: "●", color: "#2ea043", title: "tabctl: armed" },
    error: { text: "!", color: "#d29922", title: "tabctl: error (see service worker log)" },
    off: { text: "", color: "#000000", title: "tabctl: arm/disarm debugging for this tab" },
  };
  const b = badges[state];
  chrome.action.setBadgeText({ text: b.text });
  chrome.action.setBadgeBackgroundColor({ color: b.color });
  chrome.action.setTitle({ title: b.title });
}

// ---------------------------------------------------------------- CDP attach / detach

async function isStillAttached(tabId) {
  const targets = await chrome.debugger.getTargets();
  return targets.some((t) => t.tabId === tabId && t.attached);
}

async function arm(tabId) {
  if (armedTabId !== null && armedTabId !== tabId) {
    await disarm("rearmed on another tab");
  }

  await chrome.debugger.attach({ tabId }, CDP_VERSION);
  armedTabId = tabId;
  await saveState();

  for (const domain of AUTO_ENABLE) {
    // A domain refusing to enable shouldn't sink the whole session — Network.enable fails
    // on some internal pages, for instance, while Runtime still works fine.
    try {
      await chrome.debugger.sendCommand({ tabId }, `${domain}.enable`);
    } catch (e) {
      console.warn(`tabctl: ${domain}.enable failed:`, e.message);
    }
  }

  setBadge("armed");
  chrome.alarms.create(KEEPALIVE_ALARM, { periodInMinutes: KEEPALIVE_MINUTES });
  connect();
  await announceAttached();
  console.log(`tabctl: armed tab ${tabId}`);
}

async function disarm(reason) {
  const tabId = armedTabId;
  armedTabId = null;
  await saveState();
  setBadge("off");
  chrome.alarms.clear(KEEPALIVE_ALARM);

  if (tabId !== null) {
    try {
      await chrome.debugger.detach({ tabId });
    } catch {
      // Already gone (tab closed, infobar dismissed). Nothing to clean up.
    }
  }

  send({ type: "detached", reason: reason ?? "disarmed" });
  console.log(`tabctl: disarmed (${reason})`);
  disconnect();
}

async function announceAttached() {
  if (armedTabId === null) return;
  try {
    const tab = await chrome.tabs.get(armedTabId);
    send({ type: "attached", tabId: tab.id, url: tab.url, title: tab.title });
  } catch {
    send({ type: "attached", tabId: armedTabId, url: null, title: null });
  }
}

// ---------------------------------------------------------------- daemon socket

function send(msg) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(msg));
}

function disconnect() {
  clearTimeout(reconnectTimer);
  reconnectTimer = null;
  if (ws) {
    const sock = ws;
    ws = null;
    sock.close();
  }
}

function scheduleReconnect() {
  if (armedTabId === null || reconnectTimer) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connect();
  }, reconnectDelay);
  reconnectDelay = Math.min(reconnectDelay * 2, RECONNECT_MAX_MS);
}

async function connect() {
  if (armedTabId === null) return;
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;

  const port = await getPort();
  const sock = new WebSocket(`ws://127.0.0.1:${port}/ext`);
  ws = sock;

  sock.onopen = () => {
    reconnectDelay = RECONNECT_MIN_MS;
    console.log("tabctl: connected to daemon");
    announceAttached();
  };

  sock.onmessage = (ev) => handleDaemonMessage(ev.data);

  sock.onclose = () => {
    if (ws === sock) ws = null;
    scheduleReconnect();
  };

  // onerror is followed by onclose; let the close handler own reconnection.
  sock.onerror = () => {};
}

async function handleDaemonMessage(raw) {
  let msg;
  try {
    msg = JSON.parse(raw);
  } catch {
    return;
  }

  if (msg.type === "ping") {
    send({ type: "pong" });
    return;
  }

  if (typeof msg.id !== "number" || typeof msg.method !== "string") return;

  if (armedTabId === null) {
    send({ id: msg.id, error: "no tab is armed" });
    return;
  }

  try {
    const result = await chrome.debugger.sendCommand(
      { tabId: armedTabId },
      msg.method,
      msg.params ?? {}
    );
    send({ id: msg.id, result: result ?? {} });
  } catch (e) {
    send({ id: msg.id, error: e?.message ?? String(e) });
  }
}

// ---------------------------------------------------------------- chrome events

chrome.action.onClicked.addListener(async (tab) => {
  try {
    if (armedTabId === tab.id) {
      await disarm("toolbar click");
    } else {
      await arm(tab.id);
    }
  } catch (e) {
    console.error("tabctl: arm failed:", e);
    armedTabId = null;
    await saveState();
    setBadge("error");
  }
});

chrome.debugger.onEvent.addListener((source, method, params) => {
  if (source.tabId !== armedTabId) return;
  send({ type: "event", method, params: params ?? {} });
});

chrome.debugger.onDetach.addListener(async (source, reason) => {
  if (source.tabId !== armedTabId) return;
  // Chrome detached us (infobar dismissed, DevTools opened, tab closed). Our own detach
  // call already cleared armedTabId, so reaching here means it was external.
  armedTabId = null;
  await saveState();
  setBadge("off");
  chrome.alarms.clear(KEEPALIVE_ALARM);
  send({ type: "detached", reason });
  console.log(`tabctl: detached by browser (${reason})`);
  disconnect();
});

chrome.tabs.onRemoved.addListener(async (tabId) => {
  if (tabId === armedTabId) {
    armedTabId = null;
    await saveState();
    setBadge("off");
    chrome.alarms.clear(KEEPALIVE_ALARM);
    send({ type: "detached", reason: "tab closed" });
    disconnect();
  }
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  // Without this, status keeps reporting whatever the tab showed at arm time, which is
  // actively misleading once anything has navigated.
  if (tabId !== armedTabId) return;
  if (changeInfo.url || changeInfo.title || changeInfo.status === "complete") {
    send({ type: "attached", tabId, url: tab.url, title: tab.title });
  }
});

chrome.alarms.onAlarm.addListener((alarm) => {
  // Waking the worker is most of the point; reconnect if the socket lapsed while asleep.
  if (alarm.name === KEEPALIVE_ALARM && armedTabId !== null) connect();
});

// ---------------------------------------------------------------- worker startup

(async function restore() {
  const saved = await loadState();
  if (saved === null) {
    setBadge("off");
    return;
  }

  if (await isStillAttached(saved)) {
    // Session outlived the worker — just reconnect the relay.
    armedTabId = saved;
    setBadge("armed");
    connect();
    console.log(`tabctl: restored session on tab ${saved}`);
    return;
  }

  try {
    await arm(saved);
  } catch (e) {
    console.warn("tabctl: could not restore session:", e.message);
    armedTabId = null;
    await saveState();
    setBadge("off");
  }
})();
