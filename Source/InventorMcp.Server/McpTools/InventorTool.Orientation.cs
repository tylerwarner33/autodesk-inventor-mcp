using System.ComponentModel;

using InventorMcp.Contracts;
using InventorMcp.Server.Bridge;

using ModelContextProtocol.Server;

namespace InventorMcp.Server.McpTools;

internal static partial class InventorTool
{
	/// <summary>
	/// 	Asks the camera where each ViewCube face points, then restores the user's view.
	/// </summary>
	/// <remarks>
	/// 	Composed on top of the execution operation rather than added as a new bridge operation,
	/// 	so it needs no add-in rebuild and therefore no Inventor restart.
	/// </remarks>
	private const string _orientationSnippet = """
		Camera camera = Application.ActiveView.Camera;

		Point originalEye = camera.Eye;
		Point originalTarget = camera.Target;
		UnitVector originalUp = camera.UpVector;

		string Axis(double x, double y, double z)
		{
			if (Math.Abs(x) > 0.99) return x > 0 ? "+X" : "-X";
			if (Math.Abs(y) > 0.99) return y > 0 ? "+Y" : "-Y";
			if (Math.Abs(z) > 0.99) return z > 0 ? "+Z" : "-Z";
			return "(" + x.ToString("F3") + ", " + y.ToString("F3") + ", " + z.ToString("F3") + ")";
		}

		string Describe(string label, ViewOrientationTypeEnum orientation)
		{
			camera.ViewOrientationType = orientation;
			camera.Apply();

			Point eye = camera.Eye;
			Point target = camera.Target;

			Vector direction = Application.TransientGeometry.CreateVector(
				eye.X - target.X, eye.Y - target.Y, eye.Z - target.Z);
			direction.Normalize();

			UnitVector up = camera.UpVector;

			return label + " outward=" + Axis(direction.X, direction.Y, direction.Z)
				+ " up=" + Axis(up.X, up.Y, up.Z);
		}

		Log(Describe("Top", ViewOrientationTypeEnum.kTopViewOrientation));
		Log(Describe("Bottom", ViewOrientationTypeEnum.kBottomViewOrientation));
		Log(Describe("Front", ViewOrientationTypeEnum.kFrontViewOrientation));
		Log(Describe("Back", ViewOrientationTypeEnum.kBackViewOrientation));
		Log(Describe("Right", ViewOrientationTypeEnum.kRightViewOrientation));
		Log(Describe("Left", ViewOrientationTypeEnum.kLeftViewOrientation));

		camera.Eye = originalEye;
		camera.Target = originalTarget;
		camera.UpVector = originalUp;
		camera.Apply();

		return "Resolved from the live camera; the user's view was restored.";
		""";

	[McpServerTool(Name = "inventor_orientation")]
	[Description("""
		Reports which world direction each ViewCube face points in, for the active document.

		Call this before writing any geometry that refers to a named face such as "the top face". The ViewCube can be
		redefined per document, so its mapping to world axes is not a constant and must not be assumed. On an
		unmodified part Top is +Y, Front is +Z and Right is +X, meaning Y is the vertical axis rather than Z.

		The user's view is changed briefly and then restored. The model itself is not modified.
		""")]
	public static Task<object> Orientation(
		BridgeClient bridge,
		[Description("Display name or full path of the document. Omit to use the active document.")] string? documentName = null,
		CancellationToken cancellationToken = default) =>
		SafeAsync(() => bridge.InvokeAsync<Contracts.Models.ExecutionResult>(
			BridgeOperations.EvalCSharp,
			// The unsaved guard protects the model from damage. This snippet only moves the camera and puts it back,
			// so refusing it on a dirty document would block a read for no benefit.
			new ExecuteRequest(_orientationSnippet, documentName, AllowUnsavedChanges: true),
			cancellationToken));
}
