using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Ruby/Sapphire/Emerald clock repair (PKHeX SAV_RTC3), explained in plain words. Writes use
/// <see cref="SaveAction.RepairRtc"/>, which Hardcore mode refuses like every other data edit.
/// </summary>
public static class ClockRepairEditor
{
    private const string Reset = "Zerar os dois relógios (recomendado)";
    private const string BerryFix = "Berry fix (passar do bug de 1 ano)";
    private const string ArmReset = "Deixar o jogo perguntar a hora de novo";
    private const string Explain = "O que isso faz?";

    private const SaveAction Action = SaveAction.RepairRtc;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        while (true)
        {
            var state = ClockRepairService.GetState(session);
            var detail =
                $"Início do relógio: {state.Initial}{Environment.NewLine}" +
                $"Última atualização de berries/eventos: {state.Elapsed}{Environment.NewLine}" +
                (state.ResetArmed ? "O reset do relógio no jogo está ativado." : "O reset do relógio no jogo não está ativado.") +
                Environment.NewLine + "O relógio do próprio emulador (os dados de RTC do mGBA) não é alterado.";
            if (await WorldEventsMenu.BlockedAsync(host, "Conserto do relógio", detail, Action)) return;

            var choice = await EditorMenu.ShowAsync(host, "Conserto do relógio", detail,
                new PadOption(Reset, Accent: UiTokens.Green, IconPath: "restore",
                    Detail: "As berries e os eventos diários recomeçam a partir de agora; use depois de uma bateria descarregada."),
                new PadOption(BerryFix, IconPath: "fix", Accent: WorldEventsMenu.Good(state.BerryFixApplied),
                    Detail: "Para as berries de Ruby e Sapphire que pararam de crescer depois de um ano."),
                new PadOption(ArmReset, IconPath: "calendar", Accent: WorldEventsMenu.Good(state.ResetArmed),
                    Detail: "O jogo mostra a própria tela de ajuste do relógio na próxima vez que iniciar."),
                new PadOption(Explain, IconPath: "info"));
            switch (choice)
            {
                case Reset:
                    if (!await WorldEventsMenu.WriteAsync(viewModel, s =>
                        {
                            ClockRepairService.ResetClocks(s);
                            return "Relógios zerados: as berries e os eventos diários recomeçam a partir de agora.";
                        }, Action)) return;
                    break;
                case BerryFix:
                    if (!await WorldEventsMenu.WriteAsync(viewModel, s =>
                        $"Berry fix aplicado: {ClockRepairService.ApplyBerryFix(s).Elapsed}.", Action)) return;
                    break;
                case ArmReset:
                    if (!await WorldEventsMenu.WriteAsync(viewModel, s =>
                        {
                            ClockRepairService.ArmInGameReset(s);
                            return "Reset do relógio ativado: o jogo vai oferecer o ajuste da hora na próxima inicialização.";
                        }, Action)) return;
                    break;
                case Explain:
                    await EditorMenu.ShowAsync(host, "Sobre o relógio",
                        "O jogo guarda dois horários: quando o relógio começou e quando as berries e os eventos diários foram atualizados pela última vez. " +
                        "O tempo só avança no jogo quando o relógio do cartucho passa dessa última atualização." +
                        Environment.NewLine + Environment.NewLine +
                        "Zerar os dois relógios: coloca os dois em zero, para o jogo voltar a contar a partir do relógio do cartucho. " +
                        "Use isso depois de uma bateria descarregada ou ao mover o save para outro aparelho ou emulador." +
                        Environment.NewLine + Environment.NewLine +
                        "Berry fix: Ruby e Sapphire param de atualizar as berries depois que o relógio passa de um ano. " +
                        "Isso move o contador da última atualização para depois do dia 734, como faz o Berry fix do PKHeX." +
                        Environment.NewLine + Environment.NewLine +
                        "Perguntar a hora de novo: liga a própria tela de reset do relógio do jogo.",
                        "OK");
                    break;
                default:
                    return;
            }
        }
    }
}
