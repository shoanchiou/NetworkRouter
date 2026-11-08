using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using BindIPManager.Services;
using BindIPManager.ViewModels;

namespace BindIPManager.Views;
public sealed class AppPickerWindow : Window {
 private sealed class PickerApplication(ApplicationEntry entry) : ObservableObject {
  public ApplicationEntry Entry {get;private set;}=entry;
  public string Name=>Entry.Name;public string Path=>Entry.Path;public bool CanSelect=>Entry.CanSelect;
  private bool _checked;public bool Checked {get=>_checked;set=>SetProperty(ref _checked,value);}
  private string _detail=entry.Source;public string Detail {get=>_detail;set=>SetProperty(ref _detail,value);}
  public void Update(ApplicationEntry value){Entry=value;OnPropertyChanged(nameof(Name));OnPropertyChanged(nameof(Path));OnPropertyChanged(nameof(CanSelect));}
 }
 private readonly ObservableCollection<PickerApplication> _entries=[];
 private readonly Dictionary<string,PickerApplication> _rows=new(StringComparer.OrdinalIgnoreCase);
 private readonly Dictionary<string,ApplicationEntry> _checked=new(StringComparer.OrdinalIgnoreCase);
 private readonly ListView _list=new(){SelectionMode=SelectionMode.Single};
 private readonly ComboBox _source=new(){Width=220,ItemsSource=new[]{"正在執行的程式","已安裝的程式"},SelectedIndex=0};
 private readonly CheckBox _background=new(){Content="包含背景程式",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(16,0,0,0)};
 private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)};
 private readonly Button _refresh;private readonly Button _add;
 private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(3)};
 private CancellationTokenSource _cancellation=new();private int _generation;private int _refreshing=-1;private bool _closed;
 private ApplicationEntry[]? _installed;private Task<BridgeResponse>? _installedTask;
 public ApplicationEntry[] Selection {get;private set;}=[];

 public AppPickerWindow(ApplicationEntry[]? installed=null,bool includeBackground=false,bool autoRefresh=true,bool runningOnly=false) {
  _background.IsChecked=includeBackground;
  _installed=installed;if(runningOnly){_source.ItemsSource=new[]{"正在執行的程式"};_source.SelectedIndex=0;}
  Title="新增應用程式";Width=980;Height=650;MinWidth=740;MinHeight=450;WindowStartupLocation=WindowStartupLocation.CenterOwner;
  Background=(Brush)FindResource("BgSolidBrush");Foreground=(Brush)FindResource("TextBrush");
  var grid=new Grid{Margin=new Thickness(20)};
  foreach(var height in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})grid.RowDefinitions.Add(new(){Height=height});
  var intro=new TextBlock{Text="先開啟想限制的程式，再從下方勾選新增。執行中清單每 3 秒自動更新，已勾選的程式會保留。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};grid.Children.Add(intro);
  var sources=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,12)};
  sources.Children.Add(_source);sources.Children.Add(_background);
  _refresh=new Button{Content="重新整理",Style=(Style)FindResource("GhostButton"),Margin=new Thickness(16,0,0,0)};sources.Children.Add(_refresh);Grid.SetRow(sources,1);grid.Children.Add(sources);
  var search=new TextBox{Margin=new Thickness(0,0,0,12),ToolTip="搜尋名稱或程式位置"};Grid.SetRow(search,2);grid.Children.Add(search);
  var view=CollectionViewSource.GetDefaultView(_entries);
  view.Filter=item=>item is PickerApplication app&&(string.IsNullOrWhiteSpace(search.Text)||app.Name.Contains(search.Text,StringComparison.CurrentCultureIgnoreCase)||app.Path.Contains(search.Text,StringComparison.CurrentCultureIgnoreCase));
  search.TextChanged+=(_,_)=>view.Refresh();
  _list.ItemsSource=view;_list.Background=(Brush)FindResource("SurfaceBrush");_list.Foreground=Foreground;
  var itemStyle=new Style(typeof(ListViewItem));itemStyle.Setters.Add(new Setter(IsEnabledProperty,new Binding("CanSelect")));_list.ItemContainerStyle=itemStyle;
  var columns=new GridView();
  var check=new FrameworkElementFactory(typeof(CheckBox));check.SetBinding(ToggleButton.IsCheckedProperty,new Binding("Checked"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});check.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);check.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);check.SetBinding(IsEnabledProperty,new Binding("CanSelect"));
  columns.Columns.Add(new(){Header="選取",Width=48,CellTemplate=new DataTemplate{VisualTree=check}});
  columns.Columns.Add(new(){Header="應用程式",Width=220,DisplayMemberBinding=new Binding("Name")});columns.Columns.Add(new(){Header="程式位置",Width=480,DisplayMemberBinding=new Binding("Path")});columns.Columns.Add(new(){Header="狀態或來源",Width=150,DisplayMemberBinding=new Binding("Detail")});_list.View=columns;
  Grid.SetRow(_list,3);grid.Children.Add(_list);Grid.SetRow(_status,4);grid.Children.Add(_status);
  var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
  var browse=new Button{Content="瀏覽執行檔",Style=(Style)FindResource("GhostButton"),Margin=new Thickness(0,0,10,0)};
  browse.Click+=(_,_)=>{var dialog=new Microsoft.Win32.OpenFileDialog{Filter="程式執行檔 (*.exe)|*.exe",Multiselect=true};if(dialog.ShowDialog(this)==true){foreach(var path in dialog.FileNames)_checked[path]=new(){Name=System.IO.Path.GetFileNameWithoutExtension(path),Path=path,Source="瀏覽執行檔"};Selection=_checked.Values.ToArray();DialogResult=true;}};
  var cancel=new Button{Content="取消",Style=(Style)FindResource("GhostButton"),Margin=new Thickness(0,0,10,0),IsCancel=true};cancel.Click+=(_,_)=>DialogResult=false;
  _add=new Button{Content="新增選取的程式",Style=(Style)FindResource("AccentButton"),IsEnabled=false};_add.Click+=(_,_)=>{Selection=SelectedApplications();if(Selection.Length>0)DialogResult=true;};
  if(!runningOnly)buttons.Children.Add(browse);buttons.Children.Add(cancel);buttons.Children.Add(_add);Grid.SetRow(buttons,5);grid.Children.Add(buttons);Content=grid;
  _source.SelectionChanged+=(_,_)=>{_background.IsEnabled=_source.SelectedIndex==0;RestartRefresh();};
  _background.Checked+=(_,_)=>RestartRefresh();_background.Unchecked+=(_,_)=>RestartRefresh();
  _refresh.Click+=(_,_)=>{if(_source.SelectedIndex==1){_installed=null;_installedTask=null;}RestartRefresh();};
  _timer.Tick+=async (_,_)=>{if(_source.SelectedIndex==0)await RefreshEntriesAsync();};
  Loaded+=(_,_)=>{if(autoRefresh)_timer.Start();RestartRefresh();};
  Closed+=(_,_)=>{_closed=true;_timer.Stop();_cancellation.Cancel();_cancellation.Dispose();};
 }
 private void RestartRefresh() {
  if(_closed||!IsLoaded)return;
  _cancellation.Cancel();_cancellation.Dispose();_cancellation=new();_generation++;
  _=RefreshEntriesAsync();
 }
 private async Task RefreshEntriesAsync() {
  if(_closed||!IsLoaded||_refreshing==_generation)return;
  int generation=_generation;_refreshing=generation;var token=_cancellation.Token;
  bool running=_source.SelectedIndex==0;bool includeBackground=_background.IsChecked==true;
  _refresh.IsEnabled=false;
  if(_entries.Count==0)_status.Text=running?"正在讀取目前執行中的程式":"正在讀取已安裝的程式，首次讀取可能需要一些時間";
  try {
   ApplicationEntry[] result;
   if(running)result=await Task.Run(()=>RunningApplications.Read(includeBackground,token),token);
   else {
    if(_installed==null){_installedTask??=RouterBridge.CallAsync(new{Action="Applications"});var response=await _installedTask;if(_closed||token.IsCancellationRequested||generation!=_generation)return;_installed=response.Applications;}
    result=_installed;
   }
   if(_closed||token.IsCancellationRequested||generation!=_generation)return;
   UpdateEntries(result,running);
  }catch(OperationCanceledException) { }
   catch(Exception error){if(!_closed&&!token.IsCancellationRequested&&generation==_generation){if(!running)_installedTask=null;RouterBridge.LogError(error,"讀取新增程式清單");_status.Text="無法更新清單，可按重新整理重試，或使用瀏覽執行檔。";}}
  finally {if(!_closed&&generation==_generation){_refreshing=-1;_refresh.IsEnabled=true;}}
 }
 private static string Key(ApplicationEntry entry)=>string.IsNullOrWhiteSpace(entry.Path)?"unavailable:"+entry.Name+":"+entry.Source:entry.Path;
 private void UpdateEntries(IEnumerable<ApplicationEntry> incoming,bool running) {
  var scroll=FindScrollViewer(_list);double offset=scroll?.VerticalOffset??0;
  object? anchor=offset>=0&&(int)offset<_list.Items.Count?_list.Items[(int)offset]:null;
  var candidates=incoming.GroupBy(Key,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToDictionary(Key,StringComparer.OrdinalIgnoreCase);
  var incomingKeys=candidates.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
  foreach(var entry in _checked.Values)if(!candidates.ContainsKey(Key(entry)))candidates[Key(entry)]=entry;
  var target=new List<PickerApplication>();
  foreach(var entry in candidates.Values.OrderBy(x=>x.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(x=>x.Path,StringComparer.OrdinalIgnoreCase)) {
   var key=Key(entry);
   if(!_rows.TryGetValue(key,out var row)) {
    row=new(entry);_rows[key]=row;
    row.PropertyChanged+=(_,change)=>{if(change.PropertyName==nameof(PickerApplication.Checked)){if(row.Checked&&row.CanSelect)_checked[row.Path]=row.Entry;else _checked.Remove(row.Path);UpdateCheckedCount();}};
   }else row.Update(entry);
   if(row.Checked)_checked[row.Path]=row.Entry;
   row.Detail=incomingKeys.Contains(key)?(string.IsNullOrWhiteSpace(entry.Source)?"已安裝":entry.Source):"已勾選 保留選擇";
   target.Add(row);
  }
  var desired=target.ToHashSet();for(int i=_entries.Count-1;i>=0;i--)if(!desired.Contains(_entries[i]))_entries.RemoveAt(i);
  for(int i=0;i<target.Count;i++){int existing=_entries.IndexOf(target[i]);if(existing<0)_entries.Insert(i,target[i]);else if(existing!=i)_entries.Move(existing,i);}
  foreach(var key in _rows.Keys.Where(key=>!candidates.ContainsKey(key)).ToArray())_rows.Remove(key);
  UpdateCheckedCount();
  _status.Text=running?$"正在執行的程式共 {incomingKeys.Count} 個  每 3 秒自動更新  已勾選 {_checked.Count} 個":"已安裝的程式共 "+incomingKeys.Count+" 個  已勾選 "+_checked.Count+" 個";
  if(scroll!=null){_list.UpdateLayout();int anchorIndex=anchor==null?-1:_list.Items.IndexOf(anchor);scroll.ScrollToVerticalOffset(anchorIndex>=0?anchorIndex+offset-Math.Floor(offset):offset);}
 }
 private void UpdateCheckedCount(){_add.IsEnabled=_checked.Count>0;_add.Content=_checked.Count==0?"新增選取的程式":$"新增選取的程式  {_checked.Count}";}
 private ApplicationEntry[] SelectedApplications()=>_checked.Values.Where(entry=>entry.CanSelect).ToArray();
 internal static void CheckSelectionPersistence() {
  var picker=new AppPickerWindow();
  try {
   var alpha=new ApplicationEntry{Name="Alpha",Path=@"C:\PickerCheck\alpha.exe",Source="正在執行"};
   var expired=new ApplicationEntry{Name="Expired",Path=@"C:\PickerCheck\expired.exe",Source="正在執行"};
   var unavailable=new ApplicationEntry{Name="Unavailable",Path=@"C:\PickerCheck\unavailable.exe",Source="已安裝",Available=false};
   picker.UpdateEntries([alpha,expired,unavailable],true);
   picker._rows[Key(unavailable)].Checked=true;
   if(picker._checked.Count!=0||picker._add.IsEnabled||picker.SelectedApplications().Length!=0)throw new InvalidOperationException("Unavailable applications must not be added.");
   picker._rows[Key(alpha)].Checked=true;
   var beta=new ApplicationEntry{Name="Beta",Path=@"C:\PickerCheck\beta.exe",Source="正在執行"};
   picker.UpdateEntries([beta],true);
   if(!picker._rows.TryGetValue(Key(alpha),out var retained)||!retained.Checked||picker._checked.Count!=1||!picker._add.IsEnabled)throw new InvalidOperationException("A checked application was lost when its process exited.");
   if(picker._rows.ContainsKey(Key(expired))||picker._rows.ContainsKey(Key(unavailable)))throw new InvalidOperationException("Expired unchecked applications remained in the list.");
   var installedAlpha=new ApplicationEntry{Name="Alpha installed",Path=alpha.Path,Source="已安裝來源"};
   var installedOther=new ApplicationEntry{Name="Other installed",Path=@"C:\PickerCheck\other.exe",Source="已安裝來源"};
   picker.UpdateEntries([installedAlpha,installedOther],false);
   if(!ReferenceEquals(retained,picker._rows[Key(alpha)])||!retained.Checked||picker._rows.ContainsKey(Key(beta)))throw new InvalidOperationException("Source changes did not preserve checked applications and remove stale rows.");
   picker.Selection=picker.SelectedApplications();
   if(picker.Selection.Length!=1||!ReferenceEquals(picker.Selection[0],installedAlpha)||picker.Selection[0].Source!="已安裝來源")throw new InvalidOperationException("Selection did not return the selected application's current source data.");
   retained.Checked=false;
   if(picker.SelectedApplications().Length!=0||picker._add.IsEnabled)throw new InvalidOperationException("An unchecked application remained selected.");
  }finally{picker.Close();}
 }
 private static ScrollViewer? FindScrollViewer(DependencyObject element) {
  if(element is ScrollViewer scroll)return scroll;
  for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++){var found=FindScrollViewer(VisualTreeHelper.GetChild(element,i));if(found!=null)return found;}return null;
 }
}
public sealed class TextWindow : Window {
 public TextWindow(string title,string text){Title=title;Width=900;Height=600;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=(Brush)FindResource("BgSolidBrush");Content=new TextBox{Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(16)};}
}
