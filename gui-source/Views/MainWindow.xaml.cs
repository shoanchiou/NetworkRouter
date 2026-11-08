using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BindIPManager.ViewModels;
namespace BindIPManager.Views;
// MainWindow.xaml and Theme.xaml are adapted from BindRouter v1.2.0, MIT, Aydin Farro.
public partial class MainWindow : Window {
 private Services.TrayController? _paidProtection;
 public MainWindow() { InitializeComponent();if(App.IsOfflineMode){PaidStatus.Text="離線檢查 未啟用保護";PaidDetail.Text="此預覽不會讀取或變更計費網路保護。";} }
 public void AttachPaidProtection(Services.TrayController controller){
  _paidProtection=controller;if(DataContext is MainViewModel vm){void Apply()=>controller.ApplyPreferences(vm.Option("ShowTray",true),vm.Option("CloseToTray",true),vm.Option("MinimizeToTray",true),vm.Option("AutoRefreshStatus",true),vm.OptionInt("PollSeconds",2));vm.PreferencesChanged+=Apply;Apply();}controller.Changed+=(_,_)=>UpdatePaidProtection();UpdatePaidProtection();
 }
 private void UpdatePaidProtection(){
  if(_paidProtection is not {} guard)return;
  PaidStatus.Text=guard.IsBusy?"正在處理 計費網卡保護":guard.StatusText;
  PaidDetail.Text=guard.DetailText;
  PaidAllowButton.IsEnabled=guard.CanAllow;PaidBlockButton.IsEnabled=guard.CanBlock;
  PaidInstallButton.IsEnabled=guard.CanInstall;
  PaidInstallButton.Visibility=guard.Status is not {Installed:true,ServiceRunning:true}||guard.Error.Length>0?Visibility.Visible:Visibility.Collapsed;
  PaidIndicator.Fill=new SolidColorBrush(guard.Error.Length>0||guard.Status is not {Installed:true,ServiceRunning:true}?Color.FromRgb(148,163,184):guard.Status.Locked?Color.FromRgb(226,90,93):Color.FromRgb(52,211,153));
 }
 private async void OnPaidAllow(object sender,RoutedEventArgs e){if(_paidProtection is {} guard)await guard.Change("allow");}
 private async void OnPaidBlock(object sender,RoutedEventArgs e){if(_paidProtection is {} guard)await guard.Change("block");}
 private async void OnPaidInstall(object sender,RoutedEventArgs e){if(_paidProtection is not {} guard)return;if(guard.Status is {Installed:true,Guid.Length:>0} status)await guard.Install(status.Guid);else OnOptions(sender,e);}
 private void OnDiagnostics(object sender,RoutedEventArgs e)=>ShowDiagnostics();
 private void ShowDiagnostics(){if(DataContext is MainViewModel vm){var owner=System.Windows.Application.Current.Windows.OfType<OptionsWindow>().FirstOrDefault(w=>w.IsActive) as Window??this;new DiagnosticsWindow(vm){Owner=owner}.ShowDialog();}}
 private void OnRebind(object sender,RoutedEventArgs e){if(DataContext is MainViewModel vm&&vm.CanRebind&&vm.SelectedProfile is {} app)new RebindWindow(vm,app.Id){Owner=this}.ShowDialog();}
 private async void OnOptions(object sender,RoutedEventArgs e){
  if(DataContext is not MainViewModel vm||vm.StateRevision==null||vm.IsBusy)return;
  try {var dialog=new OptionsWindow(OptionsWindow.LoadSchema(),vm.Options,vm.SaveOptions,vm.Catalog,_paidProtection,ShowDiagnostics){Owner=this};dialog.ShowDialog();}
  catch(Exception error){MessageBox.Show("無法讀取功能設定："+error.Message,"所有功能設定",MessageBoxButton.OK,MessageBoxImage.Error);}
 }
 private void OnMoreMenu(object sender,RoutedEventArgs e){if(sender is FrameworkElement fe&&fe.ContextMenu is {} menu){menu.PlacementTarget=fe;menu.IsOpen=true;}}
 private async void OnRestore(object sender,RoutedEventArgs e){if(DataContext is MainViewModel vm)await vm.RestoreAll();}
 private async void OnReset(object sender,RoutedEventArgs e){if(DataContext is MainViewModel vm)await vm.ResetRules();}
 private void OnEnable(object sender,RoutedEventArgs e){if(DataContext is MainViewModel vm&&vm.EnableCommand.CanExecute(null))vm.EnableCommand.Execute(null);}
 private async void OnLogs(object sender,RoutedEventArgs e){if(DataContext is MainViewModel vm)await vm.ShowLogs();}
 private void OnMinimizeToTray(object sender,RoutedEventArgs e){if(_paidProtection is {} guard)guard.HideSettings();else WindowState=WindowState.Minimized;}
 private void OnExit(object sender,RoutedEventArgs e){Close();}
 private void OnExitTray(object sender,RoutedEventArgs e){if(_paidProtection is {} guard)guard.Exit();else Close();}
 public static void CheckActualState() {
  var response=Task.Run(()=>Services.RouterBridge.CallAsync(new{Action="Read"})).GetAwaiter().GetResult();
  if(response.State==null)throw new InvalidOperationException("無法讀取實際設定。");
  var window=new MainWindow();var model=(MainViewModel)window.DataContext;
  model.Accept(response);model.UpdateRunningPrograms(Services.RunningApplications.ReadPaths());
  if(response.State.Enabled||response.State.Rules.Length!=0)throw new InvalidOperationException("完成狀態應為限制關閉且規則清空。");
  if(model.Networks.Count==0)throw new InvalidOperationException("無法顯示實際動態網卡。");
  var output=Path.Combine(Path.GetTempPath(),"UnifiedNetworkRouter-preview");Directory.CreateDirectory(output);
  File.WriteAllText(Path.Combine(output,"Actual-state-check.txt"),$"PASS actual bridge response and view model load\nSaved programs {model.Profiles.Count}\nVisible running programs {model.ProfilesView.Cast<object>().Count()}\nDynamic adapter choices {model.Networks.Count}\nSaved rules {response.State.Rules.Length}\nRouting enabled {response.State.Enabled}\nRead-only inspection; no restriction enabled.");
 }
 public static void CheckInterface() {
  if(Services.RouterBridge.NetworkName(1)!="網路 1"||Services.RouterBridge.NetworkName(2)!="網路 2"||Services.RouterBridge.NetworkName(3)!="網路 3"||Services.RouterBridge.NetworkName(1,[new(){Id=1,Name="Ethernet"}])!="Ethernet"||new Services.PaidNetworkStatus().AdapterName!="未選擇網卡")throw new InvalidOperationException("網卡名稱未使用目錄或通用預設值。");
  var paidLocked=Services.PaidNetworkClient.ParseStatus("{\"Installed\":true,\"Locked\":true,\"ServiceRunning\":true,\"AdapterName\":\"計費網卡\"}",0,"");
  if(!paidLocked.Installed||!paidLocked.Locked||!paidLocked.ServiceRunning)throw new InvalidOperationException("無法讀取計費有線保護狀態。");
  foreach(var broken in new[]{"{}","{\"Installed\":false,\"Locked\":false}","{\"Installed\":true,\"Locked\":true,\"ServiceRunning\":true,\"Error\":\"拒絕存取\"}"}) {
   try {Services.PaidNetworkClient.ParseStatus(broken,0,"");throw new InvalidOperationException("無效計費保護狀態不應被接受。");}catch(IOException){}
  }
  try {Services.PaidNetworkClient.ParseStatus("{\"Installed\":true,\"Locked\":true,\"ServiceRunning\":true}",1,"操作失敗");throw new InvalidOperationException("失敗的保護操作不應顯示成功。");}catch(IOException){}
  AppPickerWindow.CheckSelectionPersistence();
  var visibleProcesses=Services.RunningApplications.Read(false);
  var backgroundProcesses=Services.RunningApplications.Read(true);
  var runningPaths=Services.RunningApplications.ReadPaths();
  if(visibleProcesses.Any(p=>string.IsNullOrWhiteSpace(p.Path))||visibleProcesses.Select(p=>p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=visibleProcesses.Length)throw new InvalidOperationException("執行中程式清單包含空白或重複路徑。");
  var probeOutput=Path.Combine(Path.GetTempPath(),"UnifiedNetworkRouter-preview");Directory.CreateDirectory(probeOutput);
  File.WriteAllText(Path.Combine(probeOutput,"Running-program-check.txt"),$"Visible applications {visibleProcesses.Length}\nIncluding background {backgroundProcesses.Length}\nRunning executable paths {runningPaths.Count}\nPicker selection persistence passed\nRead-only process discovery; no programs started or stopped.");
  foreach(var time in new[]{"2026-10-08 23:48:43","2026-10-08T23:48:43"}) {
   var reply=System.Text.Json.JsonSerializer.Deserialize<Services.BridgeResponse>("{\"Ok\":true,\"Status\":{\"Time\":\""+time+"\"}}",Services.RouterBridge.JsonOptions);
   if(reply?.Status?.Time.Year!=2026)throw new InvalidOperationException("狀態時間格式相容檢查失敗。");
  }
  var invalid=System.Text.Json.JsonSerializer.Deserialize<Services.RouterStatus>("{\"Time\":\"invalid\"}",Services.RouterBridge.JsonOptions);
  if(invalid?.Time!=DateTime.MinValue)throw new InvalidOperationException("無效狀態時間未標記為過期。");
  if(Services.RouterBridge.ReadStateRevision("{\"Revision\":7,\"Rules\":[{\"Id\":14,\"Mode\":\"Deny\",\"Networks\":[3],\"Priority\":{}}]}")!=7)throw new InvalidOperationException("舊版封鎖規則不應影響版本輪詢。");
  var actualStatus=Path.Combine(Services.RouterBridge.DataRoot,"status.json");
  if(File.Exists(actualStatus))System.Text.Json.JsonSerializer.Deserialize<Services.RouterStatus>(File.ReadAllText(actualStatus),Services.RouterBridge.JsonOptions);
  var window=new MainWindow();var vm=(MainViewModel)window.DataContext;vm.SetPreview();vm.ConfirmDiscardChanges=()=>true;
  if(vm.SelectedMode?.ToString()!="僅允許選定的網路"||string.Join(",",vm.Networks.Where(n=>n.Selected).Select(n=>n.Id))!="2,1")throw new InvalidOperationException("Allow editor did not load priority correctly.");
  int detailChanges=0;var detailChoice=vm.Networks[0];detailChoice.PropertyChanged+=(_,change)=>{if(change.PropertyName==nameof(NetworkChoice.Detail))detailChanges++;};detailChoice.Detail="離線檢查網卡狀態";
  if(detailChanges!=1||vm.HasUnsavedChanges)throw new InvalidOperationException("更新網卡狀態應通知介面，但不應標記規則為未保存。");
  vm.MoveUpCommand.Execute(vm.Networks[1]);
  if(string.Join(",",vm.Networks.Where(n=>n.Selected).Select(n=>n.Id))!="1,2")throw new InvalidOperationException("Network priority move failed.");
  vm.ConfirmDiscardChanges=()=>false;vm.SelectedProfile=vm.Profiles.First(p=>p.Id==14);
  if(vm.SelectedProfile?.Id!=2||string.Join(",",vm.Networks.Where(n=>n.Selected).Select(n=>n.Id))!="1,2")throw new InvalidOperationException("取消切換程式應保留尚未保存的選擇。");
  vm.ConfirmDiscardChanges=()=>true;
  vm.SelectedMode=vm.Modes[1];if(!vm.NetworkHelp.Contains("封鎖"))throw new InvalidOperationException("Deny editor did not load.");
  vm.SelectedProfile=vm.Profiles.First(p=>p.Id==14);
  if(string.Join(",",vm.Networks.Where(n=>n.Selected).Select(n=>n.Id))!="3")throw new InvalidOperationException("Program selection did not load its own rule.");
  if(vm.ActiveBindings.Count!=2||vm.ActiveBindings.Any(r=>!r.Current.Contains("未啟用")))throw new InvalidOperationException("Result rows do not show saved-only state.");
  vm.SelectedMode=vm.Modes[1];vm.Networks.First(n=>n.Id==1).Selected=true;
  var draftOrder=string.Join(",",vm.Networks.Select(n=>n.Id));var draftSelection=string.Join(",",vm.Networks.Where(n=>n.Selected).Select(n=>n.Id));
  vm.ConfirmDiscardChanges=()=>throw new InvalidOperationException("重新載入已保存狀態不應跳出程式切換確認。");
  vm.Accept(new(){State=new(){Revision=2,Enabled=false,Apps=[new(){Id=14,Name="CS2",Paths=[@"D:\Example\cs2.exe"]}],Rules=[new(){Id=14,Mode="Allow",Networks=[2],Priority=[2]}]},NetworkCatalog=[new(){Id=1,Name="Wi-Fi",Present=true},new(){Id=2,Name="iPhone 有線",Present=true},new(){Id=3,Name="計費網卡",Present=true},new(){Id=4,Name="USB 備用網路",Alias="Ethernet 4",Present=true,Up4=true}]},preserveEditor:true);
  if(vm.StateRevision!=2||vm.SelectedMode?.Value!="Deny"||!vm.HasUnsavedChanges||string.Join(",",vm.Networks.Take(3).Select(n=>n.Id))!=draftOrder||string.Join(",",vm.Networks.Where(n=>n.Selected).Select(n=>n.Id))!=draftSelection)throw new InvalidOperationException("失敗後重新讀取設定應更新版本並保留原編輯草稿。");
  if(vm.Networks.Count!=4||vm.Networks.Single(n=>n.Id==4).Name!="USB 備用網路"||!vm.Networks.Single(n=>n.Id==4).Detail.Contains("有可用路由"))throw new InvalidOperationException("動態網卡清單應顯示第四張網卡與名稱。");
  var dynamicAllowed=Services.RouterBridge.AllowedText(new(){Id=14,Mode="Deny",Networks=[1,2,3]},[new(){Id=1,Name="Wi-Fi"},new(){Id=2,Name="iPhone 有線"},new(){Id=3,Name="計費網卡"},new(){Id=4,Name="USB 備用網路"}]);
  if(!dynamicAllowed.Contains("USB 備用網路")||dynamicAllowed.Contains("計費網卡"))throw new InvalidOperationException("封鎖模式應使用完整動態網卡清單。");
  vm.Accept(new(){State=new(){Revision=3,Enabled=false}},preserveEditor:true);
  if(vm.Networks.Count!=0||vm.SelectedProfile!=null||vm.ActiveBindings.Count!=0)throw new InvalidOperationException("沒有偵測到網卡或程式時，清單應保持空白且可讀取。");
  if(new Services.ApplicationEntry{Name="Unavailable",Available=false}.CanSelect||new Services.ApplicationEntry{Name="Missing path"}.CanSelect||!new Services.ApplicationEntry{Name="Available",Path=@"C:\Example\app.exe"}.CanSelect)throw new InvalidOperationException("應用程式清單應排除未取得執行檔的項目。");
  var live=new MainViewModel();live.SetPreview();live.ConfirmDiscardChanges=()=>throw new InvalidOperationException("動態更新程式清單不應跳出確認視窗。");
  var chrome=live.Profiles.Single(p=>p.Id==2);var cs2=live.Profiles.Single(p=>p.Id==14);
  live.UpdateRunningPrograms(chrome.Paths);
  if(live.ShowInactivePrograms||live.ProfilesView.Cast<Services.ProgramEntry>().Single().Id!=2||live.VisibleRuleCount!=1||live.Profiles.Count!=2||live.ActiveBindings.Count!=2)throw new InvalidOperationException("預設只應顯示執行中的已加入程式，保存的程式與規則不能被刪除。");
  live.SelectedProfile=cs2;live.SelectedMode=live.Modes[1];live.Networks.Single(n=>n.Id==1).Selected=true;
  live.UpdateRunningPrograms([]);
  if(live.SelectedProfile!=cs2||!live.HasUnsavedChanges||live.ProfilesView.Cast<Services.ProgramEntry>().Single()!=cs2||live.VisibleRuleCount!=1)throw new InvalidOperationException("關閉程式後應保留目前正在編輯的程式與未保存草稿。");
  live.ShowInactivePrograms=true;
  if(live.ProfilesView.Cast<Services.ProgramEntry>().Count()!=2||live.VisibleRuleCount!=2||!live.HasUnsavedChanges)throw new InvalidOperationException("顯示未執行程式應還原完整清單，不可覆蓋草稿。");
  live.ShowInactivePrograms=false;live.ConfirmDiscardChanges=()=>true;live.SelectedProfile=chrome;
  if(live.ProfilesView.Cast<Services.ProgramEntry>().Single()!=chrome||live.VisibleRuleCount!=1)throw new InvalidOperationException("切換編輯其他程式後，先前已關閉的程式應自動隱藏。");
  live.SelectedProfile=null;
  if(live.HasVisibleProfiles||live.HasActiveBindings||live.VisibleRuleCount!=0||live.Profiles.Count!=2||live.ActiveBindings.Count!=2)throw new InvalidOperationException("沒有執行中程式時應顯示空清單，並保留全部已保存規則。");
  live.UpdateRunningPrograms(chrome.Paths);
  if(live.SelectedProfile!=chrome||!live.HasVisibleProfiles||live.VisibleRuleCount!=1)throw new InvalidOperationException("重新開啟程式後應自動重新出現在主畫面。");
  live.Accept(new(){State=new(){Revision=9,Apps=[new(){Id=30,Name="Versioned app",Pattern=@"(?i)^C:\\Example\\[^\\]+\\app\.exe$"}],Rules=[new(){Id=30,Mode="Allow",Networks=[1],Priority=[1]}]}});
  live.UpdateRunningPrograms([@"C:\Example\v2\app.exe"]);
  if(live.SelectedProfile?.Id!=30||live.VisibleRuleCount!=1)throw new InvalidOperationException("已保存的版本路徑規則也應能辨識執行中的程式。");
  var picker=new AppPickerWindow([new(){Name="Example app",Path=@"C:\Example\app.exe"}]);
  picker.Measure(new Size(900,580));picker.Arrange(new Rect(0,0,900,580));picker.UpdateLayout();
  var log=new TextWindow("執行記錄","Example log");log.Measure(new Size(900,600));log.Arrange(new Rect(0,0,900,600));log.UpdateLayout();
  var output=Path.Combine(Path.GetTempPath(),"UnifiedNetworkRouter-preview");Directory.CreateDirectory(output);
  File.WriteAllText(Path.Combine(output,"Interface-check.txt"),"PASS original XAML loads\nPASS Allow and Deny editor\nPASS selected program loads its own rule\nPASS network priority moves\nPASS saved-only result rows\nPASS network detail change notifications\nPASS unsaved program selection confirmation\nPASS failure refresh preserves draft and updates revision\nPASS legacy Deny priority revision polling\nPASS dynamic network catalog and fourth adapter\nPASS empty dynamic catalog\nPASS unavailable application selection guard\nPASS running program filter preserves saved profiles and rules\nPASS closed edited program preserves draft\nPASS show inactive toggle preserves draft and visible rule count\nPASS previous closed selection disappears after selecting another\nPASS empty running list and automatic reappearance\nPASS versioned application regex matches running path\nPASS Windows application picker and logs load\nNo routing or connection test started.");
 }
 public static void CheckImprovements(){
  var schema=OptionsWindow.LoadSchema();var vm=new MainViewModel();vm.SetPreview();var options=new OptionsWindow(schema,new Dictionary<string,System.Text.Json.JsonElement>(),_=>Task.CompletedTask,[],null);
  if(schema.Length!=36||!options.ActiveTabIsVisible())throw new InvalidOperationException("設定應保留 36 項且顯示目前頁籤。");var basic=options.DisplayedKeys();options.SwitchPage(true);if(!options.ActiveTabIsVisible())throw new InvalidOperationException("切換後頁籤狀態未更新。");var advanced=options.DisplayedKeys();if(basic.Intersect(advanced).Any()||basic.Concat(advanced).Distinct().Count()!=schema.Length)throw new InvalidOperationException("設定分頁未完整涵蓋所有項目。");
  options.SearchFor("DnsIPv4Servers");if(options.DisplayedKeys().SingleOrDefault()!="DnsIPv4Servers")throw new InvalidOperationException("搜尋必須跨頁。");
  vm.Accept(new(){State=new(){Revision=12,Enabled=true,Apps=[new(){Id=14,Name="Example",Paths=[@"C:\Example\missing.exe"]}],Rules=[new(){Id=14,Enabled=true,Networks=[1],Priority=[1]}]},AppHealth=[new(){Id=14,ExactOnly=true,Scope="Exact",Paths=[new(){Path=@"C:\Example\missing.exe",Exists=false}]}],Status=new(){Revision=12,Phase="Ready",Time=DateTime.Now,Rows=[new(){Id=14,IPv4="WiFi",IPv6="斷線",Reason4="目前使用 Wi-Fi",Reason6="網卡未連線"}],Dns=new(){Route="Wi-Fi",Reason="尚未測試解析"}}});
  vm.UpdateRunningPrograms([]);if(!vm.HasPathWarnings||vm.ProfilesView.Cast<Services.ProgramEntry>().Count()!=1||!vm.CanRebind||!vm.ProgramHealth.Contains("無法讀取"))throw new InvalidOperationException("失效 exact 程式必須保留在清單並可重綁。");
  if(!vm.ActiveBindings.Single().Current.Contains("網卡未連線")||!vm.DnsStatus.Contains("尚未測試解析"))throw new InvalidOperationException("新鮮回報原因未顯示。");
  vm.Accept(new(){State=new(){Revision=13,Enabled=true,Apps=[new(){Id=14,Name="Example"}],Rules=[new(){Id=14,Networks=[1],Priority=[1]}]},Status=new(){Revision=12,Phase="Ready",Time=DateTime.Now,Rows=[new(){Id=14,Reason4="不應顯示舊狀態"}]}});if(vm.ActiveBindings.Single().Current.Contains("不應顯示舊狀態")||!vm.DnsStatus.Contains("尚未確認"))throw new InvalidOperationException("不符版本的狀態不可顯示為實況。");
  vm.Accept(new(){State=new(){Revision=14},NetworkCatalog=[new(){Id=1,Name="在線",Guid="11111111-1111-1111-1111-111111111111",Present=true},new(){Id=2,Name="離線",Guid="22222222-2222-2222-2222-222222222222",Present=false}]});var diag=new DiagnosticsWindow(vm);if(diag.AdapterGuids().Length!=2)throw new InvalidOperationException("診斷應列出完整有效 GUID 目錄，包括離線網卡。");if(!OptionsWindow.DisplayLabelsAreClean()||diag.AdapterLabels().Any(x=>x.Contains("AdapterItem")||x.Contains("BindIPManager")||x.Contains("Entry =")))throw new InvalidOperationException("下拉選單應顯示清楚名稱，而非內部物件資訊。");diag.Measure(new Size(760,580));diag.Arrange(new Rect(0,0,760,580));diag.UpdateLayout();
  var output=Path.Combine(Path.GetTempPath(),"UnifiedNetworkRouter-preview");Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"Improvements-check.txt"),"PASS 36 options, active tab, and cross-page search\nPASS diagnostic catalog includes offline adapters\nPASS missing exact path remains visible and rebindable\nPASS fresh reasons and DNS status\nPASS revision mismatch remains unconfirmed\nPASS diagnostic window loads without running probes\nNo network, native guard, rule or installation operations.");
 }
 public static void RenderImprovements(){
  var vm=new MainViewModel();vm.SetPreview();var options=new OptionsWindow(OptionsWindow.LoadSchema(),new Dictionary<string,System.Text.Json.JsonElement>(),_=>Task.CompletedTask,[],null);RenderWindow(options,"Improvements-options.png",850,700);
  vm.Accept(new(){State=new(){Revision=1},NetworkCatalog=[new(){Id=1,Name="Wi-Fi（範例）",Guid="11111111-1111-1111-1111-111111111111",Present=true},new(){Id=2,Name="乙太網路（範例）",Guid="22222222-2222-2222-2222-222222222222",Present=true},new(){Id=3,Name="行動網路（範例）",Guid="33333333-3333-3333-3333-333333333333",Present=true}]});var diagnostics=new DiagnosticsWindow(vm,preview:true);RenderWindow(diagnostics,"Improvements-diagnostics.png",760,580);
 }
 private static void RenderWindow(Window window,string name,int width,int height){var content=window.Content;window.Content=null;var root=new System.Windows.Controls.Border{Background=window.Background,Child=(UIElement)content};root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));var output=Path.Combine(Path.GetTempPath(),"UnifiedNetworkRouter-preview");Directory.CreateDirectory(output);using var file=File.Create(Path.Combine(output,name));encoder.Save(file);}
 public static void CheckOptions(){
  if(!EntryPoint.NeedsElevation([],false)||EntryPoint.NeedsElevation(["--check-options"],false)||EntryPoint.NeedsElevation(["--render-options"],false)||EntryPoint.NeedsElevation([],true))throw new InvalidOperationException("啟動權限判斷錯誤。");
  if(EntryPoint.TryElevatedLaunch(()=>throw new System.ComponentModel.Win32Exception(1223)))throw new InvalidOperationException("取消管理員權限必須取消啟動。");
  var schema=OptionsWindow.LoadSchema();var defaults=new Dictionary<string,System.Text.Json.JsonElement>();bool saved=false;
  var dialog=new OptionsWindow(schema,defaults,_=>{saved=true;return Task.CompletedTask;},[],null);
  dialog.ResetDraft();if(saved||dialog.Values.Count!=schema.Length)throw new InvalidOperationException("還原預設不可寫入設定。");
  if(!dialog.ValidateDraft())throw new InvalidOperationException("預設設定驗證失敗。");
  dialog.SwitchPage(true);if(dialog.Input("DnsIPv4Servers") is not System.Windows.Controls.TextBox dns||!dns.AcceptsReturn)throw new InvalidOperationException("DNS 上游應使用多行欄位。");
  dns.Text="https://1.1.1.1/dns-query\nhttps://8.8.8.8/dns-query";dialog.SearchFor("不存在的搜尋字");dialog.SearchFor("");
  if(((System.Windows.Controls.TextBox)dialog.Input("DnsIPv4Servers")).Text!=dns.Text)throw new InvalidOperationException("搜尋切換應保留多行草稿。");
  dialog.ResetDraft();if(saved)throw new InvalidOperationException("還原預設不可保存設定。");
  try{_ = new OptionsWindow([],defaults,_=>Task.CompletedTask,[],null);throw new InvalidOperationException("空 schema 不可保存。");}catch(InvalidDataException){}
  if(Services.TrayController.ShouldHide(false,true)||!Services.TrayController.ShouldHide(true,true)||Services.TrayController.ShouldHide(true,false))throw new InvalidOperationException("通知區隱藏條件錯誤。");
  var preferences=System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,System.Text.Json.JsonElement>>("{\"ShowInactivePrograms\":true,\"AutoRefreshStatus\":false,\"PollSeconds\":30}")!;
  var vm=new MainViewModel();vm.Accept(new(){State=new(){Options=preferences}});if(!vm.ShowInactivePrograms||vm.EngineState!="狀態自動更新已關閉"||vm.OptionInt("PollSeconds",2)!=30)throw new InvalidOperationException("UI 偏好未套用。");vm.ShowInactivePrograms=false;vm.Accept(new(){State=new(){Options=preferences}});if(vm.ShowInactivePrograms)throw new InvalidOperationException("背景更新不可覆蓋主頁顯示選擇。");var changedPreferences=new Dictionary<string,System.Text.Json.JsonElement>(preferences);changedPreferences["ShowInactivePrograms"]=System.Text.Json.JsonSerializer.SerializeToElement(false);vm.ShowInactivePrograms=true;vm.Accept(new(){State=new(){Options=changedPreferences}});if(vm.ShowInactivePrograms)throw new InvalidOperationException("已保存顯示偏好變更未套用。");
  changedPreferences["FailClosed"]=System.Text.Json.JsonSerializer.SerializeToElement(false);vm.Accept(new(){State=new(){Options=changedPreferences}});if(!vm.ProtectionNotice.Contains("斷線保護已關閉")||vm.ProtectionNotice.Contains("會讓整台電腦斷網"))throw new InvalidOperationException("關閉斷線保護時不可顯示啟用提示。");
  var output=Path.Combine(Path.GetTempPath(),"UnifiedNetworkRouter-preview");Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"Options-check.txt"),$"PASS {schema.Length} options loaded\nPASS defaults remain draft\nPASS empty schema rejected\nPASS ShowInactivePrograms applied and session choice preserved on refresh\nPASS AutoRefreshStatus applied\nPASS PollSeconds read\nPASS ShowTray visibility safety policy\nPASS multiline DNS draft survives search\nNo network or guard operations.");
 }
 public static void RenderOptions(){
  var dialog=new OptionsWindow(OptionsWindow.LoadSchema(),new Dictionary<string,System.Text.Json.JsonElement>(),_=>Task.CompletedTask,[],null);
  var content=dialog.Content;dialog.Content=null;var root=new System.Windows.Controls.Border{Background=dialog.Background,Child=(UIElement)content};root.Measure(new Size(850,700));root.Arrange(new Rect(0,0,850,700));root.UpdateLayout();
  var bitmap=new RenderTargetBitmap(850,700,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));var output=Path.Combine(Path.GetTempPath(),"UnifiedNetworkRouter-preview");Directory.CreateDirectory(output);using var file=File.Create(Path.Combine(output,"Options-preview.png"));encoder.Save(file);
 }
 public static void RenderPreview() {
  var window=new MainWindow();((MainViewModel)window.DataContext).SetPreview();
  var content=window.Content;window.Content=null;
  var root=new System.Windows.Controls.Border{Background=window.Background,Child=(UIElement)content,DataContext=window.DataContext};
  var baseLayer=new System.Windows.Controls.Border{Background=(Brush)System.Windows.Application.Current.FindResource("BgSolidBrush"),Child=root};
  baseLayer.Measure(new Size(1160,1040));baseLayer.Arrange(new Rect(0,0,1160,1040));baseLayer.UpdateLayout();
  var bitmap=new RenderTargetBitmap(1160,1040,96,96,PixelFormats.Pbgra32);bitmap.Render(baseLayer);
  var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
  var output=Path.Combine(Path.GetTempPath(),"UnifiedNetworkRouter-preview");Directory.CreateDirectory(output);
  using var file=File.Create(Path.Combine(output,"Interface-preview.png"));encoder.Save(file);
 }
}
