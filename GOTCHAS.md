# GOTCHAS

Raw log, appended while bleeding. `[HIT]` bit for real, `[TRAP]` was anticipated
and dodged but would have bitten someone.

---

### [HIT] `dotnet new xunit3` does not exist — the template ships xunit **2.9.3**

The test project was specified to use xunit.v3 3.2.2, assuming a matching template. SDK 10.0.302
offers only `xunit`, and it generates:

```xml
<PackageReference Include="xunit" Version="2.9.3" />
```

That is the v2 line. A new project on .NET 10 that follows the template silently
lands two major versions behind.

Migration is manual: swap `xunit` for `xunit.v3`, keep
`xunit.runner.visualstudio` (3.1.4 handles both v2 and v3), and add
`<OutputType>Exe</OutputType>` — xunit.v3 test projects are executables, unlike v2.

### [HIT] .NET 10 `dotnet new sln` emits `.slnx`, not `.sln`

The new XML solution format is the default. Any tooling, script, or test that
walks up looking for `*.sln` finds nothing and fails with something unhelpful:

```
Assert.NotNull() Failure: Value is null
```

The root-finding walk now accepts both.

### [HIT] MSBuild catches *some* layering violations before any test runs — but only by accident

Adding `Core → Infrastructure` produced:

```
error MSB4006: There is a circular dependency in the target dependency graph
```

not an architecture-test failure. `Infrastructure → Core` already existed, so the
forbidden edge closed a cycle and MSBuild rejected it during restore.

That is a stronger guard than the test — but it is **incidental**. It only fires
because the reverse edge happens to exist. A forbidden reference with no reverse
edge compiles cleanly, and then the architecture test is the only thing standing
there.

Consequence for verification: sabotaging `Core → Infrastructure` does **not**
exercise the test. To prove the test works, the reverse edge must be removed
first. Done, confirmed failing with the right message, then both restored:

```
LoraHotswap.Core must not reference LoraHotswap.Infrastructure.
Current references: LoraHotswap.Core.Ports, LoraHotswap.Infrastructure.
```

### [HIT] `dotnet remove reference` can fail silently

`dotnet remove <proj> reference <target>` returned without error and left the
`ProjectReference` in place. With output redirected to `/dev/null` this looked
like success, and the subsequent sabotage run kept reporting the circular-
dependency error instead of the expected test failure — costing a full debug
cycle chasing the wrong thing.

Verify the `.csproj` after mutating references. Do not trust the exit code.

### [TRAP] An assembly-reference check would have passed for the wrong reason

The obvious implementation of the dependency test is:

```csharp
typeof(X).Assembly.GetReferencedAssemblies()
```

The compiler elides assembly references that no code uses. With empty projects —
the state early on, before any real code existed — `Core` emits no reference to
anything, so the assertion passes while proving nothing.

The test reads the `.csproj` `<ProjectReference>` graph instead. Cannot be
optimised away, and true from the first commit.

### [NOTE] No architecture-testing library was added, deliberately

- **NetArchTest** — last commit **2024-07-29** (checked 2026-08-10). Stale.
- **ArchUnitNET** (`TngTech.ArchUnitNET` 0.13.3) — last push **2026-08-09**.
  Actively maintained and the right choice *if* richer rules are ever needed.

One rule does not justify a dependency. ~40 lines of XML parsing covers it.

---

### [HIT] `Directory.Build.props` is a decoy if the csproj templates re-declare the same properties

`dotnet new classlib` and `dotnet new web` both emit `<Nullable>` and
`<ImplicitUsings>` into the project file. Project-level properties win over
`Directory.Build.props`, so setting them centrally does **nothing** — the file
looks authoritative and is inert for those two settings.

Only `TreatWarningsAsErrors` and `LangVersion` — the properties the template does
not emit — actually took effect. Found by review, not by anything failing.

Fix: strip `TargetFramework`, `Nullable`, and `ImplicitUsings` from every
generated csproj and let the props file own them.

### [HIT] `TreatWarningsAsErrors` turns a published CVE into a broken build

NuGet audit reports NU1901–NU1904 as warnings. Promoted to errors, a CVE
disclosed against any transitive dependency breaks the build with no commit and
no code change.

```xml
<WarningsNotAsErrors>NU1901;NU1902;NU1903;NU1904</WarningsNotAsErrors>
```

### [TRAP] `EnumerateFiles("Name.sln?")` also matches `.slnf`

The single-character wildcard matches solution *filter* files as well as `.slnx`.
A stray `.slnf` in an ancestor directory stops the root walk in the wrong place,
and the failure surfaces as a confusing missing-project assertion. Both filenames
are now checked explicitly.

### [TRAP] Parsing csproj XML without namespace handling can silently find zero references

`Descendants("ProjectReference")` uses a no-namespace name. A csproj carrying the
legacy `xmlns="http://schemas.microsoft.com/developer/msbuild/2003"` root
attribute returns **no matches**, so every "must not reference" rule passes
while the forbidden reference sits in the file.

Match on `Name.LocalName`, case-insensitively — MSBuild itself is case-insensitive
— and split `Include` on `;`, which MSBuild accepts.

---

### [NOTE] `ggml-org/Qwen3-0.6B-GGUF` ships four quantisations — none pinned by spec

The repo publishes `BF16`, `f16` (~1.5 GB each), `Q8_0` (~805 MB, near-lossless)
and `Q4_0` (~429 MB), with no single one obviously "the" pinned quant. Resolved
as **Q8_0**: CPU-only Apple Silicon serving doesn't benefit from BF16/f16's
extra precision, and Q8_0 stays close enough to full precision that it
shouldn't confound later merge-verification tolerance checks the way a 4-bit
quant might. `sources.toml` fetches only that one file — never a
`snapshot_download` of the whole repo. Revisit if those tolerance checks ever
disagree with this choice.

### [NOTE] Unauthenticated Hub requests print a rate-limit warning, not an error

Every `hf_hub_download`/`model_info` call without `HF_TOKEN` set prints
`Warning: You are sending unauthenticated requests to the HF Hub...` to
stderr. It is noise, not failure — all five artifacts acquired here (the base
model plus four adapters) are public and downloaded fine anonymously — but a
script that treats any stderr output as a failure signal would misreport a
clean run.

### [NOTE] `convert_lora_to_gguf.py` needs only the base model's *config*, not its weights — and it fetches it itself

The open question was whether converting these Qwen3 adapters would drag in the
full HF base-model safetensors repo — a much larger download than intended.
It does not. When neither `--base` nor `--base-model-id` is passed, the converter
reads each adapter's own `base_model_name_or_path` and loads *only* that base's
`config.json` via `transformers.AutoConfig.from_pretrained` (cached under
`~/.cache/huggingface`). That's a small config file, not the model weights — no
large artifact fetched. So the tool deliberately passes neither flag: letting
the converter use each adapter's declared base string also preserves the
`unsloth/Qwen3-0.6B` vs `Qwen/Qwen3-0.6B` divergence between adapters without
normalising it away.

### [NOTE] Qwen3 LoRA adapters convert cleanly at the pinned llama.cpp revision

All four acquired adapters (`self_cognition_Alice`, `self_cognition_Bob`,
`Qwen3-0.6B-TLDR-Lora`, `qwen3_06B_lora_math`) convert with no error at converter
revision `749f688f…` — the architecture (`Qwen3ForCausalLM`) is supported. This
was the main unverified external dependency in the conversion step; it is now
confirmed to work.

### [NOTE] The vendored converter imports `conversion` and `gguf` as siblings, so vendor more than one file

"Obtain the converter" is not "download one script". `convert_lora_to_gguf.py`
does `from conversion import ...` (the per-architecture model definitions, a
whole package) and `import gguf` (from the bundled `gguf-py`). `convert.py`
vendors all three paths (`convert_lora_to_gguf.py`, `conversion/`, `gguf-py/`) at
the pinned commit and runs the script with cwd set to the vendored root so both
imports resolve. Pinning just the one filename would break at import.

### [NOTE] Converter chatter goes to stderr on success — do not treat it as failure

Same trap as acquisition's Hub warning. The converter logs INFO lines and (when
unauthenticated) the Hub rate-limit warning to stderr even on a clean run.
`convert.py` keys off the subprocess *exit code*, and only surfaces stderr
verbatim when that code is non-zero. GGUF output is also not guaranteed
byte-identical across machines/library versions, which is exactly why the
`ggufSha256` and the manifests are computed per-local-conversion and never
committed — a committed hash would fail a fresh clone's warm-path check.

### [NOTE] Vendoring fetches the converter by commit SHA, which the remote must allow

`convert.py` obtains the converter with `git fetch --depth 1 origin <sha>` then
`git checkout <sha>`. Fetching a bare commit SHA works only when the server
enables `uploadpack.allowReachableSHA1InWant` (or `allowAnySHA1InWant`). GitHub
(the pinned `ggml-org/llama.cpp` remote) currently allows it, so this is fine
today — but a self-hosted mirror or proxy that disables it would fail the fetch
with an opaque git error. There is no branch/tag fallback: the lock pins a SHA
by design (reproducibility), and `load_converter_lock` now rejects a non-40-hex
`revision` outright. If the remote ever stops allowing SHA fetches, the fix is to
mirror the converter, not to loosen the pin. The offline-on-rerun guarantee rests
on the vendored `.revision` marker short-circuit, not on the fetch itself.

### [HIT] The circular-dependency wall hides the fact that a layering test is untested

Second time this bit. Adding `Infrastructure → Api` fails at MSBuild restore with
a *circular dependency* error, because `Api → Infrastructure` already exists —
so the sabotage never reaches the test, and an empty grep looks like "no
findings" rather than "never ran".

Every inward layering violation in a well-formed hexagon is circular, so **no
forbidden edge can be sabotage-tested without first removing the legitimate
reverse edge**. Verified properly:

```
LoraHotswap.Infrastructure must not reference LoraHotswap.Api.
Current references: LoraHotswap.Api, LoraHotswap.Core, LoraHotswap.Core.Ports.
```

### [HIT] A vacuous test and a broken test are indistinguishable

`PortConventionTests` scanned an assembly with no types and asserted "no
offenders found". Green, and completely without evidence — a typo in the scanner
would have looked identical, and would have stayed hidden until the first real
port arrived.

The scanner is now proven against deliberately non-conforming fixtures declared
in the test assembly: a synchronous method, a method with no `CancellationToken`,
an `internal` interface, and a member inherited from a base interface. Each must
be detected; a conforming port must not be.

The original also used `GetExportedTypes()` (misses `internal` interfaces) and
`Type.GetMethods()` (misses base-interface members) — both invisible while the
assembly was empty.
