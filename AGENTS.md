# Repository contribution rules

- Make all repository changes through pull requests, including documentation,
  configuration, infrastructure, and agent-authored changes.
- Work on a dedicated task branch. Do not commit or push directly to `main` or an
  integration branch; integrate changes through pull requests.
- Use the GitHub account `vaclav-pekarek-microsoft` for commits, pushes, and pull
  requests. Before publishing, verify `gh api user --jq .login` matches that account.
  If authentication is missing or different, stop publishing and resolve it first.
- Use a Git author/committer identity associated with that account. Its GitHub
  no-reply email is `188451675+vaclav-pekarek-microsoft@users.noreply.github.com`.
  Preserve required assistant attribution trailers.
- Do not publish changes through a bot, service account, or another user's account.
  All development sub-agents follow these same branch, identity, and PR rules.
- Automatic PR merging is authorized after the change is verified and configured
  review/CI requirements pass. Never bypass the PR or push changes directly to main.
- Follow `docs\sidequest-project-handoff.md` for the accepted specification,
  development ownership boundaries, and remaining external approval gates.

## C# engineering standards

- Follow the user-selected [C# best-practices guide](https://github.com/PlagueHO/github-copilot-assets-library/blob/ea4125167b98053f083cffcc79883221f872da30/instructions/csharp-best-practices.instructions.md).
  This pinned revision is the adopted baseline; do not silently adopt future upstream
  changes. Read `.github\instructions\csharp.instructions.md` before C# work, including
  C# inside Razor components. These standards apply to every development sub-agent.
- Apply SOLID pragmatically: cohesive responsibilities, focused contracts, dependency
  inversion at external boundaries, and composition rather than unnecessary inheritance.
  Keep business rules out of Razor components and provider-specific concerns out of Domain.
- Prefer clear names, small cohesive methods, nullable-safe code, constructor injection,
  cancellation-aware async operations, explicit failures, and deterministic, testable rules.
  Reuse existing abstractions; do not add speculative frameworks or unrelated refactors.
- Document every public C# type and member with meaningful XML documentation, including
  constructors, methods, properties, fields/constants, enum values, and interface members.
  Describe intent and observable behavior, not merely a restatement of the identifier.
- Document parameters/type parameters, return values, applicable validation/authorization
  exceptions, units, nullability, state transitions, and side effects where relevant.
  Use `<inheritdoc />` only for a genuine inherited/interface contract.
- These requirements also apply to handwritten test fixtures and public test methods.
  Do not hand-edit tool-generated source solely for style or suppress missing-documentation
  diagnostics across handwritten code.
- XML documentation generation and warnings-as-errors are enabled in
  `Directory.Build.props`; missing public documentation (CS1591) is a build failure.
  Compiler enforcement checks presence and XML validity, not semantic quality: review
  the comments alongside the implementation.
