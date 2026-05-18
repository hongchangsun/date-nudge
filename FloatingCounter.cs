using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DateReminder
{
    /// <summary>悬浮数字窗 - 托盘时显示</summary>
    public class FloatingCounter : Form
    {
        public enum ScannerStatus
        {
            Disconnected,   // 未连接（灰色）
            Connected,      // 已连接（绿色）
            Scanning,       // 扫码中（蓝色闪烁）
            PasswordBound,  // 密码已绑定（紫色）
            WaitingActivation // 等待激活（黄色）
        }

        private int _count;
        private readonly MainForm _mainForm;
        private bool _isDragging;
        private Point _dragOffset;
        private bool _isHovering;
        private ScannerStatus _scannerStatus = ScannerStatus.Disconnected;
        private int? _daysLeft = null;  // null = 未扫过限期商品

        // 浅色配色
        static readonly Color BG_NORMAL = Color.FromArgb(255, 255, 255);
        static readonly Color BG_HOVER = Color.FromArgb(248, 250, 255);
        static readonly Color BORDER_NORMAL = Color.FromArgb(210, 218, 235);
        static readonly Color BORDER_HOVER = Color.FromArgb(64, 128, 255);
        static readonly Color NUMBER_COLOR = Color.FromArgb(64, 128, 255);
        static readonly Color LABEL_COLOR = Color.FromArgb(150, 155, 170);
        static readonly Color SHADOW_COLOR = Color.FromArgb(180, 190, 210);

        // 扫码枪状态灯颜色
        static readonly Color LIGHT_DISCONNECTED = Color.FromArgb(200, 200, 210);
        static readonly Color LIGHT_CONNECTED = Color.FromArgb(34, 197, 94);
        static readonly Color LIGHT_SCANNING = Color.FromArgb(59, 130, 246);
        static readonly Color LIGHT_PASSWORD = Color.FromArgb(168, 85, 247);
        static readonly Color LIGHT_WAITING = Color.FromArgb(251, 191, 36);  // 黄色

        public FloatingCounter(MainForm mainForm)
        {
            _mainForm = mainForm;
            _count = 0;

            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.Size = new Size(88, 45);
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.DoubleBuffered = true;
            this.Opacity = 0.95;
            this.BackColor = BG_NORMAL;
            this.Cursor = Cursors.Hand;

            var screen = Screen.PrimaryScreen!.WorkingArea;
            this.Location = new Point(screen.Right - this.Width - 20, screen.Bottom - this.Height - 20);

            ApplyRoundedRegion();

            this.MouseDown += FloatingCounter_MouseDown;
            this.MouseMove += FloatingCounter_MouseMove;
            this.MouseUp += FloatingCounter_MouseUp;
            this.MouseEnter += (s, e) => { _isHovering = true; this.Invalidate(); };
            this.MouseLeave += (s, e) => { _isHovering = false; this.Invalidate(); };
            this.DoubleClick += (s, e) => _mainForm.ShowFromTray();
            this.Resize += (s, e) => ApplyRoundedRegion();
        }

        public void UpdateCount(int count)
        {
            if (_count == count) return;
            _count = count;
            this.Invalidate();
        }

        public void SetStatus(ScannerStatus status)
        {
            if (_scannerStatus == status) return;
            _scannerStatus = status;
            this.Invalidate();
        }

        public void SetDaysLeft(int? days)
        {
            _daysLeft = days;
            this.Invalidate();
        }

        public void RefreshDaysLeft()
        {
            // 跨日后刷新：如果有过期记录，重算天数（_expiryDate 未存，由 MainForm 调用 SetDaysLeft）
            this.Invalidate();
        }

        private void ApplyRoundedRegion()
        {
            var path = new GraphicsPath();
            int r = 18;
            var rect = new Rectangle(0, 0, this.Width, this.Height);
            path.AddArc(rect.X, rect.Y, r, r, 180, 90);
            path.AddArc(rect.X + rect.Width - r, rect.Y, r, r, 270, 90);
            path.AddArc(rect.X + rect.Width - r, rect.Y + rect.Height - r, r, r, 0, 90);
            path.AddArc(rect.X, rect.Y + rect.Height - r, r, r, 90, 90);
            path.CloseAllFigures();
            this.Region = new Region(path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var bg = _isHovering ? BG_HOVER : BG_NORMAL;
            var border = _isHovering ? BORDER_HOVER : BORDER_NORMAL;

            // 阴影
            using (var shadowBrush = new SolidBrush(SHADOW_COLOR))
            {
                var shadowRect = new Rectangle(2, 3, this.Width - 2, this.Height - 2);
                var r = 16;
                var shadowPath = new GraphicsPath();
                shadowPath.AddArc(shadowRect.X, shadowRect.Y, r, r, 180, 90);
                shadowPath.AddArc(shadowRect.X + shadowRect.Width - r, shadowRect.Y, r, r, 270, 90);
                shadowPath.AddArc(shadowRect.X + shadowRect.Width - r, shadowRect.Y + shadowRect.Height - r, r, r, 0, 90);
                shadowPath.AddArc(shadowRect.X, shadowRect.Y + shadowRect.Height - r, r, r, 90, 90);
                shadowPath.CloseAllFigures();
                g.FillPath(shadowBrush, shadowPath);
            }

            // 白色圆角背景
            using (var bgBrush = new SolidBrush(bg))
            using (var borderPen = new Pen(border, _isHovering ? 2 : 1))
            {
                var rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
                var r = 16;
                var path = new GraphicsPath();
                path.AddArc(rect.X, rect.Y, r, r, 180, 90);
                path.AddArc(rect.X + rect.Width - r, rect.Y, r, r, 270, 90);
                path.AddArc(rect.X + rect.Width - r, rect.Y + rect.Height - r, r, r, 0, 90);
                path.AddArc(rect.X, rect.Y + rect.Height - r, r, r, 90, 90);
                path.CloseAllFigures();

                g.FillPath(bgBrush, path);
                g.DrawPath(borderPen, path);
            }

            // 扫码枪状态灯（右上角小圆点）
            var lightColor = _scannerStatus switch
            {
                ScannerStatus.Connected => LIGHT_CONNECTED,
                ScannerStatus.Scanning => LIGHT_SCANNING,
                ScannerStatus.PasswordBound => LIGHT_PASSWORD,
                ScannerStatus.WaitingActivation => LIGHT_WAITING,
                _ => LIGHT_DISCONNECTED
            };
            using (var lightBrush = new SolidBrush(lightColor))
            {
                g.FillEllipse(lightBrush, this.Width - 20, 8, 10, 10);
            }
            // 状态灯外圈
            using (var lightPen = new Pen(Color.FromArgb(255, 255, 255), 2))
            {
                g.DrawEllipse(lightPen, this.Width - 21, 7, 12, 12);
            }

            // 剩余天数数字（居中显示）
            if (_daysLeft.HasValue)
            {
                int d = _daysLeft.Value;
                string displayText = d.ToString();
                Color daysColor = d > 7 ? LIGHT_CONNECTED         // 绿色：余7天以上
                                : d > 1 ? Color.FromArgb(234, 179, 8)  // 黄色：2-7天
                                : d >= 0 ? Color.FromArgb(239, 68, 68)  // 红色：0-1天
                                         : Color.FromArgb(220, 38, 38);  // 深红：已过期
                
                float fontSize = d >= 100 ? 24f : d <= -100 ? 24f : 32f;
                using (var numFont = new Font("Segoe UI", fontSize, FontStyle.Bold))
                using (var numBrush = new SolidBrush(daysColor))
                {
                    var numSize = g.MeasureString(displayText, numFont);
                    var nx = (this.Width - numSize.Width) / 2;
                    var ny = (this.Height - numSize.Height) / 2;
                    g.DrawString(displayText, numFont, numBrush, nx, ny);
                }
            }
        }

        private void FloatingCounter_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _isDragging = true;
                _dragOffset = e.Location;
            }
        }

        private void FloatingCounter_MouseMove(object? sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                var newLocation = this.PointToScreen(e.Location);
                newLocation.Offset(-_dragOffset.X, -_dragOffset.Y);
                this.Location = newLocation;
            }
        }

        public event Action? PositionChanged;

        private void FloatingCounter_MouseUp(object? sender, MouseEventArgs e)
        {
            _isDragging = false;
            PositionChanged?.Invoke();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;
                return cp;
            }
        }
    }
}
