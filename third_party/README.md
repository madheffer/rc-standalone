# third_party

Upstream code this project builds on. Nothing here except the patches and the
pin is committed.

| | |
|---|---|
| `VENDORED.json` | the exact ValveResourceFormat commit this project is built and tested against |
| `patches/` | the twelve literal find-and-replace patches applied to that commit |
| `PATCHES.md` | why each patch exists, and how to move the pin |
| `ValveResourceFormat/` | **not committed.** Fetched by `dotnet run tools/vendor.cs` |

Run the vendor step once after cloning:

```bash
dotnet run tools/vendor.cs
```

It shallow-fetches the pinned commit, applies the patches, and reports what it
did. Re-running is safe: an already-applied patch is detected and counted as
such rather than applied twice.

```bash
dotnet run tools/vendor.cs -- --check
```

verifies the tree is fetched and fully patched without changing anything, which
is what CI runs before the build.

## Licensing

[ValveResourceFormat](https://github.com/ValveResourceFormat/ValveResourceFormat),
[ValvePak](https://github.com/ValveResourceFormat/ValvePak) and
[ValveKeyValue](https://github.com/ValveResourceFormat/ValveKeyValue) are MIT,
Copyright their respective contributors. ValvePak and ValveKeyValue are consumed
as published NuGet packages and are not patched; only VRF is vendored, because
only VRF needs patching.

[bc7enc](../src/Source2.Compiler/Native/bc7enc) is vendored in source form under
`src/`, not here, because it is compiled by this project rather than fetched.
Richard Geldreich, MIT / public domain; its license file sits beside the sources.
