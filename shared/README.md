# shared/

Common code for more than one mod lives here. It is empty on purpose: the first mod owns
everything it needs (see team/plan.md, "no speculative shared lib"). Promote code here only when a
second mod in `mods/` has a real use for it, as its own project (`shared/Sotf.<Name>/`) with no
BepInEx or interop references unless every consumer is a plugin.
