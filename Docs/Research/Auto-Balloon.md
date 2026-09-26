# Auto Balloon

The evidence behind `inventor_auto_balloon` (`Source/InventorMcp.Server/McpTools/InventorTool.AutoBalloon.cs`).

## Sources

The rules come from two production implementations:

- `Drawing_GenerateShopDrawing.vb` (`AddPartBalloons`, `PlanBalloon`, `SpreadBalloonPlacements`), an iLogic rule in
  the Automation Library. It gave the side selection from the part's outline, the attach point nearest that side,
  the walk of the occurrences, and the removal of a repeated item number.
- The Strobic drawing engine (`DrawingAnnotationService.cs`, and `Drawing-Engine-Internals.md` in that repository).
  It gave the attach point chain on null, the median start of a packed group, the swap of neighbours whose leaders
  cross, and the per-occurrence curve query.

## Decisions

- **No API method.** `inventor_api_lookup` found no auto-balloon member on 2026-09-26. The tool adds each balloon
  with `Sheet.Balloons.Add`.
- **Delete the view's balloons first.** The user asked for this on 2026-09-26: auto-ballooning a view replaces all of
  its balloons, also those added by hand. Balloons of other views stay.
- **Sides.** The user asked on 2026-09-26 that the tool select the side of each part by default, and that the user
  can limit the sides. `sides` limits them.
- **Room on each side.** The first live run put two balloons 0.08 in outside the sheet, because the view was 0.46 in
  from the sheet edge. A side is now used only when the space to the border, or to another view beside that side, is
  at least the offset plus the balloon radius.
- **Walk every sub-assembly, then remove repeats.** Inventor gives the item number only after `Balloons.Add`. The tool
  adds the balloon, reads `ItemNumber`, deletes a repeat, and then moves the other balloons together again.
- **Time limit.** The scan stops after 6 s and aborts the transaction with no change, because a snippet runs on
  Inventor's main thread. The shop rule measured about 5 s for the curve scan of a full elevator cab.

## Live checks, 2026-09-26, Inventor 2025.4

Test assembly built in the session's scratch folder: a plate, two rails, two brackets (a Normal sub-assembly of a
post and a tab) and five bolts, on an 11 x 8.5 in sheet with a front view (VIEW1) and a projected top view (VIEW2).

| Run | Result |
| --- | --- |
| VIEW1, all sides | 4 balloons in 191 ms. The tab's balloon showed item 2 of the bracket, the same as the post, and was removed. Two balloons were 0.08 in outside the sheet. |
| VIEW1, all sides, with the room check | Deleted the 4 balloons of the first run, did not use Left (0.46 in of room), and put the plate and rail balloons on the right. All on the sheet. |
| VIEW1, `sides` Bottom | 4 balloons packed at 0.42 in on the bottom, no crossing leader. The bottom rail (`Rail:2`) was selected as the instance nearest the side. |
| Transaction abort | A failed snippet after the start of the transaction left the 4 balloons as they were. |

- The bolt balloon attached to a full circle, through `CenterPoint`.
- `inventor_drawing_layout` flagged the post's arrowhead as attached to `Post.ipt`, while the balloon names
  `Bracket.iam`. That was a false issue: the check compared only the part file. It now compares every file on the
  occurrence path.

Not yet checked: a view of more than a few hundred parts against the 6 s limit, a phantom sub-assembly, a balloon
style from `balloonStyle`, and the tool through the packed server from a real MCP client.
