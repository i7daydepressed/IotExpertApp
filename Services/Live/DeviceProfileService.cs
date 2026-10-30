using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Models.Live;
using IotExpertApp.Services.Knowledge;

namespace IotExpertApp.Services.Live;

public sealed class DeviceProfileService {
    private static readonly string[] Statuses = { "ok", "error", "unavailable", "not_polled", "disabled", "stale", "unknown" };
    private static readonly string[] Outcomes = { "ready", "degraded", "incomplete" };
    private static readonly string[] SourceIds = { "esp32", "oled", "dht11", "light", "ir" };

    public DeviceProfile Load(string path) {
        string fullPath = Path.GetFullPath(path);
        byte[] bytes = KnowledgeJson.ReadBounded(fullPath);
        using JsonDocument document = KnowledgeJson.Parse(bytes);
        JsonElement root = document.RootElement;
        KnowledgeJson.Shape(root, "профиль", new[] { "schema_version", "id", "version", "title", "scope",
            "device_id", "config_version", "packet_timeout_ms", "required_sources", "diagnostic_knowledge",
            "strategy_knowledge", "static_facts", "diagnostic_mapping" }, new[] { "module_check_knowledge", "evidence_policy" });
        KnowledgeJson.Require(KnowledgeJson.Number(root.GetProperty("schema_version"), "schema_version") == 1,
            "поддерживается schema_version профиля 1");
        string directory = Path.GetDirectoryName(fullPath)!;
        KnowledgeBaseService loader = new KnowledgeBaseService();
        KnowledgeBase diagnostic = loader.Load(Resolve(directory, root.GetProperty("diagnostic_knowledge")));
        KnowledgeBase strategy = loader.Load(Resolve(directory, root.GetProperty("strategy_knowledge")));
        KnowledgeJson.Require(strategy.SchemaVersion == 2 && strategy.Id == "lr1-iot-response",
            "профиль требует БЗ стратегий ЛР1 схемы 2");
        HashSet<string> strategyInputs = strategy.Facts.Values.Where(item => item.Role == FactRole.Input)
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        KnowledgeJson.Require(strategyInputs.SetEquals(Enumerable.Range(1, 11).Select(index => "V" + index))
            && strategy.Facts["V1"].Type == FactType.Boolean, "не совпал контракт входов ЛР1 V1..V11");
        FactDefinition diagnosticGoal = diagnostic.Facts.Values.Single(item => item.Role == FactRole.Goal);
        KnowledgeJson.Require(diagnosticGoal.Type == FactType.Enum && diagnosticGoal.Options.ToHashSet(StringComparer.Ordinal).SetEquals(Outcomes),
            "цель диагностики должна иметь значения ready/degraded/incomplete");
        JsonElement sourceArray = root.GetProperty("required_sources");
        KnowledgeJson.Require(sourceArray.ValueKind == JsonValueKind.Array && sourceArray.GetArrayLength() == SourceIds.Length,
            "первый профиль контролирует пять источников esp32/oled/dht11/light/ir");
        ImmutableArray<SourceRequirement>.Builder sources = ImmutableArray.CreateBuilder<SourceRequirement>();
        HashSet<string> seenSources = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> seenFacts = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement source in sourceArray.EnumerateArray()) {
            KnowledgeJson.Shape(source, "источник профиля", new[] { "source_id", "fact_id", "max_age_ms" });
            string sourceId = KnowledgeJson.Id(source.GetProperty("source_id"), "source_id");
            string factId = KnowledgeJson.Id(source.GetProperty("fact_id"), "fact_id");
            KnowledgeJson.Require(SourceIds.Contains(sourceId, StringComparer.Ordinal) && seenSources.Add(sourceId)
                && seenFacts.Add(factId), "неподдержанный или повторный источник/признак");
            KnowledgeJson.Require(diagnostic.Facts.TryGetValue(factId, out FactDefinition? definition)
                && definition.Role == FactRole.Input && definition.Type == FactType.Enum
                && definition.Options.ToHashSet(StringComparer.Ordinal).SetEquals(Statuses), "не совпал enum статуса " + factId);
            sources.Add(new SourceRequirement(sourceId, factId, Milliseconds(source.GetProperty("max_age_ms"))));
        }
        KnowledgeJson.Require(seenFacts.SetEquals(diagnostic.Facts.Values.Where(item => item.Role == FactRole.Input).Select(item => item.Id)),
            "каждый вход диагностической БЗ должен получать один статус источника");
        ImmutableDictionary<string, KnowledgeValue> defaults = Values(root.GetProperty("static_facts"), strategy,
            Enumerable.Range(5, 7).Select(index => "V" + index).ToArray(), true);
        JsonElement mapping = root.GetProperty("diagnostic_mapping");
        KnowledgeJson.Shape(mapping, "diagnostic_mapping", Outcomes);
        ImmutableDictionary<string, ImmutableDictionary<string, KnowledgeValue>>.Builder mappings
            = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, KnowledgeValue>>(StringComparer.Ordinal);
        foreach (string outcome in Outcomes) {
            ImmutableDictionary<string, KnowledgeValue> values = Values(mapping.GetProperty(outcome), strategy,
                new[] { "V2", "V3", "V4" }, outcome != "incomplete");
            KnowledgeJson.Require(values.ContainsKey("V2"), "отображение диагностики должно задать V2");
            mappings.Add(outcome, values);
        }
        KnowledgeBase? module = root.TryGetProperty("module_check_knowledge", out JsonElement modulePath) ? loader.Load(Resolve(directory, modulePath)) : null;
        if (module is not null) {
            KnowledgeJson.Require(module.SchemaVersion == 2 && module.Id == "esp32-module-checks", "не совпала БЗ выборочных проверок");
            IotExpertApp.Services.Checks.ModuleCheckAssessment.ValidateKnowledge(module);
        }
        EvidencePolicy? evidence = root.TryGetProperty("evidence_policy", out JsonElement policy) ? LoadEvidence(policy, directory, loader, strategy, module) : null;
        string deviceId = KnowledgeJson.Text(root.GetProperty("device_id"), "device_id");
        KnowledgeJson.Require(System.Text.RegularExpressions.Regex.IsMatch(deviceId, @"\Aesp32-[0-9a-fA-F]{12}\z"),
            "device_id профиля должен обозначать конкретную ESP32");
        return new DeviceProfile(fullPath, KnowledgeJson.Id(root.GetProperty("id"), "profile.id"),
            KnowledgeJson.Text(root.GetProperty("version"), "profile.version"),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            KnowledgeJson.Text(root.GetProperty("title"), "profile.title"),
            KnowledgeJson.Text(root.GetProperty("scope"), "profile.scope"), deviceId,
            KnowledgeJson.Text(root.GetProperty("config_version"), "config_version"),
            Milliseconds(root.GetProperty("packet_timeout_ms")), sources.ToImmutable(), diagnostic, strategy, defaults, mappings.ToImmutable()) { ModuleKnowledge = module, EvidencePolicy = evidence };
    }

    private static EvidencePolicy LoadEvidence(JsonElement root, string directory, KnowledgeBaseService loader, KnowledgeBase strategy, KnowledgeBase? moduleKnowledge) {
        KnowledgeJson.Require(moduleKnowledge is not null, "учет проверок требует module_check_knowledge");
        KnowledgeJson.Shape(root,"evidence_policy",new[]{"knowledge","modules","mapping"});
        KnowledgeBase knowledge=loader.Load(Resolve(directory,root.GetProperty("knowledge")));
        KnowledgeJson.Require(knowledge.SchemaVersion==2&&knowledge.Id=="esp32-evidence-policy","не совпала БЗ связи свидетельств");
        ImmutableArray<EvidenceModulePolicy>.Builder modules=ImmutableArray.CreateBuilder<EvidenceModulePolicy>();
        HashSet<string> seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(JsonElement item in root.GetProperty("modules").EnumerateArray()) {
            KnowledgeJson.Shape(item,"модуль политики",new[]{"source_id","kind","required","influencing_outcomes"});
            string source=KnowledgeJson.Id(item.GetProperty("source_id"),"source_id");
            KnowledgeJson.Require(IotExpertApp.Services.Checks.CheckProtocol.Sources.Contains(source)&&seen.Add(source),"повторный/неизвестный модуль политики");
            string kind=KnowledgeJson.Text(item.GetProperty("kind"),"kind");
            string expected=source switch {"esp32"=>"metrics","oled"=>"display","dht11"=>"read","microsd"=>"file","relay_1"or"relay_2"=>"relay",_=>"digital"};
            KnowledgeJson.Require(kind==expected,"тип модуля не соответствует источнику");
            ImmutableHashSet<string> impact=item.GetProperty("influencing_outcomes").EnumerateArray().Select(x=>KnowledgeJson.Text(x,"исход")).ToImmutableHashSet(StringComparer.Ordinal);
            KnowledgeJson.Require(impact.All(x=>x is "negative"or"machine_error"),"неподдержанный влияющий исход");
            modules.Add(new EvidenceModulePolicy(source,kind,item.GetProperty("required").GetBoolean(),impact));
        }
        KnowledgeJson.Require(seen.SetEquals(IotExpertApp.Services.Checks.CheckProtocol.Sources),"политика должна явно описать девять модулей стенда");
        HashSet<string> expectedInputs=new HashSet<string>(StringComparer.Ordinal){"obs_state"};
        void Enum(string id,string[] options) {
            expectedInputs.Add(id);
            KnowledgeJson.Require(knowledge.Facts.TryGetValue(id,out FactDefinition? f)&&f.Role==FactRole.Input&&f.Type==FactType.Enum&&f.Options.ToHashSet().SetEquals(options),"не совпал enum связи "+id);
        }
        Enum("obs_state",new[]{"ready","degraded","incomplete","unknown"});
        foreach(EvidenceModulePolicy module in modules) {
            string source=module.Source;
            Enum("observation_"+source,Statuses);Enum("machine_"+source,EvidenceAssessmentService.Machines);Enum("human_"+source,EvidenceAssessmentService.Humans);Enum("signal_"+source,EvidenceAssessmentService.Signals);
            foreach(string prefix in new[]{"required_","included_","affect_negative_","affect_machine_error_"}) {
                string id=prefix+source;expectedInputs.Add(id);
                KnowledgeJson.Require(knowledge.Facts.TryGetValue(id,out FactDefinition? f)&&f.Role==FactRole.Input&&f.Type==FactType.Boolean,"не совпал bool связи "+id);
            }
        }
        KnowledgeJson.Require(expectedInputs.SetEquals(knowledge.Facts.Values.Where(f=>f.Role==FactRole.Input).Select(f=>f.Id)),"лишние входы БЗ связи");
        FactDefinition goal=knowledge.Facts.Values.Single(f=>f.Role==FactRole.Goal);
        string[] outcomes={"ready","degraded","clarify","incomplete"};
        KnowledgeJson.Require(goal.Type==FactType.Enum&&goal.Options.ToHashSet().SetEquals(outcomes),"исходы БЗ связи");
        JsonElement mapping=root.GetProperty("mapping");KnowledgeJson.Shape(mapping,"mapping связи",outcomes);
        ImmutableDictionary<string,ImmutableDictionary<string,KnowledgeValue>>.Builder mappings=ImmutableDictionary.CreateBuilder<string,ImmutableDictionary<string,KnowledgeValue>>(StringComparer.Ordinal);
        foreach(string outcome in outcomes) {
            ImmutableDictionary<string,KnowledgeValue> values=Values(mapping.GetProperty(outcome),strategy,new[]{"V2","V3","V4"},outcome is "ready"or"degraded");
            KnowledgeJson.Require(values.ContainsKey("V2"),"mapping связи должен задать V2");mappings.Add(outcome,values);
        }
        return new EvidencePolicy(knowledge,modules.ToImmutable(),mappings.ToImmutable());
    }

    private static string Resolve(string directory, JsonElement value) {
        string relative = KnowledgeJson.Text(value, "путь БЗ");
        KnowledgeJson.Require(!Path.IsPathRooted(relative), "пути БЗ в профиле должны быть относительными к файлу профиля");
        return Path.GetFullPath(Path.Combine(directory, relative));
    }

    private static uint Milliseconds(JsonElement value) {
        decimal number = KnowledgeJson.Number(value, "время, мс");
        KnowledgeJson.Require(number == decimal.Truncate(number) && number is >= 1 and <= 300000,
            "время должно быть целым числом 1..300000 мс");
        return (uint)number;
    }

    private static ImmutableDictionary<string, KnowledgeValue> Values(JsonElement json, KnowledgeBase knowledge,
        string[] allowed, bool requireAll) {
        KnowledgeJson.Shape(json, "факты профиля", requireAll ? allowed : Array.Empty<string>(), requireAll ? Array.Empty<string>() : allowed);
        ImmutableDictionary<string, KnowledgeValue>.Builder result = ImmutableDictionary.CreateBuilder<string, KnowledgeValue>(StringComparer.Ordinal);
        foreach (JsonProperty property in json.EnumerateObject()) {
            FactDefinition definition = knowledge.Facts[property.Name];
            KnowledgeJson.Require(definition.Role == FactRole.Input, "профиль может задавать только входы");
            result.Add(property.Name, KnowledgeJson.Value(property.Value, definition, property.Name));
        }
        return result.ToImmutable();
    }
}
