using Inventor;

using InventorMcp.AddIn.Bridge;
using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;

namespace InventorMcp.AddIn.Operations;

internal sealed partial class InventorOperations
{
	private IReadOnlyList<DocumentProperty> GetProperties(PropertiesRequest request)
	{
		Document document = ResolveDocument(request.DocumentName);
		PropertySets sets = document.PropertySets;

		List<DocumentProperty> result = [];

		for (int setIndex = 1; setIndex <= sets.Count; setIndex++)
		{
			PropertySet set = sets[setIndex];

			if (string.IsNullOrWhiteSpace(request.SetName) is false
				&& string.Equals(set.Name, request.SetName, StringComparison.OrdinalIgnoreCase) is false)
			{
				continue;
			}

			for (int propertyIndex = 1; propertyIndex <= set.Count; propertyIndex++)
			{
				Property property = set[propertyIndex];

				result.Add(Describe(set.Name, property));
			}
		}

		return result;
	}

	private DocumentProperty SetProperty(SetPropertyRequest request)
	{
		Document document = ResolveDocument(request.DocumentName);
		PropertySets sets = document.PropertySets;

		PropertySet set;

		try
		{
			set = sets[request.SetName];
		}
		catch
		{
			throw new BridgeFailureException(
				BridgeErrorCodes.NotFound,
				$"'{document.DisplayName}' has no property set named '{request.SetName}'.");
		}

		Property property;

		try
		{
			property = set[request.Name];
		}
		catch
		{
			throw new BridgeFailureException(
				BridgeErrorCodes.NotFound,
				$"The set '{request.SetName}' has no property named '{request.Name}'.");
		}

		// A property write can raise a dialog, ex. when the value conflicts with a Vault managed property.
		using (SilentOperationScope.Enter(_inventor))
		{
			property.Value = request.Value;
		}

		return Describe(set.Name, property);
	}

	private static DocumentProperty Describe(string setName, Property property)
	{
		object? value = null;

		try
		{
			value = property.Value;
		}
		catch
		{
			// A property whose value Inventor cannot materialise still has a name worth reporting.
		}

		string? expression = null;

		try
		{
			expression = string.IsNullOrWhiteSpace(property.Expression) ? null : property.Expression;
		}
		catch
		{
			// Not every property supports an expression.
		}

		return new DocumentProperty(
			setName,
			property.Name,
			value?.ToString() ?? string.Empty,
			value?.GetType().Name ?? "Null",
			expression);
	}
}
