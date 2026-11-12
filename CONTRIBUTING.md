# Contributing

Thanks for helping with DotGame.AudioCodecs. Bug reports, fixes and new tests are all welcome.

## Reporting a bug

Open an issue with the file that fails, if you can share it, and what you expected: the samples another
decoder gives, or the file another encoder writes. For a security problem, see `SECURITY.md` instead.

## Making a change

Build and test with the .NET 11 SDK or later:

```bash
dotnet build
dotnet test
```

The build treats every warning as an error, including the code style in `.editorconfig`, so a change
that builds cleanly already follows it. `dotnet format` fixes most style errors.

A few things the library holds to:

- **The Opus port matches libopus exactly.** The decoder gives libopus 1.6.1's samples (its fixed-point
  build) and the encoder its packets and final ranges, byte for byte. A change keeps that, and says
  how it was checked (`eng/reference/` has the checks against native libopus).
- **No dependencies, and trim and AOT safe.** The library references nothing beyond .NET.
- **Each fix comes with a test** that fails without it. Test files go in `test/TestData/<format>/`, and
  damaged files that once broke a decoder in `test/TestData/Fuzz/<format>/`.
- **Public API changes are discussed first,** in an issue, before a pull request. `eng/api/DotGame.AudioCodecs/PublicAPI.Shipped.txt`
  and `eng/api/DotGame.AudioCodecs/PublicAPI.Unshipped.txt` list the public API, and the build fails until a change to it is in
  `Unshipped`: the analyzer's code fix (or `dotnet format analyzers --diagnostics RS0016`) adds it.
- **The docs follow the code.** `docs/api` is generated from the doc comments, and every public type
  and member has one: after changing a public API or its comments, run
  `dotnet run tools/api-docs/generate.cs` (`-- --check` fails if `docs/api` is out of date, and
  `-- --missing` lists members with no comment). After changing `README.md` or `docs/`,
  `python3 tools/api-docs/check-docs.py` checks their links and compiles the samples in the README and the guide. The
  other DotGame repositories' API references use copies of this `generate.cs`, each with its own
  `tools/api-docs/packages.json`.
- **The decoders take untrusted files.** A change to one is fuzzed with `fuzz/` (see its README)
  before it ships.

Open a pull request against `main`. By contributing, you agree that your contribution is licensed under
the MIT License, as the rest of the library is (`LICENSE`).
