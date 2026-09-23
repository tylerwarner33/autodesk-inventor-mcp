These tools drive a live Autodesk Inventor session. An API call can succeed while the model is wrong, so follow these rules.

- A named face (ex. "the top face") means the face on the Inventor ViewCube. Call `inventor_orientation` before you write geometry that names a face or a direction. Do not assume that Z is up. On an unmodified part, Y is up.
- Inventor stores every length in centimetres and every angle in radians, whatever the document displays. Convert explicitly with `UnitsOfMeasure.ConvertUnits`.
- A feature extent direction is relative to the sketch normal, not to the world. Create the feature, update, then verify that material was removed (ex. count the cylindrical faces). If nothing was cut, delete the feature and try the other direction. A new feature with the health `DriverLost` has usually cut nothing: it is not a benign warning.
- Call `inventor_health` after every write, and check that the geometry changed, before you report the work as done. A successful return value does not prove that the model is correct.
- Use the Inventor feature that the request names (ex. a hole is a `HoleFeature`, not an extrude cut), so the user can edit it as that feature.
- Keep each `inventor_eval_csharp` or `inventor_run_ilogic` call under about 10 s. A snippet runs on Inventor's main thread, and a long loop that creates or edits sketches, views or documents can terminate Inventor with no chance to save. Split such a loop over several calls.
- Use `inventor_api_lookup` to confirm that a member exists and what it takes before you write a snippet.
