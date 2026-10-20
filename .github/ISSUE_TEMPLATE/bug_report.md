---
name: Bug report
about: Something is wrong with measurement, startup, UI, or exports
title: "[bug] "
labels: bug
assignees: ""
---

## What happened

A clear description of the problem.

## Steps to reproduce

1.
2.
3.

## Expected behaviour

What did you expect?

## Environment

- Laptop model:
- CPU:
- Windows build (`winver`):
- WattLedger version:
- Battery name / capacity:
- Running from source or published build:

## Diagnostics

Please paste the output of:

```powershell
dotnet run --project src/WattLedger.Cli -- diag
```

Review it first and redact serial numbers, device paths, or anything personal.

## Logs / screenshots

If the problem involves the UI, attach a screenshot. If it involves energy numbers, include a short `wattledger watch` excerpt and the relevant `wattledger today` output.
