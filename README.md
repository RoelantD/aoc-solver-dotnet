# Advent of Code solver agent (.NET)

An autonomous agent that solves **past** Advent of Code puzzles end to end, built with
[Microsoft Agent Framework](https://github.com/microsoft/agent-framework) for .NET and hosted on
Microsoft Foundry. It reads a puzzle, writes Python, tests that code in an isolated cloud sandbox, repairs
failures, hands hard problems to a stronger model, runs the program on the real input, asks a human for
approval and submits the answer.

This is the companion code for the dotNetFriday 2026 talk *Building production AI agents*. It shows
what it takes to move an agent from a demo to something you can leave running: workflows, handoff,
human in the loop, durable runs with checkpoints, a streaming UI protocol, guardrails and telemetry.

## Respectful use

- The creator of Advent of Code **asks people not to use AI to solve the puzzles**. They are made
  for people. This project is a technical demo for **puzzles from past years**. Don't use it to
  compete, and don't run it while an Advent of Code event is live. The agent refuses to start a year
  that has not finished.
- Puzzle text and personal inputs are **not redistributed**. They are cached at runtime only, and the
  tests use synthetic puzzles.
- Traffic to adventofcode.com:
  - Requests are sequential and paced; the minimum interval is configurable.
  - Every request carries a User-Agent with your contact details (`AOC_CONTACT`) and this repository's URL.
  - Inputs are cached, and `Retry-After` and answer cooldowns are respected.
  - A POST is never retried automatically, and an answer that was already rejected is never submitted again.
- Answers are submitted only after a person approves each one, or after you authorize automatic
  submission for that run in the dashboard.

## What's inside

```text
React dashboard (Vite)  ──AG-UI over SSE──►  Aps.Agent (ASP.NET Core, Foundry hosted agent)
                                              ├─ RunOrchestrator: one run = a year and a range of days
                                              │   └─ PartWorkflow (Agent Framework workflow, one per puzzle part)
                                              │       analyst → verify-examples → coder → sandbox-test
                                              │       → run-input → approval (RequestPort) → submit
                                              ├─ Agents: GPT-6.1 Sol (workhorse) with a handoff to Claude Opus 5.5
                                              ├─ Sandbox: Azure Container Apps Dynamic Sessions, no outbound network
                                              ├─ Verification: example checks, answer ledger, hardcoded-answer guard
                                              ├─ AocClient: paced, cached, allowlisted website client
                                              ├─ Persistence: Cosmos DB (runs, parts, events, ledger, cache, checkpoints)
                                              │   with an ETag lease so exactly one instance owns a run
                                              └─ OpenTelemetry → Application Insights, with secrets redacted
```

| Folder | What |
|--------|------|
| [`samples/01-single-agent-tool`](samples/01-single-agent-tool) | One `ChatClientAgent` with a function tool. The smallest useful agent. |
| [`samples/02-workflow-handoff-approval`](samples/02-workflow-handoff-approval) | A workflow with executors and edges, a handoff to a second model, and a human approval through a `RequestPort`. |
| [`samples/03-workflow-checkpoints`](samples/03-workflow-checkpoints) | Workflow checkpoints: crash the process mid-run (`--crash-after coder`), then continue with `--resume` without repeating finished steps. |
| [`src/Aps.Agent`](src/Aps.Agent) | The full solver agent. Start reading at `Program.cs` and `Workflow/PartWorkflow.cs`. |
| [`src/Aps.Agent.Tests`](src/Aps.Agent.Tests) | Unit, workflow and protocol tests with a fake Advent of Code site, scripted models and a fake sandbox. |
| [`frontend`](frontend) | React dashboard that talks AG-UI to the agent: runs, live activity, code, approvals, preflight. |
| [`infra`](infra) | Bicep for Foundry, the Dynamic Sessions pool, Cosmos DB, Key Vault and role assignments. |
| [`scripts`](scripts) | `env.ps1` (load `.env` into a terminal), `preflight.ps1`, `provision-data.ps1` and an azd hook. |

## Prerequisites

- .NET SDK 10 (see `global.json`), Node.js 22 or later, PowerShell
- Azure CLI and Azure Developer CLI (`azd`) with the AI agent extension
- An Azure subscription with a Microsoft Foundry project and two model deployments: a workhorse
  (`gpt-6.1-sol`) and an escalation model (`claude-opus-5-5`). Any other pair of chat models works if
  you change the deployment names in `.env`.
- An Advent of Code account, for the full agent only. The samples don't need one.

## Run the samples

```powershell
az login
Copy-Item .env.example .env     # then fill in the Foundry endpoint and the sessions pool endpoint
. .\scripts\env.ps1             # loads .env into this terminal
dotnet run --project samples/01-single-agent-tool
dotnet run --project samples/02-workflow-handoff-approval
dotnet run --project samples/03-workflow-checkpoints -- --crash-after coder
dotnet run --project samples/03-workflow-checkpoints -- --resume
```

In VS Code, the launch configurations in `.vscode/launch.json` run the same samples and read `.env`
themselves.

## Run the agent locally

```powershell
. .\scripts\env.ps1
dotnet run --project src/Aps.Agent          # AG-UI endpoint on http://localhost:8088/invocations
```

```powershell
cd frontend
npm ci
npm run dev                                 # dashboard on http://localhost:5175
```

Locally the agent uses an in-memory store and reads `AOC_SESSION` (the value of the `session` cookie
on adventofcode.com) from `.env`. A dashboard that points at a localhost agent skips sign-in.

## Deploy to Foundry

1. Base infrastructure (Foundry account, model deployments and the sandbox pool): see [infra/README.md](infra/README.md).
2. Deploy the hosted agent: `azd deploy aps-solver` (configured in `azure.yaml`).
3. Add Cosmos DB, Key Vault and the agent's role assignments: `.\scripts\provision-data.ps1`, then
   store the cookie in Key Vault as the script tells you and run `azd deploy aps-solver` again.
4. Check everything from the agent's own identity: `.\scripts\preflight.ps1 -Year 2023 -Days 1-5`.
5. Point the dashboard at the hosted agent: copy `frontend/.env.example` to `frontend/.env.local`
   and fill in the hosted endpoint and an Entra app registration (single-page application).

## Tests

```powershell
dotnet test --project src/Aps.Agent.Tests
cd frontend; npm test
```

No test calls a model or the real website, and no test submits an answer.

## License

MIT. See [LICENSE](LICENSE).
