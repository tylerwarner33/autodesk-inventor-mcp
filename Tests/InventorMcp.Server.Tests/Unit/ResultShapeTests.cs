using System.Text.Json;

using InventorMcp.Contracts.Models;
using InventorMcp.Server.McpTools;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The server side changes to the health and assembly tree results.
/// </summary>
[Trait("Level", "Unit")]
public sealed class ResultShapeTests
{
	private static readonly DocumentInfo _document = new("Assembly1", "C:\\Work\\Assembly1.iam", "AssemblyDocument", false, true, false);

	[Fact]
	public void SuppressedSickFeaturesAreLeftOutAndCounted()
	{
		HealthInfo health = new(_document, false, [Feature("Hole1", suppressed: false), Feature("Extrusion7", suppressed: true), Feature("Fillet2", suppressed: true)], 0, []);

		JsonElement result = ToJson(InventorTool.FilterSuppressed(health, includeSuppressed: false));

		Assert.Equal(["Hole1"], result.GetProperty("sickFeatures").EnumerateArray().Select(static feature => feature.GetProperty("Name").GetString()));
		Assert.Equal(2, result.GetProperty("suppressedSickFeaturesLeftOut").GetInt32());
	}

	[Fact]
	public void IncludeSuppressedKeepsTheHealthAsItIs()
	{
		HealthInfo health = new(_document, false, [Feature("Extrusion7", suppressed: true)], 0, []);

		Assert.Same(health, InventorTool.FilterSuppressed(health, includeSuppressed: true));
	}

	[Fact]
	public void SummaryCountsEachDepthAndEachDocument()
	{
		AssemblyTree tree = new(_document, [Node("Sub:1", "Sub.iam", Node("Bolt:1", "Bolt.ipt"), Node("Bolt:2", "Bolt.ipt")), Node("Plate:1", "Plate.ipt")], 4, false);

		JsonElement result = ToJson(InventorTool.ShapeTree(tree, summary: true));

		Assert.Equal([2, 2], result.GetProperty("countByDepth").EnumerateArray().Select(static depth => depth.GetProperty("occurrences").GetInt32()));
		Assert.Equal(3, result.GetProperty("uniqueDocuments").GetInt32());
		Assert.Equal("Bolt.ipt", result.GetProperty("documents")[0].GetProperty("file").GetString());
		Assert.Equal(2, result.GetProperty("documents")[0].GetProperty("occurrences").GetInt32());
	}

	[Fact]
	public void SmallTreeIsReturnedAsItIs()
	{
		AssemblyTree tree = new(_document, [Node("Plate:1", "Plate.ipt")], 1, false);

		Assert.Same(tree, InventorTool.ShapeTree(tree, summary: false));
	}

	[Fact]
	public void TreeOverTheLimitBecomesASummaryWithAMessage()
	{
		AssemblyTree tree = new(_document, [.. Enumerable.Range(1, 2000).Select(static index => Node($"Part{index}:1", $"C:\\Work\\Library\\Part{index}.ipt"))], 2000, false);

		JsonElement result = ToJson(InventorTool.ShapeTree(tree, summary: false));

		Assert.Contains("over the limit", result.GetProperty("message").GetString());
		Assert.Equal(2000, result.GetProperty("uniqueDocuments").GetInt32());
		Assert.Equal(1800, result.GetProperty("documentsLeftOut").GetInt32());
	}

	private static FeatureHealth Feature(string name, bool suppressed) => new(name, "HoleFeature", "DriverLost", suppressed, "");

	private static OccurrenceNode Node(string name, string file, params OccurrenceNode[] children) =>
		new(name, file, "PartDocument", false, true, false, false, children);

	private static JsonElement ToJson(object value) => JsonSerializer.SerializeToElement(value);
}