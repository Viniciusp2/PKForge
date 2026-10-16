using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>
/// Hardcore mode, PKForge edition: the achievement-hunter setting. While it is on the
/// open save may be browsed, organised, moved, transferred, backed up and exported,
/// but nothing that edits, fabricates or duplicates Pokémon data can be written - a
/// Pokémon may be MOVED, never COPIED. Off by default. Hardcore wins over
/// <see cref="HaXMode"/>: HaX mode only widens which options are offered, and every
/// one of its writes still has to pass this guard.
/// </summary>
public static class HardcoreMode
{
    private const string Key = "hardcore_mode";

    /// <summary>The one line every blocked surface shows instead of writing.</summary>
    public const string StatusLine = "MODO HARDCORE - edição de dados desligada";

    /// <summary>Short marker for footers and headers while the mode is on.</summary>
    public const string Marker = "HARDCORE";

    /// <summary>The settings-screen explanation; the HaX-mode relationship is stated, not implied.</summary>
    public const string SettingExplanation =
        "Para jogar no estilo RetroAchievements. Navegar, organizar, mover, enviar para outro jogo, " +
        "depositar e retirar do banco como MOVIMENTO, pontos de restauração e exportações continuam disponíveis; " +
        "tudo o que edita, gera ou duplica dados de Pokémon é bloqueado, e um Pokémon pode ser movido, mas nunca copiado. " +
        "O Hardcore tem prioridade sobre o modo HaX: enquanto ele está ligado, as opções do modo HaX também não podem ser gravadas.";

    public static bool IsOn => Preferences.Default.Get(Key, false);

    public static void Set(bool on) => Preferences.Default.Set(Key, on);

    /// <summary>The decision table for the current setting.</summary>
    public static SaveGuard Guard => SaveGuard.For(IsOn);

    /// <summary>The status a blocked action shows, e.g. "HARDCORE MODE - data editing is off (duplication blocked)".</summary>
    public static string StatusFor(SaveAction action) => $"{StatusLine} ({Describe(action)} bloqueado)";

    /// <summary>
    /// True when Hardcore mode forbids <paramref name="action"/>; <paramref name="status"/>
    /// then carries the line to show instead of writing.
    /// </summary>
    public static bool Blocks(SaveAction action, out string status)
    {
        if (!Guard.Blocks(action))
        {
            status = string.Empty;
            return false;
        }
        status = StatusFor(action);
        return true;
    }

    private static string Describe(SaveAction action) => action switch
    {
        SaveAction.EditMon => "edição de Pokémon",
        SaveAction.CreateMon => "criação de Pokémon",
        SaveAction.BatchEdit => "edição em lote",
        SaveAction.Duplicate => "duplication",
        SaveAction.InjectEvent => "injeção de eventos",
        SaveAction.EditInventory => "edição de inventário",
        SaveAction.EditTrainer => "edição do treinador",
        SaveAction.EditDex => "edição da dex",
        SaveAction.EditWorld => "edição do mundo",
        SaveAction.WriteRawBytes => "gravação de bytes",
        SaveAction.RepairRtc => "conserto do relógio",
        SaveAction.Move => "mover Pokémon",
        SaveAction.Release => "soltar Pokémon",
        SaveAction.ExportFile => "exports",
        SaveAction.Backup => "pontos de restauração",
        _ => "edição de dados",
    };
}
