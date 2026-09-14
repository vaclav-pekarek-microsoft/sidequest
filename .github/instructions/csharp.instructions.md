---
applyTo: "**/*.cs,**/*.razor"
description: "Sidequest C# baseline, public API documentation, and review requirements."
---

# Sidequest C# instructions

Before creating or changing handwritten C#, read and follow the
[user-selected C# best-practices guide](https://github.com/PlagueHO/github-copilot-assets-library/blob/ea4125167b98053f083cffcc79883221f872da30/instructions/csharp-best-practices.instructions.md)
from PlagueHO/github-copilot-assets-library. This is the reviewed revision of the
[upstream file](https://github.com/PlagueHO/github-copilot-assets-library/blob/main/instructions/csharp-best-practices.instructions.md);
upstream updates require an explicit repository change rather than silently changing
the development baseline. If the reference cannot be accessed, report the limitation.

Apply the guide alongside `AGENTS.md` and the accepted project handoff. In particular:

- Keep each handwritten public type in its own matching file, using file-scoped
  namespaces and appropriately restricted, explicit accessibility.
- Follow `.editorconfig`. Keep System imports first, followed by external libraries
  and project imports; remove unused imports and trailing whitespace in changed code.
- Keep domain behavior independent of UI and providers. Reuse focused application
  boundaries rather than creating speculative abstractions.
- Preserve cancellation throughout asynchronous operations. Use `ConfigureAwait(false)`
  for context-independent library continuations, not renderer-dependent Blazor updates.
- Document all handwritten public types and members, including tests and Razor
  parameters, with meaningful XML comments. Explain relevant parameters, results,
  exceptions, units, side effects, and concurrency constraints; add examples where
  complex usage warrants them. Inherit documentation only from a genuine contract.
- Dispose owned resources, validate inputs, and report specific failures without
  leaking sensitive data. Read-only collection interfaces alone do not guarantee
  immutability of the underlying data.
- Use the existing SDK analyzers and repository tests. Review design, documentation
  quality, one-type-per-file organization, and behavior explicitly; compiler checks
  do not establish compliance with every guideline.

Do not hand-edit generated migration/designer/Razor output solely for formatting or
documentation. Change its source or generator configuration when necessary. Do not
suppress missing-documentation diagnostics across handwritten code.

All changes, including standards updates, follow the account and PR rules in `AGENTS.md`.
