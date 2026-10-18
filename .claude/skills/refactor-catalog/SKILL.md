---
name: refactor-catalog
description: The safe-refactoring law and the move catalog — load before any behavior-preserving change.
---

# Refactor catalog

## The law
A refactor changes structure, never behavior. Proof = the **same** test suite
passes before and after, with zero test edits. No passing test over the target →
no refactor; request characterization tests first.

## Safe moves
| Move | When |
|---|---|
| Rename | the name lies about intent (standards §3 naming) |
| Extract function | a block needs a comment to explain *what* — name it instead |
| Inline | an indirection earns nothing |
| Extract module | a file does more than one job (SRP, standards §14) |
| Replace conditional with dispatch | a growing `if`/`switch` on a type tag |
| Introduce Result type | try/catch used for control flow (standards §8) |
| Remove dead code | no caller, git-confirmed unreachable |
| De-duplicate | the third real occurrence — not the second (YAGNI, standards §14) |

## Rules
- One concern per branch — a rename branch never also extracts a module.
- Small steps: one move, run tests, commit. Never batch moves between test runs.
- Match the idioms in team/map.md; a refactor never introduces a new pattern.
- A behavior change after a move means the move was wrong. Revert; never edit the
  test to match new behavior.
- Measure before a performance-motivated refactor — standards §14 KISS: complexity
  earns its place only against a named, present problem.
