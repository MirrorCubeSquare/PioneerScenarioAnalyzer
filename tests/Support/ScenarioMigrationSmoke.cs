using UmamusumeResponseAnalyzer.TerminalGui;

static class ScenarioMigrationSmoke
{
    public static void Run(WorkspaceSmokeSession ui)
    {
        using (var directory = new TempCurrentDirectory("PioneerScenarioAnalyzer-unused"))
        using (var context = new RuntimePluginContext(ui.Application))
        {
            ui.Bootstrap.SwitchTo();
            var unused = new PioneerScenarioAnalyzer.PioneerScenarioAnalyzer();
            try
            {
                unused.Initialize(context);
                ui.Flush();
                if (!ReferenceEquals(Workspace.Current, ui.Bootstrap))
                    throw new InvalidOperationException("Initialize changed Workspace.Current.");
            }
            finally { unused.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            ui.Flush();
            if (!ReferenceEquals(Workspace.Current, ui.Bootstrap))
                throw new InvalidOperationException("Unused Dispose changed Workspace.Current.");
        }
        PluginCases.Case.Run(ui);
        PluginCases.VerifyLocalizedTraining(PluginCases.Case, ui);
        Console.WriteLine("PASS PioneerScenarioAnalyzer: migrated lifecycle and history checks");
    }
}
