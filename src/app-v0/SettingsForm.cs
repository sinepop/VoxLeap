using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace VoxLeap
{
    // ------------------------------------------------------------------
    // 设置窗口视觉规范（2026-09-06 依据 taste-skill 适配 WinForms 重制）
    //  - 单一强调色：蓝青（与 Logo 同源），全窗口锁定，不换色；
    //  - 中性色统一冷调，不用纯黑纯白；
    //  - 形状体系一致：控件全部直角/系统原生描边，不混用圆角；
    //  - 层级靠字号 + 字重 + 颜色 + 留白，不靠卡片和描边堆叠；
    //  - 正文不小于 12px（9pt = 12px 为最小档）。
    // ------------------------------------------------------------------
    internal static class SettingsTheme
    {
        public static readonly Color WindowBackground = Color.FromArgb(246, 248, 248);
        public static readonly Color Surface = Color.FromArgb(255, 255, 255);
        public static readonly Color SurfaceLocked = Color.FromArgb(240, 243, 243);
        public static readonly Color SurfaceHover = Color.FromArgb(236, 241, 241);
        public static readonly Color Border = Color.FromArgb(213, 222, 223);
        public static readonly Color Hairline = Color.FromArgb(227, 233, 233);

        public static readonly Color Accent = Color.FromArgb(23, 121, 138);
        public static readonly Color AccentHover = Color.FromArgb(17, 96, 111);
        public static readonly Color AccentPressed = Color.FromArgb(13, 79, 91);
        public static readonly Color AccentTint = Color.FromArgb(232, 241, 243);

        public static readonly Color TextPrimary = Color.FromArgb(31, 45, 50);
        public static readonly Color TextLabel = Color.FromArgb(58, 74, 80);
        public static readonly Color TextSecondary = Color.FromArgb(90, 107, 112);
        public static readonly Color TextOnAccent = Color.FromArgb(255, 255, 255);

        public static readonly Color ErrorText = Color.FromArgb(176, 82, 78);
        public static readonly Color SuccessText = Color.FromArgb(46, 125, 91);
        public static readonly Color WarningText = Color.FromArgb(154, 106, 31);

        public const int LabelColumnWidth = 116;
        public const int HelperMaxWidth = 470;

        // 9.5pt ≈ 12.7px 正文；9pt = 12px 为辅助文字下限，均不低于项目 12px 约束。
        public static readonly Font Body = new Font("Microsoft YaHei UI", 9.5f);
        public static readonly Font Small = new Font("Microsoft YaHei UI", 9f);
        public static readonly Font Strong = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        public static readonly Font Title = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
    }

    // 扁平分页标签：用强调色文字 + 底部 2px 强调条替代系统 TabControl 的立体页签，
    // 保留左右方向键切换与可访问性名称。
    internal sealed class FlatTabs : Control
    {
        private readonly string[] _titles;
        private Rectangle[] _rects;
        private int _selected;
        private int _hover = -1;

        public event EventHandler SelectedChanged;

        public FlatTabs(params string[] titles)
        {
            _titles = titles;
            _rects = new Rectangle[0];
            Height = 42;
            MinimumSize = new Size(0, 42);
            TabStop = true;
            Font = SettingsTheme.Body;
            BackColor = SettingsTheme.WindowBackground;
            ForeColor = SettingsTheme.TextSecondary;
            Cursor = Cursors.Default;
            AccessibleName = "设置分区";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (value < 0 || value >= _titles.Length || value == _selected) return;
                _selected = value;
                Invalidate();
                EventHandler handler = SelectedChanged;
                if (handler != null)
                {
                    try { handler(this, EventArgs.Empty); }
                    catch { }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            _rects = ComputeRects(g);
            for (int i = 0; i < _titles.Length; i++)
            {
                Color color = i == _selected
                    ? SettingsTheme.Accent
                    : (i == _hover ? SettingsTheme.TextPrimary : SettingsTheme.TextSecondary);
                TextRenderer.DrawText(g, _titles[i], Font, _rects[i], color,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            }

            int bottom = Height - 1;
            using (var line = new Pen(SettingsTheme.Hairline, 1f))
            {
                g.DrawLine(line, 0, bottom, Width, bottom);
            }
            if (_selected >= 0 && _selected < _rects.Length && _rects[_selected].Width > 0)
            {
                using (var bar = new SolidBrush(SettingsTheme.Accent))
                {
                    g.FillRectangle(bar, _rects[_selected].X, Height - 3, _rects[_selected].Width, 2);
                }
            }
            // 键盘焦点可见指示（审查 P1）：Tab 聚焦后绘制焦点框，避免自绘控件丢失焦点可感知性。
            if (Focused && _selected >= 0 && _selected < _rects.Length)
            {
                Rectangle cue = _rects[_selected];
                cue.Inflate(4, -8);
                ControlPaint.DrawFocusRectangle(g, cue);
            }
        }

        private Rectangle[] ComputeRects(Graphics g)
        {
            var rects = new Rectangle[_titles.Length];
            int x = Padding.Left;
            for (int i = 0; i < _titles.Length; i++)
            {
                Size size = TextRenderer.MeasureText(g, _titles[i], Font);
                rects[i] = new Rectangle(x, 0, size.Width + 4, Height);
                x = rects[i].Right + 26;
            }
            return rects;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _rects = new Rectangle[0];
        }

        private int HitTest(Point location)
        {
            // 审查 P1：首次 OnPaint 之前也可能收到鼠标消息，按需预热命中矩形。
            if (_rects.Length == 0)
            {
                using (Graphics g = CreateGraphics())
                {
                    _rects = ComputeRects(g);
                }
            }
            for (int i = 0; i < _rects.Length; i++)
            {
                if (_rects[i].Contains(location)) return i;
            }
            return -1;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int index = HitTest(e.Location);
            if (index >= 0)
            {
                SelectedIndex = index;
                Focus();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = HitTest(e.Location);
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            if (index == _hover) return;
            _hover = index;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            Cursor = Cursors.Default;
            if (_hover == -1) return;
            _hover = -1;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right
                || keyData == Keys.Home || keyData == Keys.End) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            // 对抗审查 P2：补 Home/End 语义，与原生 TabControl 一致（文档 TODO）。
            if (e.KeyCode == Keys.Left)
            {
                SelectedIndex = Math.Max(0, _selected - 1);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Right)
            {
                SelectedIndex = Math.Min(_titles.Length - 1, _selected + 1);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Home)
            {
                SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.End)
            {
                SelectedIndex = _titles.Length - 1;
                e.Handled = true;
            }
        }
    }

    // 底部操作区：顶部一条 1px 细分隔线，替代立体边框。
    internal sealed class HairlineTopTable : TableLayoutPanel
    {
        public HairlineTopTable()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(SettingsTheme.Hairline, 1f))
            {
                e.Graphics.DrawLine(pen, 0, 0, Width, 0);
            }
        }
    }

    internal sealed class SettingsForm : Form
    {
        private sealed class ServiceOption
        {
            public string Api;
            public string Title;

            public ServiceOption(string api, string title)
            {
                Api = api;
                Title = title;
            }

            public override string ToString()
            {
                return Title;
            }
        }

        private sealed class ChoiceOption
        {
            public string Value;
            public string Title;

            public ChoiceOption(string value, string title)
            {
                Value = value;
                Title = title;
            }

            public override string ToString()
            {
                return Title;
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private readonly Action<Config> _onSaved;
        private readonly Func<bool> _canSaveNow;
        private readonly Config _baseConfig;
        private readonly ISecretProtector _protector;

        private readonly FlatTabs _tabs;
        private readonly Panel _commonScroll;
        private readonly Panel _advancedScroll;
        private ComboBox _serviceBox;
        private Label _serviceHint;
        private LinkLabel _docsLink;
        private TextBox _baseUrlBox;
        private TextBox _endpointBox;
        private TextBox _modelBox;
        private TextBox _apiKeyBox;
        private CheckBox _showApiKeyBox;
        private ComboBox _languageBox;
        private ComboBox _hotkeyBox;
        private ComboBox _hotkeyModeBox;
        private TextBox _hotwordsBox;
        private CheckBox _autoInsertBox;
        private CheckBox _enableVadBox;
        private CheckBox _aiOrganizeBox;
        private CheckBox _overrideEndpointBox;
        private NumericUpDown _maxRecordSeconds;
        private NumericUpDown _timeoutSeconds;
        private NumericUpDown _vadThreshold;
        private NumericUpDown _vadPaddingMs;
        private CheckBox _autoStopBox;
        private NumericUpDown _autoStopSilenceMs;
        private CheckBox _streamingBox;
        private TextBox _organizerBaseUrlBox;
        private TextBox _organizerEndpointBox;
        private TextBox _organizerModelBox;
        private TextBox _organizerApiKeyBox;
        private readonly Label _statusLabel;
        private readonly Button _saveButton;
        private readonly Button _testButton;
        private readonly Button _cancelButton;

        private bool _loading;
        private bool _testing;
        private string _currentApi = "sse";
        private string _customBaseUrl = "";
        private string _customEndpoint = "/audio/transcriptions";
        private string _customModel = "";

        public SettingsForm(Config current, Action<Config> onSaved, Func<bool> canSaveNow)
        {
            _baseConfig = current == null ? new Config() : current.Clone();
            _onSaved = onSaved;
            _canSaveNow = canSaveNow;
            _protector = new DpapiSecretProtector();

            Text = "声跃设置";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SettingsTheme.Body;
            BackColor = SettingsTheme.WindowBackground;
            ForeColor = SettingsTheme.TextPrimary;
            ClientSize = new Size(720, 620);
            MinimumSize = new Size(680, 560);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;

            var root = new TableLayoutPanel();
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.Dock = DockStyle.Fill;
            root.BackColor = SettingsTheme.WindowBackground;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // ---- 页头 ----
            var header = new TableLayoutPanel();
            header.ColumnCount = 1;
            header.RowCount = 2;
            header.AutoSize = true;
            header.Dock = DockStyle.Fill;
            header.BackColor = SettingsTheme.WindowBackground;
            header.Padding = new Padding(28, 22, 28, 10);

            var title = new Label();
            title.AutoSize = true;
            title.Font = SettingsTheme.Title;
            title.ForeColor = SettingsTheme.TextPrimary;
            title.Margin = new Padding(0, 0, 0, 5);
            title.Text = "声跃设置";
            header.Controls.Add(title, 0, 0);

            var intro = new Label();
            intro.AutoSize = true;
            intro.Font = SettingsTheme.Small;
            intro.ForeColor = SettingsTheme.TextSecondary;
            intro.Margin = new Padding(1, 0, 0, 0);
            intro.Text = "配置语音服务与输入方式，保存后从下一次录音生效。";
            header.Controls.Add(intro, 0, 1);
            root.Controls.Add(header, 0, 0);

            // ---- 分页标签 ----
            _tabs = new FlatTabs("常用", "高级");
            _tabs.Dock = DockStyle.Fill;
            _tabs.Padding = new Padding(28, 0, 28, 0);
            _tabs.SelectedChanged += delegate { SelectPage(_tabs.SelectedIndex); };
            root.Controls.Add(_tabs, 0, 1);

            // ---- 内容区（两页各自滚动，共用一个宿主格）----
            _commonScroll = CreateScrollPage();
            _advancedScroll = CreateScrollPage();
            var contentHost = new Panel();
            contentHost.Dock = DockStyle.Fill;
            contentHost.BackColor = SettingsTheme.WindowBackground;
            contentHost.Margin = new Padding(0);
            contentHost.Controls.Add(_commonScroll);
            contentHost.Controls.Add(_advancedScroll);
            root.Controls.Add(contentHost, 0, 2);

            var common = CreateFieldsTable();
            var advanced = CreateFieldsTable();
            _commonScroll.Controls.Add(common);
            _advancedScroll.Controls.Add(advanced);
            BuildCommonPage(common);
            BuildAdvancedPage(advanced);

            // ---- 底部操作区 ----
            var footer = new HairlineTopTable();
            footer.ColumnCount = 2;
            footer.RowCount = 1;
            footer.AutoSize = true;
            footer.Dock = DockStyle.Fill;
            footer.BackColor = SettingsTheme.WindowBackground;
            footer.Padding = new Padding(28, 15, 28, 18);
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _statusLabel = new Label();
            _statusLabel.AutoSize = true;
            _statusLabel.MaximumSize = new Size(420, 0);
            _statusLabel.Anchor = AnchorStyles.Left;
            _statusLabel.Font = SettingsTheme.Small;
            _statusLabel.ForeColor = SettingsTheme.TextSecondary;
            _statusLabel.AccessibleName = "设置状态";
            _statusLabel.Margin = new Padding(0);
            footer.Controls.Add(_statusLabel, 0, 0);

            var buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.WrapContents = false;
            buttons.AutoSize = true;
            buttons.Anchor = AnchorStyles.Right;
            buttons.BackColor = SettingsTheme.WindowBackground;
            buttons.Margin = new Padding(16, 0, 0, 0);

            _saveButton = CreatePrimaryButton("保存设置");
            _saveButton.Click += SaveClicked;
            _testButton = CreateSecondaryButton("测试连接");
            _testButton.Click += TestConnectionClicked;
            _cancelButton = CreateQuietButton("取消");
            _cancelButton.Click += delegate { Close(); };

            buttons.Controls.Add(_saveButton);
            buttons.Controls.Add(_testButton);
            buttons.Controls.Add(_cancelButton);
            footer.Controls.Add(buttons, 1, 0);
            root.Controls.Add(footer, 0, 3);

            AcceptButton = _saveButton;
            CancelButton = _cancelButton;
            SelectPage(0);
            LoadFromConfig(_baseConfig);
        }

        // 标题栏在 Windows 11 上染成与窗口同色，让页头与系统边框连成一块；
        // 旧系统调用失败，保持默认标题栏，无副作用。
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                Color c = SettingsTheme.WindowBackground;
                int colorref = c.B | (c.G << 8) | (c.R << 16);
                DwmSetWindowAttribute(Handle, 35, ref colorref, 4);
            }
            catch { }
        }

        private void BuildCommonPage(TableLayoutPanel common)
        {
            AddSectionHeader(common, "语音服务");

            _serviceBox = new ComboBox();
            _serviceBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _serviceBox.FlatStyle = FlatStyle.Flat;
            _serviceBox.Font = SettingsTheme.Body;
            _serviceBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _serviceBox.AccessibleName = "语音服务";
            _serviceBox.Items.Add(new ServiceOption("sse", "阶跃星辰 StepFun（已验证）"));
            _serviceBox.Items.Add(new ServiceOption("transcriptions", "OpenAI 兼容转写（高级，未验证）"));
            _serviceBox.SelectedIndexChanged += delegate
            {
                if (!_loading) SwitchService();
            };

            var restorePreset = CreateSecondaryButton("使用官方预设");
            restorePreset.Margin = new Padding(10, 0, 0, 0);
            restorePreset.Click += delegate { RestoreStepFunPreset(); };

            var servicePanel = new TableLayoutPanel();
            servicePanel.ColumnCount = 2;
            servicePanel.RowCount = 1;
            servicePanel.Dock = DockStyle.Fill;
            servicePanel.AutoSize = true;
            servicePanel.BackColor = SettingsTheme.WindowBackground;
            servicePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            servicePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            servicePanel.Margin = new Padding(0);
            servicePanel.Controls.Add(_serviceBox, 0, 0);
            servicePanel.Controls.Add(restorePreset, 1, 0);

            _serviceHint = AddField(common, "服务类型", servicePanel,
                "已验证的 StepFun SSE 路径。服务地址与 endpoint 默认锁定，模型仍可按官方说明调整。", true);

            _docsLink = new LinkLabel();
            _docsLink.AutoSize = true;
            _docsLink.Font = SettingsTheme.Small;
            _docsLink.LinkBehavior = LinkBehavior.HoverUnderline;
            _docsLink.LinkColor = SettingsTheme.Accent;
            _docsLink.ActiveLinkColor = SettingsTheme.AccentPressed;
            _docsLink.DisabledLinkColor = SettingsTheme.TextSecondary;
            _docsLink.Margin = new Padding(0, 0, 0, 16);
            _docsLink.Text = "打开 StepFun 官方语音接入文档";
            _docsLink.LinkClicked += delegate
            {
                if (SelectedApi() != "sse") return;
                try { Process.Start("https://platform.stepfun.com/docs/zh/step-plan/integrations/audio-api"); }
                catch { ShowError("无法打开浏览器，请访问 platform.stepfun.com/docs。", null); }
            };
            AddAlignedRow(common, _docsLink);

            _modelBox = BuildTextBox("模型");
            AddField(common, "模型", _modelBox, null, true);

            _apiKeyBox = BuildTextBox("API Key");
            _apiKeyBox.UseSystemPasswordChar = true;
            _apiKeyBox.Margin = new Padding(0, 0, 10, 0);

            _showApiKeyBox = new CheckBox();
            _showApiKeyBox.AutoSize = true;
            _showApiKeyBox.Font = SettingsTheme.Body;
            _showApiKeyBox.ForeColor = SettingsTheme.TextLabel;
            _showApiKeyBox.Anchor = AnchorStyles.Left;
            _showApiKeyBox.Margin = new Padding(0, 4, 0, 0);
            _showApiKeyBox.Text = "显示";
            _showApiKeyBox.AccessibleName = "显示 API Key";
            _showApiKeyBox.CheckedChanged += delegate
            {
                _apiKeyBox.UseSystemPasswordChar = !_showApiKeyBox.Checked;
            };

            var apiKeyPanel = new TableLayoutPanel();
            apiKeyPanel.ColumnCount = 2;
            apiKeyPanel.RowCount = 1;
            apiKeyPanel.Dock = DockStyle.Fill;
            apiKeyPanel.AutoSize = true;
            apiKeyPanel.BackColor = SettingsTheme.WindowBackground;
            apiKeyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            apiKeyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            apiKeyPanel.Margin = new Padding(0);
            apiKeyPanel.Controls.Add(_apiKeyBox, 0, 0);
            apiKeyPanel.Controls.Add(_showApiKeyBox, 1, 0);

            AddField(common, "API Key", apiKeyPanel,
                "密钥使用当前 Windows 账户加密保存，不会明文写入配置文件。", true);

            AddSectionHeader(common, "识别与输入");

            _languageBox = new ComboBox();
            _languageBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _languageBox.DropDownStyle = ComboBoxStyle.DropDown;
            _languageBox.FlatStyle = FlatStyle.Flat;
            _languageBox.Font = SettingsTheme.Body;
            _languageBox.AccessibleName = "识别语言";
            _languageBox.Items.Add("");
            _languageBox.Items.Add("zh");
            _languageBox.Items.Add("en");
            AddField(common, "识别语言", _languageBox, "留空表示由服务自动判断语种。", true);

            _autoInsertBox = new CheckBox();
            _autoInsertBox.AutoSize = true;
            _autoInsertBox.Font = SettingsTheme.Body;
            _autoInsertBox.ForeColor = SettingsTheme.TextPrimary;
            _autoInsertBox.Margin = new Padding(0, 3, 0, 0);
            _autoInsertBox.Text = "识别完成后直接写入光标处";
            _autoInsertBox.AccessibleDescription = "关闭时会先打开审阅窗口，默认关闭";
            AddField(common, "自动输入", _autoInsertBox,
                "默认关闭。开启后跳过审阅，但仍会检查目标窗口和敏感输入框。", false);

            _hotkeyBox = new ComboBox();
            _hotkeyBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _hotkeyBox.FlatStyle = FlatStyle.Flat;
            _hotkeyBox.Font = SettingsTheme.Body;
            _hotkeyBox.Width = 128;
            _hotkeyBox.Margin = new Padding(0, 0, 8, 0);
            _hotkeyBox.AccessibleName = "录音热键";
            _hotkeyBox.Items.Add(new ChoiceOption("RControl", "右 Ctrl"));
            _hotkeyBox.Items.Add(new ChoiceOption("RMenu", "右 Alt"));
            _hotkeyBox.Items.Add(new ChoiceOption("RShift", "右 Shift"));
            _hotkeyBox.Items.Add(new ChoiceOption("Capital", "Caps Lock"));

            _hotkeyModeBox = new ComboBox();
            _hotkeyModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _hotkeyModeBox.FlatStyle = FlatStyle.Flat;
            _hotkeyModeBox.Font = SettingsTheme.Body;
            _hotkeyModeBox.Width = 208;
            _hotkeyModeBox.Margin = new Padding(0);
            _hotkeyModeBox.AccessibleName = "录音触发方式";
            _hotkeyModeBox.Items.Add(new ChoiceOption("hold", "按住说话"));
            _hotkeyModeBox.Items.Add(new ChoiceOption("toggle", "按一次开始，再按一次结束"));

            var hotkeyPanel = new FlowLayoutPanel();
            hotkeyPanel.FlowDirection = FlowDirection.LeftToRight;
            hotkeyPanel.WrapContents = true;
            hotkeyPanel.AutoSize = true;
            hotkeyPanel.Dock = DockStyle.Fill;
            hotkeyPanel.BackColor = SettingsTheme.WindowBackground;
            hotkeyPanel.Margin = new Padding(0);
            hotkeyPanel.Controls.Add(_hotkeyBox);
            hotkeyPanel.Controls.Add(_hotkeyModeBox);
            AddField(common, "录音热键", hotkeyPanel,
                "Caps Lock 作为热键时会被程序吞掉，不会切换大小写状态。", true);
        }

        private void BuildAdvancedPage(TableLayoutPanel advanced)
        {
            AddSectionHeader(advanced, "服务地址与排障");

            AddNoteRow(advanced,
                "只有明确声明兼容 OpenAI /audio/transcriptions 的服务才能使用高级兼容模式。品牌名相同不代表协议兼容。",
                SettingsTheme.WarningText);

            _overrideEndpointBox = new CheckBox();
            _overrideEndpointBox.AutoSize = true;
            _overrideEndpointBox.Font = SettingsTheme.Body;
            _overrideEndpointBox.ForeColor = SettingsTheme.TextPrimary;
            _overrideEndpointBox.Margin = new Padding(0, 3, 0, 0);
            _overrideEndpointBox.Text = "排障时允许修改 StepFun 地址（仅按官方文档操作）";
            _overrideEndpointBox.CheckedChanged += delegate { UpdateServiceUi(); };
            AddField(advanced, "地址保护", _overrideEndpointBox, null, false);

            _baseUrlBox = BuildTextBox("Base URL");
            AddField(advanced, "Base URL", _baseUrlBox, null, true);

            _endpointBox = BuildTextBox("转写 endpoint");
            AddField(advanced, "Endpoint", _endpointBox, null, true);

            AddSectionHeader(advanced, "识别增强");

            _hotwordsBox = BuildTextBox("热词");
            _hotwordsBox.Multiline = true;
            _hotwordsBox.Height = 84;
            _hotwordsBox.ScrollBars = ScrollBars.Vertical;
            AddField(advanced, "热词", _hotwordsBox,
                "一行一个或用逗号分隔，例如 VoxLeap、Cursor、TypeScript。是否生效取决于服务能力。", true);

            AddSectionHeader(advanced, "端点检测与 AI 整理");

            _enableVadBox = new CheckBox();
            _enableVadBox.AutoSize = true;
            _enableVadBox.Font = SettingsTheme.Body;
            _enableVadBox.Text = "裁剪首尾静音（VAD）";
            AddField(advanced, "VAD", _enableVadBox, "只做端点能量检测，不会分离录音中间的键盘声或咳嗽声。", false);

            _vadThreshold = BuildNumeric(50, 10000, "VAD RMS 阈值");
            AddField(advanced, "VAD 阈值", _vadThreshold, "默认 450；环境噪声较大时可适当提高。", false);
            _vadPaddingMs = BuildNumeric(0, 2000, "VAD 前后保留毫秒");
            AddField(advanced, "VAD 缓冲", WrapWithSuffix(_vadPaddingMs, "毫秒"), null, false);

            _autoStopBox = new CheckBox();
            _autoStopBox.AutoSize = true;
            _autoStopBox.Font = SettingsTheme.Body;
            _autoStopBox.Text = "说话停下后自动结束录音";
            AddField(advanced, "静音自动停止", _autoStopBox,
                "默认关闭。开启后，连续静音超过下方时长即自动结束录音并开始识别，说完话不必再按着热键。", false);

            _autoStopSilenceMs = BuildNumeric(300, 10000, "静音多久后自动结束");
            _autoStopSilenceMs.Enabled = false;
            _autoStopBox.CheckedChanged += delegate { _autoStopSilenceMs.Enabled = _autoStopBox.Checked; };
            AddField(advanced, "静音时长", WrapWithSuffix(_autoStopSilenceMs, "毫秒"), null, false);

            _streamingBox = new CheckBox();
            _streamingBox.AutoSize = true;
            _streamingBox.Font = SettingsTheme.Body;
            _streamingBox.Text = "边说边送（说话期间就分段上传）";
            AddField(advanced, "边说边送", _streamingBox,
                "默认关闭。开启后说话期间就把已完成的分段送去识别，松手时只剩最后一段要传，"
                + "因此松手后的等待大幅缩短。任一段失败会自动回退到整段上传，不会产生残缺文本。", false);

            _aiOrganizeBox = new CheckBox();
            _aiOrganizeBox.AutoSize = true;
            _aiOrganizeBox.Font = SettingsTheme.Body;
            _aiOrganizeBox.Text = "启用 AI 整理";
            _aiOrganizeBox.CheckedChanged += delegate { UpdateOrganizerUi(); };
            AddField(advanced, "AI 整理", _aiOrganizeBox, "关闭时不会调用 LLM；整理失败或高风险变化会自动回退原文。", false);

            _organizerBaseUrlBox = BuildTextBox("整理 Base URL");
            AddField(advanced, "整理地址", _organizerBaseUrlBox, null, true);
            _organizerEndpointBox = BuildTextBox("整理 endpoint");
            AddField(advanced, "整理 Endpoint", _organizerEndpointBox, null, true);
            _organizerModelBox = BuildTextBox("整理模型");
            AddField(advanced, "整理模型", _organizerModelBox, null, true);
            _organizerApiKeyBox = BuildTextBox("整理 API Key");
            _organizerApiKeyBox.UseSystemPasswordChar = true;
            AddField(advanced, "整理 API Key", _organizerApiKeyBox, "使用当前 Windows 账户 DPAPI 加密保存。", true);

            AddSectionHeader(advanced, "时长与超时");

            _maxRecordSeconds = BuildNumeric(0, 3600, "录音上限秒数");
            AddField(advanced, "录音上限", WrapWithSuffix(_maxRecordSeconds, "秒，0 表示不限制"), null, false);

            _timeoutSeconds = BuildNumeric(1, 300, "请求超时秒数");
            AddField(advanced, "请求超时", WrapWithSuffix(_timeoutSeconds, "秒"), null, false);
        }

        private static Panel CreateScrollPage()
        {
            var page = new Panel();
            page.Dock = DockStyle.Fill;
            page.AutoScroll = true;
            page.BackColor = SettingsTheme.WindowBackground;
            page.Padding = new Padding(28, 16, 24, 16);
            page.Visible = false;
            return page;
        }

        private static TableLayoutPanel CreateFieldsTable()
        {
            var table = new TableLayoutPanel();
            table.ColumnCount = 2;
            table.RowCount = 0;
            table.AutoSize = true;
            table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            table.Dock = DockStyle.Top;
            table.BackColor = SettingsTheme.WindowBackground;
            table.Margin = new Padding(0);
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SettingsTheme.LabelColumnWidth));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            return table;
        }

        private void SelectPage(int index)
        {
            bool common = index == 0;
            _commonScroll.Visible = common;
            _advancedScroll.Visible = !common;
            try
            {
                _commonScroll.AutoScrollPosition = Point.Empty;
                _advancedScroll.AutoScrollPosition = Point.Empty;
            }
            catch { }
        }

        private static TextBox BuildTextBox(string accessibleName)
        {
            var box = new TextBox();
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = SettingsTheme.Surface;
            box.ForeColor = SettingsTheme.TextPrimary;
            box.Font = SettingsTheme.Body;
            box.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            box.Margin = new Padding(0);
            box.AccessibleName = accessibleName;
            return box;
        }

        private static NumericUpDown BuildNumeric(decimal min, decimal max, string accessibleName)
        {
            var numeric = new NumericUpDown();
            numeric.Minimum = min;
            numeric.Maximum = max;
            numeric.BorderStyle = BorderStyle.FixedSingle;
            numeric.BackColor = SettingsTheme.Surface;
            numeric.ForeColor = SettingsTheme.TextPrimary;
            numeric.Font = SettingsTheme.Body;
            numeric.Anchor = AnchorStyles.Left;
            numeric.Width = 104;
            numeric.Margin = new Padding(0);
            numeric.ThousandsSeparator = false;
            numeric.AccessibleName = accessibleName;
            return numeric;
        }

        private static Control WrapWithSuffix(Control control, string suffix)
        {
            var panel = new FlowLayoutPanel();
            panel.FlowDirection = FlowDirection.LeftToRight;
            panel.WrapContents = false;
            panel.AutoSize = true;
            panel.Dock = DockStyle.Fill;
            panel.BackColor = SettingsTheme.WindowBackground;
            panel.Margin = new Padding(0);
            panel.Controls.Add(control);

            var label = new Label();
            label.AutoSize = true;
            label.Font = SettingsTheme.Small;
            label.ForeColor = SettingsTheme.TextSecondary;
            label.Margin = new Padding(10, 5, 0, 0);
            label.Text = suffix;
            panel.Controls.Add(label);
            return panel;
        }

        private static Button CreateFlatButton(string text)
        {
            var button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.FlatStyle = FlatStyle.Flat;
            button.Font = SettingsTheme.Body;
            button.Padding = new Padding(12, 5, 12, 5);
            button.Margin = new Padding(0);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private static Button CreatePrimaryButton(string text)
        {
            Button button = CreateFlatButton(text);
            button.BackColor = SettingsTheme.Accent;
            button.ForeColor = SettingsTheme.TextOnAccent;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = SettingsTheme.AccentHover;
            button.FlatAppearance.MouseDownBackColor = SettingsTheme.AccentPressed;
            return button;
        }

        private static Button CreateSecondaryButton(string text)
        {
            Button button = CreateFlatButton(text);
            button.BackColor = SettingsTheme.Surface;
            button.ForeColor = SettingsTheme.TextPrimary;
            button.FlatAppearance.BorderColor = SettingsTheme.Border;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = SettingsTheme.AccentTint;
            button.FlatAppearance.MouseDownBackColor = SettingsTheme.SurfaceHover;
            return button;
        }

        private static Button CreateQuietButton(string text)
        {
            Button button = CreateFlatButton(text);
            button.BackColor = SettingsTheme.WindowBackground;
            button.ForeColor = SettingsTheme.TextSecondary;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = SettingsTheme.SurfaceHover;
            button.FlatAppearance.MouseDownBackColor = SettingsTheme.Hairline;
            return button;
        }

        private static void AddSectionHeader(TableLayoutPanel table, string text)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var header = new Label();
            header.Text = text;
            header.AutoSize = true;
            header.Font = SettingsTheme.Strong;
            header.ForeColor = SettingsTheme.TextPrimary;
            header.Margin = new Padding(0, 4, 0, 12);
            table.Controls.Add(header, 0, row);
            table.SetColumnSpan(header, 2);
        }

        // 字段行：左侧标签 + 右侧控件，辅助说明与控件左对齐（而不是与标签对齐），
        // 形成一条稳定的视觉轴。
        private static Label AddField(TableLayoutPanel table, string labelText, Control control, string helperText, bool stretch)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var label = new Label();
            label.Text = labelText;
            label.AutoSize = true;
            label.Font = SettingsTheme.Body;
            label.ForeColor = SettingsTheme.TextLabel;
            label.Margin = new Padding(0, 6, 16, 0);
            label.Anchor = AnchorStyles.Left | AnchorStyles.Top;

            control.Anchor = stretch
                ? AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top
                : AnchorStyles.Left | AnchorStyles.Top;
            // 无辅助说明时，行距由控件自身的下边距承担；有辅助说明时交给说明行。
            control.Margin = new Padding(control.Margin.Left, control.Margin.Top, control.Margin.Right,
                helperText == null ? 16 : 0);

            table.Controls.Add(label, 0, row);
            table.Controls.Add(control, 1, row);

            if (helperText == null) return null;
            return AddAlignedRow(table, CreateHelperLabel(helperText));
        }

        // 辅助说明 / 链接 / 提示：占满控件列，与控件同一条左边线。
        private static Label AddAlignedRow(TableLayoutPanel table, Control control)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(0, control.Margin.Top == 0 ? 7 : control.Margin.Top, 0, 16);
            control.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            table.Controls.Add(control, 1, row);
            return control as Label;
        }

        private static Label CreateHelperLabel(string text)
        {
            var label = new Label();
            label.AutoSize = true;
            label.MaximumSize = new Size(SettingsTheme.HelperMaxWidth, 0);
            label.Font = SettingsTheme.Small;
            label.ForeColor = SettingsTheme.TextSecondary;
            label.Margin = new Padding(0, 7, 0, 16);
            label.Text = text;
            return label;
        }

        private static void AddNoteRow(TableLayoutPanel table, string text, Color color)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var note = new Label();
            note.AutoSize = true;
            note.MaximumSize = new Size(SettingsTheme.HelperMaxWidth + SettingsTheme.LabelColumnWidth, 0);
            note.Font = SettingsTheme.Small;
            note.ForeColor = color;
            note.Margin = new Padding(0, 0, 0, 16);
            note.Text = text;
            table.Controls.Add(note, 0, row);
            table.SetColumnSpan(note, 2);
        }

        private void LoadFromConfig(Config cfg)
        {
            _loading = true;
            _baseUrlBox.Text = cfg.BaseUrl;
            _endpointBox.Text = cfg.Endpoint;
            _modelBox.Text = cfg.Model;
            _apiKeyBox.Text = cfg.ApiKey;
            _languageBox.Text = cfg.Language;
            SelectChoice(_hotkeyBox, cfg.Hotkey);
            SelectChoice(_hotkeyModeBox, cfg.HotkeyMode);
            _hotwordsBox.Text = (cfg.Hotwords ?? "").Replace(",", Environment.NewLine);
            _autoInsertBox.Checked = cfg.AutoInsert;
            _maxRecordSeconds.Value = Clamp(_maxRecordSeconds, cfg.MaxRecordMs <= 0 ? 0m : (decimal)((cfg.MaxRecordMs + 999) / 1000));
            _timeoutSeconds.Value = Clamp(_timeoutSeconds, (decimal)((cfg.RequestTimeoutMs + 999) / 1000));
            _enableVadBox.Checked = cfg.EnableVad;
            _vadThreshold.Value = Clamp(_vadThreshold, cfg.VadThreshold);
            _vadPaddingMs.Value = Clamp(_vadPaddingMs, cfg.VadPaddingMs);
            _autoStopSilenceMs.Value = Clamp(_autoStopSilenceMs, cfg.AutoStopSilenceMs);
            _autoStopBox.Checked = cfg.AutoStopOnSilence;
            _streamingBox.Checked = cfg.StreamingSegments;
            _aiOrganizeBox.Checked = cfg.AiOrganize;
            _organizerBaseUrlBox.Text = cfg.OrganizerBaseUrl;
            _organizerEndpointBox.Text = cfg.OrganizerEndpoint;
            _organizerModelBox.Text = cfg.OrganizerModel;
            _organizerApiKeyBox.Text = cfg.OrganizerApiKey;

            if (cfg.Api == "transcriptions")
            {
                _customBaseUrl = cfg.BaseUrl;
                _customEndpoint = cfg.Endpoint;
                _customModel = cfg.Model;
            }
            SelectService(cfg.Api);
            _currentApi = SelectedApi();
            _loading = false;
            UpdateServiceUi();
            UpdateOrganizerUi();

            if (!string.IsNullOrEmpty(cfg.ParseIssue))
            {
                _statusLabel.ForeColor = SettingsTheme.WarningText;
                _statusLabel.Text = cfg.ParseIssue;
            }
        }

        private static decimal Clamp(NumericUpDown numeric, decimal value)
        {
            return Math.Min(numeric.Maximum, Math.Max(numeric.Minimum, value));
        }

        private void SelectService(string api)
        {
            for (int i = 0; i < _serviceBox.Items.Count; i++)
            {
                ServiceOption option = _serviceBox.Items[i] as ServiceOption;
                if (option != null && option.Api == api)
                {
                    _serviceBox.SelectedIndex = i;
                    return;
                }
            }
            _serviceBox.SelectedIndex = 0;
        }

        private static void SelectChoice(ComboBox box, string value)
        {
            for (int i = 0; i < box.Items.Count; i++)
            {
                ChoiceOption option = box.Items[i] as ChoiceOption;
                if (option != null && option.Value == value)
                {
                    box.SelectedIndex = i;
                    return;
                }
            }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
        }

        private static string SelectedChoice(ComboBox box, string fallback)
        {
            ChoiceOption option = box.SelectedItem as ChoiceOption;
            return option == null ? fallback : option.Value;
        }

        private string SelectedApi()
        {
            ServiceOption option = _serviceBox.SelectedItem as ServiceOption;
            return option == null ? "sse" : option.Api;
        }

        private void SwitchService()
        {
            string nextApi = SelectedApi();
            if (nextApi == _currentApi)
            {
                UpdateServiceUi();
                return;
            }

            if (nextApi == "transcriptions")
            {
                DialogResult confirm = MessageBox.Show(
                    this,
                    "高级兼容模式尚未验证，并不适用于所有供应商。只有服务商明确兼容 OpenAI /audio/transcriptions 时才应继续。",
                    "启用高级兼容模式",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning);
                if (confirm != DialogResult.OK)
                {
                    _loading = true;
                    SelectService(_currentApi);
                    _loading = false;
                    return;
                }
            }

            if (_currentApi == "transcriptions")
            {
                _customBaseUrl = _baseUrlBox.Text;
                _customEndpoint = _endpointBox.Text;
                _customModel = _modelBox.Text;
            }

            if (nextApi == "sse")
            {
                _baseUrlBox.Text = Config.StepFunBaseUrlDefault;
                _endpointBox.Text = Config.StepFunEndpointDefault;
                _modelBox.Text = Config.StepFunModelDefault;
            }
            else
            {
                _baseUrlBox.Text = _customBaseUrl;
                _endpointBox.Text = string.IsNullOrEmpty(_customEndpoint) ? "/audio/transcriptions" : _customEndpoint;
                _modelBox.Text = _customModel;
            }

            // 密钥不能跨供应商沿用，避免把一家服务的凭据发送给另一端点。
            _apiKeyBox.Clear();
            _showApiKeyBox.Checked = false;
            _overrideEndpointBox.Checked = false;
            _statusLabel.ForeColor = SettingsTheme.WarningText;
            _statusLabel.Text = "服务已切换。请填写该服务自己的 API Key。";
            _currentApi = nextApi;
            UpdateServiceUi();
        }

        private void RestoreStepFunPreset()
        {
            bool switching = SelectedApi() != "sse";
            _loading = true;
            SelectService("sse");
            _loading = false;
            _currentApi = "sse";
            _overrideEndpointBox.Checked = false;
            _baseUrlBox.Text = Config.StepFunBaseUrlDefault;
            _endpointBox.Text = Config.StepFunEndpointDefault;
            _modelBox.Text = Config.StepFunModelDefault;
            if (switching)
            {
                _apiKeyBox.Clear();
                _showApiKeyBox.Checked = false;
                _statusLabel.ForeColor = SettingsTheme.WarningText;
                _statusLabel.Text = "已恢复 StepFun 官方预设。请填写对应的 API Key。";
            }
            else
            {
                _statusLabel.ForeColor = SettingsTheme.SuccessText;
                _statusLabel.Text = "已恢复 StepFun 官方服务地址、endpoint 和模型。";
            }
            UpdateServiceUi();
        }

        private void UpdateServiceUi()
        {
            bool stepFun = SelectedApi() == "sse";
            bool lockOfficialAddress = stepFun && !_overrideEndpointBox.Checked;
            _overrideEndpointBox.Enabled = stepFun;
            _baseUrlBox.ReadOnly = lockOfficialAddress;
            _endpointBox.ReadOnly = lockOfficialAddress;
            _baseUrlBox.BackColor = lockOfficialAddress ? SettingsTheme.SurfaceLocked : SettingsTheme.Surface;
            _endpointBox.BackColor = lockOfficialAddress ? SettingsTheme.SurfaceLocked : SettingsTheme.Surface;
            _serviceHint.Text = stepFun
                ? (lockOfficialAddress
                    ? "已验证的 StepFun SSE 路径。服务地址与 endpoint 默认锁定，模型仍可按官方说明调整。"
                    : "已开启排障覆盖。请只按 StepFun 官方文档修改地址，随时可恢复官方预设。")
                : "高级兼容入口尚未验证。只有接口协议、鉴权和响应字段都兼容时才能使用。";
            _docsLink.Enabled = stepFun;
            _docsLink.Text = stepFun
                ? "打开 StepFun 官方语音接入文档"
                : "高级兼容模式没有统一文档，请查阅所选服务商的官方 API 文档";
        }

        private void UpdateOrganizerUi()
        {
            bool enabled = _aiOrganizeBox != null && _aiOrganizeBox.Checked;
            if (_organizerBaseUrlBox != null) _organizerBaseUrlBox.Enabled = enabled;
            if (_organizerEndpointBox != null) _organizerEndpointBox.Enabled = enabled;
            if (_organizerModelBox != null) _organizerModelBox.Enabled = enabled;
            if (_organizerApiKeyBox != null) _organizerApiKeyBox.Enabled = enabled;
        }

        private Config ReadConfigFromForm()
        {
            Config updated = _baseConfig.Clone();
            updated.Api = SelectedApi();
            updated.BaseUrl = _baseUrlBox.Text;
            updated.Endpoint = _endpointBox.Text;
            updated.Model = _modelBox.Text;
            updated.ApiKey = _apiKeyBox.Text;
            updated.Language = _languageBox.Text;
            updated.Hotkey = SelectedChoice(_hotkeyBox, "RControl");
            updated.HotkeyMode = SelectedChoice(_hotkeyModeBox, "hold");
            updated.Hotwords = _hotwordsBox.Text;
            updated.AutoInsert = _autoInsertBox.Checked;
            updated.MaxRecordMs = (int)_maxRecordSeconds.Value * 1000;
            updated.RequestTimeoutMs = (int)_timeoutSeconds.Value * 1000;
            updated.EnableVad = _enableVadBox.Checked;
            updated.VadThreshold = (int)_vadThreshold.Value;
            updated.VadPaddingMs = (int)_vadPaddingMs.Value;
            updated.AutoStopOnSilence = _autoStopBox.Checked;
            updated.AutoStopSilenceMs = (int)_autoStopSilenceMs.Value;
            updated.StreamingSegments = _streamingBox.Checked;
            updated.AiOrganize = _aiOrganizeBox.Checked;
            updated.OrganizerBaseUrl = _organizerBaseUrlBox.Text;
            updated.OrganizerEndpoint = _organizerEndpointBox.Text;
            updated.OrganizerModel = _organizerModelBox.Text;
            updated.OrganizerApiKey = _organizerApiKeyBox.Text;
            updated.ParseIssue = "";
            updated.LoadedLegacyPlaintextKey = false;
            return ConfigValidator.Normalize(updated);
        }

        private void SaveClicked(object sender, EventArgs e)
        {
            if (_testing)
            {
                ShowError("连接测试尚未结束，请稍候。", null);
                return;
            }
            if (_canSaveNow != null && !_canSaveNow())
            {
                ShowError("当前会话尚未结束，暂时不能保存设置。", null);
                return;
            }

            Config updated = ReadConfigFromForm();
            ConfigValidationResult validation = ConfigValidator.Validate(updated);
            if (!validation.Ok)
            {
                ShowError(validation.ToDisplayText(), GetFirstInvalidControl(updated));
                return;
            }

            try
            {
                ConfigStore.SaveToPath(updated, _protector);
                if (_onSaved != null) _onSaved(updated);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (CryptographicException)
            {
                ShowError("Windows 无法安全保护 API Key，原设置没有修改。请确认当前账户可用后重试。", _apiKeyBox);
            }
            catch (Exception ex)
            {
                ShowError("设置未保存：" + ex.Message, null);
            }
        }

        private void TestConnectionClicked(object sender, EventArgs e)
        {
            if (_testing) return;
            Config candidate = ReadConfigFromForm();
            ConfigValidationResult validation = ConfigValidator.Validate(candidate);
            if (!validation.Ok)
            {
                ShowError(validation.ToDisplayText(), GetFirstInvalidControl(candidate));
                return;
            }

            _testing = true;
            _testButton.Enabled = false;
            _saveButton.Enabled = false;
            // 审查 P2：测试期间一并禁用取消，避免关窗后后台线程回调已释放窗体、或重开后并发多个测试。
            _cancelButton.Enabled = false;
            _statusLabel.ForeColor = SettingsTheme.Accent;
            _statusLabel.Text = "正在发送 0.25 秒静音样本测试连接，不会采集麦克风…";

            Thread worker = new Thread(delegate()
            {
                AsrResult result;
                string wav = Path.Combine(Path.GetTempPath(), "voxleap_connection_test_" + Guid.NewGuid().ToString("N") + ".wav");
                try
                {
                    DiagnosticAudio.WriteSilentWav(wav, 250);
                    result = AsrClient.Transcribe(candidate, wav);
                }
                catch (Exception ex)
                {
                    result = new AsrResult();
                    result.Error = ex.Message;
                }
                finally
                {
                    try { if (File.Exists(wav)) File.Delete(wav); } catch { }
                }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _testing = false;
                        _testButton.Enabled = true;
                        _saveButton.Enabled = true;
                        _cancelButton.Enabled = true;
                        bool success = result.Ok;
                        _statusLabel.ForeColor = success
                            ? SettingsTheme.SuccessText
                            : SettingsTheme.ErrorText;
                        _statusLabel.Text = ConnectionDiagnosis.Describe(result, candidate);
                    });
                }
                catch { }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void ShowError(string message, Control focusTarget)
        {
            _statusLabel.ForeColor = SettingsTheme.ErrorText;
            _statusLabel.Text = message;
            if (focusTarget != null) focusTarget.Focus();
        }

        private Control GetFirstInvalidControl(Config cfg)
        {
            Uri uri;
            if (cfg.BaseUrl.Length == 0 || !Uri.TryCreate(cfg.BaseUrl, UriKind.Absolute, out uri))
            {
                return _baseUrlBox;
            }
            bool loopbackHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
            if (uri.Scheme != Uri.UriSchemeHttps && !loopbackHttp) return _baseUrlBox;
            if (cfg.Endpoint.Length == 0 || !cfg.Endpoint.StartsWith("/")) return _endpointBox;
            if (cfg.Model.Length == 0) return _modelBox;
            if (!cfg.HasKey) return _apiKeyBox;
            if (cfg.Hotkey != "RControl" && cfg.Hotkey != "RMenu" && cfg.Hotkey != "RShift" && cfg.Hotkey != "Capital") return _hotkeyBox;
            if (cfg.HotkeyMode != "hold" && cfg.HotkeyMode != "toggle") return _hotkeyModeBox;
            if (cfg.RequestTimeoutMs < 1000 || cfg.RequestTimeoutMs > 300000) return _timeoutSeconds;
            if (cfg.MaxRecordMs < 0 || cfg.MaxRecordMs > 3600000) return _maxRecordSeconds;
            if (cfg.Language.Length > 32) return _languageBox;
            if (cfg.Hotwords.Length > 500) return _hotwordsBox;
            return null;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!_baseConfig.HasKey) _apiKeyBox.Focus();
        }

        // 对抗审查 P2：连接测试期间禁止通过 X/Alt+F4 关窗，否则后台 HTTP 线程继续跑完、
        // 且关闭消息队列下 BeginInvoke 回调静默丢弃，测试结果无处展示，还会耗尽配置的 API 配额。
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_testing)
            {
                e.Cancel = true;
                ShowError("连接测试尚未结束，请稍候。", null);
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
