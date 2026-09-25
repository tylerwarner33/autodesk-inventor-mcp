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
		double clearanceInches = __CLEARANCE__;
		string? sheetName = __SHEET__;

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
				// With the rules off, so an open trigger does not run. The open is often most of the wall clock time.
				dynamic? automation = null;
				bool? rulesWereEnabled = null;
				try { automation = ILogicAutomation(); rulesWereEnabled = (bool)automation.RulesEnabled; automation.RulesEnabled = false; }
				catch (Exception) { }

				System.Diagnostics.Stopwatch openWatch = System.Diagnostics.Stopwatch.StartNew();
				try { drawing = (DrawingDocument)Application.Documents.Open(drawingPath, OpenVisible: false); }
				finally { if (rulesWereEnabled is bool previous) automation!.RulesEnabled = previous; }
				openedHere = true;
				Log($"Opened the drawing in {openWatch.ElapsedMilliseconds} ms.");
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

		// A sheet that is not active gives E_FAIL for the range boxes of its tables, so it is activated for the measure.
		Sheet activeBefore = drawing.ActiveSheet;
		try
		{
			Sheet sheet = sheetName is null
				? drawing.ActiveSheet
				: drawing.Sheets.OfType<Sheet>().FirstOrDefault(candidate => string.Equals(candidate.Name, sheetName, StringComparison.OrdinalIgnoreCase))
					?? throw new ArgumentException($"'{drawing.DisplayName}' has no sheet named '{sheetName}'. Sheets: {string.Join(", ", drawing.Sheets.OfType<Sheet>().Select(candidate => candidate.Name))}.");
			if (!ReferenceEquals(sheet, activeBefore))
				sheet.Activate();
			Log($"Drawing '{drawing.DisplayName}', sheet '{sheet.Name}' {In(sheet.Width)} x {In(sheet.Height)} in. All values are inches from the sheet's lower left corner.");

			Box2d? border = null;
			try { border = sheet.Border?.RangeBox; } catch (Exception) { }
			if (border is not null)
				Log($"Border {Box(border.MinPoint.X, border.MinPoint.Y, border.MaxPoint.X, border.MaxPoint.Y)}");

			List<DrawingView> views = [.. sheet.DrawingViews.OfType<DrawingView>()];
			// Two views of one sheet can have the same name, so a view is known by a label that is unique.
			Dictionary<DrawingView, string> labels = new();
			foreach (var sameName in views.GroupBy(view => view.Name))
			{
				int number = 0;
				foreach (DrawingView view in sameName)
					labels[view] = sameName.Count() == 1 ? view.Name : $"{view.Name} #{++number}";
			}
			string Label(DrawingView view) => labels[view];

			foreach (DrawingView view in views)
				Log($"View '{Label(view)}' {Box(view.Left, view.Top - view.Height, view.Left + view.Width, view.Top)}, scale {view.Scale:0.#####}");

			string? ViewAt(double x, double y) =>
				views.FirstOrDefault(view => x >= view.Left && x <= view.Left + view.Width && y >= view.Top - view.Height && y <= view.Top) is DrawingView found ? Label(found) : null;

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

			// The file of the component that a drawing curve shows, or null for a curve with no single component.
			static string? ComponentOf(DrawingCurve curve)
			{
				try
				{
					dynamic geometry = curve.ModelGeometry;
					ComponentOccurrence? occurrence = geometry.ContainingOccurrence as ComponentOccurrence;
					return occurrence is null ? null : ((Document)occurrence.Definition.Document).FullFileName;
				}
				catch (Exception) { return null; }
			}

			List<(string Item, double X, double Y, double Diameter, string? View, List<(double X, double Y)> Path, string? Named, string? Attached)> balloons = [];
			// The arrowhead end of each leader, in the order of balloons.
			List<(double X, double Y)> tips = [];
			foreach (Balloon balloon in sheet.Balloons)
			{
				string item = "?";
				try { item = balloon.BalloonValueSets[1].ItemNumber; } catch (Exception) { }

				// The component that the balloon names (its BOM row), and the one its leader is attached to.
				string? named = null;
				string? attached = null;
				try
				{
					dynamic row = balloon.BalloonValueSets[1].ReferencedRow;
					named = ((Document)row.BOMRow.ComponentDefinitions[1].Document).FullFileName;
				}
				catch (Exception) { }

				BalloonStyle style = balloon.Style;
				double diameter = balloonDiameterOverrideInches > 0 ? balloonDiameterOverrideInches * cmPerInch
					: style.ScaleToTextHeight ? style.TextStyle.FontSize * 3.0
					: style.BalloonDiameter;

				// The nodes go from the balloon to the arrowhead. RootNode is the balloon end, not the arrowhead.
				List<(double X, double Y)> path = [(balloon.Position.X, balloon.Position.Y)];
				LeaderNode? arrowhead = null;
				foreach (LeaderNode node in balloon.Leader.AllNodes)
				{
					path.Add((node.Position.X, node.Position.Y));
					arrowhead = node;
				}

				try
				{
					if (arrowhead?.AttachedEntity is GeometryIntent intent && intent.Geometry is DrawingCurve curve)
						attached = ComponentOf(curve);
				}
				catch (Exception) { }

				string? view = ViewAt(path[^1].X, path[^1].Y);
				balloons.Add((item, balloon.Position.X, balloon.Position.Y, diameter, view, path, named, attached));
				tips.Add(path[^1]);
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

			// Arrowheads: a balloon is ambiguous when its arrowhead is near another arrowhead, another leader, or a curve of
			// a component that it does not name.
			double clearance = clearanceInches * cmPerInch;
			string FileName(string? path) => path is null ? "?" : System.IO.Path.GetFileName(path);

			static double ToSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
			{
				double dx = b.X - a.X, dy = b.Y - a.Y;
				double lengthSquared = dx * dx + dy * dy;
				double t = lengthSquared == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
				return Math.Sqrt(Math.Pow(p.X - (a.X + t * dx), 2) + Math.Pow(p.Y - (a.Y + t * dy), 2));
			}

			for (int i = 0; i < balloons.Count; i++)
			{
				if (balloons[i].Named is string named && balloons[i].Attached is string attachedTo && !string.Equals(named, attachedTo, StringComparison.OrdinalIgnoreCase))
					Issue($"the leader of balloon {balloons[i].Item} is attached to '{FileName(attachedTo)}', but the balloon names '{FileName(named)}'");

				for (int j = 0; j < balloons.Count; j++)
				{
					if (j == i)
						continue;

					double tipDistance = Math.Sqrt(Math.Pow(tips[i].X - tips[j].X, 2) + Math.Pow(tips[i].Y - tips[j].Y, 2));
					if (j > i && tipDistance < clearance)
						Issue($"the arrowheads of balloons {balloons[i].Item} and {balloons[j].Item} are {In(tipDistance)} in apart");

					double leaderDistance = double.MaxValue;
					for (int s = 1; s < balloons[j].Path.Count; s++)
						leaderDistance = Math.Min(leaderDistance, ToSegment(tips[i], balloons[j].Path[s - 1], balloons[j].Path[s]));
					if (leaderDistance < clearance && tipDistance >= clearance)
						Issue($"the arrowhead of balloon {balloons[i].Item} is {In(leaderDistance)} in from the leader of balloon {balloons[j].Item}");
				}
			}

			// The curves of other components near each arrowhead. A range box gate first, because a view can have many curves.
			ScriptDeadline curveDeadline = StartDeadline(6);
			bool curvesComplete = true;
			foreach (DrawingView view in views)
			{
				List<int> onView = Enumerable.Range(0, balloons.Count).Where(index => balloons[index].View == Label(view) && balloons[index].Named is not null).ToList();
				if (onView.Count == 0)
					continue;

				Dictionary<int, (double Distance, string File)> nearest = new();
				foreach (DrawingCurve curve in view.get_DrawingCurves(Type.Missing))
				{
					if (curveDeadline.Passed) { curvesComplete = false; break; }

					Box2d range = curve.Evaluator2D.RangeBox;
					List<int> near = onView.Where(index => tips[index].X > range.MinPoint.X - clearance && tips[index].X < range.MaxPoint.X + clearance
						&& tips[index].Y > range.MinPoint.Y - clearance && tips[index].Y < range.MaxPoint.Y + clearance).ToList();
					if (near.Count == 0 || ComponentOf(curve) is not string component)
						continue;

					foreach (int index in near.Where(index => !string.Equals(component, balloons[index].Named, StringComparison.OrdinalIgnoreCase)))
					{
						double distance = double.MaxValue;
						foreach (DrawingCurveSegment segment in curve.Segments)
						{
							object geometry = segment.Geometry;
							distance = Math.Min(distance, geometry switch
							{
								LineSegment2d line => ToSegment(tips[index], (line.StartPoint.X, line.StartPoint.Y), (line.EndPoint.X, line.EndPoint.Y)),
								Circle2d circle => Math.Abs(Math.Sqrt(Math.Pow(tips[index].X - circle.Center.X, 2) + Math.Pow(tips[index].Y - circle.Center.Y, 2)) - circle.Radius),
								// An arc is measured as its whole circle, which can only report too near, never too far.
								Arc2d arc => Math.Abs(Math.Sqrt(Math.Pow(tips[index].X - arc.Center.X, 2) + Math.Pow(tips[index].Y - arc.Center.Y, 2)) - arc.Radius),
								_ => 0
							});
						}

						if (distance < clearance && (!nearest.TryGetValue(index, out var best) || distance < best.Distance))
							nearest[index] = (distance, component);
					}
				}

				foreach ((int index, (double distance, string file)) in nearest)
					Issue($"the arrowhead of balloon {balloons[index].Item} is {In(distance)} in from '{FileName(file)}', which the balloon does not name ('{FileName(balloons[index].Named)}')");

				if (!curvesComplete)
					break;
			}
			if (!curvesComplete)
				Log("The check of arrowheads against other components stopped at its time limit, so it is not complete.");

			// View groups: each view with the annotations near it. The gaps between groups are what a spacing rule measures.
			string? NearestView(double x, double y) => views
				.OrderBy(view => Math.Pow(Math.Max(0, Math.Max(view.Left - x, x - (view.Left + view.Width))), 2) + Math.Pow(Math.Max(0, Math.Max(view.Top - view.Height - y, y - view.Top)), 2))
				.FirstOrDefault() is DrawingView closest ? Label(closest) : null;

			Dictionary<string, (double X1, double Y1, double X2, double Y2)> groups = views.ToDictionary(view => Label(view), view => (view.Left, view.Top - view.Height, view.Left + view.Width, view.Top));
			void Grow(string? view, double x1, double y1, double x2, double y2)
			{
				if (view is null || !groups.TryGetValue(view, out var extent))
					return;
				groups[view] = (Math.Min(extent.X1, x1), Math.Min(extent.Y1, y1), Math.Max(extent.X2, x2), Math.Max(extent.Y2, y2));
			}

			foreach (var box in boxes.Where(box => box.Kind == "dimension"))
				Grow(NearestView((box.X1 + box.X2) / 2, (box.Y1 + box.Y2) / 2), box.X1, box.Y1, box.X2, box.Y2);
			for (int i = 0; i < balloons.Count; i++)
				Grow(balloons[i].View ?? NearestView(tips[i].X, tips[i].Y), balloons[i].X - balloons[i].Diameter / 2, balloons[i].Y - balloons[i].Diameter / 2, balloons[i].X + balloons[i].Diameter / 2, balloons[i].Y + balloons[i].Diameter / 2);

			foreach ((string view, var extent) in groups)
				Log($"View group '{view}' with its annotations {Box(extent.X1, extent.Y1, extent.X2, extent.Y2)}");

			List<string> names = [.. groups.Keys];
			for (int i = 0; i < names.Count; i++)
				for (int j = i + 1; j < names.Count; j++)
				{
					var a = groups[names[i]];
					var b = groups[names[j]];
					double gapX = Math.Max(b.X1 - a.X2, a.X1 - b.X2);
					double gapY = Math.Max(b.Y1 - a.Y2, a.Y1 - b.Y2);
					if (gapX < 0 && gapY < 0)
						Issue($"view groups '{names[i]}' and '{names[j]}' overlap");
					else if (gapY < 0)
						Log($"Gap between view groups '{names[i]}' and '{names[j]}': {In(gapX)} in, side by side");
					else if (gapX < 0)
						Log($"Gap between view groups '{names[i]}' and '{names[j]}': {In(gapY)} in, one above the other");
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
			else if (!ReferenceEquals(drawing.ActiveSheet, activeBefore))
				activeBefore.Activate();
		}
		""";

	[McpServerTool(Name = "inventor_drawing_layout")]
	[Description("""
		Measures a drawing sheet and reports where its annotations collide: views, balloons, dimension text, parts lists,
		custom tables and the title block, in inches from the sheet's lower left corner.

		Reports balloons grouped by the view their leader reaches, with the closest centre spacing, and flags as
		ISSUE: balloons that overlap, balloon leaders that cross, a balloon or dimension overlapping another annotation
		or a table, and anything outside the border.

		Also flags an ambiguous balloon: its arrowhead is closer than the clearance to another arrowhead, to another
		leader, or to a curve of a component that the balloon does not name, or its leader is attached to a component
		other than the one it names. Reports each view with its dimensions and balloons as a view group, and the gap
		between groups side by side or one above the other.

		Read only. A drawing this opens is opened with iLogic rules off and closed without saving, and a drawing
		already open is measured and left as it was. Use it to compare layout settings across several generated
		drawings rather than judging a PDF by eye. Use inventor_export_sheet_image to look at a region it flags.
		""")]
	public static Task<object> DrawingLayout(
		BridgeClient bridge,
		[Description("Full path of the .idw. Omit to measure the active document, which must be a drawing.")] string? drawingPath = null,
		[Description("Balloon diameter in inches, when the style scales to its text and the estimate is not good enough.")] double? balloonDiameterInches = null,
		[Description("List every dimension and balloon, not only the summary and the issues. Default true.")] bool includeDetails = true,
		[Description("The least distance in inches from an arrowhead to another arrowhead, leader or component. Default 0.1.")] double clearanceInches = 0.1,
		[Description("The sheet name, ex. 'Sheet:1'. Omit to measure the active sheet.")] string? sheetName = null,
		CancellationToken cancellationToken = default)
	{
		string code = ScriptPrelude.Apply(_drawingLayoutSnippet
			.Replace("__DRAWING_PATH__", CSharpLiteral.String(drawingPath), StringComparison.Ordinal)
			.Replace("__BALLOON_DIAMETER__", (balloonDiameterInches ?? -1).ToString("R", CultureInfo.InvariantCulture), StringComparison.Ordinal)
			.Replace("__DETAILS__", includeDetails ? "true" : "false", StringComparison.Ordinal)
			.Replace("__SHEET__", CSharpLiteral.String(sheetName), StringComparison.Ordinal)
			.Replace("__CLEARANCE__", (clearanceInches > 0 ? clearanceInches : 0.1).ToString("R", CultureInfo.InvariantCulture), StringComparison.Ordinal));

		return SafeAsync(() => bridge.InvokeAsync<Contracts.Models.ExecutionResult>(
			BridgeOperations.EvalCSharp,
			// Read only, and it never touches a document it did not open itself beyond reading it.
			new ExecuteRequest(code, DocumentName: null, AllowUnsavedChanges: true),
			cancellationToken));
	}
}
