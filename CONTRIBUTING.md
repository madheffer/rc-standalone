# Contributing

## Code style

The house style is the [dotnet/runtime C# coding style][runtime-style], which is
also what [Microsoft's own C# conventions][ms-conventions] are adopted from. It
is the closest thing C# has to a default, so if you have worked on any .NET
codebase you already know it and there is nothing here to learn.

It lives in [`.editorconfig`](.editorconfig) and is applied by the formatter:

```bash
dotnet format Source2Compiler.slnx --exclude third_party
```

CI runs the same command with `--verify-no-changes`. **Formatting is not a code
review topic here.** If the formatter is happy, the style is correct; if it is
not, run it.

The one place `.editorconfig` departs from runtime is local `const` naming, and
the file says why at the rule.

## Comments (hard rule: 20 lines, one exception)

The formatter cannot judge these, so this is the part that needs a human.

**People come here to read code, not prose about code. No comment block may
exceed 20 lines, and most should be under 10.** Not a class summary, not a file
header, not a `//` run inside a method. The tree was brought under this cap by
hand, so it starts true: a block over it is something you added. If it will not
fit, it is an article, and it belongs in `docs/` or the README where someone
looking for it will actually find it.

**The one exception is a binary layout table.** A `<code>` block spelling out the
bytes of an undocumented Source 2 structure may run long, because that table IS
the spec and it has to sit beside the code that reads or writes it. The prose
around it still obeys the cap.

**A comment earns its place by saying something the code cannot.** Almost always
that is *why*, not *what*. This codebase is reverse-engineered from a closed
format, so the highest-value comments are the measurements behind a constant and
the trap behind a workaround. Keep those, with their numbers:

```csharp
// 400 of 400 sampled stock vector graphics carry an EMPTY name table, so an
// authored empty one is not a simplification.
```

Write, in order of preference:

1. **A measured fact**, with the sample size. `// 93 of 200 sampled` is worth ten
   lines of prose, because the next person can check it.
2. **The trap.** What breaks if this line is written the obvious way, and how it
   fails. Prefer the observable symptom: "the streamer fails to bind and the
   material FATAL-errors" beats "this is important".
3. **A binary layout**, as a table. There is no public spec for most of these
   structures, so the comment is the spec.

Do not write:

- **Restatements.** If the comment paraphrases the line under it, delete it.
- **Changelog.** "Before this, we used to..." belongs in `git log` and in
  `docs/RC_PARITY.md`, not above a function. Document what the code does now.
- **Roadmap.** Plans for other projects, or for work nobody has scheduled.
- **The same explanation twice.** Put it in the one place it belongs and
  `<see cref="..."/>` the other.
- **Section banners made of box-drawing characters.** A short `// helpers` label
  is fine; the rule out to column 80 is not, and it rots when the text changes.
- **Bulleted lists enumerating members, parameters or routes** the reader can see
  immediately below. Document the RULE they share instead.

### XML documentation

Public API gets `///`. Keep the `<summary>` to a few lines that say what the
member is for; if there is more to say, put it in a `<para>` below, and if there
is a lot more, it belongs in `docs/`. A `<summary>` that runs past about ten
lines is usually an article that lost its way.

Some mechanical rules, mostly from the Microsoft conventions:

- `//` for ordinary comments; never `/* */`.
- One space after `//`, and the comment on its own line rather than trailing code.
- **One `<summary>` per member.** Two stacked is a merge accident and the
  compiler will not tell you.
- **No em dashes** anywhere in prose. Use a plain `-`.
- Prefer ASCII. Mathematical symbols are fine where they carry meaning.

### Keep documentation true

A wrong comment is worse than no comment, and it is the one kind of rot nothing
in CI catches. When you change behaviour, re-read the doc comment above it and
the `<see cref="..."/>` links pointing at it. Stale summaries in this repo have
outlived the thing they described by months.

## Before you open a pull request

```bash
dotnet run tools/vendor.cs                 # fetch pinned VRF, apply patches
dotnet build -c Release                    # must be warning-free
dotnet test -c Release --no-build
dotnet run -c Release --no-build --project src/Source2.Compiler.Cli -- selftest
dotnet format Source2Compiler.slnx --verify-no-changes --exclude third_party
```

The build is warning-free today; please keep it that way. Tests that need real
game bytes skip when CS2 is not installed, so a green run on a machine without
it is expected and the self-test still compiles every supported type.

## Changing a VRF patch

Patches under `third_party/patches/` are literal find/replace pairs against the
pinned upstream commit, and applying one aborts if the snippet is missing or
matches more than once. If you add a patch the compiler's behaviour depends on,
add its id to `VrfRequirements.RequiredPatchIds` too; `VrfRequirementsTests`
checks the two agree.

[runtime-style]: https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md
[ms-conventions]: https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions
