using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Services.Consultation;
using IotExpertApp.Services.Knowledge;
using IotExpertApp.ViewModels;

namespace IotExpertApp.ConsoleUI;

public static class KnowledgeEditorChecks {
    public static void Run() {
        string source = Path.Combine(AppContext.BaseDirectory, "Knowledge", "lr1-strategies.json");
        byte[] original = File.ReadAllBytes(source);
        DirectoryInfo temp = Directory.CreateTempSubdirectory("iot-editor-");
        try {
            string path = Path.Combine(temp.FullName, "working.json");
            KnowledgeWorkspace.CreateWorkingCopy(source, path);
            Invalid(() => KnowledgeWorkspace.CreateWorkingCopy(source, path), "копия не перезаписывает существующий файл");
            Check(File.ReadAllBytes(path).SequenceEqual(original), "точная рабочая копия");
            KnowledgeWorkspace workspace = KnowledgeWorkspace.Load(path);
            ConsultationService pinned = new ConsultationService(workspace.Knowledge);
            string oldHash = pinned.Knowledge.Sha256;
            RuleDraft rule = RuleDraftCodec.FromRule(workspace.Knowledge.Rules.Single(item => item.Id == "R-13"));
            rule.Title += " (editor check)";
            workspace.Apply("R-13", rule, "editor-check-1");
            Check(workspace.IsDirty && File.ReadAllBytes(path).SequenceEqual(original), "apply не пишет файл");
            workspace.Delete("R-20"); Check(workspace.Knowledge.Rules.Length == 37, "удаление в черновике");
            workspace.Undo(); Check(workspace.Knowledge.Rules.Length == 38 && workspace.Knowledge.Version == "editor-check-1", "undo удаления");
            KnowledgeBase before = workspace.Knowledge;
            rule.Id = "R-14"; Invalid(() => workspace.Apply("R-13", rule), "ID не меняется"); rule.Id = "R-13";
            rule.PriorityText = "2147483648"; Invalid(() => workspace.Apply("R-13", rule), "Int32 без переполнения"); rule.PriorityText = "100";
            rule.Root = new ConditionDraft(DraftConditionKind.Comparison) { FactId = "missing", ValueText = "true" };
            Invalid(() => workspace.Apply("R-13", rule), "неизвестный признак");
            rule.Root.FactId = "V1"; rule.Root.ValueText = "не знаю";
            Invalid(() => workspace.Apply("R-13", rule), "unknown не записывается как константа правила");
            Check(ReferenceEquals(before, workspace.Knowledge), "невалидные операции сохраняют весь черновик");
            string backup = workspace.Save();
            Check(File.ReadAllBytes(backup).SequenceEqual(original), "резервная копия исходных байтов");
            Check(new KnowledgeBaseService().Load(path).Version == "editor-check-1" && !workspace.IsDirty, "сохранение проверенного файла");
            Check(pinned.Knowledge.Sha256 == oldHash && workspace.Knowledge.Sha256 != oldHash, "активная консультация закреплена");
            rule = RuleDraftCodec.FromRule(workspace.Knowledge.Rules[0]); rule.Title += " external check";
            workspace.Apply(rule.Id, rule);
            byte[] external = File.ReadAllBytes(path).Concat(new byte[] { 32 }).ToArray();
            File.WriteAllBytes(path, external);
            Invalid(() => workspace.Save(), "защита от внешнего изменения");
            Check(File.ReadAllBytes(path).SequenceEqual(external) && workspace.IsDirty, "конфликт сохранения не затирает файл и черновик");

            KnowledgeWorkspace numeric = KnowledgeWorkspace.Load(Path.Combine(AppContext.BaseDirectory, "KnowledgeExamples", "synthetic-engine.json"));
            RuleDraft typed = RuleDraftCodec.FromRule(numeric.Knowledge.Rules[0]);
            typed.Root = new ConditionDraft(DraftConditionKind.Comparison) { FactId = "sample_count", Operation = "eq", ValueText = "18446744073709551615" };
            numeric.Apply(typed.Id, typed);
            Check(((ComparisonCondition)numeric.Knowledge.Rules[0].Condition).Expected.Canonical == "18446744073709551615", "integer без double");
            typed.Root.FactId = "ambient"; typed.Root.ValueText = "24.125"; numeric.Apply(typed.Id, typed);
            Check(((ComparisonCondition)numeric.Knowledge.Rules[0].Condition).Expected.Canonical == "24.125", "number без строки и округления");
            typed.Root.ValueText = "0.10000000000000000000000000001";
            Invalid(() => numeric.Apply(typed.Id, typed), "недопустимая точность не округляется");

            string uiPath = Path.Combine(temp.FullName, "vm.json"); KnowledgeWorkspace.CreateWorkingCopy(source, uiPath);
            string used = "";
            KnowledgeEditorViewModel model = new KnowledgeEditorViewModel(value => used = value) { SourcePath = uiPath };
            model.LoadCommand.Execute(null); model.SelectedRuleId = "R-13";
            model.Draft!.Title += " VM check";
            model.SelectedRuleId = "R-14";
            Check(model.SelectedRuleId == "R-13" && model.HasPendingRuleChanges, "выбор не теряет непримененные поля");
            model.VersionText = "editor-vm-1"; model.ApplyCommand.Execute(null);
            Check(!model.HasPendingRuleChanges && model.WorkingKnowledge!.Version == "editor-vm-1" && File.ReadAllBytes(uiPath).SequenceEqual(original), "VM применяет правило и версию без записи");
            model.DeleteRuleCommand.Execute(null); model.UndoCommand.Execute(null);
            Check(model.WorkingKnowledge!.Rules.Any(item => item.Id == "R-13"), "VM undo удаления");
            model.SaveCommand.Execute(null); model.UseForConsultationCommand.Execute(null);
            Check(used == uiPath && new KnowledgeBaseService().Load(uiPath).Version == "editor-vm-1", "выбран сохраненный путь для новых консультаций");
            model.NewRuleCommand.Execute(null);
            RuleDraft added = model.Draft ?? throw new InvalidOperationException("новый черновик не создан");
            added.Id = "USER-CHECK"; added.Title = "проверочное правило редактора";
            added.Explanation = "только проверка CRUD"; model.ApplyCommand.Execute(null);
            Check(model.RuleIds.Contains("USER-CHECK"), "VM добавляет правило");
            model.UndoCommand.Execute(null); Check(!model.RuleIds.Contains("USER-CHECK"), "VM отменяет добавление");
            Check(File.ReadAllBytes(source).SequenceEqual(original), "поставляемая БЗ сохранена");
        }
        finally { temp.Delete(true); }
        Console.WriteLine("редактор БЗ: типы, CRUD/undo, сохранение/backup, внешний конфликт и закрепленный сеанс проверены; нативный GUI и UART этим не проверены");
    }
    private static void Invalid(Action action, string message) {
        try { action(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or InvalidOperationException or JsonException) { return; }
        throw new InvalidOperationException(message + ": операция ошибочно принята");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
