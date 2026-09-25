---
name: split-long-work
description: How to split work that would hold Inventor's main thread for a long time - a time guard in each loop, one change per call, and pages over many files. Read it before a snippet loops over many features, sketches, views, documents or files, or when a result warns that a call held the main thread for more than 10 s.
---

# Split long work

A snippet runs on Inventor's main thread, and Inventor processes no window messages until it returns. A long loop
that creates or edits sketches, views or documents can fill the message queue, and Inventor then terminates with no
chance to save. One 58 s snippet that entered and left sketch edit about 1,200 times did that. So keep each call
under 10 s. A result over 10 s carries a warning.

## A time guard in each loop

```csharp
ScriptDeadline deadline = StartDeadline(8);
int done = 0;
foreach (string name in names.Skip(offset))
{
	if (deadline.Passed)
		break;
	// ... one unit of work ...
	done++;
}
return new { done, nextOffset = offset + done };
```

Call again with `nextOffset` until the work is done.

## One change per call

- Change one parameter, one feature or one document per call when each change is slow, ex. when rules run.
- Or batch parameter writes with `inventor_set_parameters` and `suppressRules`, which is fast because no rule runs
  until the end.

## Pages over many files

- `inventor_file_info` reads many files in pages: it stops before about 8 s and returns `nextOffset`.
- `inventor_assembly_tree` with `summary: true` gives the counts at each depth for a large assembly, in place of the
  whole tree.

## What cannot be split

- A single iLogic rule run, or a plugin run through `inventor_run_plugin`, cannot be stopped or split from outside.
  Tell the user how long it took, and run it once, not in a loop.
