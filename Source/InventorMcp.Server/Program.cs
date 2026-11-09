using System.Globalization;

using InventorMcp.Server.Bridge;
using InventorMcp.Server.McpTools;
using InventorMcp.Server.Services;
using InventorMcp.Server.Telemetry;

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

TelemetrySettings telemetry = TelemetrySettings.Read(Environment.GetEnvironmentVariable);

// The default template of the file sink, and the same with the trace ID, which Serilog takes from Activity.Current.
const string DefaultLineTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";
const string TracedLineTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {TraceId}{NewLine}{Exception}";

Log.Logger = new LoggerConfiguration()
	.MinimumLevel.Information()
	// Kept by age, not by count: each server process writes its own file, so with many clients a count limit deletes
	// the files of the same day.
	.WriteTo.File(
		Path.Combine(logDirectory, "server-.log"),
		// With telemetry on, each line inside a span ends with its trace ID, so a line leads to its trace.
		outputTemplate: telemetry.Enabled ? TracedLineTemplate : DefaultLineTemplate,
		formatProvider: CultureInfo.InvariantCulture,
		rollingInterval: RollingInterval.Day,
		retainedFileCountLimit: null,
		retainedFileTimeLimit: TimeSpan.FromDays(14))
	.CreateLogger();

builder.Logging.ClearProviders();
_ = builder.Logging.AddSerilog(Log.Logger, dispose: true);

if (telemetry.Enabled)
{
	builder.AddServerTelemetry();
	Log.Information("Telemetry on, endpoint {Endpoint}.", telemetry.Endpoint);
}
else
{
	Log.Information("Telemetry off.");
}

// Read one time, at start, so an edit of the user's file has no effect until a person restarts the server.
// See DialogSettings.Load.
DialogSettings dialogSettings = DialogSettings.Load();

Log.Information(
	"Dialog settings read. User file {UserFile} {UserFileState}.",
	DialogSettings.UserFilePath,
	File.Exists(DialogSettings.UserFilePath) ? "applied" : "not found");

foreach (string problem in dialogSettings.Problems)
	Log.Warning("Dialog settings: {Problem}", problem);

// One instance for both routes, inventor_skill and the Skills over MCP extension, so the files are read once.
SkillCatalog skillCatalog = new();
_ = builder.Services.AddInventorServices(skillCatalog, () => dialogSettings);

SafetySettings safety = SafetySettings.Read(Environment.GetEnvironmentVariable);

Log.Information(
	"Tools {Tools}. Confirmation of risky calls {Confirmation}.",
	safety.ReadOnlyTools ? "read-only" : "all",
	safety.ConfirmRiskyCalls ? "on" : "off");

if (safety.Problem is not null)
	Log.Warning("Safety settings: {Problem}", safety.Problem);

// The modelling rules go to every client in the initialize response, not only to agents that read .agents/rules.
_ = builder.Services
	.AddMcpServer(static options => options.ServerInstructions = ReadServerInstructions())
	.WithStdioServerTransport()
	.WithInventorTools(safety)
	// inventor_skill serves the same skills, so the server starts without the extension when the SDK refuses them.
	.WithInventorSkills(
		skillCatalog,
		static exception => Log.Warning(exception, "The Skills over MCP extension is off. inventor_skill still serves the skills."))
	.WithInventorCallFilter(
		safety,
		static (tool, release, selection) => Log.Information(
			"Tool {Tool} finished. Release {Release}, selection {Selection}.",
			tool,
			release,
			selection));

IHost host = builder.Build();

InventorTool.ResultLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("InventorMcp.Server.ToolResults");

BridgeClient connection = host.Services.GetRequiredService<BridgeClient>();
ServerTelemetry.ConnectionState = () => (connection.IsConnected, connection.ReleaseYear);

await host.RunAsync();

static string ReadServerInstructions()
{
	using Stream stream = typeof(BridgeClient).Assembly.GetManifestResourceStream("InventorMcp.Server.ServerInstructions.md")
		?? throw new InvalidOperationException("The embedded resource ServerInstructions.md is missing.");

	using StreamReader reader = new(stream);

	return reader.ReadToEnd();
}
