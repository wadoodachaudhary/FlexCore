# Crystal sample database

`CrystalSamples.db` is the synthetic SQLite pack a Crystal host opens when a report's schema fingerprint is already known. Report SQL is not executed against this file. Open it read-only.

## How a consumer finds it

The file lives at `Reports/CrystalSamples/CrystalSamples.db` in this repository. The FlexCore project copies that same relative path into the build output and packs it as NuGet content (`contentFiles`) with `PackageCopyToOutput`, so a referenced app receives the file next to its own output.

```csharp
using Fx.ControlKit.Reports;

var databasePath = CrystalSampleDatabase.Path;
```

This path is the shared corpus (530 reports). A translation that still has to seed a report writes a separate session file named `samples.db` beside its XML. Pass `CrystalSampleDatabase.Path` in as the existing corpus so a fingerprint that is already here is reused instead of written again.

The pack uses this shape:

- `ReportCatalog (Hash, Name, Metadata)` — one row per report binary, keyed by the SHA-256 of the `.rpt`
- `SampleDatasets (ReportHash, DatasetKey, Metadata)` — schema fingerprint, columns, and parameter values
- `sample_{reportHash}_{datasetKey}` — the rows for that fingerprint

## Provenance

Copied from [vb6ToDotNet](https://github.com/wadoodachaudhary/vb6ToDotNet) commit `0c3e251` (`0c3e251f35b634334919dcbe88253b671767d300`), file `FlexKitTester/Data/CrystalSamples.db`. That commit is the tip of draft pull request #1 (`cursor/crystal-qa-cloud-9175`). `vb6ToDotNet` `main` still has the older pack; do not replace this file from there.

The bytes were produced by `tools/CrystalSamples.Seed` in [JavaToCSharp](https://github.com/wadoodachaudhary/JavaToCSharp), then refreshed with that tool's `worker` and `install-capture` commands for the 158 reports whose pack no longer matched the converter. The refresh replaced 153 schema fingerprints and 24 parameter sets. `tools/CrystalBench.Tests` then ran the 530-report bench: 158 blocked before, 0 blocked after, 530 rendering. Source `.rpt` files were not modified.

The file is 3,973,120 bytes. That is small enough to store in git, so Git LFS is not used.

## Regenerate

From a checkout of JavaToCSharp, against the report folders the seed reads (`rpt/` and `downloaded-samples/`). The seed does not modify `.rpt` files.

Build a new pack (this refuses to overwrite an existing database):

```sh
dotnet run --project tools/CrystalSamples.Seed -- build <Reports folder> <new-pack.db> <audit directory>
```

Replace datasets for reports already in a pack:

```sh
dotnet run --project tools/CrystalSamples.Seed -- worker <Reports folder> <relative-rpt-path> <sha256> <capture.json>
dotnet run --project tools/CrystalSamples.Seed -- install-capture CrystalSamples.db <capture.json>
```

`install-capture` keeps the catalog id and replaces that report's datasets only. Copy the resulting database over `Reports/CrystalSamples/CrystalSamples.db` and update the regression check that pins its SHA-256.
