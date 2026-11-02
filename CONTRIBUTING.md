# Contributing

Issues are welcome. Name the package in the title or pick its label. For anything larger than a small fix, open an issue to discuss it before you send a pull request.

A pull request should:

- build with no warnings: `dotnet build DevInteg.slnx` (warnings are errors);
- pass `dotnet test --solution DevInteg.slnx` and `dotnet format DevInteg.slnx --verify-no-changes`;
- come with a test that fails without the change;
- record any public API change in the package's `PublicAPI.Unshipped.txt` and its `CHANGELOG.md` under *Unreleased*;
- add no new dependency without a reason in the description. Only permissive licences are accepted (MIT, Apache-2.0, BSD).

Tests never call a vendor. Each package's live tests run only on demand, with credentials, from a maintainer's machine or the manual workflow.

By contributing, you agree that your contribution is licensed under the MIT licence of this repository.
