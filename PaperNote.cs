using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Reflection;

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
        [DataMember] public bool CardManuallyPositioned { get; set; }
        public override string ToString()
        {
            return "第" + Page + " 页 ·  " + Kind + (String.IsNullOrWhiteSpace(Quote) ? "" : "“" + Quote + "”") + (String.IsNullOrWhiteSpace(Note) ? "" : "\r\n    " + Note);
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
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetDllDirectory(string path);

        [STAThread]
        static void Main()
        {
            string runtimeFolder = "PaperNoteRuntime-PDFium";
            string executableName=Path.GetFileNameWithoutExtension(Application.ExecutablePath),updatedPrefix="PaperNote-PDFium-Updated";
            if(executableName.StartsWith(updatedPrefix,StringComparison.OrdinalIgnoreCase))runtimeFolder="PaperNoteRuntime-PDFium"+executableName.Substring("PaperNote-PDFium".Length);
            string runtimeDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, runtimeFolder);
            if (Directory.Exists(runtimeDirectory))
            {
                SetDllDirectory(runtimeDirectory);
                AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
                {
                    string name = new AssemblyName(args.Name).Name + ".dll";
                    string path = Path.Combine(runtimeDirectory, name);
                    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
                };
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form, IMessageFilter
    {
        const int PreviewDpi=300, FullDpi=600, MaxParallelLoadJobs=8, MaxPrefetchPages=4, MaxRenderDimension=2500;
        const int MaxFullPageDimension=4096, RegionMarginPixels=180, MaxCanvasSide=30000, MaxRegionPixelSize=6000;
        const long MaxFullPagePixels=24000000L;
        const float MinimumZoom=0.08f;
        const int MarkCardDefaultWidth=562, MarkCardDefaultHeight=282, MarkCardMinimumWidth=300, MarkCardMinimumHeight=200;
        const long MaxRenderPixels=120000000L;
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
        TextBox pageIndicator = new TextBox();
        ComboBox paperPicker = new ComboBox();
        Panel pdfViewport = new Panel();
        Panel pdfPages;
        VScrollBar pdfVerticalScrollBar;
        HScrollBar pdfHorizontalScrollBar;
        Panel libraryDrawer;
        Timer drawerAnimation;
        int drawerWidth=360;
        bool drawerOpen;
        Dictionary<int, PdfCanvas> pageCanvases = new Dictionary<int, PdfCanvas>();
        Panel popup = new Panel();
        Button popupSave;
        Label popupHeading;
        Control readerSurface;
        Rectangle popupTargetBounds;
        PdfCanvas popupPage;
        float popupX,popupY,popupW,popupH;
        Dictionary<Mark,Panel> markCards = new Dictionary<Mark,Panel>();
        Dictionary<Mark,ConnectorLayer> markCardLines = new Dictionary<Mark,ConnectorLayer>();
        Dictionary<Mark,PdfCanvas> markCardPages = new Dictionary<Mark,PdfCanvas>();
        Dictionary<PdfCanvas,ConnectorLayer> pageConnectorLayers = new Dictionary<PdfCanvas,ConnectorLayer>();
        Dictionary<Mark,float> markCardScales = new Dictionary<Mark,float>();
        HashSet<Mark> flashingMarks = new HashSet<Mark>();
        float zoomLevel = 1f;
        bool fitMode = true;
        float regionScale = -1f;
        bool resizingCanvas;
        int currentPage = 1;
        int virtualScrollX,virtualScrollY,documentWidth,documentHeight;
        bool updatingScrollBars;
        const int ScrollBarLogicalRange=30000;
        string selectedPdfText = "";
        PdfCanvas selectedPdfCanvas;
        Dictionary<string, Image> renderedPages = new Dictionary<string, Image>();
        Dictionary<string, int> renderedDpi = new Dictionary<string, int>();
        Dictionary<int, int> pageDpi = new Dictionary<int, int>();
        Dictionary<int, SizeF> pageReferenceSize = new Dictionary<int, SizeF>();
        SizeF defaultPageReference;
        int pageLayoutVersion;
        int pageLayoutIndexVersion=-1;
        List<int> pageLayoutIndex=new List<int>();
        List<double> pageLayoutCorrections=new List<double>();
        Dictionary<string, bool> renderToCanvas = new Dictionary<string, bool>();
        Dictionary<string, List<WordBox>> textPages = new Dictionary<string, List<WordBox>>();
        HashSet<string> textLoadsInFlight = new HashSet<string>();
        object textPagesLock = new object();
        Dictionary<string, int> pageCounts = new Dictionary<string, int>();
        HashSet<string> fullResolutionFailures = new HashSet<string>();
        HashSet<string> pageGeometryLoads = new HashSet<string>();
        bool pageGeometryReady;
        int pendingPageJump;
        Queue<string> cacheOrder = new Queue<string>();
        int requestToken;
        volatile int activeLoadCount;
        DateTime lastViewportScroll=DateTime.MinValue;
        Timer wheelTimer;
        Timer scrollWatch;
        Timer crossPageDragTimer;
        bool crossPageDragTracking,crossPageDragActive,crossPageDragMoved;
        int crossPageDragStartPage,crossPageDragStartWord,crossPageDragEndPage,crossPageDragEndWord;
        Dictionary<int,List<WordBox>> crossPageSelectedWords=new Dictionary<int,List<WordBox>>();
        string crossPageSelectedText="";
        int pendingPage;
        bool pageIndicatorEditing;
        HashSet<string> loadingPages = new HashSet<string>();
        HashSet<string> regionLoads = new HashSet<string>();
        Dictionary<int,string> regionRequests = new Dictionary<int,string>();
        public class WordBox { public float X1, Y1, X2, Y2; public string Text; public int TaggedOrder=-1, TaggedCharIndex=-1; }

        public MainForm()
        {
            Text = "PaperNote · 论文精读工作台"; Width = 2560; Height = 1440; MinimumSize = new Size(1100, 700); StartPosition = FormStartPosition.CenterScreen; WindowState = FormWindowState.Maximized; KeyPreview = true;
            BackColor = Color.FromArgb(255, 247, 214); Font = new Font("Segoe UI", 9F); LoadLibrary(); BuildUi(); RefreshPapers(); Application.AddMessageFilter(this);crossPageDragTimer=new Timer{Interval=35};crossPageDragTimer.Tick+=delegate{AutoScrollCrossPageDrag();};FormClosed+=delegate{Application.RemoveMessageFilter(this);if(drawerAnimation!=null){drawerAnimation.Stop();drawerAnimation.Dispose();}if(crossPageDragTimer!=null){crossPageDragTimer.Stop();crossPageDragTimer.Dispose();}};ApplyCpuAffinity();
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
            pageIndicator = new TextBox{Text="0/0",Size=new Size(58,28),TextAlign=HorizontalAlignment.Center,Font=new Font("Segoe UI Semibold",9.5F),ForeColor=Color.FromArgb(57,52,79),BackColor=Color.White,BorderStyle=BorderStyle.FixedSingle,Anchor=AnchorStyles.Right|AnchorStyles.Bottom,MaxLength=12,TabStop=false};
            pageIndicator.Enter+=delegate{pageIndicatorEditing=true;pageIndicator.Text=Math.Max(1,currentPage).ToString();pageIndicator.SelectAll();};
            pageIndicator.Click+=delegate{if(pageIndicatorEditing)pageIndicator.SelectAll();};
            pageIndicator.Leave+=delegate{CommitPageIndicator(false);};
            pageIndicator.KeyDown+=delegate(object s,KeyEventArgs e){if(e.KeyCode==Keys.Enter){CommitPageIndicator(false);pdfViewport.Focus();e.Handled=true;e.SuppressKeyPress=true;}else if(e.KeyCode==Keys.Escape){CommitPageIndicator(true);pdfViewport.Focus();e.Handled=true;e.SuppressKeyPress=true;}};
            pdfViewport = new Panel { Dock = DockStyle.Fill, AutoScroll = false, BackColor = Color.FromArgb(251, 231, 214) };
            pdfCanvas.BackColor = Color.White; pdfCanvas.SizeMode = PictureBoxSizeMode.StretchImage;
            pdfPages = new Panel { Dock=DockStyle.Fill, BackColor=Color.FromArgb(251,231,214) };
            pdfViewport.Controls.Add(pdfPages); pdfViewport.Resize += delegate { if(!resizingCanvas)ResizeCanvas(); };
            pdfVerticalScrollBar=new VScrollBar{Dock=DockStyle.Right,Width=16,Visible=false};pdfVerticalScrollBar.Scroll+=delegate{if(updatingScrollBars)return;virtualScrollY=ScrollValueToOffset(pdfVerticalScrollBar.Value,GetMaxScrollY());ApplyVirtualScroll();};
            pdfHorizontalScrollBar=new HScrollBar{Dock=DockStyle.Bottom,Height=16,Visible=false};pdfHorizontalScrollBar.Scroll+=delegate{if(updatingScrollBars)return;virtualScrollX=ScrollValueToOffset(pdfHorizontalScrollBar.Value,GetMaxScrollX());ApplyVirtualScroll();};
            reader.Controls.Add(pdfViewport);reader.Controls.Add(pdfHorizontalScrollBar);reader.Controls.Add(pdfVerticalScrollBar);
            BuildPopup(reader);
            BuildLibraryDrawer(reader);
            reader.Controls.Add(pageIndicator);pageIndicator.BringToFront();reader.Resize+=delegate{PositionPageIndicator(reader);};PositionPageIndicator(reader);
            pdfViewport.MouseWheel += ReaderWheel; reader.MouseWheel += ReaderWheel;
            right.Controls.Add(reader);

            /* annotation editor is a floating card shown only after selecting PDF content */
            /*
            kindBox.DropDownStyle = ComboBoxStyle.DropDownList; kindBox.Items.AddRange(new object[] { "高亮", "公式", "创新点", "实验", "疑问", "普通批注" }); kindBox.SelectedIndex = 0; kindBox.Dock = DockStyle.Left; kindBox.Width = 140;
            pageBox.Text = "1"; pageBox.Width = 80; pageBox.Dock = DockStyle.Left;
            var pagePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false }; pagePanel.Controls.Add(pageBox); pagePanel.Controls.Add(new Label { Text = "  页码", AutoSize = true, Padding = new Padding(4, 6, 0, 0) });
            quoteBox.Multiline = true; quoteBox.Dock = DockStyle.Fill; quoteBox.ScrollBars = ScrollBars.Vertical; quoteBox.PlaceholderTextCompat("选中的原文或摘录（可留空）");
            noteBox.Multiline = true; noteBox.Dock = DockStyle.Fill; noteBox.ScrollBars = ScrollBars.Vertical; noteBox.PlaceholderTextCompat("写下你的想法、问题或公式说明。");
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
            scrollWatch = new Timer(); scrollWatch.Interval = 180; scrollWatch.Tick += delegate { EnsureVisiblePages(); TryUpgradeVisiblePage(); UpdatePageIndicator(); }; scrollWatch.Start();
        }

        void PositionPageIndicator(Control parent){if(pageIndicator==null)return;int rightInset=(pdfVerticalScrollBar!=null&&pdfVerticalScrollBar.Visible?pdfVerticalScrollBar.Width:0)+18,bottomInset=(pdfHorizontalScrollBar!=null&&pdfHorizontalScrollBar.Visible?pdfHorizontalScrollBar.Height:0)+18;pageIndicator.Location=new Point(Math.Max(12,parent.ClientSize.Width-pageIndicator.Width-rightInset),Math.Max(12,parent.ClientSize.Height-pageIndicator.Height-bottomInset));}
        void UpdatePageIndicator()
        {
            if(pageIndicator==null||pageIndicatorEditing)return;
            if(current==null){pageIndicator.Text="0/0";return;}
            int total;pageCounts.TryGetValue(current.FilePath,out total);
            if(pdfViewport!=null&&pdfViewport.ClientSize.Height>0)
            {
                int top=virtualScrollY,bottom=top+pdfViewport.ClientSize.Height,bestPage=0,bestVisible=0;
                foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases)
                {
                    int pageTop=virtualScrollY+entry.Value.Top;int visible=Math.Max(0,Math.Min(bottom,pageTop+entry.Value.Height)-Math.Max(top,pageTop));
                    if(visible>bestVisible){bestVisible=visible;bestPage=entry.Key;}
                }
                if(bestPage>0)currentPage=bestPage;
                else if(total>0)currentPage=FindPageAt(top,total,fitMode?FitScale():zoomLevel);
            }
            int shownTotal=total>0?total:0;
            pageIndicator.Text=Math.Max(1,currentPage)+"/"+(shownTotal>0?shownTotal.ToString():"…");
        }
        void CommitPageIndicator(bool cancel)
        {
            if(!pageIndicatorEditing)return;
            string value=pageIndicator.Text;pageIndicatorEditing=false;
            if(cancel){UpdatePageIndicator();return;}
            int target,total;
            if(!Int32.TryParse(value.Trim(),out target)){statusLabel.Text="请输入要跳转的页码";UpdatePageIndicator();return;}
            if(current==null||!pageCounts.TryGetValue(current.FilePath,out total)||total<1){statusLabel.Text="PDF 页数仍在读取，请稍后再跳转";UpdatePageIndicator();return;}
            if(target<1||target>total){statusLabel.Text="页码范围为 1–"+total;UpdatePageIndicator();return;}
            JumpToPage(target);UpdatePageIndicator();
        }
        void JumpToPage(int target)
        {
            if(current==null)return;
            if(defaultPageReference.Width<=0){statusLabel.Text="正在准备论文页面，请稍后再跳转";return;}
            if(!pageGeometryReady){pendingPageJump=target;statusLabel.Text="正在读取页面尺寸索引，完成后跳转到第 "+target+" 页";return;}
            EnsurePageCanvas(target);currentPage=target;float scale=fitMode?FitScale():zoomLevel;virtualScrollY=PageTop(target,scale);virtualScrollY=Math.Min(virtualScrollY,GetMaxScrollY());pageBox.Text=target.ToString();ResizeCanvas();
            ShowPage(target);EnsureVisiblePages();UpdatePageIndicator();
        }
        void EnsurePageCanvas(int page)
        {
            if(page<1||pageCanvases.ContainsKey(page))return;
            PdfCanvas canvas=CreatePageCanvas(page);pageCanvases[page]=canvas;pdfPages.Controls.Add(canvas);
        }
        void StartPageGeometryIndex(string file,int token)
        {
            string key=file+"|"+token;if(!pageGeometryLoads.Add(key))return;activeLoadCount++;ApplyCpuAffinity();
            var worker=new BackgroundWorker();worker.DoWork+=delegate(object s,DoWorkEventArgs e){e.Result=PdfiumEngine.GetPageSizes(file);};
            worker.RunWorkerCompleted+=delegate(object s,RunWorkerCompletedEventArgs e)
            {
                pageGeometryLoads.Remove(key);activeLoadCount=Math.Max(0,activeLoadCount-1);ApplyCpuAffinity();if(IsDisposed)return;
                if(token!=requestToken||current==null||!String.Equals(current.FilePath,file,StringComparison.OrdinalIgnoreCase))return;
                int scrollY=virtualScrollY,anchor=currentPage;
                int viewportTop=scrollY,viewportBottom=viewportTop+pdfViewport.ClientSize.Height,bestVisible=0;
                foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases)
                {
                    int pageTop=virtualScrollY+entry.Value.Top;int visible=Math.Max(0,Math.Min(viewportBottom,pageTop+entry.Value.Height)-Math.Max(viewportTop,pageTop));
                    if(visible>bestVisible){bestVisible=visible;anchor=entry.Key;}
                }
                float scale=fitMode?FitScale():zoomLevel;int oldAnchorTop=pageCanvases.ContainsKey(anchor)?virtualScrollY+pageCanvases[anchor].Top:PageTop(anchor,scale);int anchorOffset=scrollY-oldAnchorTop;
                List<SizeF> sizes=e.Error==null?e.Result as List<SizeF>:null;
                if(sizes!=null&&sizes.Count>0)
                {
                    pageReferenceSize.Clear();
                    for(int i=0;i<sizes.Count;i++)pageReferenceSize[i+1]=new SizeF(sizes[i].Width*(FullDpi/72f),sizes[i].Height*(FullDpi/72f));
                    defaultPageReference=pageReferenceSize[1];pageLayoutVersion++;pageLayoutIndexVersion=-1;pageGeometryReady=true;
                }
                else
                {
                    pageGeometryReady=true;
                    if(e.Error!=null)statusLabel.Text="页面尺寸索引读取失败，将使用估算位置："+e.Error.GetBaseException().Message;
                }
                ResizeCanvas();
                if(pendingPageJump>0){int destination=pendingPageJump;pendingPageJump=0;JumpToPage(destination);}
                else{virtualScrollY=Math.Max(0,PageTop(anchor,scale)+anchorOffset);ResizeCanvas();}
                EnsureVisiblePages();UpdatePageIndicator();
            };
            worker.RunWorkerAsync();
        }
        void SetPageReferenceSize(int page,SizeF reference)
        {
            if(pageGeometryReady&&pageReferenceSize.ContainsKey(page))return;
            SizeF old;bool hadOld=pageReferenceSize.TryGetValue(page,out old),indexChanged=false;float oldBase=defaultPageReference.Height;
            if(page==1)
            {
                defaultPageReference=reference;
                if(oldBase<=0||Math.Abs(oldBase-reference.Height)>0.5f)indexChanged=true;
            }
            float baseHeight=defaultPageReference.Height;bool oldAffects=hadOld&&oldBase>0&&Math.Abs(old.Height-oldBase)>0.5f,newAffects=baseHeight>0&&Math.Abs(reference.Height-baseHeight)>0.5f;
            if(oldAffects!=newAffects||(newAffects&&(!hadOld||Math.Abs(old.Height-reference.Height)>0.5f)))indexChanged=true;
            pageReferenceSize[page]=reference;
            if(indexChanged){pageLayoutVersion++;pageLayoutIndexVersion=-1;}
        }
        SizeF ReferenceSizeForPage(int page)
        {
            SizeF reference;if(pageReferenceSize.TryGetValue(page,out reference))return reference;
            return defaultPageReference.Width>0?defaultPageReference:new SizeF(Math.Max(1,pdfViewport.ClientSize.Width),Math.Max(1,pdfViewport.ClientSize.Width*1.4f));
        }
        void BuildPageLayoutIndex()
        {
            if(pageLayoutIndexVersion==pageLayoutVersion)return;
            pageLayoutIndex.Clear();pageLayoutCorrections.Clear();double correction=0;float baseHeight=defaultPageReference.Height;
            if(baseHeight>0)foreach(KeyValuePair<int,SizeF> entry in pageReferenceSize.OrderBy(delegate(KeyValuePair<int,SizeF> item){return item.Key;}))
            {
                double delta=entry.Value.Height-baseHeight;if(Math.Abs(delta)>0.5){correction+=delta;pageLayoutIndex.Add(entry.Key);pageLayoutCorrections.Add(correction);}
            }
            pageLayoutIndexVersion=pageLayoutVersion;
        }
        int PageTop(int page,float scale)
        {
            if(page<=1)return 12;
            BuildPageLayoutIndex();int low=0,high=pageLayoutIndex.Count;
            while(low<high){int mid=(low+high)/2;if(pageLayoutIndex[mid]<page)low=mid+1;else high=mid;}
            double correction=low>0?pageLayoutCorrections[low-1]:0;
            double y=12+(page-1)*(defaultPageReference.Height*scale+20)+correction*scale;
            return (int)Math.Max(12,Math.Min(Int32.MaxValue/2,y));
        }
        int FindPageAt(int y,int total,float scale)
        {
            int low=1,high=Math.Max(1,total);
            while(low<=high)
            {
                int mid=low+(high-low)/2,top=PageTop(mid,scale),bottom=top+(int)(ReferenceSizeForPage(mid).Height*scale);
                if(y<top)high=mid-1;else if(y>bottom+20)low=mid+1;else return mid;
            }
            return Math.Max(1,Math.Min(Math.Max(1,total),low));
        }

        static Label Label(string text, float size, bool bold) { return new Label { Text = text, Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size), AutoSize = false, ForeColor = Color.FromArgb(57, 52, 79), TextAlign = ContentAlignment.MiddleLeft }; }
        static Button Button(string text, bool primary) { var b = new Button { Text = text, AutoSize = true, Height = 38, FlatStyle = FlatStyle.Flat, BackColor = primary ? Color.FromArgb(255, 190, 61) : Color.White, ForeColor = Color.FromArgb(57, 52, 79), Padding = new Padding(12, 4, 12, 4), Cursor = Cursors.Hand }; b.FlatAppearance.BorderColor = Color.FromArgb(246, 222, 193); b.FlatAppearance.BorderSize = primary ? 0 : 1; return b; }
        static GraphicsPath RoundedRect(Rectangle r,int radius) { var p=new GraphicsPath();int d=radius*2;p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p; }

        void BuildLibraryDrawer(Control parent)
        {
            libraryDrawer=new Panel{Visible=false,Width=drawerWidth,Location=new Point(-drawerWidth,0),BackColor=Color.White,Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left,Padding=new Padding(16,12,16,14)};
            libraryDrawer.Paint+=delegate(object s,PaintEventArgs e){using(var pen=new Pen(Color.FromArgb(235,224,210)))e.Graphics.DrawLine(pen,libraryDrawer.Width-1,0,libraryDrawer.Width-1,libraryDrawer.Height);};
            var header=new Panel{Dock=DockStyle.Top,Height=48,BackColor=Color.White};
            var title=new Label{Text="论文库",Font=new Font("Segoe UI Semibold",15F),ForeColor=Color.FromArgb(57,52,79),AutoSize=true,Location=new Point(2,7)};
            var close=new Button{Text="×",FlatStyle=FlatStyle.Flat,Size=new Size(34,32),Anchor=AnchorStyles.Top|AnchorStyles.Right,Location=new Point(drawerWidth-58,3),BackColor=Color.White,ForeColor=Color.FromArgb(90,85,100),Cursor=Cursors.Hand};close.FlatAppearance.BorderSize=0;close.Click+=delegate{SetLibraryDrawer(false);};
            header.Controls.Add(title);header.Controls.Add(close);header.Resize+=delegate{close.Left=header.ClientSize.Width-close.Width-2;};
            papers.Dock=DockStyle.Fill;papers.BorderStyle=BorderStyle.None;papers.IntegralHeight=false;papers.DrawMode=DrawMode.OwnerDrawFixed;papers.ItemHeight=58;papers.Font=new Font("Segoe UI",10F);papers.BackColor=Color.White;papers.DrawItem+=DrawPaperItem;
            var import=Button("＋ 导入 PDF",true);import.Dock=DockStyle.Bottom;import.Height=44;import.Click+=ImportPdf;
            libraryDrawer.Controls.Add(papers);libraryDrawer.Controls.Add(import);libraryDrawer.Controls.Add(header);
            parent.Controls.Add(libraryDrawer);libraryDrawer.BringToFront();
            drawerAnimation=new Timer{Interval=12};drawerAnimation.Tick+=delegate
            {
                int target=drawerOpen?0:-drawerWidth,currentLeft=libraryDrawer.Left,step=36;
                if(currentLeft<target)libraryDrawer.Left=Math.Min(target,currentLeft+step);else if(currentLeft>target)libraryDrawer.Left=Math.Max(target,currentLeft-step);
                if(libraryDrawer.Left==target){drawerAnimation.Stop();if(!drawerOpen)libraryDrawer.Visible=false;}
            };
            parent.Resize+=delegate{if(!drawerAnimation.Enabled){libraryDrawer.Height=parent.ClientSize.Height;if(!drawerOpen)libraryDrawer.Left=-drawerWidth;}};
        }
        void DrawPaperItem(object sender,DrawItemEventArgs e)
        {
            if(e.Index<0||e.Index>=papers.Items.Count)return;
            bool selected=(e.State&DrawItemState.Selected)==DrawItemState.Selected;Color bg=selected?Color.FromArgb(255,246,226):Color.White,fg=Color.FromArgb(57,52,79);
            using(var brush=new SolidBrush(bg))e.Graphics.FillRectangle(brush,e.Bounds);
            Paper paper=papers.Items[e.Index] as Paper;string title=paper==null?papers.Items[e.Index].ToString():paper.Title;string subtitle=paper==null?"":Path.GetFileName(paper.FilePath??"");
            Rectangle titleRect=new Rectangle(e.Bounds.Left+12,e.Bounds.Top+7,e.Bounds.Width-24,22),subRect=new Rectangle(e.Bounds.Left+12,e.Bounds.Top+30,e.Bounds.Width-24,19);
            using(var titleFont=new Font("Segoe UI Semibold",9.5F))using(var subtitleFont=new Font("Segoe UI",8F))
            {
                TextRenderer.DrawText(e.Graphics,title??"未命名论文",titleFont,titleRect,fg,TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(e.Graphics,subtitle,subtitleFont,subRect,Color.FromArgb(130,125,138),TextFormatFlags.EndEllipsis|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
            }
            if(selected)using(var pen=new Pen(Color.FromArgb(255,190,61),3))e.Graphics.DrawLine(pen,e.Bounds.Left+1,e.Bounds.Top+8,e.Bounds.Left+1,e.Bounds.Bottom-8);
        }
        void ToggleLibraryDrawer(){SetLibraryDrawer(!drawerOpen);}
        void SetLibraryDrawer(bool open)
        {
            if(libraryDrawer==null||drawerAnimation==null)return;
            bool wasAnimating=drawerAnimation.Enabled,wasOpen=drawerOpen;
            drawerOpen=open;
            if(open)
            {
                drawerWidth=Math.Min(380,Math.Max(280,libraryDrawer.Parent.ClientSize.Width-80));libraryDrawer.Width=drawerWidth;libraryDrawer.Height=libraryDrawer.Parent.ClientSize.Height;if(!wasOpen&&!wasAnimating)libraryDrawer.Left=-drawerWidth;libraryDrawer.Visible=true;libraryDrawer.BringToFront();
            }
            drawerAnimation.Start();
        }

        void BuildPopup(Control parent)
        {
            popup = new Panel { Visible = false, Width = 400, Height = 230, BackColor = Color.White, Padding = new Padding(18), BorderStyle = BorderStyle.None, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            popup.Resize += delegate { using(var path=RoundedRect(new Rectangle(0,0,popup.Width-1,popup.Height-1),22)) popup.Region=new Region(path); };
            popup.Paint += delegate(object s,PaintEventArgs e){using(var path=RoundedRect(new Rectangle(1,1,popup.Width-3,popup.Height-3),21))using(var pen=new Pen(Color.FromArgb(92,61,122),2)){e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.DrawPath(pen,path);}};
            popupHeading = new Label { Text = "添加批注", Font = new Font("Segoe UI Semibold", 12), AutoSize = true, Location = new Point(14, 10) };
            var close = new Button { Text = "×", FlatStyle = FlatStyle.Flat, Size = new Size(30, 28), Location = new Point(350, 8), BackColor = Color.White, ForeColor = Color.FromArgb(57, 52, 79) };
            close.Click += delegate { popup.Visible = false;HideEditorConnector(); quoteBox.ReadOnly=false;noteBox.ReadOnly=false;kindBox.Enabled=true;popupSave.Visible=true; };
            kindBox.DropDownStyle = ComboBoxStyle.DropDownList; kindBox.Items.AddRange(new object[] { "高亮", "公式", "创新点", "实验", "疑问", "普通批注" }); kindBox.SelectedIndex = 0; kindBox.Location = new Point(14, 42); kindBox.Width = 160;
            quoteBox.Multiline = true; quoteBox.Visible=false; quoteBox.ScrollBars = ScrollBars.Vertical; quoteBox.BackColor = Color.FromArgb(255, 252, 244); quoteBox.PlaceholderTextCompat("选中的文字（可手动填写）");
            noteBox.Multiline = true; noteBox.Location = new Point(18, 82); noteBox.Size = new Size(364, 88); noteBox.ScrollBars = ScrollBars.Vertical; noteBox.BackColor = Color.FromArgb(255, 252, 244); noteBox.BorderStyle=BorderStyle.FixedSingle; noteBox.PlaceholderTextCompat("添加批注…"); noteBox.TextChanged += delegate { ResizePopupForText(); };noteBox.KeyDown+=delegate(object s,KeyEventArgs e){if(e.Control&&e.KeyCode==Keys.Back){DeletePreviousWord(noteBox);e.Handled=true;e.SuppressKeyPress=true;}};
            popupSave = Button("保存", true); popupSave.Location = new Point(292, 184); popupSave.Width = 90; popupSave.Click += SaveMark;
            Point editDrag=Point.Empty;bool editMoving=false;popupHeading.Cursor=Cursors.SizeAll;popupHeading.MouseDown+=delegate(object s,MouseEventArgs e){if(e.Button==MouseButtons.Left){editMoving=true;Point p=popup.Parent.PointToClient(Cursor.Position);editDrag=new Point(p.X-popup.Left,p.Y-popup.Top);}};popupHeading.MouseMove+=delegate(object s,MouseEventArgs e){if(editMoving){Point p=popup.Parent.PointToClient(Cursor.Position);popup.Location=new Point(Math.Max(0,Math.Min(popup.Parent.ClientSize.Width-popup.Width,p.X-editDrag.X)),Math.Max(0,Math.Min(popup.Parent.ClientSize.Height-popup.Height,p.Y-editDrag.Y)));UpdateEditorConnector();}};popupHeading.MouseUp+=delegate{editMoving=false;};
            popup.Controls.Add(popupHeading); popup.Controls.Add(close); popup.Controls.Add(kindBox); popup.Controls.Add(noteBox); popup.Controls.Add(popupSave); parent.Controls.Add(popup); popup.BringToFront();
        }
        void PlacePopup(PdfCanvas page,Rectangle target)
        {
            if(popup.Parent!=page){if(popup.Parent!=null)popup.Parent.Controls.Remove(popup);page.Controls.Add(popup);}
            popupPage=page;
            popupTargetBounds=target;int gap=22,x,y;if(target.Right+gap+popup.Width<=page.ClientSize.Width-8){x=target.Right+gap;y=target.Top+(target.Height-popup.Height)/2;}else if(target.Left-gap-popup.Width>=8){x=target.Left-gap-popup.Width;y=target.Top+(target.Height-popup.Height)/2;}else if(target.Bottom+gap+popup.Height<=page.ClientSize.Height-8){x=target.Left+(target.Width-popup.Width)/2;y=target.Bottom+gap;}else{x=target.Left+(target.Width-popup.Width)/2;y=target.Top-gap-popup.Height;}x=Math.Max(4,Math.Min(x,page.ClientSize.Width-popup.Width-4));y=Math.Max(4,Math.Min(y,page.ClientSize.Height-popup.Height-4));popup.Location=new Point(x,y);UpdateEditorConnector();popup.BringToFront();
        }
        void HideEditorConnector(){if(popupPage!=null)popupPage.ClearConnector(popup);}
        void UpdateEditorConnector(){if(popupPage==null||!popup.Visible){HideEditorConnector();return;}Point start=new Point(popupTargetBounds.Right,popupTargetBounds.Top+popupTargetBounds.Height/2);if(popup.Left<popupTargetBounds.Left)start.X=popupTargetBounds.Left;else if(popup.Top>popupTargetBounds.Bottom)start=new Point(popupTargetBounds.Left+popupTargetBounds.Width/2,popupTargetBounds.Bottom);else if(popup.Bottom<popupTargetBounds.Top)start=new Point(popupTargetBounds.Left+popupTargetBounds.Width/2,popupTargetBounds.Top);Point end=new Point(Math.Max(popup.Left,Math.Min(start.X,popup.Right)),Math.Max(popup.Top,Math.Min(start.Y,popup.Bottom)));if(popup.Bounds.Contains(start)){end=popup.Left>=popupTargetBounds.Right?new Point(popup.Left,popup.Top+popup.Height/2):popup.Top>=popupTargetBounds.Bottom?new Point(popup.Left+popup.Width/2,popup.Top):new Point(popup.Right,popup.Top+popup.Height/2);}popupPage.SetConnector(popup,start,end);}
        void ShowAnnotationPopup(PdfCanvas canvas,float x,float y,float w,float h) { popupHeading.Text="添加批注";quoteBox.Text=selectedPdfText;quoteBox.ReadOnly=false;noteBox.ReadOnly=false;kindBox.Enabled=true;popupSave.Visible=true;popup.Visible=true;popupX=x;popupY=y;popupW=w;popupH=h;PlacePopup(canvas,canvas.AnchorBounds(x,y,w,h));noteBox.Focus(); }
        float CanvasScale(PdfCanvas canvas)
        {
            if(canvas==null)return 1f;
            foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases)
                if(entry.Value==canvas){SizeF reference=ReferenceSizeForPage(entry.Key);if(reference.Height>0)return Math.Max(0.001f,canvas.ClientSize.Height/reference.Height);}
            return 1f;
        }
        static void LayoutMarkCard(Panel card)
        {
            const int side=8, headerHeight=20, editorLeft=30, editorTop=39, editorBottom=30;
            Panel header=card.Controls.OfType<Panel>().FirstOrDefault(delegate(Panel p){return String.Equals(p.Tag as string,"mark-card-header",StringComparison.Ordinal);});
            if(header!=null)
            {
                header.Bounds=new Rectangle(side,side,Math.Max(1,card.ClientSize.Width-side*2),headerHeight);
            }
            Panel editor=card.Controls.OfType<Panel>().FirstOrDefault(delegate(Panel p){return String.Equals(p.Tag as string,"mark-card-editor",StringComparison.Ordinal);});
            TextBox text=editor==null?card.Controls.OfType<TextBox>().FirstOrDefault():editor.Controls.OfType<TextBox>().FirstOrDefault();if(text==null)return;
            if(editor==null){text.Location=new Point(editorLeft,editorTop);text.Size=new Size(Math.Max(1,card.ClientSize.Width-editorLeft*2),Math.Max(1,card.ClientSize.Height-editorTop-editorBottom));return;}
            editor.Bounds=new Rectangle(editorLeft,editorTop,Math.Max(1,card.ClientSize.Width-editorLeft*2),Math.Max(90,card.ClientSize.Height-editorTop-editorBottom));
            text.Bounds=new Rectangle(12,8,Math.Max(1,editor.ClientSize.Width-24),Math.Max(24,editor.ClientSize.Height-56));
            Button cancel=editor.Controls.OfType<Button>().FirstOrDefault(delegate(Button b){return String.Equals(b.Tag as string,"mark-card-cancel",StringComparison.Ordinal);});
            Button publish=editor.Controls.OfType<Button>().FirstOrDefault(delegate(Button b){return String.Equals(b.Tag as string,"mark-card-publish",StringComparison.Ordinal);});
            int buttonWidth=84,buttonHeight=38,bottom=8,gap=12;
            if(cancel!=null)cancel.Bounds=new Rectangle(Math.Max(8,editor.ClientSize.Width-buttonWidth*2-gap-12),Math.Max(8,editor.ClientSize.Height-buttonHeight-bottom),buttonWidth,buttonHeight);
            if(publish!=null)publish.Bounds=new Rectangle(Math.Max(8,editor.ClientSize.Width-buttonWidth-12),Math.Max(8,editor.ClientSize.Height-buttonHeight-bottom),buttonWidth,buttonHeight);
        }
        static void ScaleMarkCardContents(Panel card,float scale,float fontPoints)
        {
            Panel editor=card.Controls.OfType<Panel>().FirstOrDefault(delegate(Panel p){return String.Equals(p.Tag as string,"mark-card-editor",StringComparison.Ordinal);});
            TextBox text=editor==null?card.Controls.OfType<TextBox>().FirstOrDefault():editor.Controls.OfType<TextBox>().FirstOrDefault();
            if(text!=null)SetMarkCardFont(text,12f);
            Panel header=card.Controls.OfType<Panel>().FirstOrDefault(delegate(Panel p){return String.Equals(p.Tag as string,"mark-card-header",StringComparison.Ordinal);});
            if(header!=null)
            {
            }
            if(editor!=null)foreach(Button button in editor.Controls.OfType<Button>())SetMarkCardFont(button,14f);
            LayoutMarkCard(card);
        }
        static void SetMarkCardFont(Control control,float points)
        {
            Font owned=new Font("Segoe UI",points,FontStyle.Regular,GraphicsUnit.Point);
            control.Font=owned;
            control.Disposed+=delegate{owned.Dispose();};
        }
        float MarkTextFontPoints(PdfCanvas canvas,float scale)
        {
            return 12f;
        }
        static void DeletePreviousWord(TextBox box)
        {
            if(box.SelectionLength>0){box.SelectedText="";return;}
            string value=box.Text;int end=box.SelectionStart,start=end;
            while(start>0&&Char.IsWhiteSpace(value[start-1]))start--;
            if(start>0)
            {
                char last=value[start-1];
                if(last>=0x3400&&last<=0x9FFF)start--;
                else if(Char.IsLetterOrDigit(last)||last=='_')while(start>0&&(Char.IsLetterOrDigit(value[start-1])||value[start-1]=='_'))start--;
                else while(start>0&&!Char.IsWhiteSpace(value[start-1])&&!Char.IsLetterOrDigit(value[start-1])&&value[start-1]!='_')start--;
            }
            if(start<end){box.Select(start,end-start);box.SelectedText="";}
        }
        bool HasSavedCardPosition(Mark mark)
        {
            if(mark.HasCardPosition)return true;
            if(mark.CardX>0||mark.CardY>0){mark.HasCardPosition=true;return true;}
            return false;
        }
        bool CardIsOnPreferredSide(PdfCanvas canvas,Mark mark,Rectangle cardBounds)
        {
            Rectangle document=canvas.AnchorBounds(0,0,1,1),target=canvas.AnchorBounds(mark.X,mark.Y,mark.W,mark.H);
            bool targetOnLeft=target.Left+target.Width/2<document.Left+document.Width/2;
            return targetOnLeft?cardBounds.Right<=document.Left:cardBounds.Left>=document.Right;
        }
        void RaiseCalloutLayers(PdfCanvas canvas)
        {
            ConnectorLayer layer;if(pageConnectorLayers.TryGetValue(canvas,out layer))layer.BringToFront();
            foreach(KeyValuePair<Mark,Panel> entry in markCards){PdfCanvas page;if(markCardPages.TryGetValue(entry.Key,out page)&&page==canvas)entry.Value.BringToFront();}
        }
        void RemoveMarkConnector(Mark mark,PdfCanvas canvas)
        {
            if(canvas!=null)canvas.ClearConnector(mark);
            ConnectorLayer line;if(!markCardLines.TryGetValue(mark,out line))return;
            line.RemoveSegment(mark);markCardLines.Remove(mark);
            if(line.SegmentCount==0){canvas.Controls.Remove(line);line.Dispose();pageConnectorLayers.Remove(canvas);}
        }
        Point FindCalloutLocation(PdfCanvas canvas,Mark mark,Rectangle target,int width,int height,float scale)
        {
            int gap=Math.Max(6,(int)Math.Round(18*scale));
            int maxX=Math.Max(4,canvas.ClientSize.Width-width-4),maxY=Math.Max(4,canvas.ClientSize.Height-height-4);
            Rectangle document=canvas.AnchorBounds(0,0,1,1);int cx=target.Left+(target.Width-width)/2,cy=target.Top+(target.Height-height)/2;
            var candidates=new List<Point>();
            Action<int,int> add=delegate(int x,int y){candidates.Add(new Point(Math.Max(4,Math.Min(x,maxX)),Math.Max(4,Math.Min(y,maxY))));};
            for(int i=0;i<10;i++)
            {
                int offset=i==0?0:((i+1)/2)*((i%2==1)?1:-1);
                int dy=offset*(height+gap);
                if(target.Left+target.Width/2<document.Left+document.Width/2){add(document.Left-width-gap,cy+dy);add(document.Right+gap,cy+dy);}
                else{add(document.Right+gap,cy+dy);add(document.Left-width-gap,cy+dy);}
            }
            // Places beside the annotation itself, so the callout never covers its own quote and
            // the leader line stays short.
            add(target.Right+gap,target.Top+(target.Height-height)/2);
            add(target.Left-width-gap,target.Top+(target.Height-height)/2);
            add(target.Left+(target.Width-width)/2,target.Bottom+gap);
            add(target.Left+(target.Width-width)/2,target.Top-height-gap);
            Rectangle best=Rectangle.Empty;double bestScore=Double.MaxValue;
            foreach(Point candidate in candidates)
            {
                Rectangle r=new Rectangle(candidate.X,candidate.Y,width,height);double overlap=0;
                foreach(KeyValuePair<Mark,Panel> entry in markCards)
                {
                    PdfCanvas page;if(entry.Key==mark||!entry.Value.Visible||!markCardPages.TryGetValue(entry.Key,out page)||page!=canvas)continue;
                    Rectangle intersection=Rectangle.Intersect(r,entry.Value.Bounds);overlap+=(double)intersection.Width*intersection.Height;
                }
                Rectangle covered=Rectangle.Intersect(r,target);
                double covering=(double)covered.Width*covered.Height;
                double distance=Math.Abs(candidate.X-cx)+Math.Abs(candidate.Y-cy);
                double score=(overlap+covering*2)*1000+distance;
                if(score<bestScore){bestScore=score;best=r;}
                if(overlap==0&&covering==0)break;
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
                if(m.CardPixelW<=0)m.CardPixelW=MarkCardDefaultWidth;
                if(m.CardPixelH<=0)m.CardPixelH=MarkCardDefaultHeight;
                card.Width=Math.Max(MarkCardMinimumWidth,m.CardPixelW);
                card.Height=Math.Max(MarkCardMinimumHeight,m.CardPixelH);
                m.CardPixelW=card.Width;m.CardPixelH=card.Height;markCardScales[m]=1f;
                Rectangle target=canvas.AnchorBounds(m.X,m.Y,m.W,m.H);
                bool positioned=HasSavedCardPosition(m);Rectangle document=canvas.AnchorBounds(0,0,1,1);Rectangle saved= new Rectangle((int)(m.CardX*canvas.ClientSize.Width),(int)(m.CardY*canvas.ClientSize.Height),card.Width,card.Height);if(positioned&&!m.CardManuallyPositioned&&!CardIsOnPreferredSide(canvas,m,saved))positioned=false;if(positioned&&!m.CardManuallyPositioned&&Rectangle.Intersect(saved,document).Width>0)positioned=false;
                Point auto=positioned?Point.Empty:FindCalloutLocation(canvas,m,target,card.Width,card.Height,1f);
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
            Panel existing;
            if(markCards.TryGetValue(mark,out existing))
            {
                // Clicking the annotation again shows its callout instead of toggling it, so a
                // double click can no longer open and immediately close the card.
                existing.Visible=true;existing.BringToFront();
                Panel surface=existing.Controls.OfType<Panel>().FirstOrDefault(delegate(Panel p){return String.Equals(p.Tag as string,"mark-card-editor",StringComparison.Ordinal);});TextBox editor=surface==null?existing.Controls.OfType<TextBox>().FirstOrDefault():surface.Controls.OfType<TextBox>().FirstOrDefault();if(editor!=null){editor.Tag=editor.Text;editor.Focus();}
                ConnectorLayer reopened;if(markCardLines.TryGetValue(mark,out reopened))UpdateMarkConnector(mark,canvas.AnchorBounds(mark.X,mark.Y,mark.W,mark.H),existing,reopened);
                canvas.Invalidate();return;
            }
            Rectangle target=canvas.AnchorBounds(mark.X,mark.Y,mark.W,mark.H);string body=mark.Note??"";
            int cardWidth=mark.CardPixelW>0?Math.Max(MarkCardMinimumWidth,mark.CardPixelW):MarkCardDefaultWidth;
            int height=mark.CardPixelH>0?Math.Max(MarkCardMinimumHeight,mark.CardPixelH):MarkCardDefaultHeight;
            mark.CardPixelW=cardWidth;mark.CardPixelH=height;mark.CardDocW=0;mark.CardDocH=0;markCardScales[mark]=1f;
            var card=new BufferedPanel{Width=cardWidth,Height=height,BackColor=Color.White,BorderStyle=BorderStyle.None,Cursor=Cursors.SizeAll,Tag=mark};
            card.Resize+=delegate{using(var p=RoundedRect(new Rectangle(0,0,Math.Max(1,card.Width-1),Math.Max(1,card.Height-1)),10))card.Region=new Region(p);LayoutMarkCard(card);};using(var initialPath=RoundedRect(new Rectangle(0,0,Math.Max(1,card.Width-1),Math.Max(1,card.Height-1)),10))card.Region=new Region(initialPath);
            card.Paint+=delegate(object s,PaintEventArgs e){using(var p=RoundedRect(new Rectangle(1,1,Math.Max(1,card.Width-3),Math.Max(1,card.Height-3)),10))using(var fill=new SolidBrush(Color.White))using(var pen=new Pen(flashingMarks.Contains(mark)?Color.FromArgb(240,255,153,0):Color.FromArgb(150,150,150),flashingMarks.Contains(mark)?3f:1.5f)){e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.FillPath(fill,p);e.Graphics.DrawPath(pen,p);}};
            bool positioned=HasSavedCardPosition(mark);Rectangle document=canvas.AnchorBounds(0,0,1,1);Rectangle saved=new Rectangle((int)(mark.CardX*canvas.ClientSize.Width),(int)(mark.CardY*canvas.ClientSize.Height),cardWidth,height);if(positioned&&!mark.CardManuallyPositioned&&!CardIsOnPreferredSide(canvas,mark,saved))positioned=false;if(positioned&&!mark.CardManuallyPositioned&&Rectangle.Intersect(saved,document).Width>0)positioned=false;Point auto=positioned?Point.Empty:FindCalloutLocation(canvas,mark,target,cardWidth,height,1f);int x=positioned?(int)(mark.CardX*canvas.ClientSize.Width):auto.X,y=positioned?(int)(mark.CardY*canvas.ClientSize.Height):auto.Y;x=Math.Max(4,Math.Min(x,canvas.ClientSize.Width-card.Width-4));y=Math.Max(4,Math.Min(y,canvas.ClientSize.Height-card.Height-4));mark.CardX=(float)x/Math.Max(1,canvas.ClientSize.Width);mark.CardY=(float)y/Math.Max(1,canvas.ClientSize.Height);mark.HasCardPosition=true;
            var header=new Panel{BackColor=Color.White,Cursor=Cursors.SizeAll,Tag="mark-card-header"};
            var text=new TextBox{Multiline=true,ReadOnly=false,AcceptsReturn=true,WordWrap=true,ScrollBars=ScrollBars.None,BorderStyle=BorderStyle.None,BackColor=Color.White,Text=body,Cursor=Cursors.IBeam,Tag=body};
            text.KeyDown+=delegate(object s,KeyEventArgs e){if(e.Control&&e.KeyCode==Keys.Back){DeletePreviousWord(text);e.Handled=true;e.SuppressKeyPress=true;}};
            var editorSurface=new Panel{BackColor=Color.White,BorderStyle=BorderStyle.None,Cursor=Cursors.SizeAll,Tag="mark-card-editor"};
            editorSurface.Paint+=delegate(object s,PaintEventArgs e){if(editorSurface.ClientSize.Width<4||editorSurface.ClientSize.Height<4)return;using(var pen=new Pen(Color.FromArgb(26,115,232),2f))e.Graphics.DrawRectangle(pen,1,1,editorSurface.ClientSize.Width-3,editorSurface.ClientSize.Height-3);};
            text.Enter+=delegate{editorSurface.Invalidate();};text.Leave+=delegate{editorSurface.Invalidate();};
            var cancelButton=new Button{Text="取消",FlatStyle=FlatStyle.Flat,BackColor=Color.White,ForeColor=Color.FromArgb(95,99,104),Cursor=Cursors.Hand,TabStop=false,Tag="mark-card-cancel"};cancelButton.FlatAppearance.BorderSize=0;
            var publishButton=new Button{Text="发布",FlatStyle=FlatStyle.Flat,BackColor=Color.White,ForeColor=Color.FromArgb(26,115,232),Cursor=Cursors.Hand,TabStop=false,Tag="mark-card-publish"};publishButton.FlatAppearance.BorderSize=0;
            editorSurface.Controls.Add(text);editorSurface.Controls.Add(cancelButton);editorSurface.Controls.Add(publishButton);
            var emptyTimer=new Timer{Interval=650};card.Disposed+=delegate{try{emptyTimer.Stop();emptyTimer.Dispose();}catch{}};emptyTimer.Tick+=delegate{emptyTimer.Stop();if(!text.IsDisposed&&String.IsNullOrWhiteSpace(text.Text)){Paper owner=null;foreach(Paper p in library.Papers)if(p.Marks!=null&&p.Marks.Contains(mark)){owner=p;break;}if(owner!=null)owner.Marks.Remove(mark);canvas.Invalidate();canvas.Controls.Remove(card);RemoveMarkConnector(mark,canvas);markCards.Remove(mark);markCardPages.Remove(mark);markCardScales.Remove(mark);card.Dispose();SaveLibrary();}};
            Action close=delegate{card.Visible=false;canvas.ClearConnector(mark);canvas.Invalidate();SaveLibrary();};
            cancelButton.Click+=delegate{string savedBody=text.Tag as string??"";if(!String.Equals(text.Text,savedBody,StringComparison.Ordinal)){text.Text=savedBody;mark.Note=savedBody;}emptyTimer.Stop();close();};
            publishButton.Click+=delegate{text.Tag=text.Text;mark.Note=text.Text;close();};
            text.TextChanged+=delegate{mark.Note=text.Text;mark.CardPixelW=card.Width;mark.CardPixelH=card.Height;card.Invalidate();canvas.Invalidate(Rectangle.Inflate(card.Bounds,8,8));ConnectorLayer activeLine;if(markCardLines.TryGetValue(mark,out activeLine))UpdateMarkConnector(mark,target,card,activeLine);SaveLibrary();if(String.IsNullOrWhiteSpace(text.Text))emptyTimer.Start();else emptyTimer.Stop();};
            card.Location=new Point(x,y);
            Point dragOffset=Point.Empty;bool moving=false;MouseEventHandler down=delegate(object s,MouseEventArgs e){if(e.Button!=MouseButtons.Left)return;Point p=canvas.PointToClient(Cursor.Position);int localX=p.X-card.Left,localY=p.Y-card.Top,edge=8;Control control=s as Control;bool headerSurface=Object.ReferenceEquals(s,header)||(control!=null&&Object.ReferenceEquals(control.Parent,header));if(headerSurface&&localX>=edge&&localX<card.Width-edge&&localY>=edge&&localY<card.Height-edge){moving=true;dragOffset=new Point(localX,localY);if(control!=null)control.Capture=true;}};
            MouseEventHandler move=delegate(object s,MouseEventArgs e){if(!moving)return;Rectangle old=card.Bounds;Point p=canvas.PointToClient(Cursor.Position);card.Left=Math.Max(0,Math.Min(canvas.ClientSize.Width-card.Width,p.X-dragOffset.X));card.Top=Math.Max(0,Math.Min(canvas.ClientSize.Height-card.Height,p.Y-dragOffset.Y));mark.CardX=(float)card.Left/Math.Max(1,canvas.ClientSize.Width);mark.CardY=(float)card.Top/Math.Max(1,canvas.ClientSize.Height);mark.HasCardPosition=true;mark.CardManuallyPositioned=true;canvas.Invalidate(Rectangle.Inflate(old,8,8));canvas.Invalidate(Rectangle.Inflate(card.Bounds,8,8));UpdateMarkConnector(mark,target,card,markCardLines[mark]);};
            MouseEventHandler up=delegate(object s,MouseEventArgs e){if(moving&&(e.Button==MouseButtons.Left||e.Button==MouseButtons.None)){moving=false;Control dragSurface=s as Control;if(dragSurface!=null)dragSurface.Capture=false;canvas.Invalidate();SaveLibrary();}};
            header.MouseDown+=down;header.MouseMove+=move;header.MouseUp+=up;foreach(Label label in header.Controls.OfType<Label>()){label.MouseDown+=down;label.MouseMove+=move;label.MouseUp+=up;}
            card.MouseWheel+=ReaderWheel;text.MouseWheel+=ReaderWheel;
            // Assemble the card completely before it is put on the page, then draw the leader
            // line once it is registered, so opening shows one frame instead of a sequence of
            // partial ones.
            card.Controls.Add(header);card.Controls.Add(editorSurface);AddResizeHandlers(card,text,canvas,mark);ScaleMarkCardContents(card,1f,MarkTextFontPoints(canvas,1f));
            markCards[mark]=card;markCardPages[mark]=canvas;
            ConnectorLayer line;if(!pageConnectorLayers.TryGetValue(canvas,out line)){line=new ConnectorLayer{Dock=DockStyle.Fill,Visible=false};canvas.Controls.Add(line);pageConnectorLayers[canvas]=line;}
            markCardLines[mark]=line;UpdateMarkConnector(mark,target,card,line);
            canvas.Controls.Add(card);RaiseCalloutLayers(canvas);
        }
        void HandleMarkPopupFailure(Mark mark,PdfCanvas canvas,Exception error)
        {
            Panel card;if(markCards.TryGetValue(mark,out card)){markCards.Remove(mark);if(card.Parent!=null)card.Parent.Controls.Remove(card);card.Dispose();}
            foreach(BufferedPanel orphan in canvas.Controls.OfType<BufferedPanel>().Where(delegate(BufferedPanel p){return Object.ReferenceEquals(p.Tag,mark);}).ToArray()){canvas.Controls.Remove(orphan);orphan.Dispose();}
            markCardPages.Remove(mark);markCardScales.Remove(mark);RemoveMarkConnector(mark,canvas);
            if(statusLabel!=null)statusLabel.Text="打开批注失败："+error.GetBaseException().Message;
            MessageBox.Show(this,"打开批注框失败：\r\n\r\n"+error,"PaperNote",MessageBoxButtons.OK,MessageBoxIcon.Error);
        }
        void UpdateMarkConnector(Mark mark,Rectangle target,Panel card,ConnectorLayer line)
        {
            int sx,sy,ex,ey;Point tc=new Point(target.Left+target.Width/2,target.Top+target.Height/2),cc=new Point(card.Left+card.Width/2,card.Top+card.Height/2);
            if(Math.Abs(cc.X-tc.X)>=Math.Abs(cc.Y-tc.Y)){if(cc.X>=tc.X){sx=target.Right;sy=tc.Y;ex=card.Left;ey=Math.Max(card.Top,Math.Min(sy,card.Bottom));}else{sx=target.Left;sy=tc.Y;ex=card.Right;ey=Math.Max(card.Top,Math.Min(sy,card.Bottom));}}
            else if(cc.Y>=tc.Y){sx=tc.X;sy=target.Bottom;ex=Math.Max(card.Left,Math.Min(sx,card.Right));ey=card.Top;}else{sx=tc.X;sy=target.Top;ex=Math.Max(card.Left,Math.Min(sx,card.Right));ey=card.Bottom;}
            PdfCanvas page;
            if(markCardPages.TryGetValue(mark,out page)&&page!=null)page.SetConnector(mark,new Point(sx,sy),new Point(ex,ey));
        }
        void FlashMarkCard(Mark mark,Panel card)
        {
            flashingMarks.Add(mark);card.BringToFront();card.Invalidate();
            var timer=new Timer{Interval=650};timer.Tick+=delegate{timer.Stop();timer.Dispose();flashingMarks.Remove(mark);if(!card.IsDisposed)card.Invalidate();};timer.Start();
        }
        void AddResizeHandlers(Panel card,TextBox text,PdfCanvas canvas,Mark mark)
        {
            int edges=0;Point origin=Point.Empty;Rectangle start=Rectangle.Empty;bool resizing=false;
            Func<Point,int> hit=delegate(Point p){int e=0;const int edgeGrip=8,cornerGrip=24;int gx=(p.Y<cornerGrip||p.Y>card.Height-cornerGrip)?cornerGrip:edgeGrip;int gy=(p.X<cornerGrip||p.X>card.Width-cornerGrip)?cornerGrip:edgeGrip;if(p.X<gx)e|=1;else if(p.X>card.Width-gx)e|=2;if(p.Y<gy)e|=4;else if(p.Y>card.Height-gy)e|=8;return e;};
            Action<MouseEventArgs> begin=delegate(MouseEventArgs e){if(e.Button!=MouseButtons.Left)return;Point p=canvas.PointToClient(Cursor.Position);edges=hit(new Point(p.X-card.Left,p.Y-card.Top));if(edges!=0){resizing=true;origin=p;start=card.Bounds;card.Capture=true;}};
            Action<MouseEventArgs> update=delegate(MouseEventArgs e){Point p=canvas.PointToClient(Cursor.Position);int edge=resizing?edges:hit(new Point(p.X-card.Left,p.Y-card.Top));if(!resizing){card.Cursor=(edge==5||edge==10)?Cursors.SizeNWSE:(edge==6||edge==9)?Cursors.SizeNESW:(edge==1||edge==2)?Cursors.SizeWE:(edge==4||edge==8)?Cursors.SizeNS:Cursors.SizeAll;return;}Rectangle old=card.Bounds;int minW=MarkCardMinimumWidth,minH=MarkCardMinimumHeight;int dx=p.X-origin.X,dy=p.Y-origin.Y;int left=start.Left,top=start.Top,width=start.Width,height=start.Height;if((edges&1)!=0){left=Math.Min(start.Right-minW,start.Left+dx);width=start.Right-left;}if((edges&2)!=0)width=Math.Max(minW,start.Width+dx);if((edges&4)!=0){top=Math.Min(start.Bottom-minH,start.Top+dy);height=start.Bottom-top;}if((edges&8)!=0)height=Math.Max(minH,start.Height+dy);width=Math.Min(width,canvas.ClientSize.Width);height=Math.Min(height,canvas.ClientSize.Height);left=Math.Max(0,Math.Min(left,canvas.ClientSize.Width-width));top=Math.Max(0,Math.Min(top,canvas.ClientSize.Height-height));card.Bounds=new Rectangle(left,top,width,height);LayoutMarkCard(card);mark.CardX=(float)left/Math.Max(1,canvas.ClientSize.Width);mark.CardY=(float)top/Math.Max(1,canvas.ClientSize.Height);mark.HasCardPosition=true;mark.CardManuallyPositioned=true;mark.CardPixelW=width;mark.CardPixelH=height;mark.CardDocW=0;mark.CardDocH=0;canvas.Invalidate(Rectangle.Inflate(old,8,8));canvas.Invalidate(Rectangle.Inflate(card.Bounds,8,8));UpdateMarkConnector(mark,canvas.AnchorBounds(mark.X,mark.Y,mark.W,mark.H),card,markCardLines[mark]);card.Invalidate();};
            MouseEventHandler down=delegate(object s,MouseEventArgs e){begin(e);};MouseEventHandler move=delegate(object s,MouseEventArgs e){update(e);};MouseEventHandler up=delegate(object s,MouseEventArgs e){if(resizing){resizing=false;card.Capture=false;SaveLibrary();}};
            card.MouseDown+=down;card.MouseMove+=move;card.MouseUp+=up;text.MouseDown+=down;text.MouseMove+=move;text.MouseUp+=up;
            Panel header=card.Controls.OfType<Panel>().FirstOrDefault(delegate(Panel p){return String.Equals(p.Tag as string,"mark-card-header",StringComparison.Ordinal);});
            if(header!=null){header.MouseDown+=down;header.MouseMove+=move;header.MouseUp+=up;Label title=header.Controls.OfType<Label>().FirstOrDefault();if(title!=null){title.MouseDown+=down;title.MouseMove+=move;title.MouseUp+=up;}}
        }
        List<WordBox> GetPageWords(string file,int pg)
        {
            string key=file+"|"+pg;
            lock(textPagesLock){List<WordBox> cached;if(textPages.TryGetValue(key,out cached))return cached;}
            List<WordBox> extracted=PdfiumEngine.ExtractWords(file,pg);
            lock(textPagesLock){List<WordBox> cached;if(textPages.TryGetValue(key,out cached))return cached;textPages[key]=extracted;}return extracted;
        }
        void LoadPageTextLayout(string file,int pg,PdfCanvas canvas)
        {
            string key=file+"|"+pg;List<WordBox> words;
            lock(textPagesLock){if(textPages.TryGetValue(key,out words)){canvas.SetTextLayout(words);RepositionPageCallouts(canvas);ApplyStoredCrossPageSelection(pg,canvas,words);return;}if(!textLoadsInFlight.Add(key))return;activeLoadCount++;ApplyCpuAffinity();}
            var worker=new BackgroundWorker();worker.DoWork+=delegate(object s,DoWorkEventArgs e){e.Result=GetPageWords(file,pg);};
            worker.RunWorkerCompleted+=delegate(object s,RunWorkerCompletedEventArgs e)
            {
                lock(textPagesLock)textLoadsInFlight.Remove(key);activeLoadCount=Math.Max(0,activeLoadCount-1);ApplyCpuAffinity();
                if(IsDisposed){if(activeLoadCount==0)TrimPageCache(IdleBitmapBudget);return;}
                if(e.Error!=null){if(statusLabel!=null)statusLabel.Text="PDF文字读取失败，当前页无法选择复制";if(activeLoadCount==0)TrimPageCache(IdleBitmapBudget);return;}
                PdfCanvas active;if(pageCanvases.TryGetValue(pg,out active)&&current!=null&&current.FilePath==file){List<WordBox> words=(List<WordBox>)e.Result;active.SetTextLayout(words);RepositionPageCallouts(active);ApplyStoredCrossPageSelection(pg,active,words);}else lock(textPagesLock)textPages.Remove(key);
                if(activeLoadCount==0)TrimPageCache(IdleBitmapBudget);
            };
            worker.RunWorkerAsync();
        }
        void ResizePopupForText()
        {
            if (popup == null) return; int lines = Math.Max(3, Math.Min(12, noteBox.Text.Split('\n').Length + noteBox.Text.Length / 48)); int h = 28 + lines * 20; noteBox.Height = h; popupSave.Top = noteBox.Bottom + 10; popup.Height = popupSave.Bottom + 12; if (popup.Visible) { using(var path=RoundedRect(new Rectangle(0,0,popup.Width-1,popup.Height-1),22))popup.Region=new Region(path);popup.Invalidate();UpdateEditorConnector();popup.BringToFront(); }
        }
        void ReaderWheel(object sender, MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                Point cursor=pdfViewport.PointToClient(Cursor.Position);float oldScale=fitMode?FitScale():zoomLevel;
                float factor=e.Delta>0?1.12f:1f/1.12f;float next=Math.Max(MinimumZoom,Math.Min(4f,oldScale*factor));
                if(Math.Abs(next-oldScale)<0.0001f)return;

                int oldDocumentWidth=documentWidth;
                double anchorDocumentX=cursor.X+virtualScrollX,anchorDocumentY=cursor.Y+virtualScrollY;
                int total=0;bool hasPageAnchor=current!=null&&pageCounts.TryGetValue(current.FilePath,out total)&&total>0;
                int anchorPage=hasPageAnchor?FindPageAt((int)anchorDocumentY,total,oldScale):currentPage;
                next=Math.Min(next,MaxZoomForPage(anchorPage));
                if(Math.Abs(next-oldScale)<0.0001f)return;
                SizeF oldReference=ReferenceSizeForPage(anchorPage);
                double oldPageWidth=oldReference.Width*oldScale,oldPageHeight=oldReference.Height*oldScale;
                double oldPageLeft=(oldDocumentWidth-oldPageWidth)*0.5;
                double oldPageTop=PageTop(anchorPage,oldScale);
                double pageX=oldPageWidth>0?(anchorDocumentX-oldPageLeft)/oldPageWidth:0.5;
                double pageY=oldPageHeight>0?(anchorDocumentY-oldPageTop)/oldPageHeight:0.5;
                // Zooming with the pointer over the blank side area used to anchor on that
                // blank space and shove the page out of the viewport; keep the anchor on the page.
                pageX=Math.Max(0,Math.Min(1,pageX));pageY=Math.Max(0,Math.Min(1,pageY));

                fitMode=false;zoomLevel=next;ResizeCanvas();
                if(hasPageAnchor)
                {
                    SizeF newReference=ReferenceSizeForPage(anchorPage);
                    double newPageWidth=newReference.Width*next,newPageHeight=newReference.Height*next;
                    double newPageLeft=(documentWidth-newPageWidth)*0.5,newPageTop=PageTop(anchorPage,next);
                    virtualScrollX=(int)Math.Round(newPageLeft+pageX*newPageWidth-cursor.X);
                    virtualScrollY=(int)Math.Round(newPageTop+pageY*newPageHeight-cursor.Y);
                }
                else
                {
                    double ratio=next/oldScale;
                    virtualScrollX=(int)Math.Round(documentWidth*0.5+(anchorDocumentX-oldDocumentWidth*0.5)*ratio-cursor.X);
                    virtualScrollY=(int)Math.Round(12+(anchorDocumentY-12)*ratio-cursor.Y);
                }
                ApplyVirtualScroll();
                return;
            }
            if((ModifierKeys&Keys.Shift)==Keys.Shift)ScrollHorizontal(-e.Delta);
        }
        bool ScrollHorizontal(int delta)
        {
            int max=GetMaxScrollX();if(max==0||delta==0)return false;
            int pixels=Math.Abs(delta)>=120?(delta/120)*90:delta*2;
            virtualScrollX=Math.Max(0,Math.Min(max,virtualScrollX+pixels));ApplyVirtualScroll();return true;
        }
        bool ScrollVertical(int delta)
        {
            int max=GetMaxScrollY();if(max==0||delta==0)return false;int pixels=Math.Abs(delta)>=120?(delta/120)*90:delta*2;
            virtualScrollY=Math.Max(0,Math.Min(max,virtualScrollY+pixels));ApplyVirtualScroll();return true;
        }
        internal static List<List<WordBox>> GroupVisualRows(List<WordBox> words)
        {
            var rows=new List<List<WordBox>>();if(words==null||words.Count==0)return rows;
            var ordered=words.FindAll(delegate(WordBox word){return word!=null&&!String.IsNullOrWhiteSpace(word.Text);});
            ordered.Sort(delegate(WordBox a,WordBox b){int y=((a.Y1+a.Y2)*0.5f).CompareTo((b.Y1+b.Y2)*0.5f);return y!=0?y:a.X1.CompareTo(b.X1);});
            float centerSum=0,maxHeight=0;int rowCount=0;
            foreach(WordBox word in ordered)
            {
                float center=(word.Y1+word.Y2)*0.5f,height=Math.Max(0,word.Y2-word.Y1);
                if(rows.Count==0||Math.Abs(center-centerSum/Math.Max(1,rowCount))>Math.Max(maxHeight,height)*0.4f)
                {rows.Add(new List<WordBox>());centerSum=center;maxHeight=height;rowCount=1;}
                else{centerSum+=center;maxHeight=Math.Max(maxHeight,height);rowCount++;}
                rows[rows.Count-1].Add(word);
            }
            return rows;
        }
        sealed class LayoutCut { public bool Vertical; public float Start,End,Score; }
        internal static List<List<WordBox>> BuildXYCutBlocks(List<WordBox> words,int depth)
        {
            var result=new List<List<WordBox>>();if(words==null||words.Count==0)return result;
            if(depth>=12||words.Count<16){result.Add(words);return result;}
            LayoutCut cut=FindXYCut(words);if(cut==null){result.Add(words);return result;}
            float split=(cut.Start+cut.End)*0.5f;var first=new List<WordBox>();var second=new List<WordBox>();
            foreach(WordBox word in words)
            {
                float center=cut.Vertical?(word.X1+word.X2)*0.5f:(word.Y1+word.Y2)*0.5f;
                if(center<split)first.Add(word);else second.Add(word);
            }
            if(first.Count<3||second.Count<3){result.Add(words);return result;}
            result.AddRange(BuildXYCutBlocks(first,depth+1));result.AddRange(BuildXYCutBlocks(second,depth+1));return result;
        }
        internal static List<List<WordBox>> BuildXYCutColumns(List<WordBox> words)
        {
            return BuildXYCutColumns(words,0);
        }
        static List<List<WordBox>> BuildXYCutColumns(List<WordBox> words,int depth)
        {
            var columns=new List<List<WordBox>>();if(words==null||words.Count==0)return columns;
            if(depth>=12||words.Count<16){columns.Add(new List<WordBox>(words));return columns;}
            LayoutCut cut=FindXYCut(words);if(cut==null){columns.Add(new List<WordBox>(words));return columns;}
            float split=(cut.Start+cut.End)*0.5f;var first=new List<WordBox>();var second=new List<WordBox>();
            foreach(WordBox word in words)
            {
                float center=cut.Vertical?(word.X1+word.X2)*0.5f:(word.Y1+word.Y2)*0.5f;
                if(center<split)first.Add(word);else second.Add(word);
            }
            if(first.Count<3||second.Count<3){columns.Add(new List<WordBox>(words));return columns;}
            List<List<WordBox>> a=BuildXYCutColumns(first,depth+1),b=BuildXYCutColumns(second,depth+1);
            if(cut.Vertical){columns.AddRange(a);columns.AddRange(b);}
            else{MergeColumnGroups(columns,a);MergeColumnGroups(columns,b);}
            columns.Sort(delegate(List<WordBox> left,List<WordBox> right){return left.Min(delegate(WordBox w){return w.X1;}).CompareTo(right.Min(delegate(WordBox w){return w.X1;}));});
            return columns;
        }
        static void MergeColumnGroups(List<List<WordBox>> target,List<List<WordBox>> source)
        {
            foreach(List<WordBox> group in source)
            {
                if(group==null||group.Count==0)continue;float left=group.Min(delegate(WordBox w){return w.X1;}),right=group.Max(delegate(WordBox w){return w.X2;}),width=right-left;int match=-1;
                if(width<0.72f)
                {
                    for(int i=0;i<target.Count;i++)
                    {
                        List<WordBox> existing=target[i];float existingLeft=existing.Min(delegate(WordBox w){return w.X1;}),existingRight=existing.Max(delegate(WordBox w){return w.X2;}),existingWidth=existingRight-existingLeft;
                        if(existingWidth>=0.72f)continue;float overlap=Math.Max(0,Math.Min(right,existingRight)-Math.Max(left,existingLeft));
                        if(overlap>=Math.Min(width,existingWidth)*0.35f){match=i;break;}
                    }
                }
                if(match>=0)target[match].AddRange(group);else target.Add(new List<WordBox>(group));
            }
        }
        static LayoutCut FindXYCut(List<WordBox> words)
        {
            if(words==null||words.Count<16)return null;
            var heights=new List<float>();foreach(WordBox word in words)if(word!=null&&word.Y2>word.Y1)heights.Add(word.Y2-word.Y1);
            if(heights.Count<8)return null;heights.Sort();float textHeight=heights[heights.Count/2];
            LayoutCut best=null;
            for(int axis=0;axis<2;axis++)
            {
                bool vertical=axis==0;
                if(vertical)
                {
                    // Use a row-frequency projection for vertical cuts. A single footer,
                    // title fragment, or equation can cross the gutter without erasing a
                    // persistent column boundary across the body text.
                    const int bins=500;List<List<WordBox>> rows=GroupVisualRows(words);if(rows.Count<6)continue;
                    int[] occupiedRows=new int[bins];
                    foreach(List<WordBox> row in rows)
                    {
                        bool[] occupied=new bool[bins];
                        foreach(WordBox word in row)
                        {
                            if(word==null||word.X2<=word.X1||word.X2-word.X1>0.24f)continue;
                            int from=Math.Max(0,Math.Min(bins-1,(int)Math.Floor(word.X1*bins)));
                            int to=Math.Max(from,Math.Min(bins-1,(int)Math.Ceiling(word.X2*bins)-1));
                            for(int x=from;x<=to;x++)occupied[x]=true;
                        }
                        for(int x=0;x<bins;x++)if(occupied[x])occupiedRows[x]++;
                    }
                    int toleratedRows=Math.Max(1,(int)Math.Ceiling(rows.Count*0.05));
                    float verticalMinGap=Math.Max(0.004f,textHeight*0.4f);
                    for(int x=(int)(0.25f*bins);x<(int)(0.75f*bins);)
                    {
                        if(occupiedRows[x]>toleratedRows){x++;continue;}
                        int from=x;while(x<(int)(0.75f*bins)&&occupiedRows[x]<=toleratedRows)x++;int to=x-1;
                        float start=from/(float)bins,end=(to+1)/(float)bins;
                        if(end-start>=verticalMinGap)ConsiderXYCut(words,true,start,end,textHeight,ref best);
                    }
                    continue;
                }
                var intervals=new List<RectangleF>();
                foreach(WordBox word in words)
                {
                    if(word==null||word.X2<=word.X1||word.Y2<=word.Y1)continue;
                    float extent=word.Y2-word.Y1;
                    if(extent>Math.Max(0.08f,textHeight*5f))continue;
                    intervals.Add(RectangleF.FromLTRB(word.Y1,0,word.Y2,1));
                }
                if(intervals.Count<8)continue;intervals.Sort(delegate(RectangleF a,RectangleF b){return a.Left.CompareTo(b.Left);});
                float minimumGap=Math.Max(0.009f,textHeight*1.25f);
                float mergedRight=intervals[0].Right;
                for(int i=1;i<intervals.Count;i++)
                {
                    RectangleF interval=intervals[i];float gap=interval.Left-mergedRight;
                    if(gap>=minimumGap)
                    {
                        ConsiderXYCut(words,false,mergedRight,interval.Left,textHeight,ref best);
                    }
                    mergedRight=Math.Max(mergedRight,interval.Right);
                }
            }
            return best;
        }
        static void ConsiderXYCut(List<WordBox> words,bool vertical,float start,float end,float textHeight,ref LayoutCut best)
        {
            float split=(start+end)*0.5f;int before=0,after=0,crossing=0;
            foreach(WordBox word in words)
            {
                float a=vertical?word.X1:word.Y1,b=vertical?word.X2:word.Y2;
                if(b<=start)before++;else if(a>=end)after++;else if(a<end&&b>start)crossing++;
            }
            int minimumSide=Math.Max(3,Math.Min(12,words.Count/12));
            int maxCrossing=vertical?Math.Max(2,words.Count/100):0;
            if(before<minimumSide||after<minimumSide||crossing>maxCrossing)return;
            float score=(end-start)/Math.Max(0.001f,textHeight);
            if(vertical)
            {
                int rowsBothSides=0;
                foreach(List<WordBox> row in GroupVisualRows(words))
                {
                    bool left=false,right=false;
                    foreach(WordBox word in row){float x=(word.X1+word.X2)*0.5f;if(x<start)left=true;else if(x>end)right=true;}
                    if(left&&right)rowsBothSides++;
                }
                if(rowsBothSides<3)return;
            }
            if(best==null||score>best.Score)best=new LayoutCut{Vertical=vertical,Start=start,End=end,Score=score};
        }
        static string FormatVisualRows(List<WordBox> words)
        {
            List<List<WordBox>> rows=GroupVisualRows(words);
            var output=new List<string>();
            foreach(List<WordBox> row in rows)
            {
                row.Sort(delegate(WordBox a,WordBox b){return a.X1.CompareTo(b.X1);});var line=new StringBuilder();string previous="";
                foreach(WordBox word in row)
                {
                    string value=(word.Text??"").Trim();if(value.Length==0)continue;
                    char first=value[0],last=previous.Length==0?'\0':previous[previous.Length-1];
                    bool closingPunctuation=".,;:!?%)]}»”’".IndexOf(first)>=0,openingPunctuation="([{«“‘".IndexOf(last)>=0;
                    if(line.Length>0&&!closingPunctuation&&!openingPunctuation)line.Append(' ');
                    line.Append(value);previous=value;
                }
                if(line.Length>0)output.Add(line.ToString());
            }
            return String.Join(Environment.NewLine,output.ToArray());
        }
        internal static string FormatWordsInReadingOrder(List<WordBox> words,List<WordBox> pageWords)
        {
            if(words==null||words.Count==0)return "";
            string tagged=FormatTaggedReadingOrder(words);if(!String.IsNullOrWhiteSpace(tagged))return tagged;
            List<List<WordBox>> blocks=BuildXYCutBlocks(pageWords??words,0);var selected=new HashSet<WordBox>(words);var output=new List<string>();var emitted=new HashSet<WordBox>();
            foreach(List<WordBox> block in blocks)
            {
                var part=new List<WordBox>();foreach(WordBox word in block)if(selected.Contains(word)){part.Add(word);emitted.Add(word);}
                string formatted=FormatVisualRows(part);if(!String.IsNullOrWhiteSpace(formatted))output.Add(formatted);
            }
            if(emitted.Count<words.Count){var remainder=new List<WordBox>();foreach(WordBox word in words)if(!emitted.Contains(word))remainder.Add(word);string formatted=FormatVisualRows(remainder);if(!String.IsNullOrWhiteSpace(formatted))output.Add(formatted);}
            return output.Count==0?FormatVisualRows(words):String.Join(Environment.NewLine+Environment.NewLine,output.ToArray());
        }
        internal static List<WordBox> BuildReadingOrderWords(List<WordBox> words)
        {
            var ordered=new List<WordBox>();if(words==null||words.Count==0)return ordered;
            bool tagged=true;foreach(WordBox word in words)if(word==null||word.TaggedOrder<0||word.TaggedCharIndex<0){tagged=false;break;}
            if(tagged)
            {
                ordered.AddRange(words);ordered.Sort(delegate(WordBox a,WordBox b){int order=a.TaggedOrder.CompareTo(b.TaggedOrder);return order!=0?order:a.TaggedCharIndex.CompareTo(b.TaggedCharIndex);});return ordered;
            }
            foreach(List<WordBox> block in BuildXYCutBlocks(words,0))
                foreach(List<WordBox> row in GroupVisualRows(block))
                {
                    row.Sort(delegate(WordBox a,WordBox b){return a.X1.CompareTo(b.X1);});ordered.AddRange(row);
                }
            return ordered;
        }
        static string FormatTaggedReadingOrder(List<WordBox> words)
        {
            if(words==null||words.Count<2)return null;
            foreach(WordBox word in words)if(word==null||word.TaggedOrder<0||word.TaggedCharIndex<0)return null;
            var ordered=new List<WordBox>(words);ordered.Sort(delegate(WordBox a,WordBox b){int order=a.TaggedOrder.CompareTo(b.TaggedOrder);return order!=0?order:a.TaggedCharIndex.CompareTo(b.TaggedCharIndex);});
            var output=new StringBuilder();WordBox previous=null;
            foreach(WordBox word in ordered)
            {
                string value=(word.Text??"").Trim();if(value.Length==0)continue;
                if(previous!=null)
                {
                    float previousCenter=(previous.Y1+previous.Y2)*0.5f,currentCenter=(word.Y1+word.Y2)*0.5f,height=Math.Max(previous.Y2-previous.Y1,word.Y2-word.Y1);
                    if(Math.Abs(currentCenter-previousCenter)>height*0.55f)
                    {
                        bool resetToEarlierLine=currentCenter<previousCenter-height*0.55f;
                        bool paragraphGap=Math.Abs(currentCenter-previousCenter)>height*1.65f;
                        output.Append(resetToEarlierLine||paragraphGap?Environment.NewLine+Environment.NewLine:Environment.NewLine);
                    }
                    else
                    {
                        char first=value[0],last=(previous.Text??"").TrimEnd().LastOrDefault();
                        bool closingPunctuation=".,;:!?%)]}»”’".IndexOf(first)>=0,openingPunctuation="([{«“‘".IndexOf(last)>=0;
                        if(!closingPunctuation&&!openingPunctuation)output.Append(' ');
                    }
                }
                output.Append(value);previous=word;
            }
            return output.ToString();
        }
        void StartCrossPageDrag(int page,int wordIndex)
        {
            ClearCrossPageSelection();crossPageDragTracking=true;crossPageDragActive=false;crossPageDragMoved=false;crossPageDragStartPage=page;crossPageDragStartWord=wordIndex;crossPageDragEndPage=page;crossPageDragEndWord=wordIndex;
            if(crossPageDragTimer!=null)crossPageDragTimer.Start();
        }
        void StopCrossPageDrag()
        {
            crossPageDragTracking=false;if(crossPageDragTimer!=null)crossPageDragTimer.Stop();
        }
        void AutoScrollCrossPageDrag()
        {
            if(!crossPageDragTracking||!crossPageDragMoved||pdfViewport==null||!pdfViewport.IsHandleCreated)return;
            Point point=pdfViewport.PointToClient(Cursor.Position);if(!pdfViewport.ClientRectangle.Contains(point))return;
            if(point.Y<24)ScrollVertical(-24);else if(point.Y>pdfViewport.ClientSize.Height-24)ScrollVertical(24);
            UpdateCrossPageDrag(Cursor.Position);
        }
        bool UpdateCrossPageDrag(Point screenPoint)
        {
            if(!crossPageDragTracking||current==null)return false;
            crossPageDragMoved=true;
            Point viewportPoint=pdfViewport.PointToClient(screenPoint);if(!pdfViewport.ClientRectangle.Contains(viewportPoint))return crossPageDragActive;
            int total;if(!pageCounts.TryGetValue(current.FilePath,out total)||total<1)return false;
            float scale=fitMode?FitScale():zoomLevel;int page=FindPageAt(virtualScrollY+viewportPoint.Y,total,scale);
            if(page==crossPageDragStartPage&&!crossPageDragActive)return false;
            crossPageDragActive=true;
            SizeF reference=ReferenceSizeForPage(page);float pageWidth=Math.Max(1,reference.Width*scale),pageHeight=Math.Max(1,reference.Height*scale);
            float pageLeft=(documentWidth-pageWidth)*0.5f-virtualScrollX,pageTop=PageTop(page,scale)-virtualScrollY;
            float nx=Math.Max(0,Math.Min(1,(viewportPoint.X-pageLeft)/pageWidth)),ny=Math.Max(0,Math.Min(1,(viewportPoint.Y-pageTop)/pageHeight));
            EnsurePageCanvas(page);PdfCanvas canvas; if(!pageCanvases.TryGetValue(page,out canvas))return true;
            List<WordBox> words=GetPageWords(current.FilePath,page);canvas.SetTextLayout(words);
            int word=canvas.FindWordIndexAt(nx,ny,26f/pageWidth,30f/pageHeight);
            if(word<0&&words.Count>0)
            {
                if(ny<=words[0].Y1)word=0;else if(ny>=words[words.Count-1].Y2)word=words.Count-1;
            }
            if(word>=0||words.Count==0)
            {
                if(page==crossPageDragEndPage&&word==crossPageDragEndWord)return true;
                crossPageDragEndPage=page;crossPageDragEndWord=word;ApplyCrossPageRange(page,word);
            }
            return true;
        }
        bool EndCrossPageDrag(Point screenPoint,bool crossed)
        {
            if(!crossed&&!crossPageDragActive){StopCrossPageDrag();return false;}
            UpdateCrossPageDrag(screenPoint);StopCrossPageDrag();return crossPageSelectedWords.Count>1;
        }
        void ClearCrossPageSelection()
        {
            if(crossPageSelectedWords.Count==0)return;
            foreach(int page in crossPageSelectedWords.Keys){PdfCanvas canvas;if(pageCanvases.TryGetValue(page,out canvas))canvas.ClearRangeSelectionVisual();}
            crossPageSelectedWords.Clear();crossPageSelectedText="";selectedPdfText="";selectedPdfCanvas=null;
        }
        List<WordBox> GetCrossPageWords(int page,List<WordBox> allWords,int startPage,int startWord,int endPage,int endWord)
        {
            var result=new List<WordBox>();if(allWords==null||allWords.Count==0)return result;
            bool forward=startPage<endPage||(startPage==endPage&&startWord<=endWord);int firstPage=Math.Min(startPage,endPage),lastPage=Math.Max(startPage,endPage);
            int from=page==firstPage?(forward?startWord:endWord):0;int to=page==lastPage?(forward?endWord:startWord):allWords.Count-1;
            from=Math.Max(0,Math.Min(allWords.Count-1,from));to=Math.Max(0,Math.Min(allWords.Count-1,to));
            if(from<=to)for(int i=from;i<=to;i++)result.Add(allWords[i]);return result;
        }
        void ApplyCrossPageRange(int endPage,int endWord)
        {
            if(current==null)return;int first=Math.Min(crossPageDragStartPage,endPage),last=Math.Max(crossPageDragStartPage,endPage);
            foreach(int oldPage in crossPageSelectedWords.Keys){if(oldPage<first||oldPage>last){PdfCanvas oldCanvas;if(pageCanvases.TryGetValue(oldPage,out oldCanvas))oldCanvas.ClearRangeSelectionVisual();}}
            var next=new Dictionary<int,List<WordBox>>();var textByPage=new List<string>();
            for(int page=first;page<=last;page++)
            {
                List<WordBox> all=GetPageWords(current.FilePath,page);List<WordBox> selected=GetCrossPageWords(page,all,crossPageDragStartPage,crossPageDragStartWord,endPage,endWord);next[page]=selected;
                if(selected.Count>0)textByPage.Add(FormatWordsInReadingOrder(selected,all));else textByPage.Add("");
                PdfCanvas canvas;if(pageCanvases.TryGetValue(page,out canvas))ApplyCrossPageSelectionToCanvas(page,canvas,selected);
            }
            crossPageSelectedWords=next;crossPageSelectedText=String.Join(Environment.NewLine+Environment.NewLine,textByPage.ToArray());selectedPdfText=crossPageSelectedText;crossPageDragEndPage=endPage;crossPageDragEndWord=endWord;
            PdfCanvas endpoint;if(pageCanvases.TryGetValue(endPage,out endpoint)){pdfCanvas=endpoint;selectedPdfCanvas=endpoint;currentPage=endPage;pageBox.Text=endPage.ToString();}
            statusLabel.Text="已选中第 "+first+"–"+last+" 页内容，Ctrl+C 复制";
        }
        void ApplyCrossPageSelectionToCanvas(int page,PdfCanvas canvas,List<WordBox> selected)
        {
            if(selected==null||selected.Count==0){canvas.ClearRangeSelectionVisual();return;}
            canvas.SetRangeSelection(selected);
        }
        void ApplyStoredCrossPageSelection(int page,PdfCanvas canvas,List<WordBox> words)
        {
            List<WordBox> selected;if(crossPageSelectedWords.TryGetValue(page,out selected))ApplyCrossPageSelectionToCanvas(page,canvas,selected);
        }
        int GetMaxScrollX(){return Math.Max(0,documentWidth-(pdfViewport==null?0:pdfViewport.ClientSize.Width));}
        int GetMaxScrollY(){return Math.Max(0,documentHeight-(pdfViewport==null?0:pdfViewport.ClientSize.Height));}
        int ScrollValueToOffset(int value,int maxOffset){return maxOffset<=0?0:(int)((long)Math.Max(0,Math.Min(ScrollBarLogicalRange,value))*maxOffset/ScrollBarLogicalRange);}
        int ScrollOffsetToValue(int offset,int maxOffset){return maxOffset<=0?0:(int)((long)Math.Max(0,Math.Min(maxOffset,offset))*ScrollBarLogicalRange/maxOffset);}
        void ConfigureScrollBar(ScrollBar bar,int maxOffset,int viewportSize,int contentSize,int offset)
        {
            bool visible=maxOffset>0;if(bar.Visible!=visible)bar.Visible=visible;
            int largeChange=contentSize<=0?1:(int)Math.Max(1,Math.Min(ScrollBarLogicalRange,(long)ScrollBarLogicalRange*Math.Max(1,viewportSize)/contentSize));
            bar.Minimum=0;bar.LargeChange=largeChange;bar.SmallChange=Math.Max(1,ScrollBarLogicalRange/100);bar.Maximum=ScrollBarLogicalRange+largeChange-1;
            int value=ScrollOffsetToValue(offset,maxOffset);int maxValue=bar.Maximum-bar.LargeChange+1;bar.Value=Math.Max(bar.Minimum,Math.Min(maxValue,value));
        }
        void UpdateScrollBars()
        {
            if(pdfVerticalScrollBar==null||pdfHorizontalScrollBar==null)return;updatingScrollBars=true;
            try{ConfigureScrollBar(pdfVerticalScrollBar,GetMaxScrollY(),pdfViewport.ClientSize.Height,documentHeight,virtualScrollY);ConfigureScrollBar(pdfHorizontalScrollBar,GetMaxScrollX(),pdfViewport.ClientSize.Width,documentWidth,virtualScrollX);}
            finally{updatingScrollBars=false;}
        }
        void LayoutPagesAtCurrentScroll()
        {
            if(pdfPages==null)return;float scale=fitMode?FitScale():zoomLevel;
            foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases)
            {
                PdfCanvas page=entry.Value;int x=(documentWidth-page.Width)/2-virtualScrollX;int y=PageTop(entry.Key,scale)-virtualScrollY;Point location=new Point(x,y);if(page.Location!=location)page.Location=location;
            }
        }
        void ApplyVirtualScroll()
        {
            virtualScrollX=Math.Max(0,Math.Min(GetMaxScrollX(),virtualScrollX));virtualScrollY=Math.Max(0,Math.Min(GetMaxScrollY(),virtualScrollY));
            LayoutPagesAtCurrentScroll();UpdateScrollBars();lastViewportScroll=DateTime.UtcNow;EnsureVisiblePages();UpdatePageIndicator();
        }
        public bool PreFilterMessage(ref Message m)
        {
            const int WM_KEYDOWN=0x0100,WM_MOUSEWHEEL=0x020A,WM_MOUSEHWHEEL=0x020E;
            if(m.Msg==WM_KEYDOWN&&(int)m.WParam==(int)Keys.C&&(Control.ModifierKeys&(Keys.Control|Keys.Shift))==(Keys.Control|Keys.Shift)&&!String.IsNullOrWhiteSpace(selectedPdfText))
            {
                if(!HasEditorSelection()){CopyPdfSelection(true);return true;}
            }
            if(m.Msg==WM_KEYDOWN&&(int)m.WParam== (int)Keys.C&&(Control.ModifierKeys&Keys.Control)==Keys.Control&&!HasEditorSelection())
            {
                if(!String.IsNullOrWhiteSpace(selectedPdfText)){CopyPdfSelection(false);return true;}
            }
            if((m.Msg!=WM_MOUSEHWHEEL&&m.Msg!=WM_MOUSEWHEEL)||pdfViewport==null||!pdfViewport.IsHandleCreated)return false;
            if(!pdfViewport.RectangleToScreen(pdfViewport.ClientRectangle).Contains(Cursor.Position))return false;
            int delta=(short)(((long)m.WParam>>16)&0xffff);
            if(m.Msg==WM_MOUSEHWHEEL)return ScrollHorizontal(delta);
            if((Control.ModifierKeys&Keys.Shift)==Keys.Shift)return ScrollHorizontal(-delta);
            if((Control.ModifierKeys&Keys.Control)==Keys.Control)return false;
            return ScrollVertical(-delta);
        }
        int PageDpi(int page){int dpi;return pageDpi.TryGetValue(page,out dpi)?dpi:FullDpi;}
        // Resolution that maps one PDF point to one screen pixel at this zoom: page geometry
        // is measured in FullDpi units, so a scale of 1.0 means 600 device pixels per inch.
        int DisplayDpi(float scale){return Math.Max(72,(int)Math.Round(FullDpi*Math.Max(0.02f,scale)));}
        int MaxFullPageDpi(int page)
        {
            SizeF reference=ReferenceSizeForPage(page);
            double widthPoints=Math.Max(1,reference.Width*72.0/FullDpi),heightPoints=Math.Max(1,reference.Height*72.0/FullDpi);
            double dimensionLimit=Math.Min(MaxFullPageDimension*72.0/widthPoints,MaxFullPageDimension*72.0/heightPoints);
            double pixelLimit=Math.Sqrt(MaxFullPagePixels*72.0*72.0/(widthPoints*heightPoints));
            return Math.Max(96,(int)Math.Floor(Math.Min(dimensionLimit,pixelLimit)));
        }
        int TargetPageDpi(int page,float scale)
        {
            // Whole-page bitmaps stay inside a memory budget. Past that the page is shown at
            // its best affordable resolution and the visible part is re-rendered on top of it.
            return Math.Max(96,Math.Min(DisplayDpi(scale),MaxFullPageDpi(page)));
        }
        int PreviewPageDpi(int page)
        {
            float scale=fitMode?FitScale():zoomLevel;
            int preview=Math.Min(PreviewDpi,Math.Max(120,(int)Math.Round(DisplayDpi(scale)*1.25)));
            // Before any page has reported its size FitScale() has nothing to fit and would
            // ask for a huge bitmap; keep the very first paint cheap instead.
            if(fitMode&&pageReferenceSize.Count==0)preview=Math.Min(preview,150);
            return Math.Max(72,Math.Min(preview,TargetPageDpi(page,scale)));
        }
        bool HasEditorSelection()
        {
            if((noteBox.Focused&&noteBox.SelectionLength>0)||(quoteBox.Focused&&quoteBox.SelectionLength>0))return true;
            foreach(Panel card in markCards.Values){TextBox t=card.Controls.OfType<TextBox>().FirstOrDefault();if(t!=null&&t.Focused&&t.SelectionLength>0)return true;}
            return false;
        }
        void CopyPdfSelection(bool asFormula,string text=null)
        {
            bool multiPage=crossPageSelectedWords.Count>1;string selected=multiPage?crossPageSelectedText:(String.IsNullOrWhiteSpace(text)?selectedPdfText:text);
            if(String.IsNullOrWhiteSpace(selected))return;
            try
            {
                Clipboard.SetText(asFormula?FormulaToLatex(selected):selected);
                statusLabel.Text=asFormula?"公式文字已复制为 LaTeX":(multiPage?"跨页文字已复制（"+crossPageSelectedWords.Count+" 页）":"所选 PDF 文字已复制");
            }
            catch(Exception ex){statusLabel.Text="复制失败："+ex.Message;}
        }
        Bitmap CreateCrossPageComposite()
        {
            if(current==null||crossPageSelectedWords.Count==0||crossPageSelectedWords.Count>12)return null;
            int first=Math.Min(crossPageDragStartPage,crossPageDragEndPage),last=Math.Max(crossPageDragStartPage,crossPageDragEndPage);var crops=new List<Bitmap>();int width=1,height=0;
            try
            {
                for(int page=first;page<=last;page++)
                {
                    List<WordBox> words;if(!crossPageSelectedWords.TryGetValue(page,out words))continue;
                    int dpi=96;PdfiumEngine.RenderedPage rendered=PdfiumEngine.RenderPage(current.FilePath,page,dpi);Bitmap source=rendered.Bitmap;Bitmap crop=null;
                    try
                    {
                        RectangleF region;
                        if(words==null||words.Count==0)region=new RectangleF(0,0,1,1);
                        else
                        {
                            float left=words.Min(delegate(WordBox word){return word.X1;}),top=words.Min(delegate(WordBox word){return word.Y1;}),right=words.Max(delegate(WordBox word){return word.X2;}),bottom=words.Max(delegate(WordBox word){return word.Y2;});
                            region=new RectangleF(left,top,Math.Max(0.001f,right-left),Math.Max(0.001f,bottom-top));
                            List<RectangleF> graphics=PdfiumEngine.GetGraphicObjectRegions(current.FilePath,page,region,source.Width,source.Height);
                            foreach(RectangleF graphic in graphics)region=RectangleF.Union(region,graphic);
                        }
                        int x=Math.Max(0,(int)Math.Floor(region.Left*source.Width)),y=Math.Max(0,(int)Math.Floor(region.Top*source.Height));
                        int rightPx=Math.Min(source.Width,(int)Math.Ceiling(region.Right*source.Width)),bottomPx=Math.Min(source.Height,(int)Math.Ceiling(region.Bottom*source.Height));
                        if(rightPx<=x||bottomPx<=y)continue;
                        crop=new Bitmap(rightPx-x,bottomPx-y,PixelFormat.Format32bppArgb);
                        using(Graphics g=Graphics.FromImage(crop))g.DrawImage(source,new Rectangle(0,0,crop.Width,crop.Height),new Rectangle(x,y,crop.Width,crop.Height),GraphicsUnit.Pixel);
                        width=Math.Max(width,crop.Width);height+=crop.Height+12;crops.Add(crop);crop=null;
                    }
                    finally{if(crop!=null)crop.Dispose();source.Dispose();}
                }
                if(crops.Count==0||height>18000||(long)width*height>42000000)return null;
                height-=12;var output=new Bitmap(width,height,PixelFormat.Format32bppArgb);
                using(Graphics g=Graphics.FromImage(output)){g.Clear(Color.White);int y=0;foreach(Bitmap crop in crops){g.DrawImageUnscaled(crop,0,y);y+=crop.Height+12;}}
                return output;
            }
            finally{foreach(Bitmap crop in crops)crop.Dispose();}
        }
        static string FormulaToLatex(string text)
        {
            var result=new StringBuilder();
            for(int i=0;i<text.Length;i++)
            {
                char marker,script;
                if(TryLatexScript(text[i],out marker,out script))
                {
                    var value=new StringBuilder().Append(script);
                    int nextIndex=i+1;char nextMarker,nextScript;
                    while(nextIndex<text.Length&&TryLatexScript(text[nextIndex],out nextMarker,out nextScript)&&nextMarker==marker){value.Append(nextScript);nextIndex++;}
                    i=nextIndex-1;result.Append(marker).Append('{').Append(value).Append('}');
                    continue;
                }
                string symbol=LatexMathSymbol(text[i]);result.Append(symbol??text[i].ToString());
            }
            string latex=result.ToString().Trim();
            if(latex.Length==0)return latex;
            if((latex.StartsWith("\\(")&&latex.EndsWith("\\)"))||(latex.StartsWith("\\[")&&latex.EndsWith("\\]")))return latex;
            return latex.IndexOfAny(new char[]{'\r','\n'})>=0?"\\["+Environment.NewLine+latex+Environment.NewLine+"\\]":"\\("+latex+"\\)";
        }
        static bool TryLatexScript(char c,out char marker,out char value)
        {
            marker='^';value=c;
            const string superscript="⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ⁿ";
            const string superscriptValues="0123456789+-=()n";
            int index=superscript.IndexOf(c);if(index>=0){value=superscriptValues[index];return true;}
            const string subscript="₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎ₙₐₑᵢₒᵣᵤᵥ";
            const string subscriptValues="0123456789+-=()naeioruv";
            index=subscript.IndexOf(c);if(index>=0){marker='_';value=subscriptValues[index];return true;}
            return false;
        }
        static string LatexMathSymbol(char c)
        {
            switch(c)
            {
                case 'α':return "\\alpha ";case 'β':return "\\beta ";case 'γ':return "\\gamma ";case 'δ':return "\\delta ";case 'ε':case 'ϵ':return "\\epsilon ";case 'ζ':return "\\zeta ";case 'η':return "\\eta ";case 'θ':case 'ϑ':return "\\theta ";case 'ι':return "\\iota ";case 'κ':return "\\kappa ";case 'λ':return "\\lambda ";case 'μ':return "\\mu ";case 'ν':return "\\nu ";case 'ξ':return "\\xi ";case 'π':return "\\pi ";case 'ρ':return "\\rho ";case 'σ':return "\\sigma ";case 'τ':return "\\tau ";case 'υ':return "\\upsilon ";case 'φ':case 'ϕ':return "\\phi ";case 'χ':return "\\chi ";case 'ψ':return "\\psi ";case 'ω':return "\\omega ";
                case 'Γ':return "\\Gamma ";case 'Δ':case '∆':return "\\Delta ";case 'Θ':return "\\Theta ";case 'Λ':return "\\Lambda ";case 'Ξ':return "\\Xi ";case 'Π':return "\\Pi ";case 'Σ':return "\\Sigma ";case 'Φ':return "\\Phi ";case 'Ψ':return "\\Psi ";case 'Ω':return "\\Omega ";
                case '×':return "\\times ";case '÷':return "\\div ";case '·':case '⋅':return "\\cdot ";case '±':return "\\pm ";case '∓':return "\\mp ";case '≤':return "\\leq ";case '≥':return "\\geq ";case '≠':return "\\neq ";case '≈':return "\\approx ";case '∝':return "\\propto ";case '∞':return "\\infty ";case '∑':return "\\sum ";case '∏':return "\\prod ";case '∫':return "\\int ";case '√':return "\\sqrt ";case '∂':return "\\partial ";case '∇':return "\\nabla ";
                case '∈':return "\\in ";case '∉':return "\\notin ";case '∪':return "\\cup ";case '∩':return "\\cap ";case '⊂':return "\\subset ";case '⊆':return "\\subseteq ";case '→':return "\\to ";case '←':return "\\leftarrow ";case '↔':return "\\leftrightarrow ";case '⇒':return "\\Rightarrow ";case '⇔':return "\\Leftrightarrow ";case 'ℝ':return "\\mathbb{R}";case 'ℤ':return "\\mathbb{Z}";case 'ℕ':return "\\mathbb{N}";case 'ℚ':return "\\mathbb{Q}";case 'ℂ':return "\\mathbb{C}";case '−':return "-";case '′':return "'";
                default:return null;
            }
        }
        // Side space reserved beside a page for callout cards. It stays a screen-space
        // constant: scaling it with the zoom used to inflate page canvases into the tens of
        // thousands of pixels and pushed the page out of the viewport as soon as it zoomed.
        int RailPad()
        {
            int width=pdfViewport==null?0:pdfViewport.ClientSize.Width;
            return Math.Min(390,Math.Max(120,(width-400)/2));
        }
        // Keeps a page canvas inside the bounds Windows controls handle reliably, so extreme
        // zoom on a large page cannot leave the reader showing empty space.
        float MaxZoomForPage(int page)
        {
            SizeF reference=ReferenceSizeForPage(page);
            if(reference.Width<=0||reference.Height<=0)return 4f;
            double byWidth=(MaxCanvasSide-2.0*RailPad())/reference.Width,byHeight=(double)MaxCanvasSide/reference.Height;
            return (float)Math.Max(MinimumZoom,Math.Min(4.0,Math.Min(byWidth,byHeight)));
        }
        float FitScale()
        {
            int rail=RailPad();
            float widest=0;foreach(SizeF reference in pageReferenceSize.Values)if(reference.Width>widest)widest=reference.Width;
            if(widest>0)return Math.Max(0.01f,(float)(pdfViewport.ClientSize.Width-2*rail-24)/widest);
            Image img=pdfCanvas==null?null:pdfCanvas.Image;int page=currentPage;
            if(img==null)foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases.OrderBy(delegate(KeyValuePair<int,PdfCanvas> e){return e.Key;}))if(entry.Value.Image!=null){img=entry.Value.Image;page=entry.Key;break;}
            if(img==null)return 1f;float referenceWidth=img.Width*(FullDpi/(float)PageDpi(page));return Math.Max(0.01f,(float)(pdfViewport.ClientSize.Width-2*rail-24)/Math.Max(1,referenceWidth));
        }
        void EnsureVisiblePages()
        {
            if(resizingCanvas||current==null||pdfViewport.ClientSize.Height<1)return;
            int total;if(!pageCounts.TryGetValue(current.FilePath,out total)||total<1)
            {
                foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases)if(entry.Value.Image==null&&entry.Value.Bottom>0&&entry.Value.Top<pdfViewport.ClientSize.Height)ShowPage(entry.Key);
                return;
            }
            float scale=fitMode?FitScale():zoomLevel;int top=virtualScrollY,bottom=top+pdfViewport.ClientSize.Height;
            int first=FindPageAt(top,total,scale),last=FindPageAt(bottom,total,scale);
            int start=Math.Max(1,first-1),end=Math.Min(total,last+2);bool added=false;
            for(int page=first;page<=last;page++)if(!pageCanvases.ContainsKey(page)){EnsurePageCanvas(page);added=true;}
            if(added)ResizeCanvas();
            for(int page=first;page<=last;page++){PdfCanvas canvas;if(pageCanvases.TryGetValue(page,out canvas)&&canvas.Image==null)ShowPage(page);}
            PrefetchNextBatch(last+1);
            UnloadDistantPages(Math.Max(1,start-1),Math.Min(total,end+2));
        }
        void UnloadDistantPages(int keepFirst,int keepLast)
        {
            if(current==null)return;var remove=new List<int>();
            foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases)
            {
                int page=entry.Key;PdfCanvas canvas=entry.Value;if(page>=keepFirst&&page<=keepLast)continue;
                if(canvas==selectedPdfCanvas||canvas==popupPage||markCardPages.Values.Contains(canvas))continue;
                string prefix=current.FilePath+"|"+page+"|"+requestToken+"|";
                foreach(string key in loadingPages)if(key.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))renderToCanvas[key]=false;
                if(pdfCanvas==canvas)pdfCanvas=null;
                if(canvas.Image!=null)canvas.Image=null;
                ConnectorLayer layer;if(pageConnectorLayers.TryGetValue(canvas,out layer)){pageConnectorLayers.Remove(canvas);layer.Dispose();}
                pdfPages.Controls.Remove(canvas);canvas.Dispose();remove.Add(page);
                lock(textPagesLock)textPages.Remove(current.FilePath+"|"+page);
            }
            foreach(int page in remove)pageCanvases.Remove(page);
        }
        void PrefetchNextBatch(int firstPage)
        {
            if(current==null)return;int total;if(!pageCounts.TryGetValue(current.FilePath,out total))return;int started=0,prefetchInFlight=0;
            foreach(string loadingKey in loadingPages){bool visible;if(renderToCanvas.TryGetValue(loadingKey,out visible)&&!visible)prefetchInFlight++;}
            int lastPrefetchPage=Math.Min(total,firstPage+MaxPrefetchPages-1);
            for(int page=firstPage;page<=lastPrefetchPage&&started<MaxPrefetchPages&&prefetchInFlight<MaxPrefetchPages;page++)
            {
                string key=current.FilePath+"|"+page;if(renderedPages.ContainsKey(key))continue;PdfCanvas existing;if(pageCanvases.TryGetValue(page,out existing)&&existing.Image!=null)continue;
                if(StartPageRender(current.FilePath,page,PreviewPageDpi(page),requestToken,total,false)){started++;prefetchInFlight++;}else if(activeLoadCount>=MaxParallelLoadJobs)break;
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
                int pageTop=virtualScrollY+canvas.Top,pageBottom=pageTop+canvas.Height;if(pageBottom>top&&pageTop<bottom)return Int64.MinValue;
                long distance=pageBottom<=top?(long)top-pageBottom:(long)pageTop-bottom;
                return Math.Max(1,distance);
            }
            return 1000L+Math.Abs((long)page-visiblePage)*1200L;
        }
        void TrimPageCache(long byteBudget)
        {
            long bytes=0;
            foreach(Image image in renderedPages.Values)if(image!=null)bytes+=(long)image.Width*image.Height*4L;
            if(bytes<=byteBudget)return;

            int top=virtualScrollY,bottom=top+pdfViewport.ClientSize.Height,visiblePage=currentPage;
            foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases.OrderBy(delegate(KeyValuePair<int,PdfCanvas> e){return e.Key;}))
                if(virtualScrollY+entry.Value.Bottom>top&&virtualScrollY+entry.Value.Top<bottom){visiblePage=entry.Key;break;}
            var keys=new List<string>(renderedPages.Keys);
            keys.Sort(delegate(string a,string b){return CacheEvictionPriority(b,top,bottom,visiblePage).CompareTo(CacheEvictionPriority(a,top,bottom,visiblePage));});
            foreach(string key in keys)
            {
                if(bytes<=byteBudget)break;
                int split=key.LastIndexOf('|'),page;if(split<0||!Int32.TryParse(key.Substring(split+1),out page))continue;
                string file=key.Substring(0,split);PdfCanvas canvas;
                if(current!=null&&String.Equals(file,current.FilePath,StringComparison.OrdinalIgnoreCase)&&pageCanvases.TryGetValue(page,out canvas)&&virtualScrollY+canvas.Bottom>top&&virtualScrollY+canvas.Top<bottom)continue;
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
            if(resizingCanvas||current==null||pageCanvases.Count==0||activeLoadCount>0||DateTime.UtcNow.Subtract(lastViewportScroll).TotalMilliseconds<650)return;
            int top=virtualScrollY,bottom=top+pdfViewport.ClientSize.Height,total;pageCounts.TryGetValue(current.FilePath,out total);
            float scale=fitMode?FitScale():zoomLevel;int displayDpi=DisplayDpi(scale);
            foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases.OrderBy(delegate(KeyValuePair<int,PdfCanvas> e){return e.Key;}))
            {
                PdfCanvas canvas=entry.Value;int pageTop=virtualScrollY+canvas.Top,targetDpi=TargetPageDpi(entry.Key,scale);
                if(canvas.Image==null||PageDpi(entry.Key)>=targetDpi||pageTop+canvas.Height<=top||pageTop>=bottom||fullResolutionFailures.Contains(current.FilePath+"|"+entry.Key))continue;
                StartPageRender(current.FilePath,entry.Key,targetDpi,requestToken,total,true);return;
            }
            // The page bitmap is already at its budget limit. Zooming past it is served by a
            // high-resolution tile of the region that is actually on screen, so detail keeps
            // improving with zoom instead of the page being stretched.
            if(current==null)return;
            foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases.OrderBy(delegate(KeyValuePair<int,PdfCanvas> e){return e.Key;}))
            {
                PdfCanvas canvas=entry.Value;if(canvas.Image==null)continue;
                int pageTop=virtualScrollY+canvas.Top;
                if(pageTop+canvas.Height<=top||pageTop>=bottom)continue;
                if(displayDpi<=PageDpi(entry.Key)*1.05)continue;
                if(fullResolutionFailures.Contains(current.FilePath+"|"+entry.Key))continue;
                RectangleF bounds;int left,regionTop,width,height;
                if(!TryComputeVisibleRegion(canvas,out bounds,out left,out regionTop,out width,out height))continue;
                if(canvas.CoversRegion(bounds,displayDpi))continue;
                string requestKey=displayDpi+"|"+left+"|"+regionTop+"|"+width+"|"+height,previousKey;
                if(regionRequests.TryGetValue(entry.Key,out previousKey)&&previousKey==requestKey)continue;
                regionRequests[entry.Key]=requestKey;
                StartRegionRender(entry.Key,displayDpi,left,regionTop,width,height,bounds);
                return;
            }
        }
        // Region pixels are measured in the same space as the page as drawn on screen, so a
        // tile rendered at DisplayDpi lands exactly one bitmap pixel per screen pixel.
        bool TryComputeVisibleRegion(PdfCanvas canvas,out RectangleF bounds,out int left,out int top,out int width,out int height)
        {
            bounds=RectangleF.Empty;left=top=width=height=0;
            RectangleF image=canvas.ImageRect;
            if(image.Width<1||image.Height<1)return false;
            int viewLeft=Math.Max(0,-canvas.Left),viewTop=Math.Max(0,-canvas.Top);
            int viewRight=Math.Min(canvas.Width,-canvas.Left+pdfViewport.ClientSize.Width);
            int viewBottom=Math.Min(canvas.Height,-canvas.Top+pdfViewport.ClientSize.Height);
            if(viewRight<=viewLeft||viewBottom<=viewTop)return false;
            float padX=RegionMarginPixels/image.Width,padY=RegionMarginPixels/image.Height;
            float nx=(viewLeft-image.X)/image.Width-padX,ny=(viewTop-image.Y)/image.Height-padY;
            float nw=(viewRight-viewLeft)/image.Width+2*padX,nh=(viewBottom-viewTop)/image.Height+2*padY;
            nx=Math.Max(0,Math.Min(1-padX,nx));ny=Math.Max(0,Math.Min(1-padY,ny));
            nw=Math.Min(1-nx,nw);nh=Math.Min(1-ny,nh);
            if(nw<=0||nh<=0)return false;
            left=(int)Math.Floor(nx*image.Width);top=(int)Math.Floor(ny*image.Height);
            width=Math.Max(1,(int)Math.Ceiling(nw*image.Width));height=Math.Max(1,(int)Math.Ceiling(nh*image.Height));
            if(width>MaxRegionPixelSize){width=MaxRegionPixelSize;left=Math.Max(0,Math.Min((int)image.Width-width,left));}
            if(height>MaxRegionPixelSize){height=MaxRegionPixelSize;top=Math.Max(0,Math.Min((int)image.Height-height,top));}
            bounds=new RectangleF(left/image.Width,top/image.Height,width/image.Width,height/image.Height);
            return true;
        }
        void StartRegionRender(int page,int dpi,int left,int top,int width,int height,RectangleF bounds)
        {
            if(current==null)return;
            string docKey=current.FilePath,loadKey=docKey+"|"+page+"|"+dpi+"|"+left+"|"+top+"|"+width+"|"+height;
            if(!regionLoads.Add(loadKey))return;
            int token=requestToken;activeLoadCount++;ApplyCpuAffinity();statusLabel.Text="正在按当前缩放渲染第 "+page+" 页…";
            var worker=new BackgroundWorker();
            worker.DoWork+=delegate(object s,DoWorkEventArgs e)
            {
                var req=(RegionRequest)e.Argument;
                PdfiumEngine.RenderedPage rendered=PdfiumEngine.RenderPageRegion(req.Path,req.Page,req.Dpi,req.Left,req.Top,req.Width,req.Height);
                e.Result=new RegionResult{Path=req.Path,Page=req.Page,Token=req.Token,Dpi=req.Dpi,Bounds=req.Bounds,Bitmap=rendered.Bitmap};
            };
            worker.RunWorkerCompleted+=delegate(object s,RunWorkerCompletedEventArgs e)
            {
                regionLoads.Remove(loadKey);activeLoadCount=Math.Max(0,activeLoadCount-1);ApplyCpuAffinity();
                if(IsDisposed)return;
                if(e.Error!=null){regionRequests.Remove(page);statusLabel.Text="按缩放渲染失败："+e.Error.GetBaseException().Message;return;}
                var res=(RegionResult)e.Result;
                if(res.Token!=requestToken||current==null||!String.Equals(current.FilePath,res.Path,StringComparison.OrdinalIgnoreCase))
                {
                    res.Bitmap.Dispose();TrimPageCache(IdleBitmapBudget);return;
                }
                PdfCanvas canvas;
                if(!pageCanvases.TryGetValue(res.Page,out canvas)){res.Bitmap.Dispose();return;}
                if(canvas.Image==null){res.Bitmap.Dispose();return;}
                canvas.SetRegion(res.Bitmap,res.Bounds,res.Dpi);
                statusLabel.Text="已按 "+res.Dpi+" dpi 渲染可见区域";
                TrimPageCache(IdleBitmapBudget);
            };
            worker.RunWorkerAsync(new RegionRequest{Path=docKey,Page=page,Token=token,Dpi=dpi,Left=left,Top=top,Width=width,Height=height,Bounds=bounds});
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if(keyData==(Keys.Control|Keys.O)){ImportPdf(this,EventArgs.Empty);return true;}
            if(keyData==(Keys.Control|Keys.B)){ToggleLibraryDrawer();return true;}
            if(keyData==(Keys.Control|Keys.Shift|Keys.C)&&!String.IsNullOrWhiteSpace(selectedPdfText))
            {
                if(!HasEditorSelection()){CopyPdfSelection(true);return true;}
            }
            if(keyData==(Keys.Control|Keys.C)&&!HasEditorSelection())
            {
                if(!String.IsNullOrWhiteSpace(selectedPdfText)){CopyPdfSelection(false);return true;}
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
        void RefreshPapers() { papers.Items.Clear(); paperPicker.Items.Clear(); foreach (var p in library.Papers) { papers.Items.Add(p); paperPicker.Items.Add(p); } if (library.Papers.Count > 0 && papers.SelectedIndex < 0) { int firstAvailable=library.Papers.FindIndex(delegate(Paper p){return p!=null&&!String.IsNullOrWhiteSpace(p.FilePath)&&File.Exists(p.FilePath);}); papers.SelectedIndex=firstAvailable>=0?firstAvailable:0; } if (papers.SelectedItem != null) paperPicker.SelectedItem = papers.SelectedItem; }
        void SelectPaper(object sender, EventArgs e)
        {
            if(drawerOpen)SetLibraryDrawer(false);
            requestToken++;if(selectedPdfCanvas!=null)selectedPdfCanvas.ClearDraft();ClearOpenCallouts();lock(textPagesLock)textPages.Clear();current = papers.SelectedItem as Paper;pdfCanvas=null;currentPage=1;virtualScrollX=virtualScrollY=0;documentWidth=pdfViewport.ClientSize.Width;documentHeight=pdfViewport.ClientSize.Height;pendingPageJump=0;pageGeometryReady=false;pageBox.Text="1";pageDpi.Clear();regionRequests.Clear();regionScale=-1f;pageReferenceSize.Clear();defaultPageReference=SizeF.Empty;pageLayoutVersion++;pageLayoutIndexVersion=-1;pageCanvases.Clear();pdfPages.Controls.Clear();pdfPages.Size=pdfViewport.ClientSize;marks.Items.Clear();UpdateScrollBars();if (current == null){UpdatePageIndicator();return;}UpdatePageIndicator();
            if (current.Marks == null) current.Marks = new List<Mark>();
            foreach (var m in current.Marks) marks.Items.Add(m);ShowPage(1);StartPageGeometryIndex(current.FilePath,requestToken);
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
        long CpuAffinityMask(){int cores=Math.Min(activeLoadCount>0?8:1,Math.Max(1,Environment.ProcessorCount));return (1L<<cores)-1;}
        void ApplyCpuAffinity(){try{Process.GetCurrentProcess().ProcessorAffinity=new IntPtr(CpuAffinityMask());}catch{}}
        void ShowPage(int page)
        {
            if (current == null || !File.Exists(current.FilePath)) { if(pdfCanvas!=null)pdfCanvas.Image=null; pageIndicator.Text = "0/0"; return; }
            string docKey=current.FilePath,cacheKey=docKey+"|"+page;Image cached;
            int total;if(renderedPages.TryGetValue(cacheKey,out cached)){pageCounts.TryGetValue(docKey,out total);int dpi;if(!renderedDpi.TryGetValue(cacheKey,out dpi))dpi=FullDpi;AddPage(page,cached,total,dpi);if((page-1)%MaxParallelLoadJobs==0)PrefetchNextBatch(page+1);return;}
            int token=requestToken,knownTotal;pageCounts.TryGetValue(docKey,out knownTotal);StartPageRender(docKey,page,PreviewPageDpi(page),token,knownTotal,true);
        }
        bool StartPageRender(string docKey,int page,int dpi,int token,int knownTotal,bool showInCanvas)
        {
            string cacheKey=docKey+"|"+page,loadingKey=cacheKey+"|"+token+"|"+dpi;
            if(loadingPages.Contains(loadingKey)){if(showInCanvas)renderToCanvas[loadingKey]=true;return true;}
            if(activeLoadCount>=MaxParallelLoadJobs)return false;
            loadingPages.Add(loadingKey);renderToCanvas[loadingKey]=showInCanvas;activeLoadCount++;ApplyCpuAffinity();statusLabel.Text="正在后台加载第 "+page+" 页…";
            var worker=new BackgroundWorker();
            worker.DoWork+=delegate(object s,DoWorkEventArgs e)
            {
                var req=(RenderRequest)e.Argument;
                PdfiumEngine.RenderedPage page=PdfiumEngine.RenderPage(req.Path,req.Page,req.Dpi);
                e.Result=new RenderResult{Path=req.Path,Page=req.Page,Count=page.PageCount,Token=req.Token,Dpi=req.Dpi,Bitmap=page.Bitmap};
            };
            worker.RunWorkerCompleted+=delegate(object s,RunWorkerCompletedEventArgs e)
            {
                loadingPages.Remove(loadingKey);bool shouldShow;renderToCanvas.TryGetValue(loadingKey,out shouldShow);renderToCanvas.Remove(loadingKey);activeLoadCount=Math.Max(0,activeLoadCount-1);ApplyCpuAffinity();if(IsDisposed){if(activeLoadCount==0)TrimPageCache(IdleBitmapBudget);return;}if(e.Error!=null)
                {
                    Exception failure=e.Error.GetBaseException();if(dpi>=FullDpi)fullResolutionFailures.Add(docKey+"|"+page);
                    TrimPageCache(IdleBitmapBudget);
                    if(failure is OutOfMemoryException){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
                    int retryDpi=Math.Max(72,dpi/2);
                    if(token==requestToken&&current!=null&&String.Equals(current.FilePath,docKey,StringComparison.OrdinalIgnoreCase)&&retryDpi<dpi&&StartPageRender(docKey,page,retryDpi,token,knownTotal,shouldShow))
                    {statusLabel.Text="第 "+page+" 页渲染遇到资源限制，正在降低分辨率重试…";return;}
                    if(token==requestToken)statusLabel.Text="第 "+page+" 页加载失败："+failure.Message;
                    if(activeLoadCount==0)TrimPageCache(IdleBitmapBudget);return;
                }
                var res=(RenderResult)e.Result;string key=res.Path+"|"+res.Page;int oldDpi;bool replace=!renderedDpi.TryGetValue(key,out oldDpi)||res.Dpi>oldDpi;Image prior=null;
                if(replace){renderedPages.TryGetValue(key,out prior);renderedPages[key]=res.Bitmap;renderedDpi[key]=res.Dpi;cacheOrder.Enqueue(key);}else res.Bitmap.Dispose();pageCounts[res.Path]=res.Count;
                if(res.Token!=requestToken||current==null||current.FilePath!=res.Path){if(activeLoadCount==0)TrimPageCache(IdleBitmapBudget);return;}
                Image best=renderedPages[key];int bestDpi=renderedDpi[key];PdfCanvas canvas;
                if(shouldShow&&pageCanvases.TryGetValue(res.Page,out canvas))
                {
                    pageDpi[res.Page]=bestDpi;SetPageReferenceSize(res.Page,new SizeF(best.Width*(FullDpi/(float)bestDpi),best.Height*(FullDpi/(float)bestDpi)));
                    if(canvas.Image!=best){Image old=canvas.Image;canvas.Image=best;ResizeCanvas();canvas.Invalidate();if(old!=null&&old!=best&&old!=prior)old.Dispose();}
                }
                else if(shouldShow)AddPage(res.Page,best,res.Count,bestDpi);
                if(shouldShow&&pageCanvases.TryGetValue(res.Page,out canvas))LoadPageTextLayout(res.Path,res.Page,canvas);
                if(prior!=null&&prior!=best&&pageCanvases.TryGetValue(res.Page,out canvas)&&canvas.Image==best)prior.Dispose();
                if(shouldShow&&(res.Page-1)%MaxParallelLoadJobs==0)PrefetchNextBatch(res.Page+1);
                UpdatePageIndicator();TrimPageCache(activeLoadCount>0?LoadingBitmapBudget:IdleBitmapBudget);
            };
            worker.RunWorkerAsync(new RenderRequest{Path=docKey,Page=page,Token=token,Count=knownTotal,Dpi=dpi});return true;
        }
        class RenderRequest { public string Path; public int Page,Token,Count,Dpi; }
        class RenderResult { public string Path; public int Page,Count,Token,Dpi; public Bitmap Bitmap; }
        class RegionRequest { public string Path; public int Page,Token,Dpi,Left,Top,Width,Height; public RectangleF Bounds; }
        class RegionResult { public string Path; public int Page,Token,Dpi; public RectangleF Bounds; public Bitmap Bitmap; }
        void AttachPageContextMenu(PdfCanvas canvas,int page)
        {
            var menu=new ContextMenuStrip();
            var copyText=new ToolStripMenuItem("复制选中文字");
            var copyFormula=new ToolStripMenuItem("复制公式（LaTeX）"){ShortcutKeyDisplayString="Ctrl+Shift+C"};
            var extractImage=new ToolStripMenuItem("提取选区中的 PDF 图片对象");
            var armImageCopy=new ToolStripMenuItem("框选 PDF 图片区域…");
            copyText.Click+=delegate{CopyPdfSelection(false,canvas.SelectedText);};
            copyFormula.Click+=delegate{CopyPdfSelection(true,canvas.SelectedText);};
            extractImage.Click+=delegate{ExtractPdfImageObject(canvas,page);};
            armImageCopy.Click+=delegate{canvas.BeginImageRegionCopy();statusLabel.Text="请按住 Ctrl+左键拖动框选 PDF 图片区域";};
            canvas.ImageCopyStatus+=delegate(string message){statusLabel.Text=message;};
            canvas.RegionImageExtractRequested+=delegate{currentPage=page;pdfCanvas=canvas;pageBox.Text=page.ToString();CopyDraggedImageRegion(canvas,page);};
            menu.Opening+=delegate{bool hasSelection=!String.IsNullOrWhiteSpace(canvas.SelectedText);copyText.Enabled=hasSelection;copyFormula.Enabled=hasSelection;extractImage.Enabled=canvas.HasImageRegion;};
            menu.Items.Add(copyText);menu.Items.Add(copyFormula);menu.Items.Add(new ToolStripSeparator());menu.Items.Add(extractImage);menu.Items.Add(armImageCopy);canvas.ContextMenuStrip=menu;
        }
        bool ExtractPdfImageObject(PdfCanvas canvas,int page)
        {
            if(current==null||!canvas.HasImageRegion){statusLabel.Text="请先框选 PDF 图片区域";return false;}
            try
            {
                RectangleF region=canvas.SelectedImageRegion;
                using(Bitmap image=PdfiumEngine.ExtractImageObject(current.FilePath,page,region,canvas.Image.Width,canvas.Image.Height))
                {
                    if(image==null){statusLabel.Text="该区域没有可直接提取的 PDF 图片对象；公式可能由文字或矢量图形组成";return false;}
                    Clipboard.SetImage(image);statusLabel.Text="已提取 PDF 原始图片对象并复制（"+image.Width+"×"+image.Height+"）";
                    return true;
                }
            }
            catch(Exception ex){statusLabel.Text="提取 PDF 图片失败："+ex.Message;return false;}
        }
        void CopyDraggedImageRegion(PdfCanvas canvas,int page)
        {
            try{if(canvas.CopySelectedRegionAsImage())statusLabel.Text="框选区域截图已复制（不含蓝色选区底色）";else statusLabel.Text="框选区域无效，请重新拖动";}
            catch(Exception ex){statusLabel.Text="截图复制失败："+ex.Message;}
        }
        PdfCanvas CreatePageCanvas(int page)
        {
            PdfCanvas canvas=new PdfCanvas{BackColor=Color.White,SizeMode=PictureBoxSizeMode.Zoom,Margin=new Padding(0,0,0,20)};canvas.MouseWheel+=ReaderWheel;AttachPageContextMenu(canvas,page);canvas.SetMarksProvider(delegate{return current==null?new List<Mark>():current.Marks.FindAll(delegate(Mark m){return m.Page==page;});});canvas.MarkClicked+=delegate(Mark m){currentPage=page;pdfCanvas=canvas;try{ShowMarkPopup(m,canvas);}catch(Exception ex){HandleMarkPopupFailure(m,canvas,ex);}};
            canvas.DocumentTextDragStarted+=delegate(int word,Point screen){StartCrossPageDrag(page,word);};
            canvas.DocumentTextDragMoved+=delegate(Point screen){return UpdateCrossPageDrag(screen);};
            canvas.DocumentTextDragEnded+=delegate(Point screen,bool crossed){return EndCrossPageDrag(screen,crossed);};
            canvas.RegionSelected+=delegate(float x,float y,float w,float h){ClearCrossPageSelection();if(selectedPdfCanvas!=null&&selectedPdfCanvas!=canvas)selectedPdfCanvas.ClearDraft();currentPage=page;pdfCanvas=canvas;pageBox.Text=page.ToString();kindBox.SelectedItem="高亮";selectedPdfCanvas=canvas;selectedPdfText=canvas.SelectedText??"";quoteBox.Clear();noteBox.Clear();if(popupPage==canvas){popup.Visible=false;HideEditorConnector();}};
            canvas.SelectionActivated+=delegate(float x,float y,float w,float h){currentPage=page;pdfCanvas=canvas;pageBox.Text=page.ToString();kindBox.SelectedItem="高亮";ShowAnnotationPopup(canvas,x,y,w,h);};
            canvas.SelectionCleared+=delegate{ClearCrossPageSelection();if(selectedPdfCanvas!=null){PdfCanvas old=selectedPdfCanvas;selectedPdfText="";selectedPdfCanvas=null;quoteBox.Clear();if(popup.Visible){popup.Visible=false;HideEditorConnector();noteBox.Clear();}if(old!=canvas)old.ClearDraft();}};
            canvas.Resize+=delegate{RepositionPageCallouts(canvas);};return canvas;
        }
        void AddPage(int page,Image image,int total,int dpi)
        {
            pageDpi[page]=dpi;SetPageReferenceSize(page,new SizeF(image.Width*(FullDpi/(float)dpi),image.Height*(FullDpi/(float)dpi))); PdfCanvas canvas;
            if(!pageCanvases.TryGetValue(page,out canvas)){canvas=CreatePageCanvas(page);pageCanvases[page]=canvas;pdfPages.Controls.Add(canvas);}
            canvas.Image=image;if(pdfCanvas==null){pdfCanvas=canvas;currentPage=page;pageBox.Text=page.ToString();}ResizeCanvas();LoadPageTextLayout(current.FilePath,page,canvas);canvas.Invalidate();UpdatePageIndicator();statusLabel.Text="连续滚动阅读 · Ctrl+滚轮缩放";
        }
        void ResizeCanvas()
        {
            if (pdfViewport.ClientSize.Width < 1) return;
            resizingCanvas=true;
            try
            {
                if(current==null)
                {
                    documentWidth=pdfViewport.ClientSize.Width;documentHeight=pdfViewport.ClientSize.Height;virtualScrollX=virtualScrollY=0;
                    pdfVerticalScrollBar.Visible=pdfHorizontalScrollBar.Visible=false;if(pdfPages.Size!=pdfViewport.ClientSize)pdfPages.Size=pdfViewport.ClientSize;UpdateScrollBars();PositionPageIndicator(readerSurface);return;
                }
                int total;if(current==null||!pageCounts.TryGetValue(current.FilePath,out total)||total<1)total=pageCanvases.Count==0?1:pageCanvases.Keys.Max();
                float pageScale=fitMode?FitScale():zoomLevel;
                // Tiles are rendered for one scale only; drop them when the scale changes.
                if(Math.Abs(pageScale-regionScale)>0.0005f){regionScale=pageScale;foreach(PdfCanvas stale in pageCanvases.Values)stale.ClearRegion();}
                int rail=RailPad();
                foreach(KeyValuePair<int,PdfCanvas> entry in pageCanvases)
                {
                    PdfCanvas page=entry.Value;
                    SizeF reference=ReferenceSizeForPage(entry.Key);if(reference.Width<=0){if(page.Image==null)continue;float factor=FullDpi/(float)PageDpi(entry.Key);reference=new SizeF(page.Image.Width*factor,page.Image.Height*factor);SetPageReferenceSize(entry.Key,reference);}
                    int w=Math.Max(1,(int)(reference.Width*pageScale))+2*rail,h=Math.Max(1,(int)(reference.Height*pageScale));Size size=new Size(w,h);
                    if(page.Size!=size){page.Size=size;page.Invalidate();}
                }
                for(int pass=0;pass<3;pass++)
                {
                    int viewportWidth=pdfViewport.ClientSize.Width,viewportHeight=pdfViewport.ClientSize.Height;float scale=fitMode?FitScale():zoomLevel;int widest=0;
                    foreach(SizeF reference in pageReferenceSize.Values)if(reference.Width>0)widest=Math.Max(widest,(int)(reference.Width*scale));
                    if(widest==0)widest=Math.Max(0,(int)(ReferenceSizeForPage(1).Width*scale));
                    widest+=2*RailPad();
                    int nextWidth=Math.Max(viewportWidth-4,widest+24);long contentBottom=(long)PageTop(total,scale)+(long)(ReferenceSizeForPage(total).Height*scale)+12L;
                    int nextHeight=(int)Math.Max(viewportHeight,Math.Min(Int32.MaxValue/2,contentBottom));bool changed=false;
                    bool showVertical=nextHeight>viewportHeight,showHorizontal=nextWidth>viewportWidth;
                    if(pdfVerticalScrollBar.Visible!=showVertical){pdfVerticalScrollBar.Visible=showVertical;changed=true;}
                    if(pdfHorizontalScrollBar.Visible!=showHorizontal){pdfHorizontalScrollBar.Visible=showHorizontal;changed=true;}
                    documentWidth=nextWidth;documentHeight=nextHeight;if(!changed)break;
                }
                virtualScrollX=Math.Max(0,Math.Min(GetMaxScrollX(),virtualScrollX));virtualScrollY=Math.Max(0,Math.Min(GetMaxScrollY(),virtualScrollY));
                if(pdfPages.Size!=pdfViewport.ClientSize)pdfPages.Size=pdfViewport.ClientSize;
                LayoutPagesAtCurrentScroll();foreach(PdfCanvas page in pageCanvases.Values)RepositionPageCallouts(page);UpdateScrollBars();PositionPageIndicator(readerSurface);
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
            current.Marks.Add(m); marks.Items.Add(m); quoteBox.Clear();noteBox.Clear(); pdfCanvas.ClearDraft(); pdfCanvas.Invalidate(); popup.Visible = false;HideEditorConnector(); SaveLibrary();
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
        bool dragging, hasDraft,textDragPotential,crossPageDragDetected;
        int textDragAnchorIndex=-1;
        bool regionCopyArmed, regionCopyDrag, hasImageRegion, previewImageRegion;
        RectangleF draft;
        RectangleF imageRegion;
        Bitmap regionImage;
        RectangleF regionBounds;
        int regionDpi;
        Dictionary<object,Point[]> connectors=new Dictionary<object,Point[]>();
        List<RectangleF> draftWords;
        List<RectangleF> previewWords;
        List<MainForm.WordBox> pageWords=new List<MainForm.WordBox>();
        float medianWordHeightNormalized;
        List<MainForm.WordBox> readingOrderWords=new List<MainForm.WordBox>();
        Dictionary<MainForm.WordBox,int> readingOrderIndices=new Dictionary<MainForm.WordBox,int>();
        List<SelectionLine> selectionLines=new List<SelectionLine>();
        sealed class SelectionLine { public List<MainForm.WordBox> Words; public RectangleF Bounds; }
        Timer caretBlinkTimer;
        bool hasCaret,caretVisible;
        int caretWordIndex,caretOffset;
        float caretNormX,caretNormY;
        public string SelectedText { get; private set; }
        public bool HasImageRegion { get { return hasImageRegion; } }
        public RectangleF SelectedImageRegion { get { return imageRegion; } }
        public PdfCanvas() { DoubleBuffered = false; SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint,true); SizeMode = PictureBoxSizeMode.Zoom; Cursor = Cursors.IBeam;TabStop=true;SetStyle(ControlStyles.Selectable,true);caretBlinkTimer=new Timer{Interval=530};caretBlinkTimer.Tick+=delegate{if(!hasCaret){caretBlinkTimer.Stop();return;}caretVisible=!caretVisible;Invalidate();}; }
        void RestartCaretBlink(){caretVisible=true;if(caretBlinkTimer!=null){caretBlinkTimer.Stop();caretBlinkTimer.Start();}Invalidate();}
        protected override void Dispose(bool disposing){if(disposing){ClearRegion();ReleasePaintBuffer();if(caretBlinkTimer!=null){caretBlinkTimer.Stop();caretBlinkTimer.Dispose();caretBlinkTimer=null;}}base.Dispose(disposing);}
        public void SetMarksProvider(Func<List<Mark>> provider) { getMarks = provider; }
        public void SetTextLayout(List<MainForm.WordBox> words)
        {
            List<MainForm.WordBox> next=words??new List<MainForm.WordBox>();if(Object.ReferenceEquals(pageWords,next))return;pageWords=next;
            var heights=new List<float>();foreach(MainForm.WordBox word in next)if(word!=null){float h=word.Y2-word.Y1;if(h>0.0005f&&h<0.05f)heights.Add(h);}heights.Sort();medianWordHeightNormalized=heights.Count==0?0:heights[heights.Count/2];
            readingOrderWords=MainForm.BuildReadingOrderWords(next);readingOrderIndices=new Dictionary<MainForm.WordBox,int>();
            for(int i=0;i<readingOrderWords.Count;i++)if(!readingOrderIndices.ContainsKey(readingOrderWords[i]))readingOrderIndices.Add(readingOrderWords[i],i);
            selectionLines=BuildSelectionLines(next);Point p=PointToClient(Cursor.Position);if(ClientRectangle.Contains(p))Cursor=HitDraft(p)||HitMark(p)?Cursors.Hand:Cursors.IBeam;Invalidate();
        }
        public float EstimatedPdfGlyphHeightPixels(){RectangleF bounds=ImageBounds();return medianWordHeightNormalized>0&&bounds.Height>0?medianWordHeightNormalized*bounds.Height:0;}
        // Leader lines drawn by the page itself, from a mark to the callout that belongs to it.
        // Drawing them here keeps them under the callout windows and out of a separate control
        // that would have to span the whole page.
        public void SetConnector(object key,Point start,Point end)
        {
            Point[] previous;
            Rectangle stale=connectors.TryGetValue(key,out previous)?ConnectorBounds(previous):Rectangle.Empty;
            connectors[key]=new Point[]{start,end};
            Rectangle area=Rectangle.Union(stale,ConnectorBounds(new Point[]{start,end}));
            if(area.IsEmpty)Invalidate();else Invalidate(Rectangle.Inflate(area,10,10));
        }
        public void ClearConnector(object key)
        {
            Point[] previous;
            if(!connectors.TryGetValue(key,out previous))return;
            connectors.Remove(key);
            Invalidate(Rectangle.Inflate(ConnectorBounds(previous),10,10));
        }
        public void ClearConnectors()
        {
            if(connectors.Count==0)return;
            connectors.Clear();Invalidate();
        }
        static Rectangle ConnectorBounds(Point[] segment)
        {
            if(segment==null||segment.Length<2)return Rectangle.Empty;
            return Rectangle.FromLTRB(Math.Min(segment[0].X,segment[1].X),Math.Min(segment[0].Y,segment[1].Y),Math.Max(segment[0].X,segment[1].X),Math.Max(segment[0].Y,segment[1].Y));
        }
        public void SetDraft(float x, float y, float w, float h) { draft = new RectangleF(x,y,w,h);draftWords=null;hasDraft = w > 0 && h > 0; Invalidate(); }
        public void GetDraft(out float x, out float y, out float w, out float h) { x = draft.X; y = draft.Y; w = draft.Width; h = draft.Height; }
        public Point AnchorPoint(float x,float y,float w,float h,Control target) { RectangleF b=ImageBounds();Point screen=PointToScreen(new Point((int)(b.X+(x+w/2f)*b.Width),(int)(b.Y+(y+h/2f)*b.Height)));return target.PointToClient(screen); }
        public void ClearDraft() { hasDraft = false; draft = RectangleF.Empty;draftWords=null;previewWords=null;SelectedText="";hasCaret=false;caretVisible=false;if(caretBlinkTimer!=null)caretBlinkTimer.Stop();Invalidate();if(SelectionCleared!=null)SelectionCleared(); }
        public Bitmap CreateSelectionComposite()
        {
            if(!hasDraft||draftWords==null||Image==null)return null;
            float left=draft.Left,top=draft.Top,right=draft.Right,bottom=draft.Bottom;
            left=Math.Max(0,Math.Min(1,left));top=Math.Max(0,Math.Min(1,top));right=Math.Max(0,Math.Min(1,right));bottom=Math.Max(0,Math.Min(1,bottom));
            int sourceLeft=Math.Max(0,(int)Math.Floor(left*Image.Width)),sourceTop=Math.Max(0,(int)Math.Floor(top*Image.Height));
            int sourceRight=Math.Min(Image.Width,(int)Math.Ceiling(right*Image.Width)),sourceBottom=Math.Min(Image.Height,(int)Math.Ceiling(bottom*Image.Height));
            if(sourceRight<=sourceLeft||sourceBottom<=sourceTop)return null;
            var output=new Bitmap(sourceRight-sourceLeft,sourceBottom-sourceTop,PixelFormat.Format32bppArgb);
            using(Graphics graphics=Graphics.FromImage(output))
            {
                graphics.DrawImage(Image,new Rectangle(0,0,output.Width,output.Height),new Rectangle(sourceLeft,sourceTop,output.Width,output.Height),GraphicsUnit.Pixel);
            }
            return output;
        }
        bool HitDraft(Point p){if(!hasDraft||draftWords==null)return false;RectangleF b=ImageBounds();foreach(RectangleF word in draftWords)if(RectangleF.Inflate(new RectangleF(b.X+word.X*b.Width,b.Y+word.Y*b.Height,word.Width*b.Width,word.Height*b.Height),3,3).Contains(p))return true;return false;}
        public Rectangle AnchorBounds(float x,float y,float w,float h) { RectangleF b=ImageBounds();return Rectangle.Round(new RectangleF(b.X+x*b.Width,b.Y+y*b.Height,w*b.Width,h*b.Height)); }
        public event Action<string> ImageCopyStatus;
        public event Action RegionImageExtractRequested;
        public event Action<int,Point> DocumentTextDragStarted;
        public event Func<Point,bool> DocumentTextDragMoved;
        public event Func<Point,bool,bool> DocumentTextDragEnded;
        public void BeginImageRegionCopy() { regionCopyArmed=true;hasImageRegion=false;ClearDraft();Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Image == null || e.Button != MouseButtons.Left) return;bool activateDraft=hasDraft&&HitDraft(e.Location),hitMark=HitMark(e.Location);regionCopyDrag=regionCopyArmed||(ModifierKeys&Keys.Control)==Keys.Control;regionCopyArmed=false;hasImageRegion=false;previewImageRegion=false;textDragPotential=false;crossPageDragDetected=false;textDragAnchorIndex=-1;if(regionCopyDrag){if(hasDraft)ClearDraft();SelectedText="";}else if(hasDraft&&!activateDraft)ClearDraft();Focus();dragging = true; Cursor = Cursors.IBeam; start = e.Location; end = e.Location;previewWords=null;if(!regionCopyDrag&&!activateDraft&&!hitMark&&ImageBounds().Contains(e.Location)){MainForm.WordBox word=FindNearestWord(e.Location,14);if(word!=null){PlaceCaret(word,e.Location);textDragAnchorIndex=pageWords.IndexOf(word);textDragPotential=textDragAnchorIndex>=0;if(textDragPotential&&DocumentTextDragStarted!=null)DocumentTextDragStarted(textDragAnchorIndex,PointToScreen(e.Location));}else PlaceCaretAtPoint(e.Location);} }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) { Cursor = Cursors.IBeam; end = e.Location;bool globalCrossPage=false;if(textDragPotential&&(Math.Abs(e.X-start.X)>=9||Math.Abs(e.Y-start.Y)>=9)&&DocumentTextDragMoved!=null)globalCrossPage=DocumentTextDragMoved(Cursor.Position);if(globalCrossPage)crossPageDragDetected=true;if(crossPageDragDetected){previewWords=null;previewImageRegion=false;}else if(regionCopyDrag){previewWords=null;previewImageRegion=true;}else if(Math.Abs(e.X-start.X)>=9||Math.Abs(e.Y-start.Y)>=9){List<MainForm.WordBox> words=WordsBetween(start,e.Location);if(words.Count>0){previewWords=HighlightRects(words);previewImageRegion=false;}else{previewWords=null;previewImageRegion=true;}}else{previewWords=null;previewImageRegion=false;}Invalidate(); } else Cursor = (HitDraft(e.Location)||HitMark(e.Location))?Cursors.Hand:Cursors.IBeam; }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); if (!dragging || Image == null) return; dragging = false; end = e.Location;previewWords=null;previewImageRegion=false;Cursor=HitDraft(e.Location)||HitMark(e.Location)?Cursors.Hand:Cursors.IBeam;
            bool textDrag=textDragPotential,crossDrag=crossPageDragDetected;textDragPotential=false;crossPageDragDetected=false;if(textDrag&&DocumentTextDragEnded!=null&&DocumentTextDragEnded(Cursor.Position,crossDrag))return;
            if(regionCopyDrag){regionCopyDrag=false;if(Math.Abs(end.X-start.X)>=6&&Math.Abs(end.Y-start.Y)>=6&&SetImageRegion(Normalize(start,end))){Invalidate();if(RegionImageExtractRequested!=null)RegionImageExtractRequested();}else if(ImageCopyStatus!=null)ImageCopyStatus("框选区域太小，请重新拖动框选");return;}
            if(Math.Abs(end.X-start.X)<9&&Math.Abs(end.Y-start.Y)<9)
            {
                if(HitDraft(e.Location)){if(SelectionActivated!=null)SelectionActivated(draft.X,draft.Y,draft.Width,draft.Height);Invalidate();return;}
                Mark clicked=FindMarkAt(e.Location);if(clicked!=null){ClearDraft();if(MarkClicked!=null)MarkClicked(clicked);return;}
                MainForm.WordBox word=FindNearestWord(e.Location,14);if(word!=null)PlaceCaret(word,e.Location);else if(ImageBounds().Contains(e.Location))PlaceCaretAtPoint(e.Location);else ClearDraft();return;
            }
            List<MainForm.WordBox> words=WordsBetween(start,end);
            if(words.Count>0)SetSelection(words);
            else if(Math.Abs(end.X-start.X)>=6&&Math.Abs(end.Y-start.Y)>=6&&SetImageRegion(Normalize(start,end))){Invalidate();if(RegionImageExtractRequested!=null)RegionImageExtractRequested();}
            else if(ImageBounds().Contains(e.Location))PlaceCaretAtPoint(e.Location);else ClearDraft();
        }
        static RectangleF Normalize(Point a, Point b) { return new RectangleF(Math.Min(a.X,b.X), Math.Min(a.Y,b.Y), Math.Abs(a.X-b.X), Math.Abs(a.Y-b.Y)); }
        bool SetImageRegion(RectangleF selection)
        {
            RectangleF bounds=ImageBounds();if(bounds.Width<=0||bounds.Height<=0)return false;
            RectangleF clipped=RectangleF.Intersect(selection,bounds);if(clipped.Width<4||clipped.Height<4)return false;
            imageRegion=new RectangleF((clipped.X-bounds.X)/bounds.Width,(clipped.Y-bounds.Y)/bounds.Height,clipped.Width/bounds.Width,clipped.Height/bounds.Height);
            hasImageRegion=true;return true;
        }
        public bool CopySelectedRegionAsImage()
        {
            if(!hasImageRegion||Image==null)return false;
            int left=Math.Max(0,(int)Math.Floor(imageRegion.Left*Image.Width)),top=Math.Max(0,(int)Math.Floor(imageRegion.Top*Image.Height));
            int right=Math.Min(Image.Width,(int)Math.Ceiling(imageRegion.Right*Image.Width)),bottom=Math.Min(Image.Height,(int)Math.Ceiling(imageRegion.Bottom*Image.Height));
            if(right<=left||bottom<=top)return false;
            using(var bitmap=new Bitmap(right-left,bottom-top,PixelFormat.Format32bppArgb))
            using(var graphics=Graphics.FromImage(bitmap))
            {
                graphics.DrawImage(Image,new Rectangle(0,0,bitmap.Width,bitmap.Height),new Rectangle(left,top,bitmap.Width,bitmap.Height),GraphicsUnit.Pixel);
                Clipboard.SetImage(bitmap);
            }
            return true;
        }
        RectangleF WordRect(MainForm.WordBox word){return new RectangleF(word.X1,word.Y1,word.X2-word.X1,word.Y2-word.Y1);}
        static float Median(List<float> values)
        {
            if(values==null||values.Count==0)return 0;values.Sort();int middle=values.Count/2;return values.Count%2==0?(values[middle-1]+values[middle])*0.5f:values[middle];
        }
        static List<List<MainForm.WordBox>> GroupSelectionRows(List<MainForm.WordBox> words,out List<MainForm.WordBox> oversized,float bodyHeight)
        {
            oversized=new List<MainForm.WordBox>();var rows=new List<List<MainForm.WordBox>>();
            if(words==null||words.Count==0)return rows;
            float tallThreshold=Math.Max(bodyHeight*1.75f,bodyHeight+0.006f);
            var regular=new List<MainForm.WordBox>();var tallCandidates=new List<MainForm.WordBox>();
            foreach(MainForm.WordBox word in words)
            {
                if(word==null||String.IsNullOrWhiteSpace(word.Text))continue;
                float height=Math.Max(0,word.Y2-word.Y1);
                if(bodyHeight>0&&height>tallThreshold)tallCandidates.Add(word);else regular.Add(word);
            }
            // If this is a page made mostly of display text, keep it intact rather than
            // misclassifying most of the page as drop caps or oversized glyphs.
            if(regular.Count<Math.Max(3,words.Count/4)){regular.Clear();regular.AddRange(words);tallCandidates.Clear();}
            Func<List<MainForm.WordBox>,List<List<MainForm.WordBox>>> cluster=delegate(List<MainForm.WordBox> source)
            {
                var clustered=new List<List<MainForm.WordBox>>();source.Sort(delegate(MainForm.WordBox a,MainForm.WordBox b){float ac=(a.Y1+a.Y2)*0.5f,bc=(b.Y1+b.Y2)*0.5f;int y=ac.CompareTo(bc);return y!=0?y:a.X1.CompareTo(b.X1);});
                float centerSum=0;int count=0;
                foreach(MainForm.WordBox word in source)
                {
                    float center=(word.Y1+word.Y2)*0.5f;
                    if(clustered.Count==0||Math.Abs(center-centerSum/Math.Max(1,count))>Math.Max(bodyHeight*0.58f,0.0015f))
                    {clustered.Add(new List<MainForm.WordBox>());centerSum=center;count=1;}
                    else{centerSum+=center;count++;}
                    clustered[clustered.Count-1].Add(word);
                }
                return clustered;
            };
            rows=cluster(regular);
            var regularRowCenters=new List<float>();foreach(List<MainForm.WordBox> row in rows){var centers=new List<float>();foreach(MainForm.WordBox word in row)centers.Add((word.Y1+word.Y2)*0.5f);regularRowCenters.Add(Median(centers));}
            foreach(MainForm.WordBox candidate in tallCandidates)
            {
                int crossedRows=0;
                foreach(float center in regularRowCenters)
                {
                    if(candidate.Y1<center+bodyHeight*0.55f&&candidate.Y2>center-bodyHeight*0.55f)crossedRows++;
                }
                if(crossedRows>=2)oversized.Add(candidate);else regular.Add(candidate);
            }
            if(oversized.Count<tallCandidates.Count)rows=cluster(regular);
            return rows;
        }
        List<SelectionLine> BuildSelectionLines(List<MainForm.WordBox> words)
        {
            var result=new List<SelectionLine>();if(words==null||words.Count==0)return result;
            foreach(List<MainForm.WordBox> column in MainForm.BuildXYCutColumns(words))
            {
                var heights=new List<float>();foreach(MainForm.WordBox word in column)if(word!=null&&word.Y2>word.Y1)heights.Add(word.Y2-word.Y1);
                float bodyHeight=Median(heights);List<MainForm.WordBox> oversized;List<List<MainForm.WordBox>> rows=GroupSelectionRows(column,out oversized,bodyHeight);
                if(rows.Count==0)continue;
                var rowCenters=new List<float>();var rowHeights=new List<float>();
                foreach(List<MainForm.WordBox> row in rows)
                {
                    var centers=new List<float>();var heightsInRow=new List<float>();
                    foreach(MainForm.WordBox word in row){centers.Add((word.Y1+word.Y2)*0.5f);heightsInRow.Add(word.Y2-word.Y1);}
                    rowCenters.Add(Median(centers));rowHeights.Add(Math.Max(bodyHeight,Median(heightsInRow)));
                }
                for(int rowIndex=0;rowIndex<rows.Count;rowIndex++)
                {
                    List<MainForm.WordBox> row=rows[rowIndex];row.Sort(delegate(MainForm.WordBox a,MainForm.WordBox b){return a.X1.CompareTo(b.X1);});
                    float center=rowCenters[rowIndex],height=rowHeights[rowIndex];
                    float previousCenter=rowIndex>0?rowCenters[rowIndex-1]:Single.NaN;
                    float nextCenter=rowIndex<rows.Count-1?rowCenters[rowIndex+1]:Single.NaN;
                    float top=!Single.IsNaN(previousCenter)?(previousCenter+center)*0.5f:center-height*0.58f;
                    float bottom=!Single.IsNaN(nextCenter)?(center+nextCenter)*0.5f:center+height*0.58f;
                    // A distant heading or paragraph gap must not inflate a body line.
                    top=Math.Max(top,center-height*0.78f);bottom=Math.Min(bottom,center+height*0.78f);
                    top=Math.Max(0,top);bottom=Math.Min(1,bottom);
                    result.Add(new SelectionLine{Words=row,Bounds=RectangleF.FromLTRB(row[0].X1,top,row[row.Count-1].X2,bottom)});
                }
                // Paint a drop cap from its own box. Its full glyph stays highlighted
                // without changing the height of the adjacent body lines.
                foreach(MainForm.WordBox word in oversized)result.Add(new SelectionLine{Words=new List<MainForm.WordBox>{word},Bounds=WordRect(word)});
            }
            return result;
        }
        List<RectangleF> HighlightRects(List<MainForm.WordBox> words)
        {
            var result=new List<RectangleF>();if(words==null||words.Count==0)return result;var selected=new HashSet<MainForm.WordBox>(words);
            foreach(SelectionLine line in selectionLines)
            {
                float left=Single.MaxValue,right=Single.MinValue;bool has=false;
                foreach(MainForm.WordBox word in line.Words)if(selected.Contains(word)){left=Math.Min(left,word.X1);right=Math.Max(right,word.X2);has=true;}
                if(has&&right>left)result.Add(RectangleF.FromLTRB(left,line.Bounds.Top,right,line.Bounds.Bottom));
            }
            if(result.Count==0)foreach(MainForm.WordBox word in words)result.Add(WordRect(word));
            return result;
        }
        List<MainForm.WordBox> WordsBetween(Point from,Point to)
        {
            var result=new List<MainForm.WordBox>();if(pageWords==null||pageWords.Count==0)return result;
            MainForm.WordBox first=FindNearestWord(from,20),last=FindNearestWord(to,28);if(first==null||last==null)return result;
            int firstIndex,lastIndex;if(!readingOrderIndices.TryGetValue(first,out firstIndex)||!readingOrderIndices.TryGetValue(last,out lastIndex))return result;
            int low=Math.Min(firstIndex,lastIndex),high=Math.Max(firstIndex,lastIndex);for(int i=low;i<=high;i++)result.Add(readingOrderWords[i]);return result;
        }
        MainForm.WordBox FindNearestWord(Point point,float tolerance)
        {
            RectangleF bounds=ImageBounds();if(bounds.Width<=0||pageWords==null)return null;MainForm.WordBox best=null;float bestDistance=float.MaxValue;
            for(int i=pageWords.Count-1;i>=0;i--){MainForm.WordBox word=pageWords[i];RectangleF r=new RectangleF(bounds.X+word.X1*bounds.Width,bounds.Y+word.Y1*bounds.Height,(word.X2-word.X1)*bounds.Width,(word.Y2-word.Y1)*bounds.Height);float dx=point.X<r.Left?r.Left-point.X:point.X>r.Right?point.X-r.Right:0,dy=point.Y<r.Top?r.Top-point.Y:point.Y>r.Bottom?point.Y-r.Bottom:0,d=dx*dx+dy*dy;if(d<bestDistance){bestDistance=d;best=word;}}
            return bestDistance<=tolerance*tolerance?best:null;
        }
        public int FindWordIndexAt(float normalizedX,float normalizedY,float toleranceX,float toleranceY)
        {
            if(pageWords==null||pageWords.Count==0)return -1;int best=-1;double bestDistance=Double.MaxValue;
            for(int i=0;i<pageWords.Count;i++)
            {
                MainForm.WordBox word=pageWords[i];double dx=normalizedX<word.X1?word.X1-normalizedX:normalizedX>word.X2?normalizedX-word.X2:0;double dy=normalizedY<word.Y1?word.Y1-normalizedY:normalizedY>word.Y2?normalizedY-word.Y2:0;
                if(dx>toleranceX||dy>toleranceY)continue;double distance=dx*dx+dy*dy;if(distance<bestDistance){bestDistance=distance;best=i;}
            }
            return best;
        }
        public void SetRangeSelection(List<MainForm.WordBox> words){SetSelection(words,false);}
        public void ClearRangeSelectionVisual(){hasDraft=false;draft=RectangleF.Empty;draftWords=null;SelectedText="";hasCaret=false;caretVisible=false;if(caretBlinkTimer!=null)caretBlinkTimer.Stop();Invalidate();}
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
        void SetSelection(List<MainForm.WordBox> words){SetSelection(words,true);}
        void SetSelection(List<MainForm.WordBox> words,bool notifyMainForm)
        {
            if(words==null||words.Count==0){ClearDraft();return;}
            hasCaret=false;caretVisible=false;hasImageRegion=false;if(caretBlinkTimer!=null)caretBlinkTimer.Stop();
            draftWords=HighlightRects(words);float x=draftWords.Min(delegate(RectangleF r){return r.X;}),y=draftWords.Min(delegate(RectangleF r){return r.Y;}),right=draftWords.Max(delegate(RectangleF r){return r.Right;}),bottom=draftWords.Max(delegate(RectangleF r){return r.Bottom;});
            draft=new RectangleF(x,y,right-x,bottom-y);hasDraft=true;SelectedText=MainForm.FormatWordsInReadingOrder(words,pageWords);Invalidate();
            if(notifyMainForm&&RegionSelected!=null)RegionSelected(draft.X,draft.Y,draft.Width,draft.Height);
        }
        RectangleF ImageBounds()
        {
            if (Image == null || ClientSize.Width == 0 || ClientSize.Height == 0) return RectangleF.Empty;
            float scale = Math.Min((float)ClientSize.Width / Image.Width, (float)ClientSize.Height / Image.Height); float w = Image.Width * scale, h = Image.Height * scale;
            return new RectangleF((ClientSize.Width-w)/2f, (ClientSize.Height-h)/2f, w, h);
        }
        // Rectangle the page currently occupies in this control, in client pixels.
        public RectangleF ImageRect { get { return ImageBounds(); } }
        public int RegionDpi { get { return regionDpi; } }
        public RectangleF RegionBounds { get { return regionBounds; } }
        public void SetRegion(Bitmap bitmap,RectangleF bounds,int dpi)
        {
            if(bitmap==null)return;
            if(regionImage!=null)regionImage.Dispose();
            regionImage=bitmap;regionBounds=bounds;regionDpi=dpi;Invalidate();
        }
        public void ClearRegion()
        {
            if(regionImage==null)return;
            regionImage.Dispose();regionImage=null;regionBounds=RectangleF.Empty;regionDpi=0;Invalidate();
        }
        public bool CoversRegion(RectangleF bounds,int dpi)
        {
            if(regionImage==null||Math.Abs(regionDpi-dpi)>8)return false;
            const float eps=0.0015f;
            return regionBounds.X<=bounds.X+eps&&regionBounds.Y<=bounds.Y+eps&&regionBounds.Right>=bounds.Right-eps&&regionBounds.Bottom>=bounds.Bottom-eps;
        }
        bool HitMark(Point p) { return FindMarkAt(p)!=null; }
        Mark FindMarkAt(Point p) { if(getMarks==null)return null;RectangleF b=ImageBounds();if(b.Width<=0)return null;List<Mark> list=getMarks();if(list==null)return null;for(int i=list.Count-1;i>=0;i--){Mark m=list[i];if(m.W>0&&m.H>0){RectangleF r=new RectangleF(b.X+m.X*b.Width,b.Y+m.Y*b.Height,m.W*b.Width,m.H*b.Height);if(r.Contains(p))return m;}}return null; }
        public static double PaintSetupMs, PaintDrawMs, PaintBlitMs, PaintClearMs, PaintBaseMs, PaintTileMs;
        IntPtr paintDc, paintHBitmap, paintBits;
        int paintWidth, paintHeight;
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest,int x,int y,int width,int height,IntPtr source,int sourceX,int sourceY,int rop);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc,IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc,ref BitmapInfoHeader header,uint usage,out IntPtr bits,IntPtr section,uint offset);
        [StructLayout(LayoutKind.Sequential)] struct BitmapInfoHeader
        {
            public uint Size; public int Width; public int Height; public ushort Planes; public ushort BitCount;
            public uint Compression; public uint SizeImage; public int XPelsPerMeter; public int YPelsPerMeter;
            public uint ClrUsed; public uint ClrImportant;
        }
        const int SrcCopy=0x00CC0020;
        // A DIB section we draw into with GDI+, then copy to the window with one GDI blit. The
        // pixels live in memory we own, so what we drew is exactly what gets blitted; drawing
        // straight into a window this large costs GDI+ tens of milliseconds per image.
        void EnsurePaintBuffer(int width,int height)
        {
            if(paintDc!=IntPtr.Zero&&paintWidth==width&&paintHeight==height)return;
            ReleasePaintBuffer();
            var header=new BitmapInfoHeader{Size=(uint)Marshal.SizeOf(typeof(BitmapInfoHeader)),Width=width,Height=-height,Planes=1,BitCount=32,Compression=0};
            paintDc=CreateCompatibleDC(IntPtr.Zero);
            paintHBitmap=CreateDIBSection(paintDc,ref header,0,out paintBits,IntPtr.Zero,0);
            if(paintHBitmap==IntPtr.Zero){DeleteDC(paintDc);paintDc=IntPtr.Zero;return;}
            SelectObject(paintDc,paintHBitmap);
            paintWidth=width;paintHeight=height;
        }
        void ReleasePaintBuffer()
        {
            if(paintHBitmap!=IntPtr.Zero){DeleteObject(paintHBitmap);paintHBitmap=IntPtr.Zero;}
            if(paintDc!=IntPtr.Zero){DeleteDC(paintDc);paintDc=IntPtr.Zero;}
            paintBits=IntPtr.Zero;paintWidth=paintHeight=0;
        }
        // Part of this control that its container actually shows. A page canvas is as tall as
        // the whole page, so at high zoom only a window into it is on screen.
        Rectangle VisibleArea()
        {
            Rectangle area = ClientRectangle;
            Control parent = Parent;
            if (parent != null)
            {
                int left = -Left, top = -Top;
                area = Rectangle.Intersect(area, new Rectangle(left, top, parent.ClientSize.Width, parent.ClientSize.Height));
            }
            return area;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            // The base PictureBox paints by scaling the whole page bitmap into a client area that
            // is tens of thousands of pixels across at high zoom; GDI+ stalls for seconds on that
            // and on any image drawn straight into such a window, which is what made maximum zoom
            // unusable. Here the visible slice is composed into a window-sized buffer and handed
            // to the screen with a plain GDI blit, so cost follows the window, not the page.
            Rectangle clip = Rectangle.Intersect(e.ClipRectangle, VisibleArea());
            if (clip.Width <= 0 || clip.Height <= 0) return;
            EnsurePaintBuffer(clip.Width, clip.Height);
            if (paintDc == IntPtr.Zero) return;
            using (Graphics g = Graphics.FromHdc(paintDc))
            {
                g.TranslateTransform(-clip.X, -clip.Y);
                DrawPage(g, new RectangleF(clip.X, clip.Y, clip.Width, clip.Height));
            }
            IntPtr target = e.Graphics.GetHdc();
            try { BitBlt(target, clip.X, clip.Y, clip.Width, clip.Height, paintDc, 0, 0, SrcCopy); }
            finally { e.Graphics.ReleaseHdc(target); }
        }
        // Splits the area to paint around the tile, so the expensive upscaled page bitmap is only
        // drawn where the sharp tile does not already cover it.
        static List<RectangleF> Without(RectangleF area,RectangleF hole)
        {
            var parts=new List<RectangleF>();
            if(area.Width<=0||area.Height<=0)return parts;
            RectangleF overlap=RectangleF.Intersect(area,hole);
            if(overlap.Width<=0||overlap.Height<=0){parts.Add(area);return parts;}
            if(overlap.Top>area.Top)parts.Add(new RectangleF(area.X,area.Y,area.Width,overlap.Top-area.Top));
            if(overlap.Bottom<area.Bottom)parts.Add(new RectangleF(area.X,overlap.Bottom,area.Width,area.Bottom-overlap.Bottom));
            if(overlap.Left>area.Left)parts.Add(new RectangleF(area.X,overlap.Top,overlap.Left-area.X,overlap.Height));
            if(overlap.Right<area.Right)parts.Add(new RectangleF(overlap.Right,overlap.Top,area.Right-overlap.Right,overlap.Height));
            return parts;
        }
        void DrawPageSlice(Graphics g,RectangleF area,RectangleF page)
        {
            if(Image==null)return;
            RectangleF visible=RectangleF.Intersect(page,area);
            if(visible.Width<=0||visible.Height<=0)return;
            float k=Image.Width/page.Width;
            var source=new RectangleF((visible.X-page.X)*k,(visible.Y-page.Y)*k,visible.Width*k,visible.Height*k);
            // The whole-page bitmap is heavily upscaled at high zoom; nearest-neighbour keeps that
            // cheap, and it is only visible until the sharp tile for the same area arrives.
            g.CompositingMode=CompositingMode.SourceCopy;
            g.InterpolationMode=k<0.5f?InterpolationMode.Bilinear:InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode=PixelOffsetMode.HighSpeed;
            g.DrawImage(Image,visible,source,GraphicsUnit.Pixel);
            g.CompositingMode=CompositingMode.SourceOver;
            g.InterpolationMode=InterpolationMode.Default;
        }
        void DrawPage(Graphics g, RectangleF clip)
        {
            g.Clear(Color.White);
            RectangleF b = ImageBounds(); if (b.Width <= 0) return;
            RectangleF tile = RectangleF.Empty;
            if (regionImage != null && regionBounds.Width > 0 && regionBounds.Height > 0)
                tile = new RectangleF(b.X+regionBounds.X*b.Width,b.Y+regionBounds.Y*b.Height,regionBounds.Width*b.Width,regionBounds.Height*b.Height);
            if (Image != null)
            {
                foreach (RectangleF part in Without(clip, tile)) DrawPageSlice(g, part, b);
            }

            if (regionImage != null && regionBounds.Width > 0 && regionBounds.Height > 0)
            {
                var target = new RectangleF(b.X+regionBounds.X*b.Width,b.Y+regionBounds.Y*b.Height,regionBounds.Width*b.Width,regionBounds.Height*b.Height);
                RectangleF shown = RectangleF.Intersect(target, clip);
                if (shown.Width > 0 && shown.Height > 0)
                {
                    var source = new RectangleF((shown.X-target.X)*regionImage.Width/target.Width,(shown.Y-target.Y)*regionImage.Height/target.Height,shown.Width*regionImage.Width/target.Width,shown.Height*regionImage.Height/target.Height);
                    // The tile was rendered for exactly this on-screen size, so it only needs a
                    // copy; interpolating it here costs tens of milliseconds for no visible gain.
                    Rectangle destination = Rectangle.Round(shown);
                    Rectangle origin = Rectangle.Round(source);
                    origin.Width = Math.Min(origin.Width, regionImage.Width - origin.X);
                    origin.Height = Math.Min(origin.Height, regionImage.Height - origin.Y);
                    destination.Width = Math.Min(destination.Width, origin.Width);
                    destination.Height = Math.Min(destination.Height, origin.Height);
                    InterpolationMode previous = g.InterpolationMode;
                    g.CompositingMode=CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    if (destination.Width > 0 && destination.Height > 0) g.DrawImage(regionImage, destination, origin, GraphicsUnit.Pixel);
                    g.InterpolationMode = previous;
                    g.CompositingMode=CompositingMode.SourceOver;
                }
            }
            List<Mark> all = getMarks == null ? null : getMarks();
            Color[] palette = new Color[] { Color.FromArgb(255, 224, 130), Color.FromArgb(144, 202, 249), Color.FromArgb(225, 190, 231), Color.FromArgb(165, 214, 167), Color.FromArgb(255, 171, 145) };
            if (all != null) foreach (Mark m in all) if (m.Page > 0 && m.W > 0 && m.H > 0) { RectangleF r = new RectangleF(b.X+m.X*b.Width,b.Y+m.Y*b.Height,m.W*b.Width,m.H*b.Height); Color c=palette[Math.Abs(m.ColorIndex)%palette.Length]; using (var brush = new SolidBrush(Color.FromArgb(105,c))) g.FillRectangle(brush,r); using (var pen = new Pen(Color.FromArgb(190,c),1)) g.DrawRectangle(pen,r.X,r.Y,r.Width,r.Height); }
            using(var brush=new SolidBrush(Color.FromArgb(150,66,133,244)))
            {
                if(hasDraft&&draftWords!=null)foreach(RectangleF word in draftWords)g.FillRectangle(brush,b.X+word.X*b.Width,b.Y+word.Y*b.Height,word.Width*b.Width,word.Height*b.Height);
                if(dragging&&previewWords!=null)foreach(RectangleF word in previewWords)g.FillRectangle(brush,b.X+word.X*b.Width,b.Y+word.Y*b.Height,word.Width*b.Width,word.Height*b.Height);
            }
            if(hasImageRegion){RectangleF r=new RectangleF(b.X+imageRegion.X*b.Width,b.Y+imageRegion.Y*b.Height,imageRegion.Width*b.Width,imageRegion.Height*b.Height);using(var pen=new Pen(Color.FromArgb(230,66,133,244),2)){pen.DashStyle=DashStyle.Dash;g.DrawRectangle(pen,r.X,r.Y,r.Width,r.Height);}}
            if(connectors.Count>0)
            {
                using(var pen=new Pen(Color.FromArgb(220,198,40,40),2))using(var dot=new SolidBrush(Color.FromArgb(220,198,40,40)))
                    foreach(Point[] segment in connectors.Values)
                    {
                        if(segment==null||segment.Length<2)continue;
                        g.DrawLine(pen,segment[0],segment[1]);
                        g.FillEllipse(dot,segment[0].X-3,segment[0].Y-3,6,6);
                        g.FillEllipse(dot,segment[1].X-3,segment[1].Y-3,6,6);
                    }
            }
            if(dragging&&previewImageRegion){RectangleF r=RectangleF.Intersect(Normalize(start,end),b);if(r.Width>0&&r.Height>0)using(var pen=new Pen(Color.FromArgb(230,66,133,244),2)){pen.DashStyle=DashStyle.Dash;g.DrawRectangle(pen,r.X,r.Y,r.Width,r.Height);}}
            if(hasCaret&&caretVisible&&caretWordIndex>=0&&caretWordIndex<pageWords.Count)
            {
                MainForm.WordBox word=pageWords[caretWordIndex];RectangleF r=new RectangleF(b.X+word.X1*b.Width,b.Y+word.Y1*b.Height,(word.X2-word.X1)*b.Width,(word.Y2-word.Y1)*b.Height);int len=Math.Max(1,(word.Text??"").Length);float caretGap=caretOffset==0?-2f:2f;float x=r.Left+r.Width*caretOffset/len+caretGap;using(var pen=new Pen(Color.Black,1f))g.DrawLine(pen,x,r.Top,x,Math.Max(r.Top+3,r.Bottom));
            }
            else if(hasCaret&&caretVisible&&caretWordIndex<0)
            {
                float x=b.X+caretNormX*b.Width+2f,y=b.Y+caretNormY*b.Height,h=Math.Max(16,b.Height*0.018f),top=Math.Max(b.Top,Math.Min(b.Bottom-h,y-h/2));using(var pen=new Pen(Color.Black,1f))g.DrawLine(pen,x,top,x,top+h);
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
