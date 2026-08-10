# GOTCHAS

Raw log, appended while bleeding. `[HIT]` bit for real, `[TRAP]` was anticipated
and dodged but would have bitten someone.

---

### [HIT] `dotnet new xunit3` does not exist — the template ships xunit **2.9.3**

Story 1.1 specified xunit.v3 3.2.2 and assumed a matching template. SDK 10.0.302
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
exactly the state at Story 1.1 — `Core` emits no reference to anything, so the
assertion passes while proving nothing.

The test reads the `.csproj` `<ProjectReference>` graph instead. Cannot be
optimised away, and true from the first commit.

### [NOTE] No architecture-testing library was added, deliberately

- **NetArchTest** — last commit **2024-07-29** (checked 2026-08-10). Stale.
- **ArchUnitNET** (`TngTech.ArchUnitNET` 0.13.3) — last push **2026-08-09**.
  Actively maintained and the right choice *if* richer rules are ever needed.

One rule does not justify a dependency. ~40 lines of XML parsing covers it.
