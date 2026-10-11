using Aria.Core.Models;
using Aria.Core.Settings;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aria.Cli.Ui;

/// <summary>Affichage des tableaux et de la liste à cocher (Spectre.Console).</summary>
internal static class ConsoleUi
{
    private sealed record Choice(string Label, PlanItem? Item);

    public static void Title(string text) => AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(text)}[/]").LeftJustified());

    /// <summary>Tableau de l'état actuel de tous les réglages.</summary>
    public static void ShowPlan(IReadOnlyList<PlanItem> plan, string title)
    {
        var table = new Table().Border(TableBorder.Rounded).Title(Markup.Escape(title))
            .AddColumn("Réglage").AddColumn("Actuel").AddColumn("Cible").AddColumn("Statut");

        foreach (var group in plan.GroupBy(p => p.Setting.Category))
        {
            table.AddRow(new Markup($"[bold]{CategoryName(group.Key)}[/]"), Empty, Empty, Empty);
            foreach (var item in group)
                table.AddRow(
                    new Text(item.Setting.DisplayName),
                    new Text(item.Current?.Display ?? "-"),
                    new Text(item.NeedsChange ? item.Target!.Value.Display : ""),
                    PlanStatus(item));
        }
        AnsiConsole.Write(table);
    }

    /// <summary>Raisons des changements proposés, puis liste à cocher (pré-cochée sauf options de confort).</summary>
    public static IReadOnlyList<PlanItem> SelectChanges(IReadOnlyList<PlanItem> changes)
    {
        var reasons = new Table().Border(TableBorder.Simple).Title("Pourquoi ces changements")
            .AddColumn("Réglage").AddColumn("Raison");
        foreach (var item in changes)
            reasons.AddRow(new Text(item.Setting.DisplayName), new Text(item.Target!.Reason));
        AnsiConsole.Write(reasons);

        var prompt = new MultiSelectionPrompt<Choice>()
            .Title("[bold]Changements proposés[/] — décochez ceux que vous refusez")
            .NotRequired()
            .PageSize(20)
            .MoreChoicesText("[grey](flèches haut/bas pour voir la suite)[/]")
            .InstructionsText("[grey](Espace : cocher/décocher · Entrée : valider)[/]")
            .UseConverter(c => c.Label);

        foreach (var group in changes.GroupBy(c => c.Setting.Category))
        {
            var children = group.Select(item => new Choice(ChoiceLabel(item), item)).ToList();
            prompt.AddChoiceGroup(new Choice($"[bold]{CategoryName(group.Key)}[/] [grey](tout cocher/décocher)[/]", null), children);
            foreach (var choice in children.Where(c => c.Item!.Setting.SelectedByDefault))
                prompt.Select(choice);
        }

        return AnsiConsole.Prompt(prompt).Where(c => c.Item is not null).Select(c => c.Item!).ToList();
    }

    public static bool Confirm(string question) =>
        AnsiConsole.Prompt(new ConfirmationPrompt(Markup.Escape(question))
        {
            Yes = 'o',
            No = 'n',
            DefaultValue = false,
            InvalidChoiceMessage = "[red]Répondre o (oui) ou n (non)[/]"
        });

    public static string Choose(string question, IReadOnlyList<(string Value, string Label)> options) =>
        AnsiConsole.Prompt(new SelectionPrompt<(string Value, string Label)>()
            .Title(Markup.Escape(question))
            .AddChoices(options)
            .UseConverter(o => Markup.Escape(o.Label))).Value;

    /// <summary>Tableau final : ce qui a été appliqué, refusé, ou a échoué.</summary>
    public static void ShowResults(IReadOnlyList<SettingResult> results)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("Résultat")
            .AddColumn("Réglage").AddColumn("Avant").AddColumn("Après").AddColumn("Résultat");

        foreach (var r in results.Where(r => r.Status != SettingStatus.NotApplicable))
        {
            var after = r.Status switch
            {
                SettingStatus.Changed => r.Target?.Display ?? "",
                SettingStatus.Simulated => $"({r.Target?.Display})",
                _ => r.Current?.Display ?? ""
            };
            table.AddRow(new Text(r.DisplayName), new Text(r.Current?.Display ?? "-"), new Text(after), ResultStatus(r));
        }
        AnsiConsole.Write(table);

        int Count(SettingStatus s) => results.Count(r => r.Status == s);
        var simulated = Count(SettingStatus.Simulated);
        AnsiConsole.MarkupLine(
            (simulated > 0 ? $"[yellow]{simulated} serai(en)t modifié(s)[/] · " : "") +
            $"[green]{Count(SettingStatus.Changed)} appliqué(s)[/] · {Count(SettingStatus.Declined)} non retenu(s) · " +
            $"[red]{Count(SettingStatus.Failed)} échec(s)[/] · {Count(SettingStatus.Unchanged)} déjà correct(s)");
    }

    public static void Info(string text) => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(text)}[/]");
    public static void Success(string text) => AnsiConsole.MarkupLine($"[green]{Markup.Escape(text)}[/]");
    public static void Warning(string text) => AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(text)}[/]");

    private static IRenderable Empty => new Text("");

    private static string CategoryName(SettingCategory category) => category switch
    {
        SettingCategory.Windows => "Windows",
        SettingCategory.Nvidia => "NVIDIA",
        SettingCategory.Privacy => "Confidentialité",
        _ => category.ToString()
    };

    private static string ChoiceLabel(PlanItem item) =>
        $"{Markup.Escape(item.Setting.DisplayName)} : {Markup.Escape(item.Current!.Display)} → " +
        $"[yellow]{Markup.Escape(item.Target!.Value.Display)}[/]" +
        (item.Setting.RequiresReboot ? " [grey](redémarrage requis)[/]" : "") +
        (item.Setting.SelectedByDefault ? "" : " [grey](option de confort)[/]");

    private static IRenderable PlanStatus(PlanItem item) =>
        item.ReadError is not null ? new Markup($"[red]{Markup.Escape(item.ReadError)}[/]")
        : item.NotApplicableReason is not null ? new Markup($"[grey]Non applicable : {Markup.Escape(item.NotApplicableReason)}[/]")
        : item.NeedsChange ? new Markup("[yellow]À changer[/]")
        : new Markup("[green]OK[/]");

    private static IRenderable ResultStatus(SettingResult r) => r.Status switch
    {
        SettingStatus.Changed => new Markup("[green]Appliqué[/]" + (r.RequiresReboot ? " [grey](au redémarrage)[/]" : "")),
        SettingStatus.Unchanged => new Markup("[green]Déjà correct[/]"),
        SettingStatus.Declined => new Markup("[grey]Non retenu[/]"),
        SettingStatus.Simulated => new Markup("[yellow]Serait modifié[/]"),
        SettingStatus.Failed => new Markup($"[red]Échec : {Markup.Escape(r.Error ?? "")}[/]"),
        _ => new Text("")
    };
}
