using System.Text.Json;

using InventorMcp.Server.McpTools;
using InventorMcp.Server.Services;

using ModelContextProtocol;

namespace InventorMcp.Server.Tests.Unit;

[Trait("Level", "Unit")]
public sealed class InputSafetyTests
{
	private static readonly IProgress<ProgressNotificationValue> _noProgress = new Progress<ProgressNotificationValue>();

	/// <summary>
	/// 	The script answers this request before it loads Apprentice, so the test needs no Inventor.
	/// 	It proves that Windows PowerShell gets the script through standard input, and the request file as its parameter.
	/// </summary>
	[Fact]
	public async Task TestCopyRunsItsScriptWithNoScriptFile()
	{
		string folder = Path.Combine(Path.GetTempPath(), $"InventorMcp.Tests.{Guid.NewGuid():N}");
		string source = Path.Combine(folder, "Part.ipt");
		_ = Directory.CreateDirectory(folder);
		await File.WriteAllTextAsync(source, string.Empty, TestContext.Current.CancellationToken);

		try
		{
			object result = await InventorTool.TestCopyAsync(_noProgress, [source], Path.Combine(folder, "Copy"), cancellationToken: TestContext.Current.CancellationToken);
			JsonElement json = JsonSerializer.SerializeToElement(result);

			Assert.Equal("invalid-arguments", json.GetProperty("error").GetString());
			Assert.Contains("give a prefix", json.GetProperty("message").GetString(), StringComparison.Ordinal);
			Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "InventorMcp.TestCopy.*.ps1"));
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Theory]
	[InlineData(@"..\..\")]
	[InlineData(@"sub\TEST_")]
	[InlineData("TEST/")]
	[InlineData("..TEST_")]
	[InlineData("C:")]
	public async Task TestCopyRefusesAPrefixWithPathCharacters(string prefix)
	{
		string source = Path.Combine(Path.GetTempPath(), $"InventorMcp.Tests.{Guid.NewGuid():N}.ipt");
		await File.WriteAllTextAsync(source, string.Empty, TestContext.Current.CancellationToken);

		try
		{
			object result = await InventorTool.TestCopyAsync(_noProgress, [source], Path.Combine(Path.GetTempPath(), "InventorMcp.Tests.Target"), prefix: prefix, cancellationToken: TestContext.Current.CancellationToken);
			JsonElement json = JsonSerializer.SerializeToElement(result);

			Assert.Equal("invalid-arguments", json.GetProperty("error").GetString());
			Assert.Contains("prefix", json.GetProperty("message").GetString(), StringComparison.Ordinal);
		}
		finally
		{
			File.Delete(source);
		}
	}

	[Fact]
	public async Task TestCopyRefusesARelativeSourceFolder()
	{
		string source = Path.Combine(Path.GetTempPath(), $"InventorMcp.Tests.{Guid.NewGuid():N}.ipt");
		await File.WriteAllTextAsync(source, string.Empty, TestContext.Current.CancellationToken);

		try
		{
			object result = await InventorTool.TestCopyAsync(_noProgress, [source], Path.Combine(Path.GetTempPath(), "InventorMcp.Tests.Target"), sourceFolder: "Designs", cancellationToken: TestContext.Current.CancellationToken);

			Assert.Equal("invalid-arguments", JsonSerializer.SerializeToElement(result).GetProperty("error").GetString());
		}
		finally
		{
			File.Delete(source);
		}
	}

	[Theory]
	[InlineData("Front")]
	[InlineData("Left,Up")]
	[InlineData("")]
	public async Task AutoBalloonRefusesAnUnknownSide(string sides)
	{
		string[] sideList = sides.Split(',', StringSplitOptions.RemoveEmptyEntries);
		object result = await InventorTool.AutoBalloonAsync(null!, "VIEW1", sides: sideList, cancellationToken: TestContext.Current.CancellationToken);
		JsonElement json = JsonSerializer.SerializeToElement(result);

		Assert.Equal("invalid-arguments", json.GetProperty("error").GetString());
		Assert.Contains("sides", json.GetProperty("message").GetString(), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("", null, null, null)]
	[InlineData("VIEW1", @"Drawings\Rig.idw", null, null)]
	[InlineData("VIEW1", null, 0.0, null)]
	[InlineData("VIEW1", null, null, -0.5)]
	public async Task AutoBalloonRefusesInvalidArguments(string viewName, string? drawingPath, double? spacingInches, double? offsetInches)
	{
		object result = await InventorTool.AutoBalloonAsync(null!, viewName, drawingPath, spacingInches: spacingInches, offsetInches: offsetInches, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal("invalid-arguments", JsonSerializer.SerializeToElement(result).GetProperty("error").GetString());
	}

	[Theory]
	[InlineData("claude-code 1.0", "claude-code 1.0")]
	[InlineData("client\r\n=====\r\nforged", "client  =====  forged")]
	[InlineData("tab\there", "tab here")]
	public void OneLineRemovesLineBreaks(string value, string expected) =>
		Assert.Equal(expected, ExecutionAuditLog.OneLine(value));
}
