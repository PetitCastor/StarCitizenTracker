# Compatibility

What version of what talks to what, across the split (`gamecapture-engine` ships the engine,
`GameCapture.Contracts`, `GameCapture.Sdk`, `GameCapture.Sdk.Testing`, and the plugin template;
`gamecapture-plugins` consumes them). See [`docs/PROTOCOL.md`](PROTOCOL.md#version-policy) for how
the protocol integer itself is governed — this doc is the release-facing matrix and the rules for
bumping each of the three version axes.

## Matrix

| Protocol | Engine | SDK / Contracts / Sdk.Testing | Notes |
| --- | --- | --- | --- |
| 1 | v1.0.0+ | 1.0.0+ | First published set, tagged from `gamecapture-engine` itself — not this mono-repo, which never publishes a `v1.0.0`. |

A new row is added whenever protocol `Min` or `Current` moves (see below); package version bumps
that don't touch the protocol integer extend the existing row's floor instead of adding one.

## Rules

- **SDK/Contracts/Sdk.Testing minor or patch bump is always safe.** The plugin template pins
  `Version="1.*"` (`templates/gamecapture-plugin/.template.config/template.json`'s `SdkVersion`
  symbol), so a plugin picks up new minors on its next restore with no source change required.
- **SDK/Contracts major bump means plugin source changes are expected.** Treat it like any other
  breaking NuGet release: plugins update on their own schedule, pinning the old major until they do.
- **Protocol bump is a third, independent axis** (`GameCapture.Contracts/ProtocolVersion.cs`) —
  moving it does not require a package major, and a package major does not require a protocol bump.
  When `Current` moves, the engine advertises `[Min, Current]` on `GetStatus` and a plugin negotiates
  down automatically (`docs/PROTOCOL.md#handshake`); when `Min` moves, every plugin below it is
  refused, deliberately and loudly.
  - **Support N-1 for at least one released engine version when feasible** — raise `Min` to the new
    protocol version in the *next* release after `Current` moved, not the same one, so a plugin has
    one engine release's worth of runway to update. Not always feasible (a security fix or a broken
    v1 guarantee may force an immediate `Min` raise); when skipped, say why in that release's notes.
- **A plugin's compatibility statement is protocol-scoped, not version-scoped**: "works with any
  engine advertising protocol `N`" — never a pinned engine version. The SDK enforces this at
  handshake time; a plugin doesn't need its own version check.

## Release checklist

1. Tag the release in the repo that owns what changed (`gamecapture-engine` for engine/SDK/Contracts/
   Sdk.Testing/template; plugin releases are the plugins repo's own tags and don't get a matrix row).
2. If the tag moved `ProtocolVersion.Current` or `Min`, add or update the matrix row above in the
   same PR as the version bump — not after.
3. Verify the row: the tagged engine's `GetStatus` advertises the range the row claims, and the
   tagged SDK/Contracts/Sdk.Testing packages on nuget.org are the versions the row claims.
