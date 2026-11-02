// Dev Game Switch - start/stop the local dev stack (Postgres, Redis, RabbitMQ,
// Docker Desktop) from one small window. Built with the C# 5 compiler that
// ships with Windows (.NET Framework 4), so no SDK is needed - see build.cmd.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualBasic.Devices;
using Microsoft.Win32;

namespace DevGameSwitch
{
    enum State { Running, Stopped, Busy, Missing }

    class ServiceDef
    {
        public string Label;
        // Windows service name; null means Docker Desktop (an app, not a service).
        public string ServiceName;
        // Whether "Start all" starts it. Docker is only needed for e2e/integration tests.
        public bool InStartAll;
        // Short description shown under the name.
        public string Role;
    }

    // Colours for the Windows "app mode" setting (light or dark).
    static class Theme
    {
        public static bool Dark;
        public static Color Window, Card, CardBorder, Hover, Text, Muted, Track;
        public static Color Accent, AccentHover, Success, Warning, Danger;

        public static void Load()
        {
#if PREVIEW_LIGHT
            Dark = false;
#else
            Dark = IsDarkMode();
#endif
            Accent = Color.FromArgb(5, 150, 105);
            if (Dark)
            {
                Window = Color.FromArgb(24, 24, 27);
                Card = Color.FromArgb(34, 34, 38);
                CardBorder = Color.FromArgb(48, 48, 54);
                Hover = Color.FromArgb(44, 44, 50);
                Text = Color.FromArgb(244, 244, 245);
                Muted = Color.FromArgb(161, 161, 170);
                Track = Color.FromArgb(70, 70, 78);
                AccentHover = Color.FromArgb(16, 185, 129);
                Success = Color.FromArgb(52, 211, 153);
                Warning = Color.FromArgb(251, 191, 36);
                Danger = Color.FromArgb(248, 113, 113);
            }
            else
            {
                Window = Color.FromArgb(243, 244, 246);
                Card = Color.White;
                CardBorder = Color.FromArgb(229, 231, 235);
                Hover = Color.FromArgb(249, 250, 251);
                Text = Color.FromArgb(17, 24, 39);
                Muted = Color.FromArgb(107, 114, 128);
                Track = Color.FromArgb(209, 213, 219);
                AccentHover = Color.FromArgb(4, 120, 87);
                Success = Color.FromArgb(4, 120, 87);
                Warning = Color.FromArgb(180, 83, 9);
                Danger = Color.FromArgb(220, 38, 38);
            }
        }

        static bool IsDarkMode()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value is int && (int)value == 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    // DPI scaling, fonts and drawing helpers shared by the custom controls.
    static class Ui
    {
        public static float Scale = 1f;
        public static Font Body, Name, Title, Small;

        public static int S(float px) { return (int)Math.Round(px * Scale); }

        public static void LoadFonts()
        {
            Body = new Font(Family("Segoe UI Variable Text", "Segoe UI"), 10f);
            Name = new Font(Family("Segoe UI Variable Text Semibold", "Segoe UI Semibold"), 10.5f);
            // GDI truncates family names to 31 characters, hence "Semib".
            Title = new Font(Family("Segoe UI Variable Display Semib", "Segoe UI Semibold"), 16f);
            Small = new Font(Family("Segoe UI Variable Small", "Segoe UI"), 9f);
        }

        static string Family(string preferred, string fallback)
        {
            using (var fonts = new InstalledFontCollection())
                return fonts.Families.Any(f => f.Name == preferred) ? preferred : fallback;
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public const TextFormatFlags Left = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        public const TextFormatFlags Center = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
    }

    // Base for the owner-drawn controls: flicker-free painting, theme background.
    class PaintedControl : Control
    {
        public PaintedControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected Graphics Begin(PaintEventArgs e, Color background)
        {
            var g = e.Graphics;
            g.Clear(background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            return g;
        }
    }

    // iOS/Windows 11 style on/off switch with an animated knob and a busy spinner.
    class ToggleSwitch : PaintedControl
    {
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
        bool on, busy;
        float pos;   // knob position: 0 = off, 1 = on
        float spin;  // spinner angle while busy

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.Selectable, true);
            Size = new Size(Ui.S(44), Ui.S(24));
            Cursor = Cursors.Hand;
            anim.Tick += delegate { Step(); };
        }

        public bool On
        {
            get { return on; }
            set { if (on != value) { on = value; anim.Start(); } }
        }

        public bool Busy
        {
            get { return busy; }
            set { if (busy != value) { busy = value; anim.Start(); Invalidate(); } }
        }

        void Step()
        {
            var target = on ? 1f : 0f;
            pos += Math.Sign(target - pos) * Math.Min(0.14f, Math.Abs(target - pos));
            if (busy) spin = (spin + 10) % 360;
            if (pos == target && !busy) anim.Stop();
            Invalidate();
        }

        protected override void OnClick(EventArgs e)
        {
            if (!busy) base.OnClick(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e, BackColor);
            var track = new RectangleF(1, 1, Width - 2, Height - 2);
            var color = Enabled ? Ui.Mix(Theme.Track, Theme.Accent, pos) : Ui.Mix(Theme.Track, BackColor, 0.5f);
            using (var path = Ui.Round(track, track.Height / 2))
            using (var b = new SolidBrush(color))
                g.FillPath(b, path);

            if (Focused && ShowFocusCues)
            {
                using (var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Height / 2f))
                using (var pen = new Pen(Theme.Text, 1f))
                    g.DrawPath(pen, path);
            }

            var pad = Ui.S(3);
            var d = track.Height - pad * 2;
            var x = track.X + pad + pos * (track.Width - pad * 2 - d);
            var knob = new RectangleF(x, track.Y + pad, d, d);
            using (var b = new SolidBrush(Enabled ? Color.White : Ui.Mix(Color.White, BackColor, 0.4f)))
                g.FillEllipse(b, knob);

            if (busy)
            {
                var inset = d * 0.22f;
                using (var pen = new Pen(color, Math.Max(1.5f, Ui.S(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, RectangleF.Inflate(knob, -inset, -inset), spin, 100);
            }
        }
    }

    // A rounded card for one service: name, status line and its switch.
    class ServiceCard : PaintedControl
    {
        public readonly ServiceDef Def;
        public readonly ToggleSwitch Switch = new ToggleSwitch();
        public State Current = State.Stopped;
        public bool Busy;
        string status = "";
        Color statusColor;

        public ServiceCard(ServiceDef def)
        {
            Def = def;
            Controls.Add(Switch);
        }

        public void SetStatus(string text, Color color)
        {
            status = text;
            statusColor = color;
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Switch.Location = new Point(Width - Ui.S(18) - Switch.Width, (Height - Switch.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e, Theme.Window);
            using (var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Ui.S(10)))
            using (var fill = new SolidBrush(Theme.Card))
            using (var border = new Pen(Theme.CardBorder))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            var x = Ui.S(18);
            var nameY = Ui.S(12);
            TextRenderer.DrawText(g, Def.Label, Ui.Name, new Point(x, nameY), Theme.Text, Theme.Card, Ui.Left);

            var lineY = nameY + Ui.Name.Height + Ui.S(3);
            var dot = Ui.S(7);
            using (var b = new SolidBrush(statusColor))
                g.FillEllipse(b, x, lineY + (Ui.Small.Height - dot) / 2f, dot, dot);

            var textX = x + dot + Ui.S(7);
            TextRenderer.DrawText(g, status, Ui.Small, new Point(textX, lineY), statusColor, Theme.Card, Ui.Left);
            textX += TextRenderer.MeasureText(g, status, Ui.Small, Size.Empty, Ui.Left).Width;
            TextRenderer.DrawText(g, "  \u00B7  " + Def.Role, Ui.Small, new Point(textX, lineY), Theme.Muted, Theme.Card, Ui.Left);
        }
    }

    // Rounded button: filled accent (primary) or card-coloured with a border.
    class PillButton : PaintedControl
    {
        public bool Primary;
        bool hover, down;

        public PillButton()
        {
            SetStyle(ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e, Theme.Window);
            Color fill, text;
            Color? border = null;
            if (Primary)
            {
                fill = !Enabled ? Ui.Mix(Theme.Accent, Theme.Window, 0.55f)
                    : hover ? Theme.AccentHover : Theme.Accent;
                if (down && Enabled) fill = Ui.Mix(fill, Color.Black, 0.12f);
                text = Enabled ? Color.White : Ui.Mix(Color.White, Theme.Window, 0.3f);
            }
            else
            {
                fill = hover && Enabled ? Theme.Hover : Theme.Card;
                if (down && Enabled) fill = Ui.Mix(fill, Theme.Text, 0.06f);
                text = Enabled ? Theme.Text : Theme.Muted;
                border = Theme.CardBorder;
            }

            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Ui.Round(r, Ui.S(8)))
            using (var b = new SolidBrush(fill))
            {
                g.FillPath(b, path);
                if (border.HasValue)
                    using (var pen = new Pen(border.Value)) g.DrawPath(pen, path);
            }
            if (Focused && ShowFocusCues)
            {
                using (var path = Ui.Round(RectangleF.Inflate(r, -2, -2), Ui.S(6)))
                using (var pen = new Pen(Primary ? Color.White : Theme.Accent, 1.5f))
                    g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Ui.Name, ClientRectangle, text, fill, Ui.Center);
        }
    }

    // Memory bar plus a line about node processes.
    class MemoryFooter : PaintedControl
    {
        double freeGb, totalGb;
        string node = "";

        public int PreferredHeightFor()
        {
            return Ui.Small.Height * 2 + Ui.S(6) + Ui.S(6) + Ui.S(10);
        }

        public void SetValues(double free, double total, string nodeText)
        {
            freeGb = free;
            totalGb = total;
            node = nodeText;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e, Theme.Window);
            TextRenderer.DrawText(g, "Memory", Ui.Small, new Point(0, 0), Theme.Muted, Theme.Window, Ui.Left);
            var amount = string.Format("{0:0.0} GB free of {1:0.0} GB", freeGb, totalGb);
            TextRenderer.DrawText(g, amount, Ui.Small, new Rectangle(0, 0, Width, Ui.Small.Height),
                Theme.Muted, Theme.Window, Ui.Left | TextFormatFlags.Right);

            var used = totalGb > 0 ? (float)(1 - freeGb / totalGb) : 0f;
            var bar = new RectangleF(0, Ui.Small.Height + Ui.S(6), Width, Ui.S(6));
            using (var path = Ui.Round(bar, bar.Height / 2))
            using (var b = new SolidBrush(Theme.Track))
                g.FillPath(b, path);
            var fillColor = used < 0.7f ? Theme.Accent : used < 0.85f ? Theme.Warning : Theme.Danger;
            var filled = new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * used), bar.Height);
            using (var path = Ui.Round(filled, bar.Height / 2))
            using (var b = new SolidBrush(fillColor))
                g.FillPath(b, path);

            TextRenderer.DrawText(g, node, Ui.Small, new Rectangle(0, (int)bar.Bottom + Ui.S(10), Width, Ui.Small.Height),
                Theme.Muted, Theme.Window, Ui.Left | TextFormatFlags.EndEllipsis);
        }
    }

    class MainForm : Form
    {
        static readonly ServiceDef[] Defs =
        {
            new ServiceDef { Label = "PostgreSQL 18", ServiceName = "postgresql-x64-18", InStartAll = true, Role = "Database" },
            new ServiceDef { Label = "Redis", ServiceName = "Redis", InStartAll = true, Role = "Cache" },
            new ServiceDef { Label = "RabbitMQ", ServiceName = "RabbitMQ", InStartAll = true, Role = "Message queue" },
            new ServiceDef { Label = "Docker Desktop", ServiceName = null, InStartAll = false, Role = "e2e / integration tests" },
        };

        const string DockerExe = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
        static readonly string[] DockerProcesses = { "Docker Desktop", "com.docker.backend", "com.docker.build" };
        // RabbitMQ's service stops, but Erlang's port mapper daemon stays behind.
        static readonly string[] ErlangLeftovers = { "epmd", "erl", "erlsrv" };

        readonly List<ServiceCard> cards = new List<ServiceCard>();
        readonly Label title = new Label();
        readonly Label subtitle = new Label();
        readonly PillButton startAll = new PillButton { Primary = true, Text = "Start all" };
        readonly PillButton stopAll = new PillButton { Text = "Gaming mode" };
        readonly MemoryFooter footer = new MemoryFooter();
        readonly ToolTip tips = new ToolTip();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        int bulkOps;

        public MainForm()
        {
            Theme.Load();
            using (var g = CreateGraphics()) Ui.Scale = g.DpiX / 96f;
            Ui.LoadFonts();

            Text = "Dev Game Switch";
            // Same icon as the exe (app.ico, embedded by build.cmd) for the title bar and taskbar.
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = Ui.Body;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            int pad = Ui.S(20), width = Ui.S(360), y = Ui.S(14);

            title.Text = "Dev Game Switch";
            title.Font = Ui.Title;
            title.AutoSize = true;
            title.Location = new Point(pad - Ui.S(2), y);
            y += Ui.Title.Height + Ui.S(2);

            subtitle.Font = Ui.Small;
            subtitle.AutoSize = true;
            subtitle.Location = new Point(pad, y);
            y += Ui.Small.Height + Ui.S(16);
            Controls.Add(title);
            Controls.Add(subtitle);

            foreach (var def in Defs)
            {
                var card = new ServiceCard(def) { Bounds = new Rectangle(pad, y, width, Ui.S(64)) };
                card.Switch.Click += delegate { ToggleRow(card); };
                Controls.Add(card);
                cards.Add(card);
                y += card.Height + Ui.S(8);
            }

            y += Ui.S(8);
            var half = (width - Ui.S(10)) / 2;
            startAll.Bounds = new Rectangle(pad, y, half, Ui.S(40));
            stopAll.Bounds = new Rectangle(pad + width - half, y, half, Ui.S(40));
            startAll.Click += delegate { StartAll(); };
            stopAll.Click += delegate { StopAll(); };
            tips.SetToolTip(startAll, "Starts PostgreSQL, Redis and RabbitMQ (not Docker)");
            tips.SetToolTip(stopAll, "Stops every service and Docker, then shuts down WSL");
            Controls.Add(startAll);
            Controls.Add(stopAll);
            y += startAll.Height + Ui.S(22);

            footer.Bounds = new Rectangle(pad, y, width, footer.PreferredHeightFor());
            Controls.Add(footer);
            y += footer.Height + Ui.S(18);

            ClientSize = new Size(width + pad * 2, y);
            ApplyTheme();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            timer.Interval = 3000;
            timer.Tick += delegate { RefreshAll(); };
            timer.Start();
            RefreshAll();
        }

        // ---- theme ------------------------------------------------------------

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        const int DwmUseImmersiveDarkMode = 20, DwmCaptionColor = 35, DwmTextColor = 36;

        void ApplyTheme()
        {
            BackColor = Theme.Window;
            title.ForeColor = Theme.Text;
            subtitle.ForeColor = Theme.Muted;
            foreach (var card in cards)
            {
                card.Switch.BackColor = Theme.Card;
                card.Invalidate(true);
            }
            startAll.Invalidate();
            stopAll.Invalidate();
            footer.Invalidate();
            ApplyTitleBar();
        }

        // Windows 11: title bar in the window's own colours so it blends in.
        void ApplyTitleBar()
        {
            if (!IsHandleCreated) return;
            var dark = Theme.Dark ? 1 : 0;
            var caption = ColorTranslator.ToWin32(Theme.Window);
            var text = ColorTranslator.ToWin32(Theme.Text);
            DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref dark, 4);
            DwmSetWindowAttribute(Handle, DwmCaptionColor, ref caption, 4);
            DwmSetWindowAttribute(Handle, DwmTextColor, ref text, 4);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBar();
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General) return;
            OnUi(delegate
            {
                var wasDark = Theme.Dark;
                Theme.Load();
                if (wasDark == Theme.Dark) return;
                ApplyTheme();
                RefreshAll();
            });
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            base.OnFormClosed(e);
        }

        // ---- status -------------------------------------------------------

        static State GetState(ServiceDef def)
        {
            if (def.ServiceName == null)
            {
                if (!File.Exists(DockerExe)) return State.Missing;
                return DockerProcesses.Any(p => Process.GetProcessesByName(p).Length > 0)
                    ? State.Running
                    : State.Stopped;
            }

            try
            {
                using (var sc = new ServiceController(def.ServiceName))
                {
                    switch (sc.Status)
                    {
                        case ServiceControllerStatus.Running: return State.Running;
                        case ServiceControllerStatus.Stopped: return State.Stopped;
                        default: return State.Busy;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                return State.Missing;
            }
        }

        void RefreshAll()
        {
            foreach (var card in cards)
            {
                if (!card.Busy) ShowState(card, GetState(card.Def));
            }

            var anyBusy = cards.Any(c => c.Busy || c.Current == State.Busy) || bulkOps > 0;
            startAll.Enabled = !anyBusy;
            stopAll.Enabled = !anyBusy;

            var installed = cards.Count(c => c.Current != State.Missing);
            var running = cards.Count(c => c.Current == State.Running);
            subtitle.Text = anyBusy ? "Working\u2026"
                : running == 0 ? "Nothing running \u00B7 ready for gaming"
                : string.Format("{0} of {1} running", running, installed);

            UpdateFooter();
        }

        static void ShowState(ServiceCard card, State state)
        {
            card.Current = state;
            card.Switch.Busy = state == State.Busy;
            card.Switch.Enabled = state != State.Missing;
            switch (state)
            {
                case State.Running:
                    card.Switch.On = true;
                    card.SetStatus("Running", Theme.Success);
                    break;
                case State.Stopped:
                    card.Switch.On = false;
                    card.SetStatus("Stopped", Theme.Muted);
                    break;
                case State.Busy:
                    card.SetStatus("Changing\u2026", Theme.Warning);
                    break;
                default:
                    card.Switch.On = false;
                    card.SetStatus("Not installed", Theme.Danger);
                    break;
            }
        }

        void UpdateFooter()
        {
            var info = new ComputerInfo();
            var freeGb = info.AvailablePhysicalMemory / 1073741824.0;
            var totalGb = info.TotalPhysicalMemory / 1073741824.0;

            var node = Process.GetProcessesByName("node");
            long bytes = 0;
            foreach (var p in node)
            {
                try { bytes += p.WorkingSet64; } catch { }
                p.Dispose();
            }
            var nodeText = node.Length == 0
                ? "node: none running"
                : string.Format("node: {0} processes, {1:0} MB (VS Code, dev servers)", node.Length, bytes / 1048576.0);

            footer.SetValues(freeGb, totalGb, nodeText);
        }

        // ---- actions ------------------------------------------------------

        void ToggleRow(ServiceCard card)
        {
            if (card.Busy) return;
            var start = GetState(card.Def) != State.Running;
            RunInBackground(card, start, delegate
            {
                if (start) StartDef(card.Def); else StopDef(card.Def);
            });
        }

        void StartAll()
        {
            foreach (var card in cards.Where(c => c.Def.InStartAll && GetState(c.Def) == State.Stopped))
            {
                var def = card.Def;
                RunInBackground(card, true, delegate { StartDef(def); });
            }
        }

        void StopAll()
        {
            foreach (var card in cards.Where(c => GetState(c.Def) == State.Running))
            {
                var def = card.Def;
                RunInBackground(card, false, delegate { StopDef(def); });
            }

            // WSL can be running without Docker Desktop (e.g. a distro left open).
            bulkOps++;
            startAll.Enabled = stopAll.Enabled = false;
            subtitle.Text = "Working\u2026";
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { RunHidden("wsl.exe", "--shutdown"); } catch { }
                OnUi(delegate { bulkOps--; RefreshAll(); });
            });
        }

        void RunInBackground(ServiceCard card, bool starting, Action work)
        {
            card.Busy = true;
            card.Switch.On = starting;
            card.Switch.Busy = true;
            card.SetStatus(starting ? "Starting\u2026" : "Stopping\u2026", Theme.Warning);
            startAll.Enabled = stopAll.Enabled = false;
            subtitle.Text = "Working\u2026";

            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try { work(); }
                catch (Exception ex) { error = ex.InnerException != null ? ex.InnerException.Message : ex.Message; }

                OnUi(delegate
                {
                    card.Busy = false;
                    RefreshAll();
                    if (error != null)
                    {
                        MessageBox.Show(this, card.Def.Label + ": " + error, "Dev Game Switch",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                });
            });
        }

        // Marshal back to the UI thread; ignored if the window was closed meanwhile.
        void OnUi(Action action)
        {
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        static void StartDef(ServiceDef def)
        {
            if (def.ServiceName == null)
            {
                // Through explorer so Docker Desktop runs as the normal user,
                // not elevated like this app.
                Process.Start("explorer.exe", "\"" + DockerExe + "\"");
                WaitFor(() => GetState(def) == State.Running, 30);
                return;
            }

            using (var sc = new ServiceController(def.ServiceName))
            {
                if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            }
        }

        static void StopDef(ServiceDef def)
        {
            if (def.ServiceName == null)
            {
                KillProcesses(DockerProcesses);
                StopServiceIfRunning("com.docker.service");
                // Docker's engine lives in the WSL2 VM - shut it down too.
                RunHidden("wsl.exe", "--shutdown");
                return;
            }

            StopServiceIfRunning(def.ServiceName);
            if (def.ServiceName == "RabbitMQ") KillProcesses(ErlangLeftovers);
        }

        static void StopServiceIfRunning(string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped) return;
                    if (sc.CanStop) sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                }
            }
            catch (InvalidOperationException)
            {
                // Not installed.
            }
        }

        static void KillProcesses(IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(); p.WaitForExit(5000); } catch { }
                    p.Dispose();
                }
            }
        }

        static void RunHidden(string file, string args)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using (var p = Process.Start(psi)) { p.WaitForExit(30000); }
        }

        static void WaitFor(Func<bool> condition, int seconds)
        {
            for (var i = 0; i < seconds * 2 && !condition(); i++) Thread.Sleep(500);
        }
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
}
