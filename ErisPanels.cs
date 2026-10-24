using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

internal static partial class PetTheme {
    internal static readonly Color Paper=Color.FromArgb(255,248,235),Ink=Color.FromArgb(58,43,35),
        Muted=Color.FromArgb(109,91,78),Accent=Color.FromArgb(142,56,66),Line=Color.FromArgb(223,202,169),
        Green=Color.FromArgb(56,136,111),Amber=Color.FromArgb(175,119,30);
    internal static Label Label(string text,float size=9,FontStyle style=FontStyle.Regular){
        return new Label{Text=text,AutoSize=false,ForeColor=Ink,BackColor=Color.Transparent,Font=new Font("Microsoft YaHei UI",size,style),UseCompatibleTextRendering=false};
    }
    internal static Button Button(string text,Action click){
        var b=new PetButton{Text=text,AutoSize=true,Height=30,MinimumSize=new Size(72,30),FlatStyle=FlatStyle.Flat,
            ForeColor=Accent,Margin=new Padding(0,4,8,4),Cursor=Cursors.Hand};
        b.FlatAppearance.BorderColor=Line;b.Click+=delegate{click();};return b;
    }
    internal static Rectangle PositionNear(Rectangle pet,Size size,Rectangle area){
        int x=pet.Left-size.Width-4;
        if(x<area.Left)x=pet.Right+4;
        if(x+size.Width>area.Right)x=area.Right-size.Width;
        int y=pet.Top-size.Height+40;
        return new Rectangle(Math.Max(area.Left,x),Math.Max(area.Top,Math.Min(area.Bottom-size.Height,y)),size.Width,size.Height);
    }
    internal static void Place(Form window,Form pet){
        var area=Screen.FromRectangle(pet.Bounds).WorkingArea;
        window.Bounds=PositionNear(pet.Bounds,window.Size,area);
    }
}

// A monotonic, pausable lifetime shared by all three kinds of bubble.
// Loading may defer expiry for at most 30 seconds, including failed requests.
internal sealed class BubbleLifetime {
    internal double Remaining {get;private set;}
    internal double LoadingRemaining {get;private set;}
    internal double Total {get;private set;}
    internal void Restart(double milliseconds){Remaining=Total=Math.Max(1,milliseconds);}
    internal void SetLoading(bool value){LoadingRemaining=value?30000:0;}
    internal bool Advance(double elapsed,bool hovering){
        elapsed=Math.Max(0,elapsed);
        double free=Math.Max(0,elapsed-LoadingRemaining);
        LoadingRemaining=Math.Max(0,LoadingRemaining-elapsed);
        if(!hovering)Remaining=Math.Max(0,Remaining-free);
        return Remaining<=0;
    }
}

internal class PetBubble:Form {
    readonly Form pet;
    readonly System.Windows.Forms.Timer dismissTimer=new System.Windows.Forms.Timer();
    readonly Stopwatch clock=new Stopwatch();
    readonly BubbleLifetime lifetime=new BubbleLifetime();
    double lastTick;
    bool tailRight=true;
    internal readonly Label DismissHint;
    internal DateTime? OpenedUtc,HiddenUtc;
    internal string DismissReason="not_shown";
    internal bool HoverPaused {get;private set;}
    internal int SecondsLeft {get{return (int)Math.Ceiling(lifetime.Remaining/1000);}}
    internal PetBubble(Form owner){
        pet=owner;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
        AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
        StartPosition=FormStartPosition.Manual;ClientSize=new Size(344,300);
        Font=new Font("Microsoft YaHei UI",9);BackColor=PetTheme.Paper;ForeColor=PetTheme.Ink;DoubleBuffered=true;
        DismissHint=PetTheme.Label("8 秒后收起 · 悬停暂停",8);DismissHint.ForeColor=PetTheme.Muted;
        dismissTimer.Interval=33;dismissTimer.Tick+=delegate{
            double now=clock.Elapsed.TotalMilliseconds,elapsed=now-lastTick;lastTick=now;
            if(pet!=null&&!pet.Visible){Dismiss("pet_hidden");return;}
            HoverPaused=Bounds.Contains(Cursor.Position)&&Region!=null&&Region.IsVisible(PointToClient(Cursor.Position));
            if(lifetime.Advance(elapsed,HoverPaused)){Dismiss("timeout");return;}
            UpdateHint();Follow();
            double opacity=PetTheme.FadeOpacity(now,lifetime.Remaining,HoverPaused||lifetime.LoadingRemaining>0,SystemInformation.IsMenuAnimationEnabled);
            if(Math.Abs(Opacity-opacity)>.01)Opacity=opacity;
            Invalidate(new Rectangle(28,Height-8,Math.Max(1,Width-56),7));
        };
        VisibleChanged+=delegate{
            if(Visible){clock.Restart();lastTick=0;dismissTimer.Start();}
            else{dismissTimer.Stop();clock.Stop();HiddenUtc=DateTime.UtcNow;}
        };
        SizeChanged+=delegate{UpdateShape();if(Visible)Follow();};
        KeyPreview=true;KeyDown+=delegate(object s,KeyEventArgs e){if(e.KeyCode==Keys.Escape){Dismiss("escape");e.Handled=true;}};
        FormClosing+=delegate(object s,FormClosingEventArgs e){if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Dismiss("close");}};
        if(pet!=null){pet.LocationChanged+=PetMoved;pet.SizeChanged+=PetMoved;pet.VisibleChanged+=PetMoved;}
        UpdateShape();
    }
    protected override bool ShowWithoutActivation{get{return true;}}
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x80|0x08000000;return p;}}
    protected override void WndProc(ref Message m){if(m.Msg==0x21){m.Result=new IntPtr(3);return;}base.WndProc(ref m);}
    internal void ShowFor(int milliseconds=8000){
        lifetime.Restart(milliseconds);lastTick=clock.Elapsed.TotalMilliseconds;
        OpenedUtc=DateTime.UtcNow;HiddenUtc=null;DismissReason=null;HoverPaused=false;UpdateHint();
        Follow();if((pet==null||pet.Visible)&&!Visible){Opacity=SystemInformation.IsMenuAnimationEnabled ? .02 : 1;Show();}
    }
    internal void RestartLifetime(int milliseconds=8000){
        lifetime.Restart(milliseconds);lastTick=clock.Elapsed.TotalMilliseconds;UpdateHint();
    }
    internal void SetLoading(bool loading){lifetime.SetLoading(loading);if(!loading)RestartLifetime();UpdateHint();}
    internal void Dismiss(string reason){if(Visible){DismissReason=reason;Hide();}}
    void UpdateHint(){
        DismissHint.Text=HoverPaused?"鼠标移开后继续计时":lifetime.LoadingRemaining>0?"读取后开始计时 · 悬停暂停":
            SecondsLeft+" 秒后收起 · 悬停暂停";
    }
    void PetMoved(object sender,EventArgs e){if(Visible){if(!pet.Visible)Dismiss("pet_hidden");else Follow();}}
    internal void Follow(){
        if(pet==null)return;
        Rectangle area=Screen.FromRectangle(pet.Bounds).WorkingArea;
        Location=PetTheme.PositionNear(pet.Bounds,Size,area).Location;
        bool right=pet.Left+pet.Width/2>=Left+Width/2;
        if(right!=tailRight){tailRight=right;UpdateShape();}
    }
    internal GraphicsPath BubblePath(){
        float left=12,top=1,right=Math.Max(42,ClientSize.Width-13),bottom=Math.Max(44,ClientSize.Height-2),r=16;
        float tail=bottom-35;
        var path=new GraphicsPath();
        path.AddArc(left,top,r*2,r*2,180,90);path.AddArc(right-r*2,top,r*2,r*2,270,90);
        if(tailRight){path.AddLine(right,top+r,right,tail-10);path.AddLine(right,tail-10,right+11,tail+1);path.AddLine(right+11,tail+1,right,tail+9);}
        path.AddArc(right-r*2,bottom-r*2,r*2,r*2,0,90);path.AddArc(left,bottom-r*2,r*2,r*2,90,90);
        if(!tailRight){path.AddLine(left,bottom-r,left,tail+9);path.AddLine(left,tail+9,left-11,tail+1);path.AddLine(left-11,tail+1,left,tail-10);}
        path.CloseFigure();return path;
    }
    void UpdateShape(){
        if(ClientSize.Width<50||ClientSize.Height<50)return;
        using(var path=BubblePath()){var previous=Region;Region=new Region(path);if(previous!=null)previous.Dispose();}
        Invalidate();
    }
    internal void PaintBubble(Graphics graphics){
        graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using(var path=BubblePath())PetTheme.PaintSkin(graphics,path,ClientSize);
        float width=Math.Max(1,Width-60);
        double progress=lifetime.Total<=0?1:lifetime.Remaining/lifetime.Total;
        using(var track=new Pen(Color.FromArgb(42,PetTheme.Gold),2))
        using(var fill=new Pen(Color.FromArgb(145,PetTheme.Gold),2)){
            graphics.DrawLine(track,30,Height-5,30+width,Height-5);
            graphics.DrawLine(fill,30,Height-5,30+(float)(width*progress),Height-5);
        }
    }
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);PaintBubble(e.Graphics);}
    internal Button CloseButton(){
        var close=new PetButton{Text="×",FlatStyle=FlatStyle.Flat,TabStop=false,ForeColor=PetTheme.Muted,
            Bounds=new Rectangle(ClientSize.Width-51,7,27,27),Anchor=AnchorStyles.Top|AnchorStyles.Right,Cursor=Cursors.Hand};
        close.FlatAppearance.BorderSize=0;close.Click+=delegate{Dismiss("close");};return close;
    }
    protected override void Dispose(bool disposing){
        if(disposing){
            dismissTimer.Dispose();
            if(pet!=null){pet.LocationChanged-=PetMoved;pet.SizeChanged-=PetMoved;pet.VisibleChanged-=PetMoved;}
        }
        base.Dispose(disposing);
    }
}

internal class PetInfoForm:PetBubble {
    internal readonly Panel Body;
    internal readonly FlowLayoutPanel Buttons;
    internal readonly Label Note;
    internal PetInfoForm(Form owner,string title,string heading,string subtitle):base(owner){
        Text=title;Font=new Font("Microsoft YaHei UI",9);BackColor=PetTheme.Paper;ForeColor=PetTheme.Ink;
        Padding=new Padding(24,4,24,7);ClientSize=new Size(352,370);
        var header=new Panel{Dock=DockStyle.Top,Height=PetTheme.Adventure?106:67,Padding=new Padding(1,PetTheme.Adventure?27:8,PetTheme.Adventure?96:26,5),BackColor=Color.Transparent};
        var h=PetTheme.Label(heading,14,FontStyle.Bold);h.Dock=DockStyle.Top;h.Height=30;
        var sub=PetTheme.Label(subtitle,8.5f);sub.ForeColor=PetTheme.Muted;sub.Dock=DockStyle.Fill;
        header.Controls.Add(sub);header.Controls.Add(h);
        if(PetTheme.Adventure){
            var badge=PetTheme.Label("ERIS / 冒险手账",8,FontStyle.Bold);badge.ForeColor=PetTheme.Accent;
            badge.SetBounds(2,5,175,20);header.Controls.Add(badge);badge.BringToFront();
        }
        var footer=new Panel{Dock=DockStyle.Bottom,Height=86,BackColor=Color.Transparent};
        Note=PetTheme.Label("",8);Note.Dock=DockStyle.Fill;Note.ForeColor=PetTheme.Muted;
        Buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=36,WrapContents=false,BackColor=Color.Transparent};
        DismissHint.Dock=DockStyle.Bottom;DismissHint.Height=20;
        footer.Controls.Add(Note);footer.Controls.Add(Buttons);footer.Controls.Add(DismissHint);
        Body=new Panel{Dock=DockStyle.Fill,Padding=new Padding(0,0,0,5),BackColor=Color.Transparent};
        Controls.Add(Body);Controls.Add(footer);Controls.Add(header);
        var close=CloseButton();Controls.Add(close);close.BringToFront();
    }
}

internal sealed class QuotaCard:Panel {
    readonly UsageWindow value;
    readonly Label reset;
    internal QuotaCard(UsageWindow window){
        value=window;Height=110;Width=302;Margin=new Padding(0,0,0,8);BackColor=Color.Transparent;
        DoubleBuffered=true;
        var name=PetTheme.Label(value.bucket+" · "+value.Period,9,FontStyle.Bold);name.SetBounds(12,6,278,21);name.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
        var percent=PetTheme.Label(value.remaining.HasValue?value.remaining.Value.ToString("0.#",CultureInfo.InvariantCulture)+"% 剩余":"剩余额度未提供",18,FontStyle.Bold);
        percent.ForeColor=value.remaining.HasValue&&value.remaining<=20?PetTheme.Amber:PetTheme.Accent;
        percent.SetBounds(10,25,278,34);percent.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
        reset=PetTheme.Label("",8);reset.ForeColor=PetTheme.Muted;reset.SetBounds(12,75,278,31);reset.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
        Controls.AddRange(new Control[]{name,percent,reset});UpdateClock();
    }
    internal void UpdateClock(){reset.Text=value.ResetText(DateTime.UtcNow);}
    protected override void OnPaint(PaintEventArgs e){
        base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        using(var card=PetTheme.Rounded(new RectangleF(0,0,Width-1,Height-1),12))
        using(var surface=new SolidBrush(Color.FromArgb(255,252,244)))
        using(var pen=new Pen(PetTheme.Line)){g.FillPath(surface,card);g.DrawPath(pen,card);}
        var bar=new Rectangle(12,62,Math.Max(1,Width-24),6);
        using(var track=PetTheme.Rounded(bar,3))using(var brush=new SolidBrush(PetTheme.Line))g.FillPath(brush,track);
        if(value.remaining.HasValue){
            Color color=value.remaining<=10?PetTheme.Accent:value.remaining<=20?PetTheme.Amber:PetTheme.Green;
            if(value.remaining>0)using(var fill=PetTheme.Rounded(new RectangleF(bar.X,bar.Y,(float)(bar.Width*value.remaining.Value/100),bar.Height),3))
                using(var brush=new LinearGradientBrush(bar,color,PetTheme.Gold,0f))g.FillPath(brush,fill);
        }
    }
}

internal sealed class QuotaForm:PetInfoForm {
    readonly FlowLayoutPanel cards;
    readonly Label summary;
    readonly Button refresh;
    UsageSnapshot snapshot;
    string failure;
    bool refreshing;
    internal QuotaForm(Form owner,Action request,Action activity):base(owner,"艾莉丝 · Codex 额度","艾莉丝的补给站","看看还能一起做多少事。"){
        summary=PetTheme.Label("",8.5f);summary.Dock=DockStyle.Top;summary.Height=40;summary.ForeColor=PetTheme.Muted;
        cards=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,BackColor=Color.Transparent};
        cards.SizeChanged+=delegate{FitCards();};
        Body.Controls.Add(cards);Body.Controls.Add(summary);
        refresh=PetTheme.Button("刷新额度",request);Buttons.Controls.Add(refresh);
        Buttons.Controls.Add(PetTheme.Button("完成提醒",activity));Buttons.Controls.Add(PetTheme.Button("收起",()=>Dismiss("close")));
        Present(null,null,false);
    }
    void FitCards(){foreach(Control c in cards.Controls)c.Width=Math.Max(240,cards.ClientSize.Width-2);}
    internal void Present(UsageSnapshot data,string error,bool loading){
        bool changed=!Object.ReferenceEquals(snapshot,data)||cards.Controls.Count==0;
        bool finished=refreshing&&!loading;
        snapshot=data;failure=error;refreshing=loading;refresh.Enabled=!loading;refresh.Text=loading?"读取中…":"刷新额度";
        if(loading)SetLoading(true);else if(finished)SetLoading(false);
        if(changed){
            while(cards.Controls.Count>0){var c=cards.Controls[0];cards.Controls.Remove(c);c.Dispose();}
            if(data!=null&&data.windows.Count>0){foreach(var w in data.windows)cards.Controls.Add(new QuotaCard(w));}
            else {
                var empty=PetTheme.Label(data==null?"还没有额度数据\n\n点击“刷新额度”，读取当前 Codex 账户的用量。":"当前账户未提供可显示的额度窗口",11);
                empty.Height=110;empty.Padding=new Padding(12,12,8,8);cards.Controls.Add(empty);
            }
            ClientSize=new Size(ClientSize.Width,(int)Math.Round(((PetTheme.Adventure?248:209)+118*Math.Min(2,Math.Max(1,cards.Controls.Count)))*CurrentScale));
            FitCards();
        }
        summary.Text=data==null?"仅查看账户额度；不会发起聊天任务。":
            (String.IsNullOrEmpty(data.plan)?"套餐未提供":data.plan)+" · 额外点数："+(data.credits??"未提供")+
            "\n可用重置次数："+(data.reset_credits.HasValue?data.reset_credits.Value.ToString():"未提供")+" · 账户共享";
        UpdateClock();
    }
    internal void UpdateClock(){
        foreach(var c in cards.Controls.OfType<QuotaCard>())c.UpdateClock();
        if(refreshing){Note.ForeColor=PetTheme.Muted;Note.Text="正在读取 Codex 额度…\n读完再开始收起倒计时。";return;}
        bool stale=snapshot!=null&&(failure!=null||snapshot.IsStale(DateTime.UtcNow));
        Note.ForeColor=failure!=null||stale?PetTheme.Amber:PetTheme.Muted;
        string last=snapshot==null?"尚无成功读取":snapshot.Fetched.ToLocalTime().ToString("MM-dd HH:mm:ss");
        if(failure!=null)Note.Text=failure+"\n"+(snapshot==null?"可在 Codex 确认登录后再刷新。":"显示上次结果（已过期） · "+last);
        else if(stale)Note.Text="上次结果已过期 · "+last+"\n点击刷新，获取当前额度。";
        else Note.Text="更新于 "+last+"\n再次打开或点击刷新，获取新额度。";
    }
    float CurrentScale {get{return ClientSize.Width/352f;}}
}

internal sealed class CaptionLine {
    public string file {get;set;}
    public string label {get;set;}
    public string transcript {get;set;}
}
internal sealed class CaptionCatalog {public List<CaptionLine> clips {get;set;}}

internal sealed class SpeechBubble:PetBubble {
    readonly Label title,caption,original;
    internal SpeechBubble(Form owner):base(owner){
        ClientSize=new Size(352,190);Text="艾莉丝 · 语音字幕";
        title=PetTheme.Label("艾莉丝",9,FontStyle.Bold);title.ForeColor=PetTheme.Accent;title.SetBounds(26,20,200,22);
        caption=PetTheme.Label("",12,FontStyle.Bold);caption.SetBounds(26,52,205,46);
        original=PetTheme.Label("",8.5f);original.ForeColor=PetTheme.Muted;original.SetBounds(26,112,300,35);
        DismissHint.SetBounds(26,160,285,20);
        Controls.AddRange(new Control[]{title,caption,original,CloseButton(),DismissHint});
    }
    internal void Speak(CaptionLine line,string kind,int duration,bool silent,bool demo=false){
        title.Text=demo?"艾莉丝 · 样式预览":kind=="completion"?"艾莉丝 · 任务完成":"艾莉丝";
        caption.Text=line.label;original.Text=line.transcript+(silent?"  ·  已静音":"");
        ShowFor(Math.Max(8000,duration+2000));Invalidate();
    }
    internal void PreviewText(CaptionLine line){title.Text="艾莉丝 · 字幕样式预览";caption.Text=line.label;original.Text=line.transcript;}
}

internal sealed class ReminderEntry {
    internal DateTime Time;
    internal string Clip;
}
internal static class ReminderHistory {
    internal static List<ReminderEntry> Read(string path) {
        var results=new List<ReminderEntry>();
        try{
            if(!File.Exists(path)||new FileInfo(path).Length>600000)return results;
            foreach(string line in File.ReadAllLines(path,Encoding.UTF8)){
                try{
                    var obj=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(line);
                    if(UsageData.Text(UsageData.Get(obj,"kind"))!="completion")continue;
                    DateTime when;
                    if(!DateTime.TryParse(UsageData.Text(UsageData.Get(obj,"utc")),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out when))continue;
                    results.Add(new ReminderEntry{Time=when.ToLocalTime(),Clip=UsageData.Text(UsageData.Get(obj,"clip"))});
                }catch{}
            }
        }catch{}
        return results.OrderByDescending(x=>x.Time).Take(12).ToList();
    }
}

internal sealed class ActivityForm:PetInfoForm {
    readonly Label state,details;
    readonly FlowLayoutPanel history;
    internal ActivityForm(Form owner,Action quota):base(owner,"艾莉丝 · 完成提醒","刚才的冒险","你完成的任务，艾莉丝都记着。"){
        ClientSize=new Size(352,PetTheme.Adventure?442:403);
        state=PetTheme.Label("",10,FontStyle.Bold);state.Dock=DockStyle.Top;state.Height=29;state.ForeColor=PetTheme.Accent;
        details=PetTheme.Label("",8.5f);details.Dock=DockStyle.Top;details.Height=48;details.ForeColor=PetTheme.Muted;
        history=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,
            AutoScroll=true,BackColor=Color.Transparent};
        history.SizeChanged+=delegate{FitHistory();};
        Body.Controls.Add(history);Body.Controls.Add(details);Body.Controls.Add(state);
        Buttons.Controls.Add(PetTheme.Button("查看额度",quota));Buttons.Controls.Add(PetTheme.Button("收起",()=>Dismiss("close")));
        Note.Text="最近 12 条完成提醒，可滚动查看。\n只记录提醒，不读取聊天正文。";
    }
    internal void SetStatus(bool installed,bool muted,string active){
        state.Text=String.IsNullOrEmpty(active)?"待命，等你完成下一项任务":"正在播放："+active;
        details.Text=(installed?"完成通知：已安装接入配置":"完成通知：尚未安装接入")+
            "\n声音："+(muted?"已静音":"开启")+" · 任务密集结束时可能合并播报";
    }
    internal void SetHistory(List<ReminderEntry> rows,Dictionary<string,CaptionLine> captions){
        history.SuspendLayout();
        while(history.Controls.Count>0){var child=history.Controls[0];history.Controls.Remove(child);child.Dispose();}
        bool first=true;
        foreach(var row in rows){CaptionLine line;string label=captions.TryGetValue(row.Clip,out line)?line.label:"完成提醒";
            history.Controls.Add(new ReminderRow(row.Time.ToString("MM-dd HH:mm:ss"),label,first));first=false;}
        if(rows.Count==0)history.Controls.Add(new ReminderRow("暂无完成提醒","完成一轮 Codex 任务后显示",false));
        history.ResumeLayout();FitHistory();
    }
    void FitHistory(){foreach(Control row in history.Controls)row.Width=Math.Max(240,history.ClientSize.Width-2);}
}

internal sealed class PetPanels:IDisposable {
    readonly Form pet;
    readonly Dictionary<string,object> settings;
    readonly Action save;
    readonly Func<bool> muted;
    readonly Func<string> active;
    readonly UsageClient client=new UsageClient();
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
    readonly Dictionary<string,CaptionLine> captions;
    readonly string cache=Path.Combine(ErisVoice.Root,"runtime","usage-cache.json");
    QuotaForm quota;
    ActivityForm activity;
    SpeechBubble bubble;
    UsageSnapshot snapshot;
    DateTime lastAttempt=DateTime.MinValue;
    string failure;
    bool fetching,disposed;
    internal string Theme {
        get {return settings.ContainsKey("bubble_theme")&&Object.Equals(settings["bubble_theme"],"plain")?"plain":"adventure";}
        set {
            if(disposed)return;
            settings["bubble_theme"]=value=="plain"?"plain":"adventure";save();HideAll();
            if(quota!=null){quota.Dispose();quota=null;}if(activity!=null){activity.Dispose();activity=null;}
            if(bubble!=null){bubble.Dispose();bubble=null;}
            PetTheme.Configure(Theme);OpenQuota();
        }
    }
    internal bool CaptionsEnabled {
        get {return !settings.ContainsKey("captions_enabled")||Object.Equals(settings["captions_enabled"],true);}
        set {settings["captions_enabled"]=value;save();if(!value&&bubble!=null)bubble.Dismiss("captions_disabled");WriteStatus();}
    }
    internal PetPanels(Form owner,Dictionary<string,object> config,Action persist,Func<bool> isMuted,Func<string> activeClip){
        pet=owner;settings=config;save=persist;muted=isMuted;active=activeClip;
        PetTheme.Configure(Theme);
        captions=LoadCaptions(ErisVoice.Root);snapshot=UsageData.Load(cache);
        pet.VisibleChanged+=PetVisibility;
        timer.Interval=1000;timer.Tick+=delegate{
            if(quota!=null&&quota.Visible)quota.UpdateClock();
            if(activity!=null&&activity.Visible)UpdateActivity(false);
            if((quota!=null&&quota.Visible)||(activity!=null&&activity.Visible)||(bubble!=null&&bubble.Visible))WriteStatus();
        };timer.Start();
    }
    internal static Dictionary<string,CaptionLine> LoadCaptions(string root){
        // Early personal installations used the Chinese filename. Optional caption
        // metadata must never prevent the original audio/animation from starting.
        foreach(string name in new[]{"voice-sources.json","来源与截取.json"}){
            try{
                string path=Path.Combine(root,name);
                if(!File.Exists(path)||new FileInfo(path).Length>131072)continue;
                var catalog=new JavaScriptSerializer().Deserialize<CaptionCatalog>(File.ReadAllText(path,Encoding.UTF8));
                if(catalog==null||catalog.clips==null)continue;
                var result=new Dictionary<string,CaptionLine>();
                foreach(var line in catalog.clips){
                    if(line==null||String.IsNullOrEmpty(line.file)||Path.GetFileName(line.file)!=line.file||String.IsNullOrEmpty(line.label))continue;
                    line.label=UsageData.Text(line.label,100);line.transcript=UsageData.Text(line.transcript,160);
                    result[line.file]=line;
                }
                if(result.Count>0)return result;
            }catch{}
        }
        return new Dictionary<string,CaptionLine>();
    }
    internal void OpenQuota(){
        if(disposed)return;
        if(quota==null){
            quota=new QuotaForm(pet,()=>Refresh(),()=>OpenActivity());
            quota.VisibleChanged+=delegate{WriteStatus();};
        }
        quota.Present(snapshot,failure,fetching);
        if(activity!=null)activity.Dismiss("switched");
        if(bubble!=null)bubble.Dismiss("switched");
        quota.ShowFor();
        if(DateTime.UtcNow-lastAttempt>TimeSpan.FromSeconds(15))Refresh();
        WriteStatus();
    }
    internal void OpenActivity(){
        if(disposed)return;
        if(activity==null){activity=new ActivityForm(pet,()=>OpenQuota());activity.VisibleChanged+=delegate{WriteStatus();};}
        UpdateActivity(true);
        if(quota!=null)quota.Dismiss("switched");
        if(bubble!=null)bubble.Dismiss("switched");
        activity.ShowFor();
        WriteStatus();
    }
    internal void Say(string clip,string kind,int duration,bool silent){
        if(!CaptionsEnabled||disposed||!pet.Visible)return;
        CaptionLine line;if(!captions.TryGetValue(clip,out line))return;
        if(quota!=null)quota.Dismiss("speech");
        if(activity!=null)activity.Dismiss("speech");
        if(bubble==null){bubble=new SpeechBubble(pet);bubble.VisibleChanged+=delegate{WriteStatus();};}
        bubble.Speak(line,kind,duration,silent);WriteStatus();
    }
    internal void PlaybackFinished(){
        if(activity!=null&&activity.Visible)UpdateActivity(true);
    }
    void UpdateActivity(bool history){
        if(activity==null)return;
        CaptionLine line;string clip=active();string title=captions.TryGetValue(clip??"",out line)?line.label:clip;
        activity.SetStatus(File.Exists(Path.Combine(ErisVoice.Root,"integration.json")),muted(),title);
        if(history)activity.SetHistory(ReminderHistory.Read(Path.Combine(ErisVoice.Root,"runtime","events.jsonl")),captions);
    }
    void Refresh(){
        if(disposed||fetching)return;
        // Limit rapid manual clicks as well as automatic refreshes.
        if(DateTime.UtcNow-lastAttempt<TimeSpan.FromSeconds(5)){if(quota!=null)quota.RestartLifetime();return;}
        fetching=true;lastAttempt=DateTime.UtcNow;if(quota!=null)quota.Present(snapshot,failure,true);WriteStatus();
        ThreadPool.QueueUserWorkItem(delegate{
            UsageSnapshot next=null;string error=null;
            try{next=client.Read();try{UsageData.Save(cache,next);}catch{}}catch(OperationCanceledException){return;}
            catch(FileNotFoundException){error="未找到 Codex CLI，请先打开 Codex 桌面版。";}
            catch(TimeoutException){error="额度读取超时，请检查网络后重试。";}
            catch{error="额度暂时无法读取，请在 Codex 确认登录后重试。";}
            if(disposed)return;
            try{pet.BeginInvoke((Action)(()=>{
                if(disposed)return;fetching=false;failure=error;if(next!=null)snapshot=next;
                if(quota!=null)quota.Present(snapshot,failure,false);WriteStatus();
            }));}catch(InvalidOperationException){}
        });
    }
    internal void HideAll(){if(quota!=null)quota.Dismiss("hide_all");if(activity!=null)activity.Dismiss("hide_all");if(bubble!=null)bubble.Dismiss("hide_all");WriteStatus();}
    void PetVisibility(object s,EventArgs e){if(!pet.Visible)HideAll();}
    void WriteStatus(){
        if(disposed)return;
        try{
            var value=new{version="2.2.0-adventure",quota_visible=quota!=null&&quota.Visible,activity_visible=activity!=null&&activity.Visible,
                captions_enabled=CaptionsEnabled,bubble_visible=bubble!=null&&bubble.Visible,usage_fetching=fetching,
                bubble_theme=Theme,theme_artwork_loaded=PetTheme.HasArtwork,animations_enabled=SystemInformation.IsMenuAnimationEnabled,
                auto_hide_seconds=8,quota_lifetime=LifetimeStatus(quota),activity_lifetime=LifetimeStatus(activity),caption_lifetime=LifetimeStatus(bubble),
                usage_last_success=snapshot==null?null:snapshot.fetched_utc,usage_error=failure};
            File.WriteAllText(Path.Combine(ErisVoice.Root,"runtime","panels-status.json"),new JavaScriptSerializer().Serialize(value),new UTF8Encoding(false));
        }catch{}
    }
    static object LifetimeStatus(PetBubble b){return b==null?null:new{opened_utc=b.OpenedUtc,hidden_utc=b.HiddenUtc,reason=b.DismissReason,seconds_left=b.SecondsLeft,hover_paused=b.HoverPaused,opacity=b.Opacity};}
    public void Dispose(){
        disposed=true;timer.Dispose();client.Dispose();pet.VisibleChanged-=PetVisibility;
        if(quota!=null)quota.Dispose();if(activity!=null)activity.Dispose();if(bubble!=null)bubble.Dispose();
    }
}
