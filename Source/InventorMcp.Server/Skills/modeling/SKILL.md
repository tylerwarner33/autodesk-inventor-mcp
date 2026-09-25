---
name: modeling
description: How to write geometry in a live Inventor part or assembly - named faces and the ViewCube, units, feature extent direction, patterns, holes, constraints, and how to verify a write. Read it before you create or change sketches, features, patterns, holes or constraints.
---

# Modeling

## A named face means the ViewCube face

"The top face" means the face labelled Top on the Inventor ViewCube. The ViewCube can change per document, so call
`inventor_orientation` before you write geometry that names a face or a direction.

On an unmodified part the mapping is Top +Y, Front +Z, Right +X, so Y is up, not Z. Treat that as what to expect,
never as the answer. A Z-up guess puts the work on the Front face.

## Units

Inventor stores every length in centimetres and every angle in radians, whatever the document shows. A parameter
that reads "2 in" has the value 5.08.

- Convert with `document.UnitsOfMeasure.ConvertUnits(value, UnitsTypeEnum.kInchLengthUnits, UnitsTypeEnum.kDatabaseLengthUnits)`,
  or with the helpers `FromInches`, `ToInches`, `FromMillimetres`, `ToMillimetres`, `FromDegrees`, `ToDegrees`.
- Use the document's `UnitsOfMeasure`, not the application's: only the document's resolves parameter names in an
  expression.
- Convert once at the top of a snippet, so every value below it reads in the user's units.

## Feature extent direction is relative to the sketch

`PartFeatureExtentDirectionEnum` is relative to the sketch's own normal, and a sketch on a face can point the other
way from the face's world normal. A cut in the wrong direction removes nothing, and Inventor reports that as
`kDriverLostHealth`, not as an error.

Never assume the direction. Create the feature, update, and check that material was removed. If nothing was cut,
delete the feature and use the other direction.

## Use the feature that the request names

A hole is a `HoleFeature`, not an extrude cut of a circle. The geometry is the same, but the user edits the model
through its features.

## Make the model stable

- Dimension sketch geometry from a projected work plane, not from a body vertex or a face edge. It stays correct
  when the geometry changes.
- Base a pattern direction on a work axis, not on a feature edge. If the feature that owns the edge is suppressed,
  the pattern fails with it.
- A flush constraint with an offset can solve on either side. Test mate against flush and the sign of the offset,
  then check the position.

## Verify against the model, not the return value

An API call can succeed while the model is wrong. After every write:

1. `inventor_health`: no new sick feature. A new feature with `DriverLost` has usually cut nothing.
2. Check the geometry that the write was for:
   - `inventor_hole_check` finds the holes from the geometry, with their diameters and axes. It also finds a hole
     made by an extrude cut or a pattern.
   - `inventor_features` gives each feature's health and parameters, and each pattern's counts, spacing and
     direction.
   - `inventor_pattern_elements` gives the position of each pattern element.
3. Only then report the work as done.

A failed call can still change the model. See the `read-back-after-failed-write` skill.
