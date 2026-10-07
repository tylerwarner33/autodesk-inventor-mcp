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

// Read one time, at start, so an edit of the user's file has no effect until a person restarts the server.
// See DialogSettings.Load.
DialogSettings dialogSettings = DialogSettings.Load();

Log.Information(
	"Dialog settings read. User file {UserFile} {UserFileState}.",
	DialogSettings.UserFilePath,
	File.Exists(DialogSettings.UserFilePath) ? "applied" : "not found");

foreach (string problem in dialogSettings.Problems)
	Log.Warning("Dialog settings: {Problem}", problem);

builder.Services.AddSingleton<IBlockingDialogs>(static _ => new BlockingDialogs());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ReleaseSelection>();
builder.Services.AddSingleton(services => new BridgeClient(
	services.GetRequiredService<ILogger<BridgeClient>>(),
	services.GetRequiredService<IBlockingDialogs>(),
	services.GetRequiredService<TimeProvider>(),
	services.GetRequiredService<ReleaseSelection>(),
	() => dialogSettings));

// Singleton because it indexes a 12 MB documentation file once and holds the result.
builder.Services.AddSingleton<ApiReferenceService>();
builder.Services.AddSingleton<SkillCatalog>();

builder.Services.AddSingleton<InventorInstallations>();

// The modelling rules go to every client in the initialize response, not only to agents that read .agents/rules.
_ = builder.Services
	.AddMcpServer(static options => options.ServerInstructions = ReadServerInstructions())
	.WithStdioServerTransport()
	.WithToolsFromAssembly()
	// The client name goes to the add-in's audit log, so each snippet there shows which client sent it.
	// The release of each call goes to the server log, because a session can change its release, and the log of the
	// MCP library does not name the tool.
	.WithRequestFilters(static filters => filters.AddCallToolFilter(static next => async (context, cancellationToken) =>
	{
		BridgeClient bridge = context.Services!.GetRequiredService<BridgeClient>();
		ReleaseSelection selection = context.Services!.GetRequiredService<ReleaseSelection>();

		if (bridge.ClientName is null && context.Server.ClientInfo is { } client)
			bridge.ClientName = ExecutionAuditLog.OneLine($"{client.Name} {client.Version}").Trim();

		try
		{
			return await next(context, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			Log.Information(
				"Tool {Tool} finished. Release {Release}, selection {Selection}.",
				context.Params?.Name,
				bridge.ReleaseYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not connected",
				selection.Source);
		}
	}));

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
