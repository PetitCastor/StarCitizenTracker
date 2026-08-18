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
| `.github/workflows/ci.yml` | engine — re-scoped: `build-test` drops the Mission/Refinery build+test legs (those projects move to `plugins`), `template-guard` and `proto-guard` carry over unchanged |
| `.github/workflows/release.yml` | engine — unchanged (already engine/SDK/Contracts/Sdk.Testing/template packaging, per `00-OVERVIEW.md`'s Trusted Publishing decision) |
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
It is itself the link *target* of two engine docs that move with it (`ENGINE-SERVICES.md#replay-mode`,
`PROTOCOL.md#backpressure-and-stream-end`); moving it to `plugins` would turn those two links
cross-repo instead of the one link TASK-24 step 1 adds from the plugins README. Keeping it in `engine`
minimizes cross-repo link count (1, outbound from `plugins`) versus moving it (2, inbound to `engine`
docs that need it). The corpus-capture walkthrough it also contains (steps to run
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

## Disposition addendum: duplicated files

Four files are tracked today and needed, unchanged in content, by both new repos at the same path.
Each becomes two independent copies at the split — no shared source of truth afterward, so a future
edit to one (e.g. a `.gitignore` rule for a new build tool) does not automatically reach the other:

| Path | Why both need it |
| --- | --- |
| `.gitignore` | Both repos build .NET projects with the same local artifacts (`bin/`, `obj/`, `TestResults/`, NuGet caches) |
| `coverlet.runsettings` | `ExcludeByFile` entries are relative globs (`**/Program.cs`, `**/ConsoleSink.cs`, …), not full paths — they match both repos' equivalent process-edge files without modification |
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
(The plugin template is content/config, not a buildable project — `templates/GameCapture.Plugin.Template.csproj`
packs but doesn't need a solution folder; `templates/gamecapture-plugin/**`'s own nested project is
excluded from this solution the same way it's excluded from `GameCapture.Engine.csproj` today, via
`DefaultItemExcludes` — nothing new here, just carried over.)

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
| `template-guard` | Kept unchanged (instantiates `dotnet new gamecapture-plugin`, builds, tests) | N/A — the template lives in the engine repo |
| `proto-guard` | Kept unchanged (`buf lint` + `buf breaking --against main`) | N/A — `protos/` lives in the engine repo |
| `release` (`release.yml`) | Kept unchanged — tags, packs, Trusted-Publishing push to nuget.org, engine zip | N/A — plugins repo has no publish target; its own releases (if any) are a `gamecapture-plugins`-scoped concern out of this series' scope |

No workflow job is dropped outright; every job that exists today keeps a home in exactly one repo,
and the plugins repo gains one net-new job (its own `build-test`) that has no tracked source in this
repo to list in the path table above — TASK-23 authors it from scratch.

## Audit pass

`git ls-files | wc -l` on this branch (`feature/mat-task-20-freeze-docs`): **215** tracked paths.
Checked programmatically — every path-pattern row in the **Path table**, the **addendum**
duplicated-files list, and the three **Disposition** decisions were expanded into prefix/exact
matchers and run against the full `git ls-files` output: all 215 paths matched exactly one matcher,
zero matched none, zero matched more than one. No path is claimed by two destinations and none is
left unresolved. Re-run before TASK-21/22 execute (new files may have landed since); if a new path
doesn't obviously match an existing pattern row, add an explicit row here rather than deciding it
inline during the split.
