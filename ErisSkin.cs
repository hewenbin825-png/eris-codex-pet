using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal static partial class PetTheme {
    internal static bool Adventure=true;
    static bool attempted;
    static Bitmap banner;
    internal static readonly Color Gold=Color.FromArgb(178,137,82);
    internal static void Configure(string theme){Adventure=theme!="plain";}
    internal static bool HasArtwork {get{LoadArtwork();return banner!=null;}}
    internal static Bitmap TryLoadArtwork(string path){
        try{
            if(!File.Exists(path)||new FileInfo(path).Length>16000000)return null;
            using(var image=Image.FromFile(path)){
                if(image.Width<500||image.Height<150||image.Width>5000||image.Height>5000)return null;
                return new Bitmap(image);
            }
        }catch{return null;}
    }
    static void LoadArtwork(){
        if(attempted)return;attempted=true;
        banner=TryLoadArtwork(Path.Combine(ErisVoice.Root,"bubble-eris-adventure.png"));
    }
    internal static GraphicsPath Rounded(RectangleF bounds,float radius){
        float d=Math.Min(radius*2,Math.Min(bounds.Width,bounds.Height));
        var p=new GraphicsPath();
        p.AddArc(bounds.Left,bounds.Top,d,d,180,90);p.AddArc(bounds.Right-d,bounds.Top,d,d,270,90);
        p.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90);p.AddArc(bounds.Left,bounds.Bottom-d,d,d,90,90);
        p.CloseFigure();return p;
    }
    internal static void PaintSkin(Graphics g,GraphicsPath shape,Size size){
        using(var paper=new SolidBrush(Paper))g.FillPath(paper,shape);
        if(Adventure){
            var saved=g.Save();g.SetClip(shape,CombineMode.Intersect);
            var body=new Rectangle(12,1,Math.Max(1,size.Width-25),Math.Max(1,size.Height-3));
            using(var wash=new LinearGradientBrush(body,Color.FromArgb(255,250,238),Color.FromArgb(247,236,215),90f))
                g.FillRectangle(wash,body);
            LoadArtwork();
            if(banner!=null){
                int h=(int)Math.Round(body.Width*(double)banner.Height/banner.Width);
                g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                g.DrawImage(banner,new Rectangle(body.X,body.Y,body.Width,h));
                // Fade only the lower seam. The character stays in the header;
                // neither her face nor the paper texture is stretched vertically.
                int fade=Math.Max(12,h/4);
                using(var blend=new LinearGradientBrush(new Rectangle(body.X,body.Y+h-fade,body.Width,fade+1),
                    Color.FromArgb(0,255,249,235),Color.FromArgb(255,255,249,235),90f)){
                    blend.WrapMode=WrapMode.TileFlipXY;
                    g.FillRectangle(blend,body.X,body.Y+h-fade,body.Width,fade+1);
                }
            }
            using(var pen=new Pen(Color.FromArgb(48,Gold),1)){
                g.DrawLine(pen,22,size.Height-58,22,Math.Max(120,size.Height-115));
                g.DrawLine(pen,size.Width-23,size.Height-58,size.Width-23,Math.Max(120,size.Height-115));
            }
            g.Restore(saved);
        }
        using(var edge=new Pen(Adventure?Gold:Accent,1.4f))g.DrawPath(edge,shape);
    }
    internal static double FadeOpacity(double age,double remaining,bool paused,bool animate){
        if(!animate)return 1;
        return Math.Max(.02,Math.Min(Math.Min(1,Math.Max(0,age)/160),paused?1:Math.Min(1,remaining/240)));
    }
    internal static bool PaintBehind(Control child,Graphics graphics){
        var form=child.FindForm() as PetBubble;if(form==null)return false;
        Point offset=Point.Empty;
        for(Control current=child;current!=null&&current!=form;current=current.Parent)
            offset.Offset(current.Left,current.Top);
        var state=graphics.Save();graphics.TranslateTransform(-offset.X,-offset.Y);
        form.PaintBubble(graphics);graphics.Restore(state);return true;
    }
}

internal sealed class PetButton:Button {
    bool hovering;
    internal PetButton(){
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|
            ControlStyles.SupportsTransparentBackColor,true);
        BackColor=Color.Transparent;FlatStyle=FlatStyle.Flat;
        MouseEnter+=delegate{hovering=true;Invalidate();};
        MouseLeave+=delegate{hovering=false;Invalidate();};
    }
    protected override void OnPaintBackground(PaintEventArgs e){
        if(!PetTheme.PaintBehind(this,e.Graphics))base.OnPaintBackground(e);
    }
    protected override void OnPaint(PaintEventArgs e){
        var g=e.Graphics;PetTheme.PaintBehind(this,g);g.SmoothingMode=SmoothingMode.AntiAlias;
        var bounds=new RectangleF(1,1,Width-3,Height-3);
        using(var p=PetTheme.Rounded(bounds,9))
        using(var fill=new SolidBrush(hovering&&Enabled?Color.FromArgb(245,229,204):Color.FromArgb(255,252,244)))
        using(var edge=new Pen(hovering?PetTheme.Gold:PetTheme.Line)){
            g.FillPath(fill,p);g.DrawPath(edge,p);
        }
        TextRenderer.DrawText(g,Text,Font,ClientRectangle,Enabled?PetTheme.Accent:PetTheme.Muted,
            TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine);
    }
}

internal sealed class ReminderRow:Panel {
    internal ReminderRow(string time,string text,bool latest){
        Height=55;Width=298;Margin=new Padding(0,0,0,7);BackColor=Color.Transparent;DoubleBuffered=true;
        var when=PetTheme.Label(time,8);when.ForeColor=PetTheme.Muted;when.SetBounds(24,7,260,17);
        var reply=PetTheme.Label(text,10,FontStyle.Bold);reply.SetBounds(24,25,260,24);reply.ForeColor=latest?PetTheme.Accent:PetTheme.Ink;
        when.Anchor=reply.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
        Controls.AddRange(new Control[]{when,reply});
    }
    protected override void OnPaint(PaintEventArgs e){
        base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        using(var p=PetTheme.Rounded(new RectangleF(0,0,Width-1,Height-1),10))
        using(var fill=new SolidBrush(Color.FromArgb(255,252,244)))using(var pen=new Pen(PetTheme.Line)){
            g.FillPath(fill,p);g.DrawPath(pen,p);
        }
        using(var dot=new SolidBrush(PetTheme.Gold))g.FillEllipse(dot,10,12,5,5);
    }
}
