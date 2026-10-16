using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Key-item Mystery Gift events (Eon/Aurora/Mystic Ticket, Old Sea Map, Member Card, Oak's
/// Letter, Azure Flute, Secret Key, Enigma Stone, Liberty Pass, Crystal's GS Ball): shows whether each is armed and writes the item plus the
/// exact flags/vars the distribution sets, through the one safe write path.
/// </summary>
public static class KeyItemEventsEditor
{
    private const string Title = "Eventos de itens-chave";

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        if (!KeyItemEventService.IsSupported(session))
        {
            await EditorMenu.ShowAsync(host, Title,
                "Os eventos de itens-chave estão mapeados para Crystal, Ruby/Sapphire, Emerald, FireRed/LeafGreen, Diamond/Pearl, Platinum, HeartGold/SoulSilver, Black/White e Omega Ruby/Alpha Sapphire.", "OK");
            return;
        }

        var slot = Math.Max(0, viewModel.SelectedSlot);
        while (true)
        {
            var events = KeyItemEventService.GetEvents(session);
            var options = events.Select(e => new PadOption($"{e.Title} · {StateLabel(e.State)}", IconPath: "key",
                Accent: e.State switch
                {
                    KeyItemEventState.Enabled => UiTokens.Green,
                    KeyItemEventState.Partial => UiTokens.GiftRed,
                    _ => null,
                })).ToArray();
            var choice = await EditorMenu.ShowAsync(host, Title, "Item + flags de evento, como a distribuição real grava", options);
            if (choice is null) return;

            var selected = events.FirstOrDefault(e => choice.StartsWith($"{e.Title} ·", StringComparison.Ordinal));
            if (selected is null) continue;
            if (!await ShowEventAsync(host, selected, slot, viewModel)) return;
        }
    }

    /// <summary>False when a write was attempted and failed (status already set).</summary>
    private static async Task<bool> ShowEventAsync(Grid host, KeyItemEventStatus status, int slot, BoxBrowserViewModel viewModel)
    {
        var lines = new List<string>
        {
            $"{status.ItemName} → {status.Destination}",
            status.NeedsItem
                ? $"Item na mochila: {YesNo(status.HasItem)} · Flags de evento: {YesNo(status.EventArmed)}"
                : $"Flags de evento: {YesNo(status.EventArmed)} · o {status.ItemName} é entregue no jogo",
            StateDetail(status.State),
        };
        if (status.Prerequisite is { } prerequisite)
            lines.Add($"Observação: {prerequisite}");

        var enable = status.State is KeyItemEventState.Enabled ? null : "Ativar evento";
        var disable = status.HasItem || status.EventArmed ? "Desativar evento" : null;
        var options = new[] { enable, disable, "Voltar" }.OfType<string>()
            .Select(label => new PadOption(label, Accent: label switch
            {
                "Ativar evento" => UiTokens.Green,
                "Desativar evento" => UiTokens.GiftRed,
                _ => null,
            })).ToArray();
        var choice = await EditorMenu.ShowAsync(host, status.Title, string.Join("\n", lines), options);
        if (choice is not ("Ativar evento" or "Desativar evento")) return true;

        if (HardcoreMode.Blocks(SaveAction.InjectEvent, out var blocked))
        {
            await EditorMenu.ShowAsync(host, "Modo Hardcore", blocked, "OK");
            return true;
        }

        var enabling = choice == "Ativar evento";
        var confirmed = await PadMenu.ConfirmAsync(host, enabling ? "Ativar o evento?" : "Desativar o evento?",
            (enabling, status.NeedsItem) switch
            {
                (true, true) => $"Adiciona o {status.ItemName} e liga as flags de evento. Um ponto de restauração é criado antes.",
                (true, false) => "Liga as flags de evento. Um ponto de restauração é criado antes.",
                (false, true) => $"Remove o {status.ItemName} e desliga as flags de evento. O histórico de mostrado/capturado é mantido. Um ponto de restauração é criado antes.",
                (false, false) => "Desliga as flags de evento. O histórico de recebido/capturado é mantido. Um ponto de restauração é criado antes.",
            },
            enabling ? "Ativar" : "Desativar");
        if (!confirmed) return true;

        var id = status.Id;
        return await viewModel.RunMutationAsync(
            s => enabling ? KeyItemEventService.Enable(s, id) : KeyItemEventService.Disable(s, id),
            slot, refreshSlot: false, action: SaveAction.InjectEvent);
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string StateLabel(KeyItemEventState state) => state switch
    {
        KeyItemEventState.Enabled => "Pronto",
        KeyItemEventState.Partial => "Incompleto",
        KeyItemEventState.Used => "Usado",
        KeyItemEventState.Completed => "Concluído",
        _ => "Desligado",
    };

    private static string StateDetail(KeyItemEventState state) => state switch
    {
        KeyItemEventState.Enabled => "Pronto: o evento vai acontecer no jogo.",
        KeyItemEventState.Partial => "Incompleto: o item e as flags não batem, então o jogo ignora. Ative para corrigir.",
        KeyItemEventState.Used => "Já usado no jogo: o tíquete foi mostrado ao marinheiro, ou a GS Ball foi recebida.",
        KeyItemEventState.Completed => "O Pokémon do evento já foi capturado ou derrotado.",
        _ => "Não ativado.",
    };
}
