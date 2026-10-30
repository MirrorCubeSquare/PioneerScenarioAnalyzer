using System.IO.Compression;
using System.Text;
using Gallop;
using Gallop.Endpoints;
using Newtonsoft.Json;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Entities;
using UmamusumeResponseAnalyzer.TerminalGui;
using HostSkillData = UmamusumeResponseAnalyzer.Entities.SkillData;

static partial class PluginCases
{
    static readonly int[] BaseTrainingIds = [101, 105, 102, 103, 106];

    public static PluginCase Case { get; } =
        new(
            "PioneerScenarioAnalyzer",
            static () => new PioneerScenarioAnalyzer.PioneerScenarioAnalyzer(),
            new(
                static () => CreatePioneerTrainingResponse(),
                "PioneerScenarioAnalyzer",
                "training",
                "训练分析",
                FullBleed: true,
                SwitchesOnUpdate: true,
                VisibleText: "评价会回合周期",
                CreateUpdatedResponse: static () => CreatePioneerTrainingResponse(73),
                InitialUpdateText: "80/100",
                UpdatedVisibleText: "73/100",
                History: new(
                    static (vital, charaId, turn) => CreatePioneerTrainingResponse(vital, charaId, turn))));

    static SingleModePioneerCheckEventResponse CreatePioneerTrainingResponse(
        int vital = 80,
        int singleModeCharaId = 1001,
        int turn = 1) => new()
    {
        data = new()
        {
            chara_info = CreateChara(
                11,
                vital: vital,
                singleModeCharaId: singleModeCharaId,
                turn: turn),
            home_info = CreateHomeInfo(),
            unchecked_event_array = [],
            race_condition_array = [],
            select_index_info_array = [],
            pioneer_data_set = new()
            {
                command_info_array =
                [
                    .. BaseTrainingIds.Select((commandId, index) => new SingleModePioneerCommandInfo
                    {
                        command_type = 1,
                        command_id = commandId,
                        params_inc_dec_info_array = CreateParams(index + 1)
                    })
                ],
                pioneer_point_gain_info_array =
                [
                    .. BaseTrainingIds.Select((commandId, index) => new SingleModePioneerPointGainInfo
                    {
                        command_type = 1,
                        command_id = commandId,
                        gain_num = index + 1
                    })
                ]
            }
        }
    };

    static SingleModeChara CreateChara(
        int scenarioId,
        int[]? trainIds = null,
        int vital = 80,
        int singleModeCharaId = 1001,
        int turn = 1) => new()
    {
        single_mode_chara_id = singleModeCharaId,
        card_id = 100100,
        state = 1,
        playing_state = 1,
        scenario_id = scenarioId,
        turn = turn,
        motivation = 5,
        vital = vital,
        max_vital = 100,
        speed = 500,
        stamina = 500,
        power = 500,
        guts = 500,
        wiz = 500,
        max_speed = 1200,
        max_stamina = 1200,
        max_power = 1200,
        max_guts = 1200,
        max_wiz = 1200,
        skill_point = 1200,
        skill_array = [],
        skill_tips_array = [],
        support_card_array = [],
        evaluation_info_array = [],
        training_level_info_array =
        [
            .. (trainIds ?? BaseTrainingIds).Select(commandId => new TrainingLevelInfo
            {
                command_id = commandId,
                level = 1
            })
        ],
        chara_effect_id_array = [],
        proper_distance_short = 1,
        proper_distance_mile = 1,
        proper_distance_middle = 1,
        proper_distance_long = 1,
        proper_running_style_nige = 1,
        proper_running_style_senko = 1,
        proper_running_style_sashi = 1,
        proper_running_style_oikomi = 1,
        proper_ground_turf = 1,
        proper_ground_dirt = 1
    };

    static SingleModeHomeInfo CreateHomeInfo(int[]? trainIds = null) => new()
    {
        command_info_array =
        [
            .. (trainIds ?? BaseTrainingIds).Select((commandId, index) => new SingleModeCommandInfo
            {
                command_type = 1,
                command_id = commandId,
                is_enable = 1,
                failure_rate = index * 5,
                training_partner_array = [],
                tips_event_partner_array = [],
                sub_command_partner_array = [],
                params_inc_dec_info_array = CreateParams(index + 1)
            })
        ]
    };

    static SingleModeParamsIncDecInfo[] CreateParams(int value) =>
    [
        new() { target_type = 1, value = value },
        new() { target_type = 2, value = value },
        new() { target_type = 3, value = value },
        new() { target_type = 4, value = value },
        new() { target_type = 5, value = value },
        new() { target_type = 30, value = value },
        new() { target_type = 10, value = -10 }
    ];    static void InitializeDatabase(
        string fixtureName,
        IReadOnlyList<HostSkillData> skills,
        IReadOnlyList<BaseName> names,
        IReadOnlyDictionary<int, string> factorIds,
        IReadOnlyList<int> saddleIds)
    {
        WriteBrotliJson(Database.EVENT_NAME_FILEPATH, new List<Story>());
        WriteBrotliJson(Database.NAMES_FILEPATH, names.ToList(), new() { TypeNameHandling = TypeNameHandling.All });
        WriteBrotliJson(Database.SKILLS_FILEPATH, skills);
        WriteBrotliJson(Database.SKILL_UPGRADE_SPECIALITY_FILEPATH, new List<SkillUpgradeSpeciality>());
        WriteBrotliJson(Database.TALENT_SKILLS_FILEPATH, new Dictionary<int, TalentSkillData[]>());
        WriteBrotliJson(Database.FACTOR_IDS_FILEPATH, factorIds);
        WriteBrotliJson(Database.SADDLE_IDS_FILEPATH, saddleIds);
        WriteBrotliJson(Database.SUCCESSION_RELATION_FILEPATH, new SuccessionRelationTable());

        if (Database.Initialize().GetAwaiter().GetResult() != DatabaseAvailability.Ready)
            throw new InvalidOperationException($"{fixtureName} database fixture did not load atomically.");
    }

    static void WriteBrotliJson<T>(string path, T value, JsonSerializerSettings? settings = null)
    {
        using var file = File.Create(path);
        using var brotli = new BrotliStream(file, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(brotli, Encoding.UTF8);
        using var json = new JsonTextWriter(writer);
        JsonSerializer.CreateDefault(settings).Serialize(json, value);
    }

}
