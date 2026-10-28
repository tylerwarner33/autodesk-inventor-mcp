# Changelog

The changes to the Autodesk Inventor MCP Server that users see.
Each pull request adds one section with a new version, so many versions can have the same date.
The version is also `<Version>` in `Directory.Build.props`, which the README version badge reads.
The newest version is first. The format is [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and [Semantic Versioning](https://semver.org/): a `Breaking` entry bumps the major version, an `Added` entry the minor version, and other entries the patch version.
See [.agents/rules/changelog.md](.agents/rules/changelog.md).

## [0.1.0] - 2026-10-28

### Added
- The changelog and the versioning rule. Each pull request adds the next version to this file and to `Directory.Build.props`, and a workflow checks it.
- The first versioned state of the server. It has the tools to inspect and drive a live Inventor 2025, 2026 or 2027 session, as listed in the README.
