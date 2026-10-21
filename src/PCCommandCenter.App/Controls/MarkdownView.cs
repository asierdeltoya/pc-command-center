using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCCommandCenter.Core;

namespace PCCommandCenter.App.Controls;

public sealed record CodeBlock(string Language, string Code);

/// <summary>
/// Minimal Markdown renderer for assistant replies: headings, paragraphs, lists, quotes, inline code, bold/italic
/// and fenced code blocks with Copy / Run-in-terminal actions. Re-rendering is throttled while text streams in.
/// </summary>
public sealed partial class MarkdownView : ContentControl
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata("", (d, _) => ((MarkdownView)d).ScheduleRender()));
    public static readonly DependencyProperty RunCodeCommandProperty = DependencyProperty.Register(nameof(RunCodeCommand), typeof(ICommand), typeof(MarkdownView));
    public static readonly DependencyProperty CopyCodeCommandProperty = DependencyProperty.Register(nameof(CopyCodeCommand), typeof(ICommand), typeof(MarkdownView));

    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    public ICommand? RunCodeCommand { get => (ICommand?)GetValue(RunCodeCommandProperty); set => SetValue(RunCodeCommandProperty, value); }
    public ICommand? CopyCodeCommand { get => (ICommand?)GetValue(CopyCodeCommandProperty); set => SetValue(CopyCodeCommandProperty, value); }

    private readonly DispatcherTimer _throttle;
    private readonly StackPanel _root = new();

    public MarkdownView()
    {
        Content = _root;
        _throttle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(90) };
        _throttle.Tick += (_, _) => { _throttle.Stop(); Render(); };
        Loaded += (_, _) => Render();
    }

    private void ScheduleRender()
    {
        if (!IsLoaded) return;
        if (!_throttle.IsEnabled) _throttle.Start();
    }

    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`|\*[^*\s][^*]*\*|_[^_\s][^_]*_)")]
    private static partial Regex InlineRegex();

    [GeneratedRegex(@"^\s*(\d+)[.)]\s+(.*)$")]
    private static partial Regex NumberedRegex();

    private void Render()
    {
        _root.Children.Clear();
        var lines = (Markdown ?? "").Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            _root.Children.Add(Paragraph(string.Join(" ", paragraph), 13.5, FontWeights.Normal, new Thickness(0, 2, 0, 8)));
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```"))
            {
                FlushParagraph();
                var lang = trimmed[3..].Trim();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].Trim().StartsWith("```")) code.Add(lines[i++]);
                _root.Children.Add(CodeBlockView(lang, string.Join("\n", code)));
                continue;
            }
            if (trimmed.Length == 0) { FlushParagraph(); continue; }
            if (trimmed.StartsWith('#'))
            {
                FlushParagraph();
                int level = trimmed.TakeWhile(ch => ch == '#').Count();
                var text = trimmed[level..].Trim();
                _root.Children.Add(Paragraph(text, level <= 1 ? 17 : level == 2 ? 15.5 : 14, FontWeights.SemiBold, new Thickness(0, 10, 0, 6)));
                continue;
            }
            if (trimmed is "---" or "***")
            {
                FlushParagraph();
                _root.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8), Background = (Brush)FindResource("Brush.Border") });
                continue;
            }
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("• "))
            {
                FlushParagraph();
                int indent = line.Length - line.TrimStart().Length;
                _root.Children.Add(ListItem("•", trimmed[2..], indent));
                continue;
            }
            var num = NumberedRegex().Match(line);
            if (num.Success)
            {
                FlushParagraph();
                _root.Children.Add(ListItem(num.Groups[1].Value + ".", num.Groups[2].Value, 0));
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = Paragraph(trimmed.TrimStart('>').Trim(), 13.5, FontWeights.Normal, new Thickness(0));
                quote.Foreground = (Brush)FindResource("Brush.TextSecondary");
                _root.Children.Add(new Border
                {
                    BorderBrush = (Brush)FindResource("Brush.Accent"), BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 4, 0, 4), Margin = new Thickness(0, 2, 0, 8), Child = quote,
                });
                continue;
            }
            if (trimmed.StartsWith('|'))
            {
                FlushParagraph();
                if (Regex.IsMatch(trimmed, @"^\|[\s:|-]+\|$")) continue; // table separator row
                var cells = trimmed.Trim('|').Split('|').Select(c => c.Trim());
                var row = Paragraph(string.Join("   ·   ", cells), 13, FontWeights.Normal, new Thickness(0, 1, 0, 1));
                _root.Children.Add(row);
                continue;
            }
            paragraph.Add(trimmed);
        }
        FlushParagraph();
    }

    private TextBlock Paragraph(string text, double size, FontWeight weight, Thickness margin)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            FontWeight = weight,
            Margin = margin,
            LineHeight = size * 1.5,
            TextTrimming = TextTrimming.None,
            Foreground = (Brush)FindResource("Brush.Text"),
        };
        AddInlines(tb.Inlines, text);
        return tb;
    }

    private void AddInlines(InlineCollection inlines, string text)
    {
        int last = 0;
        foreach (Match m in InlineRegex().Matches(text))
        {
            if (m.Index > last) inlines.Add(new Run(text[last..m.Index]));
            var token = m.Value;
            if (token.StartsWith("**")) inlines.Add(new Bold(new Run(token[2..^2])));
            else if (token.StartsWith('`'))
            {
                inlines.Add(new Run(" " + token[1..^1] + " ")
                {
                    FontFamily = (FontFamily)FindResource("Font.Mono"),
                    FontSize = 12.5,
                    Background = (Brush)FindResource("Brush.SurfaceAlt"),
                    Foreground = (Brush)FindResource("Brush.Accent"),
                });
            }
            else inlines.Add(new Italic(new Run(token[1..^1])));
            last = m.Index + m.Length;
        }
        if (last < text.Length) inlines.Add(new Run(text[last..]));
    }

    private FrameworkElement ListItem(string bullet, string text, int indent)
    {
        var grid = new Grid { Margin = new Thickness(6 + indent * 6, 1, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var b = new TextBlock { Text = bullet, Foreground = (Brush)FindResource("Brush.Accent"), FontSize = 13.5, FontWeight = FontWeights.SemiBold };
        var p = Paragraph(text, 13.5, FontWeights.Normal, new Thickness(0));
        Grid.SetColumn(p, 1);
        grid.Children.Add(b);
        grid.Children.Add(p);
        return grid;
    }

    private FrameworkElement CodeBlockView(string language, string code)
    {
        var block = new CodeBlock(string.IsNullOrWhiteSpace(language) ? "text" : language.ToLowerInvariant(), code.TrimEnd());
        bool runnable = block.Language is "powershell" or "ps" or "ps1" or "pwsh" or "cmd" or "bat" or "batch" or "shell" or "console";

        var header = new DockPanel { Margin = new Thickness(12, 6, 6, 0), LastChildFill = false };
        header.Children.Add(new TextBlock { Text = block.Language, FontSize = 11, Foreground = (Brush)FindResource("Brush.TextMuted"), VerticalAlignment = VerticalAlignment.Center });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        var copy = new Button { Content = Loc.T("Copy"), Style = (Style)FindResource("Btn.Ghost"), Padding = new Thickness(8, 3, 8, 3), FontSize = 11.5, Command = CopyCodeCommand, CommandParameter = block };
        Ui.SetGlyph(copy, "");
        actions.Children.Add(copy);
        if (runnable)
        {
            var run = new Button { Content = Loc.T("Run in terminal"), Style = (Style)FindResource("Btn.Ghost"), Padding = new Thickness(8, 3, 8, 3), FontSize = 11.5, Command = RunCodeCommand, CommandParameter = block };
            Ui.SetGlyph(run, "");
            actions.Children.Add(run);
        }
        header.Children.Add(actions);

        var codeText = new TextBox
        {
            Text = block.Code,
            IsReadOnly = true,
            FontFamily = (FontFamily)FindResource("Font.Mono"),
            FontSize = 12.5,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 10),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("Brush.Text"),
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(codeText);
        return new Border
        {
            Background = (Brush)FindResource("Brush.Bg"),
            BorderBrush = (Brush)FindResource("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 4, 0, 10),
            Child = stack,
        };
    }
}
