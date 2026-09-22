# Feature Error Messages

Created: 2026-09-21

Status: **known limitation.** `inventor_health` reports which features are sick, but not why.
This document records what is missing, what was tried, and what could be done about it.

## What works today

`inventor_health` returns, for a document:

- `requiresUpdate`
- `sickFeatures`, each with `name`, `featureType`, `healthStatus` and `isSuppressed`
- `errorCount` and `errors`

The sick feature list is proven. A failed hole surfaced as `Hole1` with `kDriverLostHealth`, and that report is
what caught a bug the API had reported as a success.

## What is missing

`FeatureHealth.Message` is **always empty**.

`PartFeature` exposes no failure text. It carries `HealthStatus`, an enum, and nothing that explains the cause.
So the model learns that `Hole1` is `DriverLost` but not that the hole failed to intersect any material.

The `errors` list is also thin. `ErrorManager` has no per entry collection: it offers `AllMessages` as a single
text blob, plus `HasErrors` and `HasWarnings`. The current implementation splits that blob by line and attaches a
single severity taken from `HasErrors`, so there is no genuine per entry severity or object reference.

`errorCount` has been zero in every session so far, so even that path has never returned real content.

## Why it matters

The whole point of the health tool is debugging an automation loop that is producing bad geometry.
Knowing that a feature is sick is useful. Knowing why would remove a diagnostic round trip, which currently has to
be done by writing a snippet that inspects the feature by hand.

## What to try

Ordered by likely value.

1. **Capture `ErrorManager` around a write.** The error manager is a session level object, and its contents are
	transient. Reading it after the fact may be too late. Wrapping a write in
	`ErrorManager.StartMessageSection` and reading the resulting `MessageSection` may capture exactly the messages
	that operation produced, attributed to that operation. This is the most promising avenue and is a server side
	change only if it is done inside an `inventor_eval_csharp` snippet.
2. **Check whether specific feature types expose more.** `PartFeature` is the common base.
	`HoleFeature`, `ExtrudeFeature` and others may carry type specific diagnostics that the base interface hides.
	`inventor_api_lookup` can answer this without Inventor running.
3. **Look at the feature's consumed entities.** A `DriverLost` feature has lost a reference. Reporting which
	sketch, face or edge it can no longer resolve would often be the actual answer, and that is reachable through
	the feature's own properties rather than through any error text.
4. **Report the health status meaning.** Even without Inventor's own text, mapping each `HealthStatusEnum` value to
	a plain explanation of what usually causes it would help, ex. `DriverLost` meaning the driving geometry no
	longer exists or could not be found.

Option 4 is cheap and needs no API discovery. Options 1 to 3 need investigation against a document that actually
has sick features, which has been hard to produce on purpose.

## How to reproduce a sick feature

The failed hole earlier in the project is the known recipe: create a hole on a sketch whose extent direction points
away from the material, so it cuts nothing. The feature reports `kDriverLostHealth`.

Other candidates worth trying: suppress a feature another feature depends on, delete a sketch a feature consumes,
or drive a parameter to a value that makes a fillet radius impossible.

## Decision

Leave as is until someone is actually debugging a failure and finds the missing text costly.
The current output is honest: it says what is sick without pretending to know why.
