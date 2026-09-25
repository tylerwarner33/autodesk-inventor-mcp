using System.Text;

using InventorMcp.AddIn.Bridge;
using InventorMcp.AddIn.Operations;

using Microsoft.Extensions.Time.Testing;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The add-in's log throttle and return value text, compiled into the tests from the add-in's source.
/// </summary>
[Trait("Level", "Unit")]
public sealed class AddInTextTests
{
	private sealed record Row(string Name, double Value);

	[Fact]
	public void ThrottleWritesTheFirstLineAndCountsTheRepeats()
	{
		FakeTimeProvider clock = new();
		LogThrottle throttle = new(clock, TimeSpan.FromMinutes(1));

		Assert.Equal("busy", throttle.Filter("key", "busy"));
		Assert.Null(throttle.Filter("key", "busy"));
		Assert.Null(throttle.Filter("key", "busy"));
		Assert.Equal("other", throttle.Filter("other key", "other"));

		clock.Advance(TimeSpan.FromSeconds(61));
		string? line = throttle.Filter("key", "busy");

		Assert.NotNull(line);
		Assert.StartsWith("busy (and 2 more like it since ", line);
		Assert.Null(throttle.Filter("key", "busy"));
	}

	[Fact]
	public void ThrottleAfterAQuietWindowWritesNoCount()
	{
		FakeTimeProvider clock = new();
		LogThrottle throttle = new(clock, TimeSpan.FromMinutes(1));

		_ = throttle.Filter("key", "busy");
		clock.Advance(TimeSpan.FromMinutes(2));

		Assert.Equal("busy", throttle.Filter("key", "busy"));
	}

	[Fact]
	public void ScalarsAndStringsKeepTheirText()
	{
		Assert.Null(ReturnValueText.Format(null));
		Assert.Equal("text", ReturnValueText.Format("text"));
		Assert.Equal(5.ToString(), ReturnValueText.Format(5));
		Assert.Equal("True", ReturnValueText.Format(true));
		Assert.Equal("abc", ReturnValueText.Format(new StringBuilder("abc")));
	}

	[Fact]
	public void DataBecomesJson()
	{
		Assert.Equal("""["a","b"]""", ReturnValueText.Format(new List<string> { "a", "b" }));
		Assert.Equal("""{"done":3,"next":null}""", ReturnValueText.Format(new { done = 3, next = (string?)null }));
		Assert.Equal("""{"Name":"x","Value":1.5}""", ReturnValueText.Format(new Row("x", 1.5)));
		Assert.Equal("""{"Item1":1,"Item2":"a"}""", ReturnValueText.Format((1, "a")));
		Assert.Equal("""{"w":2}""", ReturnValueText.Format(new Dictionary<string, int> { ["w"] = 2 }));
		Assert.Equal("""[{"Name":"x","Value":1}]""", ReturnValueText.Format(new[] { new Row("x", 1) }));
	}

	[Fact]
	public void ACollectionWithACOMObjectKeepsToString()
	{
		// Scripting.Dictionary is a COM object on every Windows machine.
		object com = Activator.CreateInstance(Type.GetTypeFromProgID("Scripting.Dictionary", throwOnError: true)!)!;
		List<object> list = [com];

		Assert.Equal(list.ToString(), ReturnValueText.Format(list));
		Assert.Equal(com.ToString(), ReturnValueText.Format(com));
	}
}
