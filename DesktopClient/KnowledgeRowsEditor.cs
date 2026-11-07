using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QNAgent;

// Each visible row owns its inputs; deletion and loading cannot leave stale bound cells.
internal sealed class KnowledgeRowsEditor : StackPanel
{
    private readonly string[] _labels;
    private readonly bool _table;
    private readonly Action _changed;
    private readonly StackPanel _rows = new();
    private readonly List<TextBox[]> _inputs = new();
    internal KnowledgeRowsEditor(string addLabel, string[] labels, bool table, Action changed)
    {
        _labels = labels; _table = table; _changed = changed;
        Children.Add(_rows);
        var add = ActionButton(addLabel); add.Click += (_, _) => Add(new string[labels.Length]); Children.Add(add);
    }
    private static Button ActionButton(string label) => new()
    {
        Content = label, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 6, 8, 6),
        HorizontalAlignment = HorizontalAlignment.Left, Foreground = new SolidColorBrush(Color.FromRgb(23, 107, 206))
    };
    internal void Load(IEnumerable<string[]> rows)
    {
        _rows.Children.Clear(); _inputs.Clear();
        foreach (var row in rows) Add(row);
    }
    internal string[][] Values() => _inputs.Select(row => row.Select(input => input.Text.Trim()).ToArray()).ToArray();
    internal TextBox[] Add(params string[] values)
    {
        var content = new Grid();
        if (_table)
        {
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        var inputs = new TextBox[_labels.Length];
        for (var i = 0; i < _labels.Length; i++)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 10, 8) };
            cell.Children.Add(new TextBlock { Text = _labels[i], Margin = new Thickness(0, 0, 0, 5), TextWrapping = TextWrapping.Wrap });
            var input = new TextBox
            {
                Text = i < values.Length ? values[i] ?? "" : "", Padding = new Thickness(8), MinHeight = 36,
                AcceptsReturn = !_table, TextWrapping = TextWrapping.Wrap, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            input.TextChanged += (_, _) => _changed(); inputs[i] = input; cell.Children.Add(input);
            if (_table) Grid.SetColumn(cell, i);
            else { content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetRow(cell, i); }
            content.Children.Add(cell);
        }
        var border = new Border
        {
            Child = content, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(12), Margin = new Thickness(0, 6, 0, 6)
        };
        var remove = ActionButton("删除"); remove.VerticalAlignment = VerticalAlignment.Top;
        if (_table) { Grid.SetColumn(remove, _labels.Length); remove.Margin = new Thickness(0, 24, 0, 0); }
        else { content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetRow(remove, _labels.Length); }
        remove.Click += (_, _) => { _rows.Children.Remove(border); _inputs.Remove(inputs); _changed(); };
        content.Children.Add(remove); _inputs.Add(inputs); _rows.Children.Add(border); _changed();
        return inputs;
    }
}
