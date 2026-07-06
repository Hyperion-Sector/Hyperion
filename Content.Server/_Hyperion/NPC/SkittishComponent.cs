// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0
namespace Content.Server._Hyperion.NPC;

/// <summary>
/// Hyperion: marks a prey mob that panics as a group. When one flees, it startles nearby skittish mobs
/// so a whole nest bursts outward at once, even the ones that never saw the threat. See
/// <see cref="MouseAlarmSystem"/>.
/// </summary>
[RegisterComponent]
public sealed partial class SkittishComponent : Component
{
    /// <summary>How far a startle propagates to other skittish mobs.</summary>
    [DataField]
    public float AlarmRadius = 6f;

    /// <summary>How long a mob stays startled (and scurrying) after being alarmed.</summary>
    [DataField]
    public float AlarmSeconds = 4f;

    /// <summary>Runtime: startled until this time. Set by <see cref="MouseAlarmSystem"/>.</summary>
    [ViewVariables]
    public TimeSpan StartledUntil;
}
