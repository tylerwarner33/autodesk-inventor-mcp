namespace InventorMcp.Contracts;

/// <summary>
/// 	Selects the document an operation acts on.
/// </summary>
/// <param name="DocumentName">
/// 	Display name or full path of the target document.
/// 	Null selects the active document.
/// </param>
public record DocumentScopedRequest(string? DocumentName = null);

/// <summary>
/// 	Arguments for an assembly tree walk.
/// </summary>
/// <param name="MaxDepth">
/// 	Deepest level to walk. A large assembly can hold tens of thousands of occurrences.
/// </param>
/// <param name="MaxNodes">
/// 	Node budget for the whole walk. The result reports truncation rather than failing.
/// </param>
public sealed record AssemblyTreeRequest(
	string? DocumentName = null,
	int MaxDepth = 4,
	int MaxNodes = 2000) : DocumentScopedRequest(DocumentName);

/// <summary>
/// 	Arguments for reading parameters.
/// </summary>
/// <param name="NameFilter">
/// 	Case insensitive substring the parameter name must contain. Null returns every parameter.
/// </param>
public sealed record ParametersRequest(
	string? DocumentName = null,
	string? NameFilter = null) : DocumentScopedRequest(DocumentName);

/// <summary>
/// 	Arguments for setting one parameter.
/// </summary>
/// <param name="Name">
/// 	Parameter name.
/// </param>
/// <param name="Expression">
/// 	The new expression, ex. "50 mm". Inventor parses it in the parameter's own units.
/// </param>
public sealed record SetParameterRequest(
	string Name,
	string Expression,
	string? DocumentName = null) : DocumentScopedRequest(DocumentName);

/// <summary>
/// 	Arguments for evaluating an expression without writing it.
/// </summary>
/// <param name="Expression">
/// 	The expression to evaluate, ex. "Width / 2 + 10 mm".
/// </param>
/// <param name="Units">
/// 	Units to parse the expression in, ex. "mm". Null uses the document's default length units.
/// </param>
public sealed record EvaluateExpressionRequest(
	string Expression,
	string? Units = null,
	string? DocumentName = null) : DocumentScopedRequest(DocumentName);

/// <summary>
/// 	Arguments for reading iProperties.
/// </summary>
/// <param name="SetName">
/// 	Restrict the result to one property set. Null returns every set.
/// </param>
public sealed record PropertiesRequest(
	string? DocumentName = null,
	string? SetName = null) : DocumentScopedRequest(DocumentName);

/// <summary>
/// 	Arguments for setting one iProperty.
/// </summary>
/// <param name="SetName">
/// 	Property set that holds the property, ex. "Design Tracking Properties".
/// </param>
/// <param name="Name">
/// 	Property display name.
/// </param>
/// <param name="Value">
/// 	New value as text. Inventor converts it to the property's stored type.
/// </param>
public sealed record SetPropertyRequest(
	string SetName,
	string Name,
	string Value,
	string? DocumentName = null) : DocumentScopedRequest(DocumentName);

/// <summary>
/// 	Arguments for a rebuild.
/// </summary>
/// <param name="Full">
/// 	True performs a full rebuild rather than an incremental update.
/// </param>
public sealed record UpdateRequest(
	string? DocumentName = null,
	bool Full = false) : DocumentScopedRequest(DocumentName);

/// <summary>
/// 	Arguments for draining the activity ring buffer.
/// </summary>
/// <param name="SinceSequence">
/// 	Return entries with a sequence number above this value. Zero returns everything held.
/// </param>
/// <param name="MaxEntries">
/// 	Cap on the number of entries returned in one drain.
/// </param>
public sealed record ActivityRequest(
	long SinceSequence = 0,
	int MaxEntries = 200);

/// <summary>
/// 	Arguments for executing code inside the Inventor session.
/// </summary>
/// <param name="Code">
/// 	The rule body or expression to run.
/// </param>
/// <param name="AllowUnsavedChanges">
/// 	Execution is refused when the target document is dirty unless this is true.
/// 	The guard exists because the code runs against work that cannot be recovered.
/// </param>
/// <param name="TimeoutSeconds">
/// 	Wall clock budget. Inventor's main thread is blocked for the whole execution.
/// </param>
public sealed record ExecuteRequest(
	string Code,
	string? DocumentName = null,
	bool AllowUnsavedChanges = false,
	int TimeoutSeconds = 30) : DocumentScopedRequest(DocumentName);
