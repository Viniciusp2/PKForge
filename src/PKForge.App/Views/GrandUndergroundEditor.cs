using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>BDSP's Grand Underground inventory is a distinct fixed list, not a Bag pocket.</summary>
public static class GrandUndergroundEditor
{
    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        var slot = Math.Max(0, viewModel.SelectedSlot);
        while (true)
        {
            var items = session.GetGrandUndergroundItems();
            if (items.Count == 0)
            {
                await EditorMenu.ShowAsync(host, "Grand Underground", "Este jogo não tem inventário do Grand Underground.", "OK");
                return;
            }

            var options = items.Select(item => new PadOption($"{item.Name} · {item.Count}/{item.MaxCount} · {item.Type}"))
                .Append(new PadOption("Encher todas as pilhas", IconPath: "fill", Detail: "Todos os itens vão para o máximo; um ponto de restauração é criado antes."))
                .Append(new PadOption("Limpar todas as pilhas", IconPath: "clear", Detail: "Todos os itens vão para 0; um ponto de restauração é criado antes."))
                .ToArray();
            var choice = await EditorMenu.ShowAsync(host, "Grand Underground", "Esferas, tesouros, estátuas e pedestais", options);
            if (choice is null) return;

            if (choice is "Encher todas as pilhas" or "Limpar todas as pilhas")
            {
                var filling = choice == "Encher todas as pilhas";
                var verb = filling ? "Encher" : "Limpar";
                var confirmed = await PadMenu.ConfirmAsync(host, $"{verb} os itens do subterrâneo?",
                    filling ? "Todas as pilhas do Grand Underground ficam cheias. Um ponto de restauração é criado antes." : "Todas as pilhas do Grand Underground vão para 0. Um ponto de restauração é criado antes.",
                    filling ? "Encher" : "Limpar");
                if (!confirmed) continue;
                var saved = await viewModel.RunMutationAsync(s =>
                {
                    foreach (var item in s.GetGrandUndergroundItems())
                        s.SetGrandUndergroundItemCount(item.Id, filling ? item.MaxCount : 0);
                    return new GenerationOutcome(true, filling ? "Todas as pilhas do Grand Underground estão cheias." : "Todas as pilhas do Grand Underground foram limpas.");
                }, slot, refreshSlot: false, action: SaveAction.EditInventory);
                if (!saved) return;
                continue;
            }

            var selected = items.FirstOrDefault(item => choice.StartsWith($"{item.Name} ·", StringComparison.Ordinal));
            if (selected is null) continue;
            var next = await StatsPopup.ShowSingleAsync(host, selected.Name, selected.Count, selected.MaxCount);
            if (next is not { } count || count == selected.Count) continue;
            var savedCount = await viewModel.RunMutationAsync(s =>
            {
                var stored = s.SetGrandUndergroundItemCount(selected.Id, count);
                return new GenerationOutcome(true, $"{selected.Name} ×{stored}");
            }, slot, refreshSlot: false, action: SaveAction.EditInventory);
            if (!savedCount) return;
        }
    }
}
