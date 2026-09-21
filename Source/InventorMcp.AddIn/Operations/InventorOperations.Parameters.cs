using Inventor;

using InventorMcp.AddIn.Bridge;
using InventorMcp.Contracts;
using InventorMcp.Contracts.Models;

namespace InventorMcp.AddIn.Operations;

internal sealed partial class InventorOperations
{
	private IReadOnlyList<Contracts.Models.ParameterInfo> GetParameters(ParametersRequest request)
	{
		Document document = ResolveDocument(request.DocumentName);
		Parameters parameters = GetParametersCollection(document);

		List<Contracts.Models.ParameterInfo> result = [];

		for (int index = 1; index <= parameters.Count; index++)
		{
			// Described one at a time on purpose.
			// A single parameter Inventor refuses to read must not fail the whole list.
			try
			{
				Parameter parameter = parameters[index];

				if (string.IsNullOrWhiteSpace(request.NameFilter) is false
					&& parameter.Name.Contains(request.NameFilter, StringComparison.OrdinalIgnoreCase) is false)
				{
					continue;
				}

				result.Add(Describe(parameter, document.UnitsOfMeasure));
			}
			catch (Exception exception)
			{
				BridgeLog.Write($"Skipped parameter at index {index} in '{document.DisplayName}': {exception.Message}");
			}
		}

		return result;
	}

	private Contracts.Models.ParameterInfo SetParameter(SetParameterRequest request)
	{
		Document document = ResolveDocument(request.DocumentName);
		Parameters parameters = GetParametersCollection(document);

		Parameter parameter;

		try
		{
			parameter = parameters[request.Name];
		}
		catch
		{
			throw new BridgeFailureException(
				BridgeErrorCodes.NotFound,
				$"'{document.DisplayName}' has no parameter named '{request.Name}'.");
		}

		// The document's UnitsOfMeasure, not the application's.
		// Parameter names resolve only against the document, so an expression such as "width / 4"
		// fails validation against the application level object, which has no document scope.
		UnitsOfMeasure unitsOfMeasure = document.UnitsOfMeasure;

		string units = UnitsOf(parameter);
		string valueKind = DetermineValueKind(units, unitsOfMeasure);

		// Each kind takes a different accessor.
		// Assigning Expression on a text or boolean parameter is rejected or silently mis-stored.
		using (SilentOperationScope.Enter(_inventor))
		{
			switch (valueKind)
			{
				case ParameterValueKinds.Text:
					parameter.Value = request.Expression;
					break;

				case ParameterValueKinds.Boolean:
					if (bool.TryParse(request.Expression, out bool booleanValue) is false)
					{
						throw new BridgeFailureException(
							BridgeErrorCodes.Internal,
							$"'{request.Name}' is a boolean parameter, so it needs 'true' or 'false' rather than '{request.Expression}'.");
					}

					parameter.Value = booleanValue;
					break;

				default:
					// Ask Inventor whether it can parse the expression before writing it,
					// so a bad expression reports itself rather than throwing from deep inside the API.
					if (unitsOfMeasure.IsExpressionValid(request.Expression, units) is false)
					{
						throw new BridgeFailureException(
							BridgeErrorCodes.Internal,
							$"Inventor cannot parse '{request.Expression}' as a value in '{units}'. State the units explicitly, ex. '50 mm'.");
					}

					parameter.Expression = request.Expression;
					break;
			}

			document.Update();
		}

		return Describe(parameter, unitsOfMeasure);
	}

	private ExpressionEvaluation EvaluateExpression(EvaluateExpressionRequest request)
	{
		Document document = ResolveDocument(request.DocumentName);

		// Default to the document's own length units so an unqualified number means what the user would expect.
		string units = string.IsNullOrWhiteSpace(request.Units)
			? _inventor.UnitsOfMeasure.GetStringFromType(document.UnitsOfMeasure.LengthUnits)
			: request.Units;

		UnitsOfMeasure unitsOfMeasure = document.UnitsOfMeasure;

		if (unitsOfMeasure.IsExpressionValid(request.Expression, units) is false)
			return new ExpressionEvaluation(request.Expression, false, null, null, units, null, []);

		double internalValue = unitsOfMeasure._GetValueFromExpression(request.Expression, units);

		string? databaseUnits = null;

		try
		{
			databaseUnits = unitsOfMeasure.GetDatabaseUnitsFromExpression(request.Expression, units);
		}
		catch
		{
			// Not every expression resolves to a named database unit.
		}

		List<string> drivingParameters = [];

		try
		{
			foreach (Parameter driving in unitsOfMeasure.GetDrivingParameters(request.Expression))
				drivingParameters.Add(driving.Name);
		}
		catch
		{
			// A constant expression drives nothing.
		}

		return new ExpressionEvaluation(
			request.Expression,
			true,
			internalValue,
			unitsOfMeasure.GetStringFromValue(internalValue, units),
			units,
			databaseUnits,
			drivingParameters);
	}

	/// <summary>
	/// 	Flattens a parameter into the wire model, reporting both internal and display form.
	/// </summary>
	/// <remarks>
	/// 	Inventor stores every length in centimetres and every angle in radians, whatever the document shows.
	/// 	Returning only one form is the usual source of a silent factor of 25.4.
	///
	/// 	Not every parameter is numeric. A text or boolean parameter has no internal value at all,
	/// 	and reading one through the numeric accessor throws.
	/// </remarks>
	/// <param name="parameter">
	/// 	The parameter to describe.
	/// </param>
	/// <param name="unitsOfMeasure">
	/// 	The owning document's units. The application level object cannot resolve parameter names
	/// 	and does not carry the document's display precision.
	/// </param>
	/// <returns>
	/// 	The flattened parameter.
	/// </returns>
	private static Contracts.Models.ParameterInfo Describe(Parameter parameter, UnitsOfMeasure unitsOfMeasure)
	{
		string units = UnitsOf(parameter);
		string valueKind = DetermineValueKind(units, unitsOfMeasure);

		double? internalValue = null;
		string displayValue;
		string expression = parameter.Expression ?? string.Empty;

		switch (valueKind)
		{
			case ParameterValueKinds.Text:
				// A text parameter's expression comes back wrapped in quotation marks.
				expression = expression.Trim().Trim('"').Trim();
				displayValue = expression;
				break;

			case ParameterValueKinds.Boolean:
				displayValue = expression;
				break;

			default:
				try
				{
					internalValue = parameter._Value;
					displayValue = unitsOfMeasure.GetStringFromValue(internalValue.Value, units);
				}
				catch
				{
					// A parameter that reports numeric units but refuses the numeric accessor still has an expression.
					displayValue = expression;
					valueKind = ParameterValueKinds.Unknown;
				}

				break;
		}

		return new Contracts.Models.ParameterInfo(
			parameter.Name,
			DescribeParameterType(parameter.ParameterType),
			valueKind,
			expression,
			internalValue,
			displayValue,
			units,
			IsDriven(parameter),
			parameter.IsKey,
			parameter.Comment ?? string.Empty);
	}

	/// <summary>
	/// 	Reads a parameter's unit string.
	/// </summary>
	/// <remarks>
	/// 	Units cannot be a C# property: the COM getter returns a string while the setter takes an object,
	/// 	so the language cannot form a property from the pair and the accessor must be called directly.
	/// </remarks>
	/// <param name="parameter">
	/// 	The parameter to read.
	/// </param>
	/// <returns>
	/// 	The unit string, or an empty string when Inventor refuses the call.
	/// </returns>
	private static string UnitsOf(Parameter parameter)
	{
		try
		{
			return parameter.get_Units() ?? string.Empty;
		}
		catch
		{
			return string.Empty;
		}
	}

	/// <summary>
	/// 	Decides whether a parameter is numeric, text, or boolean.
	/// </summary>
	/// <remarks>
	/// 	The unit string is the discriminator. ModelValueType is about tolerance, nominal against upper and lower,
	/// 	and says nothing about the stored data type.
	/// </remarks>
	/// <param name="units">
	/// 	The parameter's unit string.
	/// </param>
	/// <param name="unitsOfMeasure">
	/// 	The owning document's units.
	/// </param>
	/// <returns>
	/// 	A value from <see cref="ParameterValueKinds"/>.
	/// </returns>
	private static string DetermineValueKind(string units, UnitsOfMeasure unitsOfMeasure)
	{
		if (string.IsNullOrWhiteSpace(units))
			return ParameterValueKinds.Unknown;

		try
		{
			return unitsOfMeasure.GetTypeFromString(units) switch
			{
				UnitsTypeEnum.kTextUnits => ParameterValueKinds.Text,
				UnitsTypeEnum.kBooleanUnits => ParameterValueKinds.Boolean,
				_ => ParameterValueKinds.Numeric
			};
		}
		catch
		{
			return ParameterValueKinds.Unknown;
		}
	}

	private static bool IsDriven(Parameter parameter)
	{
		try
		{
			return parameter.DrivenBy is { Count: > 0 };
		}
		catch
		{
			return false;
		}
	}

	private static string DescribeParameterType(ParameterTypeEnum parameterType) => parameterType switch
	{
		ParameterTypeEnum.kModelParameter => "Model",
		ParameterTypeEnum.kUserParameter => "User",
		ParameterTypeEnum.kReferenceParameter => "Reference",
		ParameterTypeEnum.kTableParameter => "Table",
		ParameterTypeEnum.kDerivedParameter => "Derived",
		ParameterTypeEnum.kFinishParameter => "Finish",
		_ => parameterType.ToString()
	};
}
