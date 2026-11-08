using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace BindIPManager.Services;

/// <summary>Reads process information only; never starts, stops, or changes a process.</summary>
public static class RunningApplications {
 private static readonly ConcurrentDictionary<string,string> Names=new(StringComparer.OrdinalIgnoreCase);
 private static readonly string WindowsRoot=WithSeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
 private static readonly string ToolRoot=WithSeparator(AppContext.BaseDirectory);

 public static HashSet<string> ReadPaths(CancellationToken cancellationToken=default)=>
  ReadProcesses(true,false,cancellationToken).Select(p=>p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

 public static ApplicationEntry[] Read(bool includeBackground,CancellationToken cancellationToken=default)=>
  ReadProcesses(includeBackground,true,cancellationToken)
   .GroupBy(p=>p.Path,StringComparer.OrdinalIgnoreCase)
   .Select(g=>new ApplicationEntry{Name=Names.GetOrAdd(g.Key,ReadName),Path=g.Key,Source=g.Any(p=>p.HasWindow)?"正在執行":"背景程式",Available=File.Exists(g.Key)})
   .OrderBy(p=>p.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(p=>p.Path,StringComparer.OrdinalIgnoreCase).ToArray();

 private sealed record ProcessPath(string Path,bool HasWindow);
 private static List<ProcessPath> ReadProcesses(bool includeBackground,bool applicationsOnly,CancellationToken cancellationToken) {
  var result=new List<ProcessPath>();
  using var current=Process.GetCurrentProcess();
  int session=current.SessionId;
  var processes=Process.GetProcesses();
  try {
   foreach(var process in processes) {
    cancellationToken.ThrowIfCancellationRequested();
    try {
     if((applicationsOnly&&process.Id==Environment.ProcessId)||process.SessionId!=session)continue;
     bool hasWindow=process.MainWindowHandle!=IntPtr.Zero;
     if(!includeBackground&&!hasWindow)continue;
     string? path=process.MainModule?.FileName;
     if(string.IsNullOrWhiteSpace(path)||!path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))continue;
     path=Path.GetFullPath(path);
     if(applicationsOnly&&(path.StartsWith(WindowsRoot,StringComparison.OrdinalIgnoreCase)||path.StartsWith(ToolRoot,StringComparison.OrdinalIgnoreCase)))continue;
     result.Add(new(path,hasWindow));
    }catch(System.ComponentModel.Win32Exception) { /* Protected and exiting processes are not selectable. */ }
     catch(InvalidOperationException) { }
     catch(NotSupportedException) { }
     catch(ArgumentException) { }
   }
  }finally {foreach(var process in processes)process.Dispose();}
  return result;
 }
 private static string ReadName(string path) {
  try {var name=FileVersionInfo.GetVersionInfo(path).ProductName;if(!string.IsNullOrWhiteSpace(name))return name.Trim();}catch { }
  return Path.GetFileNameWithoutExtension(path);
 }
 private static string WithSeparator(string path)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))+Path.DirectorySeparatorChar;
}
