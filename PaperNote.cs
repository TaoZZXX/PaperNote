using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PaperNote
{
    [DataContract]
    public class Paper
    {
        [DataMember] public string Title { get; set; }
        [DataMember] public string FilePath { get; set; }
        [DataMember] public string Status { get; set; }
        [DataMember] public List<Mark> Marks { get; set; }
        public override string ToString() { return Title; }
    }

    [DataContract]
    public class Mark
    {
        [DataMember] public int Page { get; set; }
        [DataMember] public string Kind { get; set; }
        [DataMember] public string Quote { get; set; }
        [DataMember] public string Note { get; set; }
        [DataMember] public string Created { get; set; }
        [DataMember] public float X { get; set; }
        [DataMember] public float Y { get; set; }
        [DataMember] public float W { get; set; }
        [DataMember] public float H { get; set; }
        [DataMember] public int ColorIndex { get; set; }
        [DataMember] public float CardX { get; set; }
        [DataMember] public float CardY { get; set; }
        [DataMember] public float CardW { get; set; }
        [DataMember] public float CardH { get; set; }
        [DataMember] public float CardDocW { get; set; }
        [DataMember] public float CardDocH { get; set; }
        [DataMember] public int CardPixelW { get; set; }
        [DataMember] public int CardPixelH { get; set; }
        [DataMember] public bool HasCardPosition { get; set; }
        public override string ToString()
        {
            return "第 " + Page + " 页  ·  " + Kind + (String.IsNullOrWhiteSpace(Quote) ? "" : "  “" + Quote + "”") + (String.IsNullOrWhiteSpace(Note) ? "" : "\r\n    " + Note);
        }
    }

    [DataContract]
    public class Library
    {
        [DataMember] public List<Paper> Papers { get; set; }
        public Library() { Papers = new List<Paper>(); }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form, IMessageFilter
    {
        const int PreviewDpi=240, FullDpi=450, MaxParallelRenders=8;
        const long LoadingBitmapBudget=4L*1024*1024*1024, IdleBitmapBudget=500L*1024*1024;
        Library library;
        Paper current;
        string dataFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaperNote", "library.json");
        ListBox papers = new ListBox();
        ListBox marks = new ListBox();
        TextBox pageBox = new TextBox();
        TextBox quoteBox = new TextBox();
        TextBox noteBox = new TextBox();
        ComboBox kindBox = new ComboBox();
        Label statusLabel = new Label();
        PdfCanvas pdfCanvas = new PdfCanvas();
        Label pageLabel = new Label();
        ComboBox paperPicker = new ComboBox();
        Panel pdfViewport = new Panel();
        Panel pdfPages;
        Dictionary<int, PdfCanvas> pageCanvases = new Dictionary<int, PdfCanvas>();
        Panel popup = new Panel();
        Button popupSave;
        Label popupHeading;
        Control readerSurface;
        ConnectorLayer editorConnector;
        Rectangle popupTargetBounds;
        PdfCanvas popupPage;
        float popupX,popupY,popupW,popupH;
        Dictionary<Mark,Panel> markCards = new Dictionary<Mark,Panel>();
        Dictionary<Mark,ConnectorLayer> markCardLines = new Dictionary<Mark,ConnectorLayer>();
        Dictionary<Mark,PdfCanvas> markCardPages = new Dictionary<Mark,PdfCanvas>();
        Dictionary<PdfCanvas,ConnectorLayer> pageConnectorLayers = new Dictionary<PdfCanvas,ConnectorLayer>();
        Dictionary<Mark,float> markCardScales = new Dictionary<Mark,float>();
        float zoomLevel = 1f;
        bool fitMode = true;
        bool resizingCanvas;
        int currentPage = 1;
        string renderRoot = Path.Combine(Path.GetTempPath(), "PaperNotePages");
        string selectedPdfText = "";
        PdfCanvas selectedPdfCanvas;
        Dictionary<string, Image> renderedPages = new Dictionary<string, Image>();
        Dictionary<string, int> renderedDpi = new Dictionary<string, int>();
        Dictionary<int, int> pageDpi = new Dictionary<int, int>();
        Dictionary<int, SizeF> pageReferenceSize = new Dictionary<int, SizeF>();
        Dictionary<string, bool> renderToCanvas = new Dictionary<string, bool>();
        Dictionary<string, List<WordBox>> textPages = new Dictionary<string, List<WordBox>>();
        HashSet<string> textLoadsInFlight = new HashSet<string>();
        object textPagesLock = new object();
        Dictionary<string, int> pageCounts = new Dictionary<string, int>();
        Queue<string> cacheOrder = new Queue<string>();
        int requestToken;
        volatile int activeRenderCount;
        DateTime lastViewportScroll=DateTime.MinValue;
        Timer wheelTimer;
        Timer scrollWatch;
        int pendingPage;
        HashSet<string> loadingPages = new HashSet<string>();
        public class WordBox { public float X1, Y1, X2, Y2; public string Text; }

        public MainForm()
        {
            Text = "PaperNote · 论文精读工作台"; Width = 2560; Height = 1440; MinimumSize = new Size(1100, 700); StartPosition = FormStartPosition.CenterScreen; WindowState = FormWindowState.Maximized; KeyPreview = true;
            BackColor = Color.FromArgb(255, 247, 214); Font = new Font("Segoe UI", 9F); LoadLibrary(); BuildUi(); RefreshPapers(); Application.AddMessageFilter(this); FormClosed+=delegate{Application.RemoveMessageFilter(this);};ApplyCpuAffinity();
        }

        void BuildUi()
        {
            var top = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Color.White, Padding = new Padding(20, 0, 20, 0) };
            var brand = new Label { Text = "PaperNote", Font = new Font("Segoe UI Semibold", 17F), ForeColor = Color.FromArgb(26, 115, 232), AutoSize = true, Location = new Point(20, 17) };
            paperPicker.DropDownStyle = ComboBoxStyle.DropDownList; paperPicker.Width = 420; paperPicker.Location = new Point(230, 13); paperPicker.SelectedIndexChanged += delegate { if (paperPicker.SelectedItem != null) { papers.SelectedItem = paperPicker.SelectedItem; } };
            var add = Button("＋ 导入 PDF", true); add.Anchor = AnchorStyles.Top | AnchorStyles.Right; add.Location = new Point(Width - 150, 12); add.Click += ImportPdf;
            top.Controls.Add(brand); top.Controls.Add(paperPicker); top.Controls.Add(add); top.Resize += delegate { add.Left = top.ClientSize.Width - add.Width - 18; };
            papers.SelectedIndexChanged += SelectPaper;

            var right = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(255, 247, 214), Padding = new Padding(0) };
            var reader = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0), BackColor = Color.FromArgb(255, 247, 214) }; readerSurface=reader;
            pageLabel = Label("第 0 / 0 页", 9, false); pageLabel.Visible = false;
            pdfViewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(251, 231, 214) };
            pdfCanvas.BackColor = Color.White; pdfCanvas.SizeMode = PictureBoxSizeMode.StretchImage;
            pdfPages = new Panel { Location=Point.Empty, BackColor=Color.FromArgb(251,231,214) };
            pdfViewport.Controls.Add(pdfPages); pdfViewport.Resize += delegate { ResizeCanvas(); }; pdfViewport.Scroll += ViewportScrolled; reader.Controls.Add(pdfViewport);
            BuildPopup(reader);
            pdfViewport.MouseWheel += ReaderWheel; reader.MouseWheel += ReaderWheel;
            right.Controls.Add(reader);

            /* annotation editor is a floating card shown only after selecting PDF content */
            /*
            kindBox.DropDownStyle = ComboBoxStyle.DropDownList; kindBox.Items.AddRange(new object[] { "高亮", "公式", "创新点", "实验", "疑问", "普通批注" }); kindBox.SelectedIndex = 0; kindBox.Dock = DockStyle.Left; kindBox.Width = 140;
            pageBox.Text = "1"; pageBox.Width = 80; pageBox.Dock = DockStyle.Left;
            var pagePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false }; pagePanel.Controls.Add(pageBox); pagePanel.Controls.Add(new Label { Text = "  页码", AutoSize = true, Padding = new Padding(4, 6, 0, 0) });
            quoteBox.Multiline = true; quoteBox.Dock = DockStyle.Fill; quoteBox.ScrollBars = ScrollBars.Vertical; quoteBox.PlaceholderTextCompat("选中的原文或摘录（可留空）");
            noteBox.Multiline = true; noteBox.Dock = DockStyle.Fill; noteBox.ScrollBars = ScrollBars.Vertical; noteBox.PlaceholderTextCompat("写下你的想法、问题或公式说明…");
            form.Controls.Add(Label("类型", 9, false), 0, 0); form.Controls.Add(kindBox, 1, 0); form.Controls.Add(Label("摘录", 9, false), 0, 1); form.Controls.Add(quoteBox, 1, 1); form.Controls.Add(Label("批注", 9, false), 0, 2); form.Controls.Add(noteBox, 1, 2);
            var save = Button("保存批注", true); save.Anchor = AnchorStyles.Right | AnchorStyles.Bottom; save.Width = 110; save.Height = 30; save.Click += SaveMark;
            form.Controls.Add(save, 1, 3); form.SetCellPosition(save, new TableLayoutPanelCellPosition(1, 3));
            entry.Controls.Add(form); entry.Controls.Add(annotationHead); work.Panel1.Controls.Add(entry);

            var markPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            var markHead = Label("批注与摘录", 11, true); markHead.Dock = DockStyle.Top; markHead.Height = 30;
            marks.Dock = DockStyle.Fill; marks.BorderStyle = BorderStyle.FixedSingle; marks.Font = new Font("Segoe UI", 9); marks.HorizontalScrollbar = true;
            var delete = Button("删除选中批注", false); delete.Dock = DockStyle.Bottom; delete.Height = 34; delete.Click += DeleteMark;
            markPanel.Controls.Add(marks); markPanel.Controls.Add(delete); markPanel.Controls.Add(markHead); work.Panel2.Controls.Add(markPanel);
            right.Controls.Add(work, 0, 2);
            */
            statusLabel = Label("本地保存已开启", 9, false); statusLabel.ForeColor = Color.Gray; statusLabel.Dock = DockStyle.Fill;
            Controls.Add(right);
            wheelTimer = new Timer(); wheelTimer.Interval = 160; wheelTimer.Tick += delegate { wheelTimer.Stop(); ShowPage(pendingPage); };
            scrollWatch = new Timer(); scrollWatch.Interval = 180; scrollWatch.Tick += delegate { EnsureVisiblePages(); TryAppendNextPage(); TryUpgradeVisiblePage(); }; scrollWatch.Start();
        }

        static Label Label(string text, float size, bool bold) { return new Label { Text = text, Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size), AutoSize = false, ForeColor = Color.FromArgb(57, 52, 79), TextAlign = ContentAlignment.MiddleLeft }; }
        static Button Button(string text, bool primary) { var b = new Button { Text = text, AutoSize = true, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(255, 190, 61) : Color.White, ForeColor = Color.FromArgb(57, 52, 79), Padding = new Padding(12, 4, 12, 4), Cursor = Cursors.Hand }; b.FlatAppearance.BorderColor = Color.FromArgb(246, 222, 193); b.FlatAppearance.BorderSize = primary ? 0 : 1; return b; }
        static GraphicsPath RoundedRect(Rectangle r,int radius) { var p=new GraphicsPath();int d=radius*2;p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p; }

        void BuildPopup(Control parent)
        {
            popup = new Panel { Visible = false, Width = 400, Height = 230, BackColor = Color.White, Padding = new Padding(18), BorderStyle = BorderStyle.None, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            popup.Resize += delegate { using(var path=RoundedRect(new Rectangle(0,0,popup.Width-1,popup.Height-1),22)) popup.Region=new Region(path); };
            popup.Paint += delegate(object s,PaintEventArgs e){using(var path=RoundedRect(new Rectangle(1,1,popup.Width-3,popup.Height-3),21))using(var pen=new Pen(Color.FromArgb(92,61,122),2)){e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.DrawPath(pen,path);}};
            popupHeading = new Label { Text = "添加批注", Font = new Font("Segoe UI Semibold", 12), AutoSize = true, Location = new Point(14, 10) };
            var close = new Button { Text = "×", FlatStyle = FlatStyle.Flat, Size = new Size(30, 28), Location = new Point(350, 8), BackColor = Color.White, ForeColor = Color.FromArgb(57, 52, 79) };
            close.Click += delegate { popup.Visible = false;if(editorConnector!=null)editorConnector.Visible=false; quoteBox.ReadOnly=false;noteBox.ReadOnly=false;kindBox.Enabled=true;popupSave.Visible=true; };
            kindBox.DropDownStyle = ComboBoxStyle.DropDownList; kindBox.Items.AddRange(new object[] { "高亮", "公式", "创新点", "实验", "疑问", "普通批注" }); kindBox.SelectedIndex = 0; kindBox.Location = new Point(14, 42); kindBox.Width = 160;
            quoteBox.Multiline = true; quoteBox.Visible=false; quoteBox.ScrollBars = ScrollBars.Vertical; quoteBox.BackColor = Color.FromArgb(255, 252, 244); quoteBox.PlaceholderTextCompat("选中的文字（可手动填写）");
            noteBox.Multiline = true; noteBox.Location = new Point(18, 82); noteBox.Size = new Size(364, 88); noteBox.ScrollBars = ScrollBars.Vertical; noteBox.BackColor = Color.FromArgb(255, 252, 244); noteBox.BorderStyle=BorderStyle.FixedSingle; noteBox.PlaceholderTextCompat("添加批注…"); noteBox.TextChanged += delegate { ResizePopupForText(); };
            popupSave = Button("保存", true); popupSave.Location = new Point(292, 184); popupSave.Width = 90; popupSave.Click += SaveMark;
            Point editDrag=Point.Empty;bool editMoving=false;popupHeading.Cursor=Cursors.SizeAll;popupHeading.MouseDown+=delegate(object s,MouseEventArgs e){if(e.Button==MouseButtons.Left){editMoving=true;Point p=popup.Parent.PointToClient(Cursor.Position);editDrag=new Point(p.X-popup.Left,p.Y-popup.Top);}};popupHeading.MouseMove+=delegate(object s,MouseEventArgs e){if(editMoving){Point p=popup.Parent.PointToClient(Cursor.Position);popup.Location=new Point(Math.Max(0,Math.Min(popup.Parent.ClientSize.Width-popup.Width,p.X-editDrag.X)),Math.Max(0,Math.Min(popup.Parent.ClientSize.Height-popup.Height,p.Y-editDrag.Y)));UpdateEditorConnector();}};popupHeading.MouseUp+=delegate{editMoving=false;};
            popup.Controls.Add(popupHeading); popup.Controls.Add(close); popup.Controls.Add(kindBox); popup.Controls.Add(noteBox); popup.Controls.Add(popupSave); parent.Controls.Add(popup); popup.BringToFront();
        }
        void PlacePopup(PdfCanvas page,Rectangle target)
        {
            if(popup.Parent!=page){if(popup.Parent!=null)popup.Parent.Controls.Remove(popup);page.Controls.Add(popup);}if(editorConnector==null)editorConnector=new ConnectorLayer{Dock=DockStyle.Fill};if(editorConnector.Parent!=page){if(editorConnector.Parent!=null)editorConnector.Parent.Controls.Remove(editorConnector);page.Controls.Add(editorConnector);}
            popupPage=page;
            popupTargetBounds=target;int gap=22,x,y;if(target.Right+gap+popup.Width<=page.ClientSize.Width-8){x=target.Right+gap;y=target.Top+(target.Height-popup.Height)/2;}else if(target.Left-gap-popup.Width>=8){x=target.Left-gap-popup.Width;y=target.Top+(target.Height-popup.Height)/2;}else if(target.Bottom+gap+popup.Height<=page.ClientSize.Height-8){x=target.Left+(target.Width-popup.Width)/2;y=target.Bottom+gap;}else{x=target.Left+(target.Width-popup.Width)/2;y=target.Top-gap-popup.Height;}x=Math.Max(4,Math.Min(x,page.ClientSize.Width-popup.Width-4));y=Math.Max(4,Math.Min(y,page.ClientSize.Height-popup.Height-4));popup.Location=new Point(x,y);UpdateEditorConnector();editorConnector.BringToFront();popup.BringToFront();
        }
        void UpdateEditorConnector(){if(editorConnector==null||popupPage==null||!popup.Visible)return;Point start=new Point(popupTargetBounds.Right,popupTargetBounds.Top+popupTargetBounds.Height/2);if(popup.Left<popupTargetBounds.Left)start.X=popupTargetBounds.Left;else if(popup.Top>popupTargetBounds.Bottom)start=new Point(popupTargetBounds.Left+popupTargetBounds.Width/2,popupTargetBounds.Bottom);else if(popup.Bottom<popupTargetBounds.Top)start=new Point(popupTargetBounds.Left+popupTargetBounds.Width/2,popupTargetBounds.Top);Point end=new Point(Math.Max(popup.Left,Math.Min(start.X,popup.Right)),Math.Max(popup.Top,Math.Min(start.Y,popup.Bottom)));if(popup.Bounds.Contains(start)){end=popup.Left>=popupTargetBounds.Right?new Point(popup.Left,popup.Top+popup.Height/2):popup.Top>=popupTargetBounds.Bottom?new Point(popup.Left+popup.Width/2,popup.Top):new Point(popup.Right,popup.Top+popup.Height/2);}editorConnector.Start=start;editorConnector.End=end;editorConnector.Visible=true;editorConnector.Invalidate();}
        void ShowAnnotationPopup(PdfCanvas canvas,float x,float y,float w,float h) { popupHeading.Text="添加批注";quoteBox.Text=selectedPdfText;quoteBox.ReadOnly=false;noteBox.ReadOnly=false;kindBox.Enabled=true;popupSave.Visible=true;popup.Visible=true;popupX=x;popupY=y;popupW=w;popupH=h;PlacePopup(canvas,canvas.AnchorBounds(x,y,w,h));noteBox.Focus(); }
        float CanvasScale(PdfCanvas canvas)
        {
            if (canvas == null || canvas.Image == null || canvas.Image.Width <= 0) return 1f;
            return Math.Max(0.001f, (float)canvas.ClientSize.Width / canvas.Image.Width);
        }
        bool HasSavedCardPosition(Mark mark)
        {
            if(mark.HasCardPosition)return true;
            if(mark.CardX>0||mark.CardY>0){mark.HasCardPosition=true;return true;}
            return false;
        }
        void RaiseCalloutLayers(PdfCanvas canvas)
        {
            ConnectorLayer layer;if(pageConnectorLayers.TryGetValue(canvas,out layer))layer.BringToFront();
            foreach(KeyValuePair<Mark,Panel> entry in markCards){PdfCanvas page;if(markCardPages.TryGetValue(entry.Key,out page)&&page==canvas)entry.Value.BringToFront();}
        }
        void RemoveMarkConnector(Mark mark,PdfCanvas canvas)
        {
            ConnectorLayer line;if(!markCardLines.TryGetValue(mark,out line))return;
            line.RemoveSegment(mark);markCardLines.Remove(mark);
            if(line.SegmentCount==0){canvas.Controls.Remove(line);line.Dispose();pageConnectorLayers.Remove(canvas);}
        }
        Point FindCalloutLocation(PdfCanvas canvas,Mark mark,Rectangle target,int width,int height,float scale)
        {
            int gap=Math.Max(6,(int)Math.Round(18*scale));
            int maxX=Math.Max(4,canvas.ClientSize.Width-width-4),maxY=Math.Max(4,canvas.ClientSize.Height-height-4);
            int cx=target.Left+(target.Width-width)/2,cy=target.Top+(target.Height-height)/2;
            var candidates=new List<Point>();
            Action<int,int> add=delegate(int x,int y){candidates.Add(new Point(Math.Max(4,Math.Min(x,maxX)),Math.Max(4,Math.Min(y,maxY))));};
            for(int i=0;i<10;i++)
            {
                int offset=i==0?0:((i+1)/2)*((i%2==1)?1:-1);
                int dy=offset*(height+gap),dx=offset*(width+gap);
                add(target.Right+gap,cy+dy);add(target.Left-width-gap,cy+dy);
                add(cx+dx,target.Bottom+gap);add(cx+dx,target.Top-height-gap);
            }
            Rectangle best=Rectangle.Empty;double bestScore=Double.MaxValue;
            foreach(Point candidate in candidates)
            {
                Rectangle r=new Rectangle(candidate.X,candidate.Y,width,height);double overlap=0;
                foreach(KeyValuePair<Mark,Panel> entry in markCards)
                {
                    PdfCanvas page;if(entry.Key==mark||!entry.Value.Visible||!markCardPages.TryGetValue(entry.Key,out page)||page!=canvas)continue;
                    Rectangle intersection=Rectangle.Intersect(r,entry.Value.Bounds);overlap+=(double)intersection.Width*intersection.Height;
                }
                double distance=Math.Abs(candidate.X-cx)+Math.Abs(candidate.Y-cy);
                double score=overlap*1000+distance;
                if(score<bestScore){bestScore=score;best=r;}
                if(overlap==0)break;
            }
            return best.IsEmpty?new Point(4,4):best.Location;
        }
        void RepositionPageCallouts(PdfCanvas canvas)
        {
            if(popup.Visible&&popupPage==canvas)PlacePopup(canvas,canvas.AnchorBounds(popupX,popupY,popupW,popupH));
            foreach(Mark m in new List<Mark>(markCards.Keys))
            {
                PdfCanvas p;
                if(!markCardPages.TryGetValue(m,out p)||p!=canvas)continue;
                Panel card=markCards[m];
                float scale=CanvasScale(canvas),last;
                if(markCardScales.TryGetValue(m,out last)&&last>0)
                {
                    float ratio=scale/last;
                    if(Math.Abs(ratio-1f)>0.001f)card.Scale(new SizeF(ratio,ratio));
                }
                markCardScales[m]=scale;
                if(m.CardDocW<=0)m.CardDocW=(float)card.Width/scale;
                if(m.CardDocH<=0)m.CardDocH=(float)card.Height/scale;
                card.Width=Math.Max(1,(int)Math.Round(m.CardDocW*scale));
                card.Height=Math.Max(1,(int)Math.Round(m.CardDocH*scale));
                TextBox text=card.Controls.OfType<TextBox>().FirstOrDefault();
                if(text!=null){text.Width=Math.Max(1,card.Width-(int)Math.Round(36*scale));text.Height=Math.Max(1,card.Height-text.Top-(int)Math.Round(18*scale));}
                foreach(Label label in card.Controls.OfType<Label>())
                    if(!label.Text.EndsWith("· 批注"))label.Width=Math.Max(1,card.Width-(int)Math.Round(40*scale));
                Rectangle target=canvas.AnchorBounds(m.X,m.Y,m.W,m.H);
                bool positioned=HasSavedCardPosition(m);
                Point auto=positioned?Point.Empty:FindCalloutLocation(canvas,m,target,card.Width,card.Height,scale);
                int x=positioned?(int)(m.CardX*canvas.ClientSize.Width):auto.X;
                int y=positioned?(int)(m.CardY*canvas.ClientSize.Height):auto.Y;
                x=Math.Max(4,Math.Min(x,canvas.ClientSize.Width-card.Width-4));
                y=Math.Max(4,Math.Min(y,canvas.ClientSize.Height-card.Height-4));
                card.Location=new Point(x,y);m.CardX=(float)x/Math.Max(1,canvas.ClientSize.Width);m.CardY=(float)y/Math.Max(1,canvas.ClientSize.Height);
                UpdateMarkConnector(m,target,card,markCardLines[m]);
            }
        }
        void ShowMarkPopup(Mark mark,PdfCanvas canvas)
        {
            Panel existing;if(markCards.TryGetValue(mark,out existing)){existing.BringToFront();return;}
            Rectangle target=canvas.AnchorBounds(mark.X,mark.Y,mark.W,mark.H);string body=mark.Note??"";
            float scale=CanvasScale(canvas);int lines=Math.Max(2,body.Split('\n').Length+body.Length/42);int height=mark.CardDocH>0?(int)(mark.CardDocH*scale):112+lines*22;int cardWidth=mark.CardDocW>0?(int)(mark.CardDocW*scale):360;
            if(mark.CardDocW<=0&&mark.CardPixelW>0)cardWidth=mark.CardPixelW;
            if(mark.CardDocH<=0&&mark.CardPixelH>0)height=mark.CardPixelH;
            if(mark.CardDocW<=0&&mark.CardW>0)cardWidth=(int)(mark.CardW*canvas.ClientSize.Width);
            if(mark.CardDocH<=0&&mark.CardH>0)height=(int)(mark.CardH*canvas.ClientSize.Height);
            var card=new BufferedPanel{Width=cardWidth,Height=height,BackColor=Color.FromArgb(205,255,255,255),BorderStyle=BorderStyle.None};card.Resize+=delegate{float s=CanvasScale(canvas);using(var p=RoundedRect(new Rectangle(0,0,card.Width-1,card.Height-1),Math.Max(3,(int)Math.Round(32*s))))card.Region=new Region(p);};card.Paint+=delegate(object s,PaintEventArgs e){float z=CanvasScale(canvas);using(var p=RoundedRect(new Rectangle(1,1,card.Width-3,card.Height-3),Math.Max(3,(int)Math.Round(31*z))))using(var fill=new SolidBrush(Color.FromArgb(205,255,255,255)))using(var pen=new Pen(Color.FromArgb(205,92,61,122),Math.Max(0.7f,1.8f*z))){e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.FillPath(fill,p);e.Graphics.DrawPath(pen,p);}};
            var head=new Label{Text=mark.Kind+" · 批注",Font=new Font("Segoe UI Semibold",12),ForeColor=Color.FromArgb(57,52,79),Location=new Point(18,12),AutoSize=true};var close=new Button{Text="×",FlatStyle=FlatStyle.Flat,Size=new Size(30,28),Location=new Point(cardWidth-48,8),Anchor=AnchorStyles.Top|AnchorStyles.Right,BackColor=Color.White,ForeColor=Color.FromArgb(57,52,79)};
            bool positioned=HasSavedCardPosition(mark);Point auto=positioned?Point.Empty:FindCalloutLocation(canvas,mark,target,cardWidth,height,scale);int x=positioned?(int)(mark.CardX*canvas.ClientSize.Width):auto.X,y=positioned?(int)(mark.CardY*canvas.ClientSize.Height):auto.Y;x=Math.Max(4,Math.Min(x,canvas.ClientSize.Width-card.Width-4));y=Math.Max(4,Math.Min(y,canvas.ClientSize.Height-card.Height-4));mark.CardX=(float)x/Math.Max(1,canvas.ClientSize.Width);mark.CardY=(float)y/Math.Max(1,canvas.ClientSize.Height);mark.HasCardPosition=true;mark.CardDocW=(float)card.Width/scale;mark.CardDocH=(float)card.Height/scale;markCardScales[mark]=scale;
            int textTop=50;
            var text=new TextBox{Multiline=true,ReadOnly=false,ScrollBars=ScrollBars.None,BorderStyle=BorderStyle.FixedSingle,BackColor=Color.FromArgb(255,252,244),Location=new Point(18,textTop),Size=new Size(card.Width-36,Math.Max(54,card.Height-textTop-18)),Text=body};
            var emptyTimer=new Timer{Interval=650};emptyTimer.Tick+=delegate{emptyTimer.Stop();if(!text.IsDisposed&&String.IsNullOrWhiteSpace(text.Text)){Paper owner=null;foreach(Paper p in library.Papers)if(p.Marks!=null&&p.Marks.Contains(mark)){owner=p;break;}if(owner!=null)owner.Marks.Remove(mark);canvas.Invalidate();canvas.Controls.Remove(card);RemoveMarkConnector(mark,canvas);markCards.Remove(mark);markCardPages.Remove(mark);markCardScales.Remove(mark);card.Dispose();SaveLibrary();}};
            text.TextChanged+=delegate{Rectangle old=card.Bounds;mark.Note=text.Text;float s=CanvasScale(canvas);int n=Math.Max(2,text.Text.Split('\n').Length+text.Text.Length/42);text.Height=Math.Max(text.Height,(int)Math.Round(n*22*s+12*s));card.Height=text.Bottom+(int)Math.Round(18*s);mark.CardDocH=(float)card.Height/s;using(var rp=RoundedRect(new Rectangle(0,0,card.Width-1,card.Height-1),Math.Max(4,(int)Math.Round(32*s))))card.Region=new Region(rp);card.Invalidate();canvas.Invalidate(Rectangle.Inflate(old,8,8));canvas.Invalidate(Rectangle.Inflate(card.Bounds,8,8));UpdateMarkConnector(mark,target,card,markCardLines[mark]);SaveLibrary();if(String.IsNullOrWhiteSpace(text.Text))emptyTimer.Start();else emptyTimer.Stop();};
            card.Location=new Point(x,y);
            Point dragOffset=Point.Empty;bool moving=false;MouseEventHandler down=delegate(object s,MouseEventArgs e){if(e.Button==MouseButtons.Left){moving=true;Point p=canvas.PointToClient(Cursor.Position);dragOffset=new Point(p.X-card.Left,p.Y-card.Top);}};MouseEventHandler move=delegate(object s,MouseEventArgs e){if(!moving)return;Rectangle old=card.Bounds;Point p=canvas.PointToClient(Cursor.Position);card.Left=Math.Max(0,Math.Min(canvas.ClientSize.Width-card.Width,p.X-dragOffset.X));card.Top=Math.Max(0,Math.Min(canvas.ClientSize.Height-card.Height,p.Y-dragOffset.Y));mark.CardX=(float)card.Left/Math.Max(1,canvas.ClientSize.Width);mark.CardY=(float)card.Top/Math.Max(1,canvas.ClientSize.Height);mark.HasCardPosition=true;canvas.Invalidate(Rectangle.Inflate(old,8,8));canvas.Invalidate(Rectangle.Inflate(card.Bounds,8,8));UpdateMarkConnector(mark,target,card,markCardLines[mark]);};MouseEventHandler up=delegate(object s,MouseEventArgs e){if(moving){moving=false;canvas.Invalidate();SaveLibrary();}};head.Cursor=Cursors.SizeAll;head.MouseDown+=down;head.MouseMove+=move;head.MouseUp+=up;card.MouseWheel+=ReaderWheel;text.MouseWheel+=ReaderWheel;head.MouseWheel+=ReaderWheel;
            ConnectorLayer line;if(!pageConnectorLayers.TryGetValue(canvas,out line)){line=new ConnectorLayer{Dock=DockStyle.Fill};canvas.Controls.Add(line);pageConnectorLayers[canvas]=line;}
            markCardLines[mark]=line;UpdateMarkConnector(mark,target,card,line);AddResizeHandlers(card,text,canvas,mark);close.Click+=delegate{canvas.Controls.Remove(card);card.Dispose();RemoveMarkConnector(mark,canvas);markCards.Remove(mark);markCardPages.Remove(mark);markCardScales.Remove(mark);SaveLibrary();};card.Controls.Add(head);card.Controls.Add(close);text.HandleCreated+=delegate{NativeTransparency.SetAlpha(text.Handle,245);};card.Controls.Add(text);if(text.IsHandleCreated)NativeTransparency.SetAlpha(text.Handle,245);canvas.Controls.Add(card);markCards[mark]=card;markCardPages[mark]=canvas;RaiseCalloutLayers(canvas);SaveLibrary();
        }
        void UpdateMarkConnector(Mark mark,Rectangle target,Panel card,ConnectorLayer line)
        {
            int sx,sy,ex,ey;Point tc=new Point(target.Left+target.Width/2,target.Top+target.Height/2),cc=new Point(card.Left+card.Width/2,card.Top+card.Height/2);
            if(Math.Abs(cc.X-tc.X)>=Math.Abs(cc.Y-tc.Y)){if(cc.X>=tc.X){sx=target.Right;sy=tc.Y;ex=card.Left;ey=Math.Max(card.Top,Math.Min(sy,card.Bottom));}else{sx=target.Left;sy=tc.Y;ex=card.Right;ey=Math.Max(card.Top,Math.Min(sy,card.Bottom));}}
            else if(cc.Y>=tc.Y){sx=tc.X;sy=target.Bottom;ex=Math.Max(card.Left,Math.Min(sx,card.Right));ey=card.Top;}else{sx=tc.X;sy=target.Top;ex=Math.Max(card.Left,Math.Min(sx,card.Right));ey=card.Bottom;}
            line.SetSegment(mark,new Point(sx,sy),new Point(ex,ey));line.Visible=true;
        }
        void AddResizeHandlers(Panel card,TextBox text,PdfCanvas canvas,Mark mark)
        {
            int edges=0;Point origin=Point.Empty;Rectangle start=Rectangle.Empty;bool resizing=false;
            Func<Point,int> hit=delegate(Point p){int e=0;const int edgeGrip=8,cornerGrip=24;int gx=(p.Y<cornerGrip||p.Y>card.Height-cornerGrip)?cornerGrip:edgeGrip;int gy=(p.X<cornerGrip||p.X>card.Width-cornerGrip)?cornerGrip:edgeGrip;if(p.X<gx)e|=1;else if(p.X>card.Width-gx)e|=2;if(p.Y<gy)e|=4;else if(p.Y>card.Height-gy)e|=8;return e;};
            Action<MouseEventArgs> begin=delegate(MouseEventArgs e){if(e.Button!=MouseButtons.Left)return;Point p=canvas.PointToClient(Cursor.Position);edges=hit(new Point(p.X-card.Left,p.Y-card.Top));if(edges!=0){resizing=true;origin=p;start=card.Bounds;}};
            Action<MouseEventArgs> update=delegate(MouseEventArgs e){Point p=canvas.PointToClient(Cursor.Position);int edge=resizing?edges:hit(new Point(p.X-card.Left,p.Y-card.Top));if(!resizing){card.Cursor=(edge==5||edge==10)?Cursors.SizeNWSE:(edge==6||edge==9)?Cursors.SizeNESW:(edge==1||edge==2)?Cursors.SizeWE:(edge==4||edge==8)?Cursors.SizeNS:Cursors.Default;return;}Rectangle old=card.Bounds;float scale=CanvasScale(canvas);int minW=Math.Max(40,(int)Math.Round(220*scale)),minH=Math.Max(28,(int)Math.Round(100*scale));int dx=p.X-origin.X,dy=p.Y-origin.Y;int left=start.Left,top=start.Top,width=start.Width,height=start.Height;if((edges&1)!=0){left=Math.Min(start.Right-minW,start.Left+dx);width=start.Right-left;}if((edges&2)!=0)width=Math.Max(minW,start.Width+dx);if((edges&4)!=0){top=Math.Min(start.Bottom-minH,start.Top+dy);height=start.Bottom-top;}if((edges&8)!=0)height=Math.Max(minH,start.Height+dy);width=Math.Min(width,canvas.ClientSize.Width);height=Math.Min(height,canvas.ClientSize.Height);left=Math.Max(0,Math.Min(left,canvas.ClientSize.Width-width));top=Math.Max(0,Math.Min(top,canvas.ClientSize.Height-height));card.Bounds=new Rectangle(left,top,width,height);text.Width=Math.Max(1,card.Width-(int)Math.Round(36*scale));text.Height=Math.Max((int)Math.Round(24*scale),card.Height-text.Top-(int)Math.Round(18*scale));mark.CardX=(float)left/Math.Max(1,canvas.ClientSize.Width);mark.CardY=(float)top/Math.Max(1,canvas.ClientSize.Height);mark.HasCardPosition=true;mark.CardDocW=(float)width/scale;mark.CardDocH=(float)height/scale;canvas.Invalidate(Rectangle.Inflate(old,8,8));canvas.Invalidate(Rectangle.Inflate(card.Bounds,8,8));UpdateMarkConnector(mark,canvas.AnchorBounds(mark.X,mark.Y,mark.W,mark.H),card,markCardLines[mark]);card.Invalidate();};
            MouseEventHandler down=delegate(object s,MouseEventArgs e){begin(e);};MouseEventHandler move=delegate(object s,MouseEventArgs e){update(e);};MouseEventHandler up=delegate(object s,MouseEventArgs e){if(resizing){resizing=false;SaveLibrary();}};
            card.MouseDown+=down;card.MouseMove+=move;card.MouseUp+=up;text.MouseDown+=down;text.MouseMove+=move;text.MouseUp+=up;
        }
        List<WordBox> GetPageWords(string file,int pg)
        {
            string key=file+"|"+pg;
            lock(textPagesLock)
            {
                List<WordBox> cached;if(textPages.TryGetValue(key,out cached))return cached;
                string textExe=PdfTextExecutable(),path=Path.Combine(renderRoot,"selection-"+Guid.NewGuid().ToString("N")+".html");Directory.CreateDirectory(renderRoot);
                RunTool(textExe,"-f "+pg+" -l "+pg+" -bbox-layout "+Q(file)+" "+Q(path));
                var xml=new XmlDocument{XmlResolver=null};var xmlSettings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Ignore,XmlResolver=null};using(var reader=XmlReader.Create(path,xmlSettings))xml.Load(reader);XmlNode page=xml.SelectSingleNode("//*[local-name()='page']");if(page==null)return new List<WordBox>();
                float pw=Single.Parse(page.Attributes["width"].Value,CultureInfo.InvariantCulture),ph=Single.Parse(page.Attributes["height"].Value,CultureInfo.InvariantCulture);
                var words=new List<WordBox>();foreach(XmlNode word in xml.SelectNodes("//*[local-name()='word']"))words.Add(new WordBox{X1=Single.Parse(word.Attributes["xMin"].Value,CultureInfo.InvariantCulture)/pw,Y1=Single.Parse(word.Attributes["yMin"].Value,CultureInfo.InvariantCulture)/ph,X2=Single.Parse(word.Attributes["xMax"].Value,CultureInfo.InvariantCulture)/pw,Y2=Single.Parse(word.Attributes["yMax"].Value,CultureInfo.InvariantCulture)/ph,Text=word.InnerText});
                textPages[key]=words;return words;
            }
        }
        void LoadPageTextLayout(string file,int pg,PdfCanvas canvas)
        {
            string key=file+"|"+pg;List<WordBox> words;
            lock(textPagesLock){if(textPages.TryGetValue(key,out words)){canvas.SetTextLayout(words);return;}if(!textLoadsInFlight.Add(key))return;}
            var worker=new BackgroundWorker();worker.DoWork+=delegate(object s,DoWorkEventArgs e){e.Result=GetPageWords(file,pg);};
            worker.RunWorkerCompleted+=delegate(object s,RunWorkerCompletedEventArgs e){lock(textPagesLock)textLoadsInFlight.Remove(key);if(IsDisposed)return;if(e.Error!=null){if(statusLabel!=null)statusLabel.Text="PDF文字读取失败，当前页无法选择复制";return;}PdfCanvas active;if(pageCanvases.TryGetValue(pg,out active)&&current!=null&&current.FilePath==file)active.SetTextLayout((List<WordBox>)e.Result);};worker.RunWorkerAsync();
        }
        void ResizePopupForText()
        {
            if (popup == null) return; int lines = Math.Max(3, Math.Min(12, noteBox.Text.Split('\n').Length + noteBox.Text.Length / 48)); int h = 28 + lines * 20; noteBox.Height = h; popupSave.Top = noteBox.Bottom + 10; popup.Height = popupSave.Bottom + 12; if (popup.Visible) { using(var path=RoundedRect(new Rectangle(0,0,popup.Width-1,popup.Height-1),22))popup.Region=new Region(path);popup.Invalidate();UpdateEditorConnector();popup.BringToFront(); }
        }
        void ReaderWheel(object sender, MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                Point cursor=pdfViewport.PointToClient(Cursor.Position); Point oldScroll=pdfViewport.AutoScrollPosition; float oldScale=fitMode?FitScale():zoomLevel;
                float factor=e.Delta>0?1.12f:1f/1.12f; float next=Math.Max(0.08f,Math.Min(4f,oldScale*factor));
                float anchorX=cursor.X-oldScroll.X, anchorY=cursor.Y-oldScroll.Y; fitMode=false; zoomLevel=next; ResizeCanvas();
                int sx=Math.Max(0,(int)(anchorX*(next/oldScale)-cursor.X)), sy=Math.Max(0,(int)(anchorY*(next/oldScale)-cursor.Y)); pdfViewport.AutoScrollPosition=new Point(sx,sy);
                return;
            }
            if((ModifierKeys&Keys.Shift)==Keys.Shift)ScrollHorizontal(-e.Delta);
        }
        bool ScrollHorizontal(int delta)
        {
            int max=Math.Max(0,pdfViewport.AutoScrollMinSize.Width-pdfViewport.ClientSize.Width);if(max==0||delta==0)return false;
            int pixels=Math.Abs(delta)>=120?(delta/120)*90:delta*2;
            int x=Math.Max(0,Math.Min(max,-pdfViewport.AutoScrollPosition.X+pixels));
            int y=Math.Abs(pdfViewport.AutoScrollPosition.Y);
            pdfViewport.AutoScrollPosition=new Point(x,y);return true;
        }
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_KEYDOWN=0x0100,WM_MOUSEWHEEL=0x020A,WM_MOUSEHWHEEL=0x020E;
            if(m.Msg==WM_KEYDOWN&&(int)m.WParam== (int)Keys.C&&(Control.ModifierKeys&Keys.Control)==Keys.Control&&!String.IsNullOrWhiteSpace(selectedPdfText))
            {
                bool editorSelection=(noteBox.Focused&&noteBox.SelectionLength>0)||(quoteBox.Focused&&quoteBox.SelectionLength>0);
                if(!editorSelection)foreach(Panel card in markCards.Values){TextBox t=card.Controls.OfType<TextBox>().FirstOrDefault();if(t!=null&&t.Focused&&t.SelectionLength>0){editorSelection=true;break;}}
                if(!editorSelection){try{Clipboard.SetText(selectedPdfText);statusLabel.Text="所选 PDF 文字已复制";}catch(Exception ex){statusLabel.Text="复制失败："+ex.Message;}return true;}
            }
            if((m.Msg!=WM_MOUSEHWHEEL&&m.Msg!=WM_MOUSEWHEEL)||pdfViewport==null||!pdfViewport.IsHandleCreated)return false;
            if(!pdfViewport.RectangleToScreen(pdfViewport.ClientRectangle).Contains(Cursor.Position))return false;
            int delta=(short)(((long)m.WParam>>16)&0xffff);
            if(m.Msg==WM_MOUSEHWHEEL)return ScrollHorizontal(delta);
            if((Control.ModifierKeys&Keys.Shift)==Keys.Shift)return ScrollHorizontal(-delta);
            return false;
        }
        int PageDpi(int page){int dpi;return pageDpi.TryGetValue(page,out dpi)?dpi:FullDpi;}
        float FitScale()
        {
            Image img=pdfCanvas==null?null:pdfCanvas.Image;int page=currentPage;
            if(img==null)foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases.OrderBy(delegate(KeyValuePair<int,PdfCanvas> e){return e.Key;}))if(entry.Value.Image!=null){img=entry.Value.Image;page=entry.Key;break;}
            SizeF reference;if(pageReferenceSize.TryGetValue(page,out reference))return Math.Max(0.01f,(float)(pdfViewport.ClientSize.Width-48)/Math.Max(1,reference.Width));
            if(img==null)return 1f;float referenceWidth=img.Width*(FullDpi/(float)PageDpi(page));return Math.Max(0.01f,(float)(pdfViewport.ClientSize.Width-48)/referenceWidth);
        }
        void ViewportScrolled(object sender, ScrollEventArgs e) { if(resizingCanvas)return;lastViewportScroll=DateTime.UtcNow;EnsureVisiblePages();TryAppendNextPage(); }
        void TryAppendNextPage() { if(resizingCanvas||current==null||pdfViewport.ClientSize.Height<1)return;int total;if(!pageCounts.TryGetValue(current.FilePath,out total))return;int max=0;foreach(int p in pageCanvases.Keys)if(p>max)max=p;if(max>=total||loadingPages.Contains(current.FilePath+"|"+(max+1)+"|"+requestToken+"|"+PreviewDpi))return;int bottom=Math.Max(0,pdfPages.Height-pdfViewport.ClientSize.Height);int pos=Math.Abs(pdfViewport.AutoScrollPosition.Y);int prefetchDistance=Math.Max(1200,pdfViewport.ClientSize.Height);if(bottom==0||pos>=bottom-prefetchDistance)ShowPage(max+1); }
        void EnsureVisiblePages()
        {
            if(current==null||pdfViewport.ClientSize.Height<1)return;int top=Math.Max(0,-pdfViewport.AutoScrollPosition.Y),bottom=top+pdfViewport.ClientSize.Height;
            foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases.OrderBy(delegate(KeyValuePair<int,PdfCanvas> e){return e.Key;}))if(entry.Value.Image==null&&pageReferenceSize.ContainsKey(entry.Key)&&entry.Value.Bottom>top&&entry.Value.Top<bottom)ShowPage(entry.Key);
        }
        void PrefetchNextBatch(int firstPage)
        {
            if(current==null)return;int total;if(!pageCounts.TryGetValue(current.FilePath,out total))return;int started=0;
            for(int page=firstPage;page<=total&&started<MaxParallelRenders;page++)
            {
                string key=current.FilePath+"|"+page;if(renderedPages.ContainsKey(key))continue;PdfCanvas existing;if(pageCanvases.TryGetValue(page,out existing)&&existing.Image!=null)continue;
                if(StartPageRender(current.FilePath,page,PreviewDpi,requestToken,total,false))started++;else if(activeRenderCount>=MaxParallelRenders)break;
            }
        }
        long CacheEvictionPriority(string key,int top,int bottom,int visiblePage)
        {
            int split=key.LastIndexOf('|'),page;
            if(split<0||!Int32.TryParse(key.Substring(split+1),out page))return Int64.MaxValue/2;
            string file=key.Substring(0,split);
            if(current==null||!String.Equals(file,current.FilePath,StringComparison.OrdinalIgnoreCase))return Int64.MaxValue/2;
            PdfCanvas canvas;
            if(pageCanvases.TryGetValue(page,out canvas))
            {
                if(canvas.Bottom>top&&canvas.Top<bottom)return Int64.MinValue;
                long distance=canvas.Bottom<=top?(long)top-canvas.Bottom:(long)canvas.Top-bottom;
                return Math.Max(1,distance);
            }
            return 1000L+Math.Abs((long)page-visiblePage)*1200L;
        }
        void TrimPageCache(long byteBudget)
        {
            long bytes=0;
            foreach(Image image in renderedPages.Values)if(image!=null)bytes+=(long)image.Width*image.Height*4L;
            if(bytes<=byteBudget)return;

            int top=Math.Max(0,-pdfViewport.AutoScrollPosition.Y),bottom=top+pdfViewport.ClientSize.Height,visiblePage=currentPage;
            foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases.OrderBy(delegate(KeyValuePair<int,PdfCanvas> e){return e.Key;}))
                if(entry.Value.Bottom>top){visiblePage=entry.Key;break;}
            var keys=new List<string>(renderedPages.Keys);
            keys.Sort(delegate(string a,string b){return CacheEvictionPriority(b,top,bottom,visiblePage).CompareTo(CacheEvictionPriority(a,top,bottom,visiblePage));});
            foreach(string key in keys)
            {
                if(bytes<=byteBudget)break;
                int split=key.LastIndexOf('|'),page;if(split<0||!Int32.TryParse(key.Substring(split+1),out page))continue;
                string file=key.Substring(0,split);PdfCanvas canvas;
                if(current!=null&&String.Equals(file,current.FilePath,StringComparison.OrdinalIgnoreCase)&&pageCanvases.TryGetValue(page,out canvas)&&canvas.Bottom>top&&canvas.Top<bottom)continue;
                Image image;if(!renderedPages.TryGetValue(key,out image)||image==null)continue;
                if(current!=null&&String.Equals(file,current.FilePath,StringComparison.OrdinalIgnoreCase)&&pageCanvases.TryGetValue(page,out canvas)&&canvas.Image==image)
                {
                    canvas.Image=null;canvas.Invalidate();pageDpi.Remove(page);
                }
                renderedPages.Remove(key);renderedDpi.Remove(key);bytes-=(long)image.Width*image.Height*4L;image.Dispose();
            }
        }
        void TryUpgradeVisiblePage()
        {
            if(resizingCanvas||current==null||pageCanvases.Count==0||activeRenderCount>0||DateTime.UtcNow.Subtract(lastViewportScroll).TotalMilliseconds<650)return;
            string previewSuffix="|"+requestToken+"|"+PreviewDpi,fullSuffix="|"+requestToken+"|"+FullDpi;
            if(loadingPages.Any(delegate(string key){return key.EndsWith(previewSuffix)||key.EndsWith(fullSuffix);}))return;
            int top=Math.Max(0,-pdfViewport.AutoScrollPosition.Y),bottom=top+pdfViewport.ClientSize.Height,total;pageCounts.TryGetValue(current.FilePath,out total);
            foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases.OrderBy(delegate(KeyValuePair<int,PdfCanvas> e){return e.Key;}))
            {
                PdfCanvas canvas=entry.Value;if(canvas.Image==null||PageDpi(entry.Key)>=FullDpi||canvas.Bottom<=top||canvas.Top>=bottom)continue;
                StartPageRender(current.FilePath,entry.Key,FullDpi,requestToken,total,true);return;
            }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if(keyData==(Keys.Control|Keys.O)){ImportPdf(this,EventArgs.Empty);return true;}
            if(keyData==(Keys.Control|Keys.C)&&!String.IsNullOrWhiteSpace(selectedPdfText))
            {
                bool editorSelection=(noteBox.Focused&&noteBox.SelectionLength>0)||(quoteBox.Focused&&quoteBox.SelectionLength>0);
                if(!editorSelection)foreach(Panel card in markCards.Values){TextBox t=card.Controls.OfType<TextBox>().FirstOrDefault();if(t!=null&&t.Focused&&t.SelectionLength>0){editorSelection=true;break;}}
                if(!editorSelection){try{Clipboard.SetText(selectedPdfText);statusLabel.Text="所选 PDF 文字已复制";return true;}catch(Exception ex){statusLabel.Text="复制失败："+ex.Message;return true;}}
            }
            return base.ProcessCmdKey(ref msg,keyData);
        }

        void LoadLibrary()
        {
            try { if (File.Exists(dataFile)) { var ser = new DataContractJsonSerializer(typeof(Library)); using (var s = File.OpenRead(dataFile)) library = (Library)ser.ReadObject(s); } } catch { }
            if (library == null) library = new Library(); if (library.Papers == null) library.Papers = new List<Paper>();
        }
        void SaveLibrary()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(dataFile)); var ser = new DataContractJsonSerializer(typeof(Library)); using (var s = File.Create(dataFile)) ser.WriteObject(s, library); statusLabel.Text = "已保存到本机 · " + DateTime.Now.ToString("HH:mm"); }
            catch (Exception ex) { MessageBox.Show("保存失败：" + ex.Message, "PaperNote", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        void RefreshPapers() { papers.Items.Clear(); paperPicker.Items.Clear(); foreach (var p in library.Papers) { papers.Items.Add(p); paperPicker.Items.Add(p); } if (library.Papers.Count > 0 && papers.SelectedIndex < 0) papers.SelectedIndex = 0; if (papers.SelectedItem != null) paperPicker.SelectedItem = papers.SelectedItem; }
        void SelectPaper(object sender, EventArgs e)
        {
            requestToken++;if(selectedPdfCanvas!=null)selectedPdfCanvas.ClearDraft();ClearOpenCallouts();current = papers.SelectedItem as Paper;pdfCanvas=null;currentPage=1;pageBox.Text="1";pageDpi.Clear();pageReferenceSize.Clear();pdfViewport.AutoScrollPosition=Point.Empty;pageCanvases.Clear();pdfPages.Controls.Clear();marks.Items.Clear();if (current == null) return;
            if (current.Marks == null) current.Marks = new List<Mark>();
            foreach (var m in current.Marks) marks.Items.Add(m);ShowPage(1);
        }
        void ClearOpenCallouts()
        {
            foreach(KeyValuePair<Mark,Panel> entry in new List<KeyValuePair<Mark,Panel>>(markCards))
            {
                PdfCanvas page;if(markCardPages.TryGetValue(entry.Key,out page))page.Controls.Remove(entry.Value);
                entry.Value.Dispose();
            }
            foreach(KeyValuePair<PdfCanvas,ConnectorLayer> entry in pageConnectorLayers){entry.Key.Controls.Remove(entry.Value);entry.Value.Dispose();}
            markCards.Clear();markCardLines.Clear();markCardPages.Clear();markCardScales.Clear();
            pageConnectorLayers.Clear();
        }
        void ImportPdf(object sender, EventArgs e)
        {
            using (var d = new OpenFileDialog { Filter = "PDF 文档 (*.pdf)|*.pdf", Title = "导入论文 PDF" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                foreach (var p in library.Papers) if (String.Equals(p.FilePath, d.FileName, StringComparison.OrdinalIgnoreCase)) { papers.SelectedItem = p; return; }
                var paper = new Paper { Title = Path.GetFileNameWithoutExtension(d.FileName), FilePath = d.FileName, Status = "未读", Marks = new List<Mark>() };
                library.Papers.Insert(0, paper); SaveLibrary(); RefreshPapers(); papers.SelectedItem = paper;
            }
        }
        void OpenPdf(object sender, EventArgs e)
        {
            if (current == null) { MessageBox.Show("请先导入或选择一篇论文 PDF。", "PaperNote"); return; }
            if (!File.Exists(current.FilePath)) { MessageBox.Show("找不到 PDF 文件：\r\n" + current.FilePath, "PaperNote"); return; }
            try { Process.Start(current.FilePath); } catch (Exception ex) { MessageBox.Show("无法打开 PDF：" + ex.Message); }
        }
        string PopplerBin()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache\\codex-runtimes\\codex-primary-runtime\\dependencies\\native\\poppler\\Library\\bin");
            if (File.Exists(Path.Combine(path, "pdftoppm.exe"))) return path;
            return "";
        }
        string PdfTextExecutable()
        {
            string bundled=Path.Combine(PopplerBin(),"pdftotext.exe");if(File.Exists(bundled))return bundled;
            string pathValue=Environment.GetEnvironmentVariable("PATH");if(!String.IsNullOrEmpty(pathValue))foreach(string entry in pathValue.Split(Path.PathSeparator)){string dir=entry.Trim().Trim('"');if(dir.Length==0)continue;string candidate=Path.Combine(dir,"pdftotext.exe");if(File.Exists(candidate))return candidate;}
            string texLiveRoot=Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)??"C:\\","texlive");if(Directory.Exists(texLiveRoot))foreach(string version in Directory.GetDirectories(texLiveRoot).OrderByDescending(delegate(string d){return d;})){string candidate=Path.Combine(version,"bin","windows","pdftotext.exe");if(File.Exists(candidate))return candidate;}
            string gitText=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Git\\mingw64\\bin\\pdftotext.exe");if(File.Exists(gitText))return gitText;
            return "pdftotext.exe";
        }
        static string Q(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
        long CpuAffinityMask(){int cores=Math.Min(activeRenderCount>0?8:1,Math.Max(1,Environment.ProcessorCount));return (1L<<cores)-1;}
        void ApplyCpuAffinity(){try{Process.GetCurrentProcess().ProcessorAffinity=new IntPtr(CpuAffinityMask());}catch{}}
        string RunTool(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi)) { try{p.ProcessorAffinity=new IntPtr(CpuAffinityMask());}catch{}string output = p.StandardOutput.ReadToEnd(); string error = p.StandardError.ReadToEnd(); p.WaitForExit(); if (p.ExitCode != 0) throw new Exception(String.IsNullOrWhiteSpace(error) ? output : error); return output + "\n" + error; }
        }
        void ShowPage(int page)
        {
            if (current == null || !File.Exists(current.FilePath)) { if(pdfCanvas!=null)pdfCanvas.Image=null; pageLabel.Text = "第 0 / 0 页"; return; }
            string docKey=current.FilePath,cacheKey=docKey+"|"+page;Image cached;
            int total;if(renderedPages.TryGetValue(cacheKey,out cached)){pageCounts.TryGetValue(docKey,out total);int dpi;if(!renderedDpi.TryGetValue(cacheKey,out dpi))dpi=FullDpi;AddPage(page,cached,total,dpi);if((page-1)%MaxParallelRenders==0)PrefetchNextBatch(page+1);return;}
            int token=requestToken,knownTotal;pageCounts.TryGetValue(docKey,out knownTotal);StartPageRender(docKey,page,PreviewDpi,token,knownTotal,true);
        }
        bool StartPageRender(string docKey,int page,int dpi,int token,int knownTotal,bool showInCanvas)
        {
            string cacheKey=docKey+"|"+page,loadingKey=cacheKey+"|"+token+"|"+dpi;
            if(loadingPages.Contains(loadingKey)){if(showInCanvas)renderToCanvas[loadingKey]=true;return true;}
            if(activeRenderCount>=MaxParallelRenders)return false;
            loadingPages.Add(loadingKey);renderToCanvas[loadingKey]=showInCanvas;activeRenderCount++;ApplyCpuAffinity();statusLabel.Text="正在后台加载第 "+page+" 页…";
            var worker=new BackgroundWorker();
            worker.DoWork+=delegate(object s,DoWorkEventArgs e)
            {
                var req=(RenderRequest)e.Argument;string bin=PopplerBin();if(bin.Length==0)throw new Exception("Poppler 渲染组件未找到。");int count=req.Count;
                if(count<1){string info=RunTool(Path.Combine(bin,"pdfinfo.exe"),Q(req.Path));foreach(string line in info.Split('\n'))if(line.TrimStart().StartsWith("Pages:"))Int32.TryParse(line.Substring(line.IndexOf(':')+1).Trim(),out count);}
                if(count<1)throw new Exception("无法读取 PDF 页数。");int pg=Math.Max(1,Math.Min(count,req.Page));string prefix=Path.Combine(renderRoot,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(renderRoot);
                RunTool(Path.Combine(bin,"pdftoppm.exe"),"-f "+pg+" -l "+pg+" -singlefile -png -r "+req.Dpi+" "+Q(req.Path)+" "+Q(prefix));
                using(var src=Image.FromFile(prefix+".png"))e.Result=new RenderResult{Path=req.Path,Page=pg,Count=count,Token=req.Token,Dpi=req.Dpi,Bitmap=new Bitmap(src)};
            };
            worker.RunWorkerCompleted+=delegate(object s,RunWorkerCompletedEventArgs e)
            {
                loadingPages.Remove(loadingKey);bool shouldShow;renderToCanvas.TryGetValue(loadingKey,out shouldShow);renderToCanvas.Remove(loadingKey);activeRenderCount=Math.Max(0,activeRenderCount-1);ApplyCpuAffinity();if(IsDisposed){if(activeRenderCount==0)TrimPageCache(IdleBitmapBudget);return;}if(e.Error!=null){if(token==requestToken)statusLabel.Text="PDF 加载失败："+e.Error.Message;if(activeRenderCount==0)TrimPageCache(IdleBitmapBudget);return;}
                var res=(RenderResult)e.Result;string key=res.Path+"|"+res.Page;int oldDpi;bool replace=!renderedDpi.TryGetValue(key,out oldDpi)||res.Dpi>oldDpi;Image prior=null;
                if(replace){renderedPages.TryGetValue(key,out prior);renderedPages[key]=res.Bitmap;renderedDpi[key]=res.Dpi;cacheOrder.Enqueue(key);}else res.Bitmap.Dispose();pageCounts[res.Path]=res.Count;
                if(res.Token!=requestToken||current==null||current.FilePath!=res.Path){if(activeRenderCount==0)TrimPageCache(IdleBitmapBudget);return;}
                Image best=renderedPages[key];int bestDpi=renderedDpi[key];PdfCanvas canvas;
                if(shouldShow&&pageCanvases.TryGetValue(res.Page,out canvas))
                {
                    pageDpi[res.Page]=bestDpi;pageReferenceSize[res.Page]=new SizeF(best.Width*(FullDpi/(float)bestDpi),best.Height*(FullDpi/(float)bestDpi));
                    if(canvas.Image!=best){Image old=canvas.Image;canvas.Image=best;ResizeCanvas();canvas.Invalidate();if(old!=null&&old!=best&&old!=prior)old.Dispose();}
                }
                else if(shouldShow)AddPage(res.Page,best,res.Count,bestDpi);
                if(prior!=null&&prior!=best&&pageCanvases.TryGetValue(res.Page,out canvas)&&canvas.Image==best)prior.Dispose();
                if(shouldShow&&(res.Page-1)%MaxParallelRenders==0)PrefetchNextBatch(res.Page+1);
                TrimPageCache(activeRenderCount>0?LoadingBitmapBudget:IdleBitmapBudget);
            };
            worker.RunWorkerAsync(new RenderRequest{Path=docKey,Page=page,Token=token,Count=knownTotal,Dpi=dpi});return true;
        }
        class RenderRequest { public string Path; public int Page,Token,Count,Dpi; }
        class RenderResult { public string Path; public int Page,Count,Token,Dpi; public Bitmap Bitmap; }
        void AddPage(int page,Image image,int total,int dpi)
        {
            pageDpi[page]=dpi;pageReferenceSize[page]=new SizeF(image.Width*(FullDpi/(float)dpi),image.Height*(FullDpi/(float)dpi));pageLabel.Text="第 "+page+" / "+total+" 页"; PdfCanvas canvas;
            if(!pageCanvases.TryGetValue(page,out canvas)){canvas=new PdfCanvas{BackColor=Color.White,SizeMode=PictureBoxSizeMode.StretchImage,Margin=new Padding(0,0,0,20)};canvas.MouseWheel+=ReaderWheel;canvas.SetMarksProvider(delegate{return current==null?new List<Mark>():current.Marks.FindAll(delegate(Mark m){return m.Page==page;});});canvas.MarkClicked+=delegate(Mark m){currentPage=page;pdfCanvas=canvas;ShowMarkPopup(m,canvas);};
                canvas.RegionSelected+=delegate(float x,float y,float w,float h){if(selectedPdfCanvas!=null&&selectedPdfCanvas!=canvas)selectedPdfCanvas.ClearDraft();currentPage=page;pdfCanvas=canvas;pageBox.Text=page.ToString();kindBox.SelectedItem="高亮";selectedPdfCanvas=canvas;selectedPdfText=canvas.SelectedText??"";quoteBox.Clear();noteBox.Clear();if(popupPage==canvas){popup.Visible=false;if(editorConnector!=null)editorConnector.Visible=false;}};
                canvas.SelectionActivated+=delegate(float x,float y,float w,float h){currentPage=page;pdfCanvas=canvas;pageBox.Text=page.ToString();kindBox.SelectedItem="高亮";ShowAnnotationPopup(canvas,x,y,w,h);};
                canvas.SelectionCleared+=delegate{if(selectedPdfCanvas!=null){PdfCanvas old=selectedPdfCanvas;selectedPdfText="";selectedPdfCanvas=null;quoteBox.Clear();if(popup.Visible){popup.Visible=false;if(editorConnector!=null)editorConnector.Visible=false;noteBox.Clear();}if(old!=canvas)old.ClearDraft();}};canvas.Resize+=delegate{RepositionPageCallouts(canvas);};pageCanvases[page]=canvas;pdfPages.Controls.Add(canvas);}
            canvas.Image=image;if(pdfCanvas==null){pdfCanvas=canvas;currentPage=page;pageBox.Text=page.ToString();}ResizeCanvas();LoadPageTextLayout(current.FilePath,page,canvas);canvas.Invalidate();statusLabel.Text="连续滚动阅读 · Ctrl+滚轮缩放";
        }
        void ResizeCanvas()
        {
            if (pdfViewport.ClientSize.Width < 1) return;
            int scrollX=Math.Max(0,-pdfViewport.AutoScrollPosition.X),scrollY=Math.Max(0,-pdfViewport.AutoScrollPosition.Y);resizingCanvas=true;
            try
            {
                int widest=0;
                foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases)
                {
                    PdfCanvas page=entry.Value;
                    SizeF reference;if(!pageReferenceSize.TryGetValue(entry.Key,out reference)){if(page.Image==null)continue;float factor=FullDpi/(float)PageDpi(entry.Key);reference=new SizeF(page.Image.Width*factor,page.Image.Height*factor);pageReferenceSize[entry.Key]=reference;}
                    float s=fitMode?FitScale():zoomLevel;
                    int w=Math.Max(1,(int)(reference.Width*s)),h=Math.Max(1,(int)(reference.Height*s));Size size=new Size(w,h);
                    if(page.Size!=size){page.Size=size;page.Invalidate();}if(w>widest)widest=w;
                }
                int contentWidth=Math.Max(pdfViewport.ClientSize.Width-4,widest+24),top=12;
                foreach(int pageNumber in pageCanvases.Keys.OrderBy(delegate(int n){return n;}))
                {
                    PdfCanvas page=pageCanvases[pageNumber];if(!pageReferenceSize.ContainsKey(pageNumber))continue;
                    Point location=new Point(Math.Max(12,(contentWidth-page.Width)/2),top);if(page.Location!=location)page.Location=location;
                    top+=page.Height+20;
                }
                int contentHeight=Math.Max(pdfViewport.ClientSize.Height,top+12);
                Size contentSize=new Size(contentWidth,contentHeight);if(pdfPages.Size!=contentSize)pdfPages.Size=contentSize;if(pdfViewport.AutoScrollMinSize!=contentSize)pdfViewport.AutoScrollMinSize=contentSize;
                pdfViewport.AutoScrollPosition=new Point(scrollX,scrollY);
            }
            finally{resizingCanvas=false;}
        }
        void SaveMark(object sender, EventArgs e)
        {
            if (current == null) { MessageBox.Show("请先导入一篇论文 PDF。", "PaperNote"); return; }
            int page; if (!Int32.TryParse(pageBox.Text, out page) || page < 1) { MessageBox.Show("页码需要是大于 0 的整数。", "PaperNote"); return; }
            if (String.IsNullOrWhiteSpace(noteBox.Text) && String.IsNullOrWhiteSpace(quoteBox.Text)) { MessageBox.Show("请填写摘录或批注内容。", "PaperNote"); return; }
            float x, y, w, h; pdfCanvas.GetDraft(out x, out y, out w, out h);
            int overlaps = 0; foreach (Mark old in current.Marks) if (old.Page == page && Intersects(x,y,w,h,old.X,old.Y,old.W,old.H)) overlaps++;
            var m = new Mark { Page = page, Kind = kindBox.SelectedItem.ToString(), Quote = quoteBox.Text.Trim(), Note = noteBox.Text.Trim(), Created = DateTime.Now.ToString("s"), X = x, Y = y, W = w, H = h, ColorIndex = overlaps % 5 };
            current.Marks.Add(m); marks.Items.Add(m); quoteBox.Clear();noteBox.Clear(); pdfCanvas.ClearDraft(); pdfCanvas.Invalidate(); popup.Visible = false;if(editorConnector!=null)editorConnector.Visible=false; SaveLibrary();
        }
        void DeleteMark(object sender, EventArgs e)
        {
            if (current == null || marks.SelectedItem == null) return; current.Marks.Remove(marks.SelectedItem as Mark); marks.Items.Remove(marks.SelectedItem); pdfCanvas.Invalidate(); SaveLibrary();
        }
        static bool Intersects(float x,float y,float w,float h,float ox,float oy,float ow,float oh) { return x < ox+ow && x+w > ox && y < oy+oh && y+h > oy; }
    }

    static class TextBoxExtensions
    {
        public static void PlaceholderTextCompat(this TextBox box, string text) { box.Text = text; box.ForeColor = Color.Gray; box.GotFocus += delegate { if (box.Text == text) { box.Text = ""; box.ForeColor = Color.Black; } }; box.LostFocus += delegate { if (box.Text.Length == 0) { box.Text = text; box.ForeColor = Color.Gray; } }; }
    }

    public class PdfCanvas : PictureBox
    {
        public event Action<float, float, float, float> RegionSelected;
        public event Action<float, float, float, float> SelectionActivated;
        public event Action SelectionCleared;
        public event Action<Mark> MarkClicked;
        Func<List<Mark>> getMarks;
        Point start, end;
        bool dragging, hasDraft;
        RectangleF draft;
        List<RectangleF> draftWords;
        List<RectangleF> previewWords;
        List<MainForm.WordBox> pageWords=new List<MainForm.WordBox>();
        Timer caretBlinkTimer;
        bool hasCaret,caretVisible;
        int caretWordIndex,caretOffset;
        float caretNormX,caretNormY;
        public string SelectedText { get; private set; }
        public PdfCanvas() { DoubleBuffered = true; SizeMode = PictureBoxSizeMode.Zoom; Cursor = Cursors.IBeam;TabStop=true;SetStyle(ControlStyles.Selectable,true);caretBlinkTimer=new Timer{Interval=530};caretBlinkTimer.Tick+=delegate{if(!hasCaret){caretBlinkTimer.Stop();return;}caretVisible=!caretVisible;Invalidate();}; }
        void RestartCaretBlink(){caretVisible=true;if(caretBlinkTimer!=null){caretBlinkTimer.Stop();caretBlinkTimer.Start();}Invalidate();}
        protected override void Dispose(bool disposing){if(disposing&&caretBlinkTimer!=null){caretBlinkTimer.Stop();caretBlinkTimer.Dispose();caretBlinkTimer=null;}base.Dispose(disposing);}
        public void SetMarksProvider(Func<List<Mark>> provider) { getMarks = provider; }
        public void SetTextLayout(List<MainForm.WordBox> words) { pageWords=words??new List<MainForm.WordBox>();Point p=PointToClient(Cursor.Position);if(ClientRectangle.Contains(p))Cursor=HitDraft(p)||HitMark(p)?Cursors.Hand:Cursors.IBeam;Invalidate(); }
        public void SetDraft(float x, float y, float w, float h) { draft = new RectangleF(x,y,w,h);draftWords=null;hasDraft = w > 0 && h > 0; Invalidate(); }
        public void GetDraft(out float x, out float y, out float w, out float h) { x = draft.X; y = draft.Y; w = draft.Width; h = draft.Height; }
        public Point AnchorPoint(float x,float y,float w,float h,Control target) { RectangleF b=ImageBounds();Point screen=PointToScreen(new Point((int)(b.X+(x+w/2f)*b.Width),(int)(b.Y+(y+h/2f)*b.Height)));return target.PointToClient(screen); }
        public void ClearDraft() { hasDraft = false; draft = RectangleF.Empty;draftWords=null;previewWords=null;SelectedText="";hasCaret=false;caretVisible=false;if(caretBlinkTimer!=null)caretBlinkTimer.Stop();Invalidate();if(SelectionCleared!=null)SelectionCleared(); }
        bool HitDraft(Point p){if(!hasDraft||draftWords==null)return false;RectangleF b=ImageBounds();foreach(RectangleF word in draftWords)if(RectangleF.Inflate(new RectangleF(b.X+word.X*b.Width,b.Y+word.Y*b.Height,word.Width*b.Width,word.Height*b.Height),3,3).Contains(p))return true;return false;}
        public Rectangle AnchorBounds(float x,float y,float w,float h) { RectangleF b=ImageBounds();return Rectangle.Round(new RectangleF(b.X+x*b.Width,b.Y+y*b.Height,w*b.Width,h*b.Height)); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Image == null || e.Button != MouseButtons.Left) return;bool activateDraft=hasDraft&&HitDraft(e.Location),hitMark=HitMark(e.Location);if(hasDraft&&!activateDraft)ClearDraft();Focus();dragging = true; Cursor = Cursors.IBeam; start = e.Location; end = e.Location;previewWords=null;if(!activateDraft&&!hitMark&&ImageBounds().Contains(e.Location)){MainForm.WordBox word=FindNearestWord(e.Location,14);if(word!=null)PlaceCaret(word,e.Location);else PlaceCaretAtPoint(e.Location);} }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) { Cursor = Cursors.IBeam; end = e.Location;if(Math.Abs(e.X-start.X)>=9||Math.Abs(e.Y-start.Y)>=9)previewWords=HighlightRects(WordsBetween(start,e.Location));else previewWords=null;Invalidate(); } else Cursor = (HitDraft(e.Location)||HitMark(e.Location))?Cursors.Hand:Cursors.IBeam; }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); if (!dragging || Image == null) return; dragging = false; end = e.Location;previewWords=null;Cursor=HitDraft(e.Location)||HitMark(e.Location)?Cursors.Hand:Cursors.IBeam;
            if(Math.Abs(end.X-start.X)<9&&Math.Abs(end.Y-start.Y)<9)
            {
                if(HitDraft(e.Location)){if(SelectionActivated!=null)SelectionActivated(draft.X,draft.Y,draft.Width,draft.Height);Invalidate();return;}
                Mark clicked=FindMarkAt(e.Location);if(clicked!=null){ClearDraft();if(MarkClicked!=null)MarkClicked(clicked);return;}
                MainForm.WordBox word=FindNearestWord(e.Location,14);if(word!=null)PlaceCaret(word,e.Location);else if(ImageBounds().Contains(e.Location))PlaceCaretAtPoint(e.Location);else ClearDraft();return;
            }
            List<MainForm.WordBox> words=WordsBetween(start,end);if(words.Count>0)SetSelection(words);else if(ImageBounds().Contains(e.Location))PlaceCaretAtPoint(e.Location);else ClearDraft();
        }
        static RectangleF Normalize(Point a, Point b) { return new RectangleF(Math.Min(a.X,b.X), Math.Min(a.Y,b.Y), Math.Abs(a.X-b.X), Math.Abs(a.Y-b.Y)); }
        RectangleF WordRect(MainForm.WordBox word){return new RectangleF(word.X1,word.Y1,word.X2-word.X1,word.Y2-word.Y1);}
        List<RectangleF> HighlightRects(List<MainForm.WordBox> words)
        {
            var result=new List<RectangleF>();if(words==null||words.Count==0)return result;
            RectangleF run=RectangleF.Empty;
            foreach(MainForm.WordBox word in words)
            {
                RectangleF r=WordRect(word);
                if(!run.IsEmpty)
                {
                    float h=Math.Max(run.Height,r.Height),minH=Math.Min(run.Height,r.Height);
                    float overlap=Math.Max(0,Math.Min(run.Bottom,r.Bottom)-Math.Max(run.Top,r.Top));
                    float centerGap=Math.Abs((run.Top+run.Bottom-r.Top-r.Bottom)*0.5f),wordGap=r.Left-run.Right;
                    bool sameLine=overlap>=minH*0.3f||centerGap<=h*0.35f;
                    if(sameLine&&wordGap>=-h*0.5f&&wordGap<=h*2.5f)
                    {
                        run=RectangleF.FromLTRB(Math.Min(run.Left,r.Left),Math.Min(run.Top,r.Top),Math.Max(run.Right,r.Right),Math.Max(run.Bottom,r.Bottom));
                        continue;
                    }
                    result.Add(run);
                }
                run=r;
            }
            if(!run.IsEmpty)result.Add(run);return result;
        }
        List<MainForm.WordBox> WordsBetween(Point from,Point to)
        {
            var result=new List<MainForm.WordBox>();if(pageWords==null||pageWords.Count==0)return result;
            MainForm.WordBox first=FindNearestWord(from,20),last=FindNearestWord(to,28);if(first==null||last==null)return result;
            int a=pageWords.IndexOf(first),b=pageWords.IndexOf(last);if(a<0||b<0)return result;
            int low=Math.Min(a,b),high=Math.Max(a,b);for(int i=low;i<=high;i++)result.Add(pageWords[i]);return result;
        }
        MainForm.WordBox FindNearestWord(Point point,float tolerance)
        {
            RectangleF bounds=ImageBounds();if(bounds.Width<=0||pageWords==null)return null;MainForm.WordBox best=null;float bestDistance=float.MaxValue;
            for(int i=pageWords.Count-1;i>=0;i--){MainForm.WordBox word=pageWords[i];RectangleF r=new RectangleF(bounds.X+word.X1*bounds.Width,bounds.Y+word.Y1*bounds.Height,(word.X2-word.X1)*bounds.Width,(word.Y2-word.Y1)*bounds.Height);float dx=point.X<r.Left?r.Left-point.X:point.X>r.Right?point.X-r.Right:0,dy=point.Y<r.Top?r.Top-point.Y:point.Y>r.Bottom?point.Y-r.Bottom:0,d=dx*dx+dy*dy;if(d<bestDistance){bestDistance=d;best=word;}}
            return bestDistance<=tolerance*tolerance?best:null;
        }
        void PlaceCaret(MainForm.WordBox word,Point point)
        {
            ClearDraft();caretWordIndex=pageWords.IndexOf(word);if(caretWordIndex<0)return;
            RectangleF bounds=ImageBounds(),r=new RectangleF(bounds.X+word.X1*bounds.Width,bounds.Y+word.Y1*bounds.Height,(word.X2-word.X1)*bounds.Width,(word.Y2-word.Y1)*bounds.Height);
            int length=Math.Max(1,(word.Text??"").Length);float fraction=Math.Max(0,Math.Min(1,(point.X-r.Left)/Math.Max(1,r.Width)));caretOffset=Math.Max(0,Math.Min(length,(int)Math.Round(fraction*length)));
            caretNormX=(point.X-bounds.X)/bounds.Width;caretNormY=(point.Y-bounds.Y)/bounds.Height;hasCaret=true;Focus();RestartCaretBlink();
        }
        void PlaceCaretAtPoint(Point point)
        {
            ClearDraft();RectangleF bounds=ImageBounds();if(bounds.Width<=0||bounds.Height<=0)return;
            caretWordIndex=-1;caretNormX=(point.X-bounds.X)/bounds.Width;caretNormY=(point.Y-bounds.Y)/bounds.Height;hasCaret=true;Focus();RestartCaretBlink();
        }
        void MoveCaret(int delta)
        {
            if(!hasCaret||pageWords.Count==0)return;int len=Math.Max(1,(pageWords[caretWordIndex].Text??"").Length);
            if(delta<0){if(caretOffset>0)caretOffset--;else if(caretWordIndex>0){caretWordIndex--;caretOffset=Math.Max(1,(pageWords[caretWordIndex].Text??"").Length);}}
            else{if(caretOffset<len)caretOffset++;else if(caretWordIndex<pageWords.Count-1){caretWordIndex++;caretOffset=0;}}
            RestartCaretBlink();
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if(hasCaret&&e.KeyCode==Keys.Left){MoveCaret(-1);e.Handled=true;e.SuppressKeyPress=true;return;}
            if(hasCaret&&e.KeyCode==Keys.Right){MoveCaret(1);e.Handled=true;e.SuppressKeyPress=true;return;}
            base.OnKeyDown(e);
        }
        void SetSelection(List<MainForm.WordBox> words)
        {
            if(words==null||words.Count==0){ClearDraft();return;}
            hasCaret=false;caretVisible=false;if(caretBlinkTimer!=null)caretBlinkTimer.Stop();
            draftWords=HighlightRects(words);float x=draftWords.Min(delegate(RectangleF r){return r.X;}),y=draftWords.Min(delegate(RectangleF r){return r.Y;}),right=draftWords.Max(delegate(RectangleF r){return r.Right;}),bottom=draftWords.Max(delegate(RectangleF r){return r.Bottom;});
            draft=new RectangleF(x,y,right-x,bottom-y);hasDraft=true;SelectedText=String.Join(" ",words.Select(delegate(MainForm.WordBox word){return word.Text;}).ToArray());Invalidate();
            if(RegionSelected!=null)RegionSelected(draft.X,draft.Y,draft.Width,draft.Height);
        }
        RectangleF ImageBounds()
        {
            if (Image == null || ClientSize.Width == 0 || ClientSize.Height == 0) return RectangleF.Empty;
            float scale = Math.Min((float)ClientSize.Width / Image.Width, (float)ClientSize.Height / Image.Height); float w = Image.Width * scale, h = Image.Height * scale;
            return new RectangleF((ClientSize.Width-w)/2f, (ClientSize.Height-h)/2f, w, h);
        }
        bool HitMark(Point p) { return FindMarkAt(p)!=null; }
        Mark FindMarkAt(Point p) { if(getMarks==null)return null;RectangleF b=ImageBounds();if(b.Width<=0)return null;List<Mark> list=getMarks();if(list==null)return null;for(int i=list.Count-1;i>=0;i--){Mark m=list[i];if(m.W>0&&m.H>0){RectangleF r=new RectangleF(b.X+m.X*b.Width,b.Y+m.Y*b.Height,m.W*b.Width,m.H*b.Height);if(r.Contains(p))return m;}}return null; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); RectangleF b = ImageBounds(); if (b.Width <= 0) return; List<Mark> all = getMarks == null ? null : getMarks();
            Color[] palette = new Color[] { Color.FromArgb(255, 224, 130), Color.FromArgb(144, 202, 249), Color.FromArgb(225, 190, 231), Color.FromArgb(165, 214, 167), Color.FromArgb(255, 171, 145) };
            if (all != null) foreach (Mark m in all) if (m.Page > 0 && m.W > 0 && m.H > 0) { RectangleF r = new RectangleF(b.X+m.X*b.Width,b.Y+m.Y*b.Height,m.W*b.Width,m.H*b.Height); Color c=palette[Math.Abs(m.ColorIndex)%palette.Length]; using (var brush = new SolidBrush(Color.FromArgb(105,c))) e.Graphics.FillRectangle(brush,r); using (var pen = new Pen(Color.FromArgb(190,c),1)) e.Graphics.DrawRectangle(pen,r.X,r.Y,r.Width,r.Height); }
            using(var brush=new SolidBrush(Color.FromArgb(150,66,133,244)))
            {
                if(hasDraft&&draftWords!=null)foreach(RectangleF word in draftWords)e.Graphics.FillRectangle(brush,b.X+word.X*b.Width,b.Y+word.Y*b.Height,word.Width*b.Width,word.Height*b.Height);
                if(dragging&&previewWords!=null)foreach(RectangleF word in previewWords)e.Graphics.FillRectangle(brush,b.X+word.X*b.Width,b.Y+word.Y*b.Height,word.Width*b.Width,word.Height*b.Height);
            }
            if(hasCaret&&caretVisible&&caretWordIndex>=0&&caretWordIndex<pageWords.Count)
            {
                MainForm.WordBox word=pageWords[caretWordIndex];RectangleF r=new RectangleF(b.X+word.X1*b.Width,b.Y+word.Y1*b.Height,(word.X2-word.X1)*b.Width,(word.Y2-word.Y1)*b.Height);int len=Math.Max(1,(word.Text??"").Length);float caretGap=caretOffset==0?-2f:2f;float x=r.Left+r.Width*caretOffset/len+caretGap;using(var pen=new Pen(Color.Black,1f))e.Graphics.DrawLine(pen,x,r.Top,x,Math.Max(r.Top+3,r.Bottom));
            }
            else if(hasCaret&&caretVisible&&caretWordIndex<0)
            {
                float x=b.X+caretNormX*b.Width+2f,y=b.Y+caretNormY*b.Height,h=Math.Max(16,b.Height*0.018f),top=Math.Max(b.Top,Math.Min(b.Bottom-h,y-h/2));using(var pen=new Pen(Color.Black,1f))e.Graphics.DrawLine(pen,x,top,x,top+h);
            }
        }
    }

    public class ConnectorLayer : Control
    {
        public Point Start,End;
        struct Segment { public Point Start,End; public Segment(Point start,Point end){Start=start;End=end;} }
        Dictionary<Mark,Segment> segments=new Dictionary<Mark,Segment>();
        public int SegmentCount { get { return segments.Count; } }
        public ConnectorLayer(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.SupportsTransparentBackColor,true);BackColor=Color.Transparent;Enabled=false;}
        public void SetSegment(Mark mark,Point start,Point end){segments[mark]=new Segment(start,end);Invalidate();}
        public void RemoveSegment(Mark mark){segments.Remove(mark);Invalidate();}
        protected override CreateParams CreateParams { get { var cp=base.CreateParams;cp.ExStyle|=0x20;return cp; } }
        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(var pen=new Pen(Color.FromArgb(220,198,40,40),2))using(var brush=new SolidBrush(Color.FromArgb(220,198,40,40))){if(segments.Count>0){foreach(Segment s in segments.Values){e.Graphics.DrawLine(pen,s.Start,s.End);e.Graphics.FillEllipse(brush,s.Start.X-4,s.Start.Y-4,8,8);}}else{e.Graphics.DrawLine(pen,Start,End);e.Graphics.FillEllipse(brush,Start.X-4,Start.Y-4,8,8);}}}
    }
    public class BufferedPanel : Panel
    {
        public BufferedPanel(){DoubleBuffered=true;SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.SupportsTransparentBackColor,true);}
    }
    static class NativeTransparency
    {
        const int GwlExStyle=-20,WsExLayered=0x80000,LwaAlpha=0x2;
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW",SetLastError=true)] static extern IntPtr GetWindowLongPtr(IntPtr hWnd,int nIndex);
        [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW",SetLastError=true)] static extern IntPtr SetWindowLongPtr(IntPtr hWnd,int nIndex,IntPtr dwNewLong);
        [DllImport("user32.dll",SetLastError=true)] static extern bool SetLayeredWindowAttributes(IntPtr hwnd,uint crKey,byte bAlpha,uint dwFlags);
        public static void SetAlpha(IntPtr hwnd,byte alpha){if(hwnd==IntPtr.Zero)return;long style=GetWindowLongPtr(hwnd,GwlExStyle).ToInt64();SetWindowLongPtr(hwnd,GwlExStyle,new IntPtr(style|WsExLayered));SetLayeredWindowAttributes(hwnd,0,alpha,LwaAlpha);}
    }
}
