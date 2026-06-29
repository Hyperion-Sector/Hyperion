# Marking code and content in this repository

Hyperion is a fork (Wizden &rarr; Frontier &rarr; Monolith &rarr; Hyperion). Two things get marked in this tree: the **license** each file carries (tracked with [REUSE](https://reuse.software/) SPDX headers), and **Hyperion's own changes** to inherited files (so future upstream merges surface cleanly). This doc covers both.

## Which license a new file gets

The project as a whole is licensed AGPLv3. A file you author from scratch gets a per-file SPDX header by type:

- **Code** (`.cs`) &rarr; `MPL-2.0`
- **Content** (`.yml`, prototypes, assets) &rarr; `AGPL-3.0-or-later`

This follows the default the upstream Monolith codebase adopted: original code defaults to MPL-2.0 (with Exhibit B removed, so it stays GPL/AGPL-compatible), while content stays AGPLv3.

New Hyperion files live under a `_Hyperion/` namespace folder. The file-level header below is enough; you do not need inline markers inside a file that is wholly ours.

### C# header

```csharp
// SPDX-FileCopyrightText: 2026 Hyperion Contributors
//
// SPDX-License-Identifier: MPL-2.0
```

### YAML / content header

```yaml
# SPDX-FileCopyrightText: 2026 Hyperion Contributors
#
# SPDX-License-Identifier: AGPL-3.0-or-later
```

## Marking changes to upstream files

When you edit a file inherited from upstream (Wizden, Frontier, Monolith), wrap your change so the next merge can find it.

**C# block:**

```csharp
// Hyperion: [what and why]
yourCodeHere();
// End Hyperion
```

**C# single-line value change:**

```csharp
private const int MaxSlots = 5; // Hyperion: 3<5
```

**YAML block:**

```yaml
# Hyperion: [what and why]
- type: SomeComponent
# End Hyperion
```

## Removing upstream code

Do not delete upstream code. Comment it out so the removal surfaces as a merge conflict later instead of silently diverging:

```csharp
// Hyperion: removed — [reason]
/*
[original upstream code]
*/
// End Hyperion
```

## Content borrowed from another fork

Content copied verbatim from a sibling fork (DeltaV, Goobstation, etc.) keeps its **origin** namespace and its **original license**. It does not go under `_Hyperion/` and does not get a Hyperion header. Preserve each RSI's `meta.json` `license` and `copyright` exactly: sprites are frequently CC-BY-SA with named authors, not AGPL, and restamping them strips required attribution. Only the upstream file you edit to wire the content in gets a `# Hyperion:` marker.
