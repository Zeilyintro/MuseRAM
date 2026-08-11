# MuseRAM tools

## Test workflows

Use the repository test entry point to keep local checks and release validation explicit:

```powershell
# Debug behavior tests; skips source/XAML contract tests.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Test-MuseRAM.ps1 -Mode Fast

# Full Debug and Release test gate used before publishing.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Test-MuseRAM.ps1 -Mode Full
```

`Fast` does not replace the full release gate. It excludes only tests tagged `SourceContract`;
production code, test compilation, and all behavior-oriented Core/App tests remain included.

For XAML source contracts, parse the fixture with `XDocument` and assert named elements,
attributes, styles, and element ownership. Reserve raw text checks for source-order or code-body
contracts that cannot be expressed structurally; do not depend on XAML whitespace or attribute order.

## Incremental calibration analysis

`Analyze-Calibration.ps1` reads `calibration-metrics.jsonl`, keeps a byte offset and a compact
analysis cache in a separate output directory, and reports structural coverage gaps. It never
modifies the source diagnostics.

Windows PowerShell 5.1 example:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Analyze-Calibration.ps1 `
  -DataDirectory E:\MuseRAM-CS1 `
  -OutputDirectory E:\MuseRAM\MuseRAM-Analysis\CS1
```

Use one output directory per data source. The first run imports the existing file; later runs
read only bytes appended since the previous run. The output includes:

- `calibration-report.md`: concise coverage and gap report.
- `calibration-report.json`: machine-readable report.
- `analysis-events.jsonl`: compact plans, outcomes and run records used for repeat analysis.
- `analysis-state.json`: source identity, byte offset and cumulative event counts.

Coverage labels are collection-health checks. They do not prove that a formula or profile
parameter is optimal.

The report also distinguishes verified recomputed-baseline parity from legacy baselines that
reused the formal plan. Parameter groups with zero feedback or identical deltas across all
variants are marked as insufficient evidence rather than treated as parameter sensitivity.
