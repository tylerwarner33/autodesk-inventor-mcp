using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Stdio carries the MCP protocol on stdout.
// Any provider that writes there corrupts the stream, so logging goes to a file and nowhere else.
string logDirectory = Path.Combine(
	Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
	"InventorMcp");

_ = Directory.CreateDirectory(logDirectory);

Log.Logger = new LoggerConfiguration()
	.MinimumLevel.Information()
	.WriteTo.File(
		Path.Combine(logDirectory, "server-.log"),
		rollingInterval: RollingInterval.Day,
		retainedFileCountLimit: 7)
	.CreateLogger();

builder.Logging.ClearProviders();
_ = builder.Logging.AddSerilog(Log.Logger, dispose: true);

builder.Services.AddSingleton<BridgeClient>();

// Singleton because it indexes a 12 MB documentation file once and holds the result.
builder.Services.AddSingleton<ApiReferenceService>();

_ = builder.Services
	.AddMcpServer()
	.WithStdioServerTransport()
	.WithToolsFromAssembly();

await builder.Build().RunAsync();
