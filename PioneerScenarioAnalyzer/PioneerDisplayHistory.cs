using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.TerminalGui;
using TKey = Terminal.Gui.Input.Key;

namespace PioneerScenarioAnalyzer;

internal sealed class PioneerDisplayHistory(
    string workspaceTitle,
    string panelKey,
    string panelTitle)
{
    const int DefaultHistoryLimit = 100;
    const int MaximumHistoryLimit = 1000;

    static readonly JsonSerializerOptions SettingsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    readonly object gate = new();
    readonly List<Entry> entries = [];
    IApplication? application;
    Func<Key, bool>? showUnit;
    Action<Key>? evictUnit;
    Workspace? workspace;
    HistoryView? view;
    WorkspaceContent? liveSnapshot;
    Key? visibleKey;
    Key? latestAnalysisKey;
    int historyLimit = DefaultHistoryLimit;
    int selectedIndex = -1;
    bool active;
    bool hasUnread;
    bool panelAdmitted;

    internal readonly record struct Key(int SingleModeCharaId, int Turn);

    public void Initialize(
        IApplication targetApplication,
        Func<Key, bool> show,
        Action<Key> evict)
    {
        ArgumentNullException.ThrowIfNull(targetApplication);
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(evict);
        var settings = LoadSettings();
        lock (gate)
        {
            application = targetApplication;
            showUnit = show;
            evictUnit = evict;
            workspace = null;
            view = null;
            entries.Clear();
            liveSnapshot = null;
            visibleKey = null;
            latestAnalysisKey = null;
            historyLimit = settings.HistoryLimit;
            selectedIndex = -1;
            hasUnread = false;
            panelAdmitted = false;
            active = true;
        }
    }

    public bool ShouldShow(Key key)
    {
        lock (gate)
        {
            if (!active)
                return false;
            if (historyLimit == 0 || selectedIndex < 0 || selectedIndex >= entries.Count)
                return true;
            if (entries[selectedIndex].HistoryKey == key)
                return true;
            if (entries.Any(entry => entry.HistoryKey == key))
                return false;

            return selectedIndex == entries.Count - 1 ||
                entries.Count >= historyLimit && selectedIndex == 0;
        }
    }

    public void RecordAnalysis(Key key)
    {
        Key? evicted = null;
        Action<Key>? evict;
        lock (gate)
        {
            if (!active)
                return;

            // 同键更新不重排 history；最近解析的 ID 因此需要独立于最后一条记录保存。
            if (latestAnalysisKey is { } previous && previous != key && previous != visibleKey &&
                !entries.Any(entry => entry.HistoryKey == previous))
                evicted = previous;
            latestAnalysisKey = key;
            evict = evictUnit;
        }
        if (evicted is { } old)
            evict?.Invoke(old);
    }

    public void Track(Workspace target, Key key)
        => PublishOrTrack(target, key, snapshot: null, switchToWorkspace: false);

    public void Show(
        Workspace target,
        Key key,
        WorkspaceContent snapshot,
        bool switchToWorkspace)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        PublishOrTrack(target, key, snapshot, switchToWorkspace);
    }

    void PublishOrTrack(
        Workspace target,
        Key key,
        WorkspaceContent? snapshot,
        bool switchToWorkspace)
    {
        ArgumentNullException.ThrowIfNull(target);

        var commit = snapshot is not null;
        var evicted = new List<Key>();
        Action<Key>? evict;
        bool admitPanel;
        bool notifyUnread;
        lock (gate)
        {
            if (!active)
                return;

            workspace = target;
            var previouslyVisible = visibleKey;
            if (historyLimit == 0)
            {
                if (previouslyVisible is { } old && old != key)
                    evicted.Add(old);
                entries.Clear();
                selectedIndex = -1;
                hasUnread = false;
                notifyUnread = false;
            }
            else
            {
                Key? selectedKey = selectedIndex >= 0 && selectedIndex < entries.Count
                    ? entries[selectedIndex].HistoryKey
                    : null;
                var wasBrowsingOlder = selectedIndex >= 0 && selectedIndex < entries.Count - 1;
                var previouslyUnread = hasUnread;
                var index = entries.FindIndex(entry => entry.HistoryKey == key);
                var inserted = index < 0;
                if (inserted)
                    entries.Add(new(key));

                var overflow = entries.Count - historyLimit;
                if (overflow > 0)
                {
                    evicted.AddRange(entries.Take(overflow).Select(entry => entry.HistoryKey));
                    entries.RemoveRange(0, overflow);
                }

                if (commit)
                {
                    selectedIndex = entries.FindIndex(entry => entry.HistoryKey == key);
                    if (selectedIndex < 0)
                        throw new InvalidOperationException($"{workspaceTitle} 无法保留 DisplayId: {key}。");
                    if (selectedIndex == entries.Count - 1)
                        hasUnread = false;
                    notifyUnread = false;
                }
                else
                {
                    var retainedSelection = selectedKey is { } currentKey
                        ? entries.FindIndex(entry => entry.HistoryKey == currentKey)
                        : -1;
                    if (selectedKey is not null && retainedSelection < 0)
                    {
                        selectedIndex = entries.Count - 1;
                        hasUnread = false;
                    }
                    else if (!wasBrowsingOlder || selectedKey is null)
                    {
                        selectedIndex = entries.Count - 1;
                        hasUnread = false;
                    }
                    else
                    {
                        selectedIndex = retainedSelection;
                        if (inserted)
                            hasUnread = true;
                        if (selectedIndex == entries.Count - 1)
                            hasUnread = false;
                    }

                    notifyUnread = inserted && wasBrowsingOlder && !previouslyUnread && hasUnread;
                }
            }

            if (commit)
            {
                liveSnapshot = snapshot;
                visibleKey = key;
                if (previouslyVisible is { } previous && previous != key &&
                    !entries.Any(entry => entry.HistoryKey == previous))
                    evicted.Add(previous);
            }
            admitPanel = commit && !panelAdmitted;
            if (admitPanel)
                panelAdmitted = true;
            evict = evictUnit;
        }

        foreach (var evictedKey in evicted.Distinct())
            if (evictedKey != key && evictedKey != latestAnalysisKey)
                evict?.Invoke(evictedKey);

        if (commit)
        {
            try
            {
                if (admitPanel)
                {
                    target.SetPanel(
                        panelKey,
                        panelTitle,
                        StablePanelContent,
                        fullBleed: true,
                        switchToWorkspace: switchToWorkspace);
                }
                else
                {
                    RefreshView(snapshot);
                    target.SetPanel(panelKey, panelTitle, StablePanelContent,
                        fullBleed: true, switchToWorkspace: switchToWorkspace);
                }
            }
            catch
            {
                if (admitPanel)
                {
                    lock (gate)
                        panelAdmitted = false;
                }
                throw;
            }
        }

        if (notifyUnread)
            target.Notify("有新的训练历史。按 → 查看最新。");
    }

    public void Stop()
    {
        HistoryView? currentView;
        lock (gate)
        {
            active = false;
            entries.Clear();
            liveSnapshot = null;
            visibleKey = null;
            latestAnalysisKey = null;
            selectedIndex = -1;
            hasUnread = false;
            panelAdmitted = false;
            currentView = view;
            view = null;
            workspace = null;
            showUnit = null;
            evictUnit = null;
            application = null;
        }
        currentView?.Deactivate();
    }

    public async Task ConfigPromptAsync(
        IApplication targetApplication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetApplication);
        cancellationToken.ThrowIfCancellationRequested();
        if (targetApplication.TopRunnable is null &&
            Environment.CurrentManagedThreadId != targetApplication.MainThreadId)
        {
            throw new InvalidOperationException(
                $"{workspaceTitle} 无法从非 UI thread 启动配置：Terminal.Gui 当前没有正在运行的 session。");
        }

        var draft = LoadSettings().HistoryLimit;
        int saved;
        if (Environment.CurrentManagedThreadId == targetApplication.MainThreadId)
        {
            saved = RunConfigDialog(targetApplication, draft, cancellationToken);
        }
        else
        {
            var completion = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            targetApplication.Invoke(() =>
            {
                try
                {
                    completion.SetResult(RunConfigDialog(
                        targetApplication,
                        draft,
                        cancellationToken));
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            saved = await completion.Task;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SaveSettings(new(saved));
        ApplyHistoryLimit(saved);
    }

    WorkspaceContent StablePanelContent
    {
        get
        {
            // The factory must create a fresh root for every Host realization.
            lock (gate)
                return stablePanelContent ??= new(CreateHistoryView);
        }
    }

    WorkspaceContent? stablePanelContent;

    void ApplyHistoryLimit(int value)
    {
        var evicted = new List<Key>();
        Key? showKey = null;
        Func<Key, bool>? show;
        Action<Key>? evict;
        lock (gate)
        {
            Key? selectedKey = selectedIndex >= 0 && selectedIndex < entries.Count
                ? entries[selectedIndex].HistoryKey
                : null;
            historyLimit = value;
            if (value == 0)
            {
                if (latestAnalysisKey is { } latest && latest != visibleKey)
                    showKey = latest;
                evicted.AddRange(entries
                    .Select(entry => entry.HistoryKey)
                    .Where(key => key != visibleKey));
                entries.Clear();
                selectedIndex = -1;
                hasUnread = false;
            }
            else
            {
                var overflow = entries.Count - value;
                if (overflow > 0)
                {
                    evicted.AddRange(entries.Take(overflow).Select(entry => entry.HistoryKey));
                    entries.RemoveRange(0, overflow);
                }
                selectedIndex = selectedKey is { } currentKey
                    ? entries.FindIndex(entry => entry.HistoryKey == currentKey)
                    : -1;
                if (selectedIndex < 0 && entries.Count != 0)
                {
                    selectedIndex = entries.Count - 1;
                    hasUnread = false;
                }
                else if (selectedIndex == entries.Count - 1)
                {
                    hasUnread = false;
                }
                if (selectedIndex >= 0 && entries[selectedIndex].HistoryKey != visibleKey)
                    showKey = entries[selectedIndex].HistoryKey;
            }
            show = showUnit;
            evict = evictUnit;
        }

        foreach (var key in evicted.Distinct())
            if (key != showKey && key != visibleKey && key != latestAnalysisKey)
                evict?.Invoke(key);
        if (showKey is { } targetKey && (show is null || !show(targetKey)))
            throw new InvalidOperationException($"{workspaceTitle} 无法显示保留的 DisplayId: {targetKey}。");
    }

    bool TryNavigate(Navigation navigation)
    {
        Key? key = null;
        Func<Key, bool>? show;
        Workspace? target;
        string? notification = null;
        lock (gate)
        {
            if (!active || historyLimit == 0 || entries.Count == 0)
                return false;
            target = workspace;
            if (entries.Count != 0)
            {
                if (selectedIndex < 0 || selectedIndex >= entries.Count)
                    selectedIndex = entries.Count - 1;
                var previousIndex = selectedIndex;
                selectedIndex = navigation switch
                {
                    Navigation.Older => Math.Max(0, selectedIndex - 1),
                    Navigation.Newer => Math.Min(entries.Count - 1, selectedIndex + 1),
                    Navigation.Oldest => 0,
                    Navigation.Newest => entries.Count - 1,
                    _ => selectedIndex,
                };
                if (selectedIndex == entries.Count - 1)
                    hasUnread = false;
                if (selectedIndex != previousIndex)
                    key = entries[selectedIndex].HistoryKey;
                notification = $"训练历史 {selectedIndex + 1}/{entries.Count}";
            }
            show = showUnit;
        }

        if (key is { } targetKey && (show is null || !show(targetKey)))
            throw new InvalidOperationException($"{workspaceTitle} 无法显示选中的 DisplayId: {targetKey}。");
        if (notification is not null)
            target?.Notify(notification);
        return true;
    }

    void RefreshView(WorkspaceContent? snapshot)
    {
        IApplication? targetApplication;
        lock (gate)
            targetApplication = active ? application : null;
        if (targetApplication is null || snapshot is null)
            return;

        void Refresh()
        {
            HistoryView? currentView;
            lock (gate)
            {
                if (!active)
                    return;
                currentView = view;
            }
            currentView?.SetContent(snapshot);
        }

        if (Environment.CurrentManagedThreadId == targetApplication.MainThreadId)
        {
            Refresh();
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        targetApplication.Invoke(() =>
        {
            try
            {
                Refresh();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        completion.Task
            .WaitAsync(TimeSpan.FromSeconds(10))
            .GetAwaiter()
            .GetResult();
    }

    View CreateHistoryView()
    {
        IApplication targetApplication;
        lock (gate)
        {
            targetApplication = application
                ?? throw new InvalidOperationException($"{workspaceTitle} 尚未初始化。");
        }
        return new HistoryView(this, targetApplication);
    }

    bool AttachHistoryView(HistoryView candidate, out WorkspaceContent? snapshot)
    {
        lock (gate)
        {
            snapshot = liveSnapshot;
            if (!active)
                return false;
            view = candidate;
            return true;
        }
    }

    void DetachHistoryView(HistoryView candidate)
    {
        lock (gate)
        {
            if (ReferenceEquals(view, candidate))
                view = null;
        }
    }

    bool CanNavigate(HistoryView candidate, IApplication targetApplication)
    {
        lock (gate)
        {
            if (!active ||
                !ReferenceEquals(Workspace.Current, workspace))
            {
                return false;
            }
        }

        for (var focused = targetApplication.TopRunnableView?.MostFocused;
             focused is not null;
             focused = focused.SuperView)
        {
            if (ReferenceEquals(focused, candidate))
                return true;
        }
        return false;
    }

    bool CanFocusHistory(IApplication targetApplication)
    {
        lock (gate)
            return active && ReferenceEquals(Workspace.Current, workspace) &&
                ReferenceEquals(targetApplication, application);
    }

    static HistorySettings LoadSettings()
    {
        var path = SettingsFilePath;
        if (!File.Exists(path))
            return new(DefaultHistoryLimit);

        HistorySettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<HistorySettings>(
                    File.ReadAllText(path),
                    SettingsJson)
                ?? throw new JsonException("配置内容为 null。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"PioneerScenarioAnalyzer 配置文件无效: {path}。{ex.Message}",
                ex);
        }
        ValidateHistoryLimit(settings.HistoryLimit, path);
        return settings;
    }

    static void SaveSettings(HistorySettings settings)
    {
        ValidateHistoryLimit(settings.HistoryLimit, SettingsFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
        File.WriteAllText(
            SettingsFilePath,
            JsonSerializer.Serialize(settings, SettingsJson));
    }

    static void ValidateHistoryLimit(int value, string source)
    {
        if (value is < 0 or > MaximumHistoryLimit)
        {
            throw new InvalidDataException(
                $"PioneerScenarioAnalyzer historyLimit 必须在 0 到 {MaximumHistoryLimit} 之间: {source}，实际值 {value}。");
        }
    }

    static int RunConfigDialog(
        IApplication application,
        int draft,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var dialog = new Dialog
        {
            Title = "PioneerScenarioAnalyzer 配置",
            Width = 62,
            Height = 10,
        };
        var input = new TextField
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Text = draft.ToString(CultureInfo.InvariantCulture),
        };
        var validation = new Label
        {
            X = 1,
            Y = 4,
            Width = Dim.Fill(1),
            Height = 2,
        };
        dialog.Add(
            new Label { X = 1, Y = 1, Text = $"History 上限 (0-{MaximumHistoryLimit})" },
            input,
            validation);

        var accepted = false;
        var result = draft;
        var save = new Button { Text = "保存", IsDefault = true };
        save.Accepting += (_, e) =>
        {
            if (!int.TryParse(
                    input.Text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out result) || result is < 0 or > MaximumHistoryLimit)
            {
                validation.Text = $"History 上限必须是 0 到 {MaximumHistoryLimit} 之间的整数。";
                e.Handled = true;
                return;
            }

            accepted = true;
            application.RequestStop(dialog);
            e.Handled = true;
        };
        var cancel = new Button { Text = "取消" };
        cancel.Accepting += (_, e) =>
        {
            application.RequestStop(dialog);
            e.Handled = true;
        };
        dialog.AddButton(cancel);
        dialog.AddButton(save);
        input.SetFocus();

        using (cancellationToken.Register(
                   () => application.Invoke(() => application.RequestStop(dialog))))
            application.Run(dialog);
        cancellationToken.ThrowIfCancellationRequested();
        if (!accepted)
        {
            throw new OperationCanceledException(
                "PioneerScenarioAnalyzer 配置已取消。",
                cancellationToken);
        }
        return result;
    }

    sealed class HistoryView : View
    {
        readonly PioneerDisplayHistory owner;
        readonly IApplication application;
        View? content;
        bool active;

        internal HistoryView(PioneerDisplayHistory owner, IApplication application)
        {
            this.owner = owner;
            this.application = application;
            Id = "pioneer-history-root";
            Width = Dim.Fill();
            Height = Dim.Fill();
            CanFocus = true;
            TabStop = TabBehavior.TabGroup;
            active = owner.AttachHistoryView(this, out var initialSnapshot);
            if (active)
                application.Keyboard.KeyDown += ApplicationKeyDown;
            if (initialSnapshot is not null)
                SetContent(initialSnapshot);
            Initialized += (_, _) =>
            {
                if (owner.CanFocusHistory(application))
                    SetFocus();
            };
        }

        internal void SetContent(WorkspaceContent snapshot)
        {
            var focused = Contains(application.TopRunnableView?.MostFocused);
            var next = snapshot.CreateView();
            next.Width = Dim.Fill();
            next.Height = Dim.Fill();
            if (content is not null)
            {
                Remove(content);
                content.Dispose();
            }
            content = next;
            Add(next);
            SetNeedsLayout();
            SetNeedsDraw();
            if (focused)
                SetFocus();
        }

        internal void Deactivate()
        {
            if (!active)
                return;
            active = false;
            application.Keyboard.KeyDown -= ApplicationKeyDown;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Deactivate();
                owner.DetachHistoryView(this);
            }
            base.Dispose(disposing);
        }

        void ApplicationKeyDown(object? sender, TKey key)
        {
            if (!active || key.Handled || key.IsCtrl || key.IsAlt || key.IsShift ||
                !owner.CanNavigate(this, application))
            {
                return;
            }

            Command? contentNavigation = key.KeyCode switch
            {
                var code when code == TKey.PageUp.KeyCode => Command.PageUp,
                var code when code == TKey.PageDown.KeyCode => Command.PageDown,
                var code when code == TKey.Home.KeyCode => Command.Start,
                var code when code == TKey.End.KeyCode => Command.End,
                _ => null,
            };
            if (contentNavigation is { } command && content is not null &&
                PioneerTrainingDisplayRenderer.TryScroll(content, command))
            {
                key.Handled = true;
                return;
            }

            Navigation? navigation = key.KeyCode switch
            {
                var code when code == TKey.CursorUp.KeyCode => Navigation.Older,
                var code when code == TKey.CursorDown.KeyCode => Navigation.Newer,
                var code when code == TKey.CursorLeft.KeyCode => Navigation.Oldest,
                var code when code == TKey.CursorRight.KeyCode => Navigation.Newest,
                _ => null,
            };
            if (navigation is { } requested && owner.TryNavigate(requested))
                key.Handled = true;
        }

        bool Contains(View? focused)
        {
            for (; focused is not null; focused = focused.SuperView)
            {
                if (ReferenceEquals(focused, this))
                    return true;
            }
            return false;
        }
    }

    static string SettingsFilePath
        => Path.Combine("PluginData", "PioneerScenarioAnalyzer", "settings.json");

    sealed record HistorySettings(
        [property: JsonRequired]
        int HistoryLimit);

    readonly record struct Entry(Key HistoryKey);

    enum Navigation
    {
        Older,
        Newer,
        Oldest,
        Newest,
    }
}
