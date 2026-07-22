using TabCtl.Client;
using TabCtl.Config;
using TabCtl.Server;

namespace TabCtl.Commands;

public static class CommandRouter
{
    public static async Task<int> RouteAsync(string[] argv)
    {
        var command = argv[0].ToLowerInvariant();
        var args = new ArgParser(argv.Skip(1));
        var port = args.GetInt("port", Paths.DefaultPort);

        // The daemon is the one command that doesn't talk to a daemon.
        if (command == "serve")
        {
            if (args.Has("foreground")) return await RelayServer.RunAsync(port, CancellationToken.None);

            // Bare `tabctl serve` means "make sure it's running", which ConnectAsync does.
            using var spawned = await DaemonClient.ConnectAsync(port);
            Console.WriteLine($"daemon running on http://127.0.0.1:{port}");
            return 0;
        }

        // `stop` shouldn't resurrect a daemon that isn't there.
        var autostart = command != "stop";
        using var client = await DaemonClient.ConnectAsync(port, autostart);

        return command switch
        {
            "status" => await InspectCommands.StatusAsync(client, args),
            "eval" => await DriveCommands.EvalAsync(client, args),
            "text" => await DriveCommands.TextAsync(client, args),
            "nav" or "navigate" => await DriveCommands.NavigateAsync(client, args),
            "click" => await DriveCommands.ClickAsync(client, args),
            "type" => await DriveCommands.TypeAsync(client, args),
            "key" => await DriveCommands.KeyAsync(client, args),
            "screenshot" or "shot" => await DriveCommands.ScreenshotAsync(client, args),
            "logs" => await InspectCommands.LogsAsync(client, args),
            "net" => await InspectCommands.NetAsync(client, args),
            "cdp" => await InspectCommands.RawCdpAsync(client, args),
            "stop" => await InspectCommands.StopAsync(client),
            _ => Unknown(command),
        };
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Run 'tabctl --help'.");
        return 1;
    }
}
