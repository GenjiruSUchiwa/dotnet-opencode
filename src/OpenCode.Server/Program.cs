using OpenCode.Server;

var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 5050;
var app = ServerHost.CreateApp(args, port);
app.Run();

public partial class Program { }
