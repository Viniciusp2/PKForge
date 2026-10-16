using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>Read-only view of the Mystery Gift cards physically stored in a save.
/// Receiving, deleting, and received-flag mutation are intentionally not exposed.</summary>
public static class MysteryGiftInboxEditor
{
    public static async Task ShowAsync(Grid host, ISaveEngineSession session)
    {
        var inbox = session.GetMysteryGiftInbox();
        if (!inbox.Supported)
        {
            await EditorMenu.ShowAsync(host, "Caixa de entrada do Mystery Gift",
                "Este jogo não tem um armazenamento de cartões de Mystery Gift suportado.", "OK");
            return;
        }
        if (inbox.Cards.Count == 0)
        {
            await EditorMenu.ShowAsync(host, "Caixa de entrada do Mystery Gift",
                "Nenhum cartão de Mystery Gift nem registro de presente recebido está guardado neste save.", "OK");
            return;
        }

        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        string SpeciesName(int species) => (uint)species < (uint)data.SpeciesNames.Count && data.SpeciesNames[species].Length != 0
            ? data.SpeciesNames[species]
            : $"#{species}";

        while (true)
        {
            inbox = session.GetMysteryGiftInbox();
            var options = inbox.Cards.Select(card => new PickItem(card.Slot,
                $"{card.Title} · {CardNumber(card)}")).ToArray();
            var picked = await PickerMenu.ShowAsync(host, $"Caixa de entrada do Mystery Gift · {inbox.Cards.Count}", options);
            if (picked is null)
                return;
            var card = inbox.Cards.FirstOrDefault(candidate => candidate.Slot == picked.Id);
            if (card is null)
                continue;

            var kind = card.IsReceivedRecord ? "Registro de presente recebido" : "Cartão Mágico guardado";
            var contents = card.IsEntity && card.Species > 0
                ? $"Pokémon: {SpeciesName(card.Species)} · Lv. {card.Level}"
                : "Presente ou registro que não é Pokémon";
            var state = card.IsReceivedRecord ? "Entrada do histórico" : card.GiftUsed ? "Presente usado" : "Presente não usado";
            await EditorMenu.ShowAsync(host, card.Title,
                $"{kind}\n{CardNumber(card)} · {card.Type}\n{contents}\n{state}\n\nSomente leitura: o PKForge não altera cartões nem marcas de recebido.", "Voltar");
        }
    }

    private static string CardNumber(MysteryGiftCard card) => card.CardId > 0
        ? $"Cartão #{card.CardId:0000}"
        : $"Slot {card.Slot + 1}";
}
