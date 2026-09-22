---
name: Inventor Modeling
description: How to write geometry against a live Inventor session - named faces, extent direction, and units
triggers:
  - Writing geometry, sketches, or features through inventor_eval_csharp or inventor_run_ilogic
  - A request that names a face (ex. "the top face") or a direction
  - Converting lengths or angles for a script
---

## Rules that come from how Inventor actually behaves

These were each learned by getting them wrong against a live session.
They are not style preferences.

### A named face means the ViewCube face, and the mapping must be queried

When a user says "the top face", they mean the face labelled on the Inventor ViewCube.

**The ViewCube can be redefined per document, so its mapping to world axes is not a constant.**
Call the `inventor_orientation` tool before writing geometry that refers to a named face.

On an unmodified part the mapping measures as Top +Y, Front +Z, Right +X, so **Y is the vertical axis, not Z**.
Treat that as the default to expect, never as the answer.
Assuming a Z-up convention borrowed from other CAD tools puts the work on the Front face.

### Feature extent direction is relative to the sketch, not the world

`PartFeatureExtentDirectionEnum` is relative to the **sketch's own normal**.
A sketch built on a face has its own coordinate system, which may point the opposite way from the face's world normal.

A hole drilled away from the solid removes nothing, and Inventor reports that as `kDriverLostHealth`
rather than a compute error, which looks like a lost reference instead of a wrong direction.

Never assume the direction. Create the feature, `Update`, then count the resulting cylindrical faces or check
`HealthStatus`. If nothing was cut, delete it and retry the other way.

### Lengths are centimetres and angles are radians

Inventor stores every length in centimetres and every angle in radians, whatever the document displays.
A parameter reading `"2 in"` has an internal value of 5.08.

Convert explicitly with `UnitsOfMeasure.ConvertUnits(value, kInchLengthUnits, kDatabaseLengthUnits)`.
Do the conversion once at the top of a script so every literal below reads in the user's units.

### Verify against the model, not the return value

An Inventor API call can succeed while the model is wrong.
Check `inventor_health` after any write, and count geometry when a feature is meant to cut material.
