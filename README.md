# StarCitizenTracker (archived)

**This repository is archived.** GameCapture split into two active repositories (TASK-22/23,
2026-08-18) and development continues there — this repo is history-only, kept for the commits and
task docs that led to the split.

- **[`gamecapture-engine`](https://github.com/PetitCastor/gamecapture-engine)** — the capture
  engine, wire contracts, plugin SDK, and `dotnet new gamecapture-plugin` template. Publishes
  `GameCapture.Contracts` / `GameCapture.Sdk` / `GameCapture.Sdk.Testing` / `GameCapture.Plugin.Template`
  to nuget.org.
- **[`gamecapture-plugins`](https://github.com/PetitCastor/gamecapture-plugins)** — `MissionPlugin`
  and `RefineryPlugin`, built as pure SDK consumers of the packages above.

Both new repos were extracted from this one with full git history preserved (`git filter-repo`) —
see [`docs/SPLIT-MANIFEST.md`](docs/SPLIT-MANIFEST.md) for the exact file-by-file disposition, and
`tasks/MATURITY/00-OVERVIEW.md` for the task series that executed it. File an issue or open a PR
against one of the two repos above, not this one.
