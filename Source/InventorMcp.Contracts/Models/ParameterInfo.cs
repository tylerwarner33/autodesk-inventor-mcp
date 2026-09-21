namespace InventorMcp.Contracts.Models;

/// <summary>
/// 	One Inventor parameter, reported in both internal and display form.
/// </summary>
/// <remarks>
/// 	Inventor stores every length in centimetres and every angle in radians, whatever the document displays.
/// 	Both forms are returned so a caller never has to guess which one it received.
/// </remarks>
/// <param name="Name">
/// 	Parameter name as used in expressions.
/// </param>
/// <param name="ParameterType">
/// 	Origin of the parameter: "Model", "User", "Reference", "Table", or "Derived".
/// </param>
/// <param name="ValueKind">
/// 	What the parameter holds: "Numeric", "Text", "Boolean", or "Unknown".
/// 	A write uses a different accessor for each, so this decides how the parameter can be set.
/// </param>
/// <param name="Expression">
/// 	The expression string shown in the parameters dialog, ex. "25.4 mm" or "Width / 2".
/// 	A text parameter's expression is returned with its surrounding quotation marks removed.
/// </param>
/// <param name="InternalValue">
/// 	The stored value in Inventor internal units: centimetres for length, radians for angle.
/// 	Null for a text or boolean parameter, which has no numeric value.
/// </param>
/// <param name="DisplayValue">
/// 	The value formatted in the parameter's own units, or the text or boolean value as written.
/// </param>
/// <param name="Units">
/// 	The parameter's unit string, ex. "mm", "ul" for unitless, "Text", or "Boolean".
/// </param>
/// <param name="IsDriven">
/// 	True when the value is driven by another parameter or by a measurement.
/// </param>
/// <param name="IsKey">
/// 	True when the parameter is marked as a key parameter.
/// </param>
/// <param name="Comment">
/// 	Author comment on the parameter, or an empty string.
/// </param>
public sealed record ParameterInfo(
	string Name,
	string ParameterType,
	string ValueKind,
	string Expression,
	double? InternalValue,
	string DisplayValue,
	string Units,
	bool IsDriven,
	bool IsKey,
	string Comment);

/// <summary>
/// 	Well known values for <see cref="ParameterInfo.ValueKind"/>.
/// </summary>
public static class ParameterValueKinds
{
	public const string Numeric = "Numeric";
	public const string Text = "Text";
	public const string Boolean = "Boolean";
	public const string Unknown = "Unknown";
}

/// <summary>
/// 	The result of evaluating an Inventor expression without writing it anywhere.
/// </summary>
/// <remarks>
/// 	Inventor parses an expression in the units it is given, so this answers what a parameter would become
/// 	before the model commits to setting it.
/// </remarks>
/// <param name="Expression">
/// 	The expression as supplied.
/// </param>
/// <param name="IsValid">
/// 	False when Inventor cannot parse the expression in the requested units.
/// </param>
/// <param name="InternalValue">
/// 	The value in Inventor internal units, or null when the expression is not valid.
/// </param>
/// <param name="DisplayValue">
/// 	The value formatted in the requested units, or null when the expression is not valid.
/// </param>
/// <param name="Units">
/// 	The units the expression was evaluated in.
/// </param>
/// <param name="DatabaseUnits">
/// 	The internal units Inventor resolved the expression to, ex. "cm".
/// </param>
/// <param name="DrivingParameters">
/// 	Names of the parameters the expression reads.
/// </param>
public sealed record ExpressionEvaluation(
	string Expression,
	bool IsValid,
	double? InternalValue,
	string? DisplayValue,
	string Units,
	string? DatabaseUnits,
	IReadOnlyList<string> DrivingParameters);

/// <summary>
/// 	One iProperty value.
/// </summary>
/// <param name="SetName">
/// 	Property set the value belongs to, ex. "Design Tracking Properties".
/// </param>
/// <param name="Name">
/// 	Property display name, ex. "Part Number".
/// </param>
/// <param name="Value">
/// 	Property value rendered as text.
/// </param>
/// <param name="ValueType">
/// 	CLR type name of the underlying value, ex. "String" or "DateTime".
/// </param>
/// <param name="Expression">
/// 	The property expression when the value is driven by one, otherwise null.
/// </param>
public sealed record DocumentProperty(
	string SetName,
	string Name,
	string Value,
	string ValueType,
	string? Expression);
