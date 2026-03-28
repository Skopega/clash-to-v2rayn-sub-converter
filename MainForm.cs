using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class CenteredTextBox : TextBox
{
    private const int EM_SETRECT = 0xB3;
    private int _desiredHeight = 19;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wp, ref RECT lp);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public CenteredTextBox()
    {
        Multiline = true;
        WordWrap = false;
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        base.SetBoundsCore(x, y, width, _desiredHeight, specified);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTextRect();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (IsHandleCreated) ApplyTextRect();
    }

    private void ApplyTextRect()
    {
        var topPad = (_desiredHeight - Font.Height) / 2;
        var rect = new RECT { Left = 4, Top = topPad, Right = Width - 4, Bottom = _desiredHeight - topPad };
        SendMessage(Handle, EM_SETRECT, 0, ref rect);
    }
}

internal sealed class ScrollbarlessFlowPanel : FlowLayoutPanel
{
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style &= ~0x00200000; // WS_VSCROLL
            cp.Style &= ~0x00100000; // WS_HSCROLL
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0083)
            ShowScrollBar(Handle, 3, false); // SB_BOTH
        base.WndProc(ref m);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
}

internal sealed class MainForm : Form
{
    private static readonly Color BgDark = Color.FromArgb(0x32, 0x32, 0x32);
    private static readonly Color BgPanel = Color.FromArgb(0x3A, 0x3A, 0x3A);
    private static readonly Color BgInput = Color.FromArgb(0x3E, 0x3E, 0x3E);
    private static readonly Color BgButton = Color.FromArgb(0x46, 0x46, 0x46);
    private static readonly Color BgButtonHover = Color.FromArgb(0x52, 0x52, 0x52);
    private static readonly Color BgRow = Color.FromArgb(0x38, 0x38, 0x38);
    private static readonly Color BgRowAlt = Color.FromArgb(0x3E, 0x3E, 0x3E);
    private static readonly Color Fg = Color.FromArgb(0xCF, 0xCF, 0xCF);
    private static readonly Color FgDim = Color.FromArgb(0x90, 0x90, 0x90);
    private static readonly Color BorderColor = Color.FromArgb(0x50, 0x50, 0x50);

    private static readonly Font MainFont = new("Verdana", 9f);
    private static readonly Font MainFontBold = new("Verdana", 9f, FontStyle.Bold);
    private static readonly Font MonoFont = new("Consolas", 8.5f);
    private static readonly Font SmallFont = new("Verdana", 7.5f);
    private static readonly Font HeaderFont = new("Verdana", 10f, FontStyle.Bold);

    private readonly SubscriptionParser _parser = new();
    private readonly TextBox _urlTextBox;
    private readonly Button _convertButton;
    private readonly Button _copyAllButton;
    private readonly Button _clearHwidButton;
    private readonly ScrollbarlessFlowPanel _resultsPanel;
    private readonly Label _statusBarLabel;
    private IReadOnlyList<string> _currentNodes = Array.Empty<string>();

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int color = 0x00323232;
        DwmSetWindowAttribute(Handle, 35, ref color, sizeof(int));
    }

    public MainForm()
    {
        Text = "Legacy Subscription Converter";
        Size = new Size(540, 650);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        if (File.Exists(iconPath))
            Icon = new Icon(iconPath);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgDark;
        ForeColor = Fg;
        Font = MainFont;
        DoubleBuffered = true;

        var inputPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 95,
            BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A),
            Padding = new Padding(12, 10, 12, 10)
        };

        var urlLabel = new Label
        {
            Text = "Paste provider link",
            Font = MainFont,
            ForeColor = Fg,
            AutoSize = true,
            Location = new Point(9, 8)
        };

        _urlTextBox = new CenteredTextBox
        {
            Location = new Point(12, 30),
            Font = MainFont,
            BackColor = BgInput,
            ForeColor = Fg,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "https://provider.example/subscription"
        };
        _urlTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { ConvertButton_Click(null, EventArgs.Empty); e.SuppressKeyPress = true; }
        };

        _convertButton = CreateStyledButton("Convert");
        _convertButton.Location = new Point(12, 58);
        _convertButton.Click += ConvertButton_Click;

        _copyAllButton = CreateStyledButton("Copy All");
        _copyAllButton.Enabled = false;
        _copyAllButton.Click += CopyAllButton_Click;

        _clearHwidButton = CreateStyledButton("Clear HWID");
        _clearHwidButton.Click += async (_, _) =>
        {
            SubscriptionParser.ClearHwid();
            _clearHwidButton.Text = "Cleared!";
            await Task.Delay(1500);
            if (!_clearHwidButton.IsDisposed)
                _clearHwidButton.Text = "Clear HWID";
        };

        var hwidPopup = new Panel
        {
            Size = new Size(320, 220),
            BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A),
            BorderStyle = BorderStyle.None,
            Visible = false,
            Padding = new Padding(8)
        };
        hwidPopup.Paint += (_, args) =>
        {
            using var pen = new Pen(BorderColor);
            args.Graphics.DrawRectangle(pen, 0, 0, hwidPopup.Width - 1, hwidPopup.Height - 1);
            TextRenderer.DrawText(args.Graphics,
                "New subscriptions require HWID to send you config data. " +
                "Legacy subscription format didn't.\n\n" +
                "On the first run, program creates a fake HWID (not an actual HWID, " +
                "it's basically a random value) to mimic other clients like Happ " +
                "and stores it in hwid.txt to reuse later.\n\n" +
                "If you want to update your subscription, it's better to reuse HWID " +
                "or else you will drain your device limit on a given subscription " +
                "(you can remove devices from a provider's dashboard or by asking them).\n\n" +
                "You can clear HWID by pressing the button. On the next conversion, " +
                "program will create a new HWID. And it will seem like you're getting " +
                "subscription data from a new device/client.",
                SmallFont,
                new Rectangle(8, 8, hwidPopup.Width - 16, hwidPopup.Height - 16),
                Fg, TextFormatFlags.WordBreak | TextFormatFlags.Left);
        };
        Controls.Add(hwidPopup);
        hwidPopup.BringToFront();

        _clearHwidButton.MouseEnter += (_, _) =>
        {
            var btnScreen = _clearHwidButton.PointToScreen(Point.Empty);
            var formPt = PointToClient(btnScreen);
            var x = formPt.X - hwidPopup.Width + _clearHwidButton.Width;
            var y = formPt.Y + _clearHwidButton.Height + 4;
            if (x < 4) x = 4;
            hwidPopup.Location = new Point(x, y);
            hwidPopup.Visible = true;
        };
        _clearHwidButton.MouseLeave += (_, _) =>
        {
            hwidPopup.Visible = false;
        };

        inputPanel.Controls.Add(urlLabel);
        inputPanel.Controls.Add(_urlTextBox);
        inputPanel.Controls.Add(_convertButton);
        inputPanel.Controls.Add(_copyAllButton);
        inputPanel.Controls.Add(_clearHwidButton);

        inputPanel.Resize += (_, _) =>
        {
            _urlTextBox.Width = inputPanel.ClientSize.Width - 24;
            var btnY = 58;
            _convertButton.Location = new Point(12, btnY);
            _copyAllButton.Location = new Point(_convertButton.Right + 6, btnY);
            _clearHwidButton.Location = new Point(inputPanel.ClientSize.Width - _clearHwidButton.Width - 12, btnY);
        };

        _resultsPanel = new ScrollbarlessFlowPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = BgDark,
            Padding = new Padding(4)
        };

        var statusBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            BackColor = Color.FromArgb(0x2A, 0x2A, 0x2A)
        };

        _statusBarLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "  0 configs found",
            Font = SmallFont,
            ForeColor = FgDim,
            TextAlign = ContentAlignment.MiddleLeft
        };
        statusBar.Controls.Add(_statusBarLabel);

        Controls.Add(_resultsPanel);
        Controls.Add(inputPanel);
        Controls.Add(statusBar);

        Shown += (_, _) => ActiveControl = _convertButton;
    }

    private static Button CreateStyledButton(string text)
    {
        var btn = new Button
        {
            Text = text,
            Font = MainFont,
            ForeColor = Fg,
            BackColor = BgButton,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(100, 26),
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter
        };
        btn.FlatAppearance.BorderColor = BorderColor;
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.MouseOverBackColor = BgButtonHover;
        return btn;
    }

    private async void ConvertButton_Click(object? sender, EventArgs e)
    {
        var providerUrl = _urlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(providerUrl))
        {
            SetStatus("Paste a provider link first.");
            return;
        }

        ToggleLoadingState(true);
        ClearResults();
        SetStatus("Trying multiple strategies to fetch subscription...");

        try
        {
            var nodes = await _parser.GetVlessNodesAsync(providerUrl, null);
            RenderResults(nodes);
            SetStatus($"{nodes.Count} configs found");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed: {ex.Message}");
        }
        finally
        {
            ToggleLoadingState(false);
        }
    }

    private async void CopyAllButton_Click(object? sender, EventArgs e)
    {
        if (_currentNodes.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _currentNodes));
            _copyAllButton.Text = "Copied!";
        }
        catch
        {
            _copyAllButton.Text = "Error";
        }
        await Task.Delay(1500);
        if (!_copyAllButton.IsDisposed)
            _copyAllButton.Text = "Copy All";
    }

    private void RenderResults(IReadOnlyList<string> nodes)
    {
        _currentNodes = nodes;
        _copyAllButton.Enabled = nodes.Count > 0;
        _resultsPanel.SuspendLayout();
        try
        {
            foreach (var (node, index) in nodes.Select((value, i) => (value, i + 1)))
            {
                var rowPanel = new Panel
                {
                    Width = _resultsPanel.ClientSize.Width - 8,
                    Height = 58,
                    BackColor = BgDark,
                    Margin = new Padding(0, 2, 0, 0),
                    Padding = new Padding(10, 6, 10, 6)
                };

                var hashPos = node.IndexOf('#');
                var displayName = hashPos >= 0
                    ? Uri.UnescapeDataString(node[(hashPos + 1)..]).Trim()
                    : "";
                if (string.IsNullOrEmpty(displayName))
                    displayName = $"Config {index}";

                var nameLabel = new Label
                {
                    Text = displayName,
                    Font = MainFontBold,
                    ForeColor = Fg,
                    AutoSize = true,
                    Location = new Point(10, 6)
                };

                var copyButton = CreateStyledButton("Copy");
                copyButton.Size = new Size(65, 22);
                copyButton.Font = SmallFont;
                var capturedNode = node;
                copyButton.Click += async (_, _) =>
                {
                    try
                    {
                        Clipboard.SetText(capturedNode);
                        copyButton.Text = "Copied!";
                    }
                    catch
                    {
                        copyButton.Text = "Error";
                    }
                    await Task.Delay(1500);
                    if (!copyButton.IsDisposed)
                        copyButton.Text = "Copy";
                };

                var textBox = new TextBox
                {
                    ReadOnly = true,
                    Text = node,
                    Font = MonoFont,
                    BackColor = BgInput,
                    ForeColor = Fg,
                    BorderStyle = BorderStyle.FixedSingle,
                    Location = new Point(10, 30),
                    Height = 22
                };

                rowPanel.Controls.Add(nameLabel);
                rowPanel.Controls.Add(copyButton);
                rowPanel.Controls.Add(textBox);

                rowPanel.Resize += (_, _) =>
                {
                    textBox.Width = rowPanel.ClientSize.Width - 20;
                    copyButton.Location = new Point(rowPanel.ClientSize.Width - copyButton.Width - 10, 4);
                };

                textBox.Width = rowPanel.ClientSize.Width - 20;
                copyButton.Location = new Point(rowPanel.ClientSize.Width - copyButton.Width - 10, 4);

                _resultsPanel.Controls.Add(rowPanel);
            }
        }
        finally
        {
            _resultsPanel.ResumeLayout();
        }
    }

    private void ClearResults()
    {
        _currentNodes = Array.Empty<string>();
        _copyAllButton.Enabled = false;
        SetStatus("Ready");
        foreach (Control control in _resultsPanel.Controls)
            control.Dispose();
        _resultsPanel.Controls.Clear();
    }

    private void SetStatus(string text) => _statusBarLabel.Text = $"  {text}";

    private void ToggleLoadingState(bool isLoading)
    {
        _convertButton.Enabled = !isLoading;
        _convertButton.Text = isLoading ? "Loading..." : "Convert";
    }
}
