using System.ComponentModel;
using System.Text.Json;

using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

/// <remarks>
/// 	Read tools for the model: features, pattern elements, holes and styles. Canned snippets through the execution
/// 	operation, so they need no add-in rebuild. See <c>Docs/Research/Usage-Findings-And-Knowledge-Delivery.md</c>, R1.6.
/// </remarks>
internal static partial class InventorTool
{
	/// <summary>
	/// 	Reads a length in the document's length unit, with the unit name.
	/// </summary>
	private const string _documentLengthHelper = """
		UnitsOfMeasure __units = document.UnitsOfMeasure;
		string LengthUnit() => __units.GetStringFromType(__units.LengthUnits);
		// + 0.0 turns -0 into 0.
		double Length(double centimetres) => Math.Round((double)__units.ConvertUnits(centimetres, UnitsTypeEnum.kCentimeterLengthUnits, __units.LengthUnits), 6) + 0.0;

		""";

	[McpServerTool(Name = "inventor_features", ReadOnly = true)]
	[Description("""
		Lists the features of a part or an assembly in browser order: the name, type, suppressed flag and health of each,
		and its parameters with their expressions. For a pattern or a mirror, also the count and spacing expressions,
		the direction or axis entity, the parent features and the element count. Use it to read a model before a change,
		and after a change to check which feature is sick.
		""")]
	public static Task<object> Features(
		BridgeClient bridge,
		[Description("Display name or full path of an open document, or the full path of a file. Omit to use the active document.")] string? documentName = null,
		[Description("Include each feature's parameters. Default true.")] bool includeParameters = true,
		CancellationToken cancellationToken = default) =>
		SafeAsync(async () => await RunJsonSnippetAsync(bridge, _findToolDocument + $$"""
			Document document = FindToolDocument({{CSharpLiteral.String(documentName)}});
			bool includeParameters = {{(includeParameters ? "true" : "false")}};
			System.Collections.IEnumerable all = document switch
			{
				PartDocument part => part.ComponentDefinition.Features,
				AssemblyDocument assembly => assembly.ComponentDefinition.Features,
				_ => throw new ArgumentException($"A {document.DocumentType} has no features.")
			};

			static string Describe(object? entity)
			{
				if (entity is null)
					return "none";
				dynamic value = entity;
				try { return $"{(string)value.Name} ({((ObjectTypeEnum)value.Type).ToString().Replace("Object", "")})"; }
				catch (Exception) { }
				try { return ((ObjectTypeEnum)value.Type).ToString(); } catch (Exception) { return entity.GetType().Name; }
			}

			static string? Expression(dynamic parameterOrValue)
			{
				// An unused direction gives the COM code DISP_E_PARAMNOTFOUND as an int, not a parameter.
				if (parameterOrValue is null || parameterOrValue is int code && code == unchecked((int)0x80020004))
					return null;
				try { return (string)parameterOrValue.Expression; } catch (Exception) { }
				try { return Convert.ToString(parameterOrValue, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { return null; }
			}

			static List<string> Parents(dynamic definition)
			{
				List<string> names = new();
				try { foreach (object parent in definition.ParentFeatures) names.Add(Describe(parent)); } catch (Exception) { }
				return names;
			}

			List<object> features = new();
			foreach (object item in all)
			{
				if (item is not PartFeature feature)
					continue;

				List<object>? parameters = null;
				if (includeParameters)
				{
					parameters = new();
					try { foreach (Parameter parameter in ((dynamic)feature).Parameters) parameters.Add(new { name = parameter.Name, expression = parameter.Expression }); }
					catch (Exception) { }
				}

				object? pattern = null;
				dynamic dynamicFeature = feature;
				try
				{
					switch (feature)
					{
						case RectangularPatternFeature:
						{
							dynamic definition = dynamicFeature.Definition;
							pattern = new
							{
								kind = "rectangular",
								xCount = Expression(definition.XCount),
								xSpacing = Expression(definition.XSpacing),
								xDirection = Describe(definition.XDirectionEntity),
								yCount = Expression(definition.YCount),
								ySpacing = Expression(definition.YSpacing),
								yDirection = Describe(definition.YDirectionEntity),
								parents = Parents(definition),
								elements = (int)dynamicFeature.PatternElements.Count
							};
							break;
						}
						case CircularPatternFeature:
						{
							dynamic definition = dynamicFeature.Definition;
							pattern = new
							{
								kind = "circular",
								count = Expression(definition.Count),
								angle = Expression(definition.Angle),
								axis = Describe(definition.AxisEntity),
								parents = Parents(definition),
								elements = (int)dynamicFeature.PatternElements.Count
							};
							break;
						}
						case MirrorFeature:
						{
							dynamic definition = dynamicFeature.Definition;
							pattern = new
							{
								kind = "mirror",
								plane = Describe(definition.MirrorPlaneEntity),
								parents = Parents(definition),
								elements = (int)dynamicFeature.PatternElements.Count
							};
							break;
						}
					}
				}
				catch (Exception exception)
				{
					pattern = new { error = exception.Message };
				}

				features.Add(new
				{
					name = feature.Name,
					type = feature.Type.ToString().Replace("Object", "").TrimStart('k'),
					suppressed = feature.Suppressed,
					health = feature.HealthStatus.ToString().Replace("HealthStatus", "").TrimStart('k'),
					parameters,
					pattern
				});
			}

			string json = System.Text.Json.JsonSerializer.Serialize(new { document = document.DisplayName, count = features.Count, features });
			CloseDocumentsOpenedHere();
			return json;
			""", cancellationToken).ConfigureAwait(false));

	[McpServerTool(Name = "inventor_pattern_elements", ReadOnly = true)]
	[Description("""
		Lists the elements of one pattern or mirror feature: the index, the suppressed flag, the face count, and the
		transform of each element as a translation and a rotation matrix. Translations are in the document's length
		unit. Use it to check that a pattern placed its elements where the design needs them.
		""")]
	public static Task<object> PatternElements(
		BridgeClient bridge,
		[Description("The name of the pattern or mirror feature, as inventor_features lists it.")] string featureName,
		[Description("Display name or full path of an open document, or the full path of a file. Omit to use the active document.")] string? documentName = null,
		CancellationToken cancellationToken = default) =>
		SafeAsync(async () => await RunJsonSnippetAsync(bridge, _findToolDocument + $$"""
			Document document = FindToolDocument({{CSharpLiteral.String(documentName)}});
			{{_documentLengthHelper}}
			string featureName = {{CSharpLiteral.String(featureName)}};
			System.Collections.IEnumerable all = document switch
			{
				PartDocument part => part.ComponentDefinition.Features,
				AssemblyDocument assembly => assembly.ComponentDefinition.Features,
				_ => throw new ArgumentException($"A {document.DocumentType} has no features.")
			};

			PartFeature feature = all.OfType<PartFeature>().FirstOrDefault(candidate => candidate.Name == featureName)
				?? throw new ArgumentException($"'{document.DisplayName}' has no feature named '{featureName}'. Call inventor_features for the names.");

			dynamic elements;
			try { elements = ((dynamic)feature).PatternElements; }
			catch (Exception) { throw new ArgumentException($"'{featureName}' is a {feature.Type}, which has no pattern elements."); }

			List<object> result = new();
			foreach (FeaturePatternElement element in elements)
			{
				Matrix transform = element.Transform;
				double[] cells = new double[16];
				transform.GetMatrixData(ref cells);
				int faces = 0;
				try { faces = element.Faces.Count; } catch (Exception) { }

				result.Add(new
				{
					index = element.Index,
					suppressed = element.Suppressed,
					faces,
					translation = new[] { Length(transform.Translation.X), Length(transform.Translation.Y), Length(transform.Translation.Z) },
					rotation = new[]
					{
						new[] { Math.Round(cells[0], 6), Math.Round(cells[1], 6), Math.Round(cells[2], 6) },
						new[] { Math.Round(cells[4], 6), Math.Round(cells[5], 6), Math.Round(cells[6], 6) },
						new[] { Math.Round(cells[8], 6), Math.Round(cells[9], 6), Math.Round(cells[10], 6) }
					}
				});
			}

			string json = System.Text.Json.JsonSerializer.Serialize(new { document = document.DisplayName, feature = feature.Name, unit = LengthUnit(), count = result.Count, elements = result });
			CloseDocumentsOpenedHere();
			return json;
			""", cancellationToken).ConfigureAwait(false));

	[McpServerTool(Name = "inventor_hole_check", ReadOnly = true)]
	[Description("""
		Finds the holes in the solid bodies of a part from the geometry, not from the features, so it also finds a hole
		made by an extrude cut or a pattern. Groups them by diameter, with the axis direction and a point on the axis
		of each hole, in the document's length unit. A hole made of two half-cylinder faces counts once.

		Use it after a cut to check that the holes exist, with the diameter and at the positions the design needs.
		A cylindrical face that is convex (ex. a boss) is not a hole, and is counted separately.
		""")]
	public static Task<object> HoleCheck(
		BridgeClient bridge,
		[Description("Display name or full path of an open part, or the full path of a file. Omit to use the active document.")] string? documentName = null,
		[Description("The most hole positions to list for each diameter. Default 50.")] int maxPositions = 50,
		CancellationToken cancellationToken = default) =>
		SafeAsync(async () => await RunJsonSnippetAsync(bridge, _findToolDocument + $$"""
			Document document = FindToolDocument({{CSharpLiteral.String(documentName)}});
			{{_documentLengthHelper}}
			int maxPositions = {{(maxPositions <= 0 ? 50 : maxPositions)}};
			PartDocument part = document as PartDocument ?? throw new ArgumentException("inventor_hole_check reads a part.");
			const double tolerance = 1e-4;

			// One entry per axis line and radius: a hole can be two half-cylinder faces. Sweep is the angle the faces go
			// around the axis. Only a full circle is a hole: the inside of a sheet metal bend, a fillet or the end of a
			// slot is also a concave cylinder.
			List<(double Radius, double[] Axis, double[] Point, int Faces, double Sweep)> holes = new();
			int bosses = 0;

			static double SweepOf(Face face)
			{
				double sweep = 0;
				foreach (Edge edge in face.Edges)
				{
					if (edge.GeometryType == CurveTypeEnum.kCircleCurve)
						return 2 * Math.PI;
					if (edge.GeometryType == CurveTypeEnum.kCircularArcCurve)
						sweep = Math.Max(sweep, Math.Abs(((Arc3d)edge.Geometry).SweepAngle));
				}
				return sweep;
			}
			foreach (SurfaceBody body in part.ComponentDefinition.SurfaceBodies)
			{
				if (!body.IsSolid)
					continue;

				foreach (Face face in body.Faces)
				{
					if (face.SurfaceType != SurfaceTypeEnum.kCylinderSurface)
						continue;

					Cylinder cylinder = (Cylinder)face.Geometry;
					double[] axis = { cylinder.AxisVector.X, cylinder.AxisVector.Y, cylinder.AxisVector.Z };
					double[] origin = { cylinder.BasePoint.X, cylinder.BasePoint.Y, cylinder.BasePoint.Z };

					// A hole's outward normal points to its axis.
					double[] point = { face.PointOnFace.X, face.PointOnFace.Y, face.PointOnFace.Z };
					double[] normal = new double[3];
					face.Evaluator.GetNormalAtPoint(ref point, ref normal);
					double along = (point[0] - origin[0]) * axis[0] + (point[1] - origin[1]) * axis[1] + (point[2] - origin[2]) * axis[2];
					double[] foot = { origin[0] + along * axis[0], origin[1] + along * axis[1], origin[2] + along * axis[2] };
					double[] outward = { point[0] - foot[0], point[1] - foot[1], point[2] - foot[2] };
					if (outward[0] * normal[0] + outward[1] * normal[1] + outward[2] * normal[2] > 0)
					{
						bosses++;
						continue;
					}

					// The point on the axis nearest the middle of the face's range box.
					Box range = face.Evaluator.RangeBox;
					double[] middle = { (range.MinPoint.X + range.MaxPoint.X) / 2, (range.MinPoint.Y + range.MaxPoint.Y) / 2, (range.MinPoint.Z + range.MaxPoint.Z) / 2 };
					double t = (middle[0] - origin[0]) * axis[0] + (middle[1] - origin[1]) * axis[1] + (middle[2] - origin[2]) * axis[2];
					double[] centre = { origin[0] + t * axis[0], origin[1] + t * axis[1], origin[2] + t * axis[2] };

					int same = holes.FindIndex(hole =>
					{
						if (Math.Abs(hole.Radius - cylinder.Radius) > tolerance)
							return false;
						double dot = Math.Abs(hole.Axis[0] * axis[0] + hole.Axis[1] * axis[1] + hole.Axis[2] * axis[2]);
						if (dot < 1 - 1e-6)
							return false;
						double[] d = { centre[0] - hole.Point[0], centre[1] - hole.Point[1], centre[2] - hole.Point[2] };
						double lengthAlong = d[0] * axis[0] + d[1] * axis[1] + d[2] * axis[2];
						double off = Math.Sqrt(Math.Max(0, d[0] * d[0] + d[1] * d[1] + d[2] * d[2] - lengthAlong * lengthAlong));
						return off < tolerance;
					});

					double sweep = SweepOf(face);
					if (same >= 0)
						holes[same] = (holes[same].Radius, holes[same].Axis, holes[same].Point, holes[same].Faces + 1, holes[same].Sweep + sweep);
					else
						holes.Add((cylinder.Radius, axis, centre, 1, sweep));
				}
			}

			const double fullCircle = 2 * Math.PI * 0.999;
			var partial = holes
				.Where(hole => hole.Sweep < fullCircle)
				.GroupBy(hole => Math.Round(hole.Radius / tolerance) * tolerance)
				.OrderBy(group => group.Key)
				.Select(group => new { diameter = Length(group.Key * 2), count = group.Count(), largestSweepDegrees = Math.Round(group.Max(hole => hole.Sweep) * 180 / Math.PI, 1) })
				.ToList();
			holes = holes.Where(hole => hole.Sweep >= fullCircle).ToList();

			var groups = holes
				.GroupBy(hole => Math.Round(hole.Radius / tolerance) * tolerance)
				.OrderBy(group => group.Key)
				.Select(group => new
				{
					diameter = Length(group.Key * 2),
					count = group.Count(),
					holes = group.Take(maxPositions).Select(hole => new
					{
						point = hole.Point.Select(Length).ToArray(),
						axis = hole.Axis.Select(value => Math.Round(value, 6) + 0.0).ToArray(),
						faces = hole.Faces
					}).ToList()
				})
				.ToList();

			string json = System.Text.Json.JsonSerializer.Serialize(new
			{
				document = document.DisplayName,
				unit = LengthUnit(),
				holes = holes.Count,
				byDiameter = groups,
				// Concave cylinders that do not go all the way around: bends, fillets, slot ends, or a hole cut open.
				partialConcaveCylinders = partial,
				convexCylinders = bosses
			});
			CloseDocumentsOpenedHere();
			return json;
			""", cancellationToken).ConfigureAwait(false));

	/// <summary>
	/// 	Reads the styles of one document as JSON text, into a variable named after the prefix.
	/// </summary>
	private static string StylesSnippet(string variable, string? documentName) => $$"""
		string {{variable}};
		{
			Document document = FindToolDocument({{CSharpLiteral.String(documentName)}});
			List<object> styles = new();
			string? standard = null;
			if (document is DrawingDocument drawing)
			{
				standard = drawing.StylesManager.ActiveStandardStyle.Name;
				foreach (Style style in drawing.StylesManager.Styles)
				{
					Dictionary<string, object?> details = new();
					try
					{
						switch (style)
						{
							case TextStyle text:
								details["font"] = text.Font;
								details["fontSize"] = Math.Round(text.FontSize, 6);
								break;
							case BalloonStyle balloon:
								details["scaleToTextHeight"] = balloon.ScaleToTextHeight;
								details["balloonDiameter"] = Math.Round(balloon.BalloonDiameter, 6);
								details["textStyle"] = balloon.TextStyle.Name;
								break;
							case LeaderStyle leader:
								details["arrowheadType"] = leader.ArrowheadType.ToString();
								details["arrowheadSize"] = Math.Round(leader.ArrowheadSize, 6);
								break;
						}
					}
					catch (Exception exception)
					{
						details["error"] = exception.Message;
					}

					styles.Add(new
					{
						name = style.Name,
						type = style.StyleType.ToString().Replace("StyleType", "").TrimStart('k'),
						location = style.StyleLocation.ToString().Replace("StyleLocation", "").TrimStart('k'),
						upToDate = style.UpToDate,
						inUse = style.InUse,
						details
					});
				}
			}
			else if (document is PartDocument part)
			{
				if (part.ComponentDefinition is SheetMetalComponentDefinition sheetMetal)
				{
					standard = sheetMetal.ActiveSheetMetalStyle.Name;
					foreach (SheetMetalStyle style in sheetMetal.SheetMetalStyles)
						styles.Add(new { name = style.Name, type = "SheetMetal", location = "Local", upToDate = true, inUse = style.Name == standard, details = new Dictionary<string, object?> { ["thickness"] = style.Thickness } });
				}
				styles.Add(new { name = part.ActiveMaterial.DisplayName, type = "ActiveMaterial", location = "Local", upToDate = true, inUse = true, details = new Dictionary<string, object?>() });
				styles.Add(new { name = part.ActiveAppearance.DisplayName, type = "ActiveAppearance", location = "Local", upToDate = true, inUse = true, details = new Dictionary<string, object?>() });
			}
			else
			{
				throw new ArgumentException($"inventor_styles reads a drawing or a part, not a {document.DocumentType}.");
			}

			{{variable}} = System.Text.Json.JsonSerializer.Serialize(new { document = document.FullFileName.Length > 0 ? document.FullFileName : document.DisplayName, standard, styles });
		}

		""";

	[McpServerTool(Name = "inventor_styles", ReadOnly = true)]
	[Description("""
		Lists the styles of a drawing: the name, type, location (local, library or both), whether it is up to date with
		the library and whether it is in use, with the font and size of a text style, the diameter of a balloon style and
		the arrowhead of a leader style. For a part: the sheet metal styles, the material and the appearance.

		Give compareWith to diff against a second document: the styles in only one of them, and the styles whose
		location, up-to-date state or details differ. Use it to find why two generated drawings look different.
		""")]
	public static Task<object> Styles(
		BridgeClient bridge,
		[Description("Display name or full path of an open document, or the full path of a file. Omit to use the active document.")] string? documentName = null,
		[Description("A second document to compare with, as a display name or a full path.")] string? compareWith = null,
		CancellationToken cancellationToken = default) =>
		SafeAsync(async () =>
		{
			JsonElement result = await RunJsonSnippetAsync(bridge, _findToolDocument + StylesSnippet("first", documentName) +
				(compareWith is null ? "string? second = null;\n" : StylesSnippet("second", compareWith)) + """
				CloseDocumentsOpenedHere();
				return System.Text.Json.JsonSerializer.Serialize(new { first, second });
				""", cancellationToken).ConfigureAwait(false);

			if (result.TryGetProperty("first", out JsonElement firstText) is false)
				return (object)result;

			JsonElement first = JsonDocument.Parse(firstText.GetString()!).RootElement.Clone();

			return result.GetProperty("second").GetString() is string secondText
				? StyleDiff.Compare(first, JsonDocument.Parse(secondText).RootElement.Clone())
				: first;
		});
}
