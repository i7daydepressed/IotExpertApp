using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IotExpertApp.Models.Checks;
using IotExpertApp.Models.Live;
using IotExpertApp.Models.Knowledge;
using IotExpertApp.Models.Telemetry;
using IotExpertApp.Services.Checks;
using IotExpertApp.Services.Telemetry;
using IotExpertApp.Services.Knowledge;
using IotExpertApp.Services.Live;
using IotExpertApp.Views;

namespace IotExpertApp.ConsoleUI;

public static class GuidedCheckChecks {
    public static void Run() {
        string[] frames = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "CheckExamples", "interactive-oled-synthetic.jsonl"))
            .Where(l => l.StartsWith("TEST_EVENT ", StringComparison.Ordinal)).ToArray();
        List<CheckEvent> decoded = new List<CheckEvent>();
        foreach (string frame in frames) { Check(CheckProtocol.TryParse(frame, out CheckEvent? e, out string error), error); decoded.Add(e!); }
        Check(decoded.Count == 15, "пятнадцать synthetic кадров C-controller v2");
        string oldJson = File.ReadLines(Path.Combine(AppContext.BaseDirectory,"CheckExamples","real-selective.jsonl")).First(l => l.StartsWith("{", StringComparison.Ordinal));
        Check(new TelemetryParser().TryParse(oldJson, out TelemetryPacket? old, out string parseError), parseError);
        TelemetryPacket packet = old! with { FirmwareVersion = "lr3-3-nogit-9d4af0c663d9", BootId = new string('b',32) };
        DeviceProfile profile = new DeviceProfileService().Load(Path.Combine(AppContext.BaseDirectory,"DeviceProfiles","esp32-observability.json"));
        KnowledgeBase knowledge = profile.ModuleKnowledge ?? throw new InvalidOperationException("БЗ модулей отсутствует");
        CommandCompatibility compatibility = CommandCompatibility.Load(Path.Combine(AppContext.BaseDirectory,"DeviceProfiles","selective-checks.json"));
        Check(compatibility.Protocol(packet.FirmwareVersion) == 2 && compatibility.Protocol(old.FirmwareVersion) == 1, "точные контракты v1/v2");
        ReceiverState state = new ReceiverState("прием","synthetic: fake v2",1,0,0,0,0,0,0,"",new ReceivedSnapshot(packet,DateTimeOffset.UtcNow,"synthetic: fake v2"),0,true,false,false,"","") { IsSerial = true };
        CheckEvent Event(string name, int seconds = 30) {
            CheckEvent e = name switch {
                "capabilities" => decoded[0], "dispatch" => decoded[1], "ready" => decoded[2], "status_ready" => decoded[3],
                "terminal" => decoded[5], "finish" => decoded[6], "watchdog" => decoded[7], "restore_failed" => decoded[8], _ => decoded[2]
            };
            JsonObject node = JsonNode.Parse(JsonSerializer.Serialize(e.Data))!.AsObject();
            if (e.Operation == "display_hold" && node.ContainsKey("max_hold_ms")) {
                node["max_hold_ms"] = (seconds + 10) * 1000;
                if (node["shown_ms"] is not null) node["deadline_ms"] = node["shown_ms"]!.GetValue<ulong>() + (uint)((seconds+10)*1000);
            }
            return e with { Device = packet.DeviceId, Boot = packet.BootId, Firmware = packet.FirmwareVersion,
                RequestId = e.RequestId > 0 ? 1UL : e.RequestId, ReplyTo = e.ReplyTo > 0 ? 1UL : e.ReplyTo,
                Target = e.Target > 0 ? 1UL : e.Target, UpperId = e.UpperId > 0 ? 1UL : 0UL, Data = JsonSerializer.SerializeToElement(node) };
        }
        (SelectiveCheckSession Session, GuidedCheckCoordinator Flow, List<CheckRequest> Sent, Action Drain, Action<CheckEvent> Receive, Action<double> Clock) Harness() {
            TimeSpan now = TimeSpan.Zero;
            SelectiveCheckSession session = new SelectiveCheckSession(); session.Configure(compatibility); session.Begin(true); session.Observe(packet);
            Check(session.Accept(Event("capabilities"), state.Origin, out _), "v2 capabilities привязаны к v1");
            GuidedCheckCoordinator flow = new GuidedCheckCoordinator(session, () => now);
            List<CheckRequest> sent = new List<CheckRequest>();
            void Drain() { while(session.TryTake(out CheckCommand? c)) { byte[] bytes = CheckProtocol.Encode(c!.Request); Check(bytes[^1]==10 && bytes.Length<=512, "wire envelope"); sent.Add(c.Request); session.Sent(c,null); } }
            void Receive(CheckEvent e) { Check(session.Accept(e,state.Origin,out string error), error); flow.Event(e); }
            Drain();
            return (session,flow,sent,Drain,Receive,s => now=TimeSpan.FromSeconds(s));
        }
        foreach (string? answer in new string?[] { "Да","Нет","Неясно","Не проверял",null }) {
            (SelectiveCheckSession session, GuidedCheckCoordinator flow, List<CheckRequest> sent, Action drain, Action<CheckEvent> receive, Action<double> clock) = Harness();
            Check(flow.Start("oled",5,500,knowledge,state),"start OLED"); drain();
            Check(sent.Last().Operation=="display_hold" && sent.Last().MaxHold==15000, "срок ответа+10с на проводе");
            receive(Event("dispatch",5)); Check(flow.Current!.Phase!="answer", "dispatch не готовность");
            clock(2); receive(Event("ready",5)); TimeSpan? deadline=flow.Current!.AnswerDeadline;
            session.Observe(packet with { Sequence=99,UptimeMs=99999 }); flow.Tick(state);
            receive(Event("status_ready",5));
            Check(flow.Current.AnswerDeadline==deadline && flow.Current.RequestId==1, "v1/status не переносят deadline/ID");
            Check(!flow.Start("oled",300,10000,knowledge with { Version="synthetic-reloaded" },state)
                && flow.Current.ResponseSeconds==5 && ReferenceEquals(flow.Current.Knowledge,knowledge), "новый срок/БЗ не заменяют активный сеанс");
            Check(!session.Enqueue("esp32","snapshot",null,null,out _),"вопрос блокирует обычные действия");
            if(answer is null) {clock(7); flow.Tick(state);} else { clock(6.99); Check(flow.Answer(answer), "явный ответ до deadline"); }
            drain(); Check(sent.Count(r=>r.Operation=="finish_display")==1 && flow.Current!.Phase=="finishing", "один адресный finish, еще не успех restore");
            flow.Answer("Да"); flow.Tick(state); drain(); Check(sent.Count(r=>r.Operation=="finish_display")==1,"поздний клик не дублирует finish");
            receive(Event("terminal",5)); GuidedCheckResult pinned=flow.Current!;
            Check(!pinned.Active && (answer is null ? pinned.Human is null && pinned.EndReason=="timeout" : pinned.Human?.Answer==answer),"timeout не является ответом");
            Check(pinned.Inference?.Status==InferenceStatus.Success,"итог через общий движок");
            string expected=answer switch {"Да"=>"oled_seen","Нет"=>"oled_not_seen","Неясно"=>"unclear","Не проверял"=>"skipped",_=>"no_answer"};
            Check(LiveAssessmentService.Goal(pinned.Inference!)==expected,"правила OLED: "+expected);
            session.Observe(packet with {Sequence=100}); flow.Tick(state); Check(ReferenceEquals(pinned,flow.Current),"терминальный итог закреплен");
        }
        {
            (SelectiveCheckSession Session, GuidedCheckCoordinator Flow, List<CheckRequest> Sent, Action Drain, Action<CheckEvent> Receive, Action<double> Clock) h=Harness(); Check(h.Flow.Start("oled",5,500,knowledge,state),"boundary start");h.Drain();h.Receive(Event("ready",5));h.Clock(5);
            Check(!h.Flow.Answer("Да") && h.Flow.Current!.Human is null,"deadline выигрывает на точной границе");
            h.Drain();Check(h.Sent.Count(r=>r.Operation=="finish_display")==1,"гонка дает один finish");
        }
        {
            (SelectiveCheckSession Session, GuidedCheckCoordinator Flow, List<CheckRequest> Sent, Action Drain, Action<CheckEvent> Receive, Action<double> Clock) h=Harness();h.Flow.Start("oled",30,500,knowledge,state);h.Drain();h.Receive(Event("ready"));h.Receive(Event("watchdog"));
            Check(h.Flow.Current!.EndReason=="early_end" && h.Flow.Current.Human is null,"watchdog до host deadline не человек");
        }
        {
            (SelectiveCheckSession Session, GuidedCheckCoordinator Flow, List<CheckRequest> Sent, Action Drain, Action<CheckEvent> Receive, Action<double> Clock) h=Harness();h.Flow.Start("oled",30,500,knowledge,state);h.Drain();h.Receive(Event("ready"));h.Flow.Answer("Да");h.Drain();h.Receive(Event("restore_failed"));
            Check(LiveAssessmentService.Goal(h.Flow.Current!.Inference!)=="machine_error","ошибка восстановления сильнее Да");
        }
        {
            (SelectiveCheckSession Session, GuidedCheckCoordinator Flow, List<CheckRequest> Sent, Action Drain, Action<CheckEvent> Receive, Action<double> Clock) h=Harness();h.Flow.Start("oled",30,500,knowledge,state);h.Drain();h.Receive(Event("ready"));h.Flow.Answer("Да");h.Drain();h.Clock(13);h.Flow.Tick(state);
            GuidedCheckResult uncertain=h.Flow.Current!;Check(uncertain.EndReason=="uncertain","потеря finish/ответа не PASS");
            h.Flow.Recover(state);h.Drain();h.Receive(Event("status_ready"));h.Flow.RetryFinish();h.Drain();h.Receive(Event("finish"));
            Check(h.Sent.Count(r=>r.Operation=="display_hold")==1 && h.Flow.History.Contains(uncertain) && h.Flow.Current!.Revision==1,"status восстанавливает без нового рисунка, история редакции сохранена");
        }
        {
            (SelectiveCheckSession Session, GuidedCheckCoordinator Flow, List<CheckRequest> Sent, Action Drain, Action<CheckEvent> Receive, Action<double> Clock) h=Harness();h.Flow.Start("oled",30,500,knowledge,state);h.Flow.PrepareStop();h.Drain();
            Check(!h.Sent.Any(r=>r.Operation is "display_hold" or "finish_display") && h.Flow.Current!.EndReason=="interrupted","Stop до отправки не запускает OLED ради уборки");
            h=Harness();h.Flow.Start("oled",30,500,knowledge,state);h.Drain();h.Receive(Event("ready"));h.Flow.PrepareStop();h.Drain();h.Flow.Interrupt();
            Check(h.Sent.Count(r=>r.Operation=="finish_display")==1 && h.Flow.Current!.EndReason=="interrupted" && h.Flow.Current.Human is null,"Stop: один best-effort finish, не timeout/Нет");
            h=Harness();h.Flow.Start("oled",30,500,knowledge,state);h.Drain();h.Receive(Event("ready"));h.Flow.Tick(state with { Snapshot=new ReceivedSnapshot(packet with {BootId=new string('c',32)},DateTimeOffset.UtcNow,"synthetic") });
            Check(h.Flow.Current!.EndReason=="interrupted","смена boot прерывает вопрос");
        }
        ModuleRules(knowledge,old!,Event("terminal"),Event("restore_failed"));
        {
            (SelectiveCheckSession Session, GuidedCheckCoordinator Flow, List<CheckRequest> Sent, Action Drain, Action<CheckEvent> Receive, Action<double> Clock) h=Harness();
            h.Flow.Start("light",5,500,knowledge,state);h.Drain();
            Check(h.Flow.Current!.Phase!="answer","GPIO не спрашивает до terminal окна");
            CheckEvent completedInput = decoded[5] with { Device=packet.DeviceId,Boot=packet.BootId,Firmware=packet.FirmwareVersion,Source="light",Operation="observe",HumanConfirmation="input_response",Data=JsonSerializer.SerializeToElement(new {transition_count=0}) };
            h.Receive(completedInput);Check(h.Flow.Current.Phase=="answer","GPIO спрашивает после окна");
            h.Clock(5);h.Flow.Tick(state);h.Drain();Check(!h.Sent.Any(r=>r.Operation=="finish_display"),"timeout GPIO не завершает SD/OLED/реле");
            Check(!h.Flow.Start("oled",5,500,knowledge,state with {IsSerial=false}),"replay не начинает ответный таймер");
        }
        ReceiverWorkflowAsync(profile,packet,oldJson,Event("capabilities"),Event("ready",5),Event("terminal",5)).GetAwaiter().GetResult();
        PreferencesAndProfile(profile);
        Check(ModuleTilePanel.Columns(1100)==3 && ModuleTilePanel.Columns(750)==2 && ModuleTilePanel.Columns(580)==1,"адаптивные границы области");
        Console.WriteLine("STEP11: строгие v1/v2, ready/finish/status, fake-таймеры/границы/Stop/boot, A5 через общий движок, настройки и закрепленная БЗ проверены; физический OLED этим не подтвержден");
    }
    private static void ModuleRules(KnowledgeBase knowledge,TelemetryPacket old,CheckEvent oled,CheckEvent failed) {
        string[] real=File.ReadAllLines(Path.Combine(AppContext.BaseDirectory,"CheckExamples","real-selective.jsonl"));
        List<CheckEvent> machines=new List<CheckEvent>();
        foreach(string line in real.Where(l=>l.StartsWith("TEST_EVENT ",StringComparison.Ordinal))) {Check(CheckProtocol.TryParse(line,out CheckEvent? e,out _),"старый fixture v1");if(e!.Stage=="completed"&&e.RequestId>0)machines.Add(e);}
        GuidedCheckResult Row(CheckEvent machine,string? answer,string end="answered") => new GuidedCheckResult(machine.Device,machine.Boot,machine.Firmware,machine.Config,machine.RequestId!.Value,machine.Source!,machine.Operation!,"synthetic: rule table",machine.ProtocolVersion,30,null,knowledge,"finished",end,TimeSpan.Zero,TimeSpan.Zero,null,machine,
            answer is null?null:new HumanCheckAnswer(machine.Device,machine.Boot,machine.RequestId.Value,machine.Source!,answer,DateTimeOffset.UtcNow,"synthetic row"),false,null);
        void Expected(GuidedCheckResult row,string goal) {
            InferenceResult result=ModuleCheckAssessment.Run(row)!;
            Check(result.Status==InferenceStatus.Success && LiveAssessmentService.Goal(result)==goal,"A5 "+goal+" фактически "+result.Status+" "+(result.Status==InferenceStatus.Success?LiveAssessmentService.Goal(result):""));
            Check(result.Steps.Length>0 && result.Inputs["human"].Origin.Contains("request=",StringComparison.Ordinal),"реальная трасса и происхождение");
        }
        Expected(Row(failed,"Да"),"machine_error");Expected(Row(oled with{Stage="rejected",Reason="busy"},null,"machine"),"not_executed");
        Expected(Row(oled,null,"interrupted"),"interrupted");Expected(Row(oled,null,"uncertain"),"interrupted");
        Expected(Row(oled,"Да","timeout"),"no_answer");Expected(Row(oled,"Не проверял"),"skipped");Expected(Row(oled,"Неясно"),"unclear");
        Expected(Row(oled,"Да"),"oled_seen");Expected(Row(oled,"Нет"),"oled_not_seen");
        foreach(string source in new[]{"light","ir","pir"}) {
            CheckEvent e=machines.First(m=>m.Source==source);
            Expected(Row(e,"Нет"),"gpio_no_reaction");Expected(Row(e,"Да"),"mismatch");
            JsonObject d=JsonNode.Parse(e.Data.GetRawText())!.AsObject();d["transition_count"]=1;d["last_transition_ms"]=d["window_end_ms"]!.GetValue<ulong>();
            e=e with{Data=JsonSerializer.SerializeToElement(d)};
            Expected(Row(e,"Да"),source=="pir"?"pir_observed":"gpio_observed");Expected(Row(e,"Нет"),"mismatch");
        }
        using JsonDocument pulse=JsonDocument.Parse("{\"in1_level\":1,\"in2_level\":1,\"off_callback_ms\":200}");
        CheckEvent relay=oled with{Source="relay_1",Operation="pulse",Reason="machine_check_complete",Data=pulse.RootElement.Clone()};
        Expected(Row(relay,"Да"),"relay_heard");Expected(Row(relay,"Нет"),"relay_not_heard");
        foreach((string source,string goal) in new[]{("esp32","metrics"),("dht11","new_read"),("microsd","file_test")})Expected(Row(machines.First(m=>m.Source==source),null,"machine"),goal);
        Expected(Row(oled with{Reason="future_unknown_reason"},"Да"),"insufficient");
        KnowledgeRule positive=knowledge.Rules.Single(r=>r.Conclusions.Any(c=>c.Value.Canonical=="oled_seen"));
        KnowledgeRule clash=positive with {Id="MOD-CLASH",Conclusions=ImmutableArray.Create(new Conclusion("module_verdict",new KnowledgeValue(FactType.Enum,"mismatch")))};
        InferenceResult conflict=ModuleCheckAssessment.Run(Row(oled,"Да") with {Knowledge=knowledge with {Rules=knowledge.Rules.Add(clash)}})!;
        Check(conflict.Status==InferenceStatus.Conflict,"конфликт БЗ не становится PASS");
        GuidedCheckResult pinned=Row(oled,"Да");InferenceResult first=ModuleCheckAssessment.Run(pinned)!;
        Check(pinned.Knowledge!.Version==knowledge.Version && first.KnowledgeBase.Sha256==knowledge.Sha256,"расчет сохраняет исходную БЗ");
    }
    private static async Task ReceiverWorkflowAsync(DeviceProfile profile, TelemetryPacket packet, string v1, CheckEvent caps, CheckEvent ready, CheckEvent terminal) {
        long ticks=0;
        using SyntheticSource source=new SyntheticSource(caps,ready,terminal);
        await using TelemetryReceiverService receiver=new TelemetryReceiverService(new ObservationStore(),(_,_)=>source,()=>TimeSpan.FromTicks(Interlocked.Read(ref ticks)));
        await receiver.StartSerialAsync("synthetic-only");
        JsonObject json=JsonNode.Parse(v1)!.AsObject();json["firmware_version"]=packet.FirmwareVersion;json["boot_id"]=packet.BootId;
        source.Feed(json.ToJsonString()+"\n");
        await Until(()=>receiver.GetChecks().Ready);
        Check(source.Writes.Count(op=>op=="capabilities")==1,"один автоматический capabilities v2 после v1");
        Check(receiver.StartGuided("oled",5,500,profile.ModuleKnowledge),"v2 workflow через receiver");
        await Until(()=>receiver.GetGuided().Current?.Phase=="answer");
        Interlocked.Exchange(ref ticks,TimeSpan.FromSeconds(5).Ticks);
        await Until(()=>receiver.GetGuided().Current?.Active==false);
        Check(receiver.GetGuided().Current?.EndReason=="timeout" && receiver.GetGuided().Current?.Human is null,"fake clock timeout в настоящем receiver");
        Check(source.Writes.Count(op=>op=="display_hold")==1 && source.Writes.Count(op=>op=="finish_display")==1,"fake wire: start/ready/один finish/terminal");
        IotExpertApp.ViewModels.ModuleChecksViewModel model=new IotExpertApp.ViewModels.ModuleChecksViewModel(receiver,()=>profile);
        model.Update(receiver.GetState());
        await using MemoryStream output=new MemoryStream();await model.ExportAsync(output);
        using JsonDocument exported=JsonDocument.Parse(output.ToArray());
        JsonElement workflow=exported.RootElement.GetProperty("workflow")[0];
        Check(workflow.GetProperty("result").GetProperty("EndReason").GetString()=="timeout" && workflow.GetProperty("result").GetProperty("Human").ValueKind==JsonValueKind.Null
            && workflow.GetProperty("tree").GetArrayLength()>0 && workflow.GetProperty("trace").GetString()!.Contains("MOD-",StringComparison.Ordinal),"экспорт machine/human/workflow и настоящей трассы timeout без ответа");
        await receiver.StopAsync();Check(source.Writes.Count(op=>op=="finish_display")==1 && !source.Writes.Any(op=>op is "pulse" or "all_off"),"Stop не повторяет завершенную уборку и не включает реле");
    }
    private sealed class SyntheticSource : ITelemetryCommandSource {
        private readonly BlockingCollection<byte[]> input=new BlockingCollection<byte[]>(128);
        private readonly CheckEvent caps,ready,terminal;
        public ConcurrentQueue<string> Writes {get;}=new ConcurrentQueue<string>();
        public SyntheticSource(CheckEvent caps,CheckEvent ready,CheckEvent terminal){this.caps=caps;this.ready=ready;this.terminal=terminal;}
        public void Feed(string text){byte[] bytes=Encoding.UTF8.GetBytes(text);for(int i=0;i<bytes.Length;i+=4096)input.Add(bytes.Skip(i).Take(4096).ToArray());}
        private void Emit(CheckEvent e) {
            JsonObject node=new JsonObject {
                ["protocol_version"]=e.ProtocolVersion,["kind"]=e.Kind,["device_id"]=e.Device,["boot_id"]=e.Boot,["firmware_version"]=e.Firmware,["config_version"]=e.Config,
                ["request_id"]=e.RequestId?.ToString(),["reply_to_request_id"]=e.ReplyTo?.ToString(),["target_request_id"]=e.Target?.ToString(),["source_id"]=e.Source,["operation"]=e.Operation,
                ["stage"]=e.Stage,["reason"]=e.Reason,["replayed"]=e.Replayed,["upper_request_id"]=e.UpperId.ToString(),["accepted_ms"]=e.Accepted,["started_ms"]=e.Started,["finished_ms"]=e.Finished,
                ["error_code"]=e.ErrorCode,["error_domain"]=e.ErrorDomain,["human_confirmation"]=e.HumanConfirmation,["data"]=JsonNode.Parse(e.Data.GetRawText())};
            Feed("TEST_EVENT "+node.ToJsonString()+"\n");
        }
        public void Write(byte[] bytes){JsonObject request=JsonNode.Parse(Encoding.ASCII.GetString(bytes)[13..])!.AsObject();string operation=request["operation"]!.ToString();Writes.Enqueue(operation);
            if(operation=="capabilities")Emit(caps);else if(operation=="display_hold")Emit(ready);else if(operation=="finish_display")Emit(terminal);}
        public int Read(byte[] buffer){if(!input.TryTake(out byte[]? bytes,10))throw new TimeoutException();bytes.CopyTo(buffer,0);return bytes.Length;}
        public void Dispose(){}
    }
    private static async Task Until(Func<bool> condition){for(int i=0;i<300;i++){if(condition())return;await Task.Delay(10);}throw new InvalidOperationException("fake v2 receiver timeout");}

    private static void PreferencesAndProfile(DeviceProfile original) {
        DirectoryInfo temp=Directory.CreateTempSubdirectory("iot-guided-check-");
        try {
            string settings=Path.Combine(temp.FullName,"prefs.json");
            Check(CheckPreferences.Load(settings).Seconds==30,"fallback30");
            foreach(string valid in new[]{"5","300"}){CheckPreferences.Save(settings,valid);Check(CheckPreferences.Load(settings).Seconds==int.Parse(valid),"границы/восстановление предпочтения");}
            foreach(string invalid in new[]{"4","301","5.5","abc"}) {bool rejected=false;try{CheckPreferences.Save(settings,invalid);}catch(InvalidDataException){rejected=true;}Check(rejected,"неверный срок");}
            JsonObject profile=JsonNode.Parse(File.ReadAllText(original.Path))!.AsObject();profile.Remove("module_check_knowledge");
            profile["diagnostic_knowledge"]=Path.GetRelativePath(temp.FullName,Path.Combine(AppContext.BaseDirectory,"DeviceProfiles","esp32-observability.rules.json"));
            profile["strategy_knowledge"]=Path.GetRelativePath(temp.FullName,Path.Combine(AppContext.BaseDirectory,"Knowledge","lr1-strategies.json"));
            string path=Path.Combine(temp.FullName,"old-profile.json");File.WriteAllText(path,profile.ToJsonString());
            Check(new DeviceProfileService().Load(path).ModuleKnowledge is null,"старый профиль без расширения");
            profile["module_check_knowledge"] = Path.GetRelativePath(temp.FullName,Path.Combine(AppContext.BaseDirectory,"KnowledgeExamples","synthetic-engine.json"));
            File.WriteAllText(path,profile.ToJsonString());
            IotExpertApp.ViewModels.LiveMonitorViewModel model = new IotExpertApp.ViewModels.LiveMonitorViewModel {ProfilePath=original.Path};
            model.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            model.ProfilePath=path; model.ReloadCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(model.Profile!.Sha256==original.Sha256 && model.Profile.ModuleKnowledge!.Sha256==original.ModuleKnowledge!.Sha256,"ошибка расширения сохраняет весь прежний комплект");
        }
        finally{temp.Delete(true);}
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
