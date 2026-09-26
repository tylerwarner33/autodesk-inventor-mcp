---
name: drawings
description: How to read, measure and check Inventor drawings - the layout and image tools, balloons and leaders, sheets, views, styles and templates. Read it before you measure, compare or change a drawing, or when a drawing looks wrong and you must find why.
---

# Drawings

## Look before you judge

- `inventor_drawing_layout` measures one sheet: views, balloons, dimension text, parts lists, tables and the title
  block, in inches from the sheet's lower left corner. It flags overlaps, crossing leaders, ambiguous arrowheads,
  and anything outside the border. It also gives each view with its annotations as a view group, and the gaps
  between groups. Give `sheetName` for a sheet that is not active.
- `inventor_export_sheet_image` renders a sheet, or a region, as an image that you can look at. Use it on a region
  that the layout flags, and before you tell the user that a drawing is correct.
- Measure every case, not only the one in the request, before you call a layout change done.

## Balloons and leaders

- A leader is a tree: `Leader.RootNode` is at the balloon, and each leaf node is an arrowhead. A leader can have
  several branches.
- A balloon names a component through its BOM row. An arrowhead can land on, or near, another component. The layout
  tool flags an arrowhead near another arrowhead, near another leader, or near a curve of a component that the
  balloon does not name. `clearanceInches` sets the distance.
- A balloon has no range box in the API. A style that scales to its text is estimated at 3 times the text height;
  give `balloonDiameterInches` when you know the size.

## Placing balloons

- `inventor_auto_balloon` balloons one assembly view. It deletes every balloon of that view first, also one added by
  hand. Call it once for each view, the main view first: an item that has a balloon on the sheet is not ballooned
  again, so the first view gets it.
- It selects a side for each part. Give `sides` only when the user asks for some sides. A side with no room for a
  balloon, to the border or to another view, is not used.
- The API has no auto-balloon method. Inventor's own Auto Balloon command needs the user to select the view and the
  components.
- Build a leader from a `Point2d` for the balloon and a `GeometryIntent` last. `DrawingCurve.MidPoint` can return
  null with no exception (ex. a full circle), so use `CenterPoint` next.
- Get the curves of one component with `view.get_DrawingCurves(occurrence)`. Asking each curve for its owner is much
  slower.
- `Balloon.ParentView` gives the view of a balloon. `BalloonValueSets[1].ItemNumber` gives its item number.
- A balloon on a part of a sub-assembly that has its own BOM row shows the item of the sub-assembly.
- After a balloon change, run `inventor_drawing_layout` and look at the view with `inventor_export_sheet_image`.

## Sheets and views

- A sheet that is not active gives E_FAIL for the range boxes of its tables. Activate it first, and restore the
  old active sheet after.
- Two views on one sheet can have the same name. The layout tool labels them `#1`, `#2`.
- `DrawingView.Position` moves the view with its balloons and dimension text. A change of scale after the
  annotations strands them. Fit the scale once, before you annotate.
- `DrawingCurve` has no visible or hidden flag.
- `view.get_DrawingCurves(Type.Missing)` gives all curves of a view. Each curve's `ModelGeometry` has a
  `ContainingOccurrence` in an assembly view.

## Styles and templates

- `inventor_styles` lists the styles of a drawing, and compares two drawings with `compareWith`. Use it when two
  drawings from the same process look different.
- A read-only style library replaces a template's layer styles at `Documents.Add`, so a template edit may not reach
  the new drawing. A host with no active project gives different results. A change to a template's local text style
  did survive `Documents.Add`, so the override is not the same for every style.
- The API cannot export a `.styxml` file. Only the Styles Editor can.
- There is no OLE object API. An OLE table in a template cannot be read or deleted.

## Images from code

A transient camera on a sheet renders it with no window: set `Camera.SceneObject` to the sheet, then use
`CreateImageWithOptions` with the option `IncludeEdits`. `Camera.SaveAsBitmap` ignores a camera change that was not
applied. `inventor_export_sheet_image` does this for you.
