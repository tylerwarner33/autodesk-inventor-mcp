using InventorMcp.Server.Bridge;
using InventorMcp.Server.McpTools;
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
	// Kept by age, not by count: each server process writes its own file, so with many clients a count limit deletes
	// the files of the same day.
	.WriteTo.File(
		Path.Combine(logDirectory, "server-.log"),
		rollingInterval: RollingInterval.Day,
		retainedFileCountLimit: null,
		retainedFileTimeLimit: TimeSpan.FromDays(14))
	.CreateLogger();

builder.Logging.ClearProviders();
_ = builder.Logging.AddSerilog(Log.Logger, dispose: true);

builder.Services.AddSingleton<IBlockingDialogs>(static _ => new BlockingDialogs());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(static services => new BridgeClient(
	services.GetRequiredService<ILogger<BridgeClient>>(),
	services.GetRequiredService<IBlockingDialogs>(),
	services.GetRequiredService<TimeProvider>()));

// Singleton because it indexes a 12 MB documentation file once and holds the result.
builder.Services.AddSingleton<ApiReferenceService>();
builder.Services.AddSingleton<SkillCatalog>();

builder.Services.AddSingleton<InventorInstallations>();

// The modelling rules go to every client in the initialize response, not only to agents that read .agents/rules.
_ = builder.Services
	.AddMcpServer(static options => options.ServerInstructions = ReadServerInstructions())
	.WithStdioServerTransport()
	.WithToolsFromAssembly();

IHost host = builder.Build();

InventorTool.ResultLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("InventorMcp.Server.ToolResults");

await host.RunAsync();

static string ReadServerInstructions()
{
	using Stream stream = typeof(BridgeClient).Assembly.GetManifestResourceStream("InventorMcp.Server.ServerInstructions.md")
		?? throw new InvalidOperationException("The embedded resource ServerInstructions.md is missing.");

	using StreamReader reader = new(stream);

	return reader.ReadToEnd();
}
