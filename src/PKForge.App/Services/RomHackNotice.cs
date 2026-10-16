using System.Security.Cryptography;
using System.Text;

namespace PKForge.App.Services;

/// <summary>
/// The user-facing side of <see cref="Domain.LayoutRiskKind.SuspectedHack"/>: a save that
/// reads as a retail layout but carries species the retail game does not have. The warning
/// is shown once per save; the save is remembered as flagged so its Home menu can offer
/// (and revoke) "Edit at my own risk" without re-analysing the whole file.
/// </summary>
public static class RomHackNotice
{
    private const string FlaggedPrefix = "romhack_flagged_";

    public const string Title = "ROM HACK NÃO SUPORTADO";

    public const string Warning =
        "Isto parece um ROM hack. O PKForge o detectou, mas o motor dele não foi feito para este jogo: " +
        "Pokémon, golpes ou itens podem aparecer errados, e editar pode danificar o save. Continue por sua conta e risco.";

    public const string ReadOnlyOption = "Navegar somente leitura";
    public const string EditOption = "Editar por minha conta e risco";

    /// <summary>The status-strip marker while such a save is open, like the Hardcore one.</summary>
    public const string Marker = "ROM HACK · NÃO SUPORTADO";

    /// <summary>The marker plus how to lift read-only, for a save the user has not accepted.</summary>
    public const string ReadOnlyStatus = Marker + " · SOMENTE LEITURA até você editar por sua conta e risco";

    /// <summary>A corrupting layout is never editable; this explains why nothing is offered.</summary>
    public const string CorruptingTitle = "O SAVE ESTÁ EM SOMENTE LEITURA";

    /// <summary>True once the warning has been shown for this save.</summary>
    public static bool IsFlagged(string documentId) => Preferences.Default.Get(Key(documentId), false);

    public static void MarkFlagged(string documentId) => Preferences.Default.Set(Key(documentId), true);

    // Document ids are SAF URIs: hashed so the preference key stays short and opaque.
    private static string Key(string documentId) =>
        FlaggedPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(documentId)))[..24];
}
