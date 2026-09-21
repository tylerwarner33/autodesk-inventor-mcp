using System.Collections;

using Inventor;

using InventorMcp.AddIn.Bridge;
using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;

namespace InventorMcp.AddIn.Operations;

internal sealed partial class InventorOperations
{
	private HealthInfo GetHealth(DocumentScopedRequest request)
	{
		Document document = ResolveDocument(request.DocumentName);

		List<FeatureHealth> sick = [];

		foreach (PartFeature feature in EnumerateFeatures(document))
		{
			HealthStatusEnum status = feature.HealthStatus;

			if (status is HealthStatusEnum.kUpToDateHealth)
				continue;

			sick.Add(new FeatureHealth(
				feature.Name,
				DescribeObjectType(feature.Type),
				DescribeHealth(status),
				feature.Suppressed,
				string.Empty));
		}

		// The Inventor error manager exposes its contents as one text blob rather than a collection,
		// so each line becomes an entry. There is no per entry severity to read.
		List<ErrorEntry> errors = [];
		ErrorManager errorManager = _inventor.ErrorManager;

		if (errorManager.HasErrors || errorManager.HasWarnings)
		{
			string severity = errorManager.HasErrors ? "Error" : "Warning";

			foreach (string line in (errorManager.AllMessages ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
				errors.Add(new ErrorEntry(severity, line, string.Empty));
		}

		return new HealthInfo(
			ToDocumentInfo(document),
			document.RequiresUpdate,
			sick,
			errors.Count,
			errors);
	}

	private DocumentInfo UpdateDocument(UpdateRequest request)
	{
		Document document = ResolveDocument(request.DocumentName);

		// A rebuild is the single most likely operation to raise a dialog,
		// ex. a style conflict against the template or a locked design view representation.
		using (SilentOperationScope.Enter(_inventor))
		{
			if (request.Full)
			{
				// Update2 keeps going past a failing feature and reports whether everything computed.
				_ = document.Update2(AcceptErrorsAndContinue: true);
			}
			else
			{
				document.Update();
			}
		}

		return ToDocumentInfo(document);
	}

	/// <summary>
	/// 	Yields the features of a part or assembly, and nothing for any other document type.
	/// </summary>
	/// <param name="document">
	/// 	The document to read.
	/// </param>
	/// <returns>
	/// 	The features, or an empty sequence.
	/// </returns>
	private static IEnumerable<PartFeature> EnumerateFeatures(Document document)
	{
		IEnumerable? features = document switch
		{
			PartDocument part => part.ComponentDefinition.Features,
			AssemblyDocument assembly => assembly.ComponentDefinition.Features,
			_ => null
		};

		if (features is null)
			yield break;

		foreach (object item in features)
		{
			// An assembly feature collection can hold entries that do not expose the part feature interface.
			if (item is PartFeature feature)
				yield return feature;
		}
	}

	private static string DescribeHealth(HealthStatusEnum status) => status switch
	{
		HealthStatusEnum.kUpToDateHealth => "UpToDate",
		HealthStatusEnum.kOutOfDateHealth => "OutOfDate",
		HealthStatusEnum.kSuppressedHealth => "Suppressed",
		HealthStatusEnum.kDriverLostHealth => "DriverLost",
		HealthStatusEnum.kUnknownHealth => "Unknown",
		HealthStatusEnum.kInErrorHealth => "InError",
		HealthStatusEnum.kCannotComputeHealth => "CannotCompute",
		HealthStatusEnum.kInconsistentHealth => "Inconsistent",
		HealthStatusEnum.kRedundantHealth => "Redundant",
		HealthStatusEnum.kInvalidLimitsHealth => "InvalidLimits",
		HealthStatusEnum.kDeletedHealth => "Deleted",
		HealthStatusEnum.kBeyondStopNodeHealth => "BeyondStopNode",
		HealthStatusEnum.kNewlyAddedHealth => "NewlyAdded",
		HealthStatusEnum.kJointDOFLockedHealth => "JointDegreesOfFreedomLocked",
		_ => status.ToString()
	};

	/// <summary>
	/// 	Turns an Inventor object type name into a readable one, ex. kExtrudeFeatureObject becomes ExtrudeFeature.
	/// </summary>
	/// <param name="objectType">
	/// 	The Inventor object type.
	/// </param>
	/// <returns>
	/// 	The trimmed name.
	/// </returns>
	private static string DescribeObjectType(ObjectTypeEnum objectType)
	{
		string name = objectType.ToString();

		if (name.StartsWith('k'))
			name = name[1..];

		if (name.EndsWith("Object", StringComparison.Ordinal))
			name = name[..^"Object".Length];

		return name;
	}
}
