using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BindIPManager.Services;

namespace BindIPManager.Views;
public sealed class OptionDefinition {
 public string Key {get;set;}=""; public string Category {get;set;}="其他"; public string Label {get;set;}=""; public string Description {get;set;}=""; public string Effect {get;set;}=""; public string Type {get;set;}=""; public JsonElement Default {get;set;} public double? Min {get;set;} public double? Max {get;set;} public int? MaxLength {get;set;} public JsonElement Choices {get;set;}
}
public sealed class OptionsWindow : Window {
 private bool _saving; private bool _advanced; private Button _basicTab=null!,_advancedTab=null!; private readonly Action? _diagnostics; private readonly OptionDefinition[] _schema; private readonly Func<IReadOnlyDictionary<string,object>,Task> _save; private readonly IReadOnlyList<NetworkCatalogEntry> _catalog; private readonly TrayController? _guard; private readonly Dictionary<string,FrameworkElement> _inputs=new(StringComparer.OrdinalIgnoreCase); private readonly Dictionary<string,object> _draft=new(StringComparer.OrdinalIgnoreCase); private readonly StackPanel _items=new(); private readonly TextBox _search=new(){Margin=new Thickness(0,8,0,12),ToolTip="輸入關鍵字搜尋功能"};
 public IReadOnlyDictionary<string,object> Values=>_draft;
 public OptionsWindow(OptionDefinition[] schema, IReadOnlyDictionary<string,JsonElement> current, Func<IReadOnlyDictionary<string,object>,Task> save, IReadOnlyList<NetworkCatalogEntry> catalog, TrayController? guard, Action? diagnostics=null) {
  if(schema.Length==0)throw new InvalidDataException("設定項目清單為空，請確認工具包完整。"); _save=save;_catalog=catalog;_guard=guard;_diagnostics=diagnostics;_schema=schema;foreach(var opt in schema)_draft[opt.Key]=ConvertValue(current.TryGetValue(opt.Key,out var value)?value:opt.Default);
  Closing+=(_,e)=>{if(_saving)e.Cancel=true;};
  Title="所有功能設定";Width=850;Height=700;MinWidth=620;MinHeight=450;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=(Brush)FindResource("BgSolidBrush");Foreground=(Brush)FindResource("TextBrush");
  var grid=new Grid{Margin=new Thickness(22)};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
  grid.Children.Add(new TextBlock{Text="所有功能設定",FontSize=22,FontWeight=FontWeights.SemiBold});var searchPanel=new StackPanel();var tabs=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,0)};
  var basic=_basicTab=Button("基本",true);var advanced=_advancedTab=Button("進階",false);advanced.Margin=new Thickness(8,0,0,0);tabs.Children.Add(basic);tabs.Children.Add(advanced);basic.Click+=(_,_)=>SwitchPage(false);advanced.Click+=(_,_)=>SwitchPage(true);
  if(_diagnostics!=null){var diagnostic=Button("連線診斷",false);diagnostic.Margin=new Thickness(16,0,0,0);diagnostic.Click+=(_,_)=>_diagnostics();tabs.Children.Add(diagnostic);}searchPanel.Children.Add(tabs);searchPanel.Children.Add(new TextBlock{Text="搜尋功能名稱或說明（搜尋涵蓋基本與進階）",Foreground=(Brush)FindResource("MutedBrush"),Margin=new Thickness(0,10,0,0)});searchPanel.Children.Add(_search);Grid.SetRow(searchPanel,1);grid.Children.Add(searchPanel);
  var scroll=new ScrollViewer{Content=_items,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(scroll,2);grid.Children.Add(scroll);
  var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
  var reset=Button("還原顯示項目的預設值",false);reset.Click+=(_,_)=>{foreach(var opt in Filtered())_draft[opt.Key]=ConvertValue(opt.Default);Render();};buttons.Children.Add(reset);
  var cancel=Button("取消",false);cancel.Margin=new Thickness(10,0,0,0);cancel.Click+=(_,_)=>{DialogResult=false;};buttons.Children.Add(cancel);
  var saveButton=Button("保存設定",true);saveButton.Margin=new Thickness(10,0,0,0);saveButton.Click+=async(_,_)=>{if(!Collect())return;
   grid.IsEnabled=false;_saving=true;try{await _save(new Dictionary<string,object>(_draft,StringComparer.OrdinalIgnoreCase));_saving=false;DialogResult=true;}catch(Exception error){MessageBox.Show("保存失敗，草稿仍保留。請檢查設定或重新嘗試。\n"+error.Message,"所有功能設定",MessageBoxButton.OK,MessageBoxImage.Error);}finally{_saving=false;grid.IsEnabled=true;}};buttons.Children.Add(saveButton);Grid.SetRow(buttons,3);grid.Children.Add(buttons);Content=grid;
  _search.TextChanged+=(_,_)=>{if(Collect(false))Render();};Render();
 }
 internal void SwitchPage(bool advanced){if(!Collect())return;_advanced=advanced;_basicTab.Style=(Style)FindResource(advanced?"GhostButton":"AccentButton");_advancedTab.Style=(Style)FindResource(advanced?"AccentButton":"GhostButton");Render();}
 internal bool ActiveTabIsVisible()=>ReferenceEquals((_advanced?_advancedTab:_basicTab).Style,FindResource("AccentButton")) && ReferenceEquals((_advanced?_basicTab:_advancedTab).Style,FindResource("GhostButton"));
 internal string[] DisplayedKeys()=>Filtered().Select(o=>o.Key).ToArray();
 internal FrameworkElement Input(string key)=>_inputs[key];
 internal void SearchFor(string text)=>_search.Text=text;
 internal void ResetDraft(){foreach(var opt in _schema)_draft[opt.Key]=ConvertValue(opt.Default);Render();}
 internal bool ValidateDraft()=>Collect();
 private Button Button(string text,bool accent)=>new(){Content=text,Style=(Style)FindResource(accent?"AccentButton":"GhostButton")};
 private static readonly HashSet<string> BasicKeys=new(StringComparer.OrdinalIgnoreCase){"DefaultNetworkId","AllowUnconfiguredPrograms","AllowBackup","AutoReturnPreferred","AutoStart","AutoRefreshProcesses","AutoRefreshNetworks","AutoRefreshStatus","PollSeconds","ShowInactivePrograms","IncludeBackgroundPrograms","ShowTray","CloseToTray","MinimizeToTray","StartHidden","TrayAutoStart"};
 private IEnumerable<OptionDefinition> Filtered()=>_schema.Where(o=>!string.IsNullOrWhiteSpace(_search.Text)?(o.Label+o.Description+o.Category+o.Key).Contains(_search.Text,StringComparison.CurrentCultureIgnoreCase):BasicKeys.Contains(o.Key)!=_advanced);
 private void Render(){_items.Children.Clear();_inputs.Clear();
  if(string.IsNullOrWhiteSpace(_search.Text)&&!_advanced){
   _items.Children.Add(new TextBlock{Text="全域分流與個別規則",FontSize=16,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,14,0,8)});
   _items.Children.Add(new TextBlock{Text="全域分流：在主視窗選單啟用已保存規則，或使用恢復網路停止。個別規則：在主視窗規則清單按暫停或恢復；暫停會保留設定。這兩種狀態分開控制。",TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("MutedBrush")});
  }
  if(_guard!=null&&string.IsNullOrWhiteSpace(_search.Text)&&_advanced){
   _items.Children.Add(new TextBlock{Text="獨立計費網卡保護",FontSize=16,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,14,0,8)});
   var panel=new StackPanel{Margin=new Thickness(0,0,0,12)};var status=new TextBlock{Text=_guard.StatusText+"  "+_guard.DetailText,TextWrapping=TextWrapping.Wrap};panel.Children.Add(status);
   var choices=_catalog.Where(n=>Guid.TryParse(n.Guid,out _)).Select(n=>new PaidItem(n.Guid,RouterBridge.NetworkName(n.Id,_catalog)+(n.Present?"":"（目前不可用）"))).ToList();
   if(_guard.Status is {Guid.Length:>0} actual&&choices.All(n=>!n.Guid.Equals(actual.Guid,StringComparison.OrdinalIgnoreCase)))choices.Add(new(actual.Guid,"目前受保護網卡（目錄不可用）"));
   var picker=new ComboBox{ItemsSource=choices,DisplayMemberPath="Label",SelectedValuePath="Guid",SelectedValue=_guard.Status?.Guid,MinWidth=300,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,8,0,8)};panel.Children.Add(new TextBlock{Text="預備安裝的保護網卡（不會自動更換現有保護對象）。安裝會建立登入時啟動通知區的排程；移除會刪除該排程。",TextWrapping=TextWrapping.Wrap});panel.Children.Add(picker);
   var buttons=new StackPanel{Orientation=Orientation.Horizontal};var install=Button("安裝保護",false);var remove=Button("移除保護",false);remove.Margin=new Thickness(8,0,0,0);buttons.Children.Add(install);buttons.Children.Add(remove);panel.Children.Add(buttons);_items.Children.Add(panel);
   void Update(){status.Text=_guard.StatusText+"  "+_guard.DetailText;install.IsEnabled=!_guard.IsBusy&&_guard.Error.Length==0&&_guard.Status is {Installed:false}&&picker.SelectedValue!=null;remove.IsEnabled=!_guard.IsBusy&&_guard.Error.Length==0&&_guard.Status is {Installed:true};}
   picker.SelectionChanged+=(_,_)=>Update();install.Click+=async(_,_)=>{if(picker.SelectedValue is string guid){await _guard.Install(guid);Update();}};remove.Click+=async(_,_)=>{if(MessageBox.Show("確定移除獨立計費網卡保護？","移除保護",MessageBoxButton.OKCancel,MessageBoxImage.Question)==MessageBoxResult.OK){await _guard.Uninstall();Update();}};Update();
  }
  if(!Filtered().Any())_items.Children.Add(new TextBlock{Text="找不到符合的設定。搜尋會涵蓋基本與進階。",Margin=new Thickness(0,14,0,8),TextWrapping=TextWrapping.Wrap});
  foreach(var group in Filtered().GroupBy(o=>o.Category)){
  _items.Children.Add(new TextBlock{Text=group.Key,FontSize=16,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,14,0,8)});
  foreach(var opt in group){var box=new Border{Background=(Brush)FindResource("SurfaceBrush"),CornerRadius=new CornerRadius(10),Padding=new Thickness(14),Margin=new Thickness(0,0,0,8)};var panel=new StackPanel();box.Child=panel;panel.Children.Add(new TextBlock{Text=opt.Label,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});panel.Children.Add(new TextBlock{Text=opt.Description+(opt.Key=="ShowTray"?" 關閉圖示時，視窗不會隱藏至通知區。":"")+(string.IsNullOrWhiteSpace(opt.Effect)?"":"\n"+opt.Effect),Foreground=(Brush)FindResource("MutedBrush"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,8)});
   FrameworkElement input;if(opt.Key is "DnsNetworkId" or "DefaultNetworkId") {var entries=new List<NetworkItem>{new(0,"自動選擇")};entries.AddRange(_catalog.Select(n=>new NetworkItem(n.Id,RouterBridge.NetworkName(n.Id,_catalog)+(n.Present?"":"（目前不可用）"))));var selected=System.Convert.ToInt32(_draft[opt.Key]);if(entries.All(n=>n.Id!=selected))entries.Add(new(selected,$"網卡 {selected}（目前不可用）"));input=new ComboBox{ItemsSource=entries,DisplayMemberPath="Label",SelectedValuePath="Id",SelectedValue=selected,MinWidth=250,HorizontalAlignment=HorizontalAlignment.Left};}
   else if(opt.Type.Equals("bool",StringComparison.OrdinalIgnoreCase)||opt.Type.Equals("boolean",StringComparison.OrdinalIgnoreCase)){input=new CheckBox{Content="啟用",IsChecked=System.Convert.ToBoolean(_draft[opt.Key])};}else if(opt.Type.Equals("enum",StringComparison.OrdinalIgnoreCase)&&opt.Choices.ValueKind==JsonValueKind.Array){var choices=opt.Choices.EnumerateArray().Select(x=>x.ValueKind==JsonValueKind.Object?new EnumItem(x.TryGetProperty("Value",out var v)?v.ToString():x.ToString(),x.TryGetProperty("Label",out var l)?l.ToString():x.ToString()):new EnumItem(x.ToString(),x.ToString())).ToArray();input=new ComboBox{ItemsSource=choices,DisplayMemberPath="Label",SelectedValuePath="Value",SelectedValue=_draft[opt.Key].ToString(),MinWidth=150,HorizontalAlignment=HorizontalAlignment.Left};}else if(opt.Key is "DnsIPv4Servers" or "DnsIPv6Servers")input=new TextBox{Text=_draft[opt.Key].ToString(),Width=450,MaxWidth=450,MinHeight=65,MaxHeight=100,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxLength=opt.MaxLength??4096,HorizontalAlignment=HorizontalAlignment.Left};
   else input=new TextBox{Text=_draft[opt.Key].ToString(),MinWidth=150,MaxLength=opt.MaxLength??0,HorizontalAlignment=HorizontalAlignment.Left};
   _inputs[opt.Key]=input;panel.Children.Add(input);_items.Children.Add(box);
  }} }
 private bool Collect(bool report=true){foreach(var opt in _schema.Where(o=>_inputs.ContainsKey(o.Key))){var input=_inputs[opt.Key];object value;if(input is CheckBox check)value=check.IsChecked==true;else if(input is ComboBox combo){if(combo.SelectedItem==null){if(report)MessageBox.Show("請選擇「"+opt.Label+"」。");return false;}value=combo.SelectedValue;}else {var text=((TextBox)input).Text;if(opt.Type.Equals("int",StringComparison.OrdinalIgnoreCase)||opt.Type.Equals("integer",StringComparison.OrdinalIgnoreCase)||opt.Type.Equals("number",StringComparison.OrdinalIgnoreCase)){if(!int.TryParse(text,out var number)||(opt.Min.HasValue&&number<opt.Min)||(opt.Max.HasValue&&number>opt.Max)){if(report)MessageBox.Show($"「{opt.Label}」請輸入 {opt.Min} 到 {opt.Max} 的整數。");return false;}value=number;}else value=text;} _draft[opt.Key]=value;}return true;}
 internal static bool DisplayLabelsAreClean()=>new EnumItem("value","enum label").ToString()=="enum label"&&new PaidItem("guid","paid label").ToString()=="paid label"&&new NetworkItem(1,"network label").ToString()=="network label";
 private sealed record EnumItem(string Value,string Label) { public override string ToString()=>Label; }
 private sealed record PaidItem(string Guid,string Label) { public override string ToString()=>Label; }
 private sealed record NetworkItem(int Id,string Label) { public override string ToString()=>Label; }
 private static object ConvertValue(JsonElement value)=>value.ValueKind switch{JsonValueKind.True=>true,JsonValueKind.False=>false,JsonValueKind.Number=>value.GetInt32(),JsonValueKind.String=>value.GetString()??"",_=>""};
 public static OptionDefinition[] LoadSchema(){var path=Path.Combine(AppContext.BaseDirectory,"OptionsSchema.json");using var doc=JsonDocument.Parse(File.ReadAllText(path));var root=doc.RootElement;if(root.ValueKind==JsonValueKind.Object)root=root.GetProperty("Options");var result=JsonSerializer.Deserialize<OptionDefinition[]>(root.GetRawText(),RouterBridge.JsonOptions)??[];if(result.Length==0||result.Any(x=>string.IsNullOrWhiteSpace(x.Key))||result.Select(x=>x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=result.Length)throw new InvalidDataException("設定清單缺少項目或包含重複鍵值。");return result;}
}
