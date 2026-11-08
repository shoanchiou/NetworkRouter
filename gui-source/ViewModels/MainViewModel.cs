using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using BindIPManager.Services;
using BindIPManager.Views;
namespace BindIPManager.ViewModels;
public sealed class NetworkChoice : ObservableObject {
 public int Id {get;init;}
 private string _name="";public string Name {get=>_name;set=>SetProperty(ref _name,value);}
 private string _detail="";public string Detail {get=>_detail;set=>SetProperty(ref _detail,value);}
 private bool _selected;public bool Selected {get=>_selected;set=>SetProperty(ref _selected,value);}
}
public sealed record ModeChoice(string Name,string Value) { public override string ToString()=>Name; }
public sealed class RuleDisplay {
 public int Id {get;init;} public string Display {get;init;}=""; public string Sub {get;init;}="";
 public string Current {get;init;}=""; public bool Enabled {get;init;}=true; public string ToggleText=>Enabled?"暫停此規則":"恢復此規則";
}
public sealed class MainViewModel : ObservableObject {
 public ObservableCollection<ProgramEntry> Profiles {get;}=[];
 public ICollectionView ProfilesView {get;}
 public ObservableCollection<NetworkChoice> Networks {get;}=[];
 public ObservableCollection<RuleDisplay> ActiveBindings {get;}=[];
 public ICollectionView ActiveBindingsView {get;}
 public ModeChoice[] Modes {get;}=[new("僅允許選定的網路","Allow"),new("封鎖選定的網路","Deny")];
 private RouterState? _state;private RouterStatus? _reported;private bool _busy;private bool _dirty;private bool _preview;private bool _acceptingState;private string? _lastPollError;
 private ProgramEntry? _selectedProfile;private ModeChoice? _selectedMode;
 private AppHealth[] _health=[];
 private NetworkCatalogEntry[] _catalog=[];private NetworkCatalogEntry[] _fullCatalog=[];
 private string? _networkFingerprint;private DateTime _nextNetworkRefresh;
 private HashSet<string> _runningPaths=new(StringComparer.OrdinalIgnoreCase);
 private HashSet<int> _runningProfileIds=[];private bool _readingRunningPrograms;private string? _lastRunningError;
 private bool _showInactivePrograms;
 public bool ShowInactivePrograms {get=>_showInactivePrograms;set {if(SetProperty(ref _showInactivePrograms,value))RefreshProgramViews();}}
 public bool HasVisibleProfiles=>!ProfilesView.IsEmpty;
 public int VisibleRuleCount=>ActiveBindingsView.Cast<RuleDisplay>().Count();
 public string EmptyRulesText=>ShowInactivePrograms?"目前沒有規則，請先選擇程式來設定。":"目前沒有正在執行的已設定程式。已保存的規則會保留，可勾選「顯示未執行的程式」查看。";
 internal Func<bool>? ConfirmDiscardChanges {get;set;}
 internal int? StateRevision=>_state?.Revision;
 internal IReadOnlyDictionary<string,JsonElement> Options=>_state?.Options??new Dictionary<string,JsonElement>();
 internal event Action? PreferencesChanged;
 internal bool Option(string key,bool fallback)=>_state?.Options.TryGetValue(key,out var value)==true && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean():fallback;
 internal int OptionInt(string key,int fallback)=>_state?.Options.TryGetValue(key,out var value)==true && value.TryGetInt32(out var n)?n:fallback;
 internal IReadOnlyList<NetworkCatalogEntry> Catalog=>_fullCatalog;
 internal async Task SaveOptions(IReadOnlyDictionary<string,object> options) {
  if(IsBusy)throw new InvalidOperationException("其他操作尚未完成，請稍後再試。");IsBusy=true;
  try {Accept(await RouterBridge.CallAsync(new{Action="SaveOptions",Revision=_state?.Revision,Options=options}),preserveEditor:true);}
  catch(RouterOperationException error){if(error.Response.State!=null)Accept(error.Response,preserveEditor:true);throw;}
  finally{IsBusy=false;}
 }
 internal bool HasUnsavedChanges=>_dirty;
 public ProgramEntry? SelectedProfile {get=>_selectedProfile;set {
  if(ReferenceEquals(_selectedProfile,value))return;
  if(!_acceptingState&&_dirty&&!ConfirmDiscard()){
   OnPropertyChanged();System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(()=>OnPropertyChanged(nameof(SelectedProfile))));return;
  }
  if(SetProperty(ref _selectedProfile,value)){LoadEditor();RefreshProgramViews();}
 }}
 public ModeChoice? SelectedMode {get=>_selectedMode;set {if(SetProperty(ref _selectedMode,value)){_dirty=true;OnPropertyChanged(nameof(NetworkHelp));}}}
 public string Name=>SelectedProfile?.Name??"請選擇程式";
 public string ProgramHealth {get {var health=_health.FirstOrDefault(h=>h.Id==SelectedProfile?.Id);if(health==null)return "程式範圍與位置狀態尚未確認。";var scope=health.Scope=="Exact"?"單一執行檔":"程式群組（範圍維持原設定）";return scope+"："+string.Join("；",health.Paths.Select(p=>p.Path+(p.Exists?"（存在）":"（程式位置已失效：目前找不到或無法讀取）")));}}
 public bool CanRebind=>_health.Any(h=>h.Id==SelectedProfile?.Id&&h.ExactOnly&&h.Paths.Any(p=>!p.Exists));
 public string ProtectionNotice=>"僅允許模式下，選定的網路都無法使用時，程式會斷線。"+(Option("FailClosed",true)?"斷線保護已啟用：分流核心停止時會封鎖未經分流的流量，可能使整台電腦斷網；可按「恢復網路」重新連線。":"斷線保護已關閉：分流核心停止時，本工具不會封鎖未經分流的流量。")+"關閉此視窗仍會保留已啟用的限制。";
 public string DnsStatus {get {if(_state?.Enabled!=true)return "DNS 狀態：全域分流未啟用";if(!FreshStatus()||_reported?.Dns is not {} dns)return "DNS 狀態：尚未確認";return "DNS："+(string.IsNullOrWhiteSpace(dns.Route)?"未確認":dns.Route)+"  "+(string.IsNullOrWhiteSpace(dns.Reason)?"原因未確認":dns.Reason);}}
 private bool FreshStatus()=>_reported?.Phase=="Ready"&&_reported.Revision==_state?.Revision&&(DateTime.Now-_reported.Time).TotalSeconds is >= -5 and <15;
 public string ProgramPath=>SelectedProfile==null?"":string.Join("; ",SelectedProfile.Paths);
 public string NetworkHelp=>SelectedMode?.Value=="Allow"?"勾選可以使用的網路，最上面的已勾選網路優先。全部不勾選代表斷線。":"勾選要封鎖的網路，未勾選的網路與其他網卡仍可使用。";
 public bool HasActiveBindings=>!ActiveBindingsView.IsEmpty;
 public bool EngineOk=>File.Exists(Path.Combine(AppContext.BaseDirectory,"core","mihomo.exe"))&&File.Exists(Path.Combine(AppContext.BaseDirectory,"router-guard.exe"));
 public bool ShowElevationWarning=>!IsElevated&&!_preview;
 public static bool IsElevated {get {using var identity=WindowsIdentity.GetCurrent();return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}}
 public bool IsBusy {get=>_busy;private set {SetProperty(ref _busy,value);OnPropertyChanged(nameof(CanEdit));System.Windows.Input.CommandManager.InvalidateRequerySuggested();}}
 public bool CanEdit=>!IsBusy;
 private string _status="正在讀取程式與規則";
 public string Status {get=>_status;set=>SetProperty(ref _status,value);}
 public string EngineState=>!Option("AutoRefreshStatus",true)?"狀態自動更新已關閉":_state?.Enabled!=true?"全域限制未啟用":_reported?.Phase=="Ready"&&_reported.Revision==_state.Revision&&(DateTime.Now-_reported.Time).TotalSeconds >= -5&&(DateTime.Now-_reported.Time).TotalSeconds < 15?"分流核心運作中":"全域限制已啟用，等待核心狀態";
 public bool TestRan=>false;
 public bool TestOk=>false;public string TestResult=>"";
 public RelayCommand NewProfileCommand {get;} public RelayCommand BrowseCommand {get;} public RelayCommand RefreshCommand {get;}
 public RelayCommand LaunchCommand {get;} public RelayCommand SaveCommand {get;} public RelayCommand TestBindingCommand {get;}
 public RelayCommand StopBindingCommand {get;} public RelayCommand RestoreCommand {get;} public RelayCommand DeleteProfileCommand {get;}
 public RelayCommand RestartElevatedCommand {get;} public RelayCommand MoveUpCommand {get;} public RelayCommand MoveDownCommand {get;}
 public RelayCommand EnableCommand {get;}
 public MainViewModel() {
  ProfilesView=CollectionViewSource.GetDefaultView(Profiles);ProfilesView.Filter=value=>value is ProgramEntry app&&IsProgramVisible(app.Id);
  ActiveBindingsView=CollectionViewSource.GetDefaultView(ActiveBindings);ActiveBindingsView.Filter=value=>value is RuleDisplay rule&&IsProgramVisible(rule.Id);
  _selectedMode=Modes[0];
  NewProfileCommand=new RelayCommand(async()=>await PickApplications(),()=>!IsBusy);
  BrowseCommand=NewProfileCommand;
  RefreshCommand=new RelayCommand(async()=>await Refresh(),()=>!IsBusy);
  LaunchCommand=new RelayCommand(async()=>await SetRule("Apply"),()=>!IsBusy&&SelectedProfile!=null&&_state!=null);
  SaveCommand=new RelayCommand(async()=>await SetRule("Save"),()=>!IsBusy&&SelectedProfile!=null&&_state!=null);
  TestBindingCommand=new RelayCommand(async()=>await SetRule("Check"),()=>!IsBusy&&SelectedProfile!=null&&_state!=null);
  RestoreCommand=new RelayCommand(async()=>await RestoreAll(),()=>!IsBusy);
  EnableCommand=new RelayCommand(async()=>await Execute(new{Action="Enable",Revision=_state?.Revision}),()=>!IsBusy&&_state!=null);
  StopBindingCommand=new RelayCommand(async p=>{if(p is RuleDisplay row)await Execute(new{Action="SetRuleEnabled",Id=row.Id,Enabled=!row.Enabled,Revision=_state?.Revision});},_=>!IsBusy&&_state!=null);
  DeleteProfileCommand=new RelayCommand(async()=>{if(SelectedProfile!=null&&MessageBox.Show("要從清單移除這個程式及其規則嗎？","移除程式",MessageBoxButton.OKCancel)==MessageBoxResult.OK)await Execute(new{Action="Delete",Id=SelectedProfile.Id,Revision=_state?.Revision});},()=>!IsBusy&&SelectedProfile!=null&&_state!=null);
  RestartElevatedCommand=new RelayCommand(()=>{
   try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas"});System.Windows.Application.Current.Shutdown();}catch(Exception ex){RouterBridge.LogError(ex,"以管理員重新開啟");Status=ex.Message;}
  });
  MoveUpCommand=new RelayCommand(p=>MoveNetwork(p,-1),_=>!IsBusy);
  MoveDownCommand=new RelayCommand(p=>MoveNetwork(p,1),_=>!IsBusy);
  var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};var nextPoll=DateTime.MinValue;
  timer.Tick+=async (_,_)=>{RebuildRows();OnPropertyChanged(nameof(EngineState));OnPropertyChanged(nameof(DnsStatus));if(DateTime.UtcNow<nextPoll)return;nextPoll=DateTime.UtcNow.AddSeconds(Math.Clamp(OptionInt("PollSeconds",2),1,30));if(Option("AutoRefreshProcesses",true))await RefreshRunningPrograms();if(Option("AutoRefreshStatus",true))PollStatus();if(Option("AutoRefreshNetworks",true))await RefreshDetectedNetworks();};timer.Start();
 }
 private bool NeedsPathCheck(int id)=>_health.Any(h=>h.Id==id&&h.ExactOnly&&h.Paths.Any(p=>!p.Exists));
 public int PathWarningCount=>Profiles.Count(p=>NeedsPathCheck(p.Id));
 public bool HasPathWarnings=>PathWarningCount>0;
 public string PathWarningText=>$"{PathWarningCount} 個程式位置需要檢查：目前找不到或無法讀取。這些程式會保留在清單中。";
 private bool IsProgramVisible(int id)=>ShowInactivePrograms||_runningProfileIds.Contains(id)||SelectedProfile?.Id==id||NeedsPathCheck(id);
 private void RefreshProgramViews() {
  ProfilesView.Refresh();ActiveBindingsView.Refresh();
  OnPropertyChanged(nameof(PathWarningCount));OnPropertyChanged(nameof(HasPathWarnings));OnPropertyChanged(nameof(PathWarningText));OnPropertyChanged(nameof(HasVisibleProfiles));OnPropertyChanged(nameof(HasActiveBindings));OnPropertyChanged(nameof(VisibleRuleCount));OnPropertyChanged(nameof(EmptyRulesText));
 }
 private void MatchRunningPrograms() {
  var running=new HashSet<int>();
  foreach(var app in Profiles){
   if(app.Paths.Any(_runningPaths.Contains)){running.Add(app.Id);continue;}
   if(string.IsNullOrWhiteSpace(app.Pattern))continue;
   try {var pattern=new Regex(app.Pattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));if(_runningPaths.Any(pattern.IsMatch))running.Add(app.Id);}catch(ArgumentException){}catch(RegexMatchTimeoutException){}
  }
  _runningProfileIds=running;
 }
 internal void UpdateRunningPrograms(IEnumerable<string> paths) {
  _runningPaths=new HashSet<string>(paths,StringComparer.OrdinalIgnoreCase);MatchRunningPrograms();
  if(SelectedProfile==null&&!_dirty)SelectedProfile=Profiles.FirstOrDefault(p=>IsProgramVisible(p.Id));
  RefreshProgramViews();
 }
 private async Task RefreshRunningPrograms() {
  if(_preview||IsBusy||_readingRunningPrograms)return;
  _readingRunningPrograms=true;
  try {var paths=await Task.Run(()=>RunningApplications.ReadPaths());if(!_runningPaths.SetEquals(paths))UpdateRunningPrograms(paths);_lastRunningError=null;}
  catch(Exception error){if(_lastRunningError!=error.ToString()){RouterBridge.LogError(error,"更新執行中的程式");_lastRunningError=error.ToString();}}
  finally{_readingRunningPrograms=false;}
 }
 private void MoveNetwork(object? value,int delta){if(value is NetworkChoice choice){int index=Networks.IndexOf(choice);int target=index+delta;if(target>=0&&target<Networks.Count){Networks.Move(index,target);_dirty=true;}}}
 private bool ConfirmDiscard()=>ConfirmDiscardChanges?.Invoke()??(!_preview&&MessageBox.Show("尚有未保存的選擇，要放棄這些選擇嗎？","尚未保存的變更",MessageBoxButton.OKCancel,MessageBoxImage.Question)==MessageBoxResult.OK);
 private void UpdateNetworkCatalog(BridgeResponse reply) {
  _catalog=(reply.NetworkCatalog.Length>0?reply.NetworkCatalog:reply.State?.NetworkCatalog??[]).GroupBy(n=>n.Id).Select(g=>g.First()).ToArray();
  if(_catalog.Length==0){
   var ids=reply.Snapshot.Select(n=>n.Id).Concat(reply.State?.Rules.SelectMany(r=>r.Networks)??[]).Distinct();
   _catalog=ids.Select(id=>{var snapshot=reply.Snapshot.FirstOrDefault(n=>n.Id==id);return new NetworkCatalogEntry{Id=id,Name=string.IsNullOrWhiteSpace(snapshot?.Name)?RouterBridge.NetworkName(id):snapshot.Name,Alias=snapshot?.Alias??"",Present=snapshot!=null,Up4=snapshot?.Up4??false,Up6=snapshot?.Up6??false};}).ToArray();
  }
  _fullCatalog=_catalog.ToArray();
  var retainedIds=(_state?.Rules.SelectMany(r=>r.Networks)??[]).Concat(Networks.Where(n=>n.Selected).Select(n=>n.Id)).ToHashSet();
  _catalog=_catalog.Where(n=>n.Present||reply.Snapshot.Any(s=>s.Id==n.Id)||retainedIds.Contains(n.Id)).ToArray();
  foreach(var choice in Networks.Where(n=>!_catalog.Any(c=>c.Id==n.Id)).ToArray())Networks.Remove(choice);
  foreach(var network in _catalog){
   var choice=Networks.FirstOrDefault(n=>n.Id==network.Id);
   if(choice==null){choice=new(){Id=network.Id};choice.PropertyChanged+=(_,change)=>{if(change.PropertyName==nameof(NetworkChoice.Selected))_dirty=true;};Networks.Add(choice);}
   choice.Name=RouterBridge.NetworkName(network.Id,_catalog);
   var snapshot=reply.Snapshot.FirstOrDefault(n=>n.Id==network.Id);
   bool present=snapshot!=null||network.Present;bool up=snapshot==null?network.Up4||network.Up6:snapshot.Up4||snapshot.Up6;
   choice.Detail=!present?"目前找不到網卡":(snapshot?.Alias??network.Alias)+"  "+(up?"已連線，有可用路由":"未連線");
  }
 }
 private void ReorderNetworks(IEnumerable<int> ids) {
  var order=ids.Where(id=>Networks.Any(n=>n.Id==id)).Concat(Networks.Select(n=>n.Id)).Distinct().ToArray();
  for(int i=0;i<order.Length;i++){var choice=Networks.First(n=>n.Id==order[i]);Networks.Move(Networks.IndexOf(choice),i);}
 }
 private sealed record EditorDraft(int? Id,string? Mode,int[] Order,int[] Selected,bool Dirty);
 private EditorDraft CaptureEditor()=>new(SelectedProfile?.Id,SelectedMode?.Value,Networks.Select(n=>n.Id).ToArray(),Networks.Where(n=>n.Selected).Select(n=>n.Id).ToArray(),_dirty);
 private void RestoreEditor(EditorDraft draft) {
  if(draft.Id!=SelectedProfile?.Id)return;
  SelectedMode=Modes.FirstOrDefault(m=>m.Value==draft.Mode)??Modes[0];
  ReorderNetworks(draft.Order);
  foreach(var choice in Networks)choice.Selected=draft.Selected.Contains(choice.Id);
  _dirty=draft.Dirty;
 }
 private void LoadEditor() {
  OnPropertyChanged(nameof(Name));OnPropertyChanged(nameof(ProgramPath));OnPropertyChanged(nameof(ProgramHealth));OnPropertyChanged(nameof(CanRebind));
  var rule=_state?.Rules.FirstOrDefault(r=>r.Id==SelectedProfile?.Id);
  SelectedMode=Modes.First(m=>m.Value==(rule?.Mode??"Allow"));
  ReorderNetworks(rule?.Mode=="Allow"?rule.Priority.Concat(rule.Networks):rule?.Networks??[]);
  foreach(var n in Networks)n.Selected=rule?.Networks.Contains(n.Id)??false;
  _dirty=false;
 }
 internal void Accept(BridgeResponse reply,bool preserveEditor=false) {
  var draft=preserveEditor?CaptureEditor():null;
  if(reply.State!=null){
   int? selected=SelectedProfile?.Id;var firstState=_state==null;_health=reply.AppHealth;var priorShowInactive=Option("ShowInactivePrograms",false);_state=reply.State;_reported=reply.Status;_acceptingState=true;
   try {
    UpdateNetworkCatalog(reply);
    Profiles.Clear();foreach(var p in _state.Apps.OrderBy(a=>a.Id)){var rule=_state.Rules.FirstOrDefault(r=>r.Id==p.Id);p.AdapterName=rule==null?"尚未設定規則":RouterBridge.AllowedText(rule,_catalog);Profiles.Add(p);}
    MatchRunningPrograms();
    SelectedProfile=Profiles.FirstOrDefault(p=>p.Id==selected)??Profiles.FirstOrDefault(p=>IsProgramVisible(p.Id));
    if(draft!=null)RestoreEditor(draft);
    OnPropertyChanged(nameof(ProgramHealth));OnPropertyChanged(nameof(CanRebind));OnPropertyChanged(nameof(DnsStatus));OnPropertyChanged(nameof(ProtectionNotice));if(firstState||priorShowInactive!=Option("ShowInactivePrograms",false))ShowInactivePrograms=Option("ShowInactivePrograms",false);RebuildRows();RefreshProgramViews();OnPropertyChanged(nameof(EngineState));PreferencesChanged?.Invoke();
   }finally{_acceptingState=false;}
   if(!_preview)_networkFingerprint=ReadNetworkFingerprint();
  }
  if(!string.IsNullOrEmpty(reply.Message))Status=reply.Message;
  else Status=_state?.Enabled==true?"規則已讀取，正常開啟程式即可套用限制。":"規則已讀取，目前尚未啟用限制。可按「保存並啟用限制」或選單內的「啟用所有已保存的規則」。";
 }
 private static string ReadNetworkFingerprint()=>string.Join("|",NetworkInterface.GetAllNetworkInterfaces().Where(n=>!n.Name.Equals("NetworkRouter",StringComparison.OrdinalIgnoreCase)).Select(n=>{
  var addresses=n.GetIPProperties();return n.Id+":"+n.Name+":"+n.OperationalStatus+":"+string.Join(",",addresses.UnicastAddresses.Select(a=>a.Address.ToString()).OrderBy(x=>x,StringComparer.Ordinal))+":"+string.Join(",",addresses.GatewayAddresses.Select(a=>a.Address.ToString()).OrderBy(x=>x,StringComparer.Ordinal));
 }).OrderBy(x=>x,StringComparer.Ordinal));
 private async Task RefreshDetectedNetworks() {
  if(_preview||IsBusy||_state==null||DateTime.UtcNow<_nextNetworkRefresh)return;
  try {
   var fingerprint=ReadNetworkFingerprint();
   if(_networkFingerprint==fingerprint)return;
   _nextNetworkRefresh=DateTime.UtcNow.AddSeconds(10);IsBusy=true;
   Accept(await RouterBridge.CallAsync(new{Action="Read"}),preserveEditor:true);
   Status="已自動更新網卡清單，尚未保存的選擇會保留。";
  }catch(Exception error){RouterBridge.LogError(error,"自動更新網卡清單");Status="網卡清單更新失敗，可按「重新整理」重試。";}
  finally{IsBusy=false;}
 }
 private void RebuildRows() {
  ActiveBindings.Clear();
  if(_state!=null)foreach(var r in _state.Rules){
   var app=_state.Apps.FirstOrDefault(a=>a.Id==r.Id);if(app==null)continue;
   bool fresh=_state.Enabled&&_reported?.Phase=="Ready"&&_reported.Revision==_state.Revision&&(DateTime.Now-_reported.Time).TotalSeconds >= -5&&(DateTime.Now-_reported.Time).TotalSeconds < 15;
   var row=fresh?_reported?.Rows.FirstOrDefault(x=>x.Id==r.Id):null;
   var current=!r.Enabled?"此規則已暫停，設定保留":!_state.Enabled?"已保存，目前未啟用":row==null?"等待分流器回報狀態":$"IPv4 {RouterBridge.TranslateNetwork(row.IPv4)}（{(string.IsNullOrWhiteSpace(row.Reason4)?"原因未確認":row.Reason4)}）    IPv6 {RouterBridge.TranslateNetwork(row.IPv6)}（{(string.IsNullOrWhiteSpace(row.Reason6)?"原因未確認":row.Reason6)}）";
   ActiveBindings.Add(new(){Id=r.Id,Display=app.Name,Sub=RouterBridge.AllowedText(r,_catalog),Current=current,Enabled=r.Enabled});
  }
  OnPropertyChanged(nameof(HasActiveBindings));OnPropertyChanged(nameof(VisibleRuleCount));
 }
 private void PollStatus() {
  if(IsBusy||_state==null)return;
  try {
   var stateFile=Path.Combine(RouterBridge.DataRoot,"rules.json");
   if(File.Exists(stateFile)&&RouterBridge.ReadStateRevision(SharedFileReader.ReadAllText(stateFile))!=_state.Revision){_reported=null;Status="設定已在其他視窗變更，請按「重新整理」讀取。";return;}
   var path=Path.Combine(RouterBridge.DataRoot,"status.json");_reported=File.Exists(path)?JsonSerializer.Deserialize<RouterStatus>(SharedFileReader.ReadAllText(path),RouterBridge.JsonOptions):null;
   if(_lastPollError!=null&&Status.StartsWith("無法讀取狀態："))Status="狀態讀取已恢復。";
   _lastPollError=null;
   if(_state.Enabled&&_reported?.Phase=="Error")Status="分流錯誤："+_reported.Message+"  需要時請按「恢復網路」。";
  }catch(Exception ex){if(_lastPollError!=ex.ToString()){RouterBridge.LogError(ex,"讀取狀態");_lastPollError=ex.ToString();}Status="無法讀取狀態："+ex.Message+"  仍可使用「恢復網路」。";}
  finally{RebuildRows();OnPropertyChanged(nameof(EngineState));OnPropertyChanged(nameof(DnsStatus));}
 }
 public async Task Refresh() {
  if(_dirty&&!ConfirmDiscard())return;
  await Execute(new{Action="Read"});
  await RefreshRunningPrograms();
 }
 private async Task SetRule(string action) {
  if(SelectedProfile==null)return;
  await Execute(new{Action=action,Id=SelectedProfile.Id,Revision=_state?.Revision,Mode=SelectedMode?.Value,Networks=Networks.Where(n=>n.Selected).Select(n=>n.Id).ToArray()});
 }
 public async Task Execute(object request) {
  if(IsBusy)return;IsBusy=true;Status="正在處理";
  try{Accept(await RouterBridge.CallAsync(request));}catch(Exception ex){
   bool logged=RouterBridge.LogError(ex,"執行設定");
   try{
    if(ex is RouterOperationException failure&&failure.Response.State!=null)Accept(failure.Response,preserveEditor:true);
    else Accept(await RouterBridge.CallAsync(new{Action="Read"}),preserveEditor:true);
   }catch(Exception refreshError){RouterBridge.LogError(refreshError,"操作失敗後重新讀取設定");}
   Status="執行失敗："+ex.Message;
   if(!_preview)MessageBox.Show(ex.Message+"\n\n需要時請按「恢復網路」。"+(logged?"詳細錯誤已保存到 gui-errors.log。":"錯誤記錄無法保存，請保留此錯誤訊息。"),"三網路規則管理",MessageBoxButton.OK,MessageBoxImage.Error);
  }
  finally{IsBusy=false;}
 }
 internal async Task Rebind(int id,string path){if(IsBusy)throw new InvalidOperationException("其他操作尚未完成。");IsBusy=true;try{Accept(await RouterBridge.CallAsync(new{Action="RebindApplication",Id=id,Path=path,Revision=_state?.Revision}),preserveEditor:true);}catch(RouterOperationException error){if(error.Response.State!=null)Accept(error.Response,preserveEditor:true);throw;}finally{IsBusy=false;}}
 public async Task RestoreAll()=>await Execute(new{Action="Restore"});
 public async Task ResetRules() {
  if(MessageBox.Show("要刪除全部規則並停止程式分流嗎？程式清單會保留；獨立計費網卡保護仍會保留。","重設全部規則",MessageBoxButton.OKCancel,MessageBoxImage.Question)==MessageBoxResult.OK)await Execute(new{Action="Reset"});
 }
 public async Task ShowLogs() {
  if(IsBusy)return;IsBusy=true;
  try{var response=await RouterBridge.CallAsync(new{Action="Logs"});var window=new TextWindow("執行記錄",string.IsNullOrEmpty(response.Message)?"目前沒有記錄。":response.Message){Owner=System.Windows.Application.Current.MainWindow};window.Show();}catch(Exception ex){RouterBridge.LogError(ex,"查看執行記錄");Status=ex.Message;}finally{IsBusy=false;}
 }
 private async Task PickApplications() {
  if(IsBusy)return;IsBusy=true;Status="請勾選目前正在執行的程式";
  ApplicationEntry[]? selected=null;
  try{var picker=new AppPickerWindow(includeBackground:Option("IncludeBackgroundPrograms",false),autoRefresh:Option("AutoRefreshProcesses",true)){Owner=System.Windows.Application.Current.MainWindow};if(picker.ShowDialog()==true)selected=picker.Selection;}
  catch(Exception ex){RouterBridge.LogError(ex,"讀取應用程式");Status="無法讀取應用程式："+ex.Message;if(!_preview)MessageBox.Show(ex.Message,"應用程式");}
  finally{IsBusy=false;}
  if(selected?.Length>0){
   if(_dirty&&!ConfirmDiscard()){Status="已保留尚未保存的選擇，未新增程式。";return;}
   await Execute(new{Action="Add",Revision=_state?.Revision,Applications=selected});
   var paths=selected.Select(entry=>entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
   var profile=Profiles.FirstOrDefault(p=>p.Paths.Any(paths.Contains));
   profile??=Profiles.FirstOrDefault(p=>!string.IsNullOrWhiteSpace(p.Pattern)&&paths.Any(path=>{
    try{return System.Text.RegularExpressions.Regex.IsMatch(path,p.Pattern,System.Text.RegularExpressions.RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(200));}catch(ArgumentException){return false;}catch(System.Text.RegularExpressions.RegexMatchTimeoutException){return false;}
   }));
   if(profile!=null&&!_dirty)SelectedProfile=profile;
  }else Status="未新增程式。";
 }
 public void SetPreview() {
  _preview=true;OnPropertyChanged(nameof(ShowElevationWarning));
  _runningPaths=new([@"C:\Example\Game\cs2.exe",@"C:\Example\Browser\browser.exe"],StringComparer.OrdinalIgnoreCase);
  Accept(new(){State=new(){Revision=1,Enabled=false,Apps=[new(){Id=14,Name="CS2",Paths=[@"C:\Example\Game\cs2.exe"]},new(){Id=2,Name="Chrome",Paths=[@"C:\Example\Browser\browser.exe"]}],Rules=[new(){Id=14,Mode="Allow",Networks=[3],Priority=[3]},new(){Id=2,Mode="Allow",Networks=[2,1],Priority=[2,1]}]},Snapshot=[new(){Id=1,Alias="Wi-Fi 2",Up4=true},new(){Id=2,Alias="Ethernet 2",Up4=true},new(){Id=3,Alias="Ethernet",Up4=true}]});
  Status="介面示範，未啟用任何網路限制";
 }
}
