# Rigol

.NET tooling for Rigol oscilloscopes (mask testing, waveform capture, an
Avalonia UI) built on top of [NET_IPTE_SCPI](https://github.com/charclo/NET_IPTE_SCPI),
a shared SCPI driver library currently used as a git submodule (see
"Switching to the NuGet package" below for where that's headed).

- `Rigol/` — console app: mask bench, self-test, profiling.
- `Rigol.UI/` — Avalonia desktop UI.
- `NET_IPTE_SCPI/` — the SCPI submodule (see below).
- `Documentation/` — scope programming guides and manuals.

## Getting started

Clone with submodules in one go:

```sh
git clone --recurse-submodules https://github.com/charclo/rigol.git
```

Already cloned without `--recurse-submodules`? Run the bootstrap script once:

```sh
./scripts/bootstrap.sh      # macOS/Linux/WSL/git-bash
./scripts/bootstrap.ps1     # Windows PowerShell
```

This does three things:
1. `git submodule update --init --recursive` — fetches NET_IPTE_SCPI.
2. `git config submodule.recurse true` — from then on, `git pull` / `git checkout`
   keep the submodule in sync automatically.
3. `git config core.hooksPath .githooks` — installs hooks (`.githooks/`) that
   catch the remaining cases `submodule.recurse` doesn't (e.g. `git fetch && git merge`).

After that you should never need to run `git submodule update --init` by hand
again in this repo — pulling, checking out a branch, or switching between
branches with different NET_IPTE_SCPI pins all "just work".

Then open `Rigol.sln`, or use the VS Code tasks (`build`, `run ui`, `run selftest`, …).

## Working from a customer site (no route to GitHub)

Some fixes get made directly on a customer's production PC while debugging
against real hardware there, and those sites don't all have the same network
access — some can reach github.com, some can't. `scripts/site-sync.sh` works
either way:

```sh
./scripts/site-sync.sh          # uses the current branch
./scripts/site-sync.sh my-branch
```

It tries a normal `git push` first. If that fails, it packages your commits
into a single portable `git bundle` file under `site-bundles/` (already
git-ignored — never commit one) instead, with the follow-up command printed
for you.

Back at a connected machine, in an up-to-date clone of this repo:

```sh
./scripts/apply-bundle.sh site-bundles/my-branch-20260101-120000.bundle
```

This fetches the bundle's commits into a local `site-import/<branch>`
branch so you can review them (`git log origin/<branch>..site-import/<branch>`)
before pushing them for real — the script prints the exact push command.

No commit history, authorship, or timestamps are lost either way; a bundle
is just git's own transfer format carried over a USB stick or shared drive
instead of a network connection.

## Switching to the NuGet package

NET_IPTE_SCPI is also used by other C# drivers (e.g. a DMM driver) outside
this repo, which is the actual motivation for moving off the submodule: a
NuGet package is the standard way to share one library across several repos
without git submodule bookkeeping in each of them.

NET_IPTE_SCPI now publishes itself as a package to a local Gitea feed on
every `vX.Y.Z` tag (see its own README, "Publishing a new version") — but
this repo hasn't cut over to consuming it yet. `nuget.config` here already
has the Gitea source wired up, scoped via `packageSourceMapping` so it only
applies to the `NET_IPTE_SCPI` package and has no effect until the switch
below is made — today's `ProjectReference`/submodule setup, and this repo's
GitHub-hosted (cloud) CI, keep working exactly as before regardless.

The switch is deliberately not done automatically here because it needs
two things confirmed working first, neither of which can be checked from
this repo: that the Gitea feed is actually reachable at the URL filled into
`nuget.config`, and that a package has actually been published to it (push
a `vX.Y.Z` tag on NET_IPTE_SCPI and confirm the publish workflow succeeds).
Once both hold:

1. In `nuget.config`, replace `GITEA-HOST` and `GITEA-OWNER` with the real
   feed URL.
2. Set `GITEA_NUGET_USER` / `GITEA_NUGET_TOKEN` as environment variables on
   any machine that will restore this repo (a Gitea access token with
   `read:package` scope is enough for restoring).
3. In `Rigol/Rigol.csproj`, replace the `ProjectReference` with:
   ```xml
   <PackageReference Include="NET_IPTE_SCPI" Version="0.2.0" />
   ```
   (whatever version was actually published).
4. The `NET_IPTE_SCPI/` submodule and `.gitmodules` can then be removed,
   along with the bootstrap-script/hooks setup above — none of it is needed
   once nothing references the submodule anymore.
5. This repo's own CI (`.github/workflows/dotnet.yml`) restores from a
   GitHub-hosted runner, which — same as NET_IPTE_SCPI's publish workflow —
   can't reach a LAN-only Gitea. Once step 3 lands, that workflow needs a
   self-hosted runner too (see NET_IPTE_SCPI's README for how to register
   one), or `dotnet restore` there will fail to resolve the package.

Until then, the submodule is what's actually in use, so the rest of this
README documents that setup.

## Using NET_IPTE_SCPI as a submodule in another driver repo

To get the same "clone it and it just works" behavior in another C# driver
repo (e.g. a DMM driver):

1. Add the submodule, pinned to the same branch/tag every other driver repo
   uses (see the note below on why this matters):
   ```sh
   git submodule add -b <branch> https://github.com/charclo/NET_IPTE_SCPI.git NET_IPTE_SCPI
   ```
2. Reference it from your driver's `.csproj` the same way `Rigol/Rigol.csproj`
   does:
   ```xml
   <ItemGroup>
     <ProjectReference Include="..\NET_IPTE_SCPI\NET_IPTE_SCPI.csproj" />
   </ItemGroup>
   ```
3. Copy `scripts/bootstrap.sh`, `scripts/bootstrap.ps1`, and `.githooks/`
   from this repo into the new one, unchanged.
4. Mention `./scripts/bootstrap.sh` (or `.ps1`) in that repo's own README as
   the required one-time setup step after cloning.
5. If that repo has CI, make sure its checkout step pulls submodules too
   (see `.github/workflows/dotnet.yml` here for the `submodules: recursive`
   option on `actions/checkout`).

### A note on branch pinning across drivers

`.gitmodules` currently pins NET_IPTE_SCPI to `feature/buffered-tcp-read`, a
branch that's still moving. With `submodule.recurse`/the hooks in place,
every checkout now tracks that branch's *pinned commit* (not its live tip —
submodules always check out the exact commit recorded in the parent repo,
so this is safe from surprise updates). The thing worth watching is that
every driver repo pins NET_IPTE_SCPI to the *same* commit/branch; if the DMM
driver and this repo drift onto different commits of NET_IPTE_SCPI, a bug
fixed in one won't be present in the other. Once the feature branch work
lands on a stable branch, repointing `.gitmodules` here (and in the other
driver repos) to that branch is worth doing.

To pick up a newer commit of NET_IPTE_SCPI deliberately:

```sh
cd NET_IPTE_SCPI
git checkout <branch-or-commit>
cd ..
git add NET_IPTE_SCPI
git commit -m "Bump NET_IPTE_SCPI to <reason>"
```
