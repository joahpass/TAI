using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("TAI 安装向导")]
[assembly: AssemblyProduct("TAI 工程助手")]
[assembly: AssemblyCompany("TAI")]

internal static class Setup {
 internal static string Quote(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }
 [STAThread] static int Main(string[] args) {
  if(args.Length > 0) return Run(args, s => Console.WriteLine(s));
  Application.EnableVisualStyles();
  Application.SetCompatibleTextRenderingDefault(false);
  using(var form = new InstallForm()) { Application.Run(form); return form.ResultCode; }
 }
 internal static int Run(string[] args, Action<string> report) {
  string temp = Path.Combine(Path.GetTempPath(), "TAI-Setup-" + Guid.NewGuid().ToString("N"));
  var owned = new List<string>();
  try {
   string logDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TAI","logs");
   Directory.CreateDirectory(logDir);
   string logPath=Path.Combine(logDir,"setup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+".log");
   Action<string> display=report;object logGate=new object();
   report=message=> { lock(logGate) { try { File.AppendAllText(logPath,DateTime.Now.ToString("HH:mm:ss ")+message+Environment.NewLine,Encoding.UTF8); } catch {} } display(message); };
   report("安装日志："+logPath);
   var forwarded = new List<string>();
   for(int i=0;i<args.Length;i++) {
    string a=args[i];
    if(a.Equals("-NoRegistration",StringComparison.OrdinalIgnoreCase)) forwarded.Add(a);
    else if(a.Equals("-TiaVersion",StringComparison.OrdinalIgnoreCase)) {
     if(++i>=args.Length || (args[i]!="19" && args[i]!="21")) throw new ArgumentException("TiaVersion must be 19 or 21.");
     forwarded.Add(a);forwarded.Add(args[i]);
    } else if(a.Equals("-InstallRoot",StringComparison.OrdinalIgnoreCase) || a.Equals("-AddInRoot",StringComparison.OrdinalIgnoreCase) || a.Equals("-TiaLocation",StringComparison.OrdinalIgnoreCase)) {
     if(++i>=args.Length || args[i].Contains("\"") || args[i].EndsWith("\\")) throw new ArgumentException("Invalid path parameter (omit trailing slash).");
     forwarded.Add(a);forwarded.Add(Quote(args[i]));
    } else throw new ArgumentException("Unknown setup parameter: "+a);
   }
   report("TAI_PROGRESS|3|正在释放安装文件");
   Directory.CreateDirectory(temp);
   using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("TAI.Payload.zip"))
   using(var zip=new ZipArchive(stream,ZipArchiveMode.Read)) {
    foreach(var e in zip.Entries) {
     string p=Path.GetFullPath(Path.Combine(temp,e.FullName));
     if(!p.StartsWith(temp+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe archive path.");
     if(e.Name.Length==0) { Directory.CreateDirectory(p);continue; }
     Directory.CreateDirectory(Path.GetDirectoryName(p));
     using(var input=e.Open()) using(var output=File.Create(p)) input.CopyTo(output);
     owned.Add(p);
    }
   }
   report("TAI_PROGRESS|10|正在核对安装环境");
   string powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
   var psi=new ProcessStartInfo(powershell,"-NoProfile -ExecutionPolicy Bypass -File "+Quote(Path.Combine(temp,"Install.ps1"))+" "+String.Join(" ",forwarded));
   psi.UseShellExecute=false;psi.CreateNoWindow=true;
   psi.RedirectStandardOutput=true;psi.RedirectStandardError=true;
   psi.StandardOutputEncoding=new UTF8Encoding(false);psi.StandardErrorEncoding=new UTF8Encoding(false);
   psi.EnvironmentVariables["PSModulePath"]=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\Modules");
   using(var process=new Process()) {
    process.StartInfo=psi;
    process.OutputDataReceived+=(s,e)=> { if(e.Data!=null) report(e.Data); };
    process.ErrorDataReceived+=(s,e)=> { if(e.Data!=null) report(e.Data); };
    process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();
    process.WaitForExit();return process.ExitCode;
   }
  } catch(Exception e) { report("安装失败："+e.Message);return 1; }
  finally {
   foreach(var p in owned) { try { File.Delete(p); } catch {} }
   try { var dirs=Directory.GetDirectories(temp,"*",SearchOption.AllDirectories);Array.Sort(dirs,(a,b)=>b.Length.CompareTo(a.Length));foreach(var d in dirs) if(Directory.GetFileSystemEntries(d).Length==0) Directory.Delete(d);if(Directory.GetFileSystemEntries(temp).Length==0) Directory.Delete(temp); } catch {}
  }
 }
}

internal sealed class InstallForm : Form {
 readonly TextBox destination=new TextBox(), portal=new TextBox(), log=new TextBox();
 readonly ComboBox version=new ComboBox();
 readonly Button install=new Button(), back=new Button(), close=new Button(), choose=new Button(), choosePortal=new Button();
 readonly ProgressBar progress=new ProgressBar();
 readonly Label status=new Label(), heading=new Label(), subtitle=new Label(), selection=new Label(), complete=new Label();
 readonly CheckBox launch=new CheckBox();
 readonly LinkLabel details=new LinkLabel(), viewLog=new LinkLabel();
 readonly Panel body=new Panel();
 readonly Panel[] pages=new Panel[4];
 readonly Label[] steps=new Label[4];
 readonly Dictionary<int,string> detected=new Dictionary<int,string>();
 readonly Color accent=Color.FromArgb(24,104,83), muted=Color.FromArgb(99,110,120);
 bool running, portalEdited, updatingPortal, succeeded;
 int page;
 string installedRoot;
 internal int ResultCode { get; private set; }
 internal static int HighestInstalled(IEnumerable<int> versions) {
  int highest=0;foreach(int v in versions)if((v==19||v==21)&&v>highest)highest=v;return highest;
 }
 int SelectedVersion() { return version.SelectedIndex==1?19:version.SelectedIndex==2?21:HighestInstalled(detected.Keys); }
 internal static string DetectPortal(int v) {
  var roots=new List<string>();
  foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32}) {
   using(var b=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,view)) {
    using(var key=b.OpenSubKey(@"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP"+v+@"\TIA_Opns")) if(key!=null)roots.Add(Convert.ToString(key.GetValue("Path","")));
    string suffix=v==21?@"\net48":"";
    using(var key=b.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness\"+v+".0\\PublicAPI\\"+v+".0.0.0"+suffix)) if(key!=null) {
     string dll=Convert.ToString(key.GetValue(v==21?"Siemens.Engineering.Base":"Siemens.Engineering",""));
     if(File.Exists(dll)) {var d=new DirectoryInfo(Path.GetDirectoryName(dll));if(v==21)d=d.Parent;if(d!=null&&d.Parent!=null&&d.Parent.Parent!=null)roots.Add(d.Parent.Parent.FullName);}
    }
   }
  }
  foreach(string env in new[]{"TAI_TIA_LOCATION","TiaPortalLocation"})roots.Add(Environment.GetEnvironmentVariable(env));
  foreach(string env in new[]{"ProgramFiles","ProgramFiles(x86)"}) {string p=Environment.GetEnvironmentVariable(env);if(!String.IsNullOrEmpty(p))roots.Add(Path.Combine(p,@"Siemens\Automation\Portal V"+v));}
  foreach(string root in roots) {
   if(String.IsNullOrWhiteSpace(root))continue;
   try {string dll=Path.Combine(root,v==21?@"PublicAPI\V21\net48\Siemens.Engineering.Base.dll":@"PublicAPI\V19\Siemens.Engineering.dll");
    if(File.Exists(dll)&&AssemblyName.GetAssemblyName(dll).Version.Major==v)return Path.GetFullPath(root);
   } catch(IOException) {} catch(UnauthorizedAccessException) {} catch(System.Security.SecurityException) {} catch(ArgumentException) {} catch(BadImageFormatException) {}
  }
  return "";
 }
 void RefreshPortal() {
  int v=SelectedVersion();
  if(!portalEdited) {updatingPortal=true;portal.Text=v>0&&detected.ContainsKey(v)?detected[v]:"";updatingPortal=false;}
  selection.Text=v==0?"未发现支持的博途版本。请选择 V19 或 V21，并指定其安装目录。":
   "将安装 V"+v+" 集成"+(version.SelectedIndex==0?"（本机已检测到的最高支持版本）":"（手动选择）")+"。";
  selection.ForeColor=v==0?Color.FromArgb(166,83,28):accent;
 }
 Label TextLabel(Control parent,string text,int x,int y,int w,int h,float size,bool bold) {
  var label=new Label {Text=text,Location=new Point(x,y),Size=new Size(w,h),Font=new Font(Font.FontFamily,size,bold?FontStyle.Bold:FontStyle.Regular),ForeColor=ForeColor};parent.Controls.Add(label);return label;
 }
 public InstallForm() {
  Text="TAI 安装向导";ClientSize=new Size(800,550);AutoScaleMode=AutoScaleMode.Dpi;
  FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=true;StartPosition=FormStartPosition.CenterScreen;
  Font=new Font("Microsoft YaHei UI",9F);BackColor=Color.White;ForeColor=Color.FromArgb(33,43,51);
  var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Margin=Padding.Empty,Padding=Padding.Empty};
  layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,188));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
  layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,66));Controls.Add(layout);
  var rail=new Panel {Dock=DockStyle.Fill,BackColor=Color.FromArgb(239,245,242),Margin=Padding.Empty};layout.Controls.Add(rail,0,0);
  TextLabel(rail,"TAI",26,30,140,56,30,true).ForeColor=accent;
  TextLabel(rail,"工程助手",28,90,145,30,13,true);
  TextLabel(rail,"多项目 · 多设计\nPLC / HMI 工程协作",28,128,150,52,9,false).ForeColor=muted;
  string[] names={"欢迎使用","安装选项","正在安装","安装完成"};
  for(int i=0;i<4;i++)steps[i]=TextLabel(rail,(i+1)+"   "+names[i],28,220+i*44,150,30,10,false);
  var build=Assembly.GetExecutingAssembly().GetName().Version;
  TextLabel(rail,"版本 "+build.Major+"."+build.Minor+"."+build.Build+"  ·  64 位",28,432,154,26,9,false).ForeColor=muted;
  body.Dock=DockStyle.Fill;body.Margin=Padding.Empty;layout.Controls.Add(body,1,0);
  heading.SetBounds(30,27,550,36);heading.Font=new Font(Font.FontFamily,17,FontStyle.Bold);body.Controls.Add(heading);
  subtitle.SetBounds(32,72,548,38);subtitle.ForeColor=muted;body.Controls.Add(subtitle);
  for(int i=0;i<4;i++) {pages[i]=new Panel {Location=new Point(30,122),Size=new Size(552,348),BackColor=Color.White};body.Controls.Add(pages[i]);}
  BuildWelcome();BuildOptions();BuildProgress();BuildComplete();
  var footer=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty,BackColor=Color.FromArgb(248,249,250)};layout.Controls.Add(footer,0,1);layout.SetColumnSpan(footer,2);
  footer.Paint+=(s,e)=>e.Graphics.DrawLine(Pens.Gainsboro,0,0,footer.Width,0);
  close.Text="取消";close.SetBounds(674,17,100,33);close.Click+=(s,e)=>Close();footer.Controls.Add(close);
  install.SetBounds(552,17,110,33);install.FlatStyle=FlatStyle.Flat;install.FlatAppearance.BorderSize=0;install.BackColor=accent;install.ForeColor=Color.White;install.Click+=async(s,e)=>await Next();footer.Controls.Add(install);
  back.Text="上一步";back.SetBounds(436,17,104,33);back.Click+=(s,e)=>ShowPage(page==1?0:1);footer.Controls.Add(back);
  AcceptButton=install;CancelButton=close;
  foreach(int v in new[]{21,19}) {string p=DetectPortal(v);if(p.Length>0)detected[v]=p;}
  destination.Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Programs\TAI");
  using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TAI")) {
   string previous=key==null?"":Convert.ToString(key.GetValue("InstallLocation",""));
   if(!String.IsNullOrWhiteSpace(previous)&&File.Exists(Path.Combine(previous,".tai-install.json")))destination.Text=previous;
  }
  version.SelectedIndexChanged+=(s,e)=>{portalEdited=false;RefreshPortal();};
  portal.TextChanged+=(s,e)=>{if(!updatingPortal)portalEdited=true;};version.SelectedIndex=0;RefreshPortal();ShowPage(0);
  FormClosing+=(s,e)=>{if(running){e.Cancel=true;status.Text="正在安装，请等待当前操作完成。";}};
 }
 void BuildWelcome() {
  TextLabel(pages[0],"欢迎使用 TAI 安装向导",0,0,540,36,14,true);
  TextLabel(pages[0],"本向导将引导您完成 TAI 的安装或升级。",0,53,540,32,10,false);
  TextLabel(pages[0],"项目与设计集中管理\n在多个设计标签之间切换，草稿和工程上下文分别保存。\n\n博途集成\n自动识别本机 V19 / V21，提供 PLC 与 HMI 工程工具。",0,103,540,140,10,false);
  TextLabel(pages[0],"安装前请退出托盘中的 TAI 集成，并关闭 TAI 窗口。\n点击“下一步”选择安装目录和博途版本。",0,267,540,64,9,false).ForeColor=muted;
 }
 void BuildOptions() {
  TextLabel(pages[1],"安装位置",0,0,540,25,10,true);
  destination.SetBounds(0,32,441,28);pages[1].Controls.Add(destination);choose.Text="浏览…";choose.SetBounds(452,30,98,32);choose.Click+=(s,e)=>Browse(destination);pages[1].Controls.Add(choose);
  TextLabel(pages[1],"博途版本",0,83,540,25,10,true);
  version.DropDownStyle=ComboBoxStyle.DropDownList;version.Items.AddRange(new object[]{"自动选择最高已安装版本（推荐）","TIA Portal V19","TIA Portal V21"});version.SetBounds(0,115,550,30);pages[1].Controls.Add(version);
  selection.SetBounds(0,153,550,40);pages[1].Controls.Add(selection);
  TextLabel(pages[1],"博途安装目录",0,200,540,25,10,true);
  portal.SetBounds(0,231,441,28);pages[1].Controls.Add(portal);choosePortal.Text="浏览…";choosePortal.SetBounds(452,229,98,32);choosePortal.Click+=(s,e)=>Browse(portal);pages[1].Controls.Add(choosePortal);
  var prerequisites=TextLabel(pages[1],"当前用户安装 · 需要 .NET Framework 4.8 和 WebView2 Runtime",0,278,550,25,9,false);prerequisites.ForeColor=muted;
  var webview=new LinkLabel {Text="获取 WebView2 Runtime",AutoSize=true,Location=new Point(0,311),LinkColor=accent};
  webview.LinkClicked+=(s,e)=>OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");pages[1].Controls.Add(webview);
 }
 void BuildProgress() {
  status.SetBounds(0,0,548,52);status.Font=new Font(Font.FontFamily,11,FontStyle.Bold);pages[2].Controls.Add(status);
  progress.SetBounds(0,65,550,20);progress.Style=ProgressBarStyle.Blocks;pages[2].Controls.Add(progress);
  TextLabel(pages[2],"安装程序正在复制文件并配置博途集成，请稍候。",0,98,550,32,9,false).ForeColor=muted;
  details.Text="显示安装详细信息";details.AutoSize=true;details.Location=new Point(0,143);details.LinkColor=accent;
  details.LinkClicked+=(s,e)=>{log.Visible=!log.Visible;details.Text=log.Visible?"隐藏安装详细信息":"显示安装详细信息";};pages[2].Controls.Add(details);
  log.Multiline=true;log.ReadOnly=true;log.ScrollBars=ScrollBars.Vertical;log.WordWrap=true;log.Font=new Font("Consolas",9);log.BackColor=Color.FromArgb(249,250,250);log.SetBounds(0,174,550,164);log.Visible=false;pages[2].Controls.Add(log);
 }
 void BuildComplete() {
  complete.SetBounds(0,0,545,218);complete.Font=new Font(Font.FontFamily,11);pages[3].Controls.Add(complete);
  launch.Text="退出安装向导后启动 TAI 工作台";launch.Checked=true;launch.SetBounds(0,242,550,32);pages[3].Controls.Add(launch);
  viewLog.Text="查看安装详细信息";viewLog.AutoSize=true;viewLog.Location=new Point(0,242);viewLog.LinkColor=accent;viewLog.Visible=false;
  viewLog.LinkClicked+=(s,e)=>{ShowPage(2);install.Enabled=false;back.Visible=true;back.Text="返回重试";close.Enabled=true;log.Visible=true;details.Text="隐藏安装详细信息";};pages[3].Controls.Add(viewLog);
  TextLabel(pages[3],"博途集成随用户登录自动启动。\n配置与诊断可从工作台左下角的“设置”打开。",0,290,550,54,9,false).ForeColor=muted;
 }
 void ShowPage(int target) {
  page=target;for(int i=0;i<4;i++){pages[i].Visible=i==page;steps[i].ForeColor=i==page?accent:muted;steps[i].Font=new Font(Font.FontFamily,10,i==page?FontStyle.Bold:FontStyle.Regular);}
  string[] titles={"安装 TAI 工程助手","选择安装选项","正在安装 TAI","TAI 安装完成"};
  string[] descriptions={"欢迎使用，让工程开发与管理更有条理。","确认安装位置和要集成的博途版本。","完成前请保持安装程序开启。","您现在可以开始使用新的多项目工作台。"};
  heading.Text=titles[page];subtitle.Text=descriptions[page];
  install.Text=page==0?"下一步":page==1?"安装":page==3?"完成":"正在安装";
  install.Enabled=page!=2;back.Visible=page==1||(page==3&&!succeeded);back.Enabled=!running;
  back.Text=page==1?"上一步":"返回重试";close.Enabled=!running;close.Visible=page!=3||!succeeded;
 }
 static void Browse(TextBox target) {using(var dialog=new FolderBrowserDialog()){if(Directory.Exists(target.Text))dialog.SelectedPath=target.Text;if(dialog.ShowDialog()==DialogResult.OK)target.Text=dialog.SelectedPath;}}
 void OpenUrl(string url) {try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch(Exception e){MessageBox.Show(this,e.Message,"TAI 安装向导",MessageBoxButtons.OK,MessageBoxIcon.Information);}}
 void Report(string message) {
  if(IsDisposed)return;if(InvokeRequired){BeginInvoke(new Action<string>(Report),message);return;}
  if(message.StartsWith("TAI_PROGRESS|",StringComparison.Ordinal)) {
   var parts=message.Split(new[]{'|'},3);int value;
   if(parts.Length==3&&Int32.TryParse(parts[1],out value)){progress.Value=Math.Max(progress.Value,Math.Max(0,Math.Min(100,value)));status.Text=parts[2]+"…";message=parts[2];}
  }
  log.AppendText(message+Environment.NewLine);
 }
 string ValidateOptions() {
  if(String.IsNullOrWhiteSpace(destination.Text))return "请选择安装位置。";
  try {string p=Path.GetFullPath(destination.Text.Trim());if(p.Length<8||!Path.IsPathRooted(destination.Text.Trim())||destination.Text.Contains("\""))return "请指定完整的安装目录，例如 D:\\Apps\\TAI。";}catch(Exception){return "安装目录格式有误，请重新选择。";}
  int v=SelectedVersion();if(v==0)return "未检测到博途。请选择 V19 或 V21，并指定已安装的博途目录。";
  if(String.IsNullOrWhiteSpace(portal.Text))return "请指定 TIA Portal V"+v+" 的安装目录。";
  try {string dll=Path.Combine(portal.Text.Trim(),v==21?@"PublicAPI\V21\net48\Siemens.Engineering.Base.dll":@"PublicAPI\V19\Siemens.Engineering.dll");
   if(!File.Exists(dll)||AssemblyName.GetAssemblyName(dll).Version.Major!=v)return "所选目录中未找到匹配的 V"+v+" Openness 组件，请重新选择。";
  }catch(Exception){return "博途目录校验失败，请检查目录与 Openness 组件。";}
  if(!HasFramework())return "请先安装 .NET Framework 4.8，再运行安装程序。";
  if(!HasWebView())return "请先安装 Microsoft Edge WebView2 Runtime，可通过下方链接获取。";
  return "";
 }
 async Task Next() {
  if(page==0){ShowPage(1);return;}
  if(page==3){if(succeeded&&launch.Checked)OpenWorkspace();Close();return;}
  if(page!=1||running)return;
  string error=ValidateOptions();if(error.Length>0){MessageBox.Show(this,error,"检查安装选项",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
  var args=new List<string>{"-InstallRoot",destination.Text.Trim().TrimEnd('\\','/'),"-TiaVersion",SelectedVersion().ToString(),"-TiaLocation",portal.Text.Trim().TrimEnd('\\','/')};
  running=true;ResultCode=1;progress.Value=0;log.Clear();log.Visible=false;details.Text="显示安装详细信息";status.Text="准备安装…";ShowPage(2);
  int result=await Task.Run(()=>Setup.Run(args.ToArray(),Report));
  CompleteInstall(result);
 }
 void CompleteInstall(int result) {
  running=false;ResultCode=result;succeeded=result==0;launch.Visible=succeeded;viewLog.Visible=!succeeded;ShowPage(3);
  if(succeeded){progress.Value=100;installedRoot=Path.GetFullPath(destination.Text.Trim());complete.Text="TAI 已成功安装到您的计算机。\n\n安装位置：\n"+installedRoot+"\n\n博途集成：TIA Portal V"+SelectedVersion()+"\n\n点击“完成”退出安装向导。";}
  else {heading.Text="安装尚未完成";subtitle.Text="您可以查看详细信息，处理问题后返回重试。";complete.Text="安装过程中出现问题。\n\n详细日志已保存到本地 TAI 日志目录。\n点击“返回重试”修改安装选项，或查看安装日志。";install.Text="关闭";}
 }
 void OpenWorkspace() {try{Process.Start(new ProcessStartInfo(Path.Combine(installedRoot,"TAI.Desktop.exe"),"--assistant"){UseShellExecute=true});}catch(Exception e){MessageBox.Show(this,e.Message,"启动 TAI",MessageBoxButtons.OK,MessageBoxIcon.Information);}}
 internal static bool HasFramework() {using(var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))return key!=null&&Convert.ToInt32(key.GetValue("Release",0))>=528040;}
 internal static bool HasWebView() {
  foreach(var hive in new[]{RegistryHive.LocalMachine,RegistryHive.CurrentUser})
   foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64})
    using(var root=RegistryKey.OpenBaseKey(hive,view))using(var clients=root.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients")) {
     if(clients==null)continue;
     foreach(var name in clients.GetSubKeyNames())using(var item=clients.OpenSubKey(name)) {
      string product=Convert.ToString(item.GetValue("name","")),pv=Convert.ToString(item.GetValue("pv",""));
      if(product.IndexOf("WebView2",StringComparison.OrdinalIgnoreCase)>=0&&!String.IsNullOrEmpty(pv)&&pv!="0.0.0.0")return true;
     }
    }
  return false;
 }
}
