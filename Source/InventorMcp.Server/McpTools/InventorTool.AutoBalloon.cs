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
	/// 	The sides of a view that a balloon can go on, in the order the snippet numbers them.
	/// </summary>
	private static readonly string[] _balloonSides = ["Left", "Right", "Bottom", "Top"];

	/// <summary>
	/// 	Balloons each part that one assembly view shows, after it deletes the balloons of that view.
	/// </summary>
	/// <remarks>
	/// 	Composed on top of the execution operation, so it needs no add-in rebuild. All changes are in one transaction,
	/// 	so one Undo removes them, and a failure or a scan over its time limit aborts it with no change.
	///
	/// 	The side and the attach point come from the part's outline in the view, and the balloons of one side are
	/// 	packed over the parts they name. The rules come from two production implementations. See
	/// 	<c>Docs/Research/Auto-Balloon.md</c>.
	/// </remarks>
	private const string _autoBalloonSnippet = """
		#nullable enable

		// One part file's balloon: the curve and point its leader attaches to, and where the balloon goes.
		sealed class BalloonPlan
		{
			public string File = "";
			public string Occurrence = "";
			public DrawingCurve Curve = null!;
			public double AttachX, AttachY, X, Y, Gap;
			public int Side;
			public string Item = "";
			public Balloon? Balloon;
		}

		string? drawingPath = __DRAWING_PATH__;
		string? sheetName = __SHEET__;
		string viewName = __VIEW__;
		string[] allowedSideNames = __SIDES__;
		double spacingInches = __SPACING__;
		double offsetInches = __OFFSET__;
		string? styleName = __STYLE__;
		bool oneBalloonPerItem = __ONE_PER_ITEM__;

		const double cmPerInch = 2.54;
		const int left = 0, right = 1, bottom = 2, top = 3;
		string[] sideNames = { "Left", "Right", "Bottom", "Top" };
		bool[] allowed = sideNames.Select(name => allowedSideNames.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
		double Inches(double cm) => Math.Round(cm / cmPerInch, 3);
		string FileName(string path) => System.IO.Path.GetFileName(path);

		System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

		DrawingDocument? drawing;
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
				// With the rules off, so an open trigger does not run. Visible, and left open, so the user can check and save.
				dynamic? automation = null;
				bool? rulesWereEnabled = null;
				try { automation = ILogicAutomation(); rulesWereEnabled = (bool)automation.RulesEnabled; automation.RulesEnabled = false; }
				catch (Exception) { }

				try { drawing = (DrawingDocument)Application.Documents.Open(drawingPath, OpenVisible: true); }
				finally { if (rulesWereEnabled is bool previous) automation!.RulesEnabled = previous; }
				Log($"Opened '{drawing.DisplayName}'. It stays open, so you can check the balloons and save.");
			}
		}

		Sheet sheet = sheetName is null
			? drawing.ActiveSheet
			: drawing.Sheets.OfType<Sheet>().FirstOrDefault(candidate => string.Equals(candidate.Name, sheetName, StringComparison.OrdinalIgnoreCase))
				?? throw new ArgumentException($"'{drawing.DisplayName}' has no sheet named '{sheetName}'. Sheets: {string.Join(", ", drawing.Sheets.OfType<Sheet>().Select(candidate => candidate.Name))}.");
		if (!ReferenceEquals(sheet, drawing.ActiveSheet))
			sheet.Activate();

		// Two views of one sheet can have the same name, so a view is also known by the label that inventor_drawing_layout gives.
		List<DrawingView> views = [.. sheet.DrawingViews.OfType<DrawingView>()];
		Dictionary<DrawingView, string> labels = new();
		foreach (var sameName in views.GroupBy(candidate => candidate.Name))
		{
			int number = 0;
			foreach (DrawingView candidate in sameName)
				labels[candidate] = sameName.Count() == 1 ? candidate.Name : $"{candidate.Name} #{++number}";
		}

		DrawingView view = views.FirstOrDefault(candidate => string.Equals(labels[candidate], viewName, StringComparison.OrdinalIgnoreCase))
			?? throw new ArgumentException($"Sheet '{sheet.Name}' has no view '{viewName}'. Views: {string.Join(", ", views.Select(candidate => labels[candidate]))}.");

		AssemblyDocument assembly = view.ReferencedDocumentDescriptor?.ReferencedDocument as AssemblyDocument
			?? throw new InvalidOperationException($"View '{labels[view]}' does not show an assembly. Balloons name assembly components.");

		BalloonStyle? style = null;
		if (styleName is not null)
		{
			style = drawing.StylesManager.BalloonStyles.OfType<BalloonStyle>().FirstOrDefault(candidate => string.Equals(candidate.Name, styleName, StringComparison.OrdinalIgnoreCase))
				?? throw new ArgumentException($"'{drawing.DisplayName}' has no balloon style '{styleName}'. Styles: {string.Join(", ", drawing.StylesManager.BalloonStyles.OfType<BalloonStyle>().Select(candidate => candidate.Name))}.");
		}

		// A balloon has no range box in the API. A style that scales to its text is about 3 times the text height.
		BalloonStyle sizeStyle = style ?? drawing.StylesManager.ActiveStandardStyle.ActiveObjectDefaults.BalloonStyle;
		double diameter = sizeStyle.ScaleToTextHeight ? sizeStyle.TextStyle.FontSize * 3.0 : sizeStyle.BalloonDiameter;
		double spacing = spacingInches > 0 ? spacingInches * cmPerInch : diameter + 0.06 * cmPerInch;
		double offset = offsetInches > 0 ? offsetInches * cmPerInch : diameter * 1.5;

		double viewLeft = view.Left, viewRight = view.Left + view.Width, viewTop = view.Top, viewBottom = view.Top - view.Height;

		// The room on each side, to the border (else the sheet edge) or to another view beside that side. A side with
		// no room for a balloon is not used, so a balloon never goes off the sheet or onto another view.
		Box2d? border = null;
		try { border = sheet.Border?.RangeBox; } catch (Exception) { }
		double[] room =
		{
			viewLeft - (border?.MinPoint.X ?? 0),
			(border?.MaxPoint.X ?? sheet.Width) - viewRight,
			viewBottom - (border?.MinPoint.Y ?? 0),
			(border?.MaxPoint.Y ?? sheet.Height) - viewTop
		};
		foreach (DrawingView other in views.Where(other => !ReferenceEquals(other, view)))
		{
			double otherLeft = other.Left, otherRight = other.Left + other.Width, otherTop = other.Top, otherBottom = other.Top - other.Height;
			bool besideVertically = otherBottom < viewTop && otherTop > viewBottom;
			bool besideHorizontally = otherLeft < viewRight && otherRight > viewLeft;
			if (besideVertically && otherRight <= viewLeft) room[left] = Math.Min(room[left], viewLeft - otherRight);
			if (besideVertically && otherLeft >= viewRight) room[right] = Math.Min(room[right], otherLeft - viewRight);
			if (besideHorizontally && otherTop <= viewBottom) room[bottom] = Math.Min(room[bottom], viewBottom - otherTop);
			if (besideHorizontally && otherBottom >= viewTop) room[top] = Math.Min(room[top], otherBottom - viewTop);
		}

		bool[] requested = allowed;
		allowed = Enumerable.Range(0, 4).Select(side => requested[side] && room[side] >= offset + diameter / 2).ToArray();
		if (!allowed.Any(usable => usable))
		{
			allowed = requested;
			Log($"No allowed side has room for a balloon ({string.Join(", ", Enumerable.Range(0, 4).Where(side => requested[side]).Select(side => $"{sideNames[side]} {Inches(room[side])} in"))}), so balloons can go off the sheet or onto another view. Move the view, or give a smaller offsetInches.");
		}
		else if (allowed.SequenceEqual(requested) is false)
		{
			Log($"Not used, because there is no room for a balloon: {string.Join(", ", Enumerable.Range(0, 4).Where(side => requested[side] && !allowed[side]).Select(side => $"{sideNames[side]} ({Inches(room[side])} in)"))}.");
		}

		static Point2d? TryPoint(Func<Point2d> read)
		{
			try { return read(); }
			catch (Exception) { return null; }
		}

		static bool Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
		{
			static double Orient((double X, double Y) p, (double X, double Y) q, (double X, double Y) r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
			double d1 = Orient(c, d, a), d2 = Orient(c, d, b), d3 = Orient(a, b, c), d4 = Orient(a, b, d);
			return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
		}

		bool SameView(DrawingView? candidate) =>
			candidate is not null && (ReferenceEquals(candidate, view)
				|| (candidate.Name == view.Name && Math.Abs(candidate.Left - view.Left) < 1e-6 && Math.Abs(candidate.Top - view.Top) < 1e-6));

		// Selects the side for one occurrence and the curve its leader attaches to, or null when the view shows none of it.
		BalloonPlan? Plan(ComponentOccurrence occurrence, string file)
		{
			DrawingCurvesEnumerator? curves;
			try { curves = view.get_DrawingCurves(occurrence); }
			catch (Exception) { return null; }
			if (curves is null || curves.Count == 0)
				return null;

			double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
			List<(DrawingCurve Curve, double X, double Y)> points = new();
			foreach (DrawingCurve curve in curves)
			{
				try
				{
					Box2d range = curve.Evaluator2D.RangeBox;
					minX = Math.Min(minX, range.MinPoint.X); maxX = Math.Max(maxX, range.MaxPoint.X);
					minY = Math.Min(minY, range.MinPoint.Y); maxY = Math.Max(maxY, range.MaxPoint.Y);
				}
				catch (Exception) { }

				// MidPoint and CenterPoint can return null with no exception (ex. MidPoint of a full circle), so chain on null.
				Point2d? point = TryPoint(() => curve.MidPoint) ?? TryPoint(() => curve.CenterPoint) ?? TryPoint(() => curve.StartPoint);
				if (point is not null)
					points.Add((curve, point.X, point.Y));
			}
			if (points.Count == 0 || minX > maxX)
				return null;

			// A part "touches" a side within a fraction of the view size, because brackets and fasteners stick out.
			double tolerance = 0.08 * Math.Min(view.Width, view.Height);
			double[] gaps = { minX - viewLeft, viewRight - maxX, minY - viewBottom, viewTop - maxY };
			bool Touches(int side) => allowed[side] && gaps[side] <= tolerance;
			bool wide = (maxX - minX) >= (maxY - minY);

			int? chosen = null;
			if (gaps[left] <= tolerance && gaps[right] <= tolerance && (allowed[left] || allowed[right]))
			{
				// A part that spans the full width, ex. a stack of panel strips.
				chosen = allowed[left] ? left : right;
			}
			else
			{
				// The sides parallel to the part's long axis first, then the other sides it touches, then the nearest side.
				int[] order = wide ? new[] { bottom, top, left, right } : new[] { left, right, bottom, top };
				foreach (int[] tier in new[] { order.Take(2).ToArray(), order.Skip(2).ToArray() })
				{
					foreach (int side in tier.Where(Touches))
						if (chosen is null || gaps[side] < gaps[chosen.Value])
							chosen = side;
					if (chosen is not null)
						break;
				}
				chosen ??= Enumerable.Range(0, 4).Where(side => allowed[side]).OrderBy(side => gaps[side]).First();
			}
			int best = chosen.Value;

			// The curve nearest that side. A point far from the middle of the part costs a little, so a long outside
			// face wins over a tab at one end.
			bool vertical = best is left or right;
			double middle = vertical ? (minY + maxY) / 2 : (minX + maxX) / 2;
			double DistanceToSide(double x, double y) => best switch { left => x - viewLeft, right => viewRight - x, bottom => y - viewBottom, _ => viewTop - y };
			var attach = points.OrderBy(p => DistanceToSide(p.X, p.Y) + 0.1 * Math.Abs((vertical ? p.Y : p.X) - middle)).First();

			return new BalloonPlan
			{
				File = file,
				Occurrence = occurrence.Name,
				Curve = attach.Curve,
				AttachX = attach.X,
				AttachY = attach.Y,
				Side = best,
				Gap = gaps[best]
			};
		}

		// Packs the balloons of each side over the parts they name, in the order of their attach points, then swaps
		// neighbours whose leaders cross. A group too long for the side is spread over the side's full length.
		int crowdedSides = 0;
		void Arrange(List<BalloonPlan> plans)
		{
			crowdedSides = 0;
			foreach (var group in plans.GroupBy(plan => plan.Side))
			{
				bool horizontal = group.Key is bottom or top;
				double low = horizontal ? viewLeft : viewBottom, high = horizontal ? viewRight : viewTop;
				List<BalloonPlan> ordered = [.. group.OrderBy(plan => horizontal ? plan.AttachX : plan.AttachY)];
				int count = ordered.Count;
				double packed = spacing * (count - 1);

				double step, start;
				if (packed <= high - low)
				{
					// The median start gives the least total leader length at a fixed spacing and order.
					List<double> residuals = [.. ordered.Select((plan, index) => (horizontal ? plan.AttachX : plan.AttachY) - spacing * index).OrderBy(value => value)];
					int middle = residuals.Count / 2;
					double median = residuals.Count % 2 == 1 ? residuals[middle] : (residuals[middle - 1] + residuals[middle]) / 2;
					step = spacing;
					start = Math.Clamp(median, low, high - packed);
				}
				else
				{
					crowdedSides++;
					step = (high - low) / count;
					start = low + step / 2;
				}

				double across = group.Key switch { left => viewLeft - offset, right => viewRight + offset, bottom => viewBottom - offset, _ => viewTop + offset };
				for (int index = 0; index < count; index++)
				{
					double along = start + step * index;
					(ordered[index].X, ordered[index].Y) = horizontal ? (along, across) : (across, along);
				}

				for (int pass = 0; pass < count; pass++)
				{
					bool swapped = false;
					for (int index = 0; index + 1 < count; index++)
					{
						BalloonPlan first = ordered[index], second = ordered[index + 1];
						if (!Cross((first.X, first.Y), (first.AttachX, first.AttachY), (second.X, second.Y), (second.AttachX, second.AttachY)))
							continue;
						(first.X, first.Y, second.X, second.Y) = (second.X, second.Y, first.X, first.Y);
						(ordered[index], ordered[index + 1]) = (second, first);
						swapped = true;
					}
					if (!swapped)
						break;
				}
			}
		}

		Transaction transaction = Application.TransactionManager.StartTransaction((_Document)drawing, "Auto Balloon");
		bool committed = false;
		try
		{
			// 1. Delete the balloons of this view. Keep the item numbers of the other views, so an item is ballooned once.
			int deleted = 0;
			HashSet<string> items = new(StringComparer.OrdinalIgnoreCase);
			foreach (Balloon balloon in sheet.Balloons.OfType<Balloon>().ToList())
			{
				DrawingView? parent = null;
				try { parent = balloon.ParentView; } catch (Exception) { }
				if (SameView(parent))
				{
					balloon.Delete();
					deleted++;
					continue;
				}

				if (oneBalloonPerItem)
					foreach (BalloonValueSet valueSet in balloon.BalloonValueSets)
						try { items.Add(valueSet.ItemNumber); } catch (Exception) { }
			}

			// 2. The part occurrences. Suppressed is read first, because Definition of a suppressed occurrence gives E_FAIL.
			// Every sub-assembly is walked, whatever its BOM structure: its parts still show, and the item-number check
			// below removes the repeats that a sub-assembly with its own BOM row causes.
			List<ComponentOccurrence> parts = new();
			int unreadable = 0;
			void Collect(System.Collections.IEnumerable occurrences)
			{
				foreach (ComponentOccurrence occurrence in occurrences)
				{
					try
					{
						if (occurrence.Suppressed || occurrence.Definition is VirtualComponentDefinition || occurrence.BOMStructure == BOMStructureEnum.kReferenceBOMStructure)
							continue;
						if (occurrence.DefinitionDocumentType == DocumentTypeEnum.kPartDocumentObject)
							parts.Add(occurrence);
						else if (occurrence.DefinitionDocumentType == DocumentTypeEnum.kAssemblyDocumentObject)
							Collect(occurrence.SubOccurrences);
					}
					catch (Exception) { unreadable++; }
				}
			}
			Collect(assembly.ComponentDefinition.Occurrences);

			// 3. One plan per part file. Of the first instances that show, the one nearest its side, for the shortest leader.
			ScriptDeadline deadline = StartDeadline(6);
			var files = parts.GroupBy(occurrence => ((Document)occurrence.Definition.Document).FullFileName, StringComparer.OrdinalIgnoreCase).ToList();
			List<BalloonPlan> plans = new();
			int notShown = 0, scanned = 0;
			foreach (var file in files)
			{
				if (deadline.Passed)
				{
					Log($"The scan stopped at its time limit after {scanned} of {files.Count} part files ({parts.Count} occurrences). Nothing was changed.");
					return new { error = "too-slow", message = $"The view shows too many parts to balloon in one call: {scanned} of {files.Count} part files were scanned in {watch.ElapsedMilliseconds} ms. Nothing was changed.", scanned, partFiles = files.Count };
				}
				scanned++;

				BalloonPlan? chosen = null;
				int tried = 0, shown = 0;
				foreach (ComponentOccurrence occurrence in file)
				{
					if (tried++ >= 12 || shown >= 3)
						break;
					if (Plan(occurrence, file.Key) is not BalloonPlan plan)
						continue;
					shown++;
					if (chosen is null || plan.Gap < chosen.Gap)
						chosen = plan;
				}

				if (chosen is null)
					notShown++;
				else
					plans.Add(chosen);
			}
			long scanMilliseconds = watch.ElapsedMilliseconds;

			// 4. Add the balloons. A balloon whose item already has one on this sheet is a repeat, and is deleted.
			Arrange(plans);
			TransientGeometry geometry = Application.TransientGeometry;
			List<BalloonPlan> kept = new();
			List<string> repeats = new(), failed = new();
			foreach (BalloonPlan plan in plans)
			{
				try
				{
					ObjectCollection leader = Application.TransientObjects.CreateObjectCollection();
					leader.Add(geometry.CreatePoint2d(plan.X, plan.Y));
					leader.Add(sheet.CreateGeometryIntent(plan.Curve, geometry.CreatePoint2d(plan.AttachX, plan.AttachY)));
					Balloon balloon = sheet.Balloons.Add(leader, Type.Missing, Type.Missing, Type.Missing, style is null ? Type.Missing : style, Type.Missing);

					plan.Item = string.Join(", ", balloon.BalloonValueSets.OfType<BalloonValueSet>().Select(valueSet => valueSet.ItemNumber));
					if (oneBalloonPerItem && !items.Add(plan.Item))
					{
						balloon.Delete();
						repeats.Add($"{FileName(plan.File)} (item {plan.Item})");
						continue;
					}

					plan.Balloon = balloon;
					kept.Add(plan);
				}
				catch (Exception exception) { failed.Add($"{FileName(plan.File)}: {exception.Message}"); }
			}

			// 5. Close the gaps that the removed balloons left.
			if (kept.Count < plans.Count)
			{
				Arrange(kept);
				foreach (BalloonPlan plan in kept)
					plan.Balloon!.Position = geometry.CreatePoint2d(plan.X, plan.Y);
			}

			transaction.End();
			committed = true;

			foreach (BalloonPlan plan in kept.OrderBy(plan => plan.Side).ThenBy(plan => plan.Side is bottom or top ? plan.X : plan.Y))
				Log($"Balloon {plan.Item} {sideNames[plan.Side]} at ({Inches(plan.X)}, {Inches(plan.Y)}) in: {FileName(plan.File)} ({plan.Occurrence})");
			if (crowdedSides > 0)
				Log($"{crowdedSides} side(s) had too many balloons to pack at {Inches(spacing)} in, so they are spread over the full side. They can overlap: allow more sides, or a smaller spacing.");

			return new
			{
				drawing = drawing.DisplayName,
				sheet = sheet.Name,
				view = labels[view],
				deletedFromView = deleted,
				placed = kept.Count,
				partFiles = files.Count,
				notShownInView = notShown,
				repeatsRemoved = repeats,
				failed,
				unreadableOccurrences = unreadable,
				sides = kept.GroupBy(plan => sideNames[plan.Side]).ToDictionary(group => group.Key, group => group.Count()),
				balloonDiameterInches = Inches(diameter),
				spacingInches = Inches(spacing),
				scanMilliseconds,
				totalMilliseconds = watch.ElapsedMilliseconds,
				undo = "One Undo in Inventor ('Auto Balloon') removes every change of this call.",
				next = "Check the sheet with inventor_drawing_layout, and look at the view with inventor_export_sheet_image."
			};
		}
		finally
		{
			if (!committed)
				transaction.Abort();
		}
		""";

	[McpServerTool(Name = "inventor_auto_balloon", Destructive = true)]
	[Description("""
		Balloons the parts that one assembly view shows. It first deletes every balloon of that view, also a balloon added
		by hand. Balloons of other views stay.

		Each part file gets one balloon, attached to the curve of the part nearest its side of the view. The tool selects
		the side from the part's outline: a side the part touches, parallel to its long axis, else the nearest side.
		Give 'sides' to allow only some sides. The balloons of a side are packed at 'spacingInches' over the parts they
		name, and neighbours whose leaders cross are swapped.

		Inventor numbers each balloon from its BOM row. When an item already has a balloon on the sheet (ex. in a view
		ballooned before, or a part of a sub-assembly that has its own row), the repeat is deleted. So the first view you
		balloon gets the item; balloon the main view first. Set oneBalloonPerItem false to keep repeats.

		All changes are one transaction: one Undo in Inventor removes them. A failure changes nothing. The call scans
		for at most 6 s, and a view with too many parts for that returns 'too-slow' with no change. Call it once for
		each view. The drawing stays open and is not saved.

		After it, check the sheet with inventor_drawing_layout and look at it with inventor_export_sheet_image. The
		'drawings' skill (inventor_skill) has the facts about balloons.
		""")]
	public static Task<object> AutoBalloon(
		BridgeClient bridge,
		[Description("The view name, ex. 'VIEW1'. When two views of the sheet have the same name, use the label from inventor_drawing_layout, ex. 'VIEW1 #2'.")] string viewName,
		[Description("Full path of the .idw. Omit to use the active document, which must be a drawing. A drawing that is not open is opened visible.")] string? drawingPath = null,
		[Description("The sheet name, ex. 'Sheet:1'. Omit to use the active sheet.")] string? sheetName = null,
		[Description("The sides the balloons can go on: 'Left', 'Right', 'Top', 'Bottom'. Omit to let the tool select a side for each part.")] string[]? sides = null,
		[Description("The name of a balloon style of the drawing. Omit to use the default style of the active standard.")] string? balloonStyle = null,
		[Description("The distance in inches between the centres of two balloons on one side. Default: the balloon diameter plus 0.06 in.")] double? spacingInches = null,
		[Description("The distance in inches from the edge of the view to the balloon centres. Default: 1.5 times the balloon diameter.")] double? offsetInches = null,
		[Description("Delete a balloon whose item number already has a balloon on the sheet. Default true.")] bool oneBalloonPerItem = true,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(viewName))
			return Task.FromResult<object>(new { error = "invalid-arguments", message = "Give the name of the view in viewName." });

		if (drawingPath is not null && Path.IsPathFullyQualified(drawingPath) is false)
			return Task.FromResult<object>(new { error = "invalid-arguments", message = $"'drawingPath' must be a full path, not '{drawingPath}'." });

		if (sides is not null && (sides.Length == 0 || sides.Any(side => _balloonSides.Contains(side, StringComparer.OrdinalIgnoreCase) is false)))
			return Task.FromResult<object>(new { error = "invalid-arguments", message = $"'sides' must hold one or more of {string.Join(", ", _balloonSides)}, not '{string.Join(", ", sides)}'." });

		if (spacingInches is <= 0 || offsetInches is <= 0)
			return Task.FromResult<object>(new { error = "invalid-arguments", message = "'spacingInches' and 'offsetInches' must be more than 0." });

		string[] allowedSides = sides ?? _balloonSides;

		Dictionary<string, string> values = new(StringComparer.Ordinal)
		{
			["__DRAWING_PATH__"] = CSharpLiteral.String(drawingPath),
			["__SHEET__"] = CSharpLiteral.String(sheetName),
			["__VIEW__"] = CSharpLiteral.String(viewName),
			["__SIDES__"] = $"new string[] {{ {string.Join(", ", allowedSides.Select(CSharpLiteral.String))} }}",
			["__SPACING__"] = (spacingInches ?? -1).ToString("R", CultureInfo.InvariantCulture),
			["__OFFSET__"] = (offsetInches ?? -1).ToString("R", CultureInfo.InvariantCulture),
			["__STYLE__"] = CSharpLiteral.String(balloonStyle),
			["__ONE_PER_ITEM__"] = oneBalloonPerItem ? "true" : "false"
		};

		// One pass, so a placeholder name inside an argument (ex. a view named '__SHEET__') is never replaced.
		string code = ScriptPrelude.Apply(DrawingLayoutPlaceholder().Replace(
			_autoBalloonSnippet,
			match => values.TryGetValue(match.Value, out string? value) ? value : match.Value));

		return SafeAsync(async () => await AddFailureContextAsync(
			bridge,
			await bridge.InvokeAsync<Contracts.Models.ExecutionResult>(
				BridgeOperations.EvalCSharp,
				// A generated drawing is dirty after an open or update. The transaction makes the change undoable.
				new ExecuteRequest(code, DocumentName: null, AllowUnsavedChanges: true),
				cancellationToken).ConfigureAwait(false),
			documentName: null,
			cancellationToken).ConfigureAwait(false));
	}
}
