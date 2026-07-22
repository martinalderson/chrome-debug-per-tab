# tabctl

Drive **one specific Chrome tab** over the DevTools Protocol — from the command line, from a
script, or from an AI coding agent. Not the whole browser. One tab, chosen by you, revocable
at any time.

```console
$ tabctl status
No tab armed. Click the tabctl toolbar button on the tab you want to drive.

# ... click the toolbar button on a tab ...

$ tabctl eval 'document.title'
Inbox (3) - you@example.com

$ tabctl click 'button[aria-label="Compose"]'
clicked button[aria-label="Compose"] at (94, 138)

$ tabctl screenshot -o /tmp/tab.png
/tmp/tab.png
```

## Why not just `--remote-debugging-port`?

The usual way to script Chrome exposes **every tab in the browser** to anything that can
reach the port, and it needs a browser relaunch — which in practice means a fresh profile
without your logged-in sessions.

`tabctl` inverts that. Chrome's `chrome.debugger` extension API attaches a CDP session scoped
to a single `tabId`. Your normal browser, your normal profile, one tab, and a toolbar click
as the permission grant.

| | `--remote-debugging-port` | `tabctl` |
|---|---|---|
| Scope | every tab, plus the browser target | one tab |
| Needs a browser relaunch | yes | no |
| Keeps your logged-in profile | usually not | yes |
| Granting access | a launch flag | a toolbar click |
| Revoking access | restart the browser | click again |

## How it works

```
Chrome tab ──chrome.debugger (CDP, ONE tabId)──> extension service worker
                                                          │ WebSocket (dials out)
                                                          ▼
                                            tabctl serve  (127.0.0.1:9333)
                                                          ▲ HTTP + X-Cdp-Token
                                                          │
                                              tabctl eval / click / logs ...
```

An extension can't listen on a port, only dial out — hence the small local daemon in the
middle. It starts on demand the first time you run a command, so there's no separate
"start the server" step.

## Install

Requires the .NET 10 SDK and Chrome.

```bash
dotnet publish src/TabCtl -c Release -r linux-x64 -o dist
cp dist/tabctl ~/.local/bin/
```

Then load the extension:

1. Go to `chrome://extensions`
2. Turn on **Developer mode** (top right)
3. **Load unpacked** → select this repo's `extension/` directory

> Chrome 137+ ignores the `--load-extension` command-line flag (Google disabled it to curb
> malware), so this manual step is the only way to install an unpacked extension. It is a
> one-off — the extension persists across restarts.

## Use

Click the toolbar button on the tab you want to drive. The badge turns green and Chrome
shows its "tabctl started debugging this browser" bar. Click again to revoke.

```
Driving:
  eval <js>                Evaluate JS in the tab, print the result
  text [selector]          Print innerText (default: body)
  nav <url>                Navigate and wait for load
  click <selector>         Real mouse click at the element's centre
  type <text>              Insert text at the caret
  key <name>               Enter, Tab, Escape, ArrowDown, ...
  screenshot [-o f.png]    Save a PNG, print its path

Inspecting:
  status                   Which tab is armed, if any
  logs [--since N]         Console output and uncaught exceptions
  net [--since N]          Network requests
  cdp <Domain.method> [--params '<json>']    Any CDP command

Daemon:
  serve [--foreground]     Start the relay (auto-starts on demand otherwise)
  stop                     Shut it down
```

`logs` and `net` read a 2000-entry ring buffer and print a `nextSeq` on stderr; pass it back
as `--since` to page through without repeats.

`click` dispatches real `Input` domain mouse events at the element's centre rather than
calling `element.click()`, so handlers that check `isTrusted` behave normally.

## Security

The daemon binds `127.0.0.1` only and requires a token from `~/.tabctl/token` (mode `0600`)
in an `X-Cdp-Token` header.

The token lives in a *custom header* deliberately. Custom headers force a CORS preflight that
the daemon never answers, so a malicious web page can't drive your tab by POSTing to
localhost. The `Host` header is checked too, which blocks DNS rebinding. The extension's
WebSocket is accepted only from a `chrome-extension://` origin — a page can open a WebSocket
to localhost, since WS has no preflight, but it cannot forge `Origin`.

The real access control is still the toolbar button: nothing is reachable until you arm a tab.

## Caveats

These are inherent to `chrome.debugger`, not bugs:

- **DevTools can't be open on the armed tab.** Chrome allows one debugger client per tab, so
  attaching fails if DevTools is already there.
- **The infobar can't be suppressed.** Closing it detaches the session.
- **`chrome://` pages, the Web Store, and other extensions' pages can't be armed.**
- The extension's ID changes if you move the `extension/` directory. Add a `key` to the
  manifest if you need it stable.

## Testing

`test/e2e.sh` runs the suite against a real armed tab. Arm a **scratch** tab first — it
navigates the tab around, then restores the original URL.

```bash
test/e2e.sh            # full run, includes a 45s service-worker idle test
test/e2e.sh --quick    # skip the idle test
```

The test can't launch its own browser, for the `--load-extension` reason above.

## Licence

MIT — see [LICENSE](LICENSE).
