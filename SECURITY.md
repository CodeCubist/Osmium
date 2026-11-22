# Security Policy

## Supported Versions

Only the most recent release of Osmium receives security fixes. Before
reporting a vulnerability, please update to the latest version and confirm the
issue can still be reproduced.

| Version | Supported          |
| ------- | ------------------ |
| latest  | :white_check_mark: |
| older   | :x:                |

## Reporting a Vulnerability

**Do NOT open a public GitHub issue for security problems.**

Please report vulnerabilities privately to the maintainer:

- **Telegram:** [https://t.me/codecubist](https://t.me/codecubist)

If private vulnerability reporting is enabled on this repository, you may
also use **Security → Report a vulnerability** on GitHub.

### What to Include

- The Osmium version you are running
- Your Windows version and build number (e.g. Windows 11 24H2, Build 26100)
- Detailed steps to reproduce the issue, or a proof of concept
- The potential impact (e.g. unauthorized remote access, credential/token
  disclosure, encryption or authentication bypass, privilege escalation)

### What to Expect

- Reports are reviewed on a **best-effort** basis. Whether a report is
  accepted, declined, or deferred — and whether a fix is released — is
  decided case by case, based on the severity and scope of the impact.
  No guarantee of action or remediation is made.
- There is no guaranteed response time.
- Coordinated disclosure is appreciated: please refrain from making a
  report public until it has been reviewed.
- Credit in the release notes is possible but not guaranteed, and only if
  you wish to be credited.

## Scope

In scope:

- The Osmium desktop application (`Osmium.App` and `Osmium.Core`) in this
  repository
- The native capture, transport, and input engine (`OsmiumEngine`)
- Session authentication, end-to-end encryption, and local storage of account
  credentials or device keys

Out of scope:

- Vulnerabilities in third-party dependencies (such as FFmpeg, SDL3, Opus, or
  `usbip2_ude`) — report them upstream, and feel free to notify us as well
- Vulnerabilities in Windows or GPU drivers themselves
- Unofficial or third-party modified builds

## Verifying Official Releases

Always download Osmium from the official
[GitHub Releases](https://github.com/OsmiumRemote/Osmium/releases) or official
build artifacts, and treat unexpectedly modified binaries as suspect.
