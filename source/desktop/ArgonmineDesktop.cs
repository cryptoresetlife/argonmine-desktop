using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Program {
    [STAThread] static int Main(string[] args) {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        if (args.Contains("--self-test")) return Tests.Run();
        if (args.Contains("--rpc-self-test")) return Tests.LiveRpc();
        if (args.Contains("--ui-self-test")) {
            int code=0;
            using(var testForm=new MainWindow(false,Path.Combine(Path.GetTempPath(),"ArgonmineGuiStopTest-"+Guid.NewGuid().ToString("N")))) {
                testForm.Shown+=async(o,e)=>{try{await testForm.RunStopRaceTest();File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"UI-SELFTEST-PASSED.txt"),"A real GUI start immediately followed by stop does not launch a node or miner. No GPU used.");}catch(Exception ex){code=1;File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"UI-SELFTEST-FAILED.txt"),ex.ToString());}finally{testForm.Close();}};
                Application.Run(testForm);
            }
            return code;
        }
        bool created;
        using (var mutex = new Mutex(true, "Local\\ArgonmineDesktop-v1", out created)) {
            if (!created) { MessageBox.Show("软件已经打开，请使用现有窗口。", "Argonmine"); return 1; }
            try {
                using (var form = new MainWindow(args.Contains("--preview"))) {
                    int render = Array.IndexOf(args, "--render-preview");
                    if (render >= 0 && render + 1 < args.Length) {
                        form.Show(); Application.DoEvents();
                        using (var bmp = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size)); bmp.Save(args[render+1]); }
                    } else Application.Run(form);
                }
                return 0;
            } catch (Exception ex) { MessageBox.Show(ex.Message, "无法启动", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
    }
}

sealed class Preferences { public string Address = ""; public int Device = 0; public string GpuUUID=""; public long MemoryBytes; public bool Fast = false; }
sealed class GpuInfo {
    public int Index, Cc; public string Name, UUID; public long MemoryBytes; public bool Supported;
    public override string ToString() {return Name+" · "+(MemoryBytes/1073741824.0).ToString("0.#")+"GB"+(Supported?"":" · 本版本不支持");}
}
sealed class Detection { public GpuInfo[] Devices {get;set;} public string Error {get;set;} }
sealed class Snapshot {
    public string Genesis; public long Height, PeerHeight, TipTime; public int Peers; public bool Syncing;
    public bool Ready(long now) { return Genesis == Engine.Genesis && Height > 0 && Peers > 0 && !Syncing && Height >= PeerHeight && now - TipTime <= 1800 && TipTime <= now + 120; }
}

// Every child belongs to this application's own Job object. Nothing is killed by name.
sealed class ChildJob : IDisposable {
    IntPtr handle;
    [StructLayout(LayoutKind.Sequential)] struct BASIC { public long PerProcess, PerJob; public uint Flags; public UIntPtr MinWS, MaxWS; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IO { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct EXTENDED { public BASIC Basic; public IO Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr j, int type, IntPtr p, uint n);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr j, IntPtr p);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    public ChildJob() {
        handle=CreateJobObject(IntPtr.Zero,null); if(handle==IntPtr.Zero) throw new Exception("无法创建进程隔离组。");
        var info=new EXTENDED(); info.Basic.Flags=0x2000; var size=Marshal.SizeOf(info); var p=Marshal.AllocHGlobal(size);
        try { Marshal.StructureToPtr(info,p,false); if(!SetInformationJobObject(handle,9,p,(uint)size)) throw new Exception("无法设置进程退出保护。"); }
        catch { Dispose(); throw; } finally { Marshal.FreeHGlobal(p); }
    }
    public void Add(Process p) { if(!AssignProcessToJobObject(handle,p.Handle)) { try { p.Kill(); } catch {} throw new Exception("无法隔离后台进程，已取消启动。"); } }
    public void Dispose() { if(handle!=IntPtr.Zero) { CloseHandle(handle); handle=IntPtr.Zero; } }
}

sealed class Engine : IDisposable {
    public const string Genesis="e378ed192099aa7693a063071926aff1415165557b9e1edebfdbbe65a1cac8f6";
    public readonly string Root, Data, Run, LogDir;
    public const string Endpoint="http://127.0.0.1:19352/";
    public Process Node, Miner;
    ChildJob nodeJob, minerJob;
    readonly object logLock=new object();
    public event Action<string> Log;
    public Engine(string root, string data) { Root=root; Data=data; Run=Path.Combine(data,"miner"); LogDir=Path.Combine(data,"logs"); }
    public void Prepare() { Directory.CreateDirectory(Data); Directory.CreateDirectory(Run); Directory.CreateDirectory(LogDir); }
    public static string Q(string s) { return "\"" + System.Text.RegularExpressions.Regex.Replace(s, "(\\\\*)\"", "$1$1\\\"").TrimEnd('\\') + new string('\\', s.Reverse().TakeWhile(c=>c=='\\').Count()*2) + "\""; }
    public void Say(string text) { var e=Log; if(e!=null) e(text); }
    public void Verify() {
        var json=new JavaScriptSerializer();
        var rows=json.DeserializeObject(File.ReadAllText(Path.Combine(Root,"binaries.json"))) as object[];
        if(rows==null || rows.Length!=4) throw new Exception("程序校验清单缺失，请重新解压完整安装包。");
        foreach(var row in rows) {
            var d=(Dictionary<string,object>)row; var name=(string)d["file"];
            if(name!="bin/argonmined.exe" && name!="bin/argonmine-gpu.exe" && name!="bin/argon-gpu-worker.exe" && name!="bin/argon-gpu-detect.exe") throw new Exception("无效校验项目。");
            using(var hash=SHA256.Create()) using(var f=File.OpenRead(Path.Combine(Root,name))) {
                if(BitConverter.ToString(hash.ComputeHash(f)).Replace("-","")!=(string)d["sha256"]) throw new Exception("程序文件校验失败："+name+"。请重新获取完整包。");
            }
        }
    }
    public void ValidateAddress(string address) {
        if(address.Length>90 || address.IndexOfAny(new[]{'"','\r','\n'})>=0) throw new Exception("请输入自己的 ARG 主网收款地址。");
        var si=new ProcessStartInfo(Path.Combine(Root,"bin/argonmine-gpu.exe"),"-check-address -address "+Q(address)) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true, RedirectStandardOutput=true };
        using(var p=Process.Start(si)) { if(!p.WaitForExit(5000)) { p.Kill(); throw new Exception("地址校验超时。"); } if(p.ExitCode!=0) throw new Exception("ARG地址无效或校验码错误。请完整复制主网 arg1 收款地址。"); }
    }
    public Detection Detect() {
        Verify();
        var si=new ProcessStartInfo(Path.Combine(Root,"bin/argon-gpu-detect.exe")) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        si.EnvironmentVariables.Remove("CUDA_VISIBLE_DEVICES");
        using(var job=new ChildJob()) using(var p=Process.Start(si)) {
            job.Add(p); var text=p.StandardOutput.ReadToEndAsync(); var errors=p.StandardError.ReadToEndAsync();
            if(!p.WaitForExit(8000))throw new Exception("NVIDIA驱动检测超时。没有启动计算，请稍后手动重新检测。");
            if(!text.Wait(2000))throw new Exception("显卡检测输出超时。");
            var result=new JavaScriptSerializer().Deserialize<Detection>(text.Result);
            if(result==null || result.Devices==null)throw new Exception("无法读取显卡检测结果。");
            if(p.ExitCode!=0)throw new Exception("显卡检测失败："+result.Error);
            return result;
        }
    }
    public static int PlanBatch(long memoryBytes,bool fast) {
        double budgetMiB=Math.Min(fast?16384:4096,memoryBytes/1048576.0*(fast?0.60:0.35));
        int batch=64; while(batch<2048 && (batch*2)*8<=budgetMiB)batch*=2; return batch;
    }
    Process Launch(string file,string arguments,string directory,string logfile,ChildJob job,string deviceUUID) {
        var si=new ProcessStartInfo(Path.Combine(Root,"bin",file),arguments) { WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        if(deviceUUID!=null) {
            if(!System.Text.RegularExpressions.Regex.IsMatch(deviceUUID,"^[0-9a-f]{32}$"))throw new Exception("无效显卡身份，请重新检测。");
            string u=deviceUUID;
            si.EnvironmentVariables["CUDA_VISIBLE_DEVICES"]="GPU-"+u.Substring(0,8)+"-"+u.Substring(8,4)+"-"+u.Substring(12,4)+"-"+u.Substring(16,4)+"-"+u.Substring(20,12);
        }
        var p=new Process { StartInfo=si };
        DataReceivedEventHandler output=(o,e)=>{ if(e.Data==null) return; lock(logLock) { try { File.AppendAllText(logfile,e.Data+Environment.NewLine,Encoding.UTF8); } catch {} } Say(e.Data); };
        p.OutputDataReceived+=output; p.ErrorDataReceived+=output;
        if(!p.Start()) throw new Exception("启动失败："+file);
        job.Add(p); p.BeginOutputReadLine(); p.BeginErrorReadLine(); return p;
    }
    public static bool Alive(Process p) { try { return p!=null && !p.HasExited; } catch { return false; } }
    public void StartNode() {
        if(Alive(Node)) return;
        if(Node!=null) { Node.Dispose(); Node=null; }
        if(nodeJob!=null) { nodeJob.Dispose(); nodeJob=null; }
        string halt=Path.Combine(Data,"STOP-NODE"); if(File.Exists(halt)) File.Delete(halt);
        // Fixed dedicated loopback port; a pre-existing listener is never adopted or stopped.
        var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,19352);
        try { listener.Start(); } catch { throw new Exception("本机19352端口已占用；请先关闭其他本软件实例，软件不会接管其他节点。"); } finally { listener.Stop(); }
        nodeJob=new ChildJob();
        try { Node=Launch("argonmined.exe","-network mainnet -datadir "+Q(Path.Combine(Data,"node"))+" -rpc 127.0.0.1:19352 -listen none -stopfile "+Q(halt),Data,Path.Combine(LogDir,"node.log"),nodeJob,null); }
        catch { nodeJob.Dispose(); nodeJob=null; throw; }
        Say("已启动本软件的本地主网节点，等待同步。");
    }
    public object Rpc(string method, params object[] args) {
        string cookie=Path.Combine(Data,"node","rpc.cookie");
        if(!File.Exists(cookie)) throw new IOException("节点正在初始化。");
        var json=new JavaScriptSerializer(); var req=(HttpWebRequest)WebRequest.Create(Endpoint);
        req.Proxy=null; req.Method="POST"; req.ContentType="application/json"; req.Timeout=4000; req.ReadWriteTimeout=4000;
        req.Headers["Authorization"]="Bearer "+File.ReadAllText(cookie).Trim();
        var bytes=Encoding.UTF8.GetBytes(json.Serialize(new {jsonrpc="2.0",id=1,method=method,@params=args})); req.ContentLength=bytes.Length;
        using(var stream=req.GetRequestStream()) stream.Write(bytes,0,bytes.Length);
        using(var res=req.GetResponse()) using(var sr=new StreamReader(res.GetResponseStream())) {
            var v=json.Deserialize<Dictionary<string,object>>(sr.ReadToEnd());
            if(v.ContainsKey("error") && v["error"]!=null) throw new Exception("节点RPC请求失败："+method);
            return v["result"];
        }
    }
    public Snapshot ReadSnapshot() {
        return ParseSnapshot((Dictionary<string,object>)Rpc("getblockchaininfo"),Rpc("getpeerinfo"));
    }
    public static Snapshot ParseSnapshot(Dictionary<string,object> c,object peerResult) {
        // Deserialize<Dictionary<string,object>> represents nested JSON arrays as ArrayList.
        // Accept the collection contract instead of assuming object[].
        var peers=peerResult as System.Collections.IEnumerable;
        if(peers==null || peerResult is string || peerResult is System.Collections.IDictionary)throw new FormatException("节点连接列表格式无效。");
        var s=new Snapshot { Genesis=(string)c["genesis"],Height=Convert.ToInt64(c["blocks"]),Syncing=Convert.ToBoolean(c["initialblockdownload"]),TipTime=Convert.ToInt64(c["time"]) };
        foreach(var entry in peers) {
            var p=entry as Dictionary<string,object>;
            if(p==null || !p.ContainsKey("startheight"))throw new FormatException("节点连接记录格式无效。");
            s.Peers++; s.PeerHeight=Math.Max(s.PeerHeight,Convert.ToInt64(p["startheight"]));
        }
        if(s.Genesis!=Genesis) throw new Exception("节点创世块不匹配，已阻止挖矿。");
        return s;
    }
    public void StartMiner(Preferences p) {
        if(Alive(Miner)) throw new Exception("已有挖矿进程。");
        foreach(var name in new[]{"argonmine-gpu","SRBMiner-MULTI","ForgeMiner"}) {
            var existing=Process.GetProcessesByName(name); bool found=existing.Length>0;
            foreach(var proc in existing)proc.Dispose();
            if(found)throw new Exception("检测到其他矿工（"+name+"）。请先停止旧矿工，再用本软件启动，避免争用显卡。");
        }
        if(File.Exists(Path.Combine(Run,"HALTED"))) throw new Exception("发现异常保护 HALTED。请打开日志排查，软件不会自动清除保护。");
        string stop=Path.Combine(Run,"STOPPED"); if(File.Exists(stop)) File.Delete(stop);
        if(File.Exists(stop))throw new IOException("停止标记无法移除，请检查数据目录权限。");
        Say("开挖前检查：本次手动启动已移除停止标记，计算目录 "+Run);
        minerJob=new ChildJob();
        try { Miner=Launch("argonmine-gpu.exe","-worker "+Q(Path.Combine(Root,"bin/argon-gpu-worker.exe"))+" -address "+Q(p.Address)+" -cookie "+Q(Path.Combine(Data,"node/rpc.cookie"))+" -rpc "+Endpoint+" -gpu-uuid "+p.GpuUUID+" -batch "+PlanBatch(p.MemoryBytes,p.Fast)+" -duty "+(p.Fast?"100":"80")+" -rounds 4 -pause 0ms -duration 0s",Run,Path.Combine(LogDir,"miner.log"),minerJob,p.GpuUUID); }
        catch { minerJob.Dispose(); minerJob=null; throw; }
        Say("启动矿工，正在校验 CPU/GPU 结果。首次自检可能需要一些时间。");
    }
    public void StopMiner() {
        File.WriteAllText(Path.Combine(Run,"STOPPED"),"Manual stop "+DateTime.UtcNow.ToString("o"));
        if(Alive(Miner) && !Miner.WaitForExit(8000)) { Say("矿工未及时退出，结束本软件拥有的矿工进程组。"); }
        if(minerJob!=null) { minerJob.Dispose(); minerJob=null; }
        if(Miner!=null) { Miner.Dispose(); Miner=null; }
    }
    public void StopNode() {
        if(Alive(Node)) { File.WriteAllText(Path.Combine(Data,"STOP-NODE"),"Desktop shutdown"); if(!Node.WaitForExit(15000)) Say("节点退出超时，仅结束本软件的节点进程组；下次启动会检查数据库。"); }
        if(nodeJob!=null) { nodeJob.Dispose(); nodeJob=null; }
        if(Node!=null) { Node.Dispose(); Node=null; }
    }
    public Dictionary<string,object> MiningStatus(DateTime started) {
        var f=Path.Combine(Run,"mining-status.json");
        if(!File.Exists(f) || File.GetLastWriteTimeUtc(f)<started) return null;
        return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(f));
    }
    public void Dispose() { if(minerJob!=null)minerJob.Dispose(); if(nodeJob!=null)nodeJob.Dispose(); }
}

sealed class MainWindow : Form {
    const string InvitationUrl="https://app.argonmine.com/?ref=arg1qt8m4tkg5a2yvxlqumdvvuqza8nekrfzk8dhu6cnh5af9cy55atsq7zefrm";
    readonly Engine engine;
    readonly TextBox address=new TextBox(); readonly ComboBox mode=new ComboBox(), device=new ComboBox();
    readonly Button start=new Button(), stop=new Button(), sync=new Button(), refresh=new Button();
    readonly Label state=new Label(), detail=new Label(), height=new Label(), peers=new Label(), rate=new Label(), candidates=new Label();
    readonly RichTextBox logs=new RichTextBox(); readonly ProgressBar progress=new ProgressBar();
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
    readonly string config; readonly bool preview;
    bool intent, pollBusy, starting, stopping, exiting, closeAllowed, detecting; int readyCount; string preferredUUID="",lastRpcError="";
    int generation; DateTime minerStart; Preferences active;
    static readonly Color Bg=Color.FromArgb(14,20,31), PanelColor=Color.FromArgb(24,34,50), Ink=Color.FromArgb(231,239,250), Muted=Color.FromArgb(151,170,195), Accent=Color.FromArgb(59,211,170);
    public MainWindow(bool isPreview, string dataOverride=null) {
        preview=isPreview; string root=AppDomain.CurrentDomain.BaseDirectory;
        string data=dataOverride??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ArgonmineDesktop");
        engine=new Engine(root,data); config=Path.Combine(data,"preferences.json");
        Text="Argonmine Desktop · NVIDIA"; Font=new Font("Microsoft YaHei UI",10); BackColor=Bg; ForeColor=Ink;
        ClientSize=new Size(1000,790); MinimumSize=new Size(900,770); StartPosition=FormStartPosition.CenterScreen; AutoScaleMode=AutoScaleMode.Dpi;
        BuildUI();
        engine.Log+=AppendLog;
        if(!preview) { engine.Prepare(); LoadPrefs(); }
        start.Click+=async (o,e)=>await BeginSession(true); sync.Click+=async (o,e)=>await BeginSession(false);
        refresh.Click+=async(o,e)=>await DetectCards();
        device.SelectedIndexChanged+=(o,e)=>{var selected=device.SelectedItem as GpuInfo;if(selected!=null)preferredUUID=selected.UUID;ControlsState();};
        if(!preview && dataOverride==null) Shown+=async(o,e)=>await DetectCards();
        stop.Click+=async(o,e)=>await StopSession(true);
        timer.Interval=3000; timer.Tick+=async(o,e)=>await Poll(); if(!preview)timer.Start();
        FormClosing+=async(o,e)=>{ if(closeAllowed||preview)return; e.Cancel=true; if(exiting||stopping)return; exiting=true; await StopSession(true); closeAllowed=true; Close(); };
        if(preview) { state.Text="界面预览 · 未启动节点或挖矿"; detail.Text="填入自己的钱包地址，点击开始；节点同步完成后自动开挖。"; }
    }
    Label L(string t,int size,Color color) { return new Label {Text=t,AutoSize=false,Font=new Font("Microsoft YaHei UI",size,FontStyle.Regular),ForeColor=color,BackColor=Color.Transparent}; }
    void BuildUI() {
        var title=L("ARGONMINE",26,Ink); title.SetBounds(28,20,600,48); Controls.Add(title);
        var sub=L("本地节点 · 自动同步 · NVIDIA 显卡自动检测",11,Muted); sub.SetBounds(31,73,800,26); Controls.Add(sub);
        var badge=L("第三方实验版  0.2.2",10,Accent); badge.SetBounds(757,38,215,27); badge.Anchor=AnchorStyles.Top|AnchorStyles.Right; Controls.Add(badge);
        var box=new Panel{BackColor=PanelColor}; box.SetBounds(28,113,944,195); box.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right; Controls.Add(box);
        var a=L("你的 ARG 收款地址",11,Ink); a.SetBounds(20,16,600,26); box.Controls.Add(a);
        address.SetBounds(20,49,904,34); address.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right; address.Font=new Font("Consolas",11); address.BackColor=Bg; address.ForeColor=Ink; address.BorderStyle=BorderStyle.FixedSingle; box.Controls.Add(address);
        var help=L("只填写自己的 arg1 收款地址，无需私钥或助记词。",10,Muted); help.SetBounds(20,90,890,24); box.Controls.Add(help);
        var m=L("运行模式",10,Muted); m.SetBounds(20,140,74,28); box.Controls.Add(m);
        mode.DropDownStyle=ComboBoxStyle.DropDownList; mode.ForeColor=Color.Black; mode.BackColor=Color.White; mode.DrawMode=DrawMode.OwnerDrawFixed; mode.ItemHeight=24;
        mode.DrawItem+=(o,e)=>{e.DrawBackground(); if(e.Index>=0)TextRenderer.DrawText(e.Graphics,mode.Items[e.Index].ToString(),mode.Font,e.Bounds,Color.Black,TextFormatFlags.VerticalCenter|TextFormatFlags.Left); e.DrawFocusRectangle();};
        mode.Items.AddRange(new object[]{"日常使用 · 留桌面余量","高性能 · 可能影响桌面响应"}); mode.SelectedIndex=0; mode.SetBounds(97,136,285,32); box.Controls.Add(mode);
        var g=L("显卡",10,Muted); g.SetBounds(400,140,52,28); box.Controls.Add(g);
        device.DropDownStyle=ComboBoxStyle.DropDownList; device.ForeColor=Color.Black; device.BackColor=Color.White; device.DropDownWidth=530;
        device.SetBounds(453,136,351,32); box.Controls.Add(device);
        ButtonStyle(refresh,"重新检测",Color.FromArgb(65,77,98),Ink); refresh.Font=new Font("Microsoft YaHei UI",9); refresh.SetBounds(816,135,108,32); box.Controls.Add(refresh);
        if(preview)device.Items.Add("NVIDIA 显卡 · 启动后自动检测");
        else device.Items.Add("等待检测 NVIDIA 显卡…"); device.SelectedIndex=0;
        state.Font=new Font("Microsoft YaHei UI",16,FontStyle.Bold); state.ForeColor=Accent; state.SetBounds(31,327,930,34); state.Text="准备就绪"; Controls.Add(state);
        detail.ForeColor=Muted; detail.SetBounds(32,368,925,36); detail.Text="填写地址后点击开始，软件会先同步本地主网节点。"; Controls.Add(detail);
        progress.SetBounds(32,414,936,6); progress.Style=ProgressBarStyle.Continuous; progress.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right; Controls.Add(progress);
        Metric(height,"本地高度",32); Metric(peers,"连接节点",266); Metric(rate,"平均算力",500); Metric(candidates,"本次提交候选",734);
        ButtonStyle(start,"开始 · 同步后自动挖矿",Accent,Color.FromArgb(8,37,31)); start.SetBounds(32,524,310,43); start.Click+=(o,e)=>{}; Controls.Add(start);
        ButtonStyle(stop,"停止 · 节点与挖矿",Color.FromArgb(65,77,98),Ink); stop.SetBounds(353,524,310,43); stop.Enabled=false; Controls.Add(stop);
        ButtonStyle(sync,"只同步节点",PanelColor,Ink); sync.SetBounds(674,524,293,43); Controls.Add(sync);
        var logLabel=L("运行日志",10,Muted); logLabel.SetBounds(32,584,115,24); Controls.Add(logLabel);
        var invite=new LinkLabel{Text="官方网页（邀请链接）",LinkColor=Accent,AutoSize=false}; invite.SetBounds(465,584,205,24); invite.Click+=(o,e)=>{Process.Start(new ProcessStartInfo(InvitationUrl){UseShellExecute=true});}; Controls.Add(invite);
        var folder=new LinkLabel{Text="打开数据和日志",LinkColor=Accent,AutoSize=false}; folder.SetBounds(680,584,148,24); folder.Click+=(o,e)=>{if(!preview)Process.Start(new ProcessStartInfo(engine.Data){UseShellExecute=true});}; Controls.Add(folder);
        var explorer=new LinkLabel{Text="查看链上收益",LinkColor=Accent}; explorer.SetBounds(834,584,133,24); explorer.Click+=(o,e)=>{Process.Start(new ProcessStartInfo("https://explorer.argonmine.com/"){UseShellExecute=true});}; Controls.Add(explorer);
        logs.SetBounds(32,616,935,106); logs.Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right; logs.ReadOnly=true; logs.BackColor=Color.FromArgb(10,16,25); logs.ForeColor=Muted; logs.Font=new Font("Consolas",9); logs.BorderStyle=BorderStyle.None; Controls.Add(logs);
        var foot=L("SOLO 找到主链有效块才有收益。候选不等于到账；奖励需经过 100 块成熟期。关闭软件会停止本软件的节点和矿工。",9,Muted); foot.SetBounds(32,737,938,30); foot.Anchor=AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right; Controls.Add(foot);
    }
    void Metric(Label label,string name,int x) {
        var p=new Panel{BackColor=PanelColor}; p.SetBounds(x,439,222,65); Controls.Add(p);
        var n=L(name,9,Muted); n.SetBounds(12,7,207,20); p.Controls.Add(n); label.Text="—"; label.ForeColor=Ink; label.Font=new Font("Consolas",17); label.SetBounds(12,29,207,30); p.Controls.Add(label);
    }
    void ButtonStyle(Button b,string text,Color bg,Color fg) { b.Text=text; b.BackColor=bg; b.ForeColor=fg; b.FlatStyle=FlatStyle.Flat; b.FlatAppearance.BorderSize=0; b.Cursor=Cursors.Hand; }
    void AppendLog(string line) {
        if(IsDisposed || !IsHandleCreated)return;
        if(InvokeRequired) { try { BeginInvoke(new Action<string>(AppendLog),line); } catch {} return; }
        if(logs.TextLength>70000) logs.Text=logs.Text.Substring(logs.TextLength-45000);
        logs.AppendText(line+Environment.NewLine); logs.SelectionStart=logs.TextLength; logs.ScrollToCaret();
    }
    void LoadPrefs() { try { if(!File.Exists(config))return; var p=new JavaScriptSerializer().Deserialize<Preferences>(File.ReadAllText(config)); address.Text=p.Address??""; preferredUUID=p.GpuUUID??""; mode.SelectedIndex=p.Fast?1:0; } catch { AppendLog("设置文件无法读取，请重新填写地址。"); } }
    async Task DetectCards() {
        if(preview||detecting||starting||stopping||intent||Engine.Alive(engine.Miner))return;
        detecting=true; ControlsState(); detail.Text="正在读取 NVIDIA 显卡型号与显存，不运行计算任务。";
        try {
            var found=await Task.Run(()=>engine.Detect()); if(exiting||IsDisposed)return;
            device.Items.Clear(); foreach(var g in found.Devices)device.Items.Add(g);
            GpuInfo best=found.Devices.FirstOrDefault(g=>g.Supported && g.UUID==preferredUUID)??found.Devices.Where(g=>g.Supported).OrderByDescending(g=>g.MemoryBytes).FirstOrDefault();
            if(best!=null) {device.SelectedItem=best; detail.Text="检测到 "+found.Devices.Length+" 张 NVIDIA 显卡，已选择 "+best.Name+"。开挖前将校验计算结果。";}
            else {if(device.Items.Count==0)device.Items.Add("未检测到可用 NVIDIA 显卡");device.SelectedIndex=0;detail.Text="没有本版本支持的显卡。需要 CUDA 计算能力7.5以上、至少2GB显存及兼容驱动。";}
        } catch(Exception ex) { if(!exiting) {device.Items.Clear();device.Items.Add("检测失败 · 可点击重新检测");device.SelectedIndex=0;detail.Text=ex.Message;AppendLog(ex.Message);} }
        finally {detecting=false;if(!IsDisposed)ControlsState();}
    }
    public async Task RunStopRaceTest() {
        address.Text="arg1qt8m4tkg5a2yvxlqumdvvuqza8nekrfzk8dhu6cnh5af9cy55atsq7zefrm";
        device.Items.Clear(); device.Items.Add(new GpuInfo{Index=0,Name="TEST ONLY",UUID=new string('0',32),MemoryBytes=8L*1024*1024*1024,Cc=86,Supported=true});device.SelectedIndex=0;
        Task pending=BeginSession(true);
        await StopSession(true);
        await pending;
        if(intent || Engine.Alive(engine.Node) || Engine.Alive(engine.Miner))throw new Exception("Stop allowed delayed start.");
    }
    void ControlsState() { bool running=intent||Engine.Alive(engine.Miner); var gpu=device.SelectedItem as GpuInfo; start.Enabled=!running&&!starting&&!stopping&&!detecting&&gpu!=null&&gpu.Supported; sync.Enabled=!running&&!starting&&!stopping&&!detecting; stop.Enabled=running||starting||Engine.Alive(engine.Node); address.Enabled=!running&&!starting; mode.Enabled=address.Enabled; device.Enabled=address.Enabled&&!detecting; refresh.Enabled=device.Enabled; }
    async Task BeginSession(bool mine) {
        if(preview || detecting || starting || stopping || intent || Engine.Alive(engine.Miner))return;
        starting=true; int session=++generation; ControlsState();
        try {
            var gpu=device.SelectedItem as GpuInfo;
            if(mine && (gpu==null||!gpu.Supported))throw new Exception("请先检测并选择受支持的 NVIDIA 显卡。");
            var p=new Preferences{Address=address.Text.Trim(),Device=gpu==null?0:gpu.Index,GpuUUID=gpu==null?"":gpu.UUID,MemoryBytes=gpu==null?0:gpu.MemoryBytes,Fast=mode.SelectedIndex==1};
            await Task.Run(()=>{engine.Verify(); if(mine)engine.ValidateAddress(p.Address);});
            if(session!=generation || exiting)return;
            if(engine.Miner!=null && !Engine.Alive(engine.Miner))await Task.Run(()=>engine.StopMiner());
            if(session!=generation || exiting)return;
            File.WriteAllText(config,new JavaScriptSerializer().Serialize(p),Encoding.UTF8); active=p;
            engine.StartNode(); intent=mine; readyCount=0;
            state.Text=mine?"正在同步 · 完成后自动挖矿":"只同步节点"; detail.Text="首次同步需要下载区块。你的区块数据会保存在本机，下次可继续同步。";
        } catch(Exception ex) { intent=false; state.Text="无法启动"; detail.Text=ex.Message; AppendLog(ex.Message); }
        finally { starting=false; ControlsState(); }
    }
    async Task StopSession(bool all) {
        ++generation; intent=false; readyCount=0; if(stopping)return; stopping=true; ControlsState();
        state.Text="正在停止"; detail.Text="取消等待中的自动开挖，等待当前批次结束。";
        try { await Task.Run(()=>engine.StopMiner()); if(all) await Task.Run(()=>engine.StopNode()); state.Text=all?"已停止":"挖矿已停止"; detail.Text=all?"节点和矿工均已停止。":"不会自动重新开挖；本地节点可继续同步。"; rate.Text="—"; }
        catch(Exception ex) { detail.Text=ex.Message; }
        finally { stopping=false; ControlsState(); }
    }
    async Task Poll() {
        if(pollBusy || starting || stopping || exiting || !Engine.Alive(engine.Node)) {
            if(!pollBusy&&!starting&&!stopping&&!exiting&&engine.Node!=null&&!Engine.Alive(engine.Node)) {
                intent=false; ++generation; state.Text="节点已退出"; detail.Text="请检查日志。软件不会重启异常退出的进程。"; if(Engine.Alive(engine.Miner))await StopSession(false); ControlsState();
            }
            return;
        }
        pollBusy=true; int session=generation; string chainFault=null;
        try {
            var s=await Task.Run(()=>engine.ReadSnapshot());
            if(session!=generation || stopping || exiting)return;
            lastRpcError="";
            height.Text=s.Height.ToString("N0"); peers.Text=s.Peers.ToString();
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds(); bool ready=s.Ready(now);
            progress.Style=ready?ProgressBarStyle.Continuous:ProgressBarStyle.Marquee; progress.Value=ready?100:0;
            if(intent && !Engine.Alive(engine.Miner)) {
                if(engine.Miner!=null) { intent=false; ++generation; await Task.Run(()=>engine.StopMiner()); state.Text="矿工已退出"; detail.Text="请查看日志；异常停止不会自动重启。"; ControlsState(); return; }
                readyCount=ready?readyCount+1:0;
                state.Text=ready?"主网已同步 · 准备自检":"正在同步 · 完成后自动挖矿";
                detail.Text=s.Peers==0?"正在寻找主网节点，暂不启动挖矿。":("当前高度 "+s.Height+" · 对端参考高度 "+s.PeerHeight+" · "+(ready?"等待连续两次同步检查通过":"等待下载追上主网并确认区块新鲜度"));
                if(readyCount>=2 && session==generation && intent) {
                    minerStart=DateTime.UtcNow;
                    try { engine.StartMiner(active); state.Text="GPU 自检 / 启动中"; detail.Text="一致性检查通过后开始挖矿，日志会显示结果。"; }
                    catch(Exception ex) { intent=false; ++generation; state.Text="挖矿未启动"; detail.Text=ex.Message; AppendLog(ex.Message); }
                }
            } else if(Engine.Alive(engine.Miner)) {
                var m=engine.MiningStatus(minerStart);
                if(m!=null) { rate.Text=(Convert.ToDouble(m["averageHps"])/1000).ToString("N1")+" kH/s"; candidates.Text=Convert.ToString(m["acceptedBlocks"]); state.Text="正在挖矿"; detail.Text="本机节点供给任务 · "+(active.Fast?"高性能模式":"日常模式")+" · 提交候选需进一步确认主链有效性"; }
                if(!ready)detail.Text="节点连接或同步状态发生变化，请留意日志；算力不代表有效主网收益。";
            } else if(!intent) { state.Text="节点同步中 / 挖矿未启动"; detail.Text=ready?"主网已同步。点击开始才会挖矿。":"当前高度 "+s.Height+" · 对端参考高度 "+s.PeerHeight+"。未设置自动开挖。"; }
            ControlsState();
        } catch(Exception ex) { readyCount=0; if(session==generation&&!stopping&&!exiting) { detail.Text="读取节点状态失败："+ex.Message; string error=ex.GetType().Name+": "+ex.Message;if(error!=lastRpcError){AppendLog("节点状态读取失败："+error);lastRpcError=error;} if(ex.Message.Contains("创世块")) { intent=false; ++generation; chainFault=ex.Message; } } }
        finally { pollBusy=false; }
 if(chainFault!=null) { await StopSession(false); state.Text="主网验证失败"; detail.Text=chainFault; }
    }
    protected override void Dispose(bool disposing) { if(disposing) { timer.Stop(); timer.Dispose(); engine.Dispose(); } base.Dispose(disposing); }
}

static class Tests {
    static void Assert(bool b,string message) { if(!b)throw new Exception(message); }
    public static int LiveRpc() {
        var root=AppDomain.CurrentDomain.BaseDirectory;
        try {
            var e=new Engine(root,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ArgonmineDesktop"));
            var s=e.ReadSnapshot();
            File.WriteAllText(Path.Combine(root,"RPC-SELFTEST-PASSED.json"),new JavaScriptSerializer().Serialize(new {height=s.Height,peers=s.Peers,peerHeight=s.PeerHeight,syncing=s.Syncing,ready=s.Ready(DateTimeOffset.UtcNow.ToUnixTimeSeconds())}));return 0;
        } catch(Exception ex){File.WriteAllText(Path.Combine(root,"RPC-SELFTEST-FAILED.txt"),ex.ToString());return 1;}
    }
    public static int Run() {
        try {
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var s=new Snapshot{Genesis=Engine.Genesis,Height=100,PeerHeight=100,TipTime=now,Peers=1,Syncing=false};
            Assert(s.Ready(now),"fresh synced chain should be ready"); s.Peers=0; Assert(!s.Ready(now),"no peers must not start"); s.Peers=1; s.Syncing=true; Assert(!s.Ready(now),"IBD must not start"); s.Syncing=false; s.PeerHeight=101; Assert(!s.Ready(now),"behind peer must not start"); s.PeerHeight=100; s.TipTime=now-1801; Assert(!s.Ready(now),"stale chain must not start"); s.TipTime=now; s.Genesis="wrong"; Assert(!s.Ready(now),"wrong genesis must not start");
            Assert(Engine.Q("C:\\a b\\c")=="\"C:\\a b\\c\"","path quoting");
            Assert(Engine.Q("a\"b")=="\"a\\\"b\"","quote escaping");
            Assert(Engine.PlanBatch(4L*1024*1024*1024,false)==128,"4GB daily allocation");
            Assert(Engine.PlanBatch(8L*1024*1024*1024,false)==256,"8GB daily allocation");
            Assert(Engine.PlanBatch(8L*1024*1024*1024,true)==512,"8GB fast allocation");
            Assert(Engine.PlanBatch(32L*1024*1024*1024,true)==2048,"32GB fast allocation");
            var rpcJson=new JavaScriptSerializer();
            var chain=new Dictionary<string,object>{{"genesis",Engine.Genesis},{"blocks",2080},{"initialblockdownload",false},{"time",now}};
            var response=rpcJson.Deserialize<Dictionary<string,object>>("{\"result\":[{\"startheight\":2079},{\"startheight\":2080}]}");
            Assert(response["result"] is System.Collections.ArrayList,"regression must exercise actual nested JSON array representation");
            var parsed=Engine.ParseSnapshot(chain,response["result"]);
            Assert(parsed.Peers==2 && parsed.PeerHeight==2080 && parsed.Ready(now),"synced RPC ArrayList must allow mining");
            Assert(!Engine.ParseSnapshot(chain,new object[0]).Ready(now),"empty peer array must block mining");
            bool invalidPeers=false;try{Engine.ParseSnapshot(chain,"not an array");}catch(FormatException){invalidPeers=true;}Assert(invalidPeers,"malformed peer list accepted");
            var root=AppDomain.CurrentDomain.BaseDirectory; var e=new Engine(root,Path.Combine(Path.GetTempPath(),"ArgonmineGuiTests-"+Guid.NewGuid().ToString("N")));
            e.Verify(); e.ValidateAddress("arg1qt8m4tkg5a2yvxlqumdvvuqza8nekrfzk8dhu6cnh5af9cy55atsq7zefrm");
            bool bad=false; try {e.ValidateAddress("arg1invalid");}catch{bad=true;} Assert(bad,"bad address accepted");
            using(var job=new ChildJob()) {
                using(var child=Process.Start(new ProcessStartInfo("cmd.exe","/d /c ping -n 30 127.0.0.1") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})) {
                    job.Add(child); job.Dispose(); Assert(child.WaitForExit(5000),"owned process survived job close");
                }
            }
            File.WriteAllText(Path.Combine(root,"SELFTEST-PASSED.txt"),"Sync gates, quoting, manifest, address validation and owned-process Job cleanup passed. No GPU or mainnet node started.\r\n"); return 0;
        } catch(Exception ex) { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"SELFTEST-FAILED.txt"),ex.ToString()); return 1; }
    }
}


