using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.ViewModels;

namespace IotExpertApp.Views;

public sealed class KnowledgeEditorView : UserControl {
    public KnowledgeEditorView() {
        StackPanel panel = new StackPanel { Spacing = 8, Margin = new Thickness(8) };
        panel.Children.Add(Label("Редактор рабочей копии. Поставляемый образец сначала скопируйте. Активная консультация не меняется.", true));
        panel.Children.Add(Input(nameof(KnowledgeEditorViewModel.SourcePath), "Файл для загрузки"));
        panel.Children.Add(Input(nameof(KnowledgeEditorViewModel.WorkingCopyPath), "Новый путь рабочей копии (существующий файл не затирается)"));
        panel.Children.Add(Buttons(("Загрузить", nameof(KnowledgeEditorViewModel.LoadCommand)), ("Создать рабочую копию", nameof(KnowledgeEditorViewModel.CreateCopyCommand))));
        panel.Children.Add(Bound(nameof(KnowledgeEditorViewModel.Information)));
        panel.Children.Add(Bound(nameof(KnowledgeEditorViewModel.Message), true));
        StackPanel editor = new StackPanel { Spacing = 8 };
        Bind(editor, IsEnabledProperty, nameof(KnowledgeEditorViewModel.CanEdit));
        panel.Children.Add(editor);
        editor.Children.Add(Label("Версия БЗ для следующего сохранения"));
        editor.Children.Add(Input(nameof(KnowledgeEditorViewModel.VersionText), "Версия"));
        ComboBox rules = Choice(nameof(KnowledgeEditorViewModel.RuleIds), nameof(KnowledgeEditorViewModel.SelectedRuleId));
        // после обработки выбора возвращаем список к принятому моделью ID, если непримененные поля запретили переход
        rules.SelectionChanged += (_, _) => Dispatcher.UIThread.Post(() => {
            if (rules.DataContext is KnowledgeEditorViewModel model && !object.Equals(rules.SelectedItem, model.SelectedRuleId))
                rules.SelectedItem = model.SelectedRuleId;
        });
        editor.Children.Add(rules);
        editor.Children.Add(Buttons(("Новое правило", nameof(KnowledgeEditorViewModel.NewRuleCommand)), ("Удалить правило", nameof(KnowledgeEditorViewModel.DeleteRuleCommand)), ("Отменить изменение", nameof(KnowledgeEditorViewModel.UndoCommand))));
        TextBox id = Input("Draft.Id", "ID нового правила");
        Bind(id, TextBox.IsReadOnlyProperty, nameof(KnowledgeEditorViewModel.IsExistingRule));
        editor.Children.Add(id);
        editor.Children.Add(Input("Draft.Title", "Название правила"));
        editor.Children.Add(Input("Draft.PriorityText", "Приоритет Int32; больший срабатывает раньше"));
        editor.Children.Add(Input("Draft.Explanation", "Объяснение правила"));
        editor.Children.Add(Input("Draft.Provenance", "Происхождение и основание знания"));
        editor.Children.Add(Label("Условие: выберите узел дерева; И/ИЛИ содержат дочерние узлы, сравнение — признак, оператор и типизированное значение", true));
        TreeView tree = new TreeView {
            Height = 240,
            ItemTemplate = new FuncTreeDataTemplate<ConditionDraft>((node, _) => {
                TextBlock label = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 850 };
                if (node is not null) { label.DataContext = node; Bind(label, TextBlock.TextProperty, nameof(ConditionDraft.Label)); }
                return label;
            }, node => node is null ? ImmutableArray<ConditionDraft>.Empty : (System.Collections.IEnumerable)node.Children)
        };
        Bind(tree, ItemsControl.ItemsSourceProperty, nameof(KnowledgeEditorViewModel.ConditionRoots));
        Bind(tree, TreeView.SelectedItemProperty, nameof(KnowledgeEditorViewModel.SelectedNode), BindingMode.TwoWay);
        editor.Children.Add(tree);
        editor.Children.Add(Buttons(("Добавить сравнение", nameof(KnowledgeEditorViewModel.AddComparisonCommand)), ("Добавить И", nameof(KnowledgeEditorViewModel.AddAllCommand)), ("Добавить ИЛИ", nameof(KnowledgeEditorViewModel.AddAnyCommand)), ("Удалить узел", nameof(KnowledgeEditorViewModel.RemoveNodeCommand))));
        editor.Children.Add(Buttons(("Обернуть корень в И", nameof(KnowledgeEditorViewModel.WrapAllCommand)), ("Обернуть корень в ИЛИ", nameof(KnowledgeEditorViewModel.WrapAnyCommand))));
        StackPanel comparison = new StackPanel { Spacing = 6 };
        Bind(comparison, IsEnabledProperty, nameof(KnowledgeEditorViewModel.IsComparisonSelected));
        comparison.Children.Add(Choice(nameof(KnowledgeEditorViewModel.ConditionFactIds), "SelectedNode.FactId"));
        comparison.Children.Add(Choice(nameof(KnowledgeEditorViewModel.Operations), "SelectedNode.Operation"));
        comparison.Children.Add(Input("SelectedNode.ValueText", "Значение; enum без кавычек, bool true/false, число с точкой"));
        comparison.Children.Add(Bound(nameof(KnowledgeEditorViewModel.ValueHint)));
        editor.Children.Add(comparison);
        editor.Children.Add(Label("Заключения: запись разрешена только в derived/goal; типы проверяются по словарю", true));
        ComboBox conclusions = new ComboBox {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<ConclusionDraft>((item, _) => {
                StackPanel row = new StackPanel();
                if (item is not null) {
                    row.DataContext = item;
                    row.Children.Add(Bound(nameof(ConclusionDraft.FactId)));
                    row.Children.Add(Bound(nameof(ConclusionDraft.ValueText)));
                }
                return row;
            })
        };
        Bind(conclusions, ItemsControl.ItemsSourceProperty, "Draft.Conclusions");
        Bind(conclusions, ComboBox.SelectedItemProperty, nameof(KnowledgeEditorViewModel.SelectedConclusion), BindingMode.TwoWay);
        editor.Children.Add(conclusions);
        editor.Children.Add(Choice(nameof(KnowledgeEditorViewModel.ConclusionFactIds), "SelectedConclusion.FactId"));
        editor.Children.Add(Input("SelectedConclusion.ValueText", "Значение заключения"));
        editor.Children.Add(Bound(nameof(KnowledgeEditorViewModel.ConclusionHint)));
        editor.Children.Add(Buttons(("Добавить заключение", nameof(KnowledgeEditorViewModel.AddConclusionCommand)), ("Удалить заключение", nameof(KnowledgeEditorViewModel.RemoveConclusionCommand))));
        editor.Children.Add(Bound(nameof(KnowledgeEditorViewModel.PendingDescription)));
        editor.Children.Add(Buttons(("Применить поля к черновику", nameof(KnowledgeEditorViewModel.ApplyCommand)), ("Сбросить поля правила", nameof(KnowledgeEditorViewModel.ResetRuleCommand))));
        editor.Children.Add(Buttons(("Сохранить файл", nameof(KnowledgeEditorViewModel.SaveCommand)), ("Сбросить весь черновик", nameof(KnowledgeEditorViewModel.DiscardCommand)), ("Для следующих консультаций", nameof(KnowledgeEditorViewModel.UseForConsultationCommand))));
        Content = new ScrollViewer { Content = panel };
    }

    private static WrapPanel Buttons(params (string Label, string Command)[] items) {
        WrapPanel panel = new WrapPanel();
        foreach ((string label, string command) in items) {
            Button button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
            Bind(button, Avalonia.Controls.Button.CommandProperty, command);
            panel.Children.Add(button);
        }
        return panel;
    }
    private static TextBlock Label(string value, bool bold = false) => new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
    private static TextBlock Bound(string path, bool bold = false) { TextBlock text = Label("", bold); Bind(text, TextBlock.TextProperty, path); return text; }
    private static TextBox Input(string path, string hint) { TextBox input = new TextBox { PlaceholderText = hint }; Bind(input, TextBox.TextProperty, path, BindingMode.TwoWay); return input; }
    private static ComboBox Choice(string items, string selected) {
        ComboBox box = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        Bind(box, ItemsControl.ItemsSourceProperty, items); Bind(box, ComboBox.SelectedItemProperty, selected, BindingMode.TwoWay); return box;
    }
    private static void Bind(Control control, AvaloniaProperty property, string path, BindingMode mode = BindingMode.OneWay) => control.Bind(property, new ReflectionBinding(path) { Mode = mode });
}
