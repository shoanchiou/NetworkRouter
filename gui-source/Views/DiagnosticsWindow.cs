using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BindIPManager.Services;
using BindIPManager.ViewModels;
namespace BindIPManager.Views;
public sealed class DiagnosticsWindow : Window {
 private readonly MainViewModel _vm;private readonly ComboBox _adapter;private readonly TextBlock _result;private readonly Button _run,_cancel;private CancellationTokenSource? _running;private bool _closeAfter;
 public DiagnosticsWindow(MainViewModel vm,bool preview=false){
  _vm=vm;Title="連線診斷";Width=760;Height=580;MinWidth=580;MinHeight=430;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=(Brush)FindResource("BgSolidBrush");Foreground=(Brush)FindResource("TextBrush");
  var grid=new Grid{Margin=new Thickness(22)};foreach(var size in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})grid.RowDefinitions.Add(new(){Height=size});
  grid.Children.Add(new TextBlock{Text="連線診斷",FontSize=22,FontWeight=FontWeights.SemiBold});
  var explanation=new TextBlock{Text="手動選擇網卡後測試 DNS、TCP、UDP，會產生少量網路流量。結果只反映所選網卡及測試對象；個別程式規則的目前狀態與原因請查看主視窗規則清單。診斷不會修改規則；所選網卡可能產生計費流量。UDP 未回應也可能有其他原因。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,12)};Grid.SetRow(explanation,1);grid.Children.Add(explanation);
  var items=vm.Catalog.Where(n=>Guid.TryParse(n.Guid,out _)).Select(n=>new AdapterItem(n,RouterBridge.NetworkName(n.Id,vm.Catalog)+(n.Present?"":"（目前不可用）")+"  "+n.Guid)).ToArray();
  _adapter=new ComboBox{ItemsSource=items,DisplayMemberPath="Label",SelectedIndex=items.Length>0?0:-1,Margin=new Thickness(0,0,0,12)};Grid.SetRow(_adapter,2);grid.Children.Add(_adapter);
  _result=new TextBlock{Text=preview?"介面預覽：範例網卡，未執行任何測試。":items.Length==0?"沒有可選網卡，請先重新整理網卡清單。":"尚未開始。",TextWrapping=TextWrapping.Wrap};var scroll=new ScrollViewer{Content=_result,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(scroll,3);grid.Children.Add(scroll);
  var row=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
  _run=new Button{Content="開始診斷",Style=(Style)FindResource("AccentButton"),IsEnabled=items.Length>0};_cancel=new Button{Content="取消測試",Style=(Style)FindResource("GhostButton"),IsEnabled=false,Margin=new Thickness(10,0,0,0)};var close=new Button{Content="關閉",Style=(Style)FindResource("GhostButton"),Margin=new Thickness(10,0,0,0)};row.Children.Add(_run);row.Children.Add(_cancel);row.Children.Add(close);Grid.SetRow(row,4);grid.Children.Add(row);Content=grid;
  _run.Click+=async(_,_)=>await Run();_cancel.Click+=(_,_)=>_running?.Cancel();close.Click+=(_,_)=>Close();Closing+=(_,e)=>{if(_running!=null){_closeAfter=true;_running.Cancel();e.Cancel=true;}};
 }
 private async Task Run(){if(_running!=null||_adapter.SelectedItem is not AdapterItem item)return;_running=new CancellationTokenSource();_run.IsEnabled=false;_cancel.IsEnabled=true;_adapter.IsEnabled=false;var lines=new List<string>();_result.Text="測試網卡："+item.Label+"\n正在準備…";
  try {var progress=new Progress<DiagnosticStep>(step=>{lines.Add(step.Name+"："+step.Status+"  "+step.Detail);_result.Text="測試網卡："+item.Label+"\n\n"+string.Join("\n",lines);});await NetworkDiagnostics.RunAsync(item.Entry,_vm.Options,progress,_running.Token);}
  catch(OperationCanceledException){_result.Text+="\n測試已取消。";}catch(Exception error){_result.Text+="\n診斷未完成："+error.Message;}
  finally{_running.Dispose();_running=null;_run.IsEnabled=true;_cancel.IsEnabled=false;_adapter.IsEnabled=true;if(_closeAfter)Close();}
 }
 internal string[] AdapterLabels()=>_adapter.Items.Cast<AdapterItem>().Select(x=>x.ToString()).ToArray();
 internal string[] AdapterGuids()=>_adapter.Items.Cast<AdapterItem>().Select(x=>x.Entry.Guid).ToArray();
 private sealed record AdapterItem(NetworkCatalogEntry Entry,string Label) { public override string ToString()=>Label; }
}
