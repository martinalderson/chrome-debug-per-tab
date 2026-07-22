// Evaluates an expression inside the tabctl extension's service worker, via the *test*
// Chrome instance's own CDP endpoint. This is how the E2E test arms a tab without a human
// clicking the toolbar button — it drives the real background.js code path.
//
// Usage: node sw-eval.mjs <debugPort> <expression>

const [, , portArg, expression] = process.argv;
const port = Number(portArg);

if (!port || !expression) {
  console.error("usage: node sw-eval.mjs <debugPort> <expression>");
  process.exit(2);
}

async function findServiceWorker(deadline) {
  while (Date.now() < deadline) {
    const res = await fetch(`http://127.0.0.1:${port}/json/list`);
    const targets = await res.json();
    const sw = targets.find(
      (t) => t.type === "service_worker" && t.url.includes("background.js")
    );
    if (sw) return sw;
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error("extension service worker never appeared in the target list");
}

const sw = await findServiceWorker(Date.now() + 20000);
const ws = new WebSocket(sw.webSocketDebuggerUrl);

const result = await new Promise((resolve, reject) => {
  const timer = setTimeout(() => reject(new Error("timed out evaluating in service worker")), 30000);

  ws.onopen = () => {
    ws.send(JSON.stringify({
      id: 1,
      method: "Runtime.evaluate",
      params: { expression, awaitPromise: true, returnByValue: true },
    }));
  };

  ws.onmessage = (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.id !== 1) return;
    clearTimeout(timer);
    if (msg.error) return reject(new Error(JSON.stringify(msg.error)));
    const details = msg.result?.exceptionDetails;
    if (details) return reject(new Error(details.exception?.description ?? details.text));
    resolve(msg.result?.result?.value);
  };

  ws.onerror = () => reject(new Error("websocket error talking to service worker"));
});

ws.close();
console.log(typeof result === "string" ? result : JSON.stringify(result));
