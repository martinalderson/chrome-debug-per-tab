# tabctl

Lets Claude drive **one specific Chrome tab** that the user has armed by clicking the toolbar
button. Nothing is reachable until they do that; clicking again revokes it.

## Driving a tab

```bash
tabctl status                      # is a tab armed? which one?
tabctl eval 'document.title'
tabctl text h1                     # innerText, cheap way to read the page
tabctl nav example.com             # navigates and waits for load
tabctl click 'button.submit'       # real Input events, not element.click()
tabctl type 'hello' && tabctl key Enter
tabctl screenshot -o /tmp/tab.png  # then Read the PNG
tabctl logs --since 0              # console + uncaught exceptions
tabctl net --since 0               # network requests
tabctl cdp Page.getNavigationHistory   # escape hatch for any CDP method
```

The daemon auto-starts on first use — never tell the user to run `tabctl serve` first.

`logs`/`net` print `nextSeq` on **stderr**; pass it back as `--since` to page without
repeats. The buffer holds 2000 events.

If a command errors with "extension not connected", a tab simply isn't armed — ask the user to
click the toolbar button. Don't try to work around it.

## Layout

- `extension/` — MV3 extension. `background.js` owns the `chrome.debugger` session and the
  WebSocket to the daemon.
- `src/TabCtl/` — .NET 10 Native AOT CLI: top-level `Program.cs`, `Commands/CommandRouter.cs`,
  JSON default output.
  - `Server/RelayState.cs` — multiplexes CDP commands over the one socket, buffers events.
  - `Server/RelayServer.cs` — Kestrel on 127.0.0.1, token + Host + Origin guards.
  - `Client/DaemonClient.cs` — HTTP client, spawns the daemon on demand.

## Things that will bite you

- **CDP payloads are moved as raw JSON strings, never deserialized into DTOs.** That is what
  keeps the build reflection-free for Native AOT. Don't "tidy" this into model classes.
- **Uses `Microsoft.NET.Sdk.Web` + `CreateSlimBuilder`, not `HttpListener`.**
  `HttpListener.AcceptWebSocketAsync` is Windows-only and throws on Linux.
- **The daemon's 20s ping is load-bearing.** WebSocket traffic is what resets Chrome's 30s
  MV3 idle timer. Remove it and the extension's service worker dies mid-session.
- **`global.json` pins SDK 10.0.201** because an 11.0 preview is also installed.
- **`--load-extension` does nothing in Chrome 137+** ("not allowed in Google Chrome"). The
  extension must be loaded by hand at `chrome://extensions`, and `test/e2e.sh` therefore
  cannot launch its own browser — it runs against an already-armed tab.

## Build and test

```bash
dotnet publish src/TabCtl -c Release -r linux-x64 -o dist && cp dist/tabctl ~/.local/bin/
test/e2e.sh --quick    # needs an armed scratch tab; --quick skips the 45s idle test
```

After editing `extension/`, hit reload on the extension card at `chrome://extensions` and
re-arm the tab.
