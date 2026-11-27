using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CoreShift.Desktop;

internal sealed record WebNode(
    string Id, string Kind, string? Text = null, string? Style = null, string? Label = null,
    bool Disabled = false, bool Checked = false, bool Selected = false, int Index = 0,
    int Column = 0, int Row = 0, int ColumnSpan = 0, int Columns = 0, string? Widths = null,
    double Value = 0, double Maximum = 0, double Width = 0, double Height = 0,
    string[]? Options = null, WebNode[]? Children = null, string? Name = null);

// The web shell uses the same commands, view state and validation as the native shell.
internal sealed class WebControlProjection(Window owner)
{
    private sealed record ItemView(long Id, FrameworkElement? Content);
    private readonly ConditionalWeakTable<ItemsControl, ConditionalWeakTable<object, ItemView>> views = new();
    private readonly Dictionary<string, Action<JsonElement>> commands = new(StringComparer.Ordinal);
    private long nextItemId;

    internal void Begin() => commands.Clear();

    internal void Register(string id, Action action) => commands[id] = _ => action();

    internal bool Invoke(string id, JsonElement value)
    {
        if (!commands.TryGetValue(id, out var command))
            return false;
        command(value);
        return true;
    }

    internal WebNode[] Project(string id, params object?[] elements) => Content(id, elements, true);

    private WebNode[] Content(string id, IEnumerable<object?> content, bool enabled)
    {
        var nodes = new List<WebNode>();
        int index = 0;
        foreach (object? item in content)
        {
            var node = Read(item, $"{id}.{index++}", enabled);
            if (node is not null)
                nodes.Add(node);
        }
        return nodes.ToArray();
    }

    private WebNode? Read(object? content, string id, bool enabled)
    {
        if (content is string text)
            return string.IsNullOrWhiteSpace(text) ? null : new WebNode(id, "text", Text: text);
        if (content is not FrameworkElement element || element.Visibility != Visibility.Visible)
            return null;

        enabled &= element.IsEnabled;
        string? name = element.Name.Length > 0 ? element.Name : null;
        string? label = GetLabel(element);
        WebNode? node = element switch
        {
            TextBlock block => Text(id, block),
            Label caption => new(id, "label", Text: Caption(caption.Content), Label: (caption.Target as FrameworkElement)?.Name),
            Expander expander => Expand(id, expander, enabled),
            TabControl tabs => Tabs(id, tabs, enabled),
            ComboBox picker => Select(id, picker, enabled),
            TextBox field => Field(id, field, enabled),
            RadioButton or CheckBox => Toggle(id, (ToggleButton)element, enabled),
            ButtonBase button => Button(id, button, enabled),
            ItemsControl items => Items(id, items, enabled),
            ProgressBar progress => new(id, "progress", Style: progress.IsIndeterminate ? "pending" : null,
                Label: label, Value: progress.Value - progress.Minimum, Maximum: progress.Maximum - progress.Minimum),
            ScrollViewer scroll => Container(id, "scroll", scroll.Content, enabled, height: LimitHeight(scroll)),
            Border border => Container(id, IsCard(border) ? "card" : "panel", border.Child, enabled),
            Viewbox viewbox => Container(id, "panel", viewbox.Child, enabled),
            ContentControl control => Container(id, "panel", control.Content, enabled),
            Canvas canvas => Chart(id, canvas),
            Grid grid => GridContent(id, grid, enabled),
            UniformGrid tileGrid => new(id, "tiles", Columns: tileGrid.Columns, Children: Content(id, tileGrid.Children.Cast<object>(), enabled)),
            StackPanel stack => new(id, stack.Orientation == Orientation.Horizontal ? "row" : "stack",
                Children: Content(id, stack.Children.Cast<object>(), enabled)),
            WrapPanel wrap => new(id, "row", Children: Content(id, wrap.Children.Cast<object>(), enabled)),
            DockPanel dock => new(id, "dock", Children: DockContent(id, dock, enabled)),
            Panel panel => new(id, "stack", Children: Content(id, panel.Children.Cast<object>(), enabled)),
            _ => null
        };
        if (node is null || node.Children is { Length: 0 } && node.Kind is "stack" or "panel" or "dock" or "row")
            return null;
        string? dockSide = element.Parent is DockPanel ? DockPanel.GetDock(element).ToString().ToLowerInvariant() : null;
        return node with
        {
            Column = Grid.GetColumn(element),
            Row = Grid.GetRow(element),
            ColumnSpan = Grid.GetColumnSpan(element),
            Name = name,
            Label = node.Label ?? label ?? (node.Kind is "input" or "select" ? name : null),
            Style = node.Style ?? (dockSide == "right" ? "end" : null)
        };
    }

    private WebNode[] DockContent(string id, DockPanel dock, bool enabled) =>
        Content(id, dock.Children.Cast<UIElement>().OrderBy(child => DockPanel.GetDock(child) == Dock.Right), enabled);

    private WebNode Text(string id, TextBlock block)
    {
        string style = IsStyle(block, "PageTitle") ? "title" : IsStyle(block, "SectionTitle") ? "heading" :
            IsStyle(block, "MetricValue") ? "metric" : IsStyle(block, "SecondaryText") ? "muted" :
            block.FontWeight >= FontWeights.SemiBold ? "strong" : "body";
        return new(id, "text", Text: block.Text, Style: style);
    }

    private WebNode Expand(string id, Expander expander, bool enabled)
    {
        if (enabled)
            commands[id] = _ =>
            {
                expander.SetCurrentValue(Expander.IsExpandedProperty, !expander.IsExpanded);
                expander.GetBindingExpression(Expander.IsExpandedProperty)?.UpdateSource();
            };
        return new(id, "expander", Text: Caption(expander.Header), Disabled: !enabled, Checked: expander.IsExpanded,
            Children: expander.IsExpanded ? Content(id, [expander.Content], enabled) : null);
    }

    private WebNode Tabs(string id, TabControl tabs, bool enabled)
    {
        AddIndexCommand(id, tabs, enabled);
        return new(id, "tabs", Disabled: !enabled, Index: tabs.SelectedIndex,
            Options: tabs.Items.OfType<TabItem>().Select(item => Caption(item.Header)).ToArray(),
            Children: Content(id, [tabs.SelectedContent], enabled));
    }

    private WebNode Select(string id, ComboBox picker, bool enabled)
    {
        if (enabled)
        {
            commands[id] = value =>
            {
                if (!TryIndex(value, picker, out int index) || !picker.IsEnabled)
                    return;
                if (picker.Items[index] is ComboBoxItem { IsEnabled: false })
                    return;
                picker.SelectedIndex = index;
            };
        }

        return new(id, "select", Disabled: !enabled, Index: picker.SelectedIndex,
            Label: GetLabel(picker) ?? NameFromLabel(picker),
            Options: picker.Items.Cast<object>().Select(DisplayName).ToArray(),
            Children: picker.Items.Cast<object>().Select((item, index) => new WebNode($"{id}.o{index}", "option",
                Disabled: item is ComboBoxItem { IsEnabled: false })).ToArray());
    }

    private WebNode Field(string id, TextBox field, bool enabled)
    {
        if (field.IsReadOnly)
            return new(id, "output", Text: field.Text, Height: LimitHeight(field));
        if (enabled)
        {
            commands[id] = value =>
            {
                if (value.ValueKind != JsonValueKind.String || !field.IsEnabled)
                    return;
                string text = value.GetString() ?? "";
                if (text.Length <= (field.MaxLength > 0 ? field.MaxLength : 1024))
                    field.Text = text;
            };
        }
        return new(id, "input", Text: field.Text, Disabled: !enabled, Label: GetLabel(field) ?? NameFromLabel(field),
            Maximum: field.MaxLength);
    }

    private WebNode Toggle(string id, ToggleButton button, bool enabled)
    {
        if (enabled)
        {
            commands[id] = value =>
            {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || !button.IsEnabled)
                    return;
                bool selected = value.GetBoolean();
                if (button is RadioButton && !selected)
                    return;
                button.SetCurrentValue(ToggleButton.IsCheckedProperty, selected);
                button.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
            };
        }
        return new(id, button is RadioButton ? "radio" : "switch", Text: Caption(button.Content), Label: GetLabel(button),
            Disabled: !enabled, Checked: button.IsChecked == true);
    }

    private WebNode? Button(string id, ButtonBase button, bool enabled)
    {
        string title = Caption(button.Content);
        if (title.Length == 0)
            title = GetLabel(button) ?? "";
        if (title.Length == 0)
            return null;
        if (enabled)
            commands[id] = _ => { if (button.IsEnabled) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button)); };
        return new(id, "button", Text: title, Style: IsStyle(button, "PrimaryButton") ? "primary" : null,
            Disabled: !enabled);
    }

    private WebNode Items(string id, ItemsControl list, bool enabled)
    {
        var cache = views.GetValue(list, _ => new ConditionalWeakTable<object, ItemView>());
        var rows = new List<WebNode>(list.Items.Count);
        bool selectable = list is ListBox { SelectionMode: SelectionMode.Single } &&
            list.Name is "MemoryProcesses" or "IndexExclusionsList";
        int index = 0;
        foreach (object item in list.Items)
        {
            var view = cache.GetValue(item, record => CreateItem(list, record));
            string key = $"{id}.i{view.Id}.{index}";
            var row = view.Content is { } content ? Read(content, key + ".v", enabled) : Read(DisplayName(item), key + ".v", enabled);
            if (row is not null)
            {
                bool selected = false;
                if (selectable && list is ListBox choices)
                {
                    int selection = index;
                    selected = choices.SelectedIndex == selection;
                    if (enabled)
                        commands[key] = _ => { if (choices.IsEnabled) choices.SelectedIndex = selection; };
                }
                rows.Add(new WebNode(key, selectable ? "choice" : "item", Selected: selected,
                    Disabled: !enabled, Children: [row]));
            }
            index++;
        }
        return new(id, "items", Label: GetLabel(list), Height: LimitHeight(list), Children: rows.ToArray());
    }

    private ItemView CreateItem(ItemsControl control, object item)
    {
        var content = control.ItemTemplate?.LoadContent() as FrameworkElement;
        if (content is not null)
            BindDetachedItem(content, item);
        return new ItemView(++nextItemId, content);
    }

    private static void BindDetachedItem(FrameworkElement element, object item)
    {
        // Detached template roots have no presenter to propagate the item's data context.
        if (element.ReadLocalValue(FrameworkElement.DataContextProperty) == DependencyProperty.UnsetValue)
            element.DataContext = item;
        foreach (FrameworkElement child in LogicalTreeHelper.GetChildren(element).OfType<FrameworkElement>())
            BindDetachedItem(child, element.DataContext);
    }

    private WebNode GridContent(string id, Grid grid, bool enabled)
    {
        string? widths = grid.ColumnDefinitions.Count > 1
            ? string.Join(' ', grid.ColumnDefinitions.Select(column => column.Width.IsStar ? $"{column.Width.Value.ToString(CultureInfo.InvariantCulture)}fr" :
                column.Width.IsAuto ? "auto" : column.Width.Value < 24 ? "0px" : $"{column.Width.Value:0}px"))
            : null;
        return new(id, widths is null ? "stack" : "grid", Widths: widths,
            Children: Content(id, grid.Children.Cast<object>(), enabled));
    }

    private static WebNode? Chart(string id, Canvas canvas)
    {
        var lines = canvas.Children.OfType<Shape>().Where(shape => !shape.RenderedGeometry.IsEmpty()).Select((shape, index) =>
            new WebNode($"{id}.{index}", "line", Text: shape is Path path ? path.Data?.ToString(CultureInfo.InvariantCulture) :
                shape.RenderedGeometry.ToString(CultureInfo.InvariantCulture),
                Style: shape.Stroke is SolidColorBrush brush ? brush.Color.ToString() : null, Value: shape.StrokeThickness)).ToArray();
        return lines.Length > 0 && canvas.ActualWidth > 0 && canvas.ActualHeight > 0
            ? new(id, "chart", Width: canvas.ActualWidth, Height: canvas.ActualHeight, Children: lines) : null;
    }

    private WebNode Container(string id, string kind, object? child, bool enabled, double height = 0) =>
        new(id, kind, Height: height, Children: Content(id, [child], enabled));

    private static double LimitHeight(FrameworkElement element) =>
        double.IsFinite(element.MaxHeight) ? element.MaxHeight : double.IsFinite(element.Height) ? element.Height : 0;

    private void AddIndexCommand(string id, Selector selector, bool enabled)
    {
        if (enabled)
            commands[id] = value => { if (TryIndex(value, selector, out int index) && selector.IsEnabled) selector.SelectedIndex = index; };
    }

    private static bool TryIndex(JsonElement value, Selector selector, out int index)
    {
        index = -1;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out index) && index >= 0 && index < selector.Items.Count;
    }

    private bool IsStyle(FrameworkElement element, string key) =>
        owner.TryFindResource(key) is Style style && ReferenceEquals(element.Style, style);

    private bool IsCard(Border border) => IsStyle(border, "Card") || IsStyle(border, "PolicyCard") || IsStyle(border, "ComponentTile");

    private static string? GetLabel(DependencyObject element) =>
        AutomationProperties.GetName(element) is { Length: > 0 } text ? text : null;

    private static string? NameFromLabel(FrameworkElement element)
    {
        if (element.Parent is not Panel parent)
            return null;
        return parent.Children.OfType<Label>().FirstOrDefault(label => ReferenceEquals(label.Target, element))
            is { } caption ? Caption(caption.Content) : null;
    }

    private static string DisplayName(object content)
    {
        if (content is ComboBoxItem picker)
            return Caption(picker.Content);
        var label = content.GetType().GetProperty("Label")?.GetValue(content) as string;
        return label ?? Caption(content);
    }

    private static string Caption(object? content) => content switch
    {
        null => "",
        string value => value,
        TextBlock text => text.Text,
        Panel panel => string.Join(" · ", panel.Children.Cast<object>().Select(Caption).Where(text => text.Length > 0)),
        Border border => Caption(border.Child),
        ContentControl control => Caption(control.Content),
        FrameworkElement element => GetLabel(element) ?? "",
        _ => content.ToString() ?? ""
    };
}
