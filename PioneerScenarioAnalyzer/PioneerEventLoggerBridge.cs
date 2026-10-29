using System.Runtime.CompilerServices;
using EventLoggerPlugin;
using static PioneerScenarioAnalyzer.i18n.Game;

namespace PioneerScenarioAnalyzer;

internal static class PioneerEventLoggerBridge
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Apply(
        PioneerScenarioResponseData data,
        IReadOnlyList<PioneerTrainingStats> trainStats,
        PioneerTrainingDisplayBuilder builder,
        bool newTraining)
    {
        var chara = data.CharaInfo;
        var turn = chara.turn;
        var snapshot = new EventLoggerSnapshot(
            chara,
            data.UncheckedEvents,
            data.SelectIndices,
            data.HomeInfo);
        var round = EventLogger.Current;
        var wrongTurn = turn != 1 && round.CurrentTurn != turn - 1 && round.CurrentTurn != turn;
        if (wrongTurn)
            builder.ImportantRows.Add(PioneerDisplayLine.Colored(string.Format(I18N_WrongTurnAlert, round.CurrentTurn, turn), PioneerDisplayColor.Red));
        if (newTraining || round.Scenario != chara.scenario_id || wrongTurn || turn == 1 && round.CurrentTurn != 1)
        {
            EventLogger.ResetSession(snapshot, isFullGame: turn == 1);
            round = EventLogger.Current;
        }

        if (chara.playing_state != 1)
        {
            builder.ImportantRows.Add(PioneerDisplayLine.Colored(I18N_RepeatTurn, PioneerDisplayColor.Yellow));
        }
        else
        {
            // BeginScenarioTurn replaces the turn, so retain its recorded choice before refreshing events.
            var turnStats = round.NewTurnBuilder(turn);
            EventLogger.BeginScenarioTurn(snapshot, chara.scenario_id, turn);
            round = EventLogger.Current;
            turnStats.isTraining = true;
            turnStats.motivation = chara.motivation;
            turnStats.fiveTrainStats = [.. trainStats.Select(stats => new TrainStats
            {
                FiveValueGain = [.. stats.FiveValueGain],
                PtGain = stats.PtGain,
                VitalGain = stats.VitalGain,
                FailureRate = stats.FailureRate
            })];

            for (var i = 0; i < 5; i++)
            {
                if (turn == 1)
                {
                    turnStats.trainLevel[i] = 1;
                    turnStats.trainLevelCount[i] = 0;
                    continue;
                }

                var trainId = TurnInfoPioneer.TrainIds[i];
                var previous = round.Turns[turn - 1];
                var level = previous?.TrainLevel[i] ?? 1;
                var count = previous?.TrainLevelCount[i] ?? 0;
                if (previous is not null &&
                    TurnInfoPioneer.ToTrainId.TryGetValue(previous.PlayerChoice, out var previousTrain) &&
                    previousTrain == trainId &&
                    !previous.IsTrainingFailed &&
                    turn - 1 is not (>= 37 and <= 40 or >= 61 and <= 64))
                {
                    count++;
                }
                if (turn is 25 or 37 or 49)
                    count += 4;
                level += count / 4;
                count %= 4;
                if (level >= 5)
                {
                    level = 5;
                    count = 0;
                }

                var actualLevel = chara.training_level_info_array.First(training =>
                    TurnInfoPioneer.ToTrainId.TryGetValue(training.command_id, out var id) && id == trainId).level;
                if (level != actualLevel)
                {
                    builder.ImportantRows.Add(PioneerDisplayLine.Colored(
                        $"警告：训练等级预测错误，预测{trainId}为lv{level}(+{count})，实际为lv{actualLevel}", PioneerDisplayColor.Red));
                    level = actualLevel;
                    count = 0;
                }
                turnStats.trainLevel[i] = level;
                turnStats.trainLevelCount[i] = count;
            }

            EventLogger.CommitScenarioTurn(chara.scenario_id, turn, turnStats);
        }

        builder.ExtraRows.AddRange(EventLogger.PrintCardEventPerf(chara.scenario_id));
    }
}
