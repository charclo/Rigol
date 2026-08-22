# Rigol

.NET tooling for Rigol oscilloscopes (mask testing, waveform capture, an
Avalonia UI) built on top of [NET_IPTE_SCPI](https://github.com/charclo/NET_IPTE_SCPI),
a shared SCPI driver library used as a git submodule.

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

## Why a submodule, and not a NuGet package

NET_IPTE_SCPI is also used by other C# drivers (e.g. a DMM driver) outside
this repo. A NuGet package would be the more standard way to share it across
those repos, but that needs a package feed reachable from every machine that
builds them, and self-hosting one (e.g. on a Gitea NuGet feed) is more
infrastructure than the current setup warrants. The submodule keeps things
dependency-free — the trade-off is that submodules are easy to forget about,
which is what the bootstrap script and hooks above are for. If a shared feed
ever becomes available, switching NET_IPTE_SCPI's `ProjectReference` to a
`PackageReference` is a one-line change per driver repo.

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
