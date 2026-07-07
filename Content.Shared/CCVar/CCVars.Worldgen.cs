using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    ///     Whether or not world generation is enabled.
    /// </summary>
    public static readonly CVarDef<bool> WorldgenEnabled =
        CVarDef.Create("worldgen.enabled", true, CVar.SERVERONLY); // Frontier: true

    /// <summary>
    ///     The worldgen config to use.
    /// </summary>
    public static readonly CVarDef<string> WorldgenConfig =
        CVarDef.Create("worldgen.worldgen_config", "NFDefault", CVar.SERVERONLY); // Frontier: Default<NFDefault

    // Hyperion: gates verbose per-stage worldgen instrumentation logging. Prometheus
    // metrics are always-on and cheap; this only turns the human-readable Info logs on/off.
    public static readonly CVarDef<bool> WorldgenDebugMetrics =
        CVarDef.Create("worldgen.debug_metrics", false, CVar.SERVERONLY);
}
