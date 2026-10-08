# Contributing

Thanks for helping. Please read this page and the [Code of Conduct](CODE_OF_CONDUCT.md) first.

## Before you start

- For a bug or a small fix, open a pull request or an issue.
- For a new feature or a change to the public API, open an issue first so we can agree on the shape before you write
  code.
- Report security problems privately, see [SECURITY.md](SECURITY.md).
- Server behaviour and the HTTP API belong to [koldera-core](https://github.com/koldera/koldera-core). This repository
  only wraps them. Changes must not make the SDK claim more than a machine check: no claim that a document is "accessible"
  or conformant, no generated alternative text, no guessed structure.

## Licence of contributions

The project is licensed under Apache-2.0. By submitting a contribution you agree that it is licensed under the same
terms (section 5 of the licence). There is no CLA and no DCO sign-off.

## Setup

You need the .NET SDK 10 (it builds both target frameworks) and the .NET 8 runtime (to run the net8.0 tests), plus
Node.js 20 or later for the repository scripts.

```bash
dotnet restore                                    # restores with the committed packages.lock.json files
dotnet build -c Release                           # warnings are errors; also compiles the README and guide examples
dotnet test -c Release                            # unit tests on net8.0 and net10.0
dotnet pack src/Koldera.Sdk -c Release -o artifacts   # nupkg and snupkg, package validation included
node scripts/check-source-hygiene.mjs             # must exit 0
node scripts/check-licences.mjs                   # licences of the restored NuGet packages
node --test scripts/                              # tests of the two scripts above
```

Dependencies are pinned in `Directory.Packages.props` and locked in `packages.lock.json`. After changing a version run
`dotnet restore --force-evaluate` and commit the lock files in the same change. CI restores in locked mode and fails
when they are out of date.

### Documentation examples

Every `csharp` block in `README.md` and `docs/*.md` is extracted and compiled by `tests/Koldera.Sdk.Docs` on each build,
so an example that no longer matches the API breaks the build. Each block is the body of one async method: leading
`using` directives are hoisted, and a block that uses `client` without declaring it receives an `IKolderaClient` named
`client`. Use a `text` or `sh` fence for anything that should not compile. Examples are compiled, not run.

### Contracts

`contracts/openapi.yaml` and the schemas next to it are copied from koldera-core at the commit named in
`contracts/CORE_VERSION`, and the golden fixtures drive the SDK tests (every fixture needs a replay case). When
koldera-core changes its API, update the copy and the commit id together; CI fetches the files at that commit and fails
on a difference (it is skipped with a notice while koldera-core is private and not readable with the workflow's token).

### Source hygiene

The hygiene check scans for internal planning references, hosted-service hostnames, long comment blocks and wording that
claims more than a machine check can. Say what was checked and that human review is still required.

## Making a change

- **Test first.** Write a failing test for the behaviour, then the smallest change that passes it.
- **Keep the public surface honest.** If you add or change a public member, give it an XML doc comment (the build fails
  without one) and update the README example that shows it.
- **Open enums.** Kinds, states, modes and problem codes are strings with constants, never closed enums.
- **No real documents or credentials.** Fixtures are synthetic. Never commit tokens, keys or personal data.
- **Credentials stay out of reach.** The API token is sent only to the configured base URL; signed addresses never get
  it. Keep it out of messages, `ToString` and logs.

## Commit style

Small focused commits, imperative mood, plain messages that say what changed and why when it is not obvious:

```text
Verify the SHA-256 before DownloadToFileAsync moves the file
Keep the request on a redacted network error
Document the compatibility table
```

Avoid messages like "update" or "fix".

## Pull requests

Describe what changed and why, list the commands you ran with their results, and note anything you could not run. Keep
a pull request to one concern. The Koldera team reviews it; expect questions about tests and about what a change could
make the SDK claim. The repository is squash-merged.

CI runs one job on Ubuntu and is skipped for changes that only touch the changelog, the community files or the issue
and PR templates; the README and `docs/` are compiled as snippets, so they run CI. Run `dotnet build -c Release`
yourself after editing an example. The release workflow runs the whole CI job again for the tagged commit.

## Releasing

Releases are made by the maintainers.

One-time setup by the owner:

1. Create an account on https://www.nuget.org, enable two-factor authentication and note the profile name (not the
   e-mail address).
2. Add a trusted publishing policy (nuget.org: your name, Trusted Publishing, create): repository owner `koldera`,
   repository `koldera-dotnet`, workflow file `release.yml` (file name only), environment `nuget`. Choose the policy
   owner (you or an organisation) and allow publishing new packages for the glob `Koldera.*`. A policy for a private
   repository starts as "temporarily active" for seven days and becomes permanent after the first successful publish;
   restart the window in the UI if it expired.
3. In the GitHub repository, store your nuget.org profile name as the secret `NUGET_USER` (Settings, Secrets and
   variables, Actions). It is a name, not a credential; no API key is ever stored.
4. The environment `nuget` exists already and only accepts deployments from tags `v*.*.*`. Optionally add required
   reviewers to it, so every publish waits for an approval.
5. Optional: reserve the ID prefix `Koldera.` (nuget.org: Account, Package ID prefix reservation request) so nobody
   else can publish under it. The request is reviewed by NuGet staff.
6. Once the repository is public, enable private vulnerability reporting (Settings, Code security).

To release:

1. Make sure `main` is green. Until koldera-core is public the CI contract check is skipped, so run it by hand: fetch
   `contracts/*` from koldera-core at the commit in `contracts/CORE_VERSION` and diff them against this repository.
   Set `<Version>` in `src/Koldera.Sdk/Koldera.Sdk.csproj` (the single source of the version) and move the changelog
   entry from "Unreleased" to the version and date.
2. Commit and merge that change through a pull request.
3. Tag the merged commit and push the tag:

   ```bash
   git tag v0.1.0 <commit-on-main>
   git push origin v0.1.0
   ```

4. `release.yml` verifies that the tag matches the csproj version and sits on `main`, runs the whole CI workflow for
   that commit, packs with `ContinuousIntegrationBuild=true`, exchanges the GitHub OIDC token for a short-lived NuGet API
   key (`NuGet/login`), pushes the `.nupkg` and the `.snupkg`, and then creates the GitHub Release with generated notes
   and the packages attached.

The trusted publishing policy is first exercised by the real publish, so the first release can fail at the login or push
step (check the policy values in step 2 and the `NUGET_USER` secret); nothing has been uploaded in that case and the tag
can be re-run. If trusted publishing cannot be used for the first push, fall back to a short-lived API key: create an
API key on nuget.org scoped to the glob `Koldera.*` with push permission and a short expiry, push the packages built from
the tag by hand with `dotnet nuget push artifacts/*.nupkg --api-key <key> --source https://api.nuget.org/v3/index.json`
(the matching `.snupkg` is pushed with it), and delete the key afterwards. Do not store the key as a repository secret.

If a step after the push fails, fix the cause and re-run the failed jobs; NuGet never accepts the same version twice, so
never reuse a version number for different content: bump to the next patch version instead.
