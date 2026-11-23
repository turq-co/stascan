# Publishing StaScan

Maintainer notes for releases, winget and code signing.

## Releases

1. Bump `<Version>` in `StaScan.csproj`.
2. Commit, then push a matching tag:

   ```powershell
   git tag v1.2.0
   git push origin v1.2.0
   ```

3. The [Release workflow](../.github/workflows/release.yml) builds `StaScan.exe`, the installer and the portable zip, and attaches them to a new GitHub release. Installed copies of StaScan pick it up through the in-app update check.

## winget

`winget install turq-co.StaScan` requires the package to be accepted into [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs).

1. After a release is published, generate the manifests:

   ```powershell
   pwsh tools/make-winget-manifest.ps1 -Version 1.2.0
   ```

   This reads the installer URL and SHA-256 from the GitHub release and writes three YAML files to `packaging/winget/manifests/t/turq-co/StaScan/<version>/`, then runs `winget validate`.
2. Submit them, either with [wingetcreate](https://github.com/microsoft/winget-create):

   ```powershell
   wingetcreate submit packaging/winget/manifests/t/turq-co/StaScan/1.2.0
   ```

   or by opening a pull request against `microsoft/winget-pkgs` that adds the same folder.
3. Microsoft's validation pipeline installs the package in a sandbox. Unsigned installers are accepted, but SmartScreen reputation still applies to users.

For later versions, `wingetcreate update turq-co.StaScan --version <new> --urls <installer url> --submit` updates the existing manifest.

## Code signing

Unsigned builds trigger SmartScreen ("Windows protected your PC") and can be blocked entirely by Smart App Control. Signing removes both problems.

**Free option for open-source projects: [SignPath Foundation](https://signpath.org).**

1. Apply at <https://signpath.org/apply> with the repository URL. Requirements include an OSI-approved licence (GPL-3.0 qualifies), a public build from source in CI, and no proprietary components in the signed files.
2. After approval, SignPath creates a project and gives you an organization ID, project slug and signing policy, plus an API token.
3. Add the token as the repository secret `SIGNPATH_API_TOKEN` (Settings → Secrets and variables → Actions).
4. In `release.yml`, after "Build app and installer", upload the unsigned `StaScan.exe` as a workflow artifact and call the [SignPath GitHub action](https://github.com/SignPath/github-action-submit-signing-request) with your IDs; it returns the signed file. Then build the installer from the signed exe and sign the installer the same way.

**Paid alternative: [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/)** (about USD 10 per month, requires identity validation). It integrates with GitHub Actions through `azure/trusted-signing-action`.

Until builds are signed, the README and the release notes tell users how to get past SmartScreen.
