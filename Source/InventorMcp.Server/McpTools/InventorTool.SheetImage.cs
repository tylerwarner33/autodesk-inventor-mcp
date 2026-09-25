using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;
using InventorMcp.Server.Services;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

/// <remarks>
/// 	A transient camera on the sheet renders it with no window, so the drawing can stay invisible.
/// 	<c>Camera.SaveAsBitmap</c> ignores a camera change that was not applied, and a transient camera has no view to
/// 	apply it to, so the image comes from <c>CreateImageWithOptions</c> with <c>IncludeEdits</c>.
/// 	See <c>Docs/Tasks/Usage-Findings-Implementation-Plan.md</c>, Phase 4.
/// </remarks>
internal static partial class InventorTool
{
	private const int _defaultSheetImageWidth = 1600;

	private const int _maxSheetImageWidth = 4000;

	[McpServerTool(Name = "inventor_export_sheet_image", ReadOnly = true)]
	[Description("""
		Renders a drawing sheet, or a region of it, as a PNG image and returns it as image content, so you can look at
		the drawing with no PDF renderer. The region is in inches from the sheet's lower left corner, the same as
		inventor_drawing_layout reports.

		Read only. A drawing this opens is opened invisibly with iLogic rules off and closed with no save.
		Use inventor_drawing_layout for measurements. Use the image to see what the numbers cannot show.
		""")]
	public static async Task<IEnumerable<ContentBlock>> ExportSheetImage(
		BridgeClient bridge,
		[Description("Display name or full path of an open drawing, or the full path of a file. Omit to use the active document.")] string? documentName = null,
		[Description("The sheet name, ex. 'Sheet:1'. Omit to use the active sheet.")] string? sheetName = null,
		[Description("A region to render, as [x1, y1, x2, y2] in inches from the sheet's lower left corner. Omit for the whole sheet.")] double[]? region = null,
		[Description("The image width in pixels, 200 to 4000. Default 1600. The height follows the region.")] int widthPixels = _defaultSheetImageWidth,
		CancellationToken cancellationToken = default)
	{
		if (region is not null && (region.Length != 4 || region[2] <= region[0] || region[3] <= region[1]))
			return [Text(new { error = "invalid-arguments", message = "Give region as [x1, y1, x2, y2] with x2 > x1 and y2 > y1." })];

		string file = Path.Combine(Path.GetTempPath(), $"InventorMcp.Sheet.{Guid.NewGuid():N}.png");
		int width = Math.Clamp(widthPixels, 200, _maxSheetImageWidth);
		string regionLiteral = region is null
			? "null"
			: "new double[] { " + string.Join(", ", region.Select(static value => value.ToString("R", CultureInfo.InvariantCulture))) + " }";

		object result = await SafeAsync(async () => await RunJsonSnippetAsync(bridge, _findToolDocument + $$"""
			DrawingDocument drawing = FindToolDocument({{CSharpLiteral.String(documentName)}}) as DrawingDocument
				?? throw new ArgumentException("inventor_export_sheet_image renders a drawing.");
			string? sheetName = {{CSharpLiteral.String(sheetName)}};
			double[]? region = {{regionLiteral}};
			Sheet sheet = sheetName is null
				? drawing.ActiveSheet
				: drawing.Sheets.OfType<Sheet>().FirstOrDefault(candidate => string.Equals(candidate.Name, sheetName, StringComparison.OrdinalIgnoreCase))
					?? throw new ArgumentException($"'{drawing.DisplayName}' has no sheet named '{sheetName}'. Sheets: {string.Join(", ", drawing.Sheets.OfType<Sheet>().Select(candidate => candidate.Name))}.");

			const double cmPerInch = 2.54;
			double x1 = region is null ? 0 : region[0] * cmPerInch;
			double y1 = region is null ? 0 : region[1] * cmPerInch;
			double x2 = region is null ? sheet.Width : region[2] * cmPerInch;
			double y2 = region is null ? sheet.Height : region[3] * cmPerInch;

			TransientGeometry geometry = Application.TransientGeometry;
			Camera camera = Application.TransientObjects.CreateCamera();
			camera.SceneObject = sheet;
			camera.Perspective = false;
			camera.Target = geometry.CreatePoint((x1 + x2) / 2, (y1 + y2) / 2, 0);
			camera.Eye = geometry.CreatePoint((x1 + x2) / 2, (y1 + y2) / 2, 1);
			camera.UpVector = geometry.CreateUnitVector(0, 1, 0);
			camera.SetExtents(x2 - x1, y2 - y1);

			int width = {{width}};
			int height = Math.Max(1, (int)Math.Round(width * (y2 - y1) / (x2 - x1)));
			NameValueMap options = Application.TransientObjects.CreateNameValueMap();
			options.Add("IncludeEdits", true);
			options.Add("AntiAliasing", true);
			options.Add("TopBackgroundColor", Application.TransientObjects.CreateColor(255, 255, 255));
			object picture = camera.CreateImageWithOptions(width, height, options);

			// IPictureDisp.Handle is an HBITMAP as a 32-bit value. A dynamic read of it overflows, so read it by reflection.
			object handle = picture.GetType().InvokeMember("Handle", System.Reflection.BindingFlags.GetProperty, null, picture, null)!;
			using (System.Drawing.Image image = System.Drawing.Image.FromHbitmap(new IntPtr(Convert.ToInt32(handle))))
				image.Save({{CSharpLiteral.String(file)}}, System.Drawing.Imaging.ImageFormat.Png);

			return System.Text.Json.JsonSerializer.Serialize(new
			{
				document = drawing.DisplayName,
				sheet = sheet.Name,
				sheetInches = new[] { Math.Round(sheet.Width / cmPerInch, 3), Math.Round(sheet.Height / cmPerInch, 3) },
				regionInches = new[] { Math.Round(x1 / cmPerInch, 3), Math.Round(y1 / cmPerInch, 3), Math.Round(x2 / cmPerInch, 3), Math.Round(y2 / cmPerInch, 3) },
				pixels = new[] { width, height },
				file = {{CSharpLiteral.String(file)}}
			});
			""", cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

		try
		{
			if (result is JsonElement { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("file", out _) && File.Exists(file))
			{
				byte[] png = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);

				return
				[
					new TextContentBlock { Text = json.ToString() },
					ImageContentBlock.FromBytes(png, "image/png")
				];
			}

			return [Text(result)];
		}
		finally
		{
			File.Delete(file);
		}
	}

	private static TextContentBlock Text(object value) =>
		new() { Text = JsonSerializer.Serialize(value, BridgeProtocol.SerializerOptions) };
}
