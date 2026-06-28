# Parked workflows

GitHub Actions only scans workflow files placed **directly** in `.github/workflows/`.
Anything in this `parked/` subdirectory is ignored: it never triggers, never runs, and
never reports a failed run. The files are kept here intact so they can be reactivated
later without rebuilding them from scratch.

## How to reactivate one

Move it back up one level:

```sh
git mv .github/workflows/parked/<name>.yml .github/workflows/<name>.yml
```

Then complete its reactivation steps from the table below (secrets, repo gates, paths).

## Why each is parked

These were inherited from the Wizden -> Frontier -> Monolith chain. They are not deleted
because Hyperion expects to want them once it self-hosts and/or grows a maintainer team.

### Needs a hosting solution

> **`publish.yml` is now LIVE** (left `parked/`, rewired to our own Robust.Cdn at
> `cdn.hyperionsector.com`). It packages server (linux-x64) + client on every push to
> `main` and POSTs to the CDN, which pings the watchdog → the live server updates at
> round end. Needs repo secret `PUBLISH_TOKEN` (the CDN fork's UpdateToken). The old
> Wizden-lineage multi-platform packaging lives in git history if we ever distribute
> server binaries for other hosters.

| File | Purpose | To reactivate |
|------|---------|---------------|
| `publish-testing.yml` | Same, to a testing channel | Set `PUBLISH_TOKEN`; pick our testing fork id |
| `publish-changelog.yml` | Daily changelog digest -> Discord (currently an all-comments husk) | Uncomment, set `CHANGELOG_DISCORD_WEBHOOK` |
| `benchmarks.yml` | Perf suite -> SQL, run on a dedicated box | Point host + key at our runner, change the `git clone` from `space-wizards/space-station-14` to `Hyperion-Sector/Hyperion`, set benchmark SQL secrets |

### Governance / labeling (want with a maintainer team)

| File | Purpose | To reactivate |
|------|---------|---------------|
| `update-credits.yml` | Auto-PR the contributor credits roll | Flip repo gate `Monolith-Station/Monolith` -> `Hyperion-Sector/Hyperion`, swap bot author identity, enable "Actions can create PRs" |
| `close-master-pr.yml` | Auto-close PRs opened from `master`/`main`/`develop` | Update the Monolith-named comment text |
| `prtitlecase.yml` | Auto Title-Case PR titles for clean changelog entries | Set `BOT_TOKEN` |
| `labeler-pr.yml` | Path-based PR labels via `actions/labeler` (reads `.github/labeler.yml`) | Define our label taxonomy |
| `labeler-size.yml` | XS/S/M/L/XL labels by diff size | Create the size labels |
| `labeler-conflict.yml` | Flag PRs with merge conflicts | Create `S: Merge Conflict` label |
| `labeler-needsreview.yml` | Swap `Needs Review`/`Awaiting Changes` on review request | Create the labels + a review team |
| `labeler-untriaged.yml` | Tag new issues/PRs `S: Untriaged` | Create the label + a triage queue |
| `labeler-review.yml` | Add `S: Approved` on maintainer approval | Flip repo gate off `new-frontiers-14/frontier-station-14`, set `LABELER_PAT`, define maintainer teams |
| `labeler-stable.yml` | Tag PRs to a `stable` branch | Create a `stable` branch |
| `labeler-staging.yml` | Tag PRs to a `staging` branch | Create a `staging` branch |

## Deferred follow-ups (for the still-active workflows)

These stay in `.github/workflows/` and run today, but carry inherited rough edges worth a
later pass:

- **`changelog.yml`** rewire: needs `BOT_TOKEN` + `vars.CHANGELOG_USER`/`CHANGELOG_EMAIL`,
  and its target `Resources/Changelog/Monolith.yml` should move to a Hyperion file (which
  also means updating the in-game changelog reader config, so it is not a pure rename).
- **`nf-mapchecker.yml`** path coverage: its trigger watches `_NF`, `Nyanotrasen`, and `_DV`
  entity dirs but not `_Mono` or `_Hyperion`, so it will not fire on our own content.
- **`validate-rgas.yml`** cleanup: leftover `github.actor` exclusions for `PJBot` and
  `FrontierATC`, upstream bots that do not exist here.
- **Branch lists**: several gates trigger on `[main, staging, stable]`; we only have `main`.
  The extra refs are harmless dead entries that could be trimmed for tidiness.
