// SPDX-FileCopyrightText: 2026 Hyperion Sector
// SPDX-License-Identifier: MPL-2.0

using Robust.Shared.Configuration;

namespace Content.Shared._Hyperion.CCVar;

/// <summary>
/// Contains CVars used by Hyperion.
/// </summary>
[CVarDefs]
public sealed partial class HyperionCVars
{
    #region ShipStorage

    /// <summary>
    ///     How many blob revisions to keep per stored ship. The newest revision is
    ///     what retrieve loads; older revisions are the corruption-fallback and
    ///     admin-restoration surface. Revisions beyond this count are GC'd at store.
    /// </summary>
    public static readonly CVarDef<int> ShipStorageKeepRevisions =
        CVarDef.Create("hyperion.ship_storage.keep_revisions", 3, CVar.SERVERONLY);

    #endregion
}
