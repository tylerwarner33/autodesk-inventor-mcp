using InventorMcp.Contracts.Models;
using InventorMcp.Server.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The hints use the real API documentation that ships with the server.
/// </summary>
[Trait("Level", "Unit")]
public sealed class DiagnosticHintsTests
{
	private static readonly ApiReferenceService _apiReference = new(NullLogger<ApiReferenceService>.Instance);

	[Fact]
	public void NullableWarningIsRemovedFromASuccess()
	{
		ExecutionResult result = Result(true, "(1,7): warning CS8632: The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.");

		Assert.Empty(DiagnosticHints.Improve(result, _apiReference, 2026).Diagnostics);
	}

	[Fact]
	public void NullableWarningStaysInAFailure()
	{
		ExecutionResult result = Result(false, "(1,7): warning CS8632: The annotation for nullable reference types ...");

		Assert.Single(DiagnosticHints.Improve(result, _apiReference, 2026).Diagnostics);
	}

	[Fact]
	public void MissingMemberNamesWhereItExists()
	{
		// From the usage research: face.RangeBox, which is on the face's Evaluator.
		ExecutionResult result = Result(false, "(3,12): error CS1061: 'Face' does not contain a definition for 'RangeBox' and no accessible extension method 'RangeBox' accepting a first argument of type 'Face' could be found (are you missing a using directive or an assembly reference?)");

		string hint = Assert.Single(DiagnosticHints.Improve(result, _apiReference, 2026).Diagnostics, diagnostic => diagnostic.StartsWith("HINT:", StringComparison.Ordinal));

		Assert.Contains("'RangeBox' is a member of", hint);
		Assert.DoesNotContain("Proxy.RangeBox", hint);
	}

	[Fact]
	public void MissingMemberNamesNearMembersOfTheType()
	{
		ExecutionResult result = Result(false, "(2,5): error CS1061: 'PartComponentDefinition' does not contain a definition for 'Parameter' and no accessible extension method ...");

		string hint = Assert.Single(DiagnosticHints.Improve(result, _apiReference, 2026).Diagnostics, diagnostic => diagnostic.StartsWith("HINT:", StringComparison.Ordinal));

		Assert.Contains("Parameters (property)", hint);
	}

	[Fact]
	public void MissingEnumValueGetsAHint()
	{
		// From the usage research: kWorkFeatureCenterline, which is kWorkFeatureCenterlineType.
		ExecutionResult result = Result(false, "(1,20): error CS0117: 'CenterlineTypeEnum' does not contain a definition for 'kWorkFeatureCenterline'");

		string hint = Assert.Single(DiagnosticHints.Improve(result, _apiReference, 2026).Diagnostics, diagnostic => diagnostic.StartsWith("HINT:", StringComparison.Ordinal));

		Assert.Contains("kWorkFeatureCenterlineType", hint);
	}

	[Fact]
	public void TypeOutsideTheApiGetsNoHint()
	{
		ExecutionResult result = Result(false, "(1,1): error CS1061: 'MyHelper' does not contain a definition for 'Zqxwv'");

		Assert.Single(DiagnosticHints.Improve(result, _apiReference, 2026).Diagnostics);
	}

	[Theory]
	[InlineData("System.Runtime.InteropServices.COMException", "Unspecified error (0x80004005 (E_FAIL))", true)]
	// The text that a write to a library part gave on Inventor 2025.
	[InlineData("System.Runtime.InteropServices.COMException", "Exception has been thrown by the target of an invocation.", true)]
	[InlineData("System.Reflection.TargetInvocationException", "Exception has been thrown by the target of an invocation.", false)]
	[InlineData("System.Runtime.InteropServices.COMException", "The parameter is incorrect. (0x80070057 (E_INVALIDARG))", false)]
	[InlineData("System.InvalidOperationException", "No document is active.", false)]
	public void UnspecifiedComFailureIsFound(string exceptionType, string message, bool expected)
	{
		ExecutionResult result = new(false, null, null, [], [], exceptionType, message, 10);

		Assert.Equal(expected, DiagnosticHints.IsUnspecifiedComFailure(result));
	}

	[Fact]
	public void SuccessIsNotAComFailure() =>
		Assert.False(DiagnosticHints.IsUnspecifiedComFailure(new ExecutionResult(true, null, null, [], [], null, "E_FAIL", 10)));

	private static ExecutionResult Result(bool succeeded, params string[] diagnostics) =>
		new(succeeded, null, null, [], diagnostics, null, null, 10);
}