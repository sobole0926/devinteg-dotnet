# DevInteg .NET libraries

Small, focused .NET libraries from [DevInteg Consulting](https://devinteg.com), extracted from production projects and published under the MIT licence.

| Package | What it does | NuGet |
|---|---|---|
| [DevInteg.DropboxSign](src/DevInteg.DropboxSign) | Thin Dropbox Sign client for embedded signing: requests, status, cancel, download, verified callbacks, signing-link tokens | [![NuGet](https://img.shields.io/nuget/v/DevInteg.DropboxSign.svg)](https://www.nuget.org/packages/DevInteg.DropboxSign) |

## Layout

- `src/<Package>/`: the library, its `README.md` (shown on nuget.org), its `CHANGELOG.md` and its `PublicAPI.*.txt`.
- `tests/<Package>.Tests/`: its tests (xUnit v3 on Microsoft.Testing.Platform).

Each package is versioned and released on its own. The version comes from a git tag with the package's prefix, for example `dropboxsign/v1.2.0` ([MinVer](https://github.com/adamralph/minver)). Pushing such a tag on `main` publishes that package to nuget.org.

## Build

```
dotnet build DevInteg.slnx
dotnet test --solution DevInteg.slnx
dotnet format DevInteg.slnx --verify-no-changes
```

The SDK version is pinned in `global.json` (.NET 10). Warnings are errors, and every public API change must be recorded in that package's `PublicAPI.Unshipped.txt`.

## Adding a package

1. Add `src/DevInteg.<Name>/` with `PackageId`, `Description`, `PackageTags` and `MinVerTagPrefix` set to `<name>/v`. Also add a `README.md`, a `CHANGELOG.md` and empty `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` files containing `#nullable enable`.
2. Add `tests/DevInteg.<Name>.Tests/` and both projects to `DevInteg.slnx`.
3. Map the tag prefix to the project in `.github/workflows/release.yml`.
4. Add a row to the table above and a label for the package.

## Contributing and security

Read [CONTRIBUTING.md](CONTRIBUTING.md) before contributing. To report a vulnerability, follow [SECURITY.md](SECURITY.md) and don't open a public issue.
