using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// SAVE DATA → "World &amp; events": every in-game world editor the open game supports, in one
/// list - clocks, roamers, one-time encounters, gadgets (Apricorn Box, Pokéwalker, Entralink)
/// and the existing honey tree / key item editors. Only what this game stores is listed.
/// Each editor shows its state in Hardcore mode and refuses writes there.
/// </summary>
public static class WorldEventsMenu
{
    private const string Clock = "Conserto do relógio";
    private const string Roamers = "Lendários errantes";
    private const string Resets = "Reativar encontros únicos";
    private const string Entralink = "Entralink & Dream World";
    private const string Apricorns = "Apricorn Box";
    private const string Walker = "Pokéwalker";
    private const string Honey = "Árvores de mel";
    private const string KeyItems = "Eventos de itens-chave";

    /// <summary>True when the open game has anything to show here (the SAVE DATA entry is hidden otherwise).</summary>
    public static bool HasAny(ISaveEngineSession session) => Options(session).Length != 0;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        while (true)
        {
            var options = Options(session);
            if (options.Length == 0) return;
            var note = HardcoreMode.IsOn ? HardcoreMode.StatusFor(SaveAction.EditWorld) : "Cada mudança cria um ponto de restauração antes.";
            var choice = await PadMenu.ShowAsync(host, "Mundo e eventos", note, options);
            switch (choice)
            {
                case Clock: await ClockRepairEditor.ShowAsync(host, session, viewModel); break;
                case Roamers: await RoamerEditor.ShowAsync(host, session, viewModel); break;
                case Resets: await EventResetEditor.ShowAsync(host, session, viewModel); break;
                case Entralink: await EntralinkEditor.ShowAsync(host, session, viewModel); break;
                case Apricorns: await HgssGadgetsEditor.ShowApricornsAsync(host, session, viewModel); break;
                case Walker: await HgssGadgetsEditor.ShowPokewalkerAsync(host, session, viewModel); break;
                case Honey: await HoneyTreeEditor.ShowAsync(host, session, viewModel); break;
                case KeyItems: await KeyItemEventsEditor.ShowAsync(host, session, viewModel); break;
                default: return;
            }
        }
    }

    private static PadOption[] Options(ISaveEngineSession session)
    {
        var options = new List<PadOption>();
        if (ClockRepairService.IsSupported(session)) options.Add(new(Clock, IconPath: "calendar", Detail: "Berries e eventos diários parados? Conserte o relógio"));
        if (RoamerService.IsSupported(session)) options.Add(new(Roamers, IconPath: "map", Detail: "Espécie, IVs, PID, shiny - sorteios legais"));
        if (EventResetService.IsSupported(session)) options.Add(new(Resets, IconPath: "restore", Detail: "Traga de volta lendários e presentes únicos"));
        if (EntralinkService.IsSupported(session)) options.Add(new(Entralink, IconPath: "game", Detail: "Entree Forest, Pass Powers, missões do Funfest"));
        if (ApricornService.IsSupported(session)) options.Add(new(Apricorns, IconPath: "ball", Detail: "Apricorns para as bolas do Kurt"));
        if (PokewalkerService.IsSupported(session)) options.Add(new(Walker, IconPath: "records", Detail: "Passos, watts e percursos"));
        if (HoneyTreeService.IsSupported(session)) options.Add(new(Honey, IconPath: "tree"));
        if (KeyItemEventService.IsSupported(session)) options.Add(new(KeyItems, IconPath: "key"));
        return options.ToArray();
    }

    /// <summary>The one write path for the world editors: Hardcore guard, restore point and
    /// atomic write through <see cref="BoxBrowserViewModel.RunMutationAsync"/>. The edit returns
    /// the confirmation line; an exception becomes a refused write with its message.</summary>
    internal static Task<bool> WriteAsync(BoxBrowserViewModel viewModel, Func<ISaveEngineSession, string> edit,
        SaveAction action = SaveAction.EditWorld) =>
        viewModel.RunMutationAsync(s =>
        {
            try
            {
                return new GenerationOutcome(true, edit(s));
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
            {
                return new GenerationOutcome(false, ex.Message);
            }
        }, Math.Max(0, viewModel.SelectedSlot), refreshSlot: false, action: action);

    /// <summary>State-only view for Hardcore mode: shows <paramref name="detail"/> and the block reason.</summary>
    internal static async Task<bool> BlockedAsync(Grid host, string title, string detail, SaveAction action = SaveAction.EditWorld)
    {
        if (!HardcoreMode.Blocks(action, out var status)) return false;
        await EditorMenu.ShowAsync(host, title, detail + Environment.NewLine + status, "OK");
        return true;
    }

    internal static Color? Good(bool on) => on ? UiTokens.Green : null;
}
