---
name: verify-a-write
description: The tools to call before and after a write to a part or an assembly - read the ViewCube before geometry that names a face, then check the health and the geometry before you report the work as done. Read it before you create or change sketches, features, patterns, holes or constraints.
---

# Verify a write

An API call can succeed while the model is wrong. The modeling facts (the ViewCube, centimeters and radians, the
sketch-relative extent direction) are in the `inventor-modeling` skill of the `inventor-api` plugin (marketplace
`tylerwarner-autodesk`). If your client has that plugin, read that skill too. This skill gives the tools that check
each fact.

## Before the write

1. **Read the ViewCube.** Call `inventor_orientation` before you write geometry that names a face or a direction (ex.
   "the top face"). It gives the world direction of each ViewCube face of the active document. On an unmodified part,
   Top is +Y, not +Z. Never assume it.
2. **Use the feature that the request names.** A hole is a `HoleFeature`, not an extrude cut, so the user can edit it
   as a hole.
3. **Convert the units.** Inventor stores centimeters and radians. Use the unit helpers of the `snippets` skill, or
   `UnitsOfMeasure.ConvertUnits`.

## After the write

1. **`inventor_health`.** No new sick feature. A new feature with `DriverLost` has usually cut nothing, because the
   extent direction is relative to the sketch normal. It is not a benign warning.
2. **Check the geometry that the write was for:**
   - `inventor_hole_check` finds the holes from the geometry, with their diameters and axes. It also finds a hole made
     by an extrude cut or a pattern.
   - `inventor_features` gives each feature's health and parameters, and each pattern's counts, spacing and
     direction.
   - `inventor_pattern_elements` gives the position of each pattern element.
3. **If nothing was cut,** delete the feature and keep its sketch (`Delete(true, false, false)`), then make it again
   in the other direction. Check again.
4. **Only then report the work as done.**

A failed call can still change the model. See the `read-back-after-failed-write` skill.
