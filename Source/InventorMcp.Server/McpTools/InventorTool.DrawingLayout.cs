using System.ComponentModel;
using System.Globalization;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

internal static partial class InventorTool
{
	/// <summary>
	/// 	Measures a drawing sheet's annotations and reports where they collide.
	/// </summary>
	/// <remarks>
	/// 	Composed on top of the execution operation, so it needs no add-in rebuild. Read only: a drawing it opens
	/// 	itself is closed without saving, and a drawing that was already open is measured and left as it was.
	///
	/// 	A balloon has no range box in the API, so a style that scales to its text is estimated at 3 times the text
	/// 	height. See <c>Docs/Plugin-Development-Loop.md</c>, "Measuring drawings".
	/// </remarks>
	private const string _drawingLayoutSnippet = """
		#nullable enable

		string? drawingPath = __DRAWING_PATH__;
		double balloonDiameterOverrideInches = __BALLOON_DIAMETER__;
		bool includeDetails = __DETAILS__;

		DrawingDocument? drawing;
		bool openedHere = false;
		if (drawingPath is null)
		{
			drawing = Application.ActiveDocument as DrawingDocument
				?? throw new InvalidOperationException("The active document is not a drawing. Pass drawingPath.");
		}
		else
		{
			drawing = Application.Documents.OfType<Document>()
				.FirstOrDefault(document => string.Equals(document.FullFileName, drawingPath, StringComparison.OrdinalIgnoreCase)) as DrawingDocument;
			if (drawing is null)
			{
				drawing = (DrawingDocument)Application.Documents.Open(drawingPath, OpenVisible: false);
				openedHere = true;
			}
		}

		const double cmPerInch = 2.54;
		string In(double cm) => (cm / cmPerInch).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
		string Box(double x1, double y1, double x2, double y2) => $"[{In(x1)}, {In(y1)} .. {In(x2)}, {In(y2)}]";

		static bool Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
		{
			static double Orient((double X, double Y) p, (double X, double Y) q, (double X, double Y) r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
			double d1 = Orient(c, d, a), d2 = Orient(c, d, b), d3 = Orient(a, b, c), d4 = Orient(a, b, d);
			return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
		}

		try
		{
			Sheet sheet = drawing.ActiveSheet;
			Log($"Drawing '{drawing.DisplayName}', sheet '{sheet.Name}' {In(sheet.Width)} x {In(sheet.Height)} in. All values are inches from the sheet's lower left corner.");

			Box2d? border = null;
			try { border = sheet.Border?.RangeBox; } catch (Exception) { }
			if (border is not null)
				Log($"Border {Box(border.MinPoint.X, border.MinPoint.Y, border.MaxPoint.X, border.MaxPoint.Y)}");

			List<DrawingView> views = [.. sheet.DrawingViews.OfType<DrawingView>()];
			foreach (DrawingView view in views)
				Log($"View '{view.Name}' {Box(view.Left, view.Top - view.Height, view.Left + view.Width, view.Top)}, scale {view.Scale:0.#####}");

			string? ViewAt(double x, double y) =>
				views.FirstOrDefault(view => x >= view.Left && x <= view.Left + view.Width && y >= view.Top - view.Height && y <= view.Top)?.Name;

			List<(string Kind, string Name, double X1, double Y1, double X2, double Y2)> boxes = [];

			foreach (PartsList partsList in sheet.PartsLists)
				boxes.Add(("table", "parts list", partsList.RangeBox.MinPoint.X, partsList.RangeBox.MinPoint.Y, partsList.RangeBox.MaxPoint.X, partsList.RangeBox.MaxPoint.Y));
			foreach (CustomTable table in sheet.CustomTables)
				boxes.Add(("table", table.Title, table.RangeBox.MinPoint.X, table.RangeBox.MinPoint.Y, table.RangeBox.MaxPoint.X, table.RangeBox.MaxPoint.Y));
			if (sheet.TitleBlock is TitleBlock titleBlock)
				boxes.Add(("table", "title block", titleBlock.RangeBox.MinPoint.X, titleBlock.RangeBox.MinPoint.Y, titleBlock.RangeBox.MaxPoint.X, titleBlock.RangeBox.MaxPoint.Y));
			foreach (var table in boxes)
				Log($"Table '{table.Name}' {Box(table.X1, table.Y1, table.X2, table.Y2)}");

			foreach (GeneralDimension dimension in sheet.DrawingDimensions.GeneralDimensions)
			{
				Box2d text = dimension.Text.RangeBox;
				string name = dimension.Text.Text.Replace("\r", " ").Replace("\n", " ");
				boxes.Add(("dimension", name, text.MinPoint.X, text.MinPoint.Y, text.MaxPoint.X, text.MaxPoint.Y));
				if (includeDetails)
					Log($"Dimension '{name}' text {Box(text.MinPoint.X, text.MinPoint.Y, text.MaxPoint.X, text.MaxPoint.Y)}");
			}

			List<(string Item, double X, double Y, double Diameter, string? View, List<(double X, double Y)> Path)> balloons = [];
			foreach (Balloon balloon in sheet.Balloons)
			{
				string item = "?";
				try { item = balloon.BalloonValueSets[1].ItemNumber; } catch (Exception) { }

				BalloonStyle style = balloon.Style;
				double diameter = balloonDiameterOverrideInches > 0 ? balloonDiameterOverrideInches * cmPerInch
					: style.ScaleToTextHeight ? style.TextStyle.FontSize * 3.0
					: style.BalloonDiameter;

				List<(double X, double Y)> path = [(balloon.Position.X, balloon.Position.Y)];
				foreach (LeaderNode node in balloon.Leader.AllNodes)
					path.Add((node.Position.X, node.Position.Y));

				string? view = ViewAt(path[^1].X, path[^1].Y);
				balloons.Add((item, balloon.Position.X, balloon.Position.Y, diameter, view, path));
				boxes.Add(("balloon", item, balloon.Position.X - diameter / 2, balloon.Position.Y - diameter / 2, balloon.Position.X + diameter / 2, balloon.Position.Y + diameter / 2));

				if (includeDetails)
					Log($"Balloon {item} at ({In(balloon.Position.X)}, {In(balloon.Position.Y)}), leader to ({In(path[^1].X)}, {In(path[^1].Y)}) on '{view ?? "no view"}'");
			}

			if (balloons.Count > 0)
			{
				string sizing = balloonDiameterOverrideInches > 0 ? "given" : balloons.Count > 0 && sheet.Balloons[1].Style.ScaleToTextHeight ? "estimated from the text height" : "from the style";
				Log($"Balloon diameter {In(balloons[0].Diameter)} in ({sizing}).");
			}

			foreach (var group in balloons.GroupBy(balloon => balloon.View))
			{
				var members = group.ToList();
				double closest = double.MaxValue;
				for (int i = 0; i < members.Count; i++)
					for (int j = i + 1; j < members.Count; j++)
						closest = Math.Min(closest, Math.Sqrt(Math.Pow(members[i].X - members[j].X, 2) + Math.Pow(members[i].Y - members[j].Y, 2)));

				double spanX = members.Max(b => b.X) - members.Min(b => b.X);
				double spanY = members.Max(b => b.Y) - members.Min(b => b.Y);
				Log($"Balloons for '{group.Key ?? "no view"}': {members.Count}, closest centres {(members.Count > 1 ? In(closest) : "-")} in, group spans {In(spanX)} x {In(spanY)} in");
			}

			int issues = 0;
			void Issue(string text)
			{
				issues++;
				Log($"ISSUE {text}");
			}

			for (int i = 0; i < balloons.Count; i++)
				for (int j = i + 1; j < balloons.Count; j++)
				{
					double distance = Math.Sqrt(Math.Pow(balloons[i].X - balloons[j].X, 2) + Math.Pow(balloons[i].Y - balloons[j].Y, 2));
					if (distance < (balloons[i].Diameter + balloons[j].Diameter) / 2)
						Issue($"balloons {balloons[i].Item} and {balloons[j].Item} overlap: centres {In(distance)} in apart");

					for (int s = 1; s < balloons[i].Path.Count; s++)
						for (int t = 1; t < balloons[j].Path.Count; t++)
							if (Cross(balloons[i].Path[s - 1], balloons[i].Path[s], balloons[j].Path[t - 1], balloons[j].Path[t]))
								Issue($"leaders of balloons {balloons[i].Item} and {balloons[j].Item} cross");
				}

			for (int i = 0; i < boxes.Count; i++)
				for (int j = i + 1; j < boxes.Count; j++)
				{
					var a = boxes[i];
					var b = boxes[j];
					if ((a.Kind == "table" && b.Kind == "table") || (a.Kind == "balloon" && b.Kind == "balloon"))
						continue;

					double overlapX = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1);
					double overlapY = Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1);
					if (overlapX > 0.01 && overlapY > 0.01)
						Issue($"{a.Kind} '{a.Name}' overlaps {b.Kind} '{b.Name}' by {In(overlapX)} x {In(overlapY)} in");
				}

			if (border is not null)
				foreach (var box in boxes.Where(box => box.Kind != "table"))
					if (box.X1 < border.MinPoint.X || box.Y1 < border.MinPoint.Y || box.X2 > border.MaxPoint.X || box.Y2 > border.MaxPoint.Y)
						Issue($"{box.Kind} '{box.Name}' extends outside the border");

			return $"{views.Count} views, {balloons.Count} balloons, {sheet.DrawingDimensions.GeneralDimensions.Count} dimensions, {issues} issue(s)";
		}
		finally
		{
			if (openedHere)
				drawing.Close(SkipSave: true);
		}
		""";

	[McpServerTool(Name = "inventor_drawing_layout")]
	[Description("""
		Measures a drawing sheet and reports where its annotations collide: views, balloons, dimension text, parts lists,
		custom tables and the title block, in inches from the sheet's lower left corner.

		Reports balloons grouped by the view their leader reaches, with the closest centre spacing, and flags as
		ISSUE: balloons that overlap, balloon leaders that cross, a balloon or dimension overlapping another annotation
		or a table, and anything outside the border.

		Read only. A drawing this opens is closed without saving, and a drawing already open is measured and left as it
		was. Use it to compare layout settings across several generated drawings rather than judging a PDF by eye.
		""")]
	public static Task<object> DrawingLayout(
		BridgeClient bridge,
		[Description("Full path of the .idw. Omit to measure the active document, which must be a drawing.")] string? drawingPath = null,
		[Description("Balloon diameter in inches, when the style scales to its text and the estimate is not good enough.")] double? balloonDiameterInches = null,
		[Description("List every dimension and balloon, not only the summary and the issues. Default true.")] bool includeDetails = true,
		CancellationToken cancellationToken = default)
	{
		string code = _drawingLayoutSnippet
			.Replace("__DRAWING_PATH__", CSharpLiteral.String(drawingPath), StringComparison.Ordinal)
			.Replace("__BALLOON_DIAMETER__", (balloonDiameterInches ?? -1).ToString("R", CultureInfo.InvariantCulture), StringComparison.Ordinal)
			.Replace("__DETAILS__", includeDetails ? "true" : "false", StringComparison.Ordinal);

		return SafeAsync(() => bridge.InvokeAsync<Contracts.Models.ExecutionResult>(
			BridgeOperations.EvalCSharp,
			// Read only, and it never touches a document it did not open itself beyond reading it.
			new ExecuteRequest(code, DocumentName: null, AllowUnsavedChanges: true),
			cancellationToken));
	}
}
