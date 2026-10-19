using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Collections.Generic;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// A local startup companion. Only reads process identities; no input injection,
// window manipulation, hooks, network access, or Codex application patching.
internal static class ErisCodexStartup {
    static readonly string Root=AppDomain.CurrentDomain.BaseDirectory;
    static readonly string Runtime=Path.Combine(Root,"runtime");
    static readonly string Prefix="Local\\ErisCodexStartup_"+WindowsIdentity.GetCurrent().User.Value;
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=16777216};
    static HashSet<int> previous=new HashSet<int>();
    static string lastStatus="";
    static bool petFollowed;
    static DateTime absentSince=DateTime.MinValue;

    static int Main(string[] args) {
        Directory.CreateDirectory(Runtime);
        if(args.Length==1 && args[0]=="--stop"){
            try{using(var e=EventWaitHandle.OpenExisting(Prefix+"Stop"))e.Set();}catch(WaitHandleCannotBeOpenedException){}
            return 0;
        }
        if(args.Length==1 && args[0]=="--self-test")return SelfTest();
        if(args.Length>0)return 2;
        bool first;
        using(var mutex=new Mutex(true,Prefix+"Instance",out first)){
            if(!first)return 0;
            using(var stopped=new EventWaitHandle(false,EventResetMode.AutoReset,Prefix+"Stop")){
                try{
                    do {
                        try{Tick();}
                        catch(Exception ex){Status("retrying",previous,ex.GetType().Name+": "+ex.Message);}
                    }while(!stopped.WaitOne(2000));
                    Status("stopped",previous,null);
                    return 0;
                }catch(Exception ex){Status("error",previous,ex.GetType().Name+": "+ex.Message);return 1;}
            }
        }
    }
    internal static bool IsDesktopImage(string path){
        return path!=null && Regex.IsMatch(path,@"\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\(?:ChatGPT|Codex)\.exe$",RegexOptions.IgnoreCase);
    }
    static HashSet<int> DesktopProcesses(){
        var found=new HashSet<int>();int session=Process.GetCurrentProcess().SessionId;
        foreach(string name in new[]{"ChatGPT","Codex"})
        foreach(var process in Process.GetProcessesByName(name))using(process){
            try{if(process.SessionId==session && IsDesktopImage(process.MainModule.FileName))found.Add(process.Id);}
            catch(System.ComponentModel.Win32Exception){}
            catch(InvalidOperationException){}
        }
        return found;
    }
    internal static bool NewSession(HashSet<int> oldIds,HashSet<int> ids){return ids.Count>0 && !ids.Overlaps(oldIds);}
    static void Helper(string args){
        var info=new ProcessStartInfo(Path.Combine(Root,"ErisVoice.exe"),args){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        using(var p=Process.Start(info)){if(args=="--stop" && p!=null)p.WaitForExit(5000);}
    }
    static void Tick(){
        var ids=DesktopProcesses();
        if(ids.Count>0){
            absentSince=DateTime.MinValue;
            if(NewSession(previous,ids)){Helper("");petFollowed=true;Event("codex_started_pet_launched",null);}
        }else{
            if(absentSince==DateTime.MinValue)absentSince=DateTime.UtcNow;
            if(petFollowed && (DateTime.UtcNow-absentSince).TotalSeconds>=4){Helper("--stop");petFollowed=false;Event("codex_exited_pet_stopped",null);}
        }
        previous=ids;
        Status(ids.Count>0?"following_codex":"waiting_for_codex",ids,null);
    }
    static void Status(string state,HashSet<int> ids,string error){
        var value=new{state=state,version="1.0",pid=Process.GetCurrentProcess().Id,codex_running=ids.Count>0,
            codex_process_ids=ids.OrderBy(x=>x).ToArray(),error=error};
        string text=Json.Serialize(value);if(text==lastStatus)return;
        File.WriteAllText(Path.Combine(Runtime,"autostart-status.json"),text,new UTF8Encoding(false));lastStatus=text;
    }
    static void Event(string kind,string error){
        string file=Path.Combine(Runtime,"autostart-events.jsonl");
        if(File.Exists(file)&&new FileInfo(file).Length>131072)File.WriteAllText(file,"",new UTF8Encoding(false));
        File.AppendAllText(file,Json.Serialize(new{timestamp=DateTime.UtcNow.ToString("o"),kind=kind,error=error})+Environment.NewLine,new UTF8Encoding(false));
    }
    static int SelfTest(){try{
        if(!IsDesktopImage(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe"))throw new Exception("Desktop identification");
        if(IsDesktopImage(@"C:\Users\ExampleUser\AppData\Local\OpenAI\Codex\bin\abc\codex.exe"))throw new Exception("CLI incorrectly identified");
        if(IsDesktopImage(@"C:\Program Files\WindowsApps\OpenAI.ChatGPT-Desktop_1\app\ChatGPT.exe"))throw new Exception("Other app incorrectly identified");
        var empty=new HashSet<int>();var first=new HashSet<int>{10,11};var childChanged=new HashSet<int>{10,12};var restarted=new HashSet<int>{20,21};
        if(NewSession(empty,empty)||!NewSession(empty,first)||NewSession(first,childChanged)||!NewSession(first,restarted))throw new Exception("Session transition");
        if(NewSession(first,empty))throw new Exception("No app should never launch pet");
        File.WriteAllText(Path.Combine(Runtime,"autostart-self-test.json"),Json.Serialize(new{passed=true,checks=8,tests_use_fixtures=true,live_codex_not_restarted=true}),new UTF8Encoding(false));return 0;
    }catch(Exception ex){File.WriteAllText(Path.Combine(Runtime,"autostart-self-test.json"),Json.Serialize(new{passed=false,error=ex.Message}),new UTF8Encoding(false));return 1;}}
}
