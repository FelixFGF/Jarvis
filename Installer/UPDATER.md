# Jarvis in-app updater

The Windows x64 updater uses the public GitHub Releases API for `FelixFGF/Jarvis`. It never treats source archives as application packages. A release eligible for the selected Stable or Beta channel must contain all three files:

- `Jarvis-Update-win-x64.zip`
- `Jarvis-Update-win-x64.json`
- `Jarvis-Update-win-x64.sig`

The JSON manifest contains format version, semantic version, exact package name and size, and SHA-256. The detached signature is RSA-PSS/SHA-256 over the exact UTF-8 manifest bytes. The application pins only the public verification key. A missing or invalid signature prevents installation; the release page remains available as a manual fallback. SHA-256 alone is not treated as authentication.

## One-time signing configuration

Generate the RSA private key in a controlled environment and retain it in an access-controlled secret manager. Put only the matching public key in `UpdateSecurity.PinnedPublicKeyPem`, review it, and never commit or distribute the private key. The repository Actions secret must be named `JARVIS_UPDATE_SIGNING_PRIVATE_KEY_PEM` and contain the PEM private key. The build workflow fails closed if this secret is absent. Rotate keys only with a planned application update that pins the replacement public key before using it to sign releases.

## Publishing

1. Set the application `Version` in `Jarvis.csproj` to the intended SemVer release.
2. Prepare human-readable release notes and create a matching tag such as `v1.2.3` (or a SemVer prerelease tag for Beta).
3. Push the tag. The Windows workflow builds the self-contained setup and update payload, signs the manifest with the Actions secret, and attaches all artifacts to the GitHub Release.
4. Confirm that the release is not a draft, the tag matches the application version, and all three update assets are present before announcing it.

For local packaging, set `JARVIS_UPDATE_SIGNING_PRIVATE_KEY_PEM` only in the current protected shell environment, then run `./Installer/Build-UpdatePackage.ps1 -Configuration Release -Version 1.2.3`. Do not save a private key in a tracked file or pass it on a command line.

The update helper waits for Jarvis to exit, verifies signature and digest, rejects unsafe archive paths, stages extraction, backs up overwritten files, and restores those files on a copy failure. Unlisted files are preserved; `JarvisData` is never included in this package or overwritten. The helper validates the existing Jarvis install marker and never elevates itself. If Windows denies write access, it stops with instructions to restart Jarvis using **Run as administrator** (which uses standard Windows UAC) or install into a user-writable folder. If installation cannot be completed, it reports an error and keeps the previous files where possible.

## Limitations

- GitHub release discovery and actual update application require a published signed release; these are not validated against a live release during ordinary development builds.
- Windows Authenticode is not configured. Authenticity is based on a pinned RSA public key and the signing key must be protected as a repository Actions secret.
- A clean-install setup is still available in addition to the in-app update archive.
- Beta clients must select the Beta channel in About. Stable clients ignore prereleases.
