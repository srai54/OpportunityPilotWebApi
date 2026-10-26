# tests/ — scoped instructions

Loaded when working under `tests/`. The repo `CLAUDE.md` and `.claude/skills/unit-test/SKILL.md` still apply.

- `OpportunityPilot.UnitTests`: xUnit, the InMemory `AppDbContext`, fakes for clocks (`TimeProvider`), the current user and providers. No
  network: HTTP clients get a stub `HttpMessageHandler`; AI clients get a scripted fake (see `ClaudeKeyTests`, `AiServiceTests`).
- `OpportunityPilot.IntegrationTests`: the real API host against Postgres in Docker (Testcontainers). They run in CI; run them locally only
  when Docker is up.
- Name tests as sentences about behaviour (`A_refused_key_is_not_stored`). Cover the failure paths: validation, another owner's id (404),
  limits, provider errors.
- Fake secrets live only here (`opk_…`, `sk-ant-…` test values are allowed by `.gitleaks.toml` under `tests/`). Never use a real key.
- Coverage floors for Application and Domain are enforced in CI (`tools/coverage-gate.py`); new logic comes with tests that keep them.
