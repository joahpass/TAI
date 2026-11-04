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
 readonly Panel bar=new Panel();
 readonly Label status=new Label(), heading=new Label(), subtitle=new Label(), selection=new Label(), complete=new Label();
 readonly CheckBox launch=new CheckBox();
 readonly LinkLabel details=new LinkLabel(), viewLog=new LinkLabel();
 readonly Panel body=new Panel();
 readonly Panel[] pages=new Panel[4];
 readonly Label[] steps=new Label[4];
 // 进度阶段：与 Install.ps1 输出的 TAI_PROGRESS 值一一对应，用于把"正在做什么"显示成可读的阶段列表。
 readonly Label[] phases=new Label[5];
 readonly int[] phaseAt={3,42,48,68,82};
 readonly string[] phaseName={"准备安装文件","关闭正在运行的 TAI","复制 TAI 程序文件","配置博途集成","验证已安装组件"};
 readonly Label requirementFramework=new Label(), requirementWebView=new Label(), requirementTia=new Label(), percent=new Label();
 readonly Dictionary<int,string> detected=new Dictionary<int,string>();
 readonly Color accent=Color.FromArgb(24,104,83), muted=Color.FromArgb(99,110,120), ok=Color.FromArgb(35,132,106), warn=Color.FromArgb(166,83,28);
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
  selection.Text=v==0?"未发现可用的博途 Openness。请选择 V19 或 V21，并指定它的安装目录。":
   "将安装 V"+v+" 集成"+(version.SelectedIndex==0?"（本机可用的最高版本）":"（手动选择）")+"。";
  selection.ForeColor=v==0?warn:accent;
 }
 void RefreshRequirements() {
  Mark(requirementFramework,".NET Framework 4.8",HasFramework());
  Mark(requirementWebView,"WebView2 Runtime",HasWebView());
  int v=HighestInstalled(detected.Keys);
  requirementTia.Text="博途 Openness   "+(v>0?"已检测到 V"+v:"未检测到 V19 / V21");
  requirementTia.ForeColor=v>0?ForeColor:warn;
 }
 void Mark(Label label,string name,bool okState) {
  label.Text=name+"   "+(okState?"已就绪":"未安装");
  label.ForeColor=okState?ForeColor:warn;
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
  string[] names={"环境检查","安装选项","正在安装","安装完成"};
  for(int i=0;i<4;i++)steps[i]=TextLabel(rail,(i+1)+"   "+names[i],28,206+i*40,150,26,10,false);
  var build=Assembly.GetExecutingAssembly().GetName().Version;
  TextLabel(rail,"版本 "+build.Major+"."+build.Minor+"."+build.Build+"  ·  64 位",28,432,154,26,9,false).ForeColor=muted;
  body.Dock=DockStyle.Fill;body.Margin=Padding.Empty;layout.Controls.Add(body,1,0);
  heading.SetBounds(30,27,550,36);heading.Font=new Font(Font.FontFamily,17,FontStyle.Bold);body.Controls.Add(heading);
  subtitle.SetBounds(32,72,548,38);subtitle.ForeColor=muted;body.Controls.Add(subtitle);
  for(int i=0;i<4;i++) {pages[i]=new Panel {Location=new Point(30,122),Size=new Size(552,348),BackColor=Color.White};body.Controls.Add(pages[i]);}
  BuildCheck();BuildOptions();BuildProgress();BuildComplete();
  var footer=new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty,BackColor=Color.FromArgb(248,249,250)};layout.Controls.Add(footer,0,1);layout.SetColumnSpan(footer,2);
  footer.Paint+=(s,e)=>e.Graphics.DrawLine(Pens.Gainsboro,0,0,footer.Width,0);
  close.Text="取消";close.SetBounds(674,17,100,33);close.Click+=(s,e)=>Close();footer.Controls.Add(close);
  install.SetBounds(552,17,110,33);install.FlatStyle=FlatStyle.Flat;install.FlatAppearance.BorderSize=0;install.BackColor=accent;install.ForeColor=Color.White;install.Click+=async(s,e)=>await Next();footer.Controls.Add(install);
  back.Text="上一步";back.SetBounds(436,17,104,33);back.Click+=(s,e)=>{if(page==3){ShowPage(1);return;}ShowPage(page==1?0:1);};footer.Controls.Add(back);
  AcceptButton=install;CancelButton=close;
  foreach(int v in new[]{21,19}) {string p=DetectPortal(v);if(p.Length>0)detected[v]=p;}
  destination.Text=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Programs\TAI");
  using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TAI")) {
   string previous=key==null?"":Convert.ToString(key.GetValue("InstallLocation",""));
   if(!String.IsNullOrWhiteSpace(previous)&&File.Exists(Path.Combine(previous,".tai-install.json")))destination.Text=previous;
  }
  version.SelectedIndexChanged+=(s,e)=>{portalEdited=false;RefreshPortal();};
  portal.TextChanged+=(s,e)=>{if(!updatingPortal)portalEdited=true;};version.SelectedIndex=0;RefreshPortal();RefreshRequirements();ShowPage(0);
  FormClosing+=(s,e)=>{if(running){e.Cancel=true;status.Text="正在安装，请等待当前操作完成。";}};
 }
 // 第 1 步：只读环境检查。打开向导即完成，不要求用户做任何准备动作。
 void BuildCheck() {
  TextLabel(pages[0],"安装前提",0,0,540,24,10,true);
  requirementFramework.SetBounds(0,30,540,24);pages[0].Controls.Add(requirementFramework);
  requirementWebView.SetBounds(0,56,540,24);pages[0].Controls.Add(requirementWebView);
  requirementTia.SetBounds(0,82,540,24);pages[0].Controls.Add(requirementTia);
  var rule=new Label {Location=new Point(0,120),Size=new Size(548,1),BackColor=Color.FromArgb(226,231,227)};pages[0].Controls.Add(rule);
  TextLabel(pages[0],"安装内容",0,136,540,24,10,true);
  TextLabel(pages[0],"工作台与本地服务 · 博途集成组件 · 托盘与自启动配置",0,164,548,24,9,false).ForeColor=muted;
  TextLabel(pages[0],"已安装的旧版本会先退出再升级，项目数据、设置与博途工程不受影响。",0,194,548,46,9,false).ForeColor=muted;
  TextLabel(pages[0],"安装期间无需手动关闭 TAI，向导会自动结束属于 TAI 的进程。",0,250,548,46,9,false).ForeColor=muted;
 }
 void BuildOptions() {
  TextLabel(pages[1],"安装位置",0,0,540,25,10,true);
  destination.SetBounds(0,30,441,28);pages[1].Controls.Add(destination);choose.Text="浏览…";choose.SetBounds(452,28,98,32);choose.Click+=(s,e)=>Browse(destination);pages[1].Controls.Add(choose);
  TextLabel(pages[1],"博途版本",0,74,540,25,10,true);
  version.DropDownStyle=ComboBoxStyle.DropDownList;version.Items.AddRange(new object[]{"自动选择最高已安装版本（推荐）","TIA Portal V19","TIA Portal V21"});version.SetBounds(0,104,550,30);pages[1].Controls.Add(version);
  selection.SetBounds(0,140,550,40);pages[1].Controls.Add(selection);
  TextLabel(pages[1],"博途安装目录",0,186,540,25,10,true);
  portal.SetBounds(0,216,441,28);pages[1].Controls.Add(portal);choosePortal.Text="浏览…";choosePortal.SetBounds(452,214,98,32);choosePortal.Click+=(s,e)=>Browse(portal);pages[1].Controls.Add(choosePortal);
  var prerequisites=TextLabel(pages[1],"当前用户安装 · .NET Framework 4.8 与 WebView2 Runtime 已在本机检查",0,262,550,24,9,false);prerequisites.ForeColor=muted;
  var webview=new LinkLabel {Text="重新获取 WebView2 Runtime",AutoSize=true,Location=new Point(0,290),LinkColor=accent};
  webview.LinkClicked+=(s,e)=>OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");pages[1].Controls.Add(webview);
  TextLabel(pages[1],"点击“安装”后，正在运行的 TAI 会被自动关闭，然后开始复制文件。",0,316,550,26,9,false).ForeColor=muted;
 }
 void BuildProgress() {
  status.SetBounds(0,0,548,30);status.Font=new Font(Font.FontFamily,11,FontStyle.Bold);pages[2].Controls.Add(status);
  progress.SetBounds(0,38,500,16);progress.Visible=false;pages[2].Controls.Add(progress);
  bar.SetBounds(0,38,500,16);bar.BackColor=Color.FromArgb(236,239,237);bar.Paint+=(s,e)=> {
   e.Graphics.Clear(bar.BackColor);
   int width=(int)Math.Round(bar.Width*progress.Value/100.0);
   if(width>0)using(var fill=new SolidBrush(accent))e.Graphics.FillRectangle(fill,0,0,width,bar.Height);
   using(var pen=new Pen(Color.FromArgb(207,214,209)))e.Graphics.DrawRectangle(pen,0,0,bar.Width-1,bar.Height-1);
  };pages[2].Controls.Add(bar);
  percent.SetBounds(508,36,42,22);percent.TextAlign=ContentAlignment.MiddleRight;percent.ForeColor=muted;pages[2].Controls.Add(percent);
  TextLabel(pages[2],"安装完成前请保持向导开启；您可以切换到其他窗口。",0,64,548,24,9,false).ForeColor=muted;
  for(int i=0;i<phases.Length;i++) {
   phases[i]=new Label {Text="○  "+phaseName[i],Location=new Point(0,96+i*26),Size=new Size(548,24),ForeColor=muted};
   pages[2].Controls.Add(phases[i]);
  }
  details.Text="显示安装详细信息";details.AutoSize=true;details.Location=new Point(0,236);details.LinkColor=accent;
  details.LinkClicked+=(s,e)=>{log.Visible=!log.Visible;details.Text=log.Visible?"隐藏安装详细信息":"显示安装详细信息";};pages[2].Controls.Add(details);
  log.Multiline=true;log.ReadOnly=true;log.ScrollBars=ScrollBars.Vertical;log.WordWrap=true;log.Font=new Font("Consolas",9);log.BackColor=Color.FromArgb(249,250,250);log.SetBounds(0,262,552,82);log.Visible=false;pages[2].Controls.Add(log);
 }
 void BuildComplete() {
  complete.SetBounds(0,0,545,214);complete.Font=new Font(Font.FontFamily,11);pages[3].Controls.Add(complete);
  launch.Text="完成后打开 TAI 工作台";launch.Checked=true;launch.SetBounds(0,228,550,30);pages[3].Controls.Add(launch);
  viewLog.Text="查看安装详细信息";viewLog.AutoSize=true;viewLog.Location=new Point(0,262);viewLog.LinkColor=accent;viewLog.Visible=false;
  viewLog.LinkClicked+=(s,e)=>{ShowPage(2);install.Enabled=false;back.Visible=true;back.Text="返回重试";close.Enabled=true;log.Visible=true;details.Text="隐藏安装详细信息";};pages[3].Controls.Add(viewLog);
  TextLabel(pages[3],"博途集成随用户登录自动启动。工作台左下角的“设置与诊断”可查看模型、连接与日志。",0,296,548,46,9,false).ForeColor=muted;
 }
 void ShowPage(int target) {
  page=target;for(int i=0;i<4;i++){pages[i].Visible=i==page;steps[i].ForeColor=i==page?accent:muted;steps[i].Font=new Font(Font.FontFamily,10,i==page?FontStyle.Bold:FontStyle.Regular);}
  string[] titles={"确认安装环境","选择安装选项","正在安装 TAI","TAI 安装完成"};
  string[] descriptions={"以下前提由向导自动检查，无需手动准备。","确认安装位置与要集成的博途版本。","正在运行的 TAI 会被自动关闭，请保持向导开启。","安装已经结束，可以关闭向导。"};
  heading.Text=titles[page];subtitle.Text=descriptions[page];
  install.Text=page==0?"下一步":page==1?"安装":page==3?"完成":"正在安装";
  install.Enabled=page!=2;back.Visible=page==1||(page==3&&!succeeded);back.Enabled=!running;
  back.Text=page==1?"上一步":"返回重试";close.Enabled=!running;close.Visible=page!=3||!succeeded;
 }
 static void Browse(TextBox target) {using(var dialog=new FolderBrowserDialog()){if(Directory.Exists(target.Text))dialog.SelectedPath=target.Text;if(dialog.ShowDialog()==DialogResult.OK)target.Text=dialog.SelectedPath;}}
 void OpenUrl(string url) {try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch(Exception e){MessageBox.Show(this,e.Message,"TAI 安装向导",MessageBoxButtons.OK,MessageBoxIcon.Information);}}
 void PaintPhases(int value) {
  for(int i=0;i<phases.Length;i++) {
   bool done=i<phases.Length-1?value>=phaseAt[i+1]:value>=100;
   bool active=!done&&value>=phaseAt[i];
   phases[i].Text=(done?"✓  ":active?"→  ":"○  ")+phaseName[i];
   phases[i].ForeColor=done?ok:active?accent:muted;
   phases[i].Font=new Font(Font.FontFamily,9,active?FontStyle.Bold:FontStyle.Regular);
  }
 }
 void Report(string message) {
  if(IsDisposed)return;if(InvokeRequired){BeginInvoke(new Action<string>(Report),message);return;}
  if(message.StartsWith("TAI_PROGRESS|",StringComparison.Ordinal)) {
   var parts=message.Split(new[]{'|'},3);int value;
   if(parts.Length==3&&Int32.TryParse(parts[1],out value)){
    bool current=value>=progress.Value;
     int applied=Math.Max(progress.Value,Math.Max(0,Math.Min(100,value)));
    progress.Value=applied;bar.Invalidate();percent.Text=applied+"%";PaintPhases(applied);if(current)status.Text=parts[2];message=parts[2];
   }
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
  running=true;ResultCode=1;progress.Value=0;bar.Invalidate();percent.Text="0%";PaintPhases(0);log.Clear();log.Visible=false;details.Text="显示安装详细信息";status.Text="正在准备安装";ShowPage(2);
  int result=await Task.Run(()=>Setup.Run(args.ToArray(),Report));
  CompleteInstall(result);
 }
 void CompleteInstall(int result) {
  running=false;ResultCode=result;succeeded=result==0;launch.Visible=succeeded;viewLog.Visible=!succeeded;ShowPage(3);
  if(succeeded){progress.Value=100;bar.Invalidate();percent.Text="100%";PaintPhases(100);installedRoot=Path.GetFullPath(destination.Text.Trim());complete.Text="TAI 已安装完成。\n\n安装位置\n"+installedRoot+"\n\n博途集成\nTIA Portal V"+SelectedVersion()+"\n\n点击“完成”关闭向导。";}
  else {heading.Text="安装未完成";subtitle.Text="查看详细信息定位问题，修改选项后可重新安装。";complete.Text="安装在中途停止，未完成的步骤需要重新执行。\n\n安装日志保存在本机 TAI 日志目录中。\n可点击“返回重试”修改安装选项，或查看安装日志。";install.Text="关闭";}
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
