namespace OpenCode.Cli.CommandLine;

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using OpenCode.Cli.Auth;
using OpenCode.Cli.Commands.Api;
using OpenCode.Cli.Commands.Run;
using OpenCode.Cli.Commands.Statistics;
using OpenCode.Schema;
using OpenCode.Protocol;

/// <summary>The sole CLI grammar. Building/parsing this graph does not start services,
/// resolve DI, read stdin/files, or invoke command handlers.</summary>
public static class CliApplication
{
    public static async Task<int> InvokeAsync(string[] args, CancellationToken ct = default, TimeProvider? clock = null)
    {
        var root = CreateRoot(clock);
        var input = CliBooleanSyntax.Normalize(root, args);
        var result = root.Parse(input.Tokens, new ParserConfiguration { ResponseFileTokenReplacer = null });
        var parseExit = result.CommandResult.Command.Name is "run" or "stats" or "api" ? 1 : 2;
        var compatibilityErrors = input.Errors.Concat(PositionalOptionErrors(result)).ToArray();
        if (result.Action?.ClearsParseErrors != true && compatibilityErrors.Length != 0)
        {
            foreach (var error in compatibilityErrors) Console.Error.WriteLine(error);
            return parseExit;
        }
        if (result.Action is ParseErrorAction errorAction)
        {
            errorAction.ShowHelp = false;
            errorAction.ShowTypoCorrections = false;
        }
        var invocation = new InvocationConfiguration
        {
            EnableDefaultExceptionHandler = false,
            ProcessTerminationTimeout = null,
            Output = Console.Out,
            Error = Console.Error
        };
        // TUI retains its native key/cancellation lifecycle; Serve retains the
        // host's ConsoleLifetime. All other command actions use one owned token.
        var intercept = result.Action is AsynchronousCommandLineAction
            && result.CommandResult.Command != root && result.CommandResult.Command.Name is not ("tui" or "serve");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var interrupts = 0;
        ConsoleCancelEventHandler cancel = (_, signal) =>
        {
            signal.Cancel = true;
            if (Interlocked.Increment(ref interrupts) > 1 && result.CommandResult.Command.Name == "run") Environment.Exit(130);
            lifetime.Cancel();
        };
        if (intercept) Console.CancelKeyPress += cancel;
        try
        {
            var code = await result.InvokeAsync(invocation, lifetime.Token).ConfigureAwait(false);
            return result.Action is ParseErrorAction ? parseExit : code;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 130; }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        finally { if (intercept) Console.CancelKeyPress -= cancel; }
    }

    private static IEnumerable<string> PositionalOptionErrors(ParseResult result)
    {
        // The library otherwise accepts an unknown --option as a variadic string
        // argument. Do not regress run into sending misspelled flags as prompt text.
        var positional = new HashSet<Token>(ReferenceEqualityComparer.Instance);
        foreach (var argument in result.CommandResult.Command.Arguments)
            if (result.GetResult(argument) is { } value)
                foreach (var token in value.Tokens) positional.Add(token);
        foreach (var token in result.Tokens)
        {
            if (token.Type == TokenType.DoubleDash) yield break;
            if (token.Type == TokenType.Directive) yield return "Command-line directives are not enabled.";
            if (positional.Contains(token) && token.Value.Length > 1 && token.Value.StartsWith('-'))
                yield return $"Unknown option: {token.Value}. Put literal option-shaped arguments after --.";
        }
    }

    public static RootCommand CreateRoot(TimeProvider? clock = null)
    {
        var time = clock ?? TimeProvider.System;
        var root = new RootCommand("dotnet opencode — independent .NET CLI. No response-file expansion: @file arguments remain literal.");
        root.Directives.Clear();
        var help = root.Options.OfType<HelpOption>().Single();
        help.Aliases.Clear();
        help.Aliases.Add("-h");
        help.Action = new ToolHelpAction((SynchronousCommandLineAction)help.Action!, root.Name);
        root.Options.OfType<VersionOption>().Single().Action = new VersionAction();
        var server = Text("--server", "Connect to an HTTP(S) origin instead of the dotnet managed service");
        server.Recursive = true;
        server.Validators.Add(value => CliValueParsers.Server(value));
        var standalone = Boolean("--standalone", "Use a private scoped server, without managed election or recovery");
        standalone.Recursive = true;
        root.Options.Add(server);
        root.Options.Add(standalone);
        Connected(root, server, standalone);
        root.SetAction((result, token) => LifecycleCommands.TuiAsync(new(result.GetValue(server), result.GetValue(standalone)), token, time));

        var tui = new Command("tui", "Run the existing native TUI");
        Connected(tui, server, standalone);
        tui.SetAction((result, token) => LifecycleCommands.TuiAsync(new(result.GetValue(server), result.GetValue(standalone)), token, time));
        root.Subcommands.Add(tui);

        AddRun(root, server, standalone, time);
        AddStatistics(root, server, standalone, time);
        AddApi(root, server, standalone, time);
        AddAuthentication(root, server, standalone, time);
        AddLifecycle(root, server, standalone, time);
        return root;
    }

    private static void AddRun(RootCommand root, Option<string?> server, Option<bool> standalone, TimeProvider clock)
    {
        var command = new Command("run", "Run one network-admitted prompt. Redirected stdin is appended; empty input fails. Headless forms are cancelled and permissions require explicit --auto.");
        var message = new Argument<string[]>("message") { Arity = ArgumentArity.ZeroOrMore, Description = "Message words; tokens after -- are literal message text" };
        var resume = Boolean("--continue", "Continue the latest top-level Session in this directory", "-c");
        var session = Text("--session", "Session ID to continue", "-s");
        var fork = Boolean("--fork", "Fork the selected Session before continuing");
        var model = Text("--model", "provider/model#variant", "-m");
        var agent = Text("--agent", "Agent to select");
        var format = new Option<string>("--format")
        {
            Description = "Output format: default or json", DefaultValueFactory = _ => "default", Arity = ArgumentArity.ExactlyOne,
            AllowMultipleArgumentsPerToken = true, CustomParser = result =>
            {
                var value = result.Tokens[0].Value;
                if (value is not ("default" or "json")) result.AddError("--format must be default or json.");
                return value;
            }
        };
        var files = new Option<string[]>("--file", "-f") { Description = "Files to attach (each <=10 MiB; repeat up to 100)", Arity = new(1, 100), AllowMultipleArgumentsPerToken = false, DefaultValueFactory = _ => [] };
        var title = Text("--title", "Title for a new Session; an empty value derives it from the message");
        var thinking = Boolean("--thinking", "Show reasoning blocks");
        var auto = Boolean("--auto", "Approve asked permissions once; explicit denials remain denied");
        var yolo = Boolean("--yolo", "Alias permission switch"); yolo.Hidden = true;
        var dangerous = Boolean("--dangerously-skip-permissions", "Alias permission switch"); dangerous.Hidden = true;
        command.Arguments.Add(message);
        foreach (var option in new Option[] { resume, session, fork, model, agent, format, files, title, thinking, auto, yolo, dangerous }) command.Options.Add(option);
        command.Validators.Add(result =>
        {
            if (result.GetValue(fork) && !result.GetValue(resume) && string.IsNullOrEmpty(result.GetValue(session)))
                result.AddError("--fork requires --continue or --session");
        });
        Connected(command, server, standalone);
        command.SetAction((result, token) => RunCommand.RunAsync(new(result.GetValue(message) ?? [], result.GetValue(files) ?? [],
            result.GetValue(resume), result.GetValue(session), result.GetValue(fork), result.GetValue(model), result.GetValue(agent),
            result.GetValue(format)!, result.GetValue(title), result.GetValue(thinking),
            result.GetValue(auto) || result.GetValue(yolo) || result.GetValue(dangerous), result.GetValue(server), result.GetValue(standalone)), token, clock));
        root.Subcommands.Add(command);
    }

    private static void AddStatistics(RootCommand root, Option<string?> server, Option<bool> standalone, TimeProvider clock)
    {
        var command = new Command("stats", "Show shareable statistics. Local timezone is used. --days, --year and --all cannot be combined.");
        var days = Integer("--days", "Last N local calendar days; 0 means today", 0, long.MaxValue);
        var year = Integer("--year", "Calendar year (1970–9999); default current year", 1970, 9999);
        var limit = Integer("--limit", "Rows in detailed sections", 1, long.MaxValue); limit.DefaultValueFactory = _ => 5;
        var all = Boolean("--all", "Lifetime statistics");
        var project = Text("--project", "Project ID or . for the current project");
        var models = Boolean("--models", "Model usage");
        var tools = Boolean("--tools", "Tool reliability (boolean, not an API tool-mode string)");
        var cost = Boolean("--cost", "Cost and token details");
        var full = Boolean("--full", "Every detailed section");
        var json = Boolean("--json", "Print indented statistics JSON");
        foreach (var option in new Option[] { days, year, all, project, models, tools, cost, full, limit, json }) command.Options.Add(option);
        command.Validators.Add(result =>
        {
            if ((result.GetValue(days) is null ? 0 : 1) + (result.GetValue(year) is null ? 0 : 1) + (result.GetValue(all) ? 1 : 0) > 1)
                result.AddError("--days, --year, and --all cannot be combined");
        });
        Connected(command, server, standalone);
        command.SetAction((result, token) => StatisticsCommand.RunAsync(new(result.GetValue(days),
            result.GetValue(year) is { } selectedYear ? (int)selectedYear : null, result.GetValue(all), result.GetValue(project),
            result.GetValue(models), result.GetValue(tools), result.GetValue(cost), result.GetValue(full), result.GetValue(limit) ?? 5,
            result.GetValue(json), result.GetValue(server), result.GetValue(standalone)), token, clock));
        root.Subcommands.Add(command);
    }

    private static void AddApi(RootCommand root, Option<string?> server, Option<bool> standalone, TimeProvider clock)
    {
        var command = new Command("api", "Make a raw HTTP request. Bodies (including HTTP errors) are UTF-8 text; final HTTP status alone does not change exit code. --data is literal, not @file/stdin expansion.");
        var request = new Argument<string[]>("operation | method path") { Arity = new(1, 2), Description = "Exact OpenAPI operation ID or METHOD /path" };
        var data = Text("--data", "Literal request body; default Content-Type application/json", "-d");
        var headers = new Option<Dictionary<string, string>>("--header", "-H")
        {
            Description = "name:value request headers; repeat up to 100; last value per name wins", Arity = new(1, 100), AllowMultipleArgumentsPerToken = false,
            CustomParser = CliValueParsers.Headers, DefaultValueFactory = _ => new(StringComparer.OrdinalIgnoreCase)
        };
        var parameters = new Option<Dictionary<string, string>>("--param")
        {
            Description = "Operation path/query key=value pairs; ignored for raw METHOD /path", Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false, CustomParser = CliValueParsers.Parameters, DefaultValueFactory = _ => new(StringComparer.Ordinal)
        };
        command.Arguments.Add(request);
        command.Options.Add(data); command.Options.Add(headers); command.Options.Add(parameters);
        command.Validators.Add(result =>
        {
            var value = result.GetValue(request);
            if (value is { Length: 2 } && ApiRequestResolver.Raw(value) is null)
                result.AddError("Expected an operation name or an HTTP method and path");
        });
        Connected(command, server, standalone);
        command.SetAction((result, token) => ApiCommand.RunAsync(new(result.GetValue(request)!, result.GetValue(data),
            result.GetValue(headers)!, result.GetValue(parameters)!, result.GetValue(server), result.GetValue(standalone)), token, clock));
        root.Subcommands.Add(command);
    }

    private static void AddAuthentication(RootCommand root, Option<string?> server, Option<bool> standalone, TimeProvider clock)
    {
        var auth = new Command("auth", "Local dotnet-channel authentication. Only login is implemented; no shared-auth import or remote routing.");
        var login = new Command("login", "Log in to opencode or openai. Omit target/method to choose interactively; redirected input requires explicit choices.");
        var target = new Argument<string?>("target") { Arity = ArgumentArity.ZeroOrOne, Description = "opencode or openai" };
        var method = Text("--method", "device (opencode), chatgpt-browser or chatgpt-headless (openai)");
        login.Arguments.Add(target); login.Options.Add(method);
        login.SetAction(async (result, token) => (await AuthCommands.RunAsync(new(result.GetValue(target), result.GetValue(method)), token, clock).ConfigureAwait(false)).ExitCode);
        NoConnection(login, server, standalone);
        auth.Subcommands.Add(login); root.Subcommands.Add(auth);
        var console = new Command("console", "OpenCode Console authentication");
        var consoleLogin = new Command("login", "Log in using the Console device flow; defaults to https://opencode.ai/console");
        var url = new Argument<string?>("url") { Arity = ArgumentArity.ZeroOrOne };
        url.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<string?>() is { } value && !CliValueParsers.IsHttpUrl(value, originOnly: false))
                result.AddError("Console URL must be HTTP(S), without credentials, query, or fragment.");
        });
        consoleLogin.Arguments.Add(url);
        consoleLogin.SetAction(async (result, token) => (await AuthCommands.RunAsync(new(ConsoleUrl: result.GetValue(url), Console: true), token, clock).ConfigureAwait(false)).ExitCode);
        NoConnection(consoleLogin, server, standalone);
        console.Subcommands.Add(consoleLogin); root.Subcommands.Add(console);
    }

    private static void AddLifecycle(RootCommand root, Option<string?> server, Option<bool> standalone, TimeProvider clock)
    {
        var serve = new Command("serve", "Start the native managed server with existing channel configuration and ownership guards");
        var port = Integer("--port", "Listening port (1–65535)", 1, 65535, "-p");
        var registration = Text("--registration-file", "Native service registration file");
        var config = Text("--service-config", "Native service configuration file");
        var startup = Text("--startup-id", "Native startup diagnostic identity"); startup.Hidden = true;
        var report = Text("--startup-report", "Native startup diagnostic report"); report.Hidden = true;
        var service = Boolean("--service", "Native service launch marker"); service.Hidden = true;
        foreach (var option in new Option[] { port, registration, config, startup, report, service }) serve.Options.Add(option);
        NoConnection(serve, server, standalone);
        serve.SetAction((result, token) => LifecycleCommands.ServeAsync(new(result.GetValue(port) is { } value ? (int)value : null,
            result.GetValue(registration), result.GetValue(config), result.GetValue(startup), result.GetValue(report), result.GetValue(service)), token, clock));
        root.Subcommands.Add(serve);
        var status = new Command("status", "Inspect the dotnet managed service without starting it");
        NoConnection(status, server, standalone);
        status.SetAction((_, token) => LifecycleCommands.StatusAsync(token, clock));
        root.Subcommands.Add(status);
        var stop = new Command("stop", "Stop the verified dotnet managed service");
        NoConnection(stop, server, standalone);
        stop.SetAction((_, token) => LifecycleCommands.StopAsync(token, clock));
        root.Subcommands.Add(stop);
    }

    private static Option<string?> Text(string name, string description, params string[] aliases) => new(name, aliases)
    {
        Description = description, Arity = ArgumentArity.ExactlyOne, AllowMultipleArgumentsPerToken = true,
        CustomParser = result => result.Tokens[0].Value
    };
    private static Option<bool> Boolean(string name, string description, params string[] aliases) => new(name, aliases)
    {
        Description = description, Arity = ArgumentArity.ZeroOrOne, DefaultValueFactory = _ => false,
        AllowMultipleArgumentsPerToken = true, CustomParser = result => result.Tokens.Count == 0 || bool.Parse(result.Tokens[0].Value)
    };
    private static Option<long?> Integer(string name, string description, long minimum, long maximum, params string[] aliases) => new(name, aliases)
    {
        Description = description, Arity = ArgumentArity.ExactlyOne, AllowMultipleArgumentsPerToken = true,
        CustomParser = result => CliValueParsers.Integer(result, minimum, maximum)
    };
    private static void Connected(Command command, Option<string?> server, Option<bool> standalone) => command.Validators.Add(result =>
    {
        if (result.GetValue(server) is not null && result.GetValue(standalone)) result.AddError("--server and --standalone cannot be combined");
    });
    private static void NoConnection(Command command, Option<string?> server, Option<bool> standalone)
    {
        command.Description += " This command does not accept --server or --standalone routing.";
        command.Validators.Add(result =>
        {
            if (result.GetResult(server) is { Implicit: false } || result.GetResult(standalone) is { Implicit: false })
                result.AddError("This command does not support --server or --standalone routing.");
        });
    }
    private sealed class VersionAction : SynchronousCommandLineAction
    {
        public override bool ClearsParseErrors => true;
        public override int Invoke(ParseResult result) { result.InvocationConfiguration.Output.WriteLine(ApplicationBuild.Version); return 0; }
    }
}
