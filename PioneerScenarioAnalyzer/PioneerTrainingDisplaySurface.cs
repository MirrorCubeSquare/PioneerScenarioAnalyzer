using UmamusumeResponseAnalyzer.TerminalGui;

namespace PioneerScenarioAnalyzer;

public enum PioneerTrain
{
    Speed = 1,
    Stamina = 2,
    Power = 3,
    Guts = 4,
    Wiz = 5
}

public readonly record struct PioneerTrainingDisplayId(int SingleModeCharaId, int Turn);

public sealed record PioneerTrainingDisplayContext(object Response, PioneerScenarioResponseData ResponseData);

public static class PioneerTrainingDisplay
{
    static readonly object Gate = new();
    static readonly Dictionary<PioneerTrainingDisplayId, DisplayUnit> Units = [];
    static long nextProducerSequence;

    public static PioneerTrainingDisplayPartProducer RegisterPartProducer(string sourceTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTitle);
        if (sourceTitle.Contains('\r') || sourceTitle.Contains('\n'))
            throw new ArgumentException("Extra 来源标题必须为单行文本。", nameof(sourceTitle));
        return new(sourceTitle, Interlocked.Increment(ref nextProducerSequence));
    }

    internal static void Update(
        object owner,
        PioneerTrainingDisplayId id,
        PioneerTrainingDisplayContext context,
        PioneerTrainingDisplayBuilder builder,
        Action<PioneerTrainingDisplayId, WorkspaceContent, bool> publish)
    {
        lock (Gate)
        {
            if (!Units.TryGetValue(id, out var unit))
                Units.Add(id, unit = new());
            unit.Scenario = new(owner, context, builder, publish);
        }
    }

    public static bool Show(
        PioneerTrainingDisplayId id,
        bool switchToWorkspace = false,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        ScenarioPart scenario;
        KeyValuePair<PioneerTrainingDisplayPartProducer, Action<PioneerTrainingDisplayContext, PioneerTrainingDisplayEditor>>[] parts;
        lock (Gate)
        {
            if (!Units.TryGetValue(id, out var unit) || unit.Scenario is not { } value)
                return false;
            scenario = value;
            parts = [.. unit.Parts.OrderBy(entry => entry.Key.Sequence)];
        }

        // 每次组合使用独立副本，失败的 part 不能修改已保存的场景输出。
        var builder = scenario.Builder.Clone();
        foreach (var (producer, part) in parts)
        {
            var extra = new PioneerDisplayRows();
            part(scenario.Context, new(builder, extra));
            if (extra.Any(row => !string.IsNullOrWhiteSpace(row)))
            {
                builder.ExtraRows.Add(PioneerDisplayLine.Colored(producer.SourceTitle, PioneerDisplayColor.Cyan));
                builder.ExtraRows.AddRange(extra.Lines);
            }
        }
        var content = PioneerTrainingDisplayRenderer.Render(builder);
        if (cancellationToken.IsCancellationRequested)
            return false;
        scenario.Publish(id, content, switchToWorkspace);
        return true;
    }

    internal static void Remove(object owner, PioneerTrainingDisplayId id)
    {
        lock (Gate)
            if (Units.TryGetValue(id, out var unit) && ReferenceEquals(unit.Scenario?.Owner, owner))
                Units.Remove(id);
    }

    internal static void Clear(object owner)
    {
        lock (Gate)
            foreach (var id in Units
                         .Where(entry => ReferenceEquals(entry.Value.Scenario?.Owner, owner))
                         .Select(entry => entry.Key)
                         .ToArray())
                Units.Remove(id);
    }

    internal static void UpdatePart(
        PioneerTrainingDisplayPartProducer producer,
        PioneerTrainingDisplayId id,
        Action<PioneerTrainingDisplayContext, PioneerTrainingDisplayEditor> part)
    {
        ArgumentNullException.ThrowIfNull(part);
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(producer.IsDisposed, producer);
            if (!Units.TryGetValue(id, out var unit))
                Units.Add(id, unit = new());
            unit.Parts[producer] = part;
        }
    }

    internal static void RemoveProducer(PioneerTrainingDisplayPartProducer producer)
    {
        lock (Gate)
        {
            foreach (var (id, unit) in Units.ToArray())
            {
                unit.Parts.Remove(producer);
                if (unit.Scenario is null && unit.Parts.Count == 0)
                    Units.Remove(id);
            }
        }
    }

    sealed record ScenarioPart(
        object Owner,
        PioneerTrainingDisplayContext Context,
        PioneerTrainingDisplayBuilder Builder,
        Action<PioneerTrainingDisplayId, WorkspaceContent, bool> Publish);

    sealed class DisplayUnit
    {
        internal ScenarioPart? Scenario { get; set; }
        internal Dictionary<PioneerTrainingDisplayPartProducer, Action<PioneerTrainingDisplayContext, PioneerTrainingDisplayEditor>> Parts { get; } = [];
    }
}

public sealed class PioneerTrainingDisplayPartProducer : IDisposable
{
    int disposed;

    internal PioneerTrainingDisplayPartProducer(string sourceTitle, long sequence)
    {
        SourceTitle = sourceTitle;
        Sequence = sequence;
    }

    internal string SourceTitle { get; }
    internal long Sequence { get; }
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public void Update(
        PioneerTrainingDisplayId id,
        Action<PioneerTrainingDisplayContext, PioneerTrainingDisplayEditor> part)
        => PioneerTrainingDisplay.UpdatePart(this, id, part);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            PioneerTrainingDisplay.RemoveProducer(this);
    }
}

public sealed class PioneerTrainingDisplayEditor
{
    internal PioneerTrainingDisplayEditor(PioneerTrainingDisplayBuilder builder, PioneerDisplayRows extraRows)
    {
        Training = new(builder.TrainingCards);
        Important = new(builder.ImportantRows);
        Scenario = new(builder.ScenarioRows);
        Extra = new(extraRows);
    }

    public PioneerTrainingCardsEditor Training { get; }
    public PioneerDisplayRowsEditor Important { get; }
    public PioneerDisplayRowsEditor Scenario { get; }
    public PioneerDisplayRowsEditor Extra { get; }
}

public sealed class PioneerTrainingCardsEditor
{
    readonly List<PioneerTrainingCard> cards;

    internal PioneerTrainingCardsEditor(List<PioneerTrainingCard> cards)
        => this.cards = cards;

    public PioneerTrainingCardEditor Get(PioneerTrain train)
        => new(cards.FirstOrDefault(card => card.TrainIndex == (int)train)
            ?? throw new InvalidOperationException($"无人岛训练不存在: {train}。"));

    public PioneerTrainingCardEditor GetByCommandId(int commandId)
        => new(cards.FirstOrDefault(card => card.CommandId == commandId)
            ?? throw new InvalidOperationException($"无人岛训练 command 不存在: {commandId}。"));

    public void Modify(PioneerTrain train, Action<PioneerTrainingCardEditor> modifier)
    {
        ArgumentNullException.ThrowIfNull(modifier);
        modifier(Get(train));
    }

    public void ModifyByCommandId(int commandId, Action<PioneerTrainingCardEditor> modifier)
    {
        ArgumentNullException.ThrowIfNull(modifier);
        modifier(GetByCommandId(commandId));
    }
}

public sealed class PioneerTrainingCardEditor
{
    readonly PioneerTrainingCard card;

    internal PioneerTrainingCardEditor(PioneerTrainingCard card)
        => this.card = card;

    public int CommandId => card.CommandId;
    public PioneerTrain Train => (PioneerTrain)card.TrainIndex;

    public string Title
    {
        get => card.Title;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            card.Title = value;
        }
    }

    public bool Highlighted
    {
        get => card.Highlighted;
        set => card.Highlighted = value;
    }

    public void Highlight() => Highlighted = true;

    public void AddText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        card.Rows.Add(text);
    }
}

public sealed class PioneerDisplayRowsEditor
{
    readonly PioneerDisplayRows rows;

    internal PioneerDisplayRowsEditor(PioneerDisplayRows rows)
        => this.rows = rows;

    public void AddText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        rows.Add(text);
    }
}

internal sealed class PioneerTrainingDisplayBuilder
{
    public List<PioneerDisplayPanel> HeaderPanels { get; } = [];
    public PioneerDisplayRows ImportantRows { get; } = new();
    public List<PioneerDisplayPanel> ScenarioPanels { get; } = [];
    public PioneerDisplayRows ScenarioRows { get; } = new();
    public List<PioneerTrainingCard> TrainingCards { get; } = [];
    public PioneerDisplayRows ExtraRows { get; } = new();

    internal PioneerTrainingDisplayBuilder Clone()
    {
        var clone = new PioneerTrainingDisplayBuilder();
        clone.HeaderPanels.AddRange(HeaderPanels);
        clone.ImportantRows.AddRange(ImportantRows.Lines);
        clone.ScenarioPanels.AddRange(ScenarioPanels);
        clone.ScenarioRows.AddRange(ScenarioRows.Lines);
        clone.ExtraRows.AddRange(ExtraRows.Lines);
        foreach (var card in TrainingCards)
        {
            var copy = new PioneerTrainingCard(card.CommandId, card.TrainIndex, card.Title)
            {
                Highlighted = card.Highlighted,
                StyledTitle = card.StyledTitle
            };
            copy.Rows.AddRange(card.Rows.Lines);
            clone.TrainingCards.Add(copy);
        }
        return clone;
    }

}

internal sealed class PioneerTrainingCard(int commandId, int trainIndex, string title)
{
    public int CommandId { get; } = commandId;
    public int TrainIndex { get; } = trainIndex;
    public PioneerDisplayLine StyledTitle { get; set; } = PioneerDisplayLine.Plain(title);
    public string Title
    {
        get => StyledTitle.Text;
        set => StyledTitle = PioneerDisplayLine.Plain(value);
    }
    public bool Highlighted { get; set; }
    public PioneerDisplayRows Rows { get; } = new();
}

internal enum PioneerDisplayColor
{
    Normal, Cyan, Green, Yellow, Red, DarkOrange, Aqua, Lime, LightGreen, Teal
}

internal readonly record struct PioneerDisplaySegment(string Text, PioneerDisplayColor Color = PioneerDisplayColor.Normal);

internal sealed record PioneerDisplayLine(IReadOnlyList<PioneerDisplaySegment> Segments, bool IsRule = false)
{
    public string Text => string.Concat(Segments.Select(segment => segment.Text));
    public static PioneerDisplayLine Plain(string text) => new([new(text)]);
    public static PioneerDisplayLine Colored(string text, PioneerDisplayColor color) => new([new(text, color)]);
    public static PioneerDisplayLine Styled(params PioneerDisplaySegment[] segments) => new(segments);
    public static PioneerDisplayLine Rule { get; } = new([], IsRule: true);
}

internal sealed class PioneerDisplayRows : IReadOnlyList<string>
{
    readonly List<PioneerDisplayLine> lines = [];
    public IReadOnlyList<PioneerDisplayLine> Lines => lines;
    public int Count => lines.Count;
    public string this[int index] => lines[index].Text;
    public void Add(string text)
    {
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
            lines.Add(PioneerDisplayLine.Plain(line));
    }
    public void Add(PioneerDisplayLine line) => lines.Add(line);
    public void AddRange(IEnumerable<string> rows)
    {
        foreach (var row in rows)
            Add(row);
    }
    public void AddRange(IEnumerable<PioneerDisplayLine> rows) => lines.AddRange(rows);
    public IEnumerator<string> GetEnumerator() => lines.Select(line => line.Text).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed record PioneerDisplayPanel(string Key, int Ratio, PioneerDisplayLine Line);
