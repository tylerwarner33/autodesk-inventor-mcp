using System.ComponentModel;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

/// <remarks>
/// 	Optional arguments carry C# default values on purpose.
/// 	The SDK publishes a parameter as required unless it has one, which would force every caller to
/// 	pass nulls and invent numbers for arguments that already have sensible defaults.
/// </remarks>
internal static partial class InventorTool
{
	[McpServerTool(Name = "inventor_parameters")]
	[Description("Reads the parameters of an open part or assembly. Each parameter reports its kind, expression, display value, and internal value. Inventor stores lengths in centimetres and angles in radians, so always read internalValue with its units in mind. A text or boolean parameter has no internal value.")]
	public static Task<object> Parameters(
		BridgeClient bridge,
		[Description("Display name or full path of the document. Omit to use the active document.")] string? documentName = null,
		[Description("Return only parameters whose name contains this text. Omit for all.")] string? nameFilter = null,
		CancellationToken cancellationToken = default) =>
		SafeAsync(() => bridge.InvokeAsync<IReadOnlyList<Contracts.Models.ParameterInfo>>(
			BridgeOperations.Parameters,
			new ParametersRequest(documentName, nameFilter),
			cancellationToken));

	[McpServerTool(Name = "inventor_set_parameter")]
	[Description("Sets one parameter and rebuilds the document. This changes the user's model. The accessor used depends on the parameter's kind: a numeric parameter takes an expression such as '50 mm', a text parameter takes plain text, and a boolean parameter takes 'true' or 'false'. Check the kind with inventor_parameters first, and check a numeric expression with inventor_evaluate_expression.")]
	public static Task<object> SetParameter(
		BridgeClient bridge,
		[Description("Parameter name.")] string name,
		[Description("New value. For a numeric parameter this is an expression, ex. '50 mm' or 'Width / 2'.")] string expression,
		[Description("Display name or full path of the document. Omit to use the active document.")] string? documentName = null,
		[Description("Turn iLogic rules off for the write, so the change does not run them. They are turned back on after it. For several writes, use inventor_set_parameters.")] bool suppressRules = false,
		CancellationToken cancellationToken = default) =>
		suppressRules
			// The same snippet as inventor_set_parameters, because the add-in operation cannot turn the rules off.
			? SafeAsync(async () => await RunJsonSnippetAsync(
				bridge,
				ComposeSetParametersSnippet(new Dictionary<string, string> { [name] = expression }, documentName, suppressRules: true, runRuleAfter: null, createIfMissing: false, allowUnsavedChanges: true),
				cancellationToken).ConfigureAwait(false))
			: SafeAsync(() => bridge.InvokeAsync<Contracts.Models.ParameterInfo>(
				BridgeOperations.SetParameter,
				new SetParameterRequest(name, expression, documentName),
				cancellationToken));

	[McpServerTool(Name = "inventor_evaluate_expression")]
	[Description("Asks Inventor what an expression evaluates to, without writing it anywhere. Reports validity, the internal value, the display value, and which parameters the expression reads. Use this to check an expression before calling inventor_set_parameter.")]
	public static Task<object> EvaluateExpression(
		BridgeClient bridge,
		[Description("Expression to evaluate, ex. 'Width / 2 + 10 mm'.")] string expression,
		[Description("Units to parse the expression in, ex. 'mm'. Omit to use the document's default length units.")] string? units = null,
		[Description("Display name or full path of the document. Omit to use the active document.")] string? documentName = null,
		CancellationToken cancellationToken = default) =>
		SafeAsync(() => bridge.InvokeAsync<Contracts.Models.ExpressionEvaluation>(
			BridgeOperations.EvaluateExpression,
			new EvaluateExpressionRequest(expression, units, documentName),
			cancellationToken));

	[McpServerTool(Name = "inventor_properties")]
	[Description("Reads the iProperties of an open document, optionally limited to one property set such as 'Design Tracking Properties'.")]
	public static Task<object> Properties(
		BridgeClient bridge,
		[Description("Display name or full path of the document. Omit to use the active document.")] string? documentName = null,
		[Description("Limit the result to one property set. Omit for all sets.")] string? setName = null,
		CancellationToken cancellationToken = default) =>
		SafeAsync(() => bridge.InvokeAsync<IReadOnlyList<Contracts.Models.DocumentProperty>>(
			BridgeOperations.Properties,
			new PropertiesRequest(documentName, setName),
			cancellationToken));

	[McpServerTool(Name = "inventor_set_property")]
	[Description("Sets one iProperty value. This changes the user's document.")]
	public static Task<object> SetProperty(
		BridgeClient bridge,
		[Description("Property set holding the property, ex. 'Design Tracking Properties'.")] string setName,
		[Description("Property display name, ex. 'Part Number'.")] string name,
		[Description("New value as text.")] string value,
		[Description("Display name or full path of the document. Omit to use the active document.")] string? documentName = null,
		CancellationToken cancellationToken = default) =>
		SafeAsync(() => bridge.InvokeAsync<Contracts.Models.DocumentProperty>(
			BridgeOperations.SetProperty,
			new SetPropertyRequest(setName, name, value, documentName),
			cancellationToken));

	[McpServerTool(Name = "inventor_health")]
	[Description("Reports whether a document needs a rebuild, which features are sick and why, and what the Inventor error manager is holding. Use this to debug automation that is producing bad geometry. If a modal dialog blocks Inventor, it returns blocked-by-dialog at once, with the dialog.")]
	public static Task<object> Health(
		BridgeClient bridge,
		[Description("Display name or full path of the document. Omit to use the active document.")] string? documentName = null,
		[Description("Also list sick features that are suppressed. Default false, because a suppressed feature is usually suppressed on purpose.")] bool includeSuppressed = false,
		CancellationToken cancellationToken = default) =>
		UnlessBlockedAsync(
			bridge,
			() => SafeAsync(async () => FilterSuppressed(AddHealthHints(await bridge.InvokeAsync<Contracts.Models.HealthInfo>(
				BridgeOperations.Health,
				new DocumentScopedRequest(documentName),
				cancellationToken).ConfigureAwait(false)), includeSuppressed)),
			cancellationToken);

	/// <summary>
	/// 	Leaves out the sick features that are suppressed, and says how many.
	/// </summary>
	/// <remarks>
	/// 	One part listed about 15 suppressed features, which hid the one that mattered.
	/// 	Filtered in the server, so the add-in needs no change.
	/// </remarks>
	internal static object FilterSuppressed(Contracts.Models.HealthInfo health, bool includeSuppressed)
	{
		int suppressed = health.SickFeatures.Count(static feature => feature.IsSuppressed);

		if (includeSuppressed || suppressed == 0)
			return health;

		return new
		{
			document = health.Document,
			requiresUpdate = health.RequiresUpdate,
			sickFeatures = health.SickFeatures.Where(static feature => feature.IsSuppressed is false),
			suppressedSickFeaturesLeftOut = suppressed,
			errorCount = health.ErrorCount,
			errors = health.Errors
		};
	}

	/// <summary>
	/// 	Explains a sick feature whose meaning is known, because Inventor records no failure text for a feature.
	/// </summary>
	/// <remarks>
	/// 	A model read an unexplained <c>DriverLost</c> on a new hole as benign and reported a hole that cut nothing.
	/// 	Add a status here only when its cause is measured. See <c>.agents/rules/inventor-modeling.md</c>.
	/// </remarks>
	/// <param name="health">
	/// 	The health read from the add-in.
	/// </param>
	/// <returns>
	/// 	The same health, with an explanation for each known status that has no message.
	/// </returns>
	private static Contracts.Models.HealthInfo AddHealthHints(Contracts.Models.HealthInfo health)
	{
		const string driverLostHint =
			"Not benign. On a feature just created, DriverLost usually means its extent points away from the solid, so " +
			"it removed no material. Count the faces it should have made (ex. cylindrical faces for a hole). If there are " +
			"none, delete the feature and create it again in the other direction.";

		return health with
		{
			SickFeatures = [.. health.SickFeatures.Select(static feature =>
				feature.HealthStatus is "DriverLost" && string.IsNullOrEmpty(feature.Message)
					? feature with { Message = driverLostHint }
					: feature)],
		};
	}

	[McpServerTool(Name = "inventor_update")]
	[Description("Rebuilds a document. This changes the user's model and can take a long time on a large assembly.")]
	public static Task<object> Update(
		BridgeClient bridge,
		[Description("Display name or full path of the document. Omit to use the active document.")] string? documentName = null,
		[Description("True performs a full rebuild that continues past failing features. Default false.")] bool full = false,
		CancellationToken cancellationToken = default) =>
		SafeAsync(() => bridge.InvokeAsync<Contracts.Models.DocumentInfo>(
			BridgeOperations.Update,
			new UpdateRequest(documentName, full),
			cancellationToken));
}
