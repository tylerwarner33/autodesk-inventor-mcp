namespace InventorMcp.Contracts.Models;

/// <summary>
/// 	One observed Inventor event.
/// </summary>
/// <remarks>
/// 	The add-in appends entries to a bounded ring buffer as events arrive.
/// 	The server drains the buffer on request, so a tight automation loop never blocks on the model reading it.
/// </remarks>
/// <param name="Sequence">
/// 	Monotonic sequence number. Pass the highest value seen back as the next cursor.
/// </param>
/// <param name="TimestampUtc">
/// 	When the add-in observed the event.
/// </param>
/// <param name="Category">
/// 	Event group, ex. "Application", "Document", "Transaction", or "Feature".
/// </param>
/// <param name="EventName">
/// 	Inventor event name, ex. "OnNewDocument" or "OnCommitTransaction".
/// </param>
/// <param name="DocumentName">
/// 	Display name of the document the event relates to, or an empty string.
/// </param>
/// <param name="Detail">
/// 	Short description of what changed, ex. the transaction display name.
/// </param>
public sealed record ActivityEntry(
	long Sequence,
	DateTimeOffset TimestampUtc,
	string Category,
	string EventName,
	string DocumentName,
	string Detail);

/// <summary>
/// 	A drain of the activity ring buffer.
/// </summary>
/// <param name="Entries">
/// 	Entries newer than the requested cursor, oldest first.
/// </param>
/// <param name="NextSequence">
/// 	Cursor to pass on the following call.
/// </param>
/// <param name="DroppedEntries">
/// 	Count of entries the ring buffer overwrote before this drain read them.
/// 	A value above zero means the automation produced events faster than the model read them.
/// </param>
public sealed record ActivityFeed(
	IReadOnlyList<ActivityEntry> Entries,
	long NextSequence,
	long DroppedEntries);
