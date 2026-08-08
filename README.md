# Dispeller

A Dalamud plugin for FINAL FANTASY XIV that helps you clean out your Glamour Dresser by
identifying items that share the same visual model.

The Glamour Dresser caps at 800 slots, and a lot of what fills it is redundant — different
items that look identical once equipped. Dispeller scans the dresser, groups items by
equipment slot and model ID, and shows you the groups where more than one item resolves to
the same appearance. Keep one, dispel the rest.

It also flags items that can live in the Armoire instead, which doesn't consume dresser
space at all.

## Installing

This plugin is not (and never will be) in the official Dalamud repository. To install it,
add this URL in Dalamud under **Settings → Experimental → Custom Plugin Repositories**:

```
https://raw.githubusercontent.com/Dryness/DispellerPlugin/master/repo.json
```

## Usage

Open the Glamour Dresser at least once so the plugin can read it, then run `/dispeller`
and press **Scan**.

The dresser contents are cached as you open it, so you can review results after closing
the dresser. Re-scanning while it's open picks up any changes.

## Fork

This is a fork of [pupwife/DispellerPlugin](https://github.com/pupwife/DispellerPlugin),
maintained for personal use only.

*I used Claude (Opus 5, max effort) to update to the current Dalamud API level (15) to work
on the current patch at time of commit (7.55).*

*I make no claims of ownership over any of the code in this repo.*

*This is a strictly "**works on my machine**" fork, with no guarantees that it will work on
yours.*

---

## AI usage declaration

*Claude thought that this section would be useful. If anyone who knows what they're doing
cares for the justifications behind why anything was changed the way they were, here you
go.*

---

Per the [Dalamud AI usage policy](https://dalamud.dev/plugin-publishing/ai-policy), this
fork discloses its level of AI involvement.

**Level: Copilot** — AI implements while the human plans and reviews.

### What that covers

Work in this fork was carried out with Claude (Anthropic) acting as the implementer, under
human direction and review at each step. Specifically:

- The human set the scope and made the decisions: what to investigate, when to stop
  investigating and write code, what to commit, what to defer.
- The AI wrote the code changes, ran the builds, and analysed the diagnostic output.
- **All empirical validation was performed by the human in-game.** The central bug in this
  fork was settled by a live experiment — depositing and retrieving a dresser item and
  observing how the game's backing array responded. The AI proposed the experiment and
  interpreted the result; the human ran it and supplied the ground truth that corrected
  two incorrect AI hypotheses along the way.
- Each change was reviewed before being committed, and the reasoning behind it is recorded
  in the commit messages and in code comments rather than left implicit.

### Why it was implemented this way

The policy asks that "Why did you implement it this way?" never be answered with "I'm not
sure, the AI did it." The substantive change in this fork is that both dresser scan loops
are bounded by `UsedSlots` rather than iterating the full 8000-entry `PrismBoxItems` array.

That bound is not a guess. Only `[0, UsedSlots)` holds live items; entries past the
boundary are non-zero leftovers that the game does not clear. On the test character, a
702-item dresser presented 1212 non-zero entries, so roughly 42% of what the plugin
analysed was stale data. This was confirmed by depositing an item and observing that it
was written at exactly index `UsedSlots`, shifting the leftover block up by one index and
leaving the leftover count unchanged — which establishes the live set as exactly
`[0, UsedSlots)`. Retrieving the item restored the array to its prior state.

The related change — keying the de-duplication on `Slot` alone rather than
`{Slot, ItemId}` — follows from the same investigation. A dresser slot holds exactly one
item, so `Slot` is the identity; the composite key was what allowed stale entries to
survive de-duplication.

What produced the leftover block is still unknown. It is sorted by `ItemId` while the live
region is not, which suggests a past sort or bulk operation, but ordinary deposit and
retrieval does not grow it. The fix is correct regardless of the cause, since it never
reads past the boundary.

### Scope note

This declaration covers work done in this fork. Code inherited from the upstream
repository is not covered by it.

## Credits

Original plugin by **pupwife**. Built on [Dalamud](https://github.com/goatcorp/Dalamud)
and [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs).

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. This project is not affiliated with or endorsed
by Square Enix.
