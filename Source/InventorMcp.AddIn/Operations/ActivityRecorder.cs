using System.Runtime.InteropServices;

using Inventor;

using InventorMcp.Contracts.Models;

namespace InventorMcp.AddIn.Operations;

/// <summary>
/// 	Records Inventor events into a bounded ring buffer the server drains on request.
/// </summary>
/// <remarks>
/// 	A pull model is used on purpose.
/// 	Pushing notifications would flood the model during a tight automation loop, which is the moment the feed must keep working.
/// 	The buffer is plain managed memory, so a drain never has to wait for Inventor's main thread.
/// </remarks>
internal sealed class ActivityRecorder : IDisposable
{
	private const int Capacity = 2000;

	private readonly Application _inventor;
	private readonly Lock _gate = new();
	private readonly ActivityEntry?[] _buffer = new ActivityEntry?[Capacity];

	// These hold the connection points open. A local would be collected and the sinks would silently disconnect.
	private ApplicationEvents? _applicationEvents;
	private TransactionEvents? _transactionEvents;

	private long _nextSequence = 1;
	private bool _disposed;

	public ActivityRecorder(Application inventor)
	{
		_inventor = inventor;
	}

	/// <summary>
	/// 	Subscribes to the Inventor event sets. Must be called from Inventor's main thread.
	/// </summary>
	public void Start()
	{
		_applicationEvents = _inventor.ApplicationEvents;
		_transactionEvents = _inventor.TransactionManager.TransactionEvents;

		_applicationEvents.OnNewDocument += OnNewDocument;
		_applicationEvents.OnOpenDocument += OnOpenDocument;
		_applicationEvents.OnCloseDocument += OnCloseDocument;
		_applicationEvents.OnSaveDocument += OnSaveDocument;
		_applicationEvents.OnActivateDocument += OnActivateDocument;

		_transactionEvents.OnCommit += OnCommit;
		_transactionEvents.OnAbort += OnAbort;
	}

	/// <summary>
	/// 	Returns entries newer than a cursor.
	/// </summary>
	/// <param name="sinceSequence">
	/// 	Exclusive lower bound. Zero returns everything the buffer still holds.
	/// </param>
	/// <param name="maxEntries">
	/// 	Cap on the number of entries returned.
	/// </param>
	/// <returns>
	/// 	The entries, the next cursor, and how many entries were overwritten before being read.
	/// </returns>
	public ActivityFeed Drain(long sinceSequence, int maxEntries)
	{
		lock (_gate)
		{
			long oldestAvailable = Math.Max(1, _nextSequence - Capacity);
			long requestedFrom = sinceSequence + 1;
			long dropped = Math.Max(0, oldestAvailable - requestedFrom);
			long start = Math.Max(requestedFrom, oldestAvailable);

			List<ActivityEntry> entries = [];

			for (long sequence = start; sequence < _nextSequence && entries.Count < maxEntries; sequence++)
			{
				ActivityEntry? entry = _buffer[(sequence - 1) % Capacity];

				if (entry is not null)
					entries.Add(entry);
			}

			long nextCursor = entries.Count is 0
				? _nextSequence - 1
				: entries[^1].Sequence;

			return new ActivityFeed(entries, nextCursor, dropped);
		}
	}

	private void Append(string category, string eventName, string documentName, string detail)
	{
		lock (_gate)
		{
			long sequence = _nextSequence++;

			_buffer[(sequence - 1) % Capacity] = new ActivityEntry(
				sequence,
				DateTimeOffset.UtcNow,
				category,
				eventName,
				documentName,
				detail);
		}
	}

	private static string NameOf(_Document? document)
	{
		try
		{
			return document?.DisplayName ?? string.Empty;
		}
		catch
		{
			// A document being torn down can refuse the call. The event is still worth recording.
			return string.Empty;
		}
	}

	private void OnNewDocument(_Document documentObject, EventTimingEnum beforeOrAfter, NameValueMap context, out HandlingCodeEnum handlingCode)
	{
		handlingCode = HandlingCodeEnum.kEventNotHandled;

		if (beforeOrAfter is EventTimingEnum.kAfter)
			Append("Document", "OnNewDocument", NameOf(documentObject), "Document created");
	}

	private void OnOpenDocument(_Document documentObject, string fullDocumentName, EventTimingEnum beforeOrAfter, NameValueMap context, out HandlingCodeEnum handlingCode)
	{
		handlingCode = HandlingCodeEnum.kEventNotHandled;

		if (beforeOrAfter is EventTimingEnum.kAfter)
			Append("Document", "OnOpenDocument", NameOf(documentObject), fullDocumentName);
	}

	private void OnCloseDocument(_Document documentObject, string fullDocumentName, EventTimingEnum beforeOrAfter, NameValueMap context, out HandlingCodeEnum handlingCode)
	{
		handlingCode = HandlingCodeEnum.kEventNotHandled;

		// Recorded before the close, because the document cannot be read afterwards.
		if (beforeOrAfter is EventTimingEnum.kBefore)
			Append("Document", "OnCloseDocument", NameOf(documentObject), fullDocumentName);
	}

	private void OnSaveDocument(_Document documentObject, EventTimingEnum beforeOrAfter, NameValueMap context, out HandlingCodeEnum handlingCode)
	{
		handlingCode = HandlingCodeEnum.kEventNotHandled;

		if (beforeOrAfter is EventTimingEnum.kAfter)
			Append("Document", "OnSaveDocument", NameOf(documentObject), "Document saved");
	}

	private void OnActivateDocument(_Document documentObject, EventTimingEnum beforeOrAfter, NameValueMap context, out HandlingCodeEnum handlingCode)
	{
		handlingCode = HandlingCodeEnum.kEventNotHandled;

		if (beforeOrAfter is EventTimingEnum.kAfter)
			Append("Document", "OnActivateDocument", NameOf(documentObject), "Document activated");
	}

	private void OnCommit(Transaction transactionObject, NameValueMap context, EventTimingEnum beforeOrAfter, out HandlingCodeEnum handlingCode)
	{
		handlingCode = HandlingCodeEnum.kEventNotHandled;

		if (beforeOrAfter is not EventTimingEnum.kAfter)
			return;

		// DisplayName carries the command name, ex. "Extrusion" or "Place Component".
		// This is what turns the feed into a readable narration of what an automation loop did.
		Append("Transaction", "OnCommit", NameOf(transactionObject.Document), transactionObject.DisplayName);
	}

	private void OnAbort(Transaction transactionObject, NameValueMap context, EventTimingEnum beforeOrAfter)
	{
		if (beforeOrAfter is EventTimingEnum.kAfter)
			Append("Transaction", "OnAbort", NameOf(transactionObject.Document), transactionObject.DisplayName);
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;

		try
		{
			if (_applicationEvents is not null)
			{
				_applicationEvents.OnNewDocument -= OnNewDocument;
				_applicationEvents.OnOpenDocument -= OnOpenDocument;
				_applicationEvents.OnCloseDocument -= OnCloseDocument;
				_applicationEvents.OnSaveDocument -= OnSaveDocument;
				_applicationEvents.OnActivateDocument -= OnActivateDocument;
			}

			if (_transactionEvents is not null)
			{
				_transactionEvents.OnCommit -= OnCommit;
				_transactionEvents.OnAbort -= OnAbort;
			}
		}
		catch
		{
			// Inventor may already be tearing the session down.
		}

		// Release the runtime callable wrappers holding the connection points open.
		// Leaving them can keep the Inventor process alive after the user closes it.
		ReleaseComObject(_transactionEvents);
		ReleaseComObject(_applicationEvents);

		_applicationEvents = null;
		_transactionEvents = null;
	}

	private static void ReleaseComObject(object? comObject)
	{
		if (comObject is null || Marshal.IsComObject(comObject) is false)
			return;

		try
		{
			_ = Marshal.ReleaseComObject(comObject);
		}
		catch (Exception exception)
		{
			BridgeLog.Write($"Could not release a COM object during shutdown. {exception.Message}");
		}
	}
}
