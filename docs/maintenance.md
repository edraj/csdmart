# Maintenance commands

Two tables in dmart grow without bound unless something removes rows from them:
`histories` and `deletions`. Two CLI commands do that.

**Nothing runs them for you.** There is no scheduler, no hook, no background
service — the only caller of either is the CLI dispatcher in `Program.cs`. That
is deliberate: both delete rows from an audit trail, and when that happens
should be an operator's decision, not a policy the server applies on its own
schedule.

Both take `--dry-run`, and both are safe to re-run.

---

## `prune-empty-histories` — audit rows that record nothing

```
dmart prune-empty-histories [--space <name>] [--dry-run]
```

Deletes history rows whose `diff` is an empty object (`{}`).

### Why they exist

Until commit 5ce715b, an entry update that changed **nothing** still appended a
history row: the caller passed a null diff and `AppendAsync` coerces that to the
literal `{}` (both JSON columns are written NOT NULL by convention). The result
is audit rows recording that nothing happened.

That commit stopped new ones. This command removes the backlog.

### Why deleting them is safe

Nothing writing history today produces `{}`:

| Writer | What it writes |
|---|---|
| entry update | guarded by `if (historyDiff.Count > 0)` since 5ce715b |
| user / space / group / role / permission / attachment update | each carries its own `if (diff.Count > 0)` in `RequestHandler` |
| lock actions | always `{lock_type: …}` |
| entry move | always shortname / subpath |
| entry **create** | appends no history row at all |

The one remaining source is `dmart import` replaying an archive that still
contains them — so re-run this after such an import.

### When to run it

**Once**, after upgrading past 5ce715b. It does not need repeating: no current
writer creates these rows. Re-run only after importing an old archive.

If you schedule it anyway (cron, systemd timer), that is harmless — it exits
cleanly with nothing to do — but see the tombstone coupling below.

### Rows with a NULL diff are left alone

The column is nullable, and a null there is an older, different shape than the
empty object this bug produced. Removing it would be a separate decision, so
those rows are **counted and reported** rather than silently swept up:

```
Deleted 412 empty-diff history row(s) in all spaces
Left alone: 7 row(s) with a NULL diff — a different, older shape than the
empty object this removes.
```

### It writes tombstones

`histories` is one of the seven tables a Parquet export replicates. An
incremental consumer that already holds these rows has to learn they are gone —
deleting them silently is exactly the drift tombstones exist to prevent
([§5.2](./parquet-export-design.md)). So each pruned row gets a tombstone, in
the same transaction, over the same predicate as the delete.

**Consequence:** a large prune writes as many rows into `deletions` as it removes
from `histories`. That is what the next command is for.

---

## `prune-tombstones` — the deletions table

```
dmart prune-tombstones --older-than <days> [--dry-run]
```

Deletes tombstones older than the window **and raises the retention floor to
that same cutoff**, in one transaction.

### The floor move is the point

`deletion_retention.floor_at` records the instant from which tombstone recording
is *complete*. Pruning below a cutoff destroys the ability to answer "what was
deleted since X" for any X inside the pruned window. Without raising the floor,
an incremental export with such a watermark reads zero deletions and reports
success — silent drift. Raising it makes that same export **warn** instead:

```
parquet export: DELETIONS MAY HAVE BEEN LOST. The watermark … predates the
tombstone retention floor …. Take a FULL export to resynchronise.
```

The floor only ever moves forward, so a cutoff older than the current floor
leaves it alone.

### The window is required, with no default

It is coupled to your **incremental export cadence**: pruning to a cutoff newer
than your last increment discards deletions that increment still needed. Only
you know that cadence, so a default here would be a guess whose failure mode is
silent data drift.

**Choose a window longer than your incremental export interval.** Nightly
increments → 30 days is comfortable. No incremental pipeline at all → the window
only needs to outlive anything you might restore from.

---

## Order, if you run both

```
dmart prune-empty-histories --dry-run     # check the count
dmart prune-empty-histories               # writes tombstones
#   … let your incremental exports catch up …
dmart prune-tombstones --older-than 30    # then drain them
```

Draining the tombstones **before** your increments have carried them is what
loses the deletions — the retention floor will tell you afterwards that it
happened, but it cannot undo it.
