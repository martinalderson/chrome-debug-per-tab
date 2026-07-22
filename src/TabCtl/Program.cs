using TabCtl.Client;
using TabCtl.Commands;
using TabCtl.Server;

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintHelp();
    return 0;
}

try
{
    return await CommandRouter.RouteAsync(args);
}
catch (DaemonException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
catch (CdpException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 2;
}

static void PrintHelp()
{
    Console.WriteLine("""
        tabctl - drive one specific Chrome tab over the DevTools Protocol

        Usage: tabctl <command> [options]

        You arm a tab by clicking the tabctl toolbar button in Chrome. That click is the
        permission grant: every command below acts on the armed tab and nothing else.

        Driving:
          eval <js>                     Evaluate JS in the tab, print the result
          text [selector]               Print innerText (default: body)
          nav <url>                     Navigate and wait for load
          click <selector>              Real mouse click at the element's centre
          type <text>                   Insert text at the caret
          key <name>                    Press Enter, Tab, Escape, ArrowDown, ...
          screenshot [-o file.png]      Save a PNG, print its path

        Inspecting:
          status                        Which tab is armed, if any
          logs [--since N]              Console output and uncaught exceptions
          net [--since N]               Network requests
          cdp <Domain.method> [--params '<json>']
                                        Send any CDP command (escape hatch)

        Daemon:
          serve [--foreground]          Start the relay (auto-starts on demand otherwise)
          stop                          Shut the relay down

        Options:
          --port <n>                    Daemon port (default: 9333)
          --since <n>                   Only events after this sequence number
          --json                        Raw JSON instead of formatted output
          --full                        screenshot: capture beyond the viewport
          --timeout <secs>              Command timeout (default: 30)
          --help                        Show this help

        Examples:
          tabctl status
          tabctl eval 'document.title'
          tabctl nav example.com && tabctl text h1
          tabctl click 'button[type=submit]'
          tabctl type 'hello' && tabctl key Enter
          tabctl screenshot -o /tmp/tab.png
          tabctl logs --since 0
          tabctl cdp Page.getNavigationHistory

        Notes:
          DevTools cannot be open on the armed tab - Chrome allows only one debugger per tab.
          Chrome shows a "tabctl started debugging this browser" bar; closing it disarms.
          chrome:// pages, the Web Store, and other extensions' pages cannot be armed.
        """);
}
