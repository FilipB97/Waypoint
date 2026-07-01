# Security Policy

## Reporting a vulnerability

If you discover a security vulnerability in RDP Manager, please report it **privately** so it
can be fixed before public disclosure.

- Preferred: open a [GitHub Security Advisory](../../security/advisories/new) for this
  repository (private to maintainers).
- Alternatively: email the maintainer at **filip.benklewski@absysco.pl** with the details.

Please include:

- affected version / commit,
- a description of the issue and its impact,
- steps to reproduce (proof of concept if possible).

We aim to acknowledge reports within a few business days and will keep you informed about the
fix and disclosure timeline. Please do not open a public issue for security problems.

## Scope & design notes

- Passwords are stored in the **Windows Credential Manager** (DPAPI). The application does not
  persist passwords in its own files (`servers.json` / `settings.json` hold only non-secret
  metadata).
- Server-identity verification (RDP `AuthenticationLevel`) is configurable per server and
  defaults to *warn on failure* (`2`). Lowering it to *don't check* (`0`) disables protection
  against man-in-the-middle attacks and is not recommended.
- The RDP transport, encryption, and credential negotiation are handled by the Microsoft RDP
  ActiveX control (`mstscax`) shipped with Windows.

## Supported versions

This project is under active early development; security fixes target the latest `master`.
