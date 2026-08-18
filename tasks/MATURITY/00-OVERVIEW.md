# Engine/Plugin Maturation — Task Series Overview

**Status (2026-08-18): TASK-01..23 complete; `gamecapture-engine` repo live at v1.0.0 on nuget.org, `gamecapture-plugins` repo live with full history (commit 96a1c29), CI green.** Handshake, buf CI, SDK plugin host (`IGameCapturePlugin`/`GameCapturePluginHost`), tick semantics, `GameCapture.Sdk.Testing`, Mission/Refinery migration, `ReplayHarness`, and replay-parity now living in the plugin suites (with a real corpus doc at `docs/REPLAY.md`) are done. Mission parity is present but skipped, awaiting a **[USER ACTION]** in-game corpus capture — carried as documented debt into `gamecapture-plugins`. TASK-16.5 (de-brand to GameCapture) landed before TASK-17 as planned. Remaining: TASK-24 (archive + smoke). Original context: the split had correct process boundaries but immature DX — plugins knew engine internals, SDK consumed by ProjectReference, no NuGet packaging, no protocol versioning.

Goal: two repos — `PetitCastor/gamecapture-engine` (engine + contracts + SDK + template, publishes to nuget.org) and `PetitCastor/gamecapture-plugins` (MissionPlugin + RefineryPlugin as pure SDK consumers) — with `dotnet new gamecapture-plugin` as the starting point for new devs.

## Locked decisions (user-confirmed)
- **Feed**: nuget.org (public). Package IDs: `GameCapture.Contracts`, `GameCapture.Sdk`, `GameCapture.Sdk.Testing`, `GameCapture.Plugin.Template`. All five IDs verified unclaimed 2026-08-18; nothing is reserved until the first push.
- **Publishing auth**: **Trusted Publishing** (GitHub Actions OIDC → short-lived key via `NuGet/login@v1`). No API key, no `NUGET_API_KEY` secret, nothing long-lived to rotate or leak. Policies are per-repository, so exactly one is configured — on `gamecapture-engine` (TASK-22), never on the mono-repo. Publishing job is gated behind GitHub environment `nuget-release` with a required reviewer.
- **Template**: Lives in engine repo — versioned with the SDK; one release = one compatible set.
- **Plugins-repo CI**: Gets engine binary via GitHub Release download (pinned in `engine-version.txt`).
- **Order**: Harden SDK in mono-repo first, split last.
- **Repos**: `gamecapture-engine` / `gamecapture-plugins` (both new; current repo keeps the name `StarCitizenTracker` until TASK-24 archives it — renaming it early would claim the name TASK-22 needs).
- **Branding**: engine, contracts, SDK and template are game-agnostic `GameCapture.*`; namespace == PackageId == folder. The two plugins stay Star-Citizen-specific and keep their names. Public API drops "Tracker": `IGameCapturePlugin`, `GameCapturePluginHost`, `CaptureRecord`, `GameCaptureException`. See TASK-16.5.
- **First published version**: `v1.0.0`, tagged from the **new engine repo** (TASK-22), not the mono-repo — so SourceLink in the first artifact anyone installs resolves to the repo that survives. TASK-17 pushes only a throwaway `v1.0.0-rc.1` to exercise the pipeline.
- **Execution**: 1 task = 1 PR, executed by claude-sonnet-5, tests via Haiku subagent gate, code-review between tasks. Task docs materialize as `tasks\MATURITY\TASK-NN-*.md` (+ `00-OVERVIEW.md`).

## Best-practices basis
- **HashiCorp go-plugin / internals**: host/plugin over local RPC; handshake carries integer protocol version distinct from artifact versions; host negotiates or rejects; plugins can't crash host.
- **Buf breaking-change detection**: contract-first proto, `buf lint` + `buf breaking --against` main in CI.
- **NuGet package authoring best practices**: SemVer, license expression, package README, SourceLink, snupkg symbols, deterministic builds.
- **.NET template packages**: template as NuGet package; CI must instantiate+build the template every PR (guards template rot).
- **`.Testing` companion package**: instead of `InternalsVisibleTo` (pattern: `Microsoft.AspNetCore.Mvc.Testing`).

## Execution conventions (apply to EVERY task)
- **Branch first**: `feature/mat-task-NN-<slug>` cut from fresh `master` (verify HEAD live, snapshot goes stale).
- **Subagent test/commit**: All git commit/push AND test runs through Haiku subagent; commit gated on green suite.
- **Code reviews**: Reviews on Opus 4.8 fresh agent (`Agent(model:"opus")`, never fork) — NOT Opus 5.
- **One type per file**: every class/enum/record/interface gets its own `.cs` file (repo pattern since commit e2deaa0). Applies to all new code, task sketches showing several types inline notwithstanding.
- **Task structure**: Every task doc gets sections: Goal / Read-first / Steps / Out of scope / Acceptance.
- **Manual steps**: Marked **[USER ACTION]** = manual step only MPLC can do (secrets, repo creation approvals, in-game capture).

## Task → SOW map & order

| Order | Task | SOW | Size | Parallel-ok |
|---|---|---|---|---|
| 1 | ✅ TASK-01 finish PR #12 | 1 | S | — |
| 2 | ✅ TASK-02 --save-frames + corpus move | 1 | M | — |
| 3 | ✅ TASK-03 release fix + README stub | 1 | S | with 02 |
| 4 | ✅ TASK-04 proto handshake | 2 | M | — |
| 5 | ✅ TASK-05 SDK handshake | 2 | S | — |
| 6 | ✅ TASK-06 buf CI + PROTOCOL.md | 2 | S | with 05 |
| 7 | ✅ TASK-07 plugin host | 3 | L | — |
| 8 | ✅ TASK-08 tick semantics | 3 | M | — |
| 9 | ✅ TASK-09 Testing pkg | 3 | S | — |
| 10 | ✅ TASK-10 Mission migrate | 4 | M | — |
| 11 | ✅ TASK-11 Refinery migrate | 4 | M | — |
| 12 | ✅ TASK-12 ReplayHarness (PR #24) | 5 | M | — |
| 13 | ✅ TASK-13 parity move + Mission parity (PR #25) | 5 | S | — |
| 14 | ✅ TASK-14 engine docs (PR #26) | 6 | M | after 08 |
| 15 | ✅ TASK-15 authoring docs | 6 | M | after 09 |
| 16 | TASK-16 packaging props | 7 | S | — |
| 16.5 | **TASK-16.5 de-brand → GameCapture** | 7 | L | **blocks 17** |
| 17 | TASK-17 release pipeline | 7 | M | — |
| 18 | TASK-18 template content | 8 | M | — |
| 19 | TASK-19 template CI | 8 | S | — |
| 20 | TASK-20 compat + manifest | 9 | S | — |
| 21 | TASK-21 dry run + v1.0.0 | 9 | S | — |
| 22 | ✅ TASK-22 engine repo | 10 | M | — |
| 23 | ✅ TASK-23 plugins repo | 11 | M | — |
| 24 | TASK-24 archive + smoke | 11 | S | — |

## Verification (end-to-end)
1. Per task: acceptance list + full suite green (Haiku) before commit; Opus 4.8 review per PR.
2. Standing gates that must stay green from their introduction onward: plugin grep gate (manual `rg` since TASK-10; becomes a ci.yml step in TASK-11), arch test (TASK-08+), proto-guard (TASK-06+), template-guard (TASK-19+), parity expectations byte-identical (TASK-11+).
3. Final: TASK-24 e2e smoke — released artifacts only, no repo checkout needed to run the system; template instantiation from nuget.org builds outside any repo.
