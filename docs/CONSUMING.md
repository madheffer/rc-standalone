# How vpkstuff consumes this compiler

Decided 2026-10-12. This library exists mainly to serve vpkstuff (vpkeditor.xyz): the
online resource compiler and the build pipeline. It also has to stay usable on its own,
for anyone who wants it standalone. vpkstuff itself never uses it standalone.

## Requirements

1. **One source of truth.** The compiler is edited here, never inside vpkstuff.
2. **Updates reach vpkstuff without anyone remembering to copy files.** A change landing
   on this repository's `main` should become a vpkstuff branch, tested by vpkstuff's own
   gate, without manual steps.
3. **Works with how vpkstuff is built.** vpkstuff has 5+ agents on worktrees of one
   `.git`, Docker builds whose context is the vpkstuff tree, and a ship tarball packed
   from the working tree (`deploy/ship-to-helm.sh`).
4. **One VRF assembly.** Today the compiler compiles against vpkstuff's own patched VRF
   (`Source2CompilerVrfProject`), because two assemblies of the same name cannot load
   side by side.
5. **Standalone stays possible**, without forcing vpkstuff's choices on other users.

## Options measured against them

| | single source | automatic pickup | worktrees + Docker | one VRF | standalone |
|---|---|---|---|---|---|
| git submodule | yes | no (pointer bump) | **poor**: every worktree needs `submodule update`, Docker needs a recursive checkout | yes | yes |
| git subtree | yes | no (`subtree pull`) | yes | yes | yes |
| vendored copy + sync script (today) | yes, enforced by `--check` | **no** | yes | yes | yes |
| NuGet package (private feed) | yes | version bump, automatable | yes, restore from a feed | **no while VRF is a patched source dependency** | **best** |

Submodules are the usual answer for "a separate project used by another", and they
are what breaks with many worktrees on one repository. NuGet is the clean answer for
versioning and standalone use, but a package cannot depend on vpkstuff's patched VRF
without shipping a second copy of it.

## Decision

**Now:** keep the vendored copy. It is the only option that meets all five today, and
vpkstuff's tooling (`deploy/sync-source2-compiler.py`, the `--check` in the ship gate, the
Dependencies tab's `VendoredCompiler`) is already built around it. What is missing is
requirement 2: pickup is a manual sync. Close that with one command on this side,
`tools/publish-to-vpkstuff`, run after a merge to `main`:

1. this repository's tests at the merged commit;
2. a fresh vpkstuff worktree on a `chore/compiler-<sha>` branch off `master`;
3. `deploy/sync-source2-compiler.py --from <this checkout>`;
4. build, then vpkstuff's compiler gate: `CompilerVrfCompatTests`, the skin-build
   regression set and the anim set (`AnimSet|AnimClip|ViewmodelAnimSet`);
5. commit; the branch then follows vpkstuff's normal staging, in-game check, merge.

**Next, after the VRF dependency is gone (docs/INDEPENDENCE.md):** ship `Source2.Compiler`
as a NuGet package, with SourceLink and symbols, to a feed on the box. vpkstuff takes it
as a `PackageReference` pinned in `Directory.Packages.props`, so pickup is a version
bump, done by the same command. The vendored copy and its sync script then retire.
Standalone users get the same package (publishable to nuget.org whenever wanted), and
nothing about vpkstuff leaks into it.

Sources consulted: [Git submodules vs subtrees vs monorepos](https://slicker.me/git/submodules-vs-subtrees-vs-monorepos.html),
[git submodule vs subtree technical overview](https://medium.com/@Spritan/git-submodule-vs-git-subtree-quick-technical-overview-92ae10119145),
[Referencing a local private NuGet package](https://weblog.west-wind.com/posts/2022/Sep/11/Referencing-a-Local-Private-NuGet-Package-in-your-Solution),
[Docker builds with local NuGet packages](https://forums.docker.com/t/local-nuget-feeds-during-build/76738).
