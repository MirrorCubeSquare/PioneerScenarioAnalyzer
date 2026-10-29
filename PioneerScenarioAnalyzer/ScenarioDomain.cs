using System.Collections.Frozen;
using Gallop;
using UmamusumeResponseAnalyzer;

namespace PioneerScenarioAnalyzer;

public sealed record PioneerScenarioResponseData(
    SingleModeChara CharaInfo,
    SingleModeHomeInfo HomeInfo,
    SingleModePioneerDataSet DataSet,
    SingleModeEventInfo[]? UncheckedEvents,
    SingleModeSelectIndexInfo[]? SelectIndices,
    SingleRaceStartInfo? RaceStartInfo);

internal sealed record PioneerTrainingStats(int[] FiveValueGain, int PtGain, int VitalGain, int FailureRate);

internal sealed class SingleModeTurnData(
    SingleModeChara charaInfo,
    SingleModeHomeInfo homeInfo,
    SingleModePioneerDataSet pioneerDataSet)
{
    public SingleModeChara chara_info { get; } = charaInfo;
    public SingleModeHomeInfo home_info { get; } = homeInfo;
    public SingleModePioneerDataSet pioneer_data_set { get; } = pioneerDataSet;

    public static SingleModeTurnData From(PioneerScenarioResponseData response)
        => new(response.CharaInfo, response.HomeInfo, response.DataSet);
}

internal class TurnInfo(SingleModeTurnData resp)
{
    SingleModeChara CharaInfo => resp.chara_info;

    public int SpeedRevised => ReviseOver1200(CharaInfo.speed);
    public int StaminaRevised => ReviseOver1200(CharaInfo.stamina);
    public int PowerRevised => ReviseOver1200(CharaInfo.power);
    public int GutsRevised => ReviseOver1200(CharaInfo.guts);
    public int WizRevised => ReviseOver1200(CharaInfo.wiz);
    public int[] Stats => [CharaInfo.speed, CharaInfo.stamina, CharaInfo.power, CharaInfo.guts, CharaInfo.wiz];
    public int[] StatsRevised => [SpeedRevised, StaminaRevised, PowerRevised, GutsRevised, WizRevised];
    public int[] MaxStatsRevised =>
    [
        ReviseOver1200(CharaInfo.max_speed),
        ReviseOver1200(CharaInfo.max_stamina),
        ReviseOver1200(CharaInfo.max_power),
        ReviseOver1200(CharaInfo.max_guts),
        ReviseOver1200(CharaInfo.max_wiz)
    ];
    public int Turn => CharaInfo.turn;
    public int Year => (Turn - 1) / 24 + 1;
    public int Vital => CharaInfo.vital;
    public int MaxVital => CharaInfo.max_vital;
    public FrozenDictionary<int, int> SupportCards => CharaInfo.support_card_array.ToDictionary(x => x.position, x => x.support_card_id).ToFrozenDictionary();
    public FrozenDictionary<int, EvaluationInfo> Evaluations => CharaInfo.evaluation_info_array.ToDictionary(x => x.target_id, x => x).ToFrozenDictionary();
    public int Month => ((Turn - 1) % 24) / 2 + 1;
    public string HalfMonth => Turn % 2 == 0 ? "后半" : "前半";
    public SingleModeTurnData GetCommonResponse() => resp;

    internal static int ReviseOver1200(int value) => value > 1200 ? value * 2 - 1200 : value;
}

internal sealed class TurnInfoPioneer(SingleModeTurnData resp) : TurnInfo(resp)
{
    public static readonly int[] TrainIds = [101, 105, 102, 103, 106];
    public static readonly FrozenDictionary<int, int> ToTrainId = new Dictionary<int, int>
    {
        [101] = 101, [105] = 105, [102] = 102, [103] = 103, [106] = 106,
        [3601] = 101, [3602] = 105, [3603] = 102, [3604] = 103, [3605] = 106
    }.ToFrozenDictionary();
    public static readonly FrozenDictionary<int, int> ToTrainIndex = new Dictionary<int, int>
    {
        [101] = 0, [105] = 1, [102] = 2, [103] = 3, [106] = 4,
        [3601] = 0, [3602] = 1, [3603] = 2, [3604] = 3, [3605] = 4
    }.ToFrozenDictionary();
    public List<CommandInfo> CommandInfoArray { get; } =
    [
        .. TrainIds.Select(trainId => new CommandInfo(resp,
            resp.pioneer_data_set.command_info_array.Single(command =>
                ToTrainId.TryGetValue(command.command_id, out var id) && id == trainId).command_id))
    ];

    public Dictionary<int, int> PointGainInfoDictionary { get; } =
        resp.pioneer_data_set.pioneer_point_gain_info_array.ToDictionary(x => x.command_id, x => x.gain_num);
}

internal sealed class CommandInfo
{
    public int CommandId { get; }
    public int TrainIndex { get; }
    public int TrainLevel { get; }
    public IEnumerable<TrainingPartner> TrainingPartners { get; }

    public CommandInfo(SingleModeTurnData resp, int commandId)
    {
        var turn = new TurnInfo(resp);
        CommandId = commandId;
        TrainIndex = TurnInfoPioneer.ToTrainIndex[commandId] + 1;
        var trainId = TurnInfoPioneer.ToTrainId[commandId];
        TrainLevel = resp.chara_info.training_level_info_array.First(x =>
            TurnInfoPioneer.ToTrainId.TryGetValue(x.command_id, out var id) && id == trainId).level;
        var normalCommand = resp.home_info.command_info_array.First(x =>
            TurnInfoPioneer.ToTrainId.TryGetValue(x.command_id, out var id) && id == trainId);
        TrainingPartners = normalCommand.training_partner_array
            .Select(x => new TrainingPartner(turn, x, normalCommand))
            .OrderBy(x => x.Priority)
            .ToArray();
    }
}

internal enum PartnerPriority
{
    友人 = 0,
    闪 = 1,
    羁绊不足 = 2,
    其他 = 3,
    关键NPC = 5,
    默认 = 7
}

internal sealed class TrainingPartner
{
    public PartnerPriority Priority { get; private set; } = PartnerPriority.默认;
    public int Position { get; }
    public int CardId { get; }
    public PioneerDisplayLine DisplayLine { get; }
    public int Friendship { get; }
    public bool IsNpc => Position is not (>= 1 and <= 6);
    public bool Shining { get; private set; }

    public TrainingPartner(TurnInfo turn, int partner, SingleModeCommandInfo command)
    {
        Position = partner;
        Friendship = turn.Evaluations.TryGetValue(Position, out var evaluation) ? evaluation.evaluation : 0;
        var segments = new List<PioneerDisplaySegment>();
        if (command.tips_event_partner_array.Contains(Position))
            segments.Add(new("!", PioneerDisplayColor.Red));

        if (!IsNpc)
        {
            CardId = turn.SupportCards[Position];
            var supportCard = Database.Names.GetRequiredSupportCard(CardId);
            var name = Database.Names.DisplayNickname(CardId);
            if (supportCard.IsFriendCard)
            {
                Priority = PartnerPriority.友人;
            }
            else if (Friendship < 80)
            {
                Priority = PartnerPriority.羁绊不足;
            }

            var trainId = TurnInfoPioneer.ToTrainId[command.command_id];
            Shining = Friendship >= 80 && supportCard.Type == trainId;

            if (Shining)
                Priority = supportCard.IsFriendCard ? PartnerPriority.友人 : PartnerPriority.闪;

            segments.Add(new($"{(Shining ? "★" : string.Empty)}{name}", Priority switch
            {
                PartnerPriority.友人 => PioneerDisplayColor.Green,
                PartnerPriority.闪 => PioneerDisplayColor.Aqua,
                PartnerPriority.羁绊不足 => PioneerDisplayColor.Yellow,
                _ => PioneerDisplayColor.Normal
            }));
        }
        else
        {
            Priority = Position is >= 100 and < 1000 ? PartnerPriority.关键NPC : PartnerPriority.默认;
            segments.Add(new(Database.Names.DisplayNickname(Position),
                Priority == PartnerPriority.关键NPC ? PioneerDisplayColor.Teal : PioneerDisplayColor.Normal));
        }

        if ((Position is >= 1 and <= 7 or >= 100 and < 1000) && Friendship < 100)
            segments.Add(new($" {Friendship}", PioneerDisplayColor.Red));
        DisplayLine = new(segments);
    }
}
