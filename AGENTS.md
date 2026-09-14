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
- Leave pull requests open for review unless the user explicitly requests merging;
  honor configured review and CI requirements.
- Follow `docs\sidequest-project-handoff.md` for the accepted specification,
  development ownership boundaries, and remaining external approval gates.
