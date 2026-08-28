# Authorized shadow preflight: safety timestamp parsing safe abort

Result: **SAFE_ABORT_BEFORE_HOST**. Exact repository, package, peer, Python,
tool, MesIngest v2.4, and installed effective-state gates passed. The run then
stopped at its first independent vehicle-safety sample, before copying or
starting the Host and before starting the proxy, Onboard, or simulator. No
shadow database, permit, RIoT request, order, or vehicle action occurred. The
authorization stated that Host startup was the consumption point, so it was
not consumed.

The safety endpoint was healthy. Its raw JSON carried an explicit UTC offset,
but PowerShell `ConvertFrom-Json` converted that value to a local `DateTime`.
Converting the value back to text removed the offset, after which the runner's
`AssumeUniversal` parse moved the instant eight hours into the future. The
fail-closed future-time check correctly rejected the resulting value, but the
input conversion was wrong.

The runner now reads the raw JSON through `System.Text.Json.JsonDocument`,
requires `reasonCodes` to be an array, requires the timestamp to end in an
explicit `Z` or `+/-HH:mm` offset, and parses it directly as `DateTimeOffset`.
Exact ordinal vehicle, source, and motion-state checks remain, as do the
not-future, maximum-age, `STOPPED`, and zero-reason gates. The extracted
production function passed 40 consecutive live read-only samples after the
fix. Independent safety review found no remaining P0/P1 in this change.

