# Changelog: DevInteg.DropboxSign

Released as tags `dropboxsign/vX.Y.Z`; the format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [SemVer](https://semver.org/).

## Unreleased

## [0.1.0] - 2026-11-02

### Added
- `DropboxSignClient`: create an embedded request, find by metadata, get, embedded sign URL, cancel, download PDF.
- `DropboxSignCallbacks`: callback verification (`Content-Sha256` and `event_hash`, replay window) and the event-type names.
- `SigningLinkTokens` over a rotating `SigningLinkKeyRing`.
- `AddDropboxSignClient()` and `AddDropboxSignSigningLinks()`.
