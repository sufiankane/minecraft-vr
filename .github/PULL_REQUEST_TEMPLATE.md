## Work item

- **Work item ID:** S<stage>-WI<number>
- **Stage / milestone:** S<stage> / <milestone or "none">

## Summary

<!-- What changed and why. Link the ADR if a contract, dependency or architecture rule changed. -->

## Section 11.3 checklist

The checklist below is dossier section 11.3, reproduced verbatim. Every box must
be ticked before review.

- [ ] Work item ID and stage referenced.
- [ ] Tests written first and listed; all pass locally and in CI.
- [ ] No contract changes, or ADR linked and version bumped.
- [ ] Dependency rule respected.
- [ ] Public API documented (preconditions, thread-safety, errors).
- [ ] Hot paths allocation-free (evidence attached for benchmarks).
- [ ] Coverage meets the module target.
- [ ] No unresolved TODO, commented-out code or debug output.
- [ ] Performance numbers recorded when the work item has a budget.
- [ ] Reviewer is not the author.

## Tests

<!-- List the tests written first, and the commands run locally. -->

## Notes for the reviewer

<!-- Anything the reviewer needs: out of scope, open questions in docs/questions/, etc. -->
