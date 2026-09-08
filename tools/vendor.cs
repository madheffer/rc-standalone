#!/usr/bin/env dotnet run
// Fetch the pinned ValveResourceFormat commit into third_party/ and apply this
// project's patches to it.
//
//   dotnet run tools/vendor.cs             fetch (if needed), then apply patches
//   dotnet run tools/vendor.cs -- --check   verify the tree is patched, change nothing
//
// A patch is a literal find -> replace against one file. The `find` snippet must
// occur EXACTLY ONCE in the pristine upstream file. Zero matches or more than one
// aborts the whole run: upstream moved and a human has to look, which is the point
// of recording patches instead of committing a mutated copy of someone else's code.

using System.Text.Json;

var repoRoot = FindRepoRoot();
var thirdParty = Path.Combine(repoRoot, "third_party");
var checkOnly = args.Contains("--check");

var pin = JsonDocument.Parse(File.ReadAllText(Path.Combine(thirdParty, "VENDORED.json")))
    .RootElement.GetProperty("vrf");
var repo = pin.GetProperty("repo").GetString()!;
var sha = pin.GetProperty("sha").GetString()!;
var cloneDir = Path.Combine(thirdParty, pin.GetProperty("dir").GetString()!);

if (!Directory.Exists(Path.Combine(cloneDir, ".git")))
{
    if (checkOnly) return Fail($"{cloneDir} is not vendored yet. Run: dotnet run tools/vendor.cs");
    Console.WriteLine($"==> fetching {repo} @ {sha[..12]}");
    Directory.CreateDirectory(cloneDir);
    Git(cloneDir, "init", "-q");
    // Upstream nests source files deep enough that a checkout under an already
    // long directory blows Windows' 260-character MAX_PATH, and git reports it
    // as "Filename too long" per file while still claiming the checkout worked:
    // the build then fails on types whose file silently never landed.
    Git(cloneDir, "config", "core.longpaths", "true");
    Git(cloneDir, "remote", "add", "origin", repo);
    Git(cloneDir, "fetch", "-q", "--depth", "1", "origin", sha);
    Git(cloneDir, "checkout", "-q", "FETCH_HEAD");
}
else
{
    var head = GitOut(cloneDir, "rev-parse", "HEAD").Trim();
    if (!head.StartsWith(sha[..12], StringComparison.OrdinalIgnoreCase))
        Console.WriteLine($"!! vendored HEAD is {head[..12]}, VENDORED.json pins {sha[..12]}");
}

var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(thirdParty, "patches", "index.json")))
    .RootElement.GetProperty("patches");

int applied = 0, already = 0;
var conflicts = new List<string>();

foreach (var p in index.EnumerateArray())
{
    var id = p.GetProperty("id").GetString()!;
    var target = Path.Combine(cloneDir, p.GetProperty("file").GetString()!.Replace('/', Path.DirectorySeparatorChar));
    var find = ReadSnippet(Path.Combine(thirdParty, "patches", p.GetProperty("find").GetString()!));
    var replace = ReadSnippet(Path.Combine(thirdParty, "patches", p.GetProperty("replace").GetString()!));

    if (!File.Exists(target)) { conflicts.Add($"{id}: target file is gone ({p.GetProperty("file")})"); continue; }

    var text = File.ReadAllText(target);
    var hits = Count(text, find);

    if (hits == 0)
    {
        // Already applied is the only benign explanation for a missing snippet.
        if (Count(text, replace) > 0) { already++; continue; }
        conflicts.Add($"{id}: `find` snippet not present in {p.GetProperty("file")} (upstream moved)");
        continue;
    }
    if (hits > 1) { conflicts.Add($"{id}: `find` snippet matches {hits}x - too ambiguous to apply"); continue; }
    if (checkOnly) { conflicts.Add($"{id}: not applied"); continue; }

    File.WriteAllText(target, ReplaceOnce(text, find, replace));
    applied++;
}

Console.WriteLine($"==> patches: {applied} applied, {already} already present, {conflicts.Count} unresolved");
foreach (var c in conflicts) Console.WriteLine("    CONFLICT " + c);
if (conflicts.Count > 0) return 1;
Console.WriteLine("==> vendored tree is ready");
return 0;

// helpers

static string ReadSnippet(string path)
{
    // Snippets are stored with the upstream file's own newlines normalised to LF;
    // normalise the target the same way at compare time instead of mangling it.
    var s = File.ReadAllText(path).Replace("\r\n", "\n");
    return s.TrimEnd('\n');
}

static int Count(string haystack, string needle)
{
    var n = Norm(haystack);
    int i = 0, c = 0;
    while ((i = n.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { c++; i += needle.Length; }
    return c;
}

static string ReplaceOnce(string original, string find, string replace)
{
    var norm = Norm(original);
    var at = norm.IndexOf(find, StringComparison.Ordinal);
    var patched = norm[..at] + replace + norm[(at + find.Length)..];
    // Restore CRLF if that is what the file used, so the diff stays minimal.
    return original.Contains("\r\n", StringComparison.Ordinal) ? patched.Replace("\n", "\r\n") : patched;
}

static string Norm(string s) => s.Replace("\r\n", "\n");

static string FindRepoRoot()
{
    // Anchor on the vendoring manifest rather than a solution filename: a
    // file-based app runs from a temp build dir, so probe both the current
    // directory and the assembly location.
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var dir = start;
        for (var i = 0; i < 12 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "third_party", "VENDORED.json")))
                return dir;
    }
    throw new InvalidOperationException(
        "Could not locate the repo root (no third_party/VENDORED.json above the working directory). "
        + "Run this from inside the repository.");
}

static void Git(string cwd, params string[] a)
{
    if (Run(cwd, "git", a, out var err, out _) != 0)
        throw new InvalidOperationException($"git {string.Join(' ', a)} failed: {err}");
}

static string GitOut(string cwd, params string[] a)
{
    Run(cwd, "git", a, out _, out var stdout);
    return stdout;
}

static int Run(string cwd, string exe, string[] a, out string stderr, out string stdout)
{
    var psi = new System.Diagnostics.ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var x in a) psi.ArgumentList.Add(x);
    using var proc = System.Diagnostics.Process.Start(psi)!;
    stdout = proc.StandardOutput.ReadToEnd();
    stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    return proc.ExitCode;
}

static int Fail(string msg) { Console.Error.WriteLine(msg); return 1; }
