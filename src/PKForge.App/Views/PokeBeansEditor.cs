using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// SM/USUM Poké Beans live in Poké Pelago, not the normal Bag. This editor keeps
/// the distinction visible while using the same safe write path as every other edit.
/// </summary>
public static class PokeBeansEditor
{
    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        var slot = Math.Max(0, viewModel.SelectedSlot);
        while (true)
        {
            var beans = session.GetPokeBeans();
            if (beans.Count == 0)
            {
                await EditorMenu.ShowAsync(host, "POKé BEANS", "Este jogo não tem armazenamento de Beans do Poké Pelago.", "OK");
                return;
            }

            var options = beans.Select(bean => new PadOption($"{bean.Name} · {bean.Count}/{bean.MaxCount}"))
                .Append(new PadOption("Encher todas as pilhas de Beans", IconPath: "fill", Detail: "Todos os Beans vão para o máximo; um ponto de restauração é criado antes."))
                .Append(new PadOption("Limpar todas as pilhas de Beans", IconPath: "clear", Detail: "Todos os Beans vão para 0; um ponto de restauração é criado antes."))
                .ToArray();
            var choice = await EditorMenu.ShowAsync(host, "POKé BEANS", "Armazenamento do Poké Pelago", options);
            if (choice is null) return;

            if (choice == "Encher todas as pilhas de Beans" || choice == "Limpar todas as pilhas de Beans")
            {
                var filling = choice.StartsWith("Encher", StringComparison.Ordinal);
                var verb = filling ? "Encher" : "Limpar";
                var confirmed = await PadMenu.ConfirmAsync(host, $"{verb} todos os Beans?",
                    filling ? "Todas as pilhas de Poké Beans vão para 255. Um ponto de restauração é criado antes." : "Todas as pilhas de Poké Beans vão para 0. Um ponto de restauração é criado antes.",
                    filling ? "Encher" : "Limpar");
                if (!confirmed) continue;
                var success = await viewModel.RunMutationAsync(s =>
                {
                    foreach (var bean in s.GetPokeBeans()) s.SetPokeBeanCount(bean.Id, filling ? bean.MaxCount : 0);
                    return new GenerationOutcome(true, filling ? "Todas as pilhas de Poké Beans estão cheias." : "Todas as pilhas de Poké Beans foram limpas.");
                }, slot, refreshSlot: false, action: SaveAction.EditInventory);
                if (!success) return;
                continue;
            }

            var index = Array.FindIndex(beans.ToArray(), bean => choice.StartsWith(bean.Name, StringComparison.Ordinal));
            if (index < 0) continue;
            var selected = beans[index];
            var count = await StatsPopup.ShowSingleAsync(host, selected.Name, selected.Count, selected.MaxCount);
            if (count is not { } next || next == selected.Count) continue;
            var saved = await viewModel.RunMutationAsync(s =>
            {
                var stored = s.SetPokeBeanCount(selected.Id, next);
                return new GenerationOutcome(true, $"{selected.Name} ×{stored}");
            }, slot, refreshSlot: false, action: SaveAction.EditInventory);
            if (!saved) return;
        }
    }
}
