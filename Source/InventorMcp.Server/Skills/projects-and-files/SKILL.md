---
name: projects-and-files
description: Inventor projects (.ipj), library paths, Vault read-only files, saved versions and migration, model states, Content Center parts, copies and references. Read it before you save, copy or write to a file, when a write fails because a file is not modifiable, or when a file must open in an older Inventor release.
---

# Projects and files

## The active project decides what can change

`inventor_session` gives the active `.ipj`, its workspace, its library paths (resolved to full paths), and for each
open document `isModifiable`, `readOnlyFile` and `library`.

- A file under a library path of the project is read-only by design. A write to it fails with E_FAIL.
- A Vault workspace makes files read-only until they are checked out. That is a second, separate layer: clear one
  and the other gives the same error.
- To edit library files, a user usually changes to an editing project that has only a workspace path. Do not change
  the active project for the user.
- Never check files in or out of Vault for the user. Do not check test copies into Vault.

## Saved versions

A file saved in a newer release does not open in an older one, even across point releases (ex. 2025.4 to 2025.3).

- Read the saved version before you save: `inventor_file_info` gives `savedBy`, from
  `Document.SoftwareVersionSaved`. Compare it with the release that reads the file next.
- A save in this session migrates the file to this session's release. Ask the user before you save a file that an
  older release must read.
- A "Data Format Has Changed" dialog migrates the file on OK. For a file that an older release must read, the user
  must click Cancel.
- Apprentice cannot save a file that needs migration to its own release.

## Copies for a test

Use `inventor_test_copy`. It copies the tree under a folder, clears the read-only attribute, and points the copies
at each other, with no rule run and Inventor open or closed. References outside the folder (library paths, the
Content Center) stay on the originals. See the `safe-template-edit` skill.

By hand: `AllReferencedDocuments` leaves out suppressed components, so walk
`document.File.ReferencedFileDescriptors`. `FileDescriptor.ReplaceReference` accepts only a copy of the same file.

## Model states

- `inventor_file_info` gives the model state names and the iProperties of each state.
- `MemberEditScope` is stored in each file, not in the session. A write to the factory with `kEditAllMembers`
  changed the Part Number of 333 members in one part. Read it before a write.
- A file with only `[Primary]` has `IsModelStateMember` false and no `FactoryDocument`.
- A write to a member document is allowed. `SaveAs` on an assembly also saves the changed member parts.

## Content Center

A Content Center part does not insert by file path: `Occurrences.Add` gives E_INVALIDARG, and
`AddByComponentDefinition` gives E_FAIL. Use the Content Center API.

## Documents that a call opens

- Close only the documents that you opened. Never call `Documents.CloseAll`: it closes the user's documents.
- `Documents.Open` returns the user's copy when the file is open already. Use `OpenOrReuseDocument(path)` and
  `CloseDocumentsOpenedHere()` in a snippet: they close only what the snippet opened.
- `inventor_close_documents` closes the documents under a folder, drawings first, and never one outside it.
- Opening a generated drawing marks it as changed. Pass `allowUnsavedChanges` when the only changes are from the open.

## Other file facts

- A STEP file wraps long `PRODUCT(...)` lines. A regular expression over the text must allow line breaks.
- An add-in DLL that Inventor loaded is locked, so a full build fails with MSB3061 on the copy step while Inventor
  runs. That is not a compile error. Build to another output folder, or use `inventor_run_plugin`, which loads a copy.
