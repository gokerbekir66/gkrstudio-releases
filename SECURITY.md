# Security policy

## Supported versions

Only the latest release gets security fixes. GKR Studio checks for updates automatically (Settings → Updates); please keep it on.

| Version | Supported |
|---|---|
| Latest release | ✅ |
| Older versions | ❌ — update to the latest |

## How updates are protected

- Every update is described by `update.json`, signed with ECDSA P-256 / SHA-256. The public key is built into the app, so a changed or fake update is refused.
- Each package is also checked with its SHA-256 hash before it is installed.
- ZIP and archive extraction blocks path escapes, links and zip bombs, and always extracts into a new folder.
- GKR Studio never overwrites your files: results are saved next to the original with a new name.

## Reporting a vulnerability

Please **don't** report security problems in a public issue.

Use GitHub's private reporting instead: open the **Security** tab of this repository and click **Report a vulnerability**. Include the version, your operating system, the steps to reproduce and what an attacker could do.

You'll get a reply as soon as possible, usually within a week. When the problem is confirmed, a fixed version is released and you'll be credited in the release notes if you want.
