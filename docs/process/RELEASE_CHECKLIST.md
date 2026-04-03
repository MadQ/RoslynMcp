# Release Checklist

Use this checklist when preparing a new release of RoslynMcp.

---

## Pre-Release Verification

### Code Quality
- [ ] All source files follow code style guidelines (`.github/copilot-instructions.md`)
- [ ] No compiler warnings (`dotnet build src/RoslynMcp/RoslynMcp.csproj`)
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
- [ ] Update version in `.csproj` files
- [ ] Update CHANGELOG.md
  - [ ] Move `[Unreleased]` items to new version section
  - [ ] Add release date
  - [ ] Add link to GitHub release
- [ ] Commit version bump: `git commit -m "Release v0.X.Y"`

### 2. Tag Release
```bash
git tag -a v0.X.Y -m "Release v0.X.Y"
git push origin v0.X.Y
```

### 3. Build Release Artifacts
```bash
# Build for all targets
dotnet build src/RoslynMcp/RoslynMcp.csproj -c Release

# Create NuGet package (if publishing)
dotnet pack src/RoslynMcp/RoslynMcp.csproj -c Release -o artifacts/

# Verify package contents
dotnet nuget verify artifacts/RoslynMcp.0.X.Y.nupkg
```

### 4. Create GitHub Release
- [ ] Go to https://github.com/MadQ/RoslynMcp/releases/new
- [ ] Select tag `v0.X.Y`
- [ ] Title: `RoslynMcp v0.X.Y`
- [ ] Description: Copy relevant section from CHANGELOG.md
- [ ] Attach artifacts (if applicable)
- [ ] Mark as pre-release if alpha/beta
- [ ] Publish release

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

---

## First Public Release (v1.0.0) Criteria

Before declaring v1.0.0, ensure:
- [ ] All 18 MVP tools stable and well-tested
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
