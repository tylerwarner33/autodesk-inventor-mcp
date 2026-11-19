namespace InventorMcp.Contracts.Models;

/// <summary>
/// 	State of the Inventor session the add-in is running inside.
/// </summary>
/// <param name="Version">
/// 	Inventor software version, ex. "2027 (Build 310000)".
/// </param>
/// <param name="ReleaseYear">
/// 	Inventor release the session is, ex. 2025. The server selects version specific data with it.
/// </param>
/// <param name="ProcessId">
/// 	Process identifier of the host Inventor.exe.
/// </param>
/// <param name="ActiveDocument">
/// 	The document currently in focus, or null when no document is open.
/// </param>
/// <param name="OpenDocumentCount">
/// 	Number of documents open in the session, including referenced documents.
/// </param>
/// <param name="IsBusy">
/// 	True when Inventor is running a command or showing a modal dialog.
/// </param>
/// <param name="ActiveEditDocument">
/// 	The document in edit during an edit in place (ex. a part edited inside an assembly), or null with no edit in
/// 	place. A tool with no document name uses it.
/// </param>
public sealed record SessionInfo(
	string Version,
	int ReleaseYear,
	int ProcessId,
	DocumentInfo? ActiveDocument,
	int OpenDocumentCount,
	bool IsBusy,
	DocumentInfo? ActiveEditDocument = null);

/// <summary>
/// 	Identity and state of one open Inventor document.
/// </summary>
/// <param name="DisplayName">
/// 	Name shown in the Inventor user interface.
/// </param>
/// <param name="FullFileName">
/// 	Full path on disk, or an empty string when the document has never been saved.
/// </param>
/// <param name="DocumentType">
/// 	Inventor document type, ex. "PartDocument" or "AssemblyDocument".
/// </param>
/// <param name="IsDirty">
/// 	True when the document holds unsaved changes.
/// </param>
/// <param name="IsModifiable">
/// 	False when the document is read only, ex. checked in to Vault by another user.
/// </param>
/// <param name="RequiresUpdate">
/// 	True when the document is out of date and needs a rebuild.
/// </param>
/// <param name="FullDocumentName">
/// 	The path with the model state, ex. "C:\Parts\Bracket.ipt&lt;State2&gt;". The path alone for the primary model state,
/// 	and an empty string for a document that was never saved. Two model states of one file have different values.
/// </param>
/// <param name="ModelStateName">
/// 	The model state, ex. "[Primary]", or null for a document with none (ex. a drawing).
/// </param>
public sealed record DocumentInfo(
	string DisplayName,
	string FullFileName,
	string DocumentType,
	bool IsDirty,
	bool IsModifiable,
	bool RequiresUpdate,
	string? FullDocumentName = null,
	string? ModelStateName = null);
