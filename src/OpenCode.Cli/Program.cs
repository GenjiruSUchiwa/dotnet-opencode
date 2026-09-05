using System.Text;
using OpenCode.Cli.CommandLine;

Console.OutputEncoding = Encoding.UTF8;
Environment.ExitCode = await CliApplication.InvokeAsync(args).ConfigureAwait(false);
