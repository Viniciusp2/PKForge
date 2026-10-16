using System.Text;
using PKForge.App.Services;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;

namespace PKForge.App.Views;

/// <summary>
/// The Bank's PKSM bridge: bring a 3DS PKSM bank (.bnk + .json), a PKSM dumps zip or loose
/// .pk files into the bank with a preview first, and write bank boxes back out as a PKSM
/// bank the 3DS can open. Format rules live in <see cref="PksmBankFile"/>; this is the UI.
/// </summary>
public static class PksmTransferFlow
{
    /// <summary>Box names imported from PKSM (or set later); shown beside the box number.</summary>
    public static BankBoxNames BoxNames => BankBoxDecor.Names;

    /// <summary>Engine decode, mapped from PKSM tags to chronological generations.</summary>
    public static PksmDecoded Decode(PksmGeneration? generation, byte[] bytes, string source)
    {
        var number = generation is { } g ? PksmBankFile.GenerationNumber(g) : (int?)null;
        if (number == 0) return PksmDecoded.Skip($"marca de geração desconhecida 0x{(uint)generation!.Value:X}");
        var result = PksmEntityConversion.Decode(number, generation == PksmGeneration.LetsGo, bytes, source);
        return new PksmDecoded(result.Bytes, result.Info, result.Reason);
    }

    public static PksmEncoded Encode(byte[] bankBytes, string? format)
    {
        var result = PksmEntityConversion.Encode(bankBytes, format);
        return result.Data is not null && PksmBankFile.FromGenerationNumber(result.Generation, result.LetsGo) is { } tag
            ? new PksmEncoded(tag, result.Data, null)
            : PksmEncoded.Skip(result.Reason ?? "O PKSM não tem slot para este formato");
    }

    /// <summary>
    /// Pick → preview → write. Importing your own PKSM bank is a transfer from your 3DS, so
    /// Hardcore mode allows it (every entry is tagged with its PKSM origin); loose files and
    /// dumps are copies from outside the games, which Hardcore refuses like any .pk import.
    /// </summary>
    public static async Task<string?> ImportAsync(Grid host, IBankService bank)
    {
        var services = IPlatformApplication.Current?.Services;
        var picker = services?.GetService<IDocumentPicker>();
        var access = services?.GetService<ISaveFileAccess>();
        if (picker is null || access is null) return null;

        var go = await PadMenu.ShowAsync(host, "Importar do PKSM",
            "Escolha o .bnk do banco (e o .json dele, com os nomes das boxes), um zip de /3ds/PKSM/dumps, ou arquivos .pk.\n" + PksmBankTransfer.SdCardHelp,
            new PadOption("Escolher arquivos…", IconPath: "folder"));
        if (go is null) return null;
        var documents = await picker.PickManyAsync();
        if (documents.Count == 0) return null;

        var overlay = LoadingOverlay.Show(host, "Lendo arquivos do PKSM…", $"{documents.Count} file(s).");
        List<PksmImportPlan> plans;
        var notes = new List<string>();
        try
        {
            var files = new List<(string, byte[])>();
            foreach (var document in documents)
                files.Add((document.DisplayName, (await access.ReadAsync(document.DocumentId)).ToArray()));
            (plans, notes) = await Task.Run(() => BuildPlans(files));
        }
        catch (Exception error)
        {
            return $"A importação do PKSM falhou: {error.Message}";
        }
        finally
        {
            overlay.Close();
        }

        if (HardcoreMode.IsOn && plans.Any(p => !p.IsBank && p.ReadyCount > 0))
        {
            plans.RemoveAll(p => !p.IsBank);
            notes.Add("Arquivos .pk avulsos e dumps são cópias - ignorados no modo Hardcore (os bancos ainda são importados).");
        }
        if (plans.Sum(p => p.ReadyCount) == 0)
        {
            var why = new StringBuilder("Nada importável encontrado.");
            foreach (var plan in plans) AppendPlan(why, plan);
            foreach (var note in notes) why.Append('\n').Append(note);
            await PadMenu.ShowAsync(host, "Importação do PKSM", why.ToString(), "OK");
            return "Nenhum Pokémon importável nesses arquivos.";
        }

        var preview = new StringBuilder();
        foreach (var plan in plans) AppendPlan(preview, plan);
        foreach (var note in notes) preview.Append('\n').Append(note);
        preview.Append("\nCópias exatas de Pokémon que já estão no banco são ignoradas.");
        var choice = await PadMenu.ShowAsync(host, $"Importar {plans.Sum(p => p.ReadyCount)} Pokémon?", preview.ToString().Trim(),
            new PadOption("Adicionar como novas boxes (mantém disposição + nomes)", IconPath: "box", Detail: "Novas boxes depois da última usada, com os mesmos slots e nomes de box."),
            new PadOption("Juntar nos slots livres", IconPath: "compact", Detail: "Preenche os primeiros slots livres do Banco. A disposição e os nomes das boxes não são mantidos."));
        if (choice is null) return "Importação do PKSM cancelada.";
        var mode = choice.StartsWith("Adicionar", StringComparison.Ordinal) ? PksmImportMode.NewBoxes : PksmImportMode.Merge;

        var writing = LoadingOverlay.Show(host, "Preenchendo o banco…", "Gravando as entradas.");
        try
        {
            var result = await Task.Run(() => PksmBankTransfer.Apply(bank, BoxNames, plans, mode));
            var where = mode == PksmImportMode.NewBoxes && result.BoxesUsed > 0
                ? $" nas boxes {result.FirstNewBox + 1:00}-{result.FirstNewBox + result.BoxesUsed:00}"
                : "";
            var skipped = plans.Sum(p => p.SkippedCount);
            return $"PKSM: {result.Imported} importados{where}"
                   + (result.Duplicates > 0 ? $" · {result.Duplicates} repetido(s) ignorado(s)" : "")
                   + (skipped > 0 ? $" · {skipped} ilegíveis" : "") + ".";
        }
        catch (Exception error)
        {
            return $"A importação do PKSM falhou: {error.Message}";
        }
        finally
        {
            writing.Close();
        }
    }

    private static (List<PksmImportPlan>, List<string>) BuildPlans(IEnumerable<(string, byte[])> files)
    {
        var sources = PksmBankTransfer.Collect(files);
        var plans = new List<PksmImportPlan>();
        var notes = new List<string>();
        foreach (var (name, bnk, json) in sources.Banks)
        {
            try { plans.Add(PksmBankTransfer.PlanBank(name, bnk, json, Decode)); }
            catch (InvalidDataException error) { notes.Add(error.Message); }
        }
        if (sources.LooseFiles.Count > 0)
            plans.Add(PksmBankTransfer.PlanLoose($"{sources.LooseFiles.Count} arquivo(s) avulso(s)", sources.LooseFiles, Decode));
        if (sources.Ignored.Count > 0)
            notes.Add($"Ignorados: {string.Join(", ", sources.Ignored.Take(4))}{(sources.Ignored.Count > 4 ? "…" : "")}");
        return (plans, notes);
    }

    private static void AppendPlan(StringBuilder text, PksmImportPlan plan)
    {
        text.Append('\n').Append(plan.IsBank
            ? $"{plan.Source}.bnk (v{(plan.SourceVersion == 0 ? "bank.bin" : plan.SourceVersion)}, {plan.UsedBoxes} box(es) usada(s)): {plan.ReadyCount} prontos"
            : $"{plan.Source}: {plan.ReadyCount} prontos");
        if (plan.CountsByGeneration.Count > 0)
            text.Append(" - ").Append(string.Join(", ", plan.CountsByGeneration.Select(c => $"{c.Label} ×{c.Count}")));
        if (plan.Truncated) text.Append("\n  O arquivo é menor do que o cabeçalho diz; os slots que faltam são lidos como vazios.");
        foreach (var (reason, count) in plan.SkipReasons.Take(4))
            text.Append($"\n  ignorados {count}: {reason}");
    }

    /// <summary>Writes <paramref name="entries"/> as NAME.bnk + NAME.json into a picked folder.</summary>
    public static async Task<string?> ExportAsync(Grid host, IBankService bank, IReadOnlyList<BankEntry> entries, PksmExportLayout layout, string label)
    {
        if (entries.Count == 0) return "Nada para exportar.";
        var services = IPlatformApplication.Current?.Services;
        var picker = services?.GetService<IFolderPicker>();
        var files = services?.GetService<IFolderFileAccess>();
        if (picker is null || files is null) return null;

        var typed = await TextPopup.ShowAsync(host, "Nome do banco do PKSM",
            "Vira NOME.bnk + NOME.json. Em branco = pkforge. Use pksm_1 só para substituir o banco padrão do PKSM.");
        if (typed is null) return null;
        var name = PksmBankTransfer.SanitizeBankName(typed);
        var folder = await picker.PickFolderAsync();
        if (folder is null) return null;

        var overlay = LoadingOverlay.Show(host, "Montando um banco do PKSM…", $"{entries.Count} Pokémon de {label}.");
        PksmExportResult result;
        try
        {
            result = await Task.Run(() => PksmBankTransfer.Export(bank, entries, Encode, BoxNames.Get, layout));
            if (result.Written > 0)
            {
                await files.WriteFileAsync(folder.TreeId, name + ".bnk", result.Bank);
                await files.WriteFileAsync(folder.TreeId, name + ".json", result.BoxNames);
            }
        }
        catch (Exception error)
        {
            return $"A exportação para o PKSM falhou: {error.Message}";
        }
        finally
        {
            overlay.Close();
        }

        var message = new StringBuilder(result.Written == 0
            ? "Nenhum destes Pokémon cabe num banco do PKSM."
            : $"{name}.bnk: {result.Written} Pokémon em {result.Boxes} box(es) → {folder.DisplayName}.");
        foreach (var group in result.Skipped.GroupBy(s => s.Reason).Take(4))
            message.Append($"\nDe fora {group.Count()}: {group.Key}");
        if (result.Written > 0)
            message.Append("\n\n").Append(PksmBankTransfer.ReturnHelp.Replace("NOME", name).Replace("box count", result.Boxes.ToString()));
        await PadMenu.ShowAsync(host, "Exportação para o PKSM", message.ToString(), "OK");
        return result.Written == 0
            ? "Exportação para o PKSM: nada que o PKSM possa guardar."
            : $"Banco do PKSM gravado: {result.Written} Pokémon ({result.Skipped.Count} de fora) → {folder.DisplayName}/{name}.bnk";
    }
}
