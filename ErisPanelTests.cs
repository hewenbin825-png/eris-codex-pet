using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Web.Script.Serialization;

internal static class ErisPanelTests {
    static void Check(bool condition,string name){if(!condition)throw new Exception("Panel self-test: "+name);}
    static UsageSnapshot Parse(string json){return UsageData.Parse(new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json),DateTime.UtcNow);}
    static void Render(Form form,string path) {
        // Force layout and render each hidden control without showing a desktop window.
        Prepare(form);
        using(var bitmap=new Bitmap(form.ClientSize.Width,form.ClientSize.Height,PixelFormat.Format32bppArgb))
        using(var g=Graphics.FromImage(bitmap)){
            g.Clear(Color.Transparent);
            var bubble=form as PetBubble;
            if(bubble!=null)bubble.PaintBubble(g);else g.Clear(form.BackColor);
            if(form.Region!=null)g.SetClip(form.Region,System.Drawing.Drawing2D.CombineMode.Intersect);
            PaintChildren(form,g,Point.Empty);bitmap.Save(path,ImageFormat.Png);
        }
    }
    static void Prepare(Control parent){var handle=parent.Handle;parent.PerformLayout();foreach(Control child in parent.Controls)Prepare(child);}
    static void PaintChildren(Control parent,Graphics canvas,Point offset){
        foreach(Control child in parent.Controls.Cast<Control>().Reverse()){
            if(child.Width<=0||child.Height<=0)continue;
            var point=new Point(offset.X+child.Left,offset.Y+child.Top);
            using(var b=new Bitmap(child.Width,child.Height)){
                child.DrawToBitmap(b,new Rectangle(Point.Empty,child.Size));canvas.DrawImageUnscaled(b,point);
            }
            var saved=canvas.Save();canvas.SetClip(new Rectangle(point,child.ClientSize),System.Drawing.Drawing2D.CombineMode.Intersect);
            PaintChildren(child,canvas,point);canvas.Restore(saved);
        }
    }
    internal static void Run(string root){
        PetTheme.Configure("adventure");Check(PetTheme.HasArtwork,"theme artwork decodes");
        Check(PetTheme.FadeOpacity(500,8000,false,true)==1,"fully visible after fade in");
        Check(PetTheme.FadeOpacity(500,120,false,true)==.5,"gentle fade before expiry");
        Check(PetTheme.FadeOpacity(500,120,true,true)==1,"hover restores full opacity");
        Check(PetTheme.FadeOpacity(0,10,false,false)==1,"disabled system animations stay opaque");
        var life=new BubbleLifetime();life.Restart(8000);
        Check(!life.Advance(7900,false)&&life.Remaining==100,"visible for almost eight seconds");
        Check(!life.Advance(60000,true)&&life.Remaining==100,"hover pauses rather than consuming time");
        Check(life.Advance(100,false),"expires after remaining time");
        life.Restart(8000);Check(!life.Advance(1000,false)&&life.Remaining==7000,"reopen resets lifetime");
        life.SetLoading(true);Check(!life.Advance(28000,false)&&life.Remaining==7000,"loading defers dismissal");
        life.SetLoading(false);life.Restart(8000);Check(!life.Advance(7999,false),"result gets full reading time");
        Check(life.Advance(1,false),"result dismisses after eight seconds");
        life.Restart(8000);life.SetLoading(true);Check(life.Advance(38000,false),"loading hold has a finite upper bound");
        life.Restart(8000);Check(!life.Advance(-1000,false)&&life.Remaining==8000,"negative clock intervals ignored");
        var normal=Parse("{\"accountId\":\"PRIVATE_ACCOUNT_SENTINEL\",\"rateLimits\":{\"primary\":{\"usedPercent\":99}},\"rateLimitsByLimitId\":{\"codex\":{\"limitId\":\"codex\",\"planType\":\"demo\",\"primary\":{\"usedPercent\":22,\"windowDurationMins\":300,\"resetsAt\":1990000000},\"secondary\":{\"usedPercent\":47,\"windowDurationMins\":10080},\"credits\":{\"hasCredits\":false,\"balance\":\"0\"}}},\"rateLimitResetCredits\":{\"availableCount\":0,\"credits\":[{\"id\":\"PRIVATE_RESET_SENTINEL\"}]}}");
        Check(normal.windows.Count==2&&normal.windows[0].remaining==78,"multi-bucket overrides legacy");
        Check(normal.windows[0].Period=="5 小时额度"&&normal.windows[1].Period=="7 天额度","dynamic periods");
        Check(normal.credits=="无"&&normal.reset_credits==0,"zero versus unavailable");
        var missing=Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":null},\"secondary\":null}}");
        Check(missing.windows.Count==1&&!missing.windows[0].remaining.HasValue,"unknown is not full or zero");
        Check(missing.windows[0].Period=="额度周期未提供"&&missing.windows[0].ResetText(DateTime.UtcNow)=="重置时间未提供","missing times");
        Check(!missing.reset_credits.HasValue,"missing reset credits");
        var extremes=Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":150},\"secondary\":{\"usedPercent\":-20,\"resetsAt\":1}}}");
        Check(extremes.windows[0].remaining==0&&extremes.windows[1].remaining==100,"clamp percentages");
        Check(extremes.windows[1].ResetText(DateTime.UtcNow).Contains("等待刷新"),"reset time does not manufacture new quota");
        var weekly=Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":1,\"windowDurationMins\":10080},\"secondary\":null}}");
        Check(weekly.windows.Count==1&&weekly.windows[0].Period=="7 天额度","single weekly window");
        bool failed=false;try{Parse("{}");}catch(InvalidDataException){failed=true;}Check(failed,"invalid payload rejected");
        Check(!normal.IsStale(DateTime.UtcNow),"fresh timestamp");normal.fetched_utc=DateTime.UtcNow.AddMinutes(-6).ToString("o");
        Check(normal.IsStale(DateTime.UtcNow),"stale timestamp");
        string serialized=new JavaScriptSerializer().Serialize(normal);
        Check(!serialized.Contains("PRIVATE_")&&!serialized.Contains("accountId"),"cache allowlist");
        string runtime=Path.Combine(root,"runtime"),scratch=Path.Combine(runtime,"panels-test-"+Guid.NewGuid().ToString("N")+".json");
        try{
            UsageData.Save(scratch,normal);Check(UsageData.Load(scratch).windows.Count==2,"cache roundtrip");
            File.WriteAllText(scratch,"corrupt");Check(UsageData.Load(scratch)==null,"corrupt cache");
            Check(PetTheme.TryLoadArtwork(scratch)==null&&PetTheme.TryLoadArtwork(scratch+".missing")==null,"missing or corrupt art falls back");
            File.WriteAllLines(scratch,new[]{
                "{\"kind\":\"preview_completion\",\"utc\":\"2026-10-20T06:01:00Z\",\"clip\":\"complete_yay.wav\"}",
                "{\"kind\":\"completion\",\"utc\":\"2026-10-20T06:02:00Z\",\"clip\":\"complete_done.wav\",\"last-assistant-message\":\"PRIVATE_CHAT_SENTINEL\"}",
                "{\"kind\":\"click\",\"utc\":\"2026-10-20T06:03:00Z\",\"clip\":\"click_special.wav\"}","broken"
            },new UTF8Encoding(false));
            var reminders=ReminderHistory.Read(scratch);Check(reminders.Count==1&&reminders[0].Clip=="complete_done.wav","history ignores previews and malformed lines");
        }finally{if(File.Exists(scratch))File.Delete(scratch);}
        int placements=0;
        foreach(var screen in new[]{new Rectangle(0,0,1920,1040),new Rectangle(-1920,0,1920,1080),new Rectangle(0,-900,1440,900)}){
            foreach(var point in new[]{screen.Location,new Point(screen.Right-160,screen.Bottom-174),new Point(screen.Left+300,screen.Top+150)}){
                foreach(var size in new[]{new Size(352,190),new Size(352,484),new Size(440,605)}){
                    var placed=PetTheme.PositionNear(new Rectangle(point,new Size(160,174)),size,screen);
                    Check(screen.Contains(placed),"multi-monitor bounds");placements++;
                }
            }
        }
        var captions=PetPanels.LoadCaptions(root);
        Check(captions.Count==5&&captions.Values.All(c=>!String.IsNullOrEmpty(c.label)&&!String.IsNullOrEmpty(c.transcript)),"five bilingual captions");
        string legacy=Path.Combine(runtime,"caption-fixture-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(legacy);
        try{
            Check(PetPanels.LoadCaptions(legacy).Count==0,"missing captions do not fail startup");
            File.Copy(Path.Combine(root,"voice-sources.json"),Path.Combine(legacy,"来源与截取.json"));
            Check(PetPanels.LoadCaptions(legacy).Count==5,"legacy Chinese filename");
            File.WriteAllText(Path.Combine(legacy,"voice-sources.json"),"broken");
            Check(PetPanels.LoadCaptions(legacy).Count==5,"invalid new catalog falls back");
        }finally{
            foreach(string name in new[]{"voice-sources.json","来源与截取.json"}){string path=Path.Combine(legacy,name);if(File.Exists(path))File.Delete(path);}
            Directory.Delete(legacy,false);
        }
        normal.fetched_utc=DateTime.UtcNow.ToString("o");
        normal.plan="演示数据";
        long epoch=(long)(DateTime.UtcNow-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds;
        normal.windows[0].reset_at=epoch+3*3600+42*60;normal.windows[1].reset_at=epoch+6*86400+12*3600;
        // Hidden controls rendered to bitmaps, not desktop screenshots or click tests.
        using(var quota=new QuotaForm(null,()=>{},()=>{})){
            quota.Present(normal,null,false);Render(quota,Path.Combine(runtime,"quota-panel-preview.png"));
            Check(quota.FormBorderStyle==FormBorderStyle.None&&!quota.ShowInTaskbar,"quota is a borderless bubble");
            using(var shape=quota.BubblePath()){
                Check(!shape.IsVisible(1,1)&&shape.IsVisible(quota.Width/2,quota.Height/2),"rounded bubble shape");
                Check(shape.IsVisible(quota.Width-4,quota.Height-36),"bubble tail points to pet");
            }
            quota.Present(normal,"模拟离线：保留上次结果",false);Check(quota.Note.Text.Contains("已过期"),"failure keeps a marked previous result");
            Render(quota,Path.Combine(runtime,"quota-stale-preview.png"));
            quota.Present(missing,null,false);Render(quota,Path.Combine(runtime,"quota-unknown-preview.png"));
        }
        using(var activity=new ActivityForm(null,()=>{})){
            activity.SetStatus(true,false,"");
            activity.SetHistory(new List<ReminderEntry>{new ReminderEntry{Time=DateTime.Now,Clip="complete_done.wav"},new ReminderEntry{Time=DateTime.Now.AddMinutes(-8),Clip="complete_yay.wav"}},captions);
            Render(activity,Path.Combine(runtime,"activity-panel-preview.png"));
        }
        using(var pet=new Form())using(var bubble=new SpeechBubble(pet)){
            bubble.PreviewText(captions["click_magic_nyan.wav"]);Render(bubble,Path.Combine(runtime,"caption-preview.png"));
        }
        PetTheme.Configure("plain");Check(!PetTheme.Adventure,"plain theme selected");
        using(var pet=new Form())using(var bubble=new SpeechBubble(pet)){
            bubble.PreviewText(captions["click_magic_nyan.wav"]);Render(bubble,Path.Combine(runtime,"caption-plain-preview.png"));
        }
        PetTheme.Configure("adventure");
        File.WriteAllText(Path.Combine(runtime,"panels-self-test.json"),new JavaScriptSerializer().Serialize(new{
            passed=true,multi_bucket=true,unknown_not_zero=true,single_week_window=true,remaining_clamped=true,
            stale_cache=true,failure_retains_marked_cache=true,private_fields_excluded=true,
            completion_only_history=true,bilingual_captions=5,legacy_caption_filename=true,missing_caption_safe=true,screen_placements=placements,
            render_previews=6,real_desktop_input_test=false,live_quota_query=false,
            adventure_artwork_loaded=true,plain_theme=true,missing_artwork_fallback=true,
            fade_in_out=true,hover_restores_opacity=true,system_animation_preference=true,
            bubble_auto_hide_seconds=8,hover_pauses_countdown=true,loading_defers_countdown=true,
            loading_hold_bounded=true,reopen_resets_countdown=true,rounded_borderless_bubbles=true
        }),new UTF8Encoding(false));
    }
}
