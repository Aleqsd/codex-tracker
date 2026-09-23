# Code signing policy

**Current status: no signing service is active. Released Codex Tracker binaries are currently unsigned.** The Foundation application submitted on 23 September 2026 did not obtain signing access. The prepared integration remains disabled; no approval by SignPath Foundation is claimed.

Codex Tracker is maintained by [Aleqsd](https://github.com/Aleqsd). The intended signing process uses [SignPath Foundation](https://signpath.org/) for this MIT-licensed project, subject to its acceptance and conditions.

## Responsibilities and release approval

- Source maintainer and reviewer: Aleqsd.
- Intended release signing approver: Aleqsd; the role must be assigned in SignPath before activation.
- GitHub and SignPath access must use multi-factor authentication. This requirement is not a claim that account configuration has already been verified.
- Signing requests must be approved manually in SignPath. The workflow token may submit requests, but must not have approval or administrative privileges.

Only binaries built from this repository on GitHub-hosted runners are eligible. The signing policy must restrict origin to `Aleqsd/codex-tracker`, branch `main` and workflow `.github/workflows/signed-release.yml`. Manual uploads and other build origins must not be allowed for the release policy. The current implementation requests approval separately for the application and the installer.

The application is signed first. The installer is then built with that application and signed separately. Windows must accept both embedded Authenticode signatures, the configured publisher and their timestamps before the candidate is produced. SHA-256 values are calculated from the final files. The workflow tests the executable and installer and does not publish a Release automatically. Unsigned rehearsals are labelled explicitly and are not signed releases.

The Inno Setup uninstaller is not separately signed by this integration. Acceptance of the artifact configurations and the first real signed build remain to be verified with SignPath.

## Privacy

See [Data and privacy](PRIVACY.md) for local data, GitHub updates, Codex/OpenAI access and optional integrations. Signing submits public build artifacts and source/build metadata to SignPath, never users' accounts, sessions, usage data or notification keys. No SignPath service is added to the installed application's runtime.

After acceptance and the first verified signature, this page and the release notes will include the required attribution: “Free code signing provided by SignPath.io, certificate by SignPath Foundation.” Until then, that sentence describes the planned attribution only.
