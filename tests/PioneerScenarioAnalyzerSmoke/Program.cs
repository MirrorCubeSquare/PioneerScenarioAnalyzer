using System.Globalization;
using System.Drawing;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Gallop;
using Gallop.Endpoints;
using MessagePack;
using PioneerScenarioAnalyzer;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.Plugin;
using UmamusumeResponseAnalyzer.TerminalGui;
using PioneerPlugin = PioneerScenarioAnalyzer.PioneerScenarioAnalyzer;
using TColor = Terminal.Gui.Drawing.Color;

CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
var capturePath = args.Length == 2 && args[0] == "--capture" ? Path.GetFullPath(args[1]) : null;
if (args.Length != 0 && capturePath is null)
    throw new ArgumentException("Usage: PioneerScenarioAnalyzerSmoke [--capture <framebuffer.json>]");
using var ui = new WorkspaceSmokeSession(120, 40);
Run("isolated loading without EventLogger", TestIsolatedLoading);
Run("endpoint projections and input boundaries", TestEndpoints);
Run("command IDs, summer training and island totals", TestTraining);
Run("original dashboard layout, colors and scrolling", TestOriginalDashboard);
Run("display producer composition", TestProducers);
Run("keyed history and configuration", TestHistory);
Console.WriteLine("PioneerScenarioAnalyzer smoke passed (6 phases).");

void Run(string name, Action test)
{
    Console.WriteLine($"Pioneer smoke: {name}");
    test();
    ui.Bootstrap.SwitchTo();
    ui.Flush();
}

void TestIsolatedLoading()
{
    using var directory = new TempCurrentDirectory("Pioneer-isolated");
    using var context = new RuntimePluginContext(ui.Application);
    var loadContext = new WithoutEventLoggerLoadContext();
    var assembly = loadContext.LoadFromAssemblyPath(typeof(PioneerPlugin).Assembly.Location);
    var plugin = (IPlugin)Activator.CreateInstance(assembly.GetType(typeof(PioneerPlugin).FullName!, throwOnError: true)!)!;
    try
    {
        plugin.Initialize(context);
        Require(ReferenceEquals(Workspace.Current, ui.Bootstrap), "Initialize must not switch workspace.");
        Dispatch(context, "check_event", Fixtures.Response());
        ScreenContains("80/100", "速度", "评价会回合周期", "无法上岛");
        Require(!loadContext.Assemblies.Any(item => item.GetName().Name == "EventLoggerPlugin"), "Optional EventLogger assembly was loaded.");
    }
    finally
    {
        plugin.DisposeAsync().AsTask().GetAwaiter().GetResult();
        loadContext.Unload();
    }
    Require(!ui.CaptureScreen().Contains("评价会回合周期", StringComparison.Ordinal), "Dispose left the panel visible.");
}

void TestEndpoints()
{
    using var directory = new TempCurrentDirectory("Pioneer-endpoints");
    using var context = new RuntimePluginContext(ui.Application);
    var plugin = new PioneerPlugin();
    plugin.Initialize(context);
    try
    {
        string[] paths = ["check_event", "continue", "exec_command", "gain_skills", "pioneer_live", "planning",
            "race_end", "race_entry", "race_out", "shima_training_exec"];
        for (var index = 0; index < paths.Length; index++)
        {
            Dispatch(context, paths[index], Fixtures.Response(vital: 40 + index));
            ScreenContains($"{40 + index}/100");
        }
        Dispatch(context, "load", Fixtures.Load(2, 72));
        ScreenContains("72/100");

        Action<SingleModePioneerCheckEventResponse>[] invalidInputs =
        [
            response => response.data.chara_info.scenario_id = 10,
            response => response.data.chara_info.state = 2,
            response => response.data.unchecked_event_array = [new() { story_id = 400010112 }],
            response => response.data.race_start_info = new(),
            response => response.data.home_info.command_info_array = response.data.home_info.command_info_array[..4],
            response => response.data.home_info = null!,
            response => response.data.pioneer_data_set = null!
        ];
        foreach (var invalidate in invalidInputs)
        {
            var response = Fixtures.Response(turn: 3, vital: 17);
            invalidate(response);
            Dispatch(context, "check_event", response);
            ScreenContains("72/100");
            Require(!PioneerTrainingDisplay.Show(new(1001, 3)), "Invalid response created a display unit.");
        }
    }
    finally { plugin.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    Require(!PioneerTrainingDisplay.Show(new(1001, 2)), "Dispose retained display units.");
}

void TestTraining()
{
    using var directory = new TempCurrentDirectory("Pioneer-training");
    using var context = new RuntimePluginContext(ui.Application);
    var plugin = new PioneerPlugin();
    plugin.Initialize(context);
    try
    {
        var response = Fixtures.Response();
        Array.Reverse(response.data.home_info.command_info_array);
        Array.Reverse(response.data.pioneer_data_set.command_info_array);
        Dispatch(context, "exec_command", response);
        var normal = ui.CaptureScreen();
        ScreenContains("速度", "耐力", "力量", "根性", "智力", "(5%)", "(10%)", "(15%)", "(20%)");
        Require(normal.IndexOf("速度", StringComparison.Ordinal) < normal.IndexOf("耐力", StringComparison.Ordinal), "Training rows must follow stat order, not packet order.");

        var summer = Fixtures.Response(turn: 37, summer: true);
        Array.Reverse(summer.data.home_info.command_info_array);
        summer.data.home_info.command_info_array = [.. summer.data.home_info.command_info_array, new()
        {
            command_id = 3101,
            params_inc_dec_info_array = Fixtures.Parameters(4),
            training_partner_array = [1, 2, 101],
            tips_event_partner_array = []
        }];
        summer.data.pioneer_data_set.command_info_array = [.. summer.data.pioneer_data_set.command_info_array, new()
        {
            command_id = 3101,
            params_inc_dec_info_array = Fixtures.Parameters(6, includeVital: false)
        }];
        Dispatch(context, "shima_training_exec", summer);
        ScreenContains("速度", "耐力", "(5%)", "Lv1 | 1", "属性: 50", "Pt: 10", "支援卡 2 人 | NPC 1 人");
    }
    finally { plugin.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}

void TestOriginalDashboard()
{
    using var directory = new TempCurrentDirectory("Pioneer-dashboard");
    using var context = new RuntimePluginContext(ui.Application);
    var plugin = new PioneerPlugin();
    plugin.Initialize(context);
    var id = new PioneerTrainingDisplayId(1001, 1);
    using var producer = PioneerTrainingDisplay.RegisterPartProducer("事件统计");
    producer.Update(id, (_, display) => display.Extra.AddText("连续事件出现 3 次\n走完 1 张卡\n剩余 6 个连续事件"));
    try
    {
        Dispatch(context, "check_event", Fixtures.Dashboard());
        foreach (var size in new[] { new Size(120, 40), new Size(160, 45) })
        {
            var cells = ui.CaptureCells(size.Width, size.Height);
            var headers = new[] { "速度", "耐力", "力量", "根性", "智力" }.Select(text => FindToken(cells, text)[0]).ToArray();
            Require(headers.All(point => point.Y == headers[0].Y), "Five training headings must share one row.");
            Require(headers.Zip(headers.Skip(1)).All(pair => pair.First.X < pair.Second.X), "Training columns are out of order.");
            var frames = ui.InvokeOnOwner(() => Descendants(ui.Application.TopRunnableView!).Where(view => view.Id?.StartsWith("pioneer-", StringComparison.Ordinal) == true)
                .ToDictionary(view => view.Id!, view => view.FrameToScreen()));
            Require(frames["pioneer-header:date"].Top == frames["pioneer-header:vital"].Top, "Status panels are not horizontal.");
            Require(frames["pioneer-header:date"].Height == 3 && frames["pioneer-important"].Height == 5, "Original status/important heights changed.");
            Require(frames["pioneer-scenario:cycle"].Top == frames["pioneer-scenario:island-partners"].Top, "Scenario panels are not horizontal.");
            Require(frames["pioneer-scenario:island-partners"].Width > frames["pioneer-scenario:cycle"].Width, "Original scenario panel ratios were lost.");
            Require(frames["pioneer-training:5"].Right <= frames["pioneer-extras"].Left, "Extras must be beside the five training tables.");
            var extras = frames["pioneer-extras"];
            Require(extras.Height < frames["pioneer-main"].Height, "Extras must fit its content instead of stretching to the main panel height.");
            Require(cells[extras.Top + 1, extras.Left + 2].Grapheme == "─", "Extras lost its leading horizontal rule.");
            Require(frames["pioneer-training:5"].Bottom < size.Height, "Default training tables do not fit in a normal terminal.");
            RequireForeground(cells, "(5%)", 0, new(StandardColor.BrightYellow));
            RequireForeground(cells, "(20%)", 0, new(StandardColor.DarkOrange));
            RequireForeground(cells, "(40%)", 0, new(StandardColor.BrightRed));
            RequireForeground(cells, "20/100", 0, new(StandardColor.BrightRed), frames["pioneer-training:1"]);
            RequireForeground(cells, "属:50|Pt:10", 2, new(StandardColor.BrightCyan));
            var speed = frames["pioneer-training:1"];
            Require(cells[speed.Top, speed.Left].Attribute?.Foreground == new TColor(StandardColor.LightGreen), "Friendship training border lost its highlight.");
            RequireForeground(cells, "[友]理事", 0, new(StandardColor.Green));
            RequireForeground(cells, "[速]气槽", 0, new(StandardColor.BrightCyan));
            RequireForeground(cells, "[力]玉藻", 0, new(StandardColor.BrightYellow));
            RequireForeground(cells, "理事长 45", 0, new(StandardColor.Cyan));
            RequireForeground(cells, "理事长 45", 4, new(StandardColor.BrightRed));
            if (capturePath is not null && size.Width == 120)
                WriteFramebuffer(capturePath, cells);
        }

        ui.CaptureScreen(80, 25, restore: false);
        ui.InvokeOnOwner(() =>
        {
            var root = Descendants(ui.Application.TopRunnableView!).Single(view => view.Id == "pioneer-root");
            var training = Descendants(root).Where(view => view.Id?.StartsWith("pioneer-training:", StringComparison.Ordinal) == true).ToArray();
            Require(training.Length == 5 && training.All(view => view.Frame.Y == training[0].Frame.Y), "Narrow terminals stacked the training tables.");
            Require(root.HorizontalScrollBar.Visible, "Narrow terminals must retain horizontal scrolling.");
            root.ScrollHorizontal(root.GetContentSize().Width);
            Require(root.Viewport.X > 0, "The narrow dashboard did not scroll horizontally.");
            return true;
        });
        Require(ui.CaptureScreen(80, 25, restore: false).Contains("智力", StringComparison.Ordinal), "Horizontal scrolling cannot reach the wisdom training table.");
        ui.CaptureScreen(160, 45, restore: false);
        Require(ui.InvokeOnOwner(() => Descendants(ui.Application.TopRunnableView!).Single(view => view.Id == "pioneer-root").Viewport.X == 0), "Resize left an invalid horizontal viewport offset.");
        ui.CaptureScreen(80, 25, restore: false);
        producer.Update(id, (_, display) => display.Training.Modify(PioneerTrain.Speed, training =>
        {
            for (var index = 0; index < 30; index++)
                training.AddText($"追加行 {index + 1}");
            training.AddText("末尾内容");
        }));
        PioneerTrainingDisplay.Show(id);
        ui.SendKey(Key.PageDown);
        Require(ui.InvokeOnOwner(() => Descendants(ui.Application.TopRunnableView!).Single(view => view.Id == "pioneer-root").Viewport.Y > 0), "PageDown did not scroll the training content.");
        ui.SendKey(Key.End);
        Require(ui.CaptureScreen(80, 25, restore: false).Contains("末尾内容", StringComparison.Ordinal), "Scrolled content tail is unreachable.");
        ui.SendKey(Key.Home);
        Require(ui.CaptureScreen(80, 25, restore: false).Contains("总属性", StringComparison.Ordinal), "Home did not restore the top panels.");
    }
    finally
    {
        plugin.DisposeAsync().AsTask().GetAwaiter().GetResult();
        ui.CaptureScreen(120, 40, restore: false);
    }
}

void TestProducers()
{
    using var directory = new TempCurrentDirectory("Pioneer-producers");
    using var context = new RuntimePluginContext(ui.Application);
    var plugin = new PioneerPlugin();
    plugin.Initialize(context);
    var id = new PioneerTrainingDisplayId(1001, 1);
    using var first = PioneerTrainingDisplay.RegisterPartProducer("First");
    using var second = PioneerTrainingDisplay.RegisterPartProducer("Second");
    try
    {
        Expect<ArgumentException>(() => PioneerTrainingDisplay.RegisterPartProducer("invalid\ntitle"));
        first.Update(id, (_, display) => display.Extra.AddText("before scenario"));
        Require(!PioneerTrainingDisplay.Show(id), "Part without a scenario must not publish.");
        Dispatch(context, "check_event", Fixtures.Response());
        ScreenContains("before scenario");
        first.Update(id, (_, display) =>
        {
            display.Important.AddText("important marker");
            display.Scenario.AddText("scenario marker");
            display.Training.Modify(PioneerTrain.Speed, training =>
            {
                training.Title = "custom speed";
                training.AddText("training marker");
            });
            var segments = new[] { new PioneerDisplaySegment("first marker", PioneerDisplayColor.Yellow) };
            display.Extra.AddStyled(segments);
            segments[0] = new("mutated marker");
        });
        second.Update(id, (_, display) => display.Extra.AddText("second marker"));
        Require(!ui.CaptureScreen().Contains("first marker", StringComparison.Ordinal), "Update published implicitly.");
        Require(PioneerTrainingDisplay.Show(id), "Existing display failed to publish.");
        var composed = ui.CaptureScreen();
        ScreenContains("important marker", "scenario marker", "custom speed", "training marker", "first marker", "second marker");
        RequireForeground(ui.CaptureCells(120, 40), "first marker", 0, new(StandardColor.BrightYellow));
        Require(composed.IndexOf("first marker", StringComparison.Ordinal) < composed.IndexOf("second marker", StringComparison.Ordinal), "Producers must compose in registration order.");
        Require(!composed.Contains("before scenario", StringComparison.Ordinal), "Same producer retained its replaced part.");
        Require(!PioneerTrainingDisplay.Show(id, cancellationToken: new CancellationToken(canceled: true)), "Canceled Show published.");

        second.Update(id, (_, display) => { display.Important.AddText("partial failed output"); throw new InvalidOperationException("test failure"); });
        Expect<InvalidOperationException>(() => PioneerTrainingDisplay.Show(id));
        ScreenContains("second marker");
        Require(!ui.CaptureScreen().Contains("partial failed output", StringComparison.Ordinal), "Failed producer changed the last successful output.");
        second.Dispose();
        ScreenContains("second marker");
        Require(PioneerTrainingDisplay.Show(id), "Show after producer disposal failed.");
        Require(!ui.CaptureScreen().Contains("second marker", StringComparison.Ordinal), "Disposed producer remained active.");

        Dispatch(context, "check_event", Fixtures.Response(turn: 2, vital: 63));
        ScreenContains("63/100");
        Require(!ui.CaptureScreen().Contains("first marker", StringComparison.Ordinal), "Producer part leaked to another display ID.");
    }
    finally { plugin.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}

void TestHistory()
{
    using var directory = new TempCurrentDirectory("Pioneer-history");
    var settings = Path.Combine(directory.Path, "PluginData", "PioneerScenarioAnalyzer", "settings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
    File.WriteAllText(settings, "{\"historyLimit\":3}");
    using var context = new RuntimePluginContext(ui.Application);
    var plugin = new PioneerPlugin();
    plugin.Initialize(context);
    try
    {
        Dispatch(context, "check_event", Fixtures.Response(1, 80));
        Dispatch(context, "check_event", Fixtures.Response(2, 70));
        Dispatch(context, "check_event", Fixtures.Response(3, 60));
        ui.SendKey(Key.CursorUp);
        ScreenContains("70/100");
        Dispatch(context, "check_event", Fixtures.Response(2, 71));
        ScreenContains("71/100");
        ui.Bootstrap.SwitchTo();
        Dispatch(context, "check_event", Fixtures.Response(4, 50));
        Require(ReferenceEquals(Workspace.Current, ui.Bootstrap), "Incoming history stole focus while browsing older output.");
        Workspace.Create("PioneerScenarioAnalyzer").SwitchTo();
        ScreenContains("71/100");
        Require(!PioneerTrainingDisplay.Show(new(1001, 1)), "Evicted history retained its display unit.");
        Dispatch(context, "check_event", Fixtures.Response(5, 40));
        ScreenContains("40/100");
        ui.SendKey(Key.CursorLeft);
        ScreenContains("60/100");
        ui.SendKey(Key.CursorRight);
        ScreenContains("40/100");
        ui.SendKey(Key.CursorUp);
        ScreenContains("50/100");
        Dispatch(context, "check_event", Fixtures.Response(6, 30));
        ScreenContains("50/100");

        var beforeCancel = File.ReadAllText(settings);
        Expect<OperationCanceledException>(() => ui.RunDialogAsync(
            () => plugin.ConfigPromptAsync(ui.Application),
            dialog => { HistoryConfigDialog.SetHistoryLimit(dialog, 0); HistoryConfigDialog.AcceptButton(ui.Application, dialog, "取消"); }).GetAwaiter().GetResult());
        Require(File.ReadAllText(settings) == beforeCancel, "Cancel persisted draft settings.");
        ScreenContains("50/100");
        ui.RunDialogAsync(() => plugin.ConfigPromptAsync(ui.Application), dialog =>
        {
            HistoryConfigDialog.SetHistoryLimit(dialog, 0);
            HistoryConfigDialog.AcceptButton(ui.Application, dialog, "保存");
        }).GetAwaiter().GetResult();
        ScreenContains("30/100");
        Require(!ui.WasHandledByApplicationKeyDown(Key.CursorLeft), "Disabled history consumed arrow keys.");
        Require(!PioneerTrainingDisplay.Show(new(1001, 5)), "Disabled history retained the prior unit.");
        using (var producer = PioneerTrainingDisplay.RegisterPartProducer("scrolling"))
        {
            producer.Update(new(1001, 6), (_, display) => display.Extra.AddText(string.Join('\n', Enumerable.Range(1, 50).Select(index => $"event {index}"))));
            Require(PioneerTrainingDisplay.Show(new(1001, 6)), "Disabled history lost the live display unit.");
            ui.SendKey(Key.PageDown);
            Require(ui.InvokeOnOwner(() => Descendants(ui.Application.TopRunnableView!).Single(view => view.Id == "pioneer-root").Viewport.Y > 0), "Disabling history also disabled content scrolling.");
            ui.SendKey(Key.Home);
            ScreenContains("30/100");
        }

        ui.RunDialogAsync(() => plugin.ConfigPromptAsync(ui.Application), dialog =>
        {
            HistoryConfigDialog.SetHistoryLimit(dialog, 3);
            HistoryConfigDialog.AcceptButton(ui.Application, dialog, "保存");
        }).GetAwaiter().GetResult();
        Require(!ui.WasHandledByApplicationKeyDown(Key.CursorLeft), "Re-enabling empty history consumed arrow keys.");
        ScreenContains("30/100");
        Dispatch(context, "check_event", Fixtures.Response(7, 20));
        Require(!PioneerTrainingDisplay.Show(new(1001, 6)), "Re-enabled history retained the old live unit.");
        ui.SendKey(Key.CursorLeft);
        ScreenContains("20/100");
        Dispatch(context, "check_event", Fixtures.Response(8, 18));
        Dispatch(context, "check_event", Fixtures.Response(9, 16));
        Dispatch(context, "check_event", Fixtures.Response(7, 21));
        ScreenContains("16/100");
        ui.RunDialogAsync(() => plugin.ConfigPromptAsync(ui.Application), dialog =>
        {
            HistoryConfigDialog.SetHistoryLimit(dialog, 0);
            HistoryConfigDialog.AcceptButton(ui.Application, dialog, "保存");
        }).GetAwaiter().GetResult();
        ScreenContains("21/100");
        Require(!PioneerTrainingDisplay.Show(new(1001, 9)), "Disabling history retained an older analysis after a same-key update.");
    }
    finally { plugin.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
}

static void Dispatch(RuntimePluginContext context, string endpointName, object response)
{
    var endpoint = GameEndpointCatalog.ByPath[$"/umamusume/single_mode_pioneer/{endpointName}"];
    var dispatch = new AnalyzerDispatchContext(endpoint, MessagePackSerializer.Serialize(response.GetType(), response), new(null, null, null, null, null, null));
    var handlers = context.AnalyzerRegistry.Registrations.Where(registration => registration.Kind == AnalyzerKind.Response
        && PluginManager.ExpandEndpointPatterns(registration.Patterns).Any(candidate => candidate.Path == endpoint.Path)).ToArray();
    Require(handlers.Length == 1, $"Expected one analyzer for {endpoint.Path}, got {handlers.Length}.");
    handlers[0].Invoke(dispatch).AsTask().GetAwaiter().GetResult();
}

void ScreenContains(params string[] expected)
{
    var screen = ui.CaptureScreen();
    foreach (var text in expected)
        Require(screen.Contains(text, StringComparison.Ordinal), $"Framebuffer lacks '{text}'.\n{screen}");
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static IEnumerable<View> Descendants(View view)
{
    yield return view;
    foreach (var child in view.SubViews)
        foreach (var descendant in Descendants(child))
            yield return descendant;
}

static Point[] FindToken(Cell[,] cells, string token, Rectangle? area = null)
{
    var bounds = area ?? new Rectangle(0, 0, cells.GetLength(1), cells.GetLength(0));
    var elements = StringInfo.GetTextElementEnumerator(token);
    var graphemes = new List<string>();
    while (elements.MoveNext())
        graphemes.Add((string)elements.Current);
    for (var y = Math.Max(0, bounds.Top); y < Math.Min(bounds.Bottom, cells.GetLength(0)); y++)
        for (var x = Math.Max(0, bounds.Left); x < Math.Min(bounds.Right, cells.GetLength(1)); x++)
        {
            var points = new List<Point>();
            var column = x;
            foreach (var grapheme in graphemes)
            {
                if (column >= Math.Min(bounds.Right, cells.GetLength(1)) || cells[y, column].Grapheme != grapheme)
                    break;
                points.Add(new(column, y));
                column += Math.Max(1, grapheme.GetColumns());
            }
            if (points.Count == graphemes.Count)
                return [.. points];
        }
    throw new InvalidOperationException($"Framebuffer token '{token}' was not found.");
}

static void RequireForeground(Cell[,] cells, string token, int offset, TColor expected, Rectangle? area = null)
{
    var point = FindToken(cells, token, area)[offset];
    Require(cells[point.Y, point.X].Attribute?.Foreground == expected, $"'{token}' lost its original foreground color.");
}

static void WriteFramebuffer(string path, Cell[,] cells)
{
    var rows = Enumerable.Range(0, cells.GetLength(0)).Select(y => Enumerable.Range(0, cells.GetLength(1)).Select(x =>
    {
        var cell = cells[y, x];
        var foreground = cell.Attribute?.Foreground ?? TColor.White;
        var background = cell.Attribute?.Background ?? TColor.Black;
        return new { text = cell.Grapheme, foreground = new int[] { foreground.R, foreground.G, foreground.B }, background = new int[] { background.R, background.G, background.B } };
    }).ToArray()).ToArray();
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, JsonSerializer.Serialize(new { width = cells.GetLength(1), height = cells.GetLength(0), rows }));
}

static void Expect<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class WithoutEventLoggerLoadContext() : AssemblyLoadContext(isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == "EventLoggerPlugin")
            throw new FileNotFoundException("EventLoggerPlugin is absent for this isolated-loading test.");
        return Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), name))
            ?? Default.LoadFromAssemblyName(name);
    }
}
