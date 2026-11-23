---
name: file-tools
description: How this server's tools read the active project, the saved version and the model states of a file, make test copies, wait on Vault, and close documents. Read it before you save, copy or write to a file, when a write fails because a file is not modifiable, or when a file must open in an older Inventor release.
---

# File tools

The file facts of Inventor (library paths and Vault as two read-only layers, saved versions, model states, Content
Center, which documents to close) are in the `inventor-projects-and-files` skill of the `inventor-data` plugin
(marketplace `tylerwarner-autodesk`). If your client has that plugin, read that skill too. This skill gives the tools of
this server.

## Can the file change?

- `inventor_session` gives the active `.ipj`, its workspace, its library paths (resolved to full paths), and for each
  open document `isModifiable`, `readOnlyFile` and `library`.
- A write to a file that is not modifiable fails with E_FAIL. The result of `inventor_eval_csharp` then has a
  `CONTEXT:` line with the state of the target document.
- Do not change the active project for the user, and never check files in or out of Vault.

## Vault downloads

A switch to a project with a Vault workspace can start a Vault download with a "Download progress" dialog. By default
the server waits until it closes (up to 10 minutes), with no click, and lists it in `blockingDialogs`. Never click its
`Cancel`, which stops the download.

## Saved versions and model states

- `inventor_file_info` gives `savedBy` (the release that saved the file), the model state names, and the iProperties
  of each state. It reads many files in pages: it stops before about 8 s and returns `nextOffset`.
- Before you save, compare `savedBy` with the release that reads the file next. A save in this session migrates the
  file to this session's release. Ask the user before you save a file that an older release must read.

## Copies for a test

Use `inventor_test_copy`. It copies the tree under a folder, clears the read-only attribute, and points the copies
at each other, with no rule run and Inventor open or closed. References outside the folder (library paths, the
Content Center) stay on the originals. Check that `problems` is empty. See the `safe-template-edit` skill.

## Opening and closing documents

- In a snippet, use `OpenOrReuseDocument(path)` and `CloseDocumentsOpenedHere()` from the `snippets` skill: they close
  only what the snippet opened.
- `inventor_close_documents` closes the documents under a folder, drawings first, and never one outside it.
- Never call `Documents.CloseAll`: it closes the user's documents.
- Opening a generated drawing marks it as changed. Pass `allowUnsavedChanges` when the only changes are from the open.

## A locked add-in DLL

An add-in DLL that Inventor loaded is locked, so a full build fails with MSB3061 on the copy step while Inventor runs.
Use `inventor_run_plugin`, which loads a copy of the build. See the `plugin-loop-cycle` skill.
