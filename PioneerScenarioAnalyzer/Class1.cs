using Gallop;
using UmamusumeResponseAnalyzer.Plugin;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace PioneerScenarioAnalyzer;

public sealed class PioneerScenarioAnalyzer : IPlugin
{
    const string WorkspaceTitle = "PioneerScenarioAnalyzer";
    const string TrainingPanelKey = "training";
    const string CommonResponseEndpoints =
        "^/umamusume/single_mode_pioneer/(?:check_event|continue|exec_command|gain_skills|pioneer_live|planning|race_end|race_entry|race_out|shima_training_exec)$";

    readonly object gate = new();
    readonly PioneerDisplayHistory history = new(WorkspaceTitle, TrainingPanelKey, "训练分析");
    IPluginContext? pluginContext;
    Workspace? workspace;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (gate)
        {
            if (pluginContext is not null)
                throw new InvalidOperationException("PioneerScenarioAnalyzer 已初始化。");
            history.Initialize(
                context.Application,
                key => PioneerTrainingDisplay.Show(new(key.SingleModeCharaId, key.Turn)),
                key => PioneerTrainingDisplay.Remove(this, new(key.SingleModeCharaId, key.Turn)));
            pluginContext = context;
        }

        context.Analyzers.Register<SingleModePioneerCheckEventResponse>(
            AnalyzerKind.Response,
            [EndpointPattern.Regex(CommonResponseEndpoints)],
            invocation => Analyze(invocation.Payload),
            priority: 1);
        context.Analyzers.Register<SingleModePioneerLoadResponse>(
            AnalyzerKind.Response,
            [EndpointPattern.Exact("/umamusume/single_mode_pioneer/load")],
            invocation => Analyze(invocation.Payload),
            priority: 1);
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            pluginContext = null;
            history.Stop();
            PioneerTrainingDisplay.Clear(this);
            workspace?.RemovePanel(TrainingPanelKey);
            workspace = null;
        }
        return ValueTask.CompletedTask;
    }

    public Task ConfigPromptAsync(
        Terminal.Gui.App.IApplication application,
        CancellationToken cancellationToken = default)
        => history.ConfigPromptAsync(application, cancellationToken);

    public ValueTask Analyze(SingleModePioneerCheckEventResponse response)
        => AnalyzeResponse(response, response.data.chara_info, response.data.home_info,
            response.data.pioneer_data_set, response.data.unchecked_event_array,
            response.data.select_index_info_array, response.data.race_start_info);

    public ValueTask Analyze(SingleModePioneerLoadResponse response)
    {
        var common = response.data.single_mode_load_common;
        return AnalyzeResponse(response, common.chara_info, common.home_info,
            response.data.pioneer_data_set, common.unchecked_event_array, null, common.race_start_info);
    }

    ValueTask AnalyzeResponse(
        object response,
        SingleModeChara? chara,
        SingleModeHomeInfo? home,
        SingleModePioneerDataSet? dataSet,
        SingleModeEventInfo[]? events,
        SingleModeSelectIndexInfo[]? selections,
        SingleRaceStartInfo? race)
    {
        if (chara is not { scenario_id: 11, playing_state: 1 } || chara.state is 2 or 3 ||
            events is { Length: > 0 } || race is not null ||
            home?.command_info_array is not { } commands ||
            dataSet?.command_info_array is not { } scenarioCommands)
            return ValueTask.CompletedTask;

        // 部分响应只有状态更新；五项训练齐全后才发布完整面板。
        if (!TurnInfoPioneer.TrainIds.All(trainId =>
                commands.Any(command => TurnInfoPioneer.ToTrainId.TryGetValue(command.command_id, out var id) && id == trainId) &&
                scenarioCommands.Any(command => TurnInfoPioneer.ToTrainId.TryGetValue(command.command_id, out var id) && id == trainId)))
            return ValueTask.CompletedTask;

        lock (gate)
        {
            if (pluginContext is null)
                return ValueTask.CompletedTask;

            var data = new PioneerScenarioResponseData(chara, home, dataSet, events, selections, race);
            var turn = new TurnInfoPioneer(SingleModeTurnData.From(data));
            var stats = Handler.CreateTrainingStats(turn);
            var builder = Handler.CreateDisplay(turn, stats);

            var target = workspace ??= Workspace.Create(WorkspaceTitle);
            var id = new PioneerTrainingDisplayId(chara.single_mode_chara_id, chara.turn);
            var key = new PioneerDisplayHistory.Key(id.SingleModeCharaId, id.Turn);
            PioneerTrainingDisplay.Update(this, id, new(response, data), builder,
                (_, content, switchToWorkspace) =>
                {
                    lock (gate)
                    {
                        if (pluginContext is not null)
                            history.Show(target, key, content, switchToWorkspace);
                    }
                });
            if (history.ShouldShow(key))
                PioneerTrainingDisplay.Show(id, switchToWorkspace: true);
            else
                history.Track(target, key);
            history.RecordAnalysis(key);
        }
        return ValueTask.CompletedTask;
    }
}
