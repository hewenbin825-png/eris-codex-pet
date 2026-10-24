using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Web.Script.Serialization;

// Only initialize + account/rateLimits/read over the installed Codex CLI's stdio.
// No browser credentials, auth.json parsing, inference, reset consumption or port.
internal sealed class UsageWindow {
    public string bucket {get;set;}
    public double? remaining {get;set;}
    public int? minutes {get;set;}
    public long? reset_at {get;set;}
    [ScriptIgnore] public string Period {
        get {
            if(!minutes.HasValue || minutes.Value<=0)return "额度周期未提供";
            int n=minutes.Value;
            if(n%1440==0)return (n/1440)+" 天额度";
            if(n%60==0)return (n/60)+" 小时额度";
            return n+" 分钟额度";
        }
    }
    public string ResetText(DateTime utc) {
        if(!reset_at.HasValue)return "重置时间未提供";
        DateTime when;
        try{when=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(reset_at.Value);}
        catch(ArgumentOutOfRangeException){return "重置时间未提供";}
        if(when<=utc)return "重置时间已到，等待刷新";
        var left=when-utc;
        string interval=left.TotalDays>=1 ? ((int)left.TotalDays)+" 天 "+left.Hours+" 小时" :
            left.TotalHours>=1 ? ((int)left.TotalHours)+" 小时 "+left.Minutes+" 分钟" :
            Math.Max(1,(int)Math.Ceiling(left.TotalMinutes))+" 分钟";
        return "约 "+interval+" 后重置\n"+when.ToLocalTime().ToString("MM-dd HH:mm")+"（本地时间）";
    }
}

internal sealed class UsageSnapshot {
    public string fetched_utc {get;set;}
    public string plan {get;set;}
    public string credits {get;set;}
    public int? reset_credits {get;set;}
    public List<UsageWindow> windows {get;set;}
    [ScriptIgnore] public DateTime Fetched {
        get {DateTime t;return DateTime.TryParse(fetched_utc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out t)?t.ToUniversalTime():DateTime.MinValue;}
    }
    public bool IsStale(DateTime utc) {return Fetched==DateTime.MinValue || utc-Fetched>TimeSpan.FromMinutes(5) || Fetched>utc.AddMinutes(1);}
}

internal static class UsageData {
    internal static Dictionary<string,object> Obj(object value){return value as Dictionary<string,object>;}
    internal static object Get(Dictionary<string,object> obj,string name){object value;return obj!=null&&obj.TryGetValue(name,out value)?value:null;}
    internal static string Text(object value,int max=60) {
        string s=value as string;if(s==null)return "";
        s=new string(s.Where(c=>!char.IsControl(c)).ToArray());
        return s.Length>max?s.Substring(0,max):s;
    }
    internal static double? Number(object value) {
        if(value==null || value is bool)return null;
        double n;if(!Double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out n) || Double.IsNaN(n) || Double.IsInfinity(n))return null;
        return n;
    }
    internal static UsageSnapshot Parse(Dictionary<string,object> result,DateTime utc) {
        if(result==null)throw new InvalidDataException("额度响应为空");
        var snapshot=new UsageSnapshot{fetched_utc=utc.ToString("o"),windows=new List<UsageWindow>()};
        var many=Obj(Get(result,"rateLimitsByLimitId"));
        var buckets=new List<Dictionary<string,object>>();
        if(many!=null && many.Count>0) {
            foreach(var entry in many.OrderBy(p=>p.Key=="codex"?0:1).ThenBy(p=>p.Key).Take(30)){
                var bucket=Obj(entry.Value);if(bucket!=null)buckets.Add(bucket);
            }
        } else {
            var one=Obj(Get(result,"rateLimits"));if(one!=null)buckets.Add(one);
        }
        if(buckets.Count==0)throw new InvalidDataException("当前账户未提供额度信息");
        foreach(var bucket in buckets){
            string label=Text(Get(bucket,"limitName"));
            if(label=="")label=Text(Get(bucket,"limitId"));
            if(label=="" || label=="codex")label="Codex";
            if(String.IsNullOrEmpty(snapshot.plan))snapshot.plan=Text(Get(bucket,"planType"),32);
            if(snapshot.credits==null){
                var c=Obj(Get(bucket,"credits"));
                if(c!=null){
                    if(Object.Equals(Get(c,"unlimited"),true))snapshot.credits="不限量";
                    else if(Object.Equals(Get(c,"hasCredits"),false))snapshot.credits="无";
                    else {string balance=Text(Get(c,"balance"),24);snapshot.credits=balance==""?"未提供":balance;}
                }
            }
            foreach(string slot in new[]{"primary","secondary"}){
                var w=Obj(Get(bucket,slot));if(w==null)continue;
                double? used=Number(Get(w,"usedPercent")),mins=Number(Get(w,"windowDurationMins")),reset=Number(Get(w,"resetsAt"));
                snapshot.windows.Add(new UsageWindow{
                    bucket=label,remaining=used.HasValue?(double?)Math.Max(0,Math.Min(100,100-used.Value)):null,
                    minutes=mins.HasValue&&mins>0&&mins<=Int32.MaxValue?(int?)mins.Value:null,
                    reset_at=reset.HasValue&&reset>=0&&reset<=253402300799L?(long?)reset.Value:null
                });
            }
        }
        var resetCredits=Obj(Get(result,"rateLimitResetCredits"));
        double? count=Number(Get(resetCredits,"availableCount"));
        if(count.HasValue&&count>=0&&count<=Int32.MaxValue)snapshot.reset_credits=(int)count.Value;
        return snapshot;
    }
    internal static void Save(string path,UsageSnapshot value) {
        // Serialize an allowlist of DTO fields; never persist raw RPC responses.
        string tmp=path+".tmp";
        File.WriteAllText(tmp,new JavaScriptSerializer().Serialize(value),new UTF8Encoding(false));
        if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);
    }
    internal static UsageSnapshot Load(string path) {
        try{
            if(!File.Exists(path)||new FileInfo(path).Length>65536)return null;
            var s=new JavaScriptSerializer().Deserialize<UsageSnapshot>(File.ReadAllText(path,Encoding.UTF8));
            if(s==null||s.windows==null||s.windows.Count>60||s.Fetched==DateTime.MinValue)return null;
            foreach(var w in s.windows){
                if(w==null||w.remaining.HasValue&&(Double.IsNaN(w.remaining.Value)||Double.IsInfinity(w.remaining.Value)||w.remaining<0||w.remaining>100))return null;
            }
            return s;
        }catch{return null;}
    }
}

internal sealed class UsageClient:IDisposable {
    readonly object gate=new object();
    Process active;
    bool disposed;
    internal static string FindCli() {
        // Prefer the official desktop app's CLI installation; no downloads.
        string bins=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenAI","Codex","bin");
        if(Directory.Exists(bins)){
            try{
                var candidates=Directory.GetDirectories(bins).Select(d=>Path.Combine(d,"codex.exe")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc);
                string found=candidates.FirstOrDefault();if(found!=null)return found;
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
        foreach(string folder in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)){
            try{string path=Path.Combine(folder.Trim('"'),"codex.exe");if(File.Exists(path))return Path.GetFullPath(path);}catch{}
        }
        throw new FileNotFoundException("未找到 Codex CLI；请先打开或安装 Codex 桌面版");
    }
    internal UsageSnapshot Read() {
        string executable=FindCli();
        var start=new ProcessStartInfo(executable,"app-server --listen stdio://"){
            UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,
            WorkingDirectory=ErisVoice.Root
        };
        // Installed copies use the same home as their notification integration.
        try{
            string integration=Path.Combine(ErisVoice.Root,"integration.json");
            if(File.Exists(integration)){
                var info=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(integration,Encoding.UTF8));
                string config=UsageData.Text(UsageData.Get(info,"config_path"),32767);
                if(Path.IsPathRooted(config)&&Path.GetFileName(config)=="config.toml")
                    start.EnvironmentVariables["CODEX_HOME"]=Path.GetDirectoryName(config);
            }
        }catch{}
        var queue=new ConcurrentQueue<Dictionary<string,object>>();
        using(var ready=new AutoResetEvent(false))
        using(var process=new Process{StartInfo=start}){
            bool ended=false;
            DataReceivedEventHandler receive=delegate(object sender,DataReceivedEventArgs e){
                if(e.Data==null){ended=true;try{ready.Set();}catch(ObjectDisposedException){}return;}
                if(e.Data.Length>262144)return;
                try{
                    var message=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(e.Data);
                    // Ignore all notifications and any unexpected message payloads.
                    if(message!=null&&message.ContainsKey("id")){queue.Enqueue(message);ready.Set();}
                }catch{}
            };
            process.OutputDataReceived+=receive;
            process.ErrorDataReceived+=delegate{}; // Drain, but don't retain raw stderr.
            try{
                lock(gate){if(disposed)throw new OperationCanceledException();process.Start();active=process;}
                process.BeginOutputReadLine();process.BeginErrorReadLine();
                Send(process,new{id=1,method="initialize",@params=new{clientInfo=new{name="eris_pet_usage",title="Eris Pet Usage",version="2.2.0"}}});
                Wait(queue,ready,1,()=>ended,10000);
                Send(process,new{method="initialized",@params=new{}});
                Send(process,new{id=2,method="account/rateLimits/read",@params=new{}});
                return UsageData.Parse(Wait(queue,ready,2,()=>ended,18000),DateTime.UtcNow);
            }finally{
                try{process.StandardInput.Close();}catch{}
                try{if(!process.WaitForExit(1200)){process.Kill();process.WaitForExit(1200);}}catch{}
                lock(gate){if(active==process)active=null;}
                process.OutputDataReceived-=receive;
            }
        }
    }
    static void Send(Process p,object request){
        p.StandardInput.WriteLine(new JavaScriptSerializer().Serialize(request));p.StandardInput.Flush();
    }
    static Dictionary<string,object> Wait(ConcurrentQueue<Dictionary<string,object>> queue,AutoResetEvent ready,int id,Func<bool> ended,int timeout){
        var clock=Stopwatch.StartNew();
        while(clock.ElapsedMilliseconds<timeout){
            Dictionary<string,object> message;
            while(queue.TryDequeue(out message)){
                if(UsageData.Number(UsageData.Get(message,"id"))!=id)continue;
                if(message.ContainsKey("error"))throw new InvalidOperationException("额度读取失败，请在 Codex 中确认登录后重试");
                var result=UsageData.Obj(UsageData.Get(message,"result"));
                if(result==null)throw new InvalidDataException("Codex 返回了无法识别的额度数据");
                return result;
            }
            if(ended())throw new IOException("Codex 额度连接已结束，请稍后重试");
            ready.WaitOne(Math.Max(1,Math.Min(250,timeout-(int)clock.ElapsedMilliseconds)));
        }
        throw new TimeoutException("额度读取超时；请检查网络后重试");
    }
    public void Dispose(){
        lock(gate){disposed=true;if(active!=null){try{if(!active.HasExited)active.Kill();}catch{}}}
    }
}
