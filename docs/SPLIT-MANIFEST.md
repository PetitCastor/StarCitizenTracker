# Split manifest

Exact disposition of every file tracked in this repo (`git ls-files`) across the two-repo split
(`gamecapture-engine`, `gamecapture-plugins`) or the archived mono-repo. See `00-OVERVIEW.md` for
why the split happens and the locked repo/branding decisions; this doc is the audit-able "where does
each path go" reference TASK-22/23 execute against.

**Disposition legend:** `engine` = tracked at the same relative path in `gamecapture-engine`.
`plugins` = tracked at the same relative path in `gamecapture-plugins`. `both` = tracked at the same
relative path in both new repos, as independent copies (no shared source of truth after the split —
each repo edits its own going forward). `archive` = stays only in this repo's history; not carried
to either new repo.

## Path table

| Path | Disposition |
| --- | --- |
| `src/GameCapture.Contracts/**` | engine |
| `src/GameCapture.Engine/**` | engine |
| `src/GameCapture.Sdk/**` | engine |
| `src/GameCapture.Sdk.Testing/**` | engine |
| `tests/GameCapture.Contracts.Tests/**` | engine |
| `tests/GameCapture.Engine.Tests/**` (incl. `Fixtures/engine-smoke/*.png`) | engine |
| `tests/GameCapture.Sdk.Tests/**` | engine |
| `tests/GameCapture.Sdk.Testing.Tests/**` | engine |
| `protos/**` | engine |
| `templates/**` (incl. `GameCapture.Plugin.Template.csproj` and the nested `gamecapture-plugin/` template content, its own `.github/workflows/ci.yml`, `.gitignore`, and `tests/`) | engine |
| `buf.yaml` | engine |
| `Directory.Build.props` | engine |
| `docs/ARCHITECTURE.md` | engine |
| `docs/ENGINE-SERVICES.md` | engine |
| `docs/PROTOCOL.md` | engine |
| `docs/PLUGIN-AUTHORING.md` | engine |
| `docs/COMPATIBILITY.md` (this task) | engine |
| `docs/REPLAY.md` | engine — see **Disposition A** below |
| `docs/SPLIT-MANIFEST.md` (this file) | archive — a record of the split itself, not living documentation either new repo needs afterward |
| `.github/workflows/ci.yml` | engine — re-scoped: `build-test` drops the Mission/Refinery build+test legs and the "Plugin boundary grep gate" step (that gate moves to `plugins`, see **Workflow split plan**); `template-guard` and `proto-guard` carry over unchanged |
| `.github/workflows/release.yml` | engine — re-scoped: the four `dotnet restore/build/test/pack` steps that name `GameCapture.slnx` re-point to `GameCaptureEngine.slnx`; Trusted-Publishing steps and the `nuget-release` environment gate carry over unchanged |
| `src/Plugins/MissionPlugin/**` | plugins |
| `src/Plugins/RefineryPlugin/**` | plugins |
| `tests/MissionPlugin.Tests/**` | plugins |
| `tests/RefineryPlugin.Tests/**` | plugins |
| `tests/fixtures/corpus/refinery-confirm/*.png` | plugins — see **Disposition B** below |
| `tests/fixtures/corpus/refinery-ice-rename/*.png` | plugins — see **Disposition B** below |
| `.gitignore` | both — independent copies (see **Disposition addendum: duplicated files**) |
| `coverlet.runsettings` | both — independent copies (see addendum) |
| `.github/skills/code-review-csharp/SKILL.md` | both — independent copies (see addendum) |
| `LICENSE` | both — independent copies (see addendum) |
| `GameCapture.slnx` | archive — replaced by two new solution files, see **Slnx split plan** |
| `README.md` | archive — superseded by a fresh README authored in each new repo (TASK-22, TASK-23); the mono-repo README documents both halves at once and no longer describes either repo alone |
| `tasks/**` | archive — already gitignored (`.gitignore`'s `/tasks/` rule), so not present in `git ls-files`; listed here only because `00-OVERVIEW.md` and the task series itself are mono-repo-only artifacts |

## Disposition A: `docs/REPLAY.md`

**Decision: engine**, with a cross-repo link from the plugins README rather than a copy or a move.

Reasoning: `REPLAY.md` documents `ReplayFrameSource`, `ClientConnection`'s replay-vs-live backpressure
mode, and `EngineLocator`/`ReplayHarness` (`GameCapture.Sdk.Testing`) in detail — all engine-repo code.
It is the link *target* of eight inbound references from four other files this manifest also sends
to `engine`: `ARCHITECTURE.md` (2), `ENGINE-SERVICES.md` (3), `PLUGIN-AUTHORING.md` (2), and the
shipped `src/GameCapture.Sdk.Testing/README.md` (1). Moving `REPLAY.md` to `plugins` would turn all
eight into cross-repo links; keeping it in `engine` costs exactly the one outbound link TASK-24 step 1
adds from the plugins README. (The mono-repo `README.md` also links it, but `README.md` is `archive`
here — superseded by fresh per-repo READMEs — so that link doesn't carry forward either way.) The
corpus-capture walkthrough it also contains (steps to run
`--save-frames` against a live game and land PNGs under `tests/fixtures/corpus/<name>/`) stays
useful to plugin authors precisely because it's one hop away via that link, pinned to the engine
version the plugin repo's `engine-version.txt` names.

## Disposition B: the two corpus paths

**Decision: already disjoint, no path changes needed.** The task doc's draft table names both
"engine smoke corpus" and `tests/fixtures/corpus/**` as if they might overlap; on the actual tree
they don't:

- Engine's own corpus lives under `tests/GameCapture.Engine.Tests/Fixtures/engine-smoke/` (3 PNGs,
  local to that test project per `docs/REPLAY.md`'s "Layout" section) → **engine**, moves with
  `tests/GameCapture.Engine.Tests/**` above.
- The plugin parity corpora live under `tests/fixtures/corpus/refinery-confirm/` (7 PNGs) and
  `tests/fixtures/corpus/refinery-ice-rename/` (8 PNGs) → **plugins**, listed explicitly above.

No file under `tests/fixtures/corpus/**` shares a name-prefix directory with
`tests/GameCapture.Engine.Tests/Fixtures/engine-smoke/**`; the two sets are disjoint by directory,
not merely by description. (There is currently no `tests/fixtures/corpus/mission-*` corpus — Mission
parity is skipped pending a **[USER ACTION]** in-game capture per `00-OVERVIEW.md`; when that corpus
lands it goes under `tests/fixtures/corpus/` and is `plugins` like the two above.)

## Disposition C: `.claude/`

**Decision: no tracked file exists to move today.** `git ls-files` has no `.claude/**` entries — the
only file on disk under `.claude/` is `settings.local.json`, matched by `.gitignore`'s `*.local.json`
rule and therefore untracked by repo convention (`.claude/` project *memory* is path-derived and
local — TASK-16.5 step 12). There is no committed `.claude/settings.json` in this repo to duplicate.
If one is added before TASK-22/23 execute, it goes in the **duplicated files** addendum below
alongside `.gitignore`; until then this row is intentionally empty rather than silently dropped.
Intended outcome either way: both `gamecapture-engine` and `gamecapture-plugins` launch with their
own `.claude/settings.json` from day one (TASK-22/23 each author one fresh if none exists to copy) —
neither repo should launch with no settings or hooks, per the task spec's original concern. (The task
spec also names `.editorconfig` alongside `.gitignore` for duplication; no `.editorconfig` is tracked
in this repo, so it isn't in the addendum below — the four files there are the complete duplicated
set today.)

## Disposition addendum: duplicated files

Four files are tracked today and needed, unchanged in content, by both new repos at the same path.
Each becomes two independent copies at the split — no shared source of truth afterward, so a future
edit to one (e.g. a `.gitignore` rule for a new build tool) does not automatically reach the other:

| Path | Why both need it |
| --- | --- |
| `.gitignore` | Both repos build .NET projects with the same local artifacts (`bin/`, `obj/`, `TestResults/`, NuGet caches) |
| `coverlet.runsettings` | Copied to `plugins` as a starting point, not because most of it applies there: of the 10 `ExcludeByFile` globs, only `**/Program.cs` matches anything under `plugins` — the other nine (`Core/CaptureInterop.cs`, `MonitorCapture.cs`, `LiveFrameSource.cs`, `FrameSaver.cs`, `EngineConfig.cs`, `ConsoleSink.cs`, `NamedPipeChannel.cs`, `Metrics/MetricsSampler.cs`, `Metrics/MetricsReporter.cs`) are engine-only files `plugins` consumes as packages and never has locally, so they're harmless no-op globs there. TASK-23 should prune those nine and the file's engine/monolith-history header comment down to what `plugins` actually needs |
| `.github/skills/code-review-csharp/SKILL.md` | Generic C# review skill, not repo-specific; both repos are C# |
| `LICENSE` (MIT) | `engine` needs it for the packages it publishes (`Directory.Build.props`'s `PackageLicenseExpression`); `plugins` gets its own copy as standard open-source hygiene even though it publishes nothing |

## Slnx split plan

`GameCapture.slnx` is replaced by two new solution files, one per repo, each named for its repo:

**`gamecapture-engine/GameCaptureEngine.slnx`**
```
/src/    GameCapture.Contracts, GameCapture.Engine, GameCapture.Sdk, GameCapture.Sdk.Testing
/tests/  GameCapture.Contracts.Tests, GameCapture.Engine.Tests, GameCapture.Sdk.Tests,
         GameCapture.Sdk.Testing.Tests
```
(`templates/GameCapture.Plugin.Template.csproj` must **not** be added to this solution: it packs
content only via `EnableDefaultItems=false` — an "untouched template source" model that both
`ci.yml`'s template-guard and `release.yml`'s "Pack template" step depend on — and joining a solution
would risk the SDK trying to compile the `.cs` files it ships as content. It carries over packed by
explicit path, same as today; nothing in `GameCapture.Engine.csproj` excludes it today because the
two trees are already siblings, not nested.)

**`gamecapture-plugins/GameCapturePlugins.slnx`**
```
/src/    MissionPlugin, RefineryPlugin
/tests/  MissionPlugin.Tests, RefineryPlugin.Tests
```
Both plugin projects move from `/src/Plugins/` (a subfolder disambiguating them from engine projects
in the old shared solution) to `/src/` directly — no more sibling engine projects to disambiguate
from in their own repo.

## Workflow split plan

| Job (today, mono-repo `ci.yml`/`release.yml`) | Engine repo | Plugins repo |
| --- | --- | --- |
| `build-test` | Kept, re-scoped to `GameCaptureEngine.slnx` (drops Mission/Refinery legs) | Not carried — plugins repo authors its own `build-test` (TASK-23) that restores `GameCapturePlugins.slnx`, downloads the pinned engine binary via `engine-version.txt` (per `00-OVERVIEW.md`'s "Plugins-repo CI" decision), and runs plugin + parity tests against it |
| `build-test`'s "Plugin boundary grep gate" step (`ci.yml`'s `rg ... src/Plugins/` check for `Grpc`/`RpcException`/proto/engine-assembly references) | Dropped — `src/Plugins/` won't exist in this repo, so the check would pass vacuously forever if left in place | Moves here: TASK-23 re-points the `rg` target at `src/` (the plugins repo has no `Plugins/` folder level, per the slnx split plan below) and adds it to the plugins repo's own `build-test`, so the standing gate (`00-OVERVIEW.md`'s "must stay green from its introduction onward") keeps meaning something after the split |
| `template-guard` | Kept unchanged (instantiates `dotnet new gamecapture-plugin`, builds, tests) | N/A — the template lives in the engine repo |
| `proto-guard` | Kept unchanged (`buf lint` + `buf breaking --against` `.git#branch=master`, per `ci.yml`'s actual `breaking_against` config) | N/A — `protos/` lives in the engine repo |
| `release` (`release.yml`) | Kept, re-scoped: the four `dotnet restore/build/test/pack` steps that name `GameCapture.slnx` re-point to `GameCaptureEngine.slnx`; Trusted-Publishing push to nuget.org, engine zip, and the `nuget-release` environment gate carry over unchanged | N/A — plugins repo has no publish target; its own releases (if any) are a `gamecapture-plugins`-scoped concern out of this series' scope |

No workflow job is dropped outright; every job that exists today keeps a home in exactly one repo
(the grep gate moves rather than either drops or duplicates), and the plugins repo gains one net-new
job (its own `build-test`, carrying the re-pointed grep gate as one of its steps) that has no tracked
source in this repo to list in the path table above — TASK-23 authors it from scratch.

## Audit pass

`git ls-files | wc -l` on this branch (`feature/mat-task-20-freeze-docs`, including this PR's own two
new doc files): **217** tracked paths. Checked programmatically — every path-pattern row in the
**Path table**, the **addendum** duplicated-files list, and the three **Disposition** decisions were
expanded into prefix/exact matchers and run against the full `git ls-files` output: all 217 paths
matched exactly one matcher, zero matched none, zero matched more than one. No path is claimed by two
destinations and none is left unresolved. Re-run before TASK-21/22 execute (new files may have landed
since); if a new path doesn't obviously match an existing pattern row, add an explicit row here rather
than deciding it inline during the split.
