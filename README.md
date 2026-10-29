# PioneerScenarioAnalyzer

解析野人杯回合信息，并在插件 workspace 中保存当前进程的训练分析 history。

面板顶部横排日期、总属性、体力和干劲四个状态框，下方为重要信息、三块剧本信息与横向五项训练表；Extras 位于右侧。失败率、体力、属性上限余量和友情训练使用颜色标记。窄窗口可横向、纵向滚动。每次实际发布分析结果时切换至本插件 workspace。

解析覆盖 `single_mode_pioneer` 的 `load`、`check_event`、`continue`、`exec_command`、`gain_skills`、`pioneer_live`、`planning`、`race_end`、`race_entry`、`race_out` 和 `shima_training_exec`。响应处于训练状态、没有待处理事件或比赛，且包含完整的五项基础训练及剧本训练数据时才更新面板。

`EventLoggerPlugin` 通过 Display producer 注入连续事件、训练失败、事件数、继承属性与技能点、胜场统计。Pioneer 独立解析和显示训练，不引用 EventLogger。

History 以 `(single_mode_chara_id, turn)` 为键；同键输出原位更新，不会因其它插件修改分析结果而新增记录。History 内容不跨进程保存。

`PluginData/PioneerScenarioAnalyzer/settings.json` 中的 `historyLimit` 配置最多保留的记录数，默认 `100`，有效范围 `0-1000`。设为 `0` 时仅显示最近一次分析，不保留 history。

History 启用时，焦点位于训练分析内容内可使用：

- `↑`：上一条（更旧）
- `↓`：下一条（更新）
- `←`：最旧一条
- `→`：最新一条

正文滚动使用 `PageUp`、`PageDown`、`Home`、`End` 或鼠标滚轮。

## Display 扩展

消费者引用本项目，在自身 manifest `Dependencies` 中声明 `PioneerScenarioAnalyzer`，并在 `IPluginContext.IsPluginAvailable("PioneerScenarioAnalyzer")` 检查后的延迟桥接中调用扩展接口。

```csharp
using PioneerScenarioAnalyzer;

using var producer = PioneerTrainingDisplay.RegisterPartProducer("MyPlugin");
var id = new PioneerTrainingDisplayId(singleModeCharaId, turn);
producer.Update(id, (_, display) =>
{
    display.Training.Modify(PioneerTrain.Speed, training =>
    {
        training.AddText("优先训练");
        training.Highlight();
    });
    display.Extra.AddText("补充分析");
});
PioneerTrainingDisplay.Show(id, switchToWorkspace: false);
```

`Update` 只替换该 producer 在指定 ID 下的内容；`Show` 按注册顺序组合并发布。同一 ID 原位更新，不增加 history 或未读。`Important`、`Scenario`、`Extra` 支持 `AddText` 和 `AddStyled(PioneerDisplaySegment[])`，`Training` 支持按训练种类或 command ID 修改标题、追加文本和标记。Extra 按 producer 来源标题分组。

注册、Update 和 Dispose 不自动发布；ID 尚无场景输出或操作已取消时，`Show` 返回 `false`。producer 抛出异常时保留最后一次成功显示。history 淘汰时释放对应 ID，插件释放时清空自身显示数据。

## 构建

```powershell
dotnet build .\PioneerScenarioAnalyzer\PioneerScenarioAnalyzer.csproj -c Release -m:1 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PlatformTarget=AnyCPU -p:DeployUraPluginToLocalAppDataOnBuild=false
```

## 验证与发布

专属 smoke 使用当前 Host 源码运行。`UraTestHostRoot` 指向 Host 仓库根目录：

```powershell
dotnet run --project .\tests\PioneerScenarioAnalyzerSmoke -p:UraTestHostRoot=K:\repos\UmamusumeResponseAnalyzer -p:GenerateUraPluginManifestOnBuild=false -p:PackageUraPluginOnBuild=false -p:DeployUraPluginToLocalAppDataOnBuild=false
```

覆盖端点分发、独立加载、训练数值、Display 组合、面板布局与颜色、窗口缩放、滚动与 history。EventLogger 联动由 Host 的 `PioneerEventLoggerSmoke` 使用双方实际注册的请求和响应处理器验证。

共用 workflow 从已解析 Host NuGet 包的源码提交构建测试 Host；该提交需要包含 `PioneerScenarioAnalyzerSmoke` 的 `InternalsVisibleTo` 声明。发布顺序为包含此测试声明的 Host 包、Pioneer 插件。

在 Windows 仓库根执行 `act workflow_dispatch --artifact-server-path "$env:TEMP/ura-act-artifacts"`。本地与 GitHub 使用同一份 workflow；版本 tag 触发 GitHub Release 发布。环境要求、共用 workflow 本地映射和发布规则见 [URA plugin workflows](https://github.com/URA-Plugins/.github/blob/v1/README.md)。
