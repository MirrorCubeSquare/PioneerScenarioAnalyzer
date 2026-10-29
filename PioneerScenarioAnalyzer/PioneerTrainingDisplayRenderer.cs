using System.Drawing;
using System.Globalization;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.TerminalGui;
using TAttribute = Terminal.Gui.Drawing.Attribute;
using TColor = Terminal.Gui.Drawing.Color;

namespace PioneerScenarioAnalyzer;

internal static class PioneerTrainingDisplayRenderer
{
    public static WorkspaceContent Render(PioneerTrainingDisplayBuilder builder)
    {
        var snapshot = new Snapshot(
            CultureInfo.CurrentUICulture.Name switch { "zh-CN" => 95, "ja-JP" => 100, _ => 90 },
            [.. builder.HeaderPanels.Select(panel => new PanelSnapshot(panel.Key, panel.Ratio, Copy(panel.Line)))],
            [.. builder.ImportantRows.Lines.Select(Copy)],
            [.. builder.ScenarioPanels.Select(panel => new PanelSnapshot(panel.Key, panel.Ratio, Copy(panel.Line)))],
            [.. builder.ScenarioRows.Lines.Select(Copy)],
            [.. builder.TrainingCards.Select(card => new TrainingSnapshot(card.TrainIndex,
                Copy(card.StyledTitle), card.Highlighted, [.. card.Rows.Lines.Select(Copy)]))],
            [.. builder.ExtraRows.Lines.Select(Copy)]);
        return new(() => new PioneerDashboardView(snapshot));
    }

    internal static bool TryScroll(View view, Command command)
        => view is PioneerDashboardView dashboard && dashboard.Scroll(command);

    static PioneerDisplayLine Copy(PioneerDisplayLine line)
        => new([.. line.Segments], line.IsRule);

    sealed record Snapshot(
        int MainWidth,
        PanelSnapshot[] Headers,
        PioneerDisplayLine[] ImportantRows,
        PanelSnapshot[] ScenarioPanels,
        PioneerDisplayLine[] ScenarioRows,
        TrainingSnapshot[] TrainingCards,
        PioneerDisplayLine[] ExtraRows);

    sealed record PanelSnapshot(string Key, int Ratio, PioneerDisplayLine Line);
    sealed record TrainingSnapshot(int TrainIndex, PioneerDisplayLine Title, bool Highlighted, PioneerDisplayLine[] Rows);

    sealed class PioneerDashboardView : View
    {
        const int HeaderHeight = 3;
        const int ScenarioHeight = 3;
        readonly int mainWidth;
        readonly int minimumContentWidth;
        readonly int mainHeight;
        readonly FrameView extras;
        readonly PioneerDisplayLine[] extraRows;

        internal PioneerDashboardView(Snapshot snapshot)
        {
            Id = "pioneer-root";
            Width = Dim.Fill();
            Height = Dim.Fill();
            CanFocus = true;
            TabStop = TabBehavior.TabGroup;
            ViewportSettings = ViewportSettingsFlags.HasScrollBars;
            AddCommand(Command.PageUp, () => Scroll(Command.PageUp));
            AddCommand(Command.PageDown, () => Scroll(Command.PageDown));
            AddCommand(Command.Start, () => Scroll(Command.Start));
            AddCommand(Command.End, () => Scroll(Command.End));
            KeyBindings.ReplaceCommands(Key.PageUp, Command.PageUp);
            KeyBindings.ReplaceCommands(Key.PageDown, Command.PageDown);
            KeyBindings.ReplaceCommands(Key.Home, Command.Start);
            KeyBindings.ReplaceCommands(Key.End, Command.End);

            mainWidth = snapshot.MainWidth;
            minimumContentWidth = mainWidth + (mainWidth + 3) / 4;
            var palette = new Palette(GetAttributeForRole(VisualRole.Normal));
            SetScheme(palette.BaseScheme);
            extraRows = snapshot.ExtraRows.Length == 0 ? [] : [PioneerDisplayLine.Rule, .. snapshot.ExtraRows];

            var importantHeight = Math.Max(5, WrappedHeight(snapshot.ImportantRows, mainWidth - 4) + 2);
            var scenarioY = HeaderHeight + importantHeight;
            var scenarioExtraHeight = snapshot.ScenarioRows.Length == 0
                ? 0
                : WrappedHeight(snapshot.ScenarioRows, mainWidth - 4) + 2;
            var trainingY = scenarioY + ScenarioHeight + scenarioExtraHeight;
            var trainingWidth = mainWidth / 5;
            var trainingInnerWidth = trainingWidth - 4;
            var trainingHeight = snapshot.TrainingCards.Max(card =>
                WrappedHeight([card.Title], trainingInnerWidth) + WrappedHeight(card.Rows, trainingInnerWidth) + 3);
            mainHeight = trainingY + trainingHeight;

            var main = new View
            {
                Id = "pioneer-main",
                Width = mainWidth,
                Height = mainHeight
            };
            main.SetScheme(palette.BaseScheme);
            AddPanelFrames(main, snapshot.Headers, palette, 0, HeaderHeight, "header");
            main.Add(CreateFrame("pioneer-important", 0, HeaderHeight, mainWidth, importantHeight,
                snapshot.ImportantRows, palette, wordWrap: true));
            AddPanelFrames(main, snapshot.ScenarioPanels, palette, scenarioY, ScenarioHeight, "scenario");
            if (scenarioExtraHeight != 0)
                main.Add(CreateFrame("pioneer-scenario-extra", 0, scenarioY + ScenarioHeight, mainWidth,
                    scenarioExtraHeight, snapshot.ScenarioRows, palette, wordWrap: true));

            for (var i = 0; i < snapshot.TrainingCards.Length; i++)
            {
                var card = snapshot.TrainingCards[i];
                var frame = new FrameView
                {
                    Id = $"pioneer-training:{card.TrainIndex}",
                    X = i * trainingWidth,
                    Y = trainingY,
                    Width = trainingWidth,
                    Height = trainingHeight,
                    BorderStyle = LineStyle.Single
                };
                frame.SetScheme(palette.BaseScheme);
                if (card.Highlighted)
                    frame.Border.GetOrCreateView().SetScheme(new Scheme(palette.Attribute(PioneerDisplayColor.LightGreen)));

                var titleWidth = Math.Min(trainingInnerWidth, card.Title.Text.GetColumns());
                var y = WrappedHeight([card.Title], trainingInnerWidth);
                frame.Add(CreateTextView([card.Title], palette, true,
                    Pos.Align(Alignment.Center), 0, titleWidth, y));
                frame.Add(new Line { X = 1, Y = y++, Width = trainingInnerWidth });
                foreach (var row in card.Rows)
                {
                    if (row.IsRule)
                    {
                        frame.Add(new Line { X = 1, Y = y++, Width = trainingInnerWidth });
                        continue;
                    }
                    var height = WrappedHeight([row], trainingInnerWidth);
                    frame.Add(CreateTextView([row], palette, true, 1, y, trainingInnerWidth, height));
                    y += height;
                }
                main.Add(frame);
            }

            extras = CreateFrame("pioneer-extras", mainWidth, 0, minimumContentWidth - mainWidth,
                WrappedHeight(extraRows, minimumContentWidth - mainWidth - 4) + 2, extraRows, palette, wordWrap: true);
            Add(main, extras);
        }

        protected override void OnSubViewLayout(LayoutEventArgs args)
        {
            var frameWidth = Math.Max(1, Frame.Width);
            var frameHeight = Math.Max(1, Frame.Height);
            var contentWidth = Math.Max(frameWidth, minimumContentWidth);
            var visibleHeight = Math.Max(1, frameHeight - (contentWidth > frameWidth ? 1 : 0));
            var extraHeight = WrappedHeight(extraRows, contentWidth - mainWidth - 4) + 2;
            var contentHeight = Math.Max(visibleHeight, Math.Max(mainHeight, extraHeight));
            var contentSize = new Size(contentWidth, contentHeight);
            if (GetContentSize() != contentSize)
                SetContentSize(contentSize);
            extras.Width = contentWidth - mainWidth;
            extras.Height = extraHeight;

            var maxX = Math.Max(0, contentWidth - Math.Max(1, Viewport.Width));
            var maxY = Math.Max(0, contentHeight - Math.Max(1, Viewport.Height));
            if (Viewport.X > maxX || Viewport.Y > maxY)
                Viewport = new Rectangle(Math.Min(Viewport.X, maxX), Math.Min(Viewport.Y, maxY),
                    Viewport.Width, Viewport.Height);
            base.OnSubViewLayout(args);
        }

        internal bool Scroll(Command command)
        {
            var amount = command switch
            {
                Command.PageUp => -Math.Max(1, Viewport.Height),
                Command.PageDown => Math.Max(1, Viewport.Height),
                Command.Start => -GetContentSize().Height,
                Command.End => GetContentSize().Height,
                _ => 0
            };
            if (amount == 0)
                return false;
            ScrollVertical(amount);
            return true;
        }

        void AddPanelFrames(View main, PanelSnapshot[] panels, Palette palette, int y, int height, string area)
        {
            var x = 0;
            var ratioTotal = panels.Sum(panel => panel.Ratio);
            var ratioUsed = 0;
            for (var i = 0; i < panels.Length; i++)
            {
                ratioUsed += panels[i].Ratio;
                var nextX = i == panels.Length - 1 ? mainWidth : mainWidth * ratioUsed / ratioTotal;
                main.Add(CreateFrame($"pioneer-{area}:{panels[i].Key}", x, y, nextX - x, height,
                    [panels[i].Line], palette, wordWrap: false));
                x = nextX;
            }
        }

        static int WrappedHeight(IEnumerable<PioneerDisplayLine> rows, int width)
            => rows.Sum(row => row.IsRule || row.Text.Length == 0
                ? 1
                : TextFormatter.WordWrapText(row.Text, width).Count());

        static FrameView CreateFrame(string id, Pos x, Pos y, Dim width, Dim height,
            IReadOnlyList<PioneerDisplayLine> rows, Palette palette, bool wordWrap)
        {
            var frame = new FrameView
            {
                Id = id,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                BorderStyle = LineStyle.Single
            };
            frame.SetScheme(palette.BaseScheme);
            var leadingRule = rows is [{ IsRule: true }, ..];
            if (leadingRule)
                frame.Add(new Line { X = 1, Y = 0, Width = Dim.Fill(1) });
            frame.Add(CreateTextView(leadingRule ? [.. rows.Skip(1)] : rows,
                palette, wordWrap, 1, leadingRule ? 1 : 0, Dim.Fill(1), Dim.Fill()));
            return frame;
        }

        static TextView CreateTextView(IReadOnlyList<PioneerDisplayLine> rows, Palette palette, bool wordWrap,
            Pos x, Pos y, Dim width, Dim height)
        {
            var text = new StyledTextView(palette)
            {
                X = x,
                Y = y,
                Width = width,
                Height = height,
                ReadOnly = true,
                CanFocus = false,
                WordWrap = wordWrap
            };
            text.Load([.. rows.Select(row => row.Segments
                .SelectMany(segment => Cell.ToCellList(segment.Text, palette.Attribute(segment.Color))).ToList())]);
            return text;
        }

        sealed class StyledTextView : TextView
        {
            readonly TAttribute normal;

            internal StyledTextView(Palette palette)
            {
                normal = palette.Normal;
                SetScheme(palette.BaseScheme);
            }

            protected override void OnDrawNormalColor(List<Cell> line, int idxCol, int idxRow)
                => SetAttribute(line[idxCol].Attribute ?? normal);
            protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
                => SetAttribute(line[idxCol].Attribute ?? normal);
            protected override void OnDrawUsedColor(List<Cell> line, int idxCol, int idxRow)
                => SetAttribute(line[idxCol].Attribute ?? normal);
        }
    }

    sealed class Palette
    {
        internal Palette(TAttribute normal)
        {
            Normal = new(normal.Foreground, TColor.Black, normal.Style);
            BaseScheme = new Scheme { Normal = Normal, ReadOnly = Normal, Focus = Normal };
        }

        internal TAttribute Normal { get; }
        internal Scheme BaseScheme { get; }

        internal TAttribute Attribute(PioneerDisplayColor color)
            => new(color switch
            {
                PioneerDisplayColor.Cyan or PioneerDisplayColor.Aqua => new TColor(StandardColor.BrightCyan),
                PioneerDisplayColor.Green => new TColor(StandardColor.Green),
                PioneerDisplayColor.Yellow => new TColor(StandardColor.BrightYellow),
                PioneerDisplayColor.Red => new TColor(StandardColor.BrightRed),
                PioneerDisplayColor.DarkOrange => new TColor(StandardColor.DarkOrange),
                PioneerDisplayColor.Lime => new TColor(StandardColor.BrightGreen),
                PioneerDisplayColor.LightGreen => new TColor(StandardColor.LightGreen),
                PioneerDisplayColor.Teal => new TColor(StandardColor.Cyan),
                _ => Normal.Foreground
            }, TColor.Black, Normal.Style);
    }
}
