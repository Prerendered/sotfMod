---
name: gh-workflows
description: CI workflow and branch protection templates — load when dispatched as cicd.
---

# GitHub workflows

## Rules

- Verify each action's current release in its repo, then pin the **commit SHA**
  with the tag as a trailing comment. Never pin a mutable tag.
- Only include jobs the project actually uses — no template leftovers.
- Cache dependencies for the stack's package manager.
- CI must run typecheck + tests on every PR.

## CI template (Bun stacks)

`.github/workflows/ci.yml`:

```yaml
name: ci
on:
  pull_request:
    branches: [main]

jobs:
  check:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@<verify-current-sha> # vX.Y.Z
      - uses: oven-sh/setup-bun@<verify-current-sha> # vX.Y.Z
        with:
          bun-version: latest
      - uses: actions/cache@<verify-current-sha> # vX.Y.Z
        with:
          path: ~/.bun/install/cache
          key: bun-${{ runner.os }}-${{ hashFiles('bun.lock', 'bun.lockb') }}
      - run: bun install --frozen-lockfile
      - run: bun run typecheck
      - run: bun test
```

Replace every `<verify-current-sha>` after checking the action's releases page —
that verification is the cicd agent's job, per its contract.

Stack variations:
- expo: add `npx expo-doctor`; typecheck via `tsc --noEmit`.
- tauri: frontend checks as above; add `cargo check` in `src-tauri/` only if Rust code changed.
- extension: add a production build step (`bun run build`) to catch MV3 manifest errors.
- any other stack (unity, react-spa, …): same rules, different tools — find the
  stack's canonical CI actions (e.g. game-ci for Unity), verify and pin their
  SHAs, cache the stack's package/build artifacts, run whatever the project's
  real check commands are per team/architecture.md.

## Branch protection

Apply via gh after the repo exists:

```bash
gh api -X PUT "repos/{owner}/{repo}/branches/main/protection" \
  -H "Accept: application/vnd.github+json" \
  -F "required_status_checks[strict]=true" \
  -F "required_status_checks[contexts][]=check" \
  -F "enforce_admins=true" \
  -F "required_pull_request_reviews=null" \
  -F "restrictions=null"
```

Result to verify: direct push to main rejected; PRs require the `check` job green.
