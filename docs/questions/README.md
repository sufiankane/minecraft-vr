# Questions and escalation

This directory is the escalation path for work that must stop. It exists so a
blocked work item leaves a written record instead of guessing.

## When to stop and ask

Dossier section 0, rule 7: **stop and ask** if a requirement conflicts with a
contract, a gate cannot be met, or an unknown changes the architecture. Write
the question in `docs/questions/` and halt that work item.

Dossier section 11.2 repeats the rule for builder agents: if blocked, or if a
requirement conflicts with a contract, stop and write `docs/questions/<ID>.md`.

Escalate to the owner (dossier section 11.5) for any of:

- the gate G-A outcome;
- any proposed contract change;
- model choice and data collection requirements;
- hardware behaviour that contradicts the verified facts in section 2.1;
- any need for extra hardware.

Do not proceed past a conflict, and do not reinterpret a contract to make code
compile. Halting is the correct outcome.

## How to write a question

Create one file per blocked work item named after the work item ID:

```
docs/questions/S<stage>-WI<number>.md
```

Include:

- **ID:** the work item ID, e.g. `S8-WI2`.
- **Stage / milestone:** the stage the work item belongs to.
- **Blocked on:** the contract, gate, unknown (ID from section 2.2) or fact in
  conflict.
- **What was tried:** the commands, fixtures or reads performed.
- **Evidence:** exact output or citations (file and line, dossier section).
- **Question:** the decision required to unblock.
- **Proposed options:** one or more, with the trade-off of each.
- **Impact if unresolved:** what cannot proceed and what is safe to continue.

## Process

1. Stop the work item. Do not commit partial work that depends on the answer.
2. Write the question file and reference it in the PR description or task
   report.
3. Halt until the owner decides. An answer that changes a contract or an
   architecture rule becomes an ADR in `docs/adr/` before the work resumes.
4. Record the resolution in the same file (dated) and delete or archive it only
   once the ADR or decision it produced is linked.

The section 11.3 checklist item "No contract changes, or ADR linked and version
bumped" is what keeps resolutions from being lost: every accepted answer that
touches a contract leaves a linked ADR.
