using Gallop;
using System.IO.Compression;
using Newtonsoft.Json;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Entities;

static class Fixtures
{
    internal static readonly int[] TrainingIds = [101, 105, 102, 103, 106];

    internal static SingleModePioneerCheckEventResponse Dashboard()
    {
        Write(Database.EVENT_NAME_FILEPATH, new List<Story>());
        Write(Database.NAMES_FILEPATH, new List<BaseName>
        {
            new BaseName(101, "理事长", "理事长"),
            new BaseName(1001, "气槽", "气槽"),
            new BaseName(1002, "玉藻", "玉藻"),
            new BaseName(1003, "理事", "理事"),
            new SupportCardName(30001, "气槽", "气槽", 101, 1001),
            new SupportCardName(30002, "玉藻", "玉藻", 102, 1002),
            new SupportCardName(30003, "理事", "理事", 0, 1003)
        }, new() { TypeNameHandling = TypeNameHandling.All });
        Write(Database.SKILLS_FILEPATH, new List<UmamusumeResponseAnalyzer.Entities.SkillData>());
        Write(Database.SKILL_UPGRADE_SPECIALITY_FILEPATH, new List<SkillUpgradeSpeciality>());
        Write(Database.TALENT_SKILLS_FILEPATH, new Dictionary<int, TalentSkillData[]>());
        Write(Database.FACTOR_IDS_FILEPATH, new Dictionary<int, string>());
        Write(Database.SADDLE_IDS_FILEPATH, new List<int>());
        Write(Database.SUCCESSION_RELATION_FILEPATH, new SuccessionRelationTable());
        if (Database.Initialize().GetAwaiter().GetResult() != DatabaseAvailability.Ready)
            throw new InvalidOperationException("Pioneer dashboard name fixture did not load.");

        var response = Response(vital: 30);
        var chara = response.data.chara_info;
        chara.support_card_array =
        [
            new() { position = 1, support_card_id = 30001 },
            new() { position = 2, support_card_id = 30002 },
            new() { position = 3, support_card_id = 30003 }
        ];
        chara.evaluation_info_array =
        [
            new() { target_id = 1, evaluation = 90 },
            new() { target_id = 2, evaluation = 35 },
            new() { target_id = 3, evaluation = 60 },
            new() { target_id = 101, evaluation = 45 }
        ];
        var commands = response.data.home_info.command_info_array;
        commands[0].training_partner_array = [1, 3, 101];
        commands[0].tips_event_partner_array = [1];
        commands[1].failure_rate = 5;
        commands[2].failure_rate = 20;
        commands[2].training_partner_array = [2];
        commands[3].failure_rate = 40;
        commands[4].failure_rate = 0;
        return response;
    }

    static void Write<T>(string path, T value, JsonSerializerSettings? settings = null)
    {
        using var file = File.Create(path);
        using var brotli = new BrotliStream(file, CompressionLevel.Optimal);
        using var writer = new StreamWriter(brotli);
        writer.Write(JsonConvert.SerializeObject(value, settings));
    }

    internal static SingleModePioneerCheckEventResponse Response(int turn = 1, int vital = 80, int charaId = 1001, bool summer = false)
    {
        var ids = summer ? new[] { 3601, 3602, 3603, 3604, 3605 } : TrainingIds;
        return new()
        {
            data = new()
            {
                chara_info = new()
                {
                    single_mode_chara_id = charaId,
                    card_id = 100100,
                    scenario_id = 11,
                    state = 1,
                    playing_state = 1,
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
                    chara_effect_id_array = [],
                    training_level_info_array = [.. TrainingIds.Select(id => new TrainingLevelInfo { command_id = id, level = 1 })]
                },
                home_info = new()
                {
                    command_info_array = [.. ids.Select((id, index) => new SingleModeCommandInfo
                    {
                        command_id = id,
                        command_type = 1,
                        is_enable = 1,
                        failure_rate = index * 5,
                        training_partner_array = [],
                        tips_event_partner_array = [],
                        sub_command_partner_array = [],
                        params_inc_dec_info_array = Parameters(index + 1)
                    })]
                },
                unchecked_event_array = [],
                race_condition_array = [],
                select_index_info_array = [],
                pioneer_data_set = new()
                {
                    command_info_array = [.. ids.Select((id, index) => new SingleModePioneerCommandInfo
                    {
                        command_id = id,
                        command_type = 1,
                        params_inc_dec_info_array = Parameters(index + 1, includeVital: false)
                    })],
                    pioneer_point_gain_info_array = [.. ids.Select((id, index) => new SingleModePioneerPointGainInfo
                    {
                        command_id = id,
                        command_type = 1,
                        gain_num = index + 1
                    })]
                }
            }
        };
    }

    internal static SingleModePioneerLoadResponse Load(int turn = 1, int vital = 80)
    {
        var data = Response(turn, vital).data;
        return new()
        {
            data = new()
            {
                pioneer_data_set = data.pioneer_data_set,
                single_mode_load_common = new()
                {
                    chara_info = data.chara_info,
                    home_info = data.home_info,
                    unchecked_event_array = []
                }
            }
        };
    }

    internal static SingleModeParamsIncDecInfo[] Parameters(int value, bool includeVital = true)
        => [.. new[] { 1, 2, 3, 4, 5, 30 }.Select(type => new SingleModeParamsIncDecInfo { target_type = type, value = value }),
            .. includeVital ? new[] { new SingleModeParamsIncDecInfo { target_type = 10, value = -10 } } : []];
}
