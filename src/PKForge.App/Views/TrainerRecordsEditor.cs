using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>Read-only dashboard for save-native trainer records. Record meanings vary by game,
/// so this deliberately exposes values without offering unsafe generic writes.</summary>
public static class TrainerRecordsEditor
{
    private const int PageSize = 24;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session)
    {
        var info = session.GetTrainerRecords();
        if (!info.Supported)
        {
            await EditorMenu.ShowAsync(host, "Recordes do treinador", "Este jogo não tem uma tabela de recordes de treinador suportada.", "OK");
            return;
        }

        var page = 0;
        var pageCount = (info.Records.Count + PageSize - 1) / PageSize;
        while (true)
        {
            var entries = info.Records.Skip(page * PageSize).Take(PageSize);
            var options = entries.Select(Format).Select(text => new PadOption(text)).ToList();
            if (page > 0) options.Add(new PadOption("Página anterior", IconPath: "back"));
            if (page < pageCount - 1) options.Add(new PadOption("Próxima página", IconPath: "next"));
            var choice = await EditorMenu.ShowAsync(host, $"Recordes do treinador · {page + 1}/{pageCount}",
                "Somente leitura: o significado dos recordes e os limites seguros mudam de jogo para jogo.", options.ToArray());
            if (choice is null) return;
            if (choice == "Página anterior") { page--; continue; }
            if (choice == "Próxima página") { page++; continue; }
        }
    }

    private static string Format(TrainerRecordEntry record) => record.Maximum == int.MaxValue
        ? $"Recorde {record.Index + 1} · {record.Value:N0}"
        : $"Recorde {record.Index + 1} · {record.Value:N0}/{record.Maximum:N0}";
}
