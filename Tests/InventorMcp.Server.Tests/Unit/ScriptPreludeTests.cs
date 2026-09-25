using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests.Unit;

[Trait("Level", "Unit")]
public sealed class ScriptPreludeTests
{
	[Fact]
	public void SnippetWithNoHelperIsSentAsItIs()
	{
		const string code = "return Document.DisplayName;";

		Assert.Same(code, ScriptPrelude.Apply(code));
	}

	[Fact]
	public void SnippetWithAHelperGetsThePreludeAndItsOwnLineNumbers()
	{
		string composed = ScriptPrelude.Apply("double inches = ToInches(2.54);\nreturn inches;");

		Assert.Contains("double ToInches(double centimetres)", composed);
		Assert.EndsWith("#line 1\ndouble inches = ToInches(2.54);\nreturn inches;", composed);
		Assert.StartsWith("#line hidden\n", composed);
	}

	[Fact]
	public void LeadingUsingDirectivesStayFirst()
	{
		string composed = ScriptPrelude.Apply("using System.IO;\r\n// A comment\r\nusing IO = System.IO;\r\nreturn ToInches(1);");

		Assert.StartsWith("using System.IO;\n// A comment\nusing IO = System.IO;\n#line hidden\n", composed);
		Assert.EndsWith("#line 4\nreturn ToInches(1);", composed);
	}

	[Theory]
	[InlineData("using var stream = System.IO.File.OpenRead(path);")]
	[InlineData("using (var stream = System.IO.File.OpenRead(path)) { }")]
	public void UsingStatementIsNotMoved(string statement)
	{
		string composed = ScriptPrelude.Apply($"{statement}\nreturn ToInches(1);");

		Assert.EndsWith($"#line 1\n{statement}\nreturn ToInches(1);", composed);
	}

	[Fact]
	public void AHelperNameInsideAnotherNameIsNotACall()
	{
		const string code = "double MyToInchesValue = 1; return MyToInchesValue;";

		Assert.Same(code, ScriptPrelude.Apply(code));
	}
}