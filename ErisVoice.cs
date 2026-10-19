using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Media;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Security.Principal;

// Local companion: owns its transparent window. No global mouse hook, input
// injection, screen capture, networking, or changes to the Codex application.
public sealed class Motion {
    public string label, action;
    public int duration_ms;
    public int[][] frames;
    public int[] FrameAt(double elapsed) {
        int edge=0;
        foreach(var f in frames){edge+=f[2];if(elapsed<edge)return f;}
        return frames[frames.Length-1];
    }
}

internal static class ErisVoice {
    internal static readonly string Root=AppDomain.CurrentDomain.BaseDirectory;
    static readonly string Runtime=Path.Combine(Root,"runtime");
    static readonly string Prefix="Local\\ErisCodexVoice_"+WindowsIdentity.GetCurrent().User.Value;
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static readonly Random Random=new Random();
    static Dictionary<string,object> settings;
    internal static Dictionary<string,Motion> motions;
    internal static Bitmap atlas;
    static string[] clicks,completions;
    static string lastClick="",lastCompletion="",activeClip="";
    static bool busy,pendingCompletion,muted,closing;
    static double volume=.65;
    static int clickCount,completionCount,playId;
    static NotifyIcon tray;
    static PetForm pet;
    static Control dispatcher;
    static EventWaitHandle completed,clicked,stopped,nyan;
    static SoundPlayer currentPlayer;
    internal static ContextMenuStrip Menu;

    [STAThread] static int Main(string[] args) {
        Directory.CreateDirectory(Runtime);
        if(args.Length>0 && args[0]=="--self-test")return SelfTest();
        if(args.Length>0){
            string name=args[0]=="--completion"?"Completion":args[0]=="--click"?"Click":args[0]=="--nyan"?"Nyan":args[0]=="--stop"?"Stop":null;
            if(name==null)return 2;
            if(!Signal(name)&&name!="Stop"){
                Process.Start(new ProcessStartInfo(Application.ExecutablePath){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
                for(int i=0;i<50;i++){Thread.Sleep(100);if(Signal(name))return 0;}return 3;
            }return 0;
        }
        bool first;
        using(var mutex=new Mutex(true,Prefix+"Instance",out first)){
            if(!first)return 0;
            bool failed=false;
            try{
                try{SetProcessDpiAwarenessContext(new IntPtr(-4));}catch{}
                Application.EnableVisualStyles();
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                dispatcher=new Control();var handle=dispatcher.Handle;
                Load();
                completed=new EventWaitHandle(false,EventResetMode.AutoReset,Prefix+"Completion");
                clicked=new EventWaitHandle(false,EventResetMode.AutoReset,Prefix+"Click");
                stopped=new EventWaitHandle(false,EventResetMode.AutoReset,Prefix+"Stop");
                nyan=new EventWaitHandle(false,EventResetMode.AutoReset,Prefix+"Nyan");
                Menu=BuildMenu();
                pet=new PetForm(atlas);pet.ContextMenuStrip=Menu;
                int size=GetInt("size",160);
                pet.SetSize(size);
                Point initial=new Point(GetInt("x",Screen.PrimaryScreen.WorkingArea.Right-size-220),GetInt("y",Screen.PrimaryScreen.WorkingArea.Bottom-pet.Height-12));
                pet.Location=pet.Clamp(initial);pet.Show();
                tray=new NotifyIcon{Icon=SystemIcons.Information,Text="艾莉丝 · 动作与语音",ContextMenuStrip=Menu,Visible=true};
                tray.DoubleClick+=delegate{pet.Show();Play("click",true,null,"tray");};
                ThreadPool.QueueUserWorkItem(delegate{
                    try{
                        var waits=new WaitHandle[]{completed,clicked,stopped,nyan};
                        while(!closing){int n=WaitHandle.WaitAny(waits);if(closing)break;
                            dispatcher.BeginInvoke((Action)(()=>{if(n==2)Application.Exit();else Play(n==0?"completion":"click",n!=0,n==3?"click_magic_nyan.wav":null,"signal");}));
                            if(n==2)break;
                        }
                    }catch(ObjectDisposedException){}
                });
                Status("running",null);Application.Run();
            }catch(Exception ex){failed=true;Status("error",ex.GetType().Name+": "+ex.Message);return 1;}
            finally{
                closing=true;if(currentPlayer!=null)currentPlayer.Stop();
                if(pet!=null){SavePosition();pet.Dispose();}
                if(tray!=null)tray.Dispose();if(Menu!=null)Menu.Dispose();
                if(atlas!=null)atlas.Dispose();if(!failed)Status("stopped",null);
            }
        }return 0;
    }
    static bool Signal(string name){try{using(var e=EventWaitHandle.OpenExisting(Prefix+name)){e.Set();return true;}}catch(WaitHandleCannotBeOpenedException){return false;}}
    static int GetInt(string key,int fallback){return settings!=null&&settings.ContainsKey(key)?Convert.ToInt32(settings[key]):fallback;}
    static void Load(){
        settings=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Root,"settings.json"),Encoding.UTF8));
        muted=settings.ContainsKey("muted")&&Convert.ToBoolean(settings["muted"]);
        volume=Math.Max(0,Math.Min(1,Convert.ToDouble(settings["volume"])));
        clicks=((System.Collections.IEnumerable)settings["click"]).Cast<object>().Select(Convert.ToString).ToArray();
        completions=((System.Collections.IEnumerable)settings["completion"]).Cast<object>().Select(Convert.ToString).ToArray();
        if(clicks.Length==0||completions.Length==0)throw new InvalidDataException("Empty clip group");
        motions=Json.Deserialize<Dictionary<string,Motion>>(File.ReadAllText(Path.Combine(Root,"motions.json"),Encoding.UTF8));
        atlas=new Bitmap(Path.Combine(Root,"assets","spritesheet.png"));
        if(atlas.Width!=1536||atlas.Height!=1872)throw new InvalidDataException("Unexpected sprite size");
        foreach(var f in clicks.Concat(completions)){
            if(Path.GetFileName(f)!=f||!f.EndsWith(".wav",StringComparison.OrdinalIgnoreCase)||!motions.ContainsKey(f))throw new InvalidDataException("Invalid clip");
            var m=motions[f];if(m.frames==null||m.frames.Length==0)throw new InvalidDataException("Empty motion");
            int sum=0;int[] counts={6,8,8,4,5,8,6,6,6};
            foreach(var a in m.frames){if(a.Length!=3||a[0]<0||a[0]>=9||a[1]<0||a[1]>=counts[a[0]]||a[2]<20)throw new InvalidDataException("Invalid frame");sum+=a[2];}
            if(sum!=m.duration_ms)throw new InvalidDataException("Motion duration mismatch");
            var audio=ReadWav(File.ReadAllBytes(Path.Combine(Root,"audio",f)),1);
            if(Math.Abs(audio.Milliseconds-m.duration_ms)>2)throw new InvalidDataException("Audio and animation duration mismatch");
        }
    }
    static ContextMenuStrip BuildMenu(){
        var menu=new ContextMenuStrip();
        var preview=new ToolStripMenuItem("台词与动作");
        foreach(string f in clicks.Concat(completions)){string clip=f;preview.DropDownItems.Add(motions[f].label,null,delegate{pet.Show();Play(clicks.Contains(clip)?"click":"completion",true,clip,"menu");});}
        menu.Items.Add(preview);
        var mute=new ToolStripMenuItem("静音（动作继续）"){CheckOnClick=true,Checked=muted};
        mute.CheckedChanged+=delegate{muted=mute.Checked;Save();};menu.Items.Add(mute);
        var vol=new ToolStripMenuItem("音量");foreach(double l in new double[]{.35,.65,1}){double v=l;vol.DropDownItems.Add(((int)(100*v))+"%",null,delegate{volume=v;Save();});}menu.Items.Add(vol);
        var sizes=new ToolStripMenuItem("大小");foreach(int s in new int[]{112,160,208}){int value=s;sizes.DropDownItems.Add(s+" 像素",null,delegate{pet.SetSize(value);SavePosition();});}menu.Items.Add(sizes);
        menu.Items.Add("放回屏幕右下角",null,delegate{var b=Screen.FromPoint(Cursor.Position).WorkingArea;pet.Location=pet.Clamp(new Point(b.Right-pet.Width-28,b.Bottom-pet.Height-12));pet.Show();SavePosition();});
        menu.Items.Add("显示桌宠",null,delegate{pet.Show();Status("running",null);});
        menu.Items.Add("隐藏桌宠",null,delegate{pet.Hide();Status("running",null);});
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出动作助手",null,delegate{Application.Exit();});
        return menu;
    }
    static void Save(){
        try{settings["muted"]=muted;settings["volume"]=volume;
            File.WriteAllText(Path.Combine(Root,"settings.json"),Json.Serialize(settings),new UTF8Encoding(false));if(!closing)Status("running",null);
        }catch(Exception ex){Log("settings_error",ex.GetType().Name,"",0);}
    }
    internal static void SavePosition(){if(pet==null||pet.IsDisposed)return;settings["x"]=pet.Left;settings["y"]=pet.Top;settings["size"]=pet.Width;Save();}
    static string Choose(string[] files,string previous){var a=files.Where(x=>x!=previous).ToArray();return (a.Length>0?a:files)[Random.Next(a.Length>0?a.Length:files.Length)];}
    internal static void ClickPet(){Play("click",false,null,"pet_mouse");}
    static void Play(string kind,bool preview,string explicitFile,string origin){
        if(closing)return;
        if(busy){if(kind=="completion")pendingCompletion=true;return;}
        string chosen=explicitFile??Choose(kind=="click"?clicks:completions,kind=="click"?lastClick:lastCompletion);
        if(!motions.ContainsKey(chosen))return;
        if(kind=="click")lastClick=chosen;else lastCompletion=chosen;
        busy=true;activeClip=chosen;int token=++playId;bool silent=muted;double gain=volume;
        Status("running",null);
        ThreadPool.QueueUserWorkItem(delegate{
            string error=null;double delay=0;
            try{
                var wav=ReadWav(File.ReadAllBytes(Path.Combine(Root,"audio",chosen)),gain);
                using(var stream=new MemoryStream(wav.Bytes))using(var player=new SoundPlayer(stream)){
                    if(!silent)player.Load();
                    long started=0;
                    dispatcher.Invoke((Action)(()=>{if(closing)return;currentPlayer=player;started=Stopwatch.GetTimestamp();pet.StartMotion(motions[chosen],started);}));
                    if(closing)return;
                    delay=(Stopwatch.GetTimestamp()-started)*1000.0/Stopwatch.Frequency;
                    if(!silent)player.PlaySync();
                    double remaining=wav.Milliseconds-(Stopwatch.GetTimestamp()-started)*1000.0/Stopwatch.Frequency;
                    if(remaining>0)Thread.Sleep((int)Math.Ceiling(remaining));
                }
            }catch(Exception ex){error=ex.GetType().Name+": "+ex.Message;}
            finally{
                if(!closing){try{dispatcher.BeginInvoke((Action)(()=>{
                    if(token!=playId)return;currentPlayer=null;pet.StopMotion();busy=false;activeClip="";
                    if(error==null){if(kind=="click")clickCount++;else completionCount++;Log((preview?"preview_":"")+kind,chosen,origin,delay,!silent,silent,gain);}else Log("play_error",error,origin,delay);
                    Status("running",error);bool next=pendingCompletion;pendingCompletion=false;if(next)Play("completion",false,null,"queued_completion");
                }));}catch(InvalidOperationException){}}
            }
        });
    }
    internal sealed class WavData {internal byte[] Bytes;internal double Milliseconds;}
    internal static WavData ReadWav(byte[] b,double gain){
        if(b.Length<44||Encoding.ASCII.GetString(b,0,4)!="RIFF"||Encoding.ASCII.GetString(b,8,4)!="WAVE")throw new InvalidDataException("Invalid WAV");
        bool pcm=false;int rate=0;
        for(int pos=12;pos+8<=b.Length;){int len=BitConverter.ToInt32(b,pos+4),start=pos+8;string id=Encoding.ASCII.GetString(b,pos,4);
            if(len<0||len>b.Length-start)throw new InvalidDataException("Invalid chunk");
            if(id=="fmt "){pcm=len>=16&&BitConverter.ToInt16(b,start)==1&&BitConverter.ToInt16(b,start+14)==16;rate=BitConverter.ToInt32(b,start+8);}
            if(id=="data"){
                if(!pcm||rate<=0)throw new InvalidDataException("PCM16 required");
                for(int i=start;i+1<start+len;i+=2){short n=(short)Math.Round(BitConverter.ToInt16(b,i)*gain);b[i]=(byte)(n&255);b[i+1]=(byte)((n>>8)&255);}
                return new WavData{Bytes=b,Milliseconds=1000.0*len/rate};
            }pos=start+len+(len&1);
        }throw new InvalidDataException("No samples");
    }
    static void Log(string kind,string clip,string origin,double delay,bool soundCompleted=false,bool wasMuted=false,double gain=0){try{
        string p=Path.Combine(Runtime,"events.jsonl");if(File.Exists(p)&&new FileInfo(p).Length>262144)File.Move(p,p+"."+DateTime.UtcNow.Ticks);
        File.AppendAllText(p,Json.Serialize(new{utc=DateTime.UtcNow.ToString("o"),kind=kind,clip=clip,origin=origin,animation=motions.ContainsKey(clip)?motions[clip].label:null,start_dispatch_delay_ms=Math.Round(delay,2),sound_player_completed=soundCompleted,muted_at_start=wasMuted,volume=gain})+Environment.NewLine,new UTF8Encoding(false));
    }catch{}}
    static void Status(string state,string error){try{
        File.WriteAllText(Path.Combine(Runtime,"status.json"),Json.Serialize(new{state=state,version="2.0.1-motion",pid=Process.GetCurrentProcess().Id,mouse_hook=false,
            own_pet_window=pet!=null&&!pet.IsDisposed,visible=pet!=null&&!pet.IsDisposed&&pet.Visible,clicks=clickCount,completions=completionCount,muted=muted,
            active_clip=activeClip,rendered_frames=pet==null?0:pet.RenderCount,error=error}),new UTF8Encoding(false));
    }catch{}}
    static int SelfTest(){try{
        Load();string previous="";for(int i=0;i<200;i++){string s=Choose(clicks,previous);if(clicks.Length>1&&s==previous)throw new Exception("Repeated click");previous=s;}
        foreach(var m in motions.Values){if(m.FrameAt(-1)!=m.frames[0]||m.FrameAt(m.duration_ms+1)!=m.frames[m.frames.Length-1])throw new Exception("Timeline boundary");
            int start=0;foreach(var f in m.frames){if(m.FrameAt(start)!=f)throw new Exception("Timeline seek");start+=f[2];}}
        using(var preview=new Bitmap(5*192,208,PixelFormat.Format32bppArgb))using(var g=Graphics.FromImage(preview)){
            int i=0;foreach(var entry in motions){var f=entry.Value.FrameAt(entry.Value.duration_ms*.8);g.DrawImage(atlas,new Rectangle(i++*192,0,192,208),new Rectangle(f[1]*192,f[0]*208,192,208),GraphicsUnit.Pixel);}
            preview.Save(Path.Combine(Runtime,"render-check.png"),ImageFormat.Png);
        }
        File.WriteAllText(Path.Combine(Runtime,"self-test.json"),Json.Serialize(new{passed=true,version="2.0.1-motion",clips=motions.Count,nonrepeat_selections=200,
            timeline_boundaries=true,wav_duration_matches=true,rendered=true,global_mouse_hook=false}),new UTF8Encoding(false));atlas.Dispose();return 0;
    }catch(Exception e){File.WriteAllText(Path.Combine(Runtime,"self-test.json"),Json.Serialize(new{passed=false,error=e.Message}));return 1;}}
    [DllImport("user32.dll")]static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}

internal sealed class PetForm:Form {
    readonly Bitmap atlas;
    readonly Dictionary<int,Bitmap> cache=new Dictionary<int,Bitmap>();
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
    readonly Stopwatch idle=Stopwatch.StartNew();
    Motion motion;long motionStart;
    Bitmap shownFrame;int lastKey=-1;
    bool pressed,dragging;Point pressPoint,pressLocation;int dragDirection=1;
    internal int RenderCount;
    internal PetForm(Bitmap source){
        atlas=source;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
        StartPosition=FormStartPosition.Manual;Text="艾莉丝 · 本地动作增强版";
        timer.Interval=25;timer.Tick+=delegate{RenderNext();};timer.Start();
        FormClosing+=delegate{Application.Exit();};
    }
    protected override bool ShowWithoutActivation{get{return true;}}
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x80000|0x80|0x08000000;return p;}}
    internal void SetSize(int width){
        width=Math.Max(96,Math.Min(240,width));Point feet=new Point(Left+Width/2,Bottom);
        Size=new Size(width,(int)Math.Round(width*208.0/192));
        foreach(var b in cache.Values)b.Dispose();cache.Clear();shownFrame=null;lastKey=-1;
        Location=Clamp(new Point(feet.X-width/2,feet.Y-Height));if(IsHandleCreated)RenderNext();
    }
    internal Point Clamp(Point p){
        var b=Screen.FromPoint(new Point(p.X+Width/2,p.Y+Height/2)).WorkingArea;
        return new Point(Math.Max(b.Left,Math.Min(b.Right-Width,p.X)),Math.Max(b.Top,Math.Min(b.Bottom-Height,p.Y)));
    }
    internal void StartMotion(Motion m,long started){motion=m;motionStart=started;lastKey=-1;RenderNext();}
    internal void StopMotion(){motion=null;idle.Restart();lastKey=-1;RenderNext();}
    protected override void OnShown(EventArgs e){base.OnShown(e);lastKey=-1;RenderNext();}
    protected override void OnVisibleChanged(EventArgs e){base.OnVisibleChanged(e);if(Visible){lastKey=-1;RenderNext();}}
    void RenderNext(){
        if(!IsHandleCreated||IsDisposed||!Visible)return;
        int row=0,col=0;
        if(motion!=null){var f=motion.FrameAt((Stopwatch.GetTimestamp()-motionStart)*1000.0/Stopwatch.Frequency);row=f[0];col=f[1];}
        else if(dragging){row=dragDirection;col=(int)(idle.ElapsedMilliseconds/90)%8;}
        else{long t=idle.ElapsedMilliseconds%5100;col=t<1800?0:t<3600?1:t<3740?2:t<3900?3:t<4500?4:5;}
        int key=row*8+col;if(key==lastKey)return;
        Bitmap b;if(!cache.TryGetValue(key,out b)){
            b=new Bitmap(Width,Height,PixelFormat.Format32bppPArgb);
            using(var g=Graphics.FromImage(b)){g.CompositingMode=CompositingMode.SourceCopy;g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                g.DrawImage(atlas,new Rectangle(0,0,Width,Height),new Rectangle(col*192,row*208,192,208),GraphicsUnit.Pixel);}
            cache[key]=b;
        }
        DrawLayer(b);shownFrame=b;lastKey=key;RenderCount++;
    }
    protected override void OnMouseDown(MouseEventArgs e){
        base.OnMouseDown(e);if(e.Button==MouseButtons.Left){pressed=true;dragging=false;pressPoint=Cursor.Position;pressLocation=Location;Capture=true;}
    }
    protected override void OnMouseMove(MouseEventArgs e){
        base.OnMouseMove(e);if(!pressed)return;Point p=Cursor.Position;int dx=p.X-pressPoint.X,dy=p.Y-pressPoint.Y;
        if(Math.Abs(dx)>6||Math.Abs(dy)>6)dragging=true;
        if(dragging){dragDirection=dx>=0?1:2;Location=Clamp(new Point(pressLocation.X+dx,pressLocation.Y+dy));RenderNext();}
    }
    protected override void OnMouseUp(MouseEventArgs e){
        base.OnMouseUp(e);if(e.Button!=MouseButtons.Left||!pressed)return;
        bool wasDrag=dragging;pressed=false;dragging=false;Capture=false;lastKey=-1;RenderNext();
        if(wasDrag)ErisVoice.SavePosition();else ErisVoice.ClickPet();
    }
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture&&pressed){pressed=false;dragging=false;ErisVoice.SavePosition();}}
    protected override void WndProc(ref Message m){
        if(m.Msg==0x21){m.Result=new IntPtr(3);return;}
        if(m.Msg==0x84&&shownFrame!=null&&!pressed){long v=m.LParam.ToInt64();Point p=PointToClient(new Point((short)(v&0xffff),(short)((v>>16)&0xffff)));
            if(p.X<0||p.Y<0||p.X>=shownFrame.Width||p.Y>=shownFrame.Height||shownFrame.GetPixel(p.X,p.Y).A<24){m.Result=new IntPtr(-1);return;}}
        base.WndProc(ref m);
    }
    void DrawLayer(Bitmap bitmap){
        IntPtr screen=GetDC(IntPtr.Zero),memory=IntPtr.Zero,hBitmap=IntPtr.Zero,old=IntPtr.Zero;
        try{
            memory=CreateCompatibleDC(screen);hBitmap=bitmap.GetHbitmap(Color.FromArgb(0));old=SelectObject(memory,hBitmap);
            var size=new SIZE{cx=bitmap.Width,cy=bitmap.Height};var source=new POINT();var dest=new POINT{x=Left,y=Top};
            var blend=new BLEND{BlendOp=0,BlendFlags=0,SourceConstantAlpha=255,AlphaFormat=1};
            if(!UpdateLayeredWindow(Handle,screen,ref dest,ref size,memory,ref source,0,ref blend,2))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }finally{if(old!=IntPtr.Zero)SelectObject(memory,old);if(hBitmap!=IntPtr.Zero)DeleteObject(hBitmap);if(memory!=IntPtr.Zero)DeleteDC(memory);if(screen!=IntPtr.Zero)ReleaseDC(IntPtr.Zero,screen);}
    }
    protected override void Dispose(bool disposing){if(disposing){timer.Stop();timer.Dispose();foreach(var b in cache.Values)b.Dispose();cache.Clear();}base.Dispose(disposing);}
    [StructLayout(LayoutKind.Sequential)]struct POINT{public int x,y;}
    [StructLayout(LayoutKind.Sequential)]struct SIZE{public int cx,cy;}
    [StructLayout(LayoutKind.Sequential,Pack=1)]struct BLEND{public byte BlendOp,BlendFlags,SourceConstantAlpha,AlphaFormat;}
    [DllImport("user32.dll",SetLastError=true)]static extern bool UpdateLayeredWindow(IntPtr h,IntPtr dest,ref POINT p,ref SIZE s,IntPtr src,ref POINT source,int color,ref BLEND blend,int flags);
    [DllImport("user32.dll")]static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")]static extern int ReleaseDC(IntPtr h,IntPtr dc);
    [DllImport("gdi32.dll")]static extern IntPtr CreateCompatibleDC(IntPtr h);
    [DllImport("gdi32.dll")]static extern bool DeleteDC(IntPtr h);
    [DllImport("gdi32.dll")]static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr h);
}
