# Code signing policy

## Current status

WitherChat's application was submitted to SignPath Foundation on 2026-10-03
and is awaiting review. Receipt was acknowledged; this is not approval.
It has **not** been enrolled or approved, and current EXEs are unsigned.
An MIT license or passing tests does not replace an Authenticode signature.
Do not disable Windows security, import a self-signed root certificate, or
claim an unsigned build is trusted merely to run this development release.

If approved, the provider acknowledgement will be:
"Free code signing provided by SignPath.io, certificate by SignPath Foundation."
This is a future acknowledgement, not a claim that signing is available today.

## People and approval

- Maintainer, committer and reviewer: [WitherOffic](https://github.com/WitherOffic).
- Release/signing approver: [WitherOffic](https://github.com/WitherOffic).
- External changes and all build/signing configuration require maintainer review.
- Every signed release requires explicit human approval in SignPath.
- Multi-factor authentication is required for GitHub and SignPath accounts.
  Setup must be verified by the maintainer; this repository does not assert
  that account-level settings have already been configured.

## Build provenance

Development, audits and local packaging take place on the project's srv host.
The maintainer approved GitHub-hosted runners only for public release builds
and signing provenance. No self-hosted build may be represented as an
approved GitHub-hosted signing artifact.

The Windows release workflow builds a pinned source commit on windows-latest,
uses actions pinned to immutable commit hashes, and uploads the standalone
package and its required notices. It currently produces **unsigned** artifacts.
Signing is not enabled until Foundation acceptance, account setup and
artifact-policy review. Tokens must be stored in GitHub Actions secrets,
never in source, logs, public issues or chat messages.

The draft artifact configuration in .signpath/ signs only the project's
standalone WitherChat.exe, with product/company/version restrictions.
Upstream library binaries must not be signed as if they were WitherChat's
own code. Native components extracted by .NET require an actual Smart App
Control compatibility test after signing; a signed outer EXE alone is not
a claim that every extracted component has been validated.

## Privacy and removal

See [PRIVACY.md](PRIVACY.md) for network behavior and local data.
No project-owned telemetry collector is configured. Requests to enabled
chat providers, OAuth endpoints and media CDNs are required for their
features and may resume automatically from saved settings.

WitherChat's Windows package is portable: close the app (use Exit from the
tray if necessary), then remove its extracted package/files. Local settings,
sessions and chat logs are separate. Remove the WitherChat application-data
folder only if you intentionally want to erase them; exporting needed chat
logs first is recommended.

## References

- https://signpath.org/terms.html
- https://signpath.org/apply
- https://docs.signpath.io/trusted-build-systems/github
- https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control
