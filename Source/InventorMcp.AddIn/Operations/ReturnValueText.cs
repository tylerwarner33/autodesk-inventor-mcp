using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InventorMcp.AddIn.Operations;

/// <summary>
/// 	Turns the value that a snippet returns into the text of the result.
/// </summary>
/// <remarks>
/// 	<c>ToString</c> gives only the type name of a list or of most objects, and a client then sent its snippet again
/// 	with a joined string. So a value made of plain data (lists, dictionaries, anonymous objects, records, tuples)
/// 	becomes JSON. A COM object stays <c>ToString</c>, because its members are not plain data and reading them all
/// 	could be slow. No Inventor types, so the test project compiles this file too.
/// </remarks>
internal static class ReturnValueText
{
	private const int _maxDepth = 6;

	private const int _maxItems = 10_000;

	private static readonly JsonSerializerOptions _options = new()
	{
		IncludeFields = true,
		MaxDepth = _maxDepth + 2,
		ReferenceHandler = ReferenceHandler.IgnoreCycles
	};

	/// <param name="value">
	/// 	The returned value.
	/// </param>
	/// <returns>
	/// 	The text, or null for a null value.
	/// </returns>
	public static string? Format(object? value)
	{
		if (value is null)
			return null;

		if (value is string text)
			return text;

		// Another type keeps its own ToString, ex. a StringBuilder or a Version.
		if (IsStructured(value) is false || IsPlain(value, 0) is false)
			return value.ToString();

		try
		{
			return JsonSerializer.Serialize(value, value.GetType(), _options);
		}
		catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
		{
			return value.ToString();
		}
	}

	/// <summary>
	/// 	True for a collection, an anonymous object, a record or a tuple: the shapes that a snippet returns as data.
	/// </summary>
	private static bool IsStructured(object value)
	{
		Type type = value.GetType();

		return value is IEnumerable
			|| (type.Name.Contains("AnonymousType", StringComparison.Ordinal) && type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
			|| type.GetMethod("<Clone>$") is not null
			|| value is System.Runtime.CompilerServices.ITuple;
	}

	private static bool IsScalar(Type type) =>
		type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
		|| type == typeof(TimeSpan) || type == typeof(Guid);

	/// <summary>
	/// 	True when the value holds only scalars, strings, collections and plain objects, and no COM object.
	/// </summary>
	private static bool IsPlain(object? value, int depth)
	{
		if (value is null or string)
			return true;

		Type type = value.GetType();

		if (IsScalar(type))
			return true;

		if (depth >= _maxDepth || Marshal.IsComObject(value) || type.IsCOMObject || typeof(Delegate).IsAssignableFrom(type))
			return false;

		if (value is IDictionary dictionary)
		{
			int count = 0;

			foreach (DictionaryEntry entry in dictionary)
			{
				if (++count > _maxItems || entry.Key is not string || IsPlain(entry.Value, depth + 1) is false)
					return false;
			}

			return true;
		}

		if (value is IEnumerable items)
		{
			int count = 0;

			foreach (object? item in items)
			{
				if (++count > _maxItems || IsPlain(item, depth + 1) is false)
					return false;
			}

			return true;
		}

		// A plain object: its type comes from the snippet or the base class library, not from a COM interop assembly.
		if (type.Assembly.GetCustomAttribute<ImportedFromTypeLibAttribute>() is not null || type.IsImport)
			return false;

		foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (property.GetIndexParameters().Length > 0 || property.CanRead is false)
				continue;

			if (IsPlain(property.GetValue(value), depth + 1) is false)
				return false;
		}

		foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
		{
			if (IsPlain(field.GetValue(value), depth + 1) is false)
				return false;
		}

		return true;
	}
}
