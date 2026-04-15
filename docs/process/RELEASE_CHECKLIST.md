# Release Checklist

Use this checklist when preparing a new release of RoslynMcp.

---

## Pre-Release Verification

### Code Quality
- [ ] ~~All source files follow code style guidelines~~ — **style passes currently suspended; skip this step**
- [ ] No compiler errors (`roslyn_get_diagnostics` — never use `dotnet build` for this)
- [ ] No Roslyn analyzer warnings
- [ ] All tests pass (`dotnet run --project src/TestHarness/TestHarness.csproj`)
- [ ] Code coverage is adequate for new features

### Documentation
- [ ] README.md is up-to-date
  - [ ] Tool table includes all tools with accurate descriptions
  - [ ] Quick Start section is current
  - [ ] Badges reflect correct versions
- [ ] INSTALLATION.md is current
  - [ ] All supported clients documented
  - [ ] Troubleshooting section covers known issues
- [ ] CHANGELOG.md updated
  - [ ] All new features listed under `[Unreleased]`
  - [ ] Breaking changes clearly marked
  - [ ] Contributors acknowledged (if applicable)
- [ ] CONTRIBUTING.md is accurate
- [ ] API documentation (XML doc comments) complete for public tools

### Security & Privacy
- [ ] No API keys, secrets, or credentials in source
- [ ] No local file paths (except in .gitignore'd files)
- [ ] No personal information (beyond intentional LICENSE/AUTHORS)
- [ ] All placeholders (YOUR_USERNAME, etc.) replaced with actual values
- [ ] Dependencies audited for known vulnerabilities (`dotnet list package --vulnerable`)

### GitHub Preparation
- [ ] Issue templates tested
- [ ] PR template tested
- [ ] CI workflow runs successfully (if configured)
- [ ] Repository description set
- [ ] Topics/tags added (mcp, roslyn, csharp, ai-coding-assistant, etc.)
- [ ] LICENSE file present and correct
- [ ] .gitignore covers all necessary patterns
- [ ] .gitattributes configured for consistent line endings

---

## Release Process

### 1. Version Bump
- [ ] Update version in `Directory.build.props` (`<VersionPrefix>` and `<VersionSuffix>` tags)
- [ ] Confirm the GitHub milestone `vX.Y.Z` exists (omit pre-release suffix — use `v0.7.4`, not `v0.7.4-alpha`)
- [ ] Update CHANGELOG.md
  - [ ] Move `[Unreleased]` items to new version section
  - [ ] Add release date
  - [ ] Add link to GitHub release
- [ ] Commit version bump: `git commit -m "chore: bump version to v0.X.Y-alpha"`

### 2. Tag Release

> **Always create the GitHub release as a draft first** — draft releases are fully mutable (assets, notes, tag all editable). Only publish when everything is verified. Once published, the release is immutable.
>
> **Tag name blacklisting:** Once a tag name has been used by *any* published release (even a deleted one), GitHub permanently blacklists it — it cannot be recreated. If you must re-release, bump to the next version instead.

- [ ] Confirm HEAD is the final commit before tagging

```bash
git tag -a vX.Y.Z-alpha -m "Release vX.Y.Z-alpha"
git push && git push origin vX.Y.Z-alpha
# then immediately proceed to Create GitHub Release (as draft) — no commits in between
```

### 3. Build Release Artifacts

> **Important:** Do NOT use `--self-contained` for the MCP server. Roslyn resolves external assemblies from the SDK installation at runtime; self-contained binaries break this. The log viewer has no such constraint but framework-dependent is fine since users already have .NET installed.

```powershell
# MCP server — framework-dependent for both targets
dotnet publish src/RoslynMcp/RoslynMcp.csproj -c Release -f net8.0  -o ./publish/net8.0
dotnet publish src/RoslynMcp/RoslynMcp.csproj -c Release -f net10.0 -o ./publish/net10.0

# Log viewer — framework-dependent, net10.0 only (no Roslyn deps, ASP.NET Core web app)
dotnet publish src/RoslynMcp.LogViewer/RoslynMcp.LogViewer.csproj -c Release -f net10.0 -o ./publish/logviewer

# Verify MCP server zip contents BEFORE creating the release — check for unexpected executables
# RoslynMcpA.exe is a local dev copy created by pub.ps1 and must NOT be included in releases
Get-ChildItem ./publish/net8.0/*.exe, ./publish/net10.0/*.exe | Select-Object Name
# Expected: only RoslynMcp.exe. If RoslynMcpA.exe appears, exclude it explicitly.

# Zip MCP server targets (excluding local dev copy)
$exc = @("RoslynMcpA.exe")
Compress-Archive -Path (Get-ChildItem ./publish/net8.0  | Where-Object { $_.Name -notin $exc }) -DestinationPath ./artifacts/RoslynMcp-vX.Y.Z-alpha-net8.0.zip
Compress-Archive -Path (Get-ChildItem ./publish/net10.0 | Where-Object { $_.Name -notin $exc }) -DestinationPath ./artifacts/RoslynMcp-vX.Y.Z-alpha-net10.0.zip

# Zip log viewer
Compress-Archive -Path ./publish/logviewer/* -DestinationPath ./artifacts/RoslynMcp-LogViewer-vX.Y.Z-alpha-net10.0.zip
```

### 4. Create GitHub Release

> **Always create as a draft first.** Draft releases are fully mutable — you can upload, remove, or replace assets freely, and the tag is not locked until you publish. Only publish when everything is verified.
>
> ⚠️ **PUBLISH POLICY: NEVER publish a release without TWO explicit user confirmations.** Always create as `--draft`. Never run `gh release edit --draft=false` unilaterally — ask the user to confirm, wait for acknowledgement, then ask again. Only run the publish command after both confirmations.

```powershell
# Step 1: create as DRAFT — tag is not locked yet
gh release create vX.Y.Z-alpha --draft --prerelease `
  --title "RoslynMcp vX.Y.Z-alpha" `
  --notes-file "$env:TEMP\release-notes.md" `
  ./artifacts/RoslynMcp-vX.Y.Z-alpha-net8.0.zip `
  ./artifacts/RoslynMcp-vX.Y.Z-alpha-net10.0.zip `
  ./artifacts/RoslynMcp-LogViewer-vX.Y.Z-alpha-net10.0.zip

# Step 2: verify assets on the release page, test the zips

# Step 3: NEVER run this unilaterally — requires TWO explicit user confirmations.
# Ask the user to confirm, wait for acknowledgement, then confirm again before running:
# gh release edit vX.Y.Z-alpha --draft=false
```

- [ ] Create draft release with all 3 zip artifacts
- [ ] Verify zip contents and release page look correct
- [ ] Publish (un-draft) — ⚠️ requires TWO explicit user confirmations; never publish unilaterally

### 5. Publish to NuGet (Future)
```bash
dotnet nuget push artifacts/RoslynMcp.0.X.Y.nupkg \
    --api-key $NUGET_API_KEY \
    --source https://api.nuget.org/v3/index.json
```

### 6. Announce Release
- [ ] Update README.md with new installation instructions (if NuGet published)
- [ ] Post announcement (Twitter, Reddit /r/dotnet, etc.)
- [ ] Update MCP directory listing (if applicable)

---

## Post-Release

- [ ] Monitor GitHub issues for bug reports
- [ ] Update milestone/project board
- [ ] Create next version milestone (if using milestones)
- [ ] Start `[Unreleased]` section in CHANGELOG.md for next version

---

## Hotfix Process

If a critical bug is found post-release:

1. Create hotfix branch from release tag:
   ```bash
   git checkout -b hotfix/v0.X.Y+1 v0.X.Y
   ```

2. Fix the bug with minimal changes

3. Follow release process above with incremented patch version

4. Merge hotfix back to main and dev branches

---

## Version Numbering (Semantic Versioning)

- **Major (X.0.0)**: Breaking changes, major feature additions
- **Minor (0.X.0)**: New features, backward-compatible
- **Patch (0.0.X)**: Bug fixes, backward-compatible

**Pre-release suffixes:**
- `-alpha`: Early development, unstable API
- `-beta`: Feature-complete, testing phase
- `-rc.N`: Release candidate N

**Pre-1.0 conventions (current):**

In pre-1.0, breaking changes can happen in any release. Use MINOR bumps (`0.X`) for anything notable enough to call out in release notes; use PATCH (`0.0.X`) for bug fixes and small enhancements.

Guidelines for what gets which bump:
- New tools or significant new surface area → MINOR (`0.X.0`)
- Logging rework, protocol changes, major internal rewrites → MINOR
- Description rewrites, attribute metadata, polish → PATCH
- Bug fixes, correctness corrections → PATCH

**Milestone naming:** Milestones omit the pre-release suffix (use `v0.7.2`, not `v0.7.2-alpha`) — the suffix is noise at the planning level.

**Tags and releases:** Always include the suffix (e.g. `v0.7.2-alpha`). Mark GitHub releases as pre-release until v1.0.0-beta.

---

## First Public Release (v1.0.0) Criteria

Before declaring v1.0.0, ensure:
- [ ] All 37 public tools stable and well-tested (39 total including 2 debug-only)
- [ ] Comprehensive test coverage (>80%)
- [ ] Documentation complete and polished
- [ ] CI/CD pipeline operational
- [ ] Published to NuGet
- [ ] No known critical bugs
- [ ] API is stable (no breaking changes planned)
- [ ] At least 3 MCP clients verified working
- [ ] Community feedback incorporated (if any)

---

## Notes

- Keep release notes user-focused (features, not commits)
- Acknowledge contributors in release notes
- Include upgrade instructions for breaking changes
- Link to relevant issues/PRs in CHANGELOG
