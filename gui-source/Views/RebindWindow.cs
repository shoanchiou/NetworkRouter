using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BindIPManager.ViewModels;
namespace BindIPManager.Views;
public sealed class RebindWindow : Window {
 private string _path="";private bool _saving;
 public RebindWindow(MainViewModel vm,int id){
  Title="重新綁定程式位置";Width=640;Height=300;MinWidth=520;MinHeight=260;WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=(Brush)FindResource("BgSolidBrush");Foreground=(Brush)FindResource("TextBrush");Closing+=(_,e)=>{if(_saving)e.Cancel=true;};
  var panel=new StackPanel{Margin=new Thickness(22)};panel.Children.Add(new TextBlock{Text="選擇一個正在執行的程式。原名稱、程式 ID 和規則會保留；不會擴大程式群組範圍。",TextWrapping=TextWrapping.Wrap});
  var path=new TextBox{IsReadOnly=true,Margin=new Thickness(0,14,0,10),Text="尚未選擇新位置"};panel.Children.Add(path);
  var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,0)};
  Button Make(string text)=>new(){Content=text,Style=(Style)FindResource("GhostButton"),Margin=new Thickness(0,0,8,0)};
  var pick=Make("選擇執行中的程式");var save=Make("重新綁定");var cancel=Make("取消");save.IsEnabled=false;row.Children.Add(pick);row.Children.Add(save);row.Children.Add(cancel);panel.Children.Add(row);
  var note=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};panel.Children.Add(note);Content=panel;
  pick.Click+=(_,_)=>{var picker=new AppPickerWindow(includeBackground:vm.Option("IncludeBackgroundPrograms",false),autoRefresh:vm.Option("AutoRefreshProcesses",true),runningOnly:true){Owner=this};if(picker.ShowDialog()==true){if(picker.Selection.Length!=1){note.Text="請只選擇一個程式。";return;}_path=picker.Selection[0].Path;path.Text=_path;save.IsEnabled=true;note.Text="";}};
  cancel.Click+=(_,_)=>DialogResult=false;
  save.Click+=async(_,_)=>{panel.IsEnabled=false;_saving=true;try{await vm.Rebind(id,_path);_saving=false;DialogResult=true;}catch(Exception error){note.Text="重新綁定失敗，所選位置保留，請更換或重試："+error.Message;}finally{_saving=false;panel.IsEnabled=true;}};
 }
}
