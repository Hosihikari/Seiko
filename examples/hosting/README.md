# Sharing an environment

`seiko.conf` plus a `seiko.lock.json` produced from it are the shareable unit.
Copy both into another root and run:

```bash
sk sync --frozen
```

`--frozen` replays the lock exactly: the same versions, artifacts, and digests.
It refuses to run when the declared direct set differs from the lock or when the
configured `rid` differs from the lock's target, so a Windows lock is never
silently replayed on Linux.

The example declares two sei on purpose:

- `hosihikari-hosting` has one artifact per bundle, each with a
  `prerequisites` entry naming the bundle marker.
- `hosihikari-hosting-levilamina` is that marker. It is a normal sei with its own
  dependency back on the shim, which keeps it root-reachable.

Nothing adds the marker automatically. Declare it (or a different bundle
marker) yourself, or the shim's artifacts have no prerequisite to match and
resolution fails with an executable hint.

Regenerate the lock online with:

```bash
sk sync          # writes seiko.lock.json for the configured rid
sk sync --locked # verifies the lock still matches the declaration
```

To move the example to another platform, change `rid`, run a normal `sync`
online, and commit the new lock. A lock is only valid for its target platform.
