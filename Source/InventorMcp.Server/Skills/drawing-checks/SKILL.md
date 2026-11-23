---
name: drawing-checks
description: The tools that measure, render, balloon and compare Inventor drawings - inventor_drawing_layout, inventor_export_sheet_image, inventor_auto_balloon and inventor_styles. Read it before you measure, compare or change a drawing, or when a drawing looks wrong and you must find why.
---

# Drawing checks

The drawing facts of the Inventor API (leaders as trees, sheets that must be active, style libraries, images from a
transient camera) are in the `inventor-drawings` skill of the `inventor-api` plugin (marketplace
`tylerwarner-autodesk`). If your client has that plugin, read that skill too. This skill gives the tools of this server.

## Look before you judge

- `inventor_drawing_layout` measures one sheet: views, balloons, dimension text, parts lists, tables and the title
  block, in inches from the sheet's lower left corner. It flags overlaps, crossing leaders, ambiguous arrowheads,
  and anything outside the border. It also gives each view with its annotations as a view group, and the gaps
  between groups. Give `sheetName` for a sheet that is not active.
- Two views on one sheet can have the same name. The layout tool labels them `#1`, `#2`.
- `inventor_export_sheet_image` renders a sheet, or a region, as an image that you can look at. Use it on a region
  that the layout flags, and before you tell the user that a drawing is correct.
- Measure every case, not only the one in the request, before you call a layout change done.

## Balloons

- The layout tool flags an arrowhead near another arrowhead, near another leader, or near a curve of a component that
  the balloon does not name. `clearanceInches` sets the distance.
- A balloon has no range box in the API, so the layout tool estimates a balloon that scales to its text at 3 times
  the text height. Give `balloonDiameterInches` when you know the size.
- `inventor_auto_balloon` balloons one assembly view. It deletes every balloon of that view first, also one added by
  hand. Call it once for each view, the main view first: an item that has a balloon on the sheet is not ballooned
  again, so the first view gets it.
- It selects a side for each part. Give `sides` only when the user asks for some sides. A side with no room for a
  balloon, to the border or to another view, is not used.
- After a balloon change, run `inventor_drawing_layout` and look at the view with `inventor_export_sheet_image`.

## Styles

- `inventor_styles` lists the styles of a drawing, and compares two drawings with `compareWith`. Use it when two
  drawings from the same process look different.

## Drawings that a plugin generates

See the `plugin-loop-cycle` skill: run one case per call, then measure each generated drawing with these tools.
