# cs#884 evidence

- `red/mutation-*.txt` — seven mutations of the change, each applied alone on top of commit `78452f684`, the test class
  `DispatchVerdictTransitionLogTests` run, the file restored (`git diff --name-only` checked to be that one file). Every
  one fails on an assertion, none on compilation (runtime-false conditions, `Environment.TickCount64 < 0`, because
  `if (false)` is CS0162 under warnings-as-errors). M3 (a demand leaving the backlog is never dropped) is caught only by the
  accepted-demand path: a demand that leaves the catalog is also cleared through "vehicle finished the round and did not
  judge it", so the catalog-leave case passes under M3 by design.
- `green/targeted.txt` — ArchitectureTests plus the dispatch round classes, 465/465.
- `green/targeted-before-transcript-fix.txt` — the same set before excluding 2884 from the control-server#209 round
  transcript: the only two failures were that transcript picking up the new log lines.

Cost: `DispatchVerdictTransitionLog.Record` takes no DbContext; it walks the round outcome the runner already built and the
catalog read at round start, once per round. No query is added.
