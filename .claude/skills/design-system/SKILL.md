---
name: design-system
description: Token structure, the component-state matrix, and the accessibility floor — load before writing team/design.md.
---

# Design system

## Tokens — live in the theme config, never constants.ts
| Group | Defines |
|---|---|
| Color roles | background, surface, foreground, muted, primary, destructive, border — each with a light and a dark value |
| Type scale | font family, size steps, weight, line-height — a named scale, not ad-hoc px |
| Spacing | one scale (e.g. 4px base); components compose from it |
| Radii | named steps (sm / md / lg / full) |
| Elevation | shadow steps mapped to z-intent |

Reference by role, never raw value — `bg-surface`, not `bg-[#111]`. Roles carry a
light and dark value; the theme toggle swaps the map, not the components.

## Component state matrix
Every interactive component specs all applicable states:

default · hover · focus-visible · active · disabled · loading (skeleton) · empty · error

A spec missing a state is incomplete — dev builds exactly what is specced, no more.

## Accessibility floor (non-negotiable)
- Contrast: 4.5:1 body text, 3:1 large text and UI boundaries — validate every
  color-role pair in both themes.
- Focus: a visible focus ring on every interactive element; never remove the
  outline without a replacement.
- Hit target: ≥ 44×44px interactive area.
- Motion: honor `prefers-reduced-motion`; never signal state through motion alone.

## Decisions
Record token and pattern choices in team/design.md's Decisions block — chose X over
Y because Z (senior-protocol rule 3). A palette without rationale is guesswork.
