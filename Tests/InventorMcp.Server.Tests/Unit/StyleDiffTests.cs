using System.Text.Json;

using InventorMcp.Server.Services;

namespace InventorMcp.Server.Tests.Unit;

[Trait("Level", "Unit")]
public sealed class StyleDiffTests
{
	[Fact]
	public void StylesInOnlyOneDocumentAreListed()
	{
		JsonElement diff = Compare(
			Styles("A.idw", "Default", Style("Text", "Note"), Style("Balloon", "Item")),
			Styles("B.idw", "Default", Style("Text", "Note"), Style("Leader", "Arrow")));

		Assert.Equal(["Balloon: Item"], diff.GetProperty("onlyInFirst").EnumerateArray().Select(static item => item.GetString()));
		Assert.Equal(["Leader: Arrow"], diff.GetProperty("onlyInSecond").EnumerateArray().Select(static item => item.GetString()));
		Assert.Equal(1, diff.GetProperty("same").GetInt32());
	}

	[Fact]
	public void ADifferentDetailIsAChange()
	{
		JsonElement diff = Compare(
			Styles("A.idw", "Default", Style("Text", "Note", details: """{"font":"Arial","fontSize":0.25}""")),
			Styles("B.idw", "Default", Style("Text", "Note", details: """{"font":"Arial","fontSize":0.35}""")));

		JsonElement change = Assert.Single(diff.GetProperty("changed").EnumerateArray());
		JsonElement difference = Assert.Single(change.GetProperty("differences").EnumerateArray());

		Assert.Equal("fontSize", difference.GetProperty("field").GetString());
		Assert.Equal("0.25", difference.GetProperty("first").GetString());
		Assert.Equal("0.35", difference.GetProperty("second").GetString());
	}

	[Fact]
	public void ALocationChangeAndAStandardChangeAreReported()
	{
		JsonElement diff = Compare(
			Styles("A.idw", "ANSI", Style("Text", "Note", location: "Both")),
			Styles("B.idw", "ISO", Style("Text", "Note", location: "Local")));

		JsonElement difference = Assert.Single(Assert.Single(diff.GetProperty("changed").EnumerateArray()).GetProperty("differences").EnumerateArray());

		Assert.Equal("location", difference.GetProperty("field").GetString());
		Assert.Equal("ISO", diff.GetProperty("standard").GetProperty("second").GetString());
	}

	[Fact]
	public void SameNameOfAnotherTypeIsAnotherStyle()
	{
		JsonElement diff = Compare(
			Styles("A.idw", "Default", Style("Text", "Default")),
			Styles("B.idw", "Default", Style("Dimension", "Default")));

		Assert.Single(diff.GetProperty("onlyInFirst").EnumerateArray());
		Assert.Single(diff.GetProperty("onlyInSecond").EnumerateArray());
		Assert.Equal("Default", diff.GetProperty("standard").GetString());
	}

	private static JsonElement Compare(string first, string second) =>
		JsonSerializer.SerializeToElement(StyleDiff.Compare(JsonDocument.Parse(first).RootElement, JsonDocument.Parse(second).RootElement));

	private static string Styles(string document, string standard, params string[] styles) =>
		$$"""{"document":"{{document}}","standard":"{{standard}}","styles":[{{string.Join(",", styles)}}]}""";

	private static string Style(string type, string name, string location = "Both", string details = "{}") =>
		$$"""{"name":"{{name}}","type":"{{type}}","location":"{{location}}","upToDate":true,"inUse":true,"details":{{details}}}""";
}
