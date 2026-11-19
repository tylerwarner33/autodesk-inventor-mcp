using InventorMcp.AddIn.Operations;

namespace InventorMcp.Server.Tests.Unit;

/// <summary>
/// 	The rules that every tool uses to find its document from a name, with the document lists of a live session.
/// </summary>
/// <remarks>
/// 	The model state lists are the ones that Inventor 2026 gave for an assembly with two occurrences of one part, one in
/// 	the primary model state and one in State2.
/// 	See <c>.agents/rules/inventor-interop.md</c>, "Model states and the edit document".
/// </remarks>
[Trait("Level", "Unit")]
public sealed class DocumentNameMatchTests
{
	private const string Folder = @"C:\Work\Test";
	private const string PartPath = Folder + @"\Bracket.ipt";
	private const string State2 = PartPath + "<State2>";

	private static readonly DocumentNames _assembly = new("Assembly.iam", Folder + @"\Assembly.iam", Folder + @"\Assembly.iam", false);
	private static readonly DocumentNames _primaryMember = new("Bracket.ipt", PartPath, PartPath, false);
	private static readonly DocumentNames _factory = new("Bracket.ipt (State2)", PartPath, State2, true);
	private static readonly DocumentNames _state2Member = new("Bracket.ipt (State2)", PartPath, State2, false);

	private static readonly DocumentNames[] _modelStates = [_assembly, _primaryMember, _state2Member, _factory];

	[Fact]
	public void ADisplayNameFindsItsDocument() =>
		Assert.Equal(0, DocumentNameMatch.Find(_modelStates, "Assembly.iam").Index);

	[Fact]
	public void AFullPathFindsItsDocument() =>
		Assert.Equal(0, DocumentNameMatch.Find(_modelStates, Folder + @"\Assembly.iam").Index);

	[Fact]
	public void TheCaseOfTheNameDoesNotMatter() =>
		Assert.Equal(0, DocumentNameMatch.Find(_modelStates, Folder.ToUpperInvariant() + @"\ASSEMBLY.IAM").Index);

	[Fact]
	public void AFullDocumentNameOfAModelStateFindsItsFactory()
	{
		DocumentMatch match = DocumentNameMatch.Find(_modelStates, State2);

		Assert.Equal(3, match.Index);
		Assert.False(match.IsAmbiguous);
	}

	[Fact]
	public void TheDisplayNameOfAModelStateFindsItsFactory() =>
		Assert.Equal(3, DocumentNameMatch.Find(_modelStates, "Bracket.ipt (State2)").Index);

	[Fact]
	public void TheFullDocumentNameOfThePrimaryModelStateFindsItsMember() =>
		Assert.Equal(1, DocumentNameMatch.Find([_assembly, _primaryMember], PartPath).Index);

	[Fact]
	public void APathWithTwoModelStatesOpenIsAmbiguous()
	{
		DocumentMatch match = DocumentNameMatch.Find(_modelStates, PartPath);

		Assert.Null(match.Index);
		Assert.True(match.IsAmbiguous);
		Assert.Equal([PartPath, State2], match.Candidates);
	}

	[Fact]
	public void TwoDocumentsWithOneDisplayNameAreAmbiguous()
	{
		DocumentNames first = new("Plate.ipt", @"C:\Work\A\Plate.ipt", @"C:\Work\A\Plate.ipt", false);
		DocumentNames second = new("Plate.ipt", @"C:\Work\B\Plate.ipt", @"C:\Work\B\Plate.ipt", false);

		DocumentMatch match = DocumentNameMatch.Find([first, second], "Plate.ipt");

		Assert.Equal([@"C:\Work\A\Plate.ipt", @"C:\Work\B\Plate.ipt"], match.Candidates);
	}

	[Fact]
	public void TwoDocumentsThatWereNeverSavedAreTwoMatches()
	{
		// Neither has a path or a full document name, so only the display name matches, and each is its own document.
		DocumentNames first = new("Part1", "", "", false);
		DocumentNames second = new("Part1", "", "", false);

		Assert.True(DocumentNameMatch.Find([first, second], "Part1").IsAmbiguous);
		Assert.Equal(0, DocumentNameMatch.Find([first], "Part1").Index);
	}

	[Fact]
	public void AFullDocumentNameThatStartsAnotherIsNotThatOther()
	{
		DocumentNames longer = new("Bracket2.ipt", Folder + @"\Bracket2.ipt", Folder + @"\Bracket2.ipt", false);

		Assert.Equal(1, DocumentNameMatch.Find([longer, _primaryMember], PartPath).Index);
	}

	[Fact]
	public void AnEmptyNameMatchesNoDocumentThatWasNeverSaved() =>
		Assert.Null(DocumentNameMatch.Find([new DocumentNames("Part1", "", "", false)], "").Index);

	[Fact]
	public void NoMatchGivesNoIndexAndNoCandidates()
	{
		DocumentMatch match = DocumentNameMatch.Find(_modelStates, "Other.ipt");

		Assert.Null(match.Index);
		Assert.Empty(match.Candidates);
	}
}
