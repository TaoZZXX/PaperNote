using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PaperNote
{
    internal sealed class DoubaoTranslationForm : Form
    {
        readonly DoubaoSettings settings;
        readonly byte[] screenshotPng;
        readonly TextBox sourceBox = new TextBox();
        readonly TextBox questionBox = new TextBox();
        readonly TextBox resultBox = new TextBox();
        readonly Label status = new Label();
        readonly Button translateButton = new Button();
        readonly Button explainButton = new Button();
        readonly Button summarizeButton = new Button();
        readonly Button sendButton = new Button();
        readonly Button copyButton = new Button();
        readonly Timer debounce = new Timer { Interval = 1000 };
        readonly List<CompletionMessage> explanationHistory = new List<CompletionMessage>();
        DoubaoStartMode mode;
        bool busy;
        bool pendingRun;
        int sourceVersion;
        string pageCaption;

        public DoubaoTranslationForm(string text, int firstPage, int lastPage, DoubaoSettings configuration,
                                     DoubaoStartMode startMode)
            : this(text, firstPage, lastPage, configuration, startMode, null)
        {
        }

        public DoubaoTranslationForm(string text, int firstPage, int lastPage, DoubaoSettings configuration,
                                     DoubaoStartMode startMode, Bitmap screenshot)
        {
            settings = configuration;
            if (screenshot != null)
            {
                using (var stream = new MemoryStream())
                {
                    screenshot.Save(stream, ImageFormat.Png);
                    screenshotPng = stream.ToArray();
                }
            }
            mode = startMode;
            pageCaption = firstPage == lastPage ? "第 " + firstPage + " 页" : "第 " + firstPage + "–" + lastPage + " 页";
            Text = "豆包论文助手";
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            ShowIcon = false;
            MinimumSize = new Size(850, 560);
            Size = new Size(1180, 780);
            Font = new Font("Microsoft YaHei UI", 10F);
            BackColor = PdfiumEngine.PaperBackgroundColor;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                ColumnCount = 2,
                RowCount = 1,
                BackColor = PdfiumEngine.PaperBackgroundColor
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));

            var left = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 12, 0), ColumnCount = 1, RowCount = 6, BackColor = PdfiumEngine.PaperBackgroundColor };
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            left.Controls.Add(SectionLabel((screenshotPng == null ? "论文输入　·　" : "截图输入　·　") + pageCaption), 0, 0);
            ConfigureTextBox(sourceBox, false);
            sourceBox.Text = text ?? "";
            left.Controls.Add(sourceBox, 0, 1);
            left.Controls.Add(SectionLabel("我输入的问题"), 0, 2);
            ConfigureTextBox(questionBox, false);
            questionBox.AcceptsReturn = true;
            left.Controls.Add(questionBox, 0, 3);

            var modes = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = PdfiumEngine.PaperBackgroundColor, Padding = new Padding(0, 5, 0, 0) };
            ConfigureModeButton(translateButton, "翻译");
            ConfigureModeButton(explainButton, "解释");
            ConfigureModeButton(summarizeButton, "总结");
            modes.Controls.Add(translateButton);
            modes.Controls.Add(explainButton);
            modes.Controls.Add(summarizeButton);
            left.Controls.Add(modes, 0, 4);

            var sendPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = PdfiumEngine.PaperBackgroundColor };
            sendButton.Text = "发送";
            sendButton.Width = 92;
            sendButton.Height = 36;
            sendButton.FlatStyle = FlatStyle.Flat;
            sendButton.BackColor = Color.FromArgb(26, 115, 232);
            sendButton.ForeColor = Color.White;
            sendButton.FlatAppearance.BorderSize = 0;
            sendPanel.Controls.Add(sendButton);
            left.Controls.Add(sendPanel, 0, 5);
            root.Controls.Add(left, 0, 0);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = PdfiumEngine.PaperBackgroundColor };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            ConfigureTextBox(resultBox, true);
            resultBox.Font = new Font("Microsoft YaHei UI", 11F);
            right.Controls.Add(resultBox, 0, 0);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = PdfiumEngine.PaperBackgroundColor };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            status.Text = screenshotPng == null
                ? "选区只会在你点击翻译、解释、总结或发送问题时提交给豆包 API。"
                : "截图会随请求发送；当前模型/接入点需要支持图片输入。";
            status.ForeColor = Color.FromArgb(95, 99, 104);
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            footer.Controls.Add(status, 0, 0);
            copyButton.Text = "复制结果";
            copyButton.Dock = DockStyle.Fill;
            footer.Controls.Add(copyButton, 1, 0);
            right.Controls.Add(footer, 0, 1);
            root.Controls.Add(right, 1, 0);
            Controls.Add(root);

            translateButton.Click += async delegate { await SelectModeAsync(DoubaoStartMode.Translate); };
            explainButton.Click += async delegate { await SelectModeAsync(DoubaoStartMode.Explain); };
            summarizeButton.Click += async delegate { await SelectModeAsync(DoubaoStartMode.Summarize); };
            sendButton.Click += async delegate { await AskQuestionAsync(); };
            questionBox.KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && !e.Shift)
                {
                    e.SuppressKeyPress = true;
                    await AskQuestionAsync();
                }
            };
            copyButton.Click += delegate
            {
                if (!String.IsNullOrWhiteSpace(resultBox.Text)) Clipboard.SetText(resultBox.Text);
            };
            sourceBox.TextChanged += delegate
            {
                sourceVersion++;
                explanationHistory.Clear();
                resultBox.Clear();
                pendingRun = true;
                debounce.Stop();
                status.Text = "论文选区已修改，停顿 1 秒后按当前模式重新处理。";
                debounce.Start();
            };
            debounce.Tick += async delegate
            {
                debounce.Stop();
                if (busy) { pendingRun = true; return; }
                pendingRun = false;
                await RunCurrentModeAsync();
            };
            FormClosed += delegate { debounce.Stop(); debounce.Dispose(); };
            UpdateModeButtons();
            Shown += async delegate { await RunCurrentModeAsync(); };
        }

        static Label SectionLabel(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(60, 64, 67), TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold) };
        }

        static void ConfigureTextBox(TextBox box, bool readOnly)
        {
            box.Multiline = true;
            box.ReadOnly = readOnly;
            box.WordWrap = true;
            box.AcceptsReturn = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.Dock = DockStyle.Fill;
            box.Font = new Font("Microsoft YaHei UI", 10F);
            box.BackColor = PdfiumEngine.PaperBackgroundColor;
            box.BorderStyle = BorderStyle.FixedSingle;
        }

        static void ConfigureModeButton(Button button, string text)
        {
            button.Text = text;
            button.Width = 94;
            button.Height = 36;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(218, 220, 224);
            button.BackColor = Color.White;
            button.ForeColor = Color.FromArgb(60, 64, 67);
            button.Margin = new Padding(0, 0, 8, 0);
        }

        async Task SelectModeAsync(DoubaoStartMode selectedMode)
        {
            mode = selectedMode;
            explanationHistory.Clear();
            UpdateModeButtons();
            await RunCurrentModeAsync();
        }

        void UpdateModeButtons()
        {
            SetModeStyle(translateButton, mode == DoubaoStartMode.Translate);
            SetModeStyle(explainButton, mode == DoubaoStartMode.Explain);
            SetModeStyle(summarizeButton, mode == DoubaoStartMode.Summarize);
        }

        static void SetModeStyle(Button button, bool selected)
        {
            button.BackColor = selected ? Color.FromArgb(232, 240, 254) : Color.White;
            button.ForeColor = selected ? Color.FromArgb(26, 115, 232) : Color.FromArgb(60, 64, 67);
            button.FlatAppearance.BorderColor = selected ? Color.FromArgb(26, 115, 232) : Color.FromArgb(218, 220, 224);
        }

        async Task RunCurrentModeAsync()
        {
            if (mode == DoubaoStartMode.Translate) await RunTextTaskAsync(false);
            else if (mode == DoubaoStartMode.Summarize) await RunTextTaskAsync(true);
            else await ExplainAsync(null, true);
        }

        async Task RunTextTaskAsync(bool summarize)
        {
            if (busy) return;
            string source = sourceBox.Text.Trim();
            if (String.IsNullOrWhiteSpace(source) && screenshotPng == null) { status.Text = "请先输入论文内容。"; return; }
            int version = sourceVersion;
            busy = true;
            SetBusy(true);
            resultBox.Clear();
            Stopwatch elapsed = Stopwatch.StartNew();
            long lastUiProgressMs = -50;
            status.Text = summarize ? "正在生成详细总结…" : "正在翻译…";
            var progress = new Progress<string>(partial =>
            {
                if (IsDisposed || version != sourceVersion || elapsed.ElapsedMilliseconds - lastUiProgressMs < 50) return;
                lastUiProgressMs = elapsed.ElapsedMilliseconds;
                UpdateStreamingResult(partial);
                status.Text = (summarize ? "正在整理总结… " : "正在接收译文… ") + elapsed.Elapsed.ToString(@"ss\.f") + " 秒";
            });
            try
            {
                string output = await Task.Run(() => summarize
                    ? (screenshotPng == null ? DoubaoTranslation.Summarize(settings, source, progress)
                                             : DoubaoTranslation.SummarizeImage(settings, source, screenshotPng, progress))
                    : (screenshotPng == null ? DoubaoTranslation.Translate(settings, source, progress)
                                             : DoubaoTranslation.TranslateImage(settings, source, screenshotPng, progress)));
                if (!IsDisposed && version == sourceVersion)
                {
                    UpdateStreamingResult(output);
                    status.Text = (summarize ? "总结完成 · " : "翻译完成 · ") + elapsed.Elapsed.ToString(@"ss\.f") + " 秒。";
                }
            }
            catch (Exception ex) { if (!IsDisposed) status.Text = (summarize ? "总结失败：" : "翻译失败：") + ex.GetBaseException().Message; }
            finally
            {
                busy = false;
                if (!IsDisposed)
                {
                    SetBusy(false);
                    if (pendingRun)
                    {
                        pendingRun = false;
                        BeginInvoke(new MethodInvoker(async delegate { await RunCurrentModeAsync(); }));
                    }
                }
            }
        }

        async Task AskQuestionAsync()
        {
            string question = questionBox.Text.Trim();
            if (String.IsNullOrWhiteSpace(question)) return;
            if (busy) { status.Text = "当前请求完成后再发送问题。"; return; }
            questionBox.Clear();
            mode = DoubaoStartMode.Explain;
            UpdateModeButtons();
            await ExplainAsync(question, explanationHistory.Count == 0);
        }

        async Task ExplainAsync(string question, bool reset)
        {
            if (busy) return;
            string source = sourceBox.Text.Trim();
            if (String.IsNullOrWhiteSpace(source) && screenshotPng == null) { status.Text = "请先输入论文内容。"; return; }
            if (reset || explanationHistory.Count == 0)
            {
                explanationHistory.Clear();
                string prompt = screenshotPng == null
                    ? "请解释这段论文内容，说明研究意图、关键术语、模型假设、方法步骤、公式变量及其作用；不确定的信息请明确指出。\r\n\r\n论文选区：\r\n" + source
                    : "请解释附带的论文截图，说明可见内容、图表/公式含义和研究逻辑；无法从截图确认的信息请指出。" +
                      (String.IsNullOrWhiteSpace(source) ? "" : "\r\n补充说明：\r\n" + source);
                if (!String.IsNullOrWhiteSpace(question)) prompt += "\r\n\r\n用户问题：\r\n" + question;
                explanationHistory.Add(new CompletionMessage { role = "user", content = prompt });
            }
            else if (!String.IsNullOrWhiteSpace(question))
                explanationHistory.Add(new CompletionMessage { role = "user", content = question });

            int version = sourceVersion;
            var conversation = new List<CompletionMessage>(explanationHistory);
            busy = true;
            SetBusy(true);
            resultBox.Clear();
            Stopwatch elapsed = Stopwatch.StartNew();
            long lastUiProgressMs = -50;
            status.Text = String.IsNullOrWhiteSpace(question) ? "正在解释选区…" : "正在回答问题…";
            var progress = new Progress<string>(partial =>
            {
                if (IsDisposed || version != sourceVersion || elapsed.ElapsedMilliseconds - lastUiProgressMs < 50) return;
                lastUiProgressMs = elapsed.ElapsedMilliseconds;
                UpdateStreamingResult(partial);
                status.Text = "正在生成回答… " + elapsed.Elapsed.ToString(@"ss\.f") + " 秒";
            });
            try
            {
                string answer = await Task.Run(() => screenshotPng == null
                    ? DoubaoTranslation.ContinueExplanation(settings, conversation, progress)
                    : DoubaoTranslation.ContinueImageExplanation(settings, conversation, screenshotPng, progress));
                if (!IsDisposed && version == sourceVersion)
                {
                    explanationHistory.Add(new CompletionMessage { role = "assistant", content = answer });
                    UpdateStreamingResult(answer);
                    status.Text = "回答完成 · " + elapsed.Elapsed.ToString(@"ss\.f") + " 秒。可以继续提问。";
                }
            }
            catch (Exception ex) { if (!IsDisposed) status.Text = "解释失败：" + ex.GetBaseException().Message; }
            finally
            {
                busy = false;
                if (!IsDisposed)
                {
                    SetBusy(false);
                    if (pendingRun)
                    {
                        pendingRun = false;
                        BeginInvoke(new MethodInvoker(async delegate { await RunCurrentModeAsync(); }));
                    }
                }
            }
        }

        void UpdateStreamingResult(string value)
        {
            if (String.IsNullOrEmpty(value) || resultBox.IsDisposed) return;
            string current = resultBox.Text;
            if (String.Equals(current, value, StringComparison.Ordinal)) return;
            if (value.StartsWith(current, StringComparison.Ordinal))
            {
                resultBox.AppendText(value.Substring(current.Length));
            }
            else
            {
                int common = 0;
                int limit = Math.Min(current.Length, value.Length);
                while (common < limit && current[common] == value[common]) common++;
                resultBox.Select(common, current.Length - common);
                resultBox.SelectedText = value.Substring(common);
            }
            resultBox.SelectionStart = resultBox.TextLength;
            resultBox.ScrollToCaret();
        }

        void SetBusy(bool value)
        {
            translateButton.Enabled = !value;
            explainButton.Enabled = !value;
            summarizeButton.Enabled = !value;
            sendButton.Enabled = !value;
            copyButton.Enabled = !value;
        }
    }
}
