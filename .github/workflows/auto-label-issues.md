---
description: "Automatically label new or updated issues for the Robot Competition Booth project."
emoji: "🏷️"
labels: ["automation", "issues", "triage"]

on:
  issues:
    types: [opened, edited, reopened]

permissions:
  contents: read
  issues: write

network: defaults

tools:
  github:
    toolsets: [issues, contents]

safe-outputs:
  add-labels:
    target: triggering
    max: 3
    allowed:
      - needs-triage
      - type:bug
      - type:feature
      - type:task
      - area:app
      - area:compiler
      - area:firmware
      - area:docs
      - area:blockly
      - area:communication
      - priority:high
      - priority:medium
      - priority:low
  remove-labels:
    target: triggering
    max: 1
    allowed:
      - needs-triage

engine: copilot
max-ai-credits: 500
---

# Auto-label issues

Inspect the triggering issue title and body and apply up to three labels that best describe it.

Use these label categories:

- Always add `needs-triage` unless the issue is clearly spam or already has enough routing labels.
- Type labels:
  - `type:bug` for broken behavior, errors, regressions, crashes, build failures, or firmware/app/compiler defects.
  - `type:feature` for new capabilities, enhancements, UI additions, new blocks, new robot behaviors, or new challenge ideas.
  - `type:task` for chores, refactors, setup work, documentation work, repo maintenance, or planning tasks.
- Area labels:
  - `area:app` for the Blazor Server operator UI or local app/backend.
  - `area:compiler` for the Python Blockly workspace JSON to opcode compiler.
  - `area:firmware` for Arduino/C++ robot runtime code.
  - `area:docs` for README, architecture notes, challenge docs, or general docs.
  - `area:blockly` for visual blocks, Blockly workspace JSON, block definitions, or generated command structure.
  - `area:communication` for Bluetooth setup, Wi-Fi, MQTT, protocol contracts, or app-to-robot communication.
- Priority labels:
  - `priority:high` only when the issue blocks core robot operation, deployment, demos, safety, or competition-critical behavior.
  - `priority:medium` for important work that does not block current development.
  - `priority:low` for cleanup, polish, or nice-to-have improvements.

Rules:

1. Do not change the issue title or body.
2. Do not close issues.
3. Do not add more than one `type:*` label.
4. Do not add more than one `priority:*` label.
5. Prefer one type label, one area label, and `needs-triage`.
6. If the issue is ambiguous, add only `needs-triage` plus the most likely area label.
7. Never create labels outside the allowed list.
8. Remove `needs-triage` only when the issue already has a clear type label, area label, and priority label.

Use the `add-labels` safe output for labels you want to add.
Use the `remove-labels` safe output only when removing `needs-triage` is justified by rule 8.
