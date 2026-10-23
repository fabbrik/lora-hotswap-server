# Contributing

```bash
dotnet build LoraHotswap.slnx -c Release
dotnet test  LoraHotswap.slnx -c Release     # asset-dependent tests skip with a reason if models/ is absent
```

To run the full suite and the real-generation tests, fetch the model and adapters first
(`tools/acquire`, then `tools/convert`; see the README, "Getting started").

Ground rules:

- **Respect the dependency direction.** `Core.Ports` <- `Core` <- `Infrastructure` <- `Api`.
  `DependencyDirectionTests` enforces it.
- **Fail closed.** An adapter that cannot be proven valid, merged-correct, or identity-bound is
  rejected with a typed problem; it is never served on a best guess.
- **Claims are tested, not asserted.** If a doc says the server does something, a test or the
  `tools/capture` output shows it. Generated README sections are never hand-edited.
- Record traps you hit in GOTCHAS.md.
