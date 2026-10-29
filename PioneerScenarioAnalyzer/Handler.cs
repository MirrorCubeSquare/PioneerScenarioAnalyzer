using static PioneerScenarioAnalyzer.i18n.Game;

namespace PioneerScenarioAnalyzer;

internal static class Handler
{
    internal static PioneerTrainingStats[] CreateTrainingStats(TurnInfoPioneer turn)
    {
        var data = turn.GetCommonResponse();
        var currentStats = turn.Stats;
        return [.. TurnInfoPioneer.TrainIds.Select(trainId =>
        {
            var commands = data.home_info.command_info_array.Where(command =>
                TurnInfoPioneer.ToTrainId.TryGetValue(command.command_id, out var id) && id == trainId).ToArray();
            var values = new int[31];
            foreach (var command in commands)
                foreach (var parameter in command.params_inc_dec_info_array)
                    if (parameter.target_type is >= 1 and <= 5 or 10 or 30)
                        values[parameter.target_type] += parameter.value;

            var scenarioCommand = data.pioneer_data_set.command_info_array.Single(command =>
                TurnInfoPioneer.ToTrainId.TryGetValue(command.command_id, out var id) && id == trainId);
            foreach (var parameter in scenarioCommand.params_inc_dec_info_array)
                if (parameter.target_type is >= 1 and <= 5 or 30)
                    values[parameter.target_type] += parameter.value;

            return new PioneerTrainingStats(
                [.. currentStats.Select((value, index) =>
                    TurnInfo.ReviseOver1200(value + values[index + 1]) - TurnInfo.ReviseOver1200(value))],
                values[30], Math.Clamp(values[10], -turn.Vital, turn.MaxVital - turn.Vital),
                commands[0].failure_rate);
        })];
    }

    internal static PioneerTrainingDisplayBuilder CreateDisplay(TurnInfoPioneer turn, IReadOnlyList<PioneerTrainingStats> stats)
    {
        var data = turn.GetCommonResponse();
        var motivation = data.chara_info.motivation switch
        {
            5 => I18N_MotivationBest,
            4 => I18N_MotivationGood,
            3 => I18N_MotivationNormal,
            2 => I18N_MotivationBad,
            1 => I18N_MotivationWorst,
            _ => throw new InvalidDataException($"未知干劲值: {data.chara_info.motivation}")
        };
        var builder = new PioneerTrainingDisplayBuilder();
        builder.HeaderPanels.Add(new("date", 4, PioneerDisplayLine.Plain($"{turn.Year}{I18N_Year} {turn.Month}{I18N_Month}{turn.HalfMonth}")));
        builder.HeaderPanels.Add(new("total", 6, PioneerDisplayLine.Colored($"总属性: {turn.StatsRevised.Sum()}", PioneerDisplayColor.Cyan)));
        builder.HeaderPanels.Add(new("vital", 6, PioneerDisplayLine.Styled(
            new($"{I18N_Vital}: "), new($"{turn.Vital}", PioneerDisplayColor.Green), new($"/{turn.MaxVital}"))));
        builder.HeaderPanels.Add(new("motivation", 3, PioneerDisplayLine.Colored(motivation,
            data.chara_info.motivation switch { 5 => PioneerDisplayColor.Green, 4 => PioneerDisplayColor.Yellow, _ => PioneerDisplayColor.Red })));
        if (data.home_info.command_info_array.Count(command => command.is_enable == 1) <= 1)
            builder.ImportantRows.Add(PioneerDisplayLine.Colored("非训练回合", PioneerDisplayColor.Aqua));
        builder.ScenarioPanels.Add(new("cycle", 3, PioneerDisplayLine.Plain($"评价会回合周期 {(turn.Turn - 1) % 6 + 1}/6")));

        var island = data.home_info.command_info_array.FirstOrDefault(command => command.command_id == 3101);
        var scenarioIsland = data.pioneer_data_set.command_info_array.FirstOrDefault(command => command.command_id == 3101);
        if (island is not null && scenarioIsland is not null)
        {
            var islandValues = island.params_inc_dec_info_array.Concat(scenarioIsland.params_inc_dec_info_array).ToArray();
            builder.ScenarioPanels.Add(new("island-gain", 3, PioneerDisplayLine.Plain(
                $"属性: {islandValues.Where(value => value.target_type is >= 1 and <= 5).Sum(value => value.value)} | Pt: {islandValues.Where(value => value.target_type == 30).Sum(value => value.value)}")));
            var supportCount = island.training_partner_array.Count(position => position is >= 1 and <= 6);
            builder.ScenarioPanels.Add(new("island-partners", 6, PioneerDisplayLine.Styled(
                new("支援卡 "), new($"{supportCount}", supportCount == 6 ? PioneerDisplayColor.Green : PioneerDisplayColor.Red),
                new($" 人 | NPC {island.training_partner_array.Length - supportCount} 人"))));
        }
        else
        {
            builder.ScenarioPanels.Add(new("island-gain", 3, PioneerDisplayLine.Plain("无法上岛")));
            builder.ScenarioPanels.Add(new("island-partners", 6, PioneerDisplayLine.Plain("无法上岛")));
        }

        var bestScore = stats.Max(stat => stat.FiveValueGain.Sum());
        foreach (var command in turn.CommandInfoArray)
        {
            var stat = stats[command.TrainIndex - 1];
            var title = command.TrainIndex switch
            {
                1 => I18N_Speed,
                2 => I18N_Stamina,
                3 => I18N_Power,
                4 => I18N_Nuts,
                5 => I18N_Wiz,
                _ => throw new InvalidDataException($"未知训练索引: {command.TrainIndex}")
            };
            var card = new PioneerTrainingCard(command.CommandId, command.TrainIndex, title);
            if (stat.FailureRate > 0)
                card.StyledTitle = PioneerDisplayLine.Styled(new(title), new($"({stat.FailureRate}%)", stat.FailureRate switch
                {
                    >= 40 => PioneerDisplayColor.Red,
                    >= 20 => PioneerDisplayColor.DarkOrange,
                    _ => PioneerDisplayColor.Yellow
                }));
            var currentStat = turn.StatsRevised[command.TrainIndex - 1];
            var remainingStat = turn.MaxStatsRevised[command.TrainIndex - 1] - currentStat;
            card.Rows.Add(I18N_CurrentRemainStat);
            card.Rows.Add(PioneerDisplayLine.Styled(new($"{currentStat}:"), new($"{remainingStat}", remainingStat switch
            {
                > 400 => PioneerDisplayColor.Normal,
                > 200 => PioneerDisplayColor.Yellow,
                _ => PioneerDisplayColor.Red
            })));
            card.Rows.Add(PioneerDisplayLine.Rule);
            var afterVital = stat.VitalGain + turn.Vital;
            card.Rows.Add(PioneerDisplayLine.Styled(new($"{I18N_Vital}:"), new($"{afterVital}", afterVital switch
            {
                < 30 => PioneerDisplayColor.Red,
                < 50 => PioneerDisplayColor.DarkOrange,
                < 70 => PioneerDisplayColor.Yellow,
                _ => PioneerDisplayColor.Green
            }), new($"/{turn.MaxVital}")));
            turn.PointGainInfoDictionary.TryGetValue(command.CommandId, out var gain);
            card.Rows.Add($"Lv{command.TrainLevel} | {gain}");
            card.Rows.Add(PioneerDisplayLine.Rule);
            var score = stat.FiveValueGain.Sum();
            card.Rows.Add(PioneerDisplayLine.Styled(new($"{I18N_StatSimple}:"),
                new($"{score}", score == bestScore ? PioneerDisplayColor.Aqua : PioneerDisplayColor.Normal), new($"|Pt:{stat.PtGain}")));
            card.Rows.AddRange(command.TrainingPartners.Select(partner => partner.DisplayLine));
            card.Highlighted = command.TrainingPartners.Any(partner => partner.Shining);
            for (var i = command.TrainingPartners.Count(); i < 5; i++)
                card.Rows.Add(string.Empty);
            card.Rows.Add(PioneerDisplayLine.Rule);
            builder.TrainingCards.Add(card);
        }
        return builder;
    }
}
