# Publishing FlexCore & upgrading consumers

This document covers two flows:

1. **Releasing a new FlexCore version** to nuget.org
2. **Picking up a new FlexCore version** in a consumer app (e.g. HomeFrontPB)

---

## 0. One-time setup state (already done)

For reference — what's already configured so future steps make sense:

- **GitHub repo**: <https://github.com/wadoodachaudhary/FlexCore> — `main` branch tracks the source
- **NuGet package**: `FlexCore` at <https://www.nuget.org/packages/FlexCore>
- **API key**: stored locally at `~/.flexcore-nuget-key` (chmod 600), used by the publish step below. If lost, regenerate at <https://www.nuget.org/account/apikeys> with scope **"Push new packages and package versions"** and glob `FlexCore` (or `*`).
- **HomeFrontPB** ([HomeFrontPB.csproj](../HomeFront/HomeFrontPB/HomeFrontPB.csproj)): now references FlexCore via NuGet — no longer a `<ProjectReference>`. The FlexCore source tree under `/Users/wadood/projects/VBToCSharp/FlexCore/` is fully independent.
- **HomeFront (MobileSource)** ([HomeFront.csproj](../HomeFront/MobileSource/HomeFront/HomeFront.csproj)): still uses a `<ProjectReference>` for now. To switch it to NuGet, follow the consumer-upgrade steps below — they apply the same way.

---

## 1. Releasing a new FlexCore version

### 1.1 Edit the FlexCore source

Make your changes in `/Users/wadood/projects/VBToCSharp/FlexCore/`. Commit them:

```bash
cd /Users/wadood/projects/VBToCSharp/FlexCore
git add -A
git commit -m "Describe the change"
git push
```

### 1.2 Bump the version

Open [`FlexCore.csproj`](FlexCore.csproj) and change the `<Version>` element. Follow [SemVer 2.0](https://semver.org):

- **Patch** bump (`0.1.0` → `0.1.1`) — bug fixes, no API changes
- **Minor** bump (`0.1.0` → `0.2.0`) — new features, backwards-compatible
- **Major** bump (`0.1.0` → `1.0.0`) — breaking changes
- **Pre-release** (`0.2.0-beta.1`) — while iterating before a stable release

```xml
<Version>0.1.1</Version>
```

Commit the bump:

```bash
git add FlexCore.csproj
git commit -m "Bump to 0.1.1"
git push
```

### 1.3 Pack

```bash
cd /Users/wadood/projects/VBToCSharp/FlexCore
rm -rf bin obj nupkg            # clean to avoid stale outputs
dotnet pack -c Release -o ./nupkg
```

This produces:
- `nupkg/FlexCore.0.1.1.nupkg` — main package
- `nupkg/FlexCore.0.1.1.snupkg` — symbols (debug info)

### 1.4 (Optional) Sanity-check locally before pushing

Point a throwaway consumer at the local `./nupkg` folder and `dotnet add package FlexCore --version 0.1.1` to confirm everything resolves and a simple app builds.

### 1.5 Push

```bash
dotnet nuget push /Users/wadood/projects/VBToCSharp/FlexCore/nupkg/FlexCore.0.1.1.nupkg \
    --api-key "$(cat ~/.flexcore-nuget-key)" \
    --source https://api.nuget.org/v3/index.json \
    --skip-duplicate
```

The `.snupkg` is pushed automatically alongside if it sits in the same folder.

Expected output:
```
Pushing FlexCore.0.1.1.nupkg to 'https://www.nuget.org/api/v2/package'...
  Created https://www.nuget.org/api/v2/package/ ...
Your package was pushed.
Pushing FlexCore.0.1.1.snupkg to 'https://www.nuget.org/api/v2/symbolpackage'...
  Created https://www.nuget.org/api/v2/symbolpackage/ ...
Your package was pushed.
```

### 1.6 Wait for indexing

NuGet validates and indexes the package — **typically 5–15 minutes**. Until that completes:
- `https://www.nuget.org/packages/FlexCore/0.1.1` will 404 in the browser
- `dotnet add package FlexCore --version 0.1.1` will fail to find the version

You can monitor at `https://www.nuget.org/packages/FlexCore` — when the new version appears in the version list, it's live.

### 1.7 Tag the release in git (optional but recommended)

```bash
cd /Users/wadood/projects/VBToCSharp/FlexCore
git tag v0.1.1
git push --tags
```

---

## 2. Picking up a new FlexCore version in a consumer

This applies to **HomeFrontPB** and any other app that references FlexCore via `<PackageReference>`.

### 2.1 Bump the `PackageReference` version

Open the consumer's `.csproj` and change the `Version` attribute on the FlexCore reference:

```xml
<!-- Before -->
<PackageReference Include="FlexCore" Version="0.1.0" />

<!-- After -->
<PackageReference Include="FlexCore" Version="0.1.1" />
```

For HomeFrontPB the file is [`HomeFrontPB.csproj`](../HomeFront/HomeFrontPB/HomeFrontPB.csproj).

### 2.2 Restore + build

```bash
cd /Users/wadood/projects/VBToCSharp/HomeFront/HomeFrontPB
dotnet restore HomeFrontPB.sln
dotnet build HomeFrontPB.sln --no-incremental
```

The new package downloads to `~/.nuget/packages/flexcore/0.1.1/` and the build links against it.

### 2.3 Verify the consumer is using the new version

```bash
find ~/.nuget/packages/flexcore -name "FlexCore.dll"
```

Should list both `0.1.0/lib/net10.0/FlexCore.dll` and `0.1.1/lib/net10.0/FlexCore.dll`. Confirm the active build picks the new one:

```bash
strings /Users/wadood/projects/VBToCSharp/HomeFront/HomeFrontPB/bin/Debug/net10.0/FlexCore.dll \
  | grep -m1 "^0\.1\.1" || echo "still on 0.1.0 — try a hard rebuild"
```

If a stale `bin/`/`obj/` is causing trouble, nuke and rebuild:

```bash
cd /Users/wadood/projects/VBToCSharp/HomeFront/HomeFrontPB
rm -rf bin obj
dotnet restore HomeFrontPB.sln
dotnet build HomeFrontPB.sln --no-incremental
```

### 2.4 Run the app

```bash
dotnet run --project /Users/wadood/projects/VBToCSharp/HomeFront/HomeFrontPB/HomeFrontPB.csproj
```

Smoke-test the screens that exercise the FlexCore changes — `FAssembly`, `FModelDimensions`, anything using `GridControl`, etc.

---

## Quick-reference scripts

### Publish helper

Drop a `publish.sh` in the FlexCore folder if you want one command:

```bash
#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

VERSION=$(grep -oE "<Version>[^<]+</Version>" FlexCore.csproj | sed 's/<[^>]*>//g')
echo "Packing FlexCore $VERSION"
rm -rf bin obj nupkg
dotnet pack -c Release -o ./nupkg

echo "Pushing to nuget.org"
dotnet nuget push "./nupkg/FlexCore.$VERSION.nupkg" \
    --api-key "$(cat ~/.flexcore-nuget-key)" \
    --source https://api.nuget.org/v3/index.json \
    --skip-duplicate

echo "Done — track validation at https://www.nuget.org/packages/FlexCore/$VERSION"
```

`chmod +x publish.sh`, then `./publish.sh` packs + pushes.

### Consumer-bump helper

```bash
# Replace 0.1.0 with the new version everywhere it appears
sed -i '' 's|Include="FlexCore" Version="[^"]*"|Include="FlexCore" Version="0.1.1"|' \
    /Users/wadood/projects/VBToCSharp/HomeFront/HomeFrontPB/HomeFrontPB.csproj
```

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `403 Forbidden` on push, "API key is invalid…" | Key lacks `Push new packages` scope, or glob doesn't match `FlexCore` | Regenerate at <https://www.nuget.org/account/apikeys> with correct scope + glob. Update `~/.flexcore-nuget-key`. |
| `Package 'FlexCore' version 'X.Y.Z' not found` | Package not yet indexed (5–15 min delay) | Wait, then retry. Check <https://www.nuget.org/packages/FlexCore>. |
| `409 Conflict` on push | Version already exists on nuget.org | Bump the version — NuGet versions are immutable once published. |
| Consumer builds against the **old** version after a bump | Stale `bin/`/`obj/` or NuGet cache hit | `rm -rf bin obj && dotnet nuget locals all --clear && dotnet restore && dotnet build --no-incremental` |
| Symbols (`.snupkg`) rejected | Symbols only accepted when the matching `.nupkg` succeeded; symbol push happens after the main push | Just retry the same `dotnet nuget push` — `--skip-duplicate` will skip the already-uploaded main and resend the symbols. |

---

## Version contract

- **Once published, a version on nuget.org is immutable.** You cannot overwrite `0.1.0` with new bits — only unlist it and publish `0.1.1`.
- **Unlisting** hides a version from default searches but leaves it pullable for anyone who already references it. Use sparingly — prefer fixing forward with a new version.
- **Pre-releases** (`0.2.0-beta.1`) are excluded from default `dotnet add package` resolution unless the consumer passes `--prerelease` or pins the exact version.
