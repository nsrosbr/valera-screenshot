using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Windows.Forms;

namespace ValeraScreenshot
{
    internal enum OverlayResult { Cancelled, Copied, Saved, Printed, Shared }

    // Повноекранний «заморожений» кадр: вибір області, редагування, дії (як у Lightshot).
    internal class OverlayForm : Form
    {
        private readonly Bitmap _shot;          // кадр у фізичних пікселях; форма ним володіє
        private readonly Rectangle _virt;
        private readonly Config _cfg;

        private Rectangle _sel;
        private bool _hasSel;
        private bool _selecting;
        private Point _dragStart;
        private bool _moving;
        private Point _moveGrab;
        private int _resize = -1;               // 0..7 — маркер зміни розміру
        private Rectangle _resizeStart;

        private Tool _tool = Tool.Select;
        private readonly List<Ann> _anns = new List<Ann>();
        private readonly List<Ann> _redo = new List<Ann>();
        private Ann _cur;
        private RouteAnn _route;         // маршрут у процесі побудови (по кліках)
        private Point _routePreview;     // положення курсору для «гумової» лінії
        private Color _color;
        private int _width;

        private TextBox _edit;
        private Point _editPos;

        private class Btn
        {
            public string Id, Hint;
            public Rectangle R;
            public bool GroupEnd;
            public Btn(string id, string hint, bool groupEnd) { Id = id; Hint = hint; GroupEnd = groupEnd; }
        }
        private readonly List<Btn> _btns = new List<Btn>();
        private Rectangle _bar;
        private string _hover;
        private bool _palette;
        private Rectangle _palRect;
        private readonly List<KeyValuePair<Rectangle, Color>> _palCells = new List<KeyValuePair<Rectangle, Color>>();
        private Rectangle _palCustom;

        private Point _mouse;

        public OverlayResult Result = OverlayResult.Cancelled;
        public string SavedPath;
        public Size ResultSize;
        // Чи справді лягло в буфер. Трей мусить це знати, інакше він рапортує «Знімок у буфері»
        // над мовчазною відмовою — і користувач вставляє попередній вміст.
        public bool ClipboardOk = true;

        private static readonly Color[] PaletteColors = new[]
        {
            Color.FromArgb(0xE8,0x11,0x23), Color.FromArgb(0xFF,0x8C,0x00), Color.FromArgb(0xFF,0xF1,0x00),
            Color.FromArgb(0x16,0xC6,0x0C), Color.FromArgb(0x00,0xB7,0xC3), Color.FromArgb(0x00,0x78,0xD7),
            Color.FromArgb(0x88,0x6C,0xE4), Color.FromArgb(0xE3,0x00,0x8C), Color.White,
            Color.FromArgb(0x8A,0x8A,0x8A), Color.Black, Color.FromArgb(0x8B,0x45,0x13),
        };

        public OverlayForm(Bitmap shot, Rectangle virt, Config cfg)
        {
            _shot = shot; _virt = virt; _cfg = cfg;
            _color = Color.FromArgb(cfg.LastColor);
            _width = cfg.LastWidth;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = virt;
            Cursor = Cursors.Cross;
            KeyPreview = true;

            BuildButtons();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Bounds = _virt; // проти самовільного клампу WinForms
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            // Відключення монітора (або зміна роздільності) під час відкритого оверлея лишало
            // повноекранне TopMost-вікно на координатах, яких уже не існує: воно ставало
            // частково або зовсім невидимим, продовжуючи ковтати ввід. Вихід був наосліп Esc.
            // Кадр уже заморожений і перегляду геометрії не переживе — чесний шлях закритись.
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            try
            {
                if (ScreenCap.VirtualScreen() == _virt) return;   // для нас нічого не змінилось
                Diag.Log(L.S("оверлей закрито: змінилась конфігурація дисплеїв", "overlay closed: the display configuration changed"));
                Result = OverlayResult.Cancelled;
                Close();
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // SystemEvents тримає СТАТИЧНУ подію: не відписавшись, форма лишалась би живою
                // після закриття — витік гірший за ті, що ми щойно лікували.
                try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; } catch { }
                if (_shot != null) _shot.Dispose();
                // Анотації теж володіють ресурсами (мозаїка тримає власний Bitmap). Диспозився
                // лише кадр, а обидва списки — і чинний, і скасований — тихо губилися.
                foreach (var a in _anns) { try { a.Dispose(); } catch { } }
                foreach (var a in _redo) { try { a.Dispose(); } catch { } }
                _anns.Clear(); _redo.Clear();
                if (_cur != null) { try { _cur.Dispose(); } catch { } _cur = null; }
                if (_route != null) { try { _route.Dispose(); } catch { } _route = null; }
            }
            base.Dispose(disposing);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _cfg.LastColor = _color.ToArgb();
            _cfg.LastWidth = _width;
            base.OnFormClosed(e);
        }

        private void BuildButtons()
        {
            _btns.Add(new Btn("select", L.S("Вибір / переміщення (V)", "Select / move (V)"), true));
            _btns.Add(new Btn("pen", L.S("Олівець (P)", "Pencil (P)"), false));
            _btns.Add(new Btn("marker", L.S("Маркер (M)", "Highlighter (M)"), false));
            _btns.Add(new Btn("line", L.S("Лінія (L)", "Line (L)"), false));
            _btns.Add(new Btn("arrow", L.S("Стрілка (A)", "Arrow (A)"), true));
            _btns.Add(new Btn("rect", L.S("Рамка (R)", "Rectangle (R)"), false));
            _btns.Add(new Btn("ellipse", L.S("Еліпс (E)", "Ellipse (E)"), false));
            _btns.Add(new Btn("fillrect", L.S("Підсвітити зону (F)", "Highlight an area (F)"), true));
            _btns.Add(new Btn("pixelate", L.S("Мозаїка — розмити зону (I)", "Mosaic — blur an area (I)"), false));
            _btns.Add(new Btn("step", L.S("Нумерований крок 1-2-3 (N)", "Numbered step 1-2-3 (N)"), false));
            _btns.Add(new Btn("route", L.S("Маршрут: клік — точка, 2 кліки — завершити (G)", "Route: click adds a point, double-click finishes (G)"), false));
            _btns.Add(new Btn("text", L.S("Текст (T)", "Text (T)"), false));
            _btns.Add(new Btn("redact", L.S("Чорна заливка — приховати (B)", "Solid black — redact (B)"), true));
            _btns.Add(new Btn("color", L.S("Колір", "Colour"), false));
            _btns.Add(new Btn("width", L.S("Товщина (коліщатко теж працює)", "Thickness (the mouse wheel works too)"), true));
            _btns.Add(new Btn("undo", L.S("Скасувати (Ctrl+Z)", "Undo (Ctrl+Z)"), false));
            _btns.Add(new Btn("redo", L.S("Повернути (Ctrl+Y)", "Redo (Ctrl+Y)"), true));
            _btns.Add(new Btn("copy", L.S("Копіювати в буфер (Enter / Ctrl+C)", "Copy to the clipboard (Enter / Ctrl+C)"), false));
            _btns.Add(new Btn("save", L.S("Зберегти у теку (Ctrl+S)", "Save to the folder (Ctrl+S)"), false));
            _btns.Add(new Btn("saveas", L.S("Зберегти як… (Ctrl+Shift+S)", "Save as… (Ctrl+Shift+S)"), false));
            _btns.Add(new Btn("print", L.S("Друк (Ctrl+P)", "Print (Ctrl+P)"), false));
            _btns.Add(new Btn("share", L.S("Поділитися — WhatsApp, Telegram, пошта… (Ctrl+D)", "Share — WhatsApp, Telegram, email… (Ctrl+D)"), true));
            _btns.Add(new Btn("close", L.S("Вийти (Esc)", "Exit (Esc)"), false));
        }

        private int NextStepNumber()
        {
            int n = 0;
            foreach (var a in _anns)
            {
                var s = a as StepAnn;
                if (s != null && s.Number > n) n = s.Number;
            }
            return n + 1;
        }

        private void Undo()
        {
            if (_anns.Count == 0) return;
            var a = _anns[_anns.Count - 1];
            _anns.RemoveAt(_anns.Count - 1);
            _redo.Add(a);
            Invalidate();
        }

        private void Redo()
        {
            if (_redo.Count == 0) return;
            var a = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _anns.Add(a);
            Invalidate();
        }

        private void CommitAnn(Ann a)
        {
            _anns.Add(a);
            // Нова дія обриває гілку «повернути»: ті анотації вже недосяжні, тож їхні ресурси
            // звільняємо тут. Раніше список просто очищався, і кожна скасована мозаїка лишала
            // по собі Bitmap до кінця процесу.
            foreach (var r in _redo) { try { r.Dispose(); } catch { } }
            _redo.Clear();
        }

        private void SetTool(Tool t)
        {
            if (_route != null && t != Tool.Route) FinishRoute();
            _tool = t;
            Invalidate();
        }

        // ---- маршрут (полілінія по кліках) ----
        private void RouteClick(Point p)
        {
            if (_route == null) _route = new RouteAnn { Color = _color, Width = _width };
            _route.Points.Add(p);
            _routePreview = p;
            Invalidate();
        }

        private void FinishRoute()
        {
            if (_route == null) return;
            // подвійний клік лишає точку-дублікат — прибираємо
            if (_route.Points.Count >= 2)
            {
                var a = _route.Points[_route.Points.Count - 1];
                var b = _route.Points[_route.Points.Count - 2];
                if (LineAnn.Dist(a, b) < 6) _route.Points.RemoveAt(_route.Points.Count - 1);
            }
            if (!_route.IsDegenerate()) CommitAnn(_route);
            _route = null;
            Invalidate();
        }

        private void CancelRoute() { _route = null; Invalidate(); }

        private void RemoveLastVertex()
        {
            if (_route == null) return;
            if (_route.Points.Count > 0) _route.Points.RemoveAt(_route.Points.Count - 1);
            if (_route.Points.Count == 0) _route = null;
            Invalidate();
        }

        // ---------- геометрія ----------

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        private Rectangle SelClamped()
        {
            return Rectangle.Intersect(_sel, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
        }

        // static: залежить ЛИШЕ від аргументів, отже перевіряється без вікна й без екрана.
        private static Point HandlePoint(int i, Rectangle s)
        {
            switch (i)
            {
                case 0: return new Point(s.Left, s.Top);
                case 1: return new Point(s.Left + s.Width / 2, s.Top);
                case 2: return new Point(s.Right, s.Top);
                case 3: return new Point(s.Right, s.Top + s.Height / 2);
                case 4: return new Point(s.Right, s.Bottom);
                case 5: return new Point(s.Left + s.Width / 2, s.Bottom);
                case 6: return new Point(s.Left, s.Bottom);
                default: return new Point(s.Left, s.Top + s.Height / 2);
            }
        }

        private int HitHandle(Point p)
        {
            if (!_hasSel) return -1;
            var s = SelClamped();
            for (int i = 0; i < 8; i++)
            {
                var h = HandlePoint(i, s);
                if (Math.Abs(p.X - h.X) <= 6 && Math.Abs(p.Y - h.Y) <= 6) return i;
            }
            return -1;
        }

        private static Cursor HandleCursor(int i)
        {
            switch (i)
            {
                case 0: case 4: return Cursors.SizeNWSE;
                case 2: case 6: return Cursors.SizeNESW;
                case 1: case 5: return Cursors.SizeNS;
                default: return Cursors.SizeWE;
            }
        }

        private bool BarActive()
        {
            return _hasSel && !_selecting && !_moving && _resize < 0 && _cur == null;
        }

        private void LayoutBar()
        {
            const int BTN = 32, PAD = 3, SEP = 8, EDGE = 6;
            int w = EDGE * 2;
            foreach (var b in _btns) { w += BTN + PAD; if (b.GroupEnd) w += SEP; }
            w -= PAD;
            int h = BTN + EDGE * 2;

            var s = SelClamped();
            int x = Clamp(s.Right - w, 8, Math.Max(8, ClientSize.Width - w - 8));
            int y = s.Bottom + 8;
            if (y + h > ClientSize.Height - 4) y = s.Top - h - 8;
            if (y < 4) y = Math.Max(4, s.Bottom - h - 8);
            _bar = new Rectangle(x, y, w, h);

            int cx = x + EDGE;
            foreach (var b in _btns)
            {
                b.R = new Rectangle(cx, y + EDGE, BTN, BTN);
                cx += BTN + PAD;
                if (b.GroupEnd) cx += SEP;
            }

            // палітра — над/під кнопкою кольору
            const int CELL = 26, COLS = 6;
            int pw = COLS * CELL + 12, ph = 2 * CELL + 12 + 30;
            Btn colorBtn = null;
            foreach (var b in _btns) if (b.Id == "color") { colorBtn = b; break; }
            int px = Clamp(colorBtn.R.X - 8, 8, Math.Max(8, ClientSize.Width - pw - 8));
            int py = _bar.Y - ph - 6;
            if (py < 4) py = _bar.Bottom + 6;
            _palRect = new Rectangle(px, py, pw, ph);
            _palCells.Clear();
            for (int i = 0; i < PaletteColors.Length; i++)
            {
                int r = i / COLS, c = i % COLS;
                _palCells.Add(new KeyValuePair<Rectangle, Color>(
                    new Rectangle(px + 6 + c * CELL, py + 6 + r * CELL, CELL - 4, CELL - 4), PaletteColors[i]));
            }
            _palCustom = new Rectangle(px + 6, py + 6 + 2 * CELL + 2, pw - 12, 24);
        }

        // ---------- миша ----------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _mouse = e.Location;

            if (_edit != null) { CommitText(); return; }

            if (e.Button == MouseButtons.Right)
            {
                if (_route != null) { FinishRoute(); return; } // ПКМ — завершити маршрут
                if (_palette) { _palette = false; Invalidate(); return; }
                if (_hasSel) { _hasSel = false; Invalidate(); return; }
                Close();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            if (_palette)
            {
                foreach (var kv in _palCells)
                    if (kv.Key.Contains(e.Location)) { _color = kv.Value; _palette = false; Invalidate(); return; }
                if (_palCustom.Contains(e.Location))
                {
                    _palette = false;
                    using (var dlg = new ColorDialog { Color = _color, FullOpen = true })
                        if (dlg.ShowDialog(this) == DialogResult.OK) _color = dlg.Color;
                    Invalidate(); return;
                }
                if (_palRect.Contains(e.Location)) return;
                _palette = false; Invalidate();
            }

            if (BarActive() && _bar.Contains(e.Location))
            {
                foreach (var b in _btns)
                    if (b.R.Contains(e.Location)) { OnButton(b.Id); return; }
                return;
            }

            int h = BarActive() ? HitHandle(e.Location) : -1;
            if (h >= 0)
            {
                _resize = h; _resizeStart = _sel; _dragStart = e.Location;
                return;
            }

            if (_hasSel && SelClamped().Contains(e.Location))
            {
                if (_tool == Tool.Select)
                {
                    _moving = true; _moveGrab = new Point(e.X - _sel.X, e.Y - _sel.Y);
                    return;
                }
                if (_tool == Tool.Text) { BeginTextEdit(e.Location); return; }
                if (_tool == Tool.Route) { RouteClick(e.Location); return; }
                BeginAnn(e.Location);
                return;
            }

            // нове виділення
            _selecting = true; _hasSel = true;
            _dragStart = e.Location;
            _sel = new Rectangle(e.Location, Size.Empty);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouse = e.Location;

            if (_route != null) { _routePreview = e.Location; Cursor = Cursors.Cross; Invalidate(); return; }

            if (_selecting)
            {
                _sel = Rectangle.FromLTRB(
                    Math.Min(_dragStart.X, e.X), Math.Min(_dragStart.Y, e.Y),
                    Math.Max(_dragStart.X, e.X), Math.Max(_dragStart.Y, e.Y));
                Invalidate(); return;
            }
            if (_resize >= 0)
            {
                int dx = e.X - _dragStart.X, dy = e.Y - _dragStart.Y;
                int L = _resizeStart.Left, T = _resizeStart.Top, R = _resizeStart.Right, B = _resizeStart.Bottom;
                if (_resize == 0 || _resize == 6 || _resize == 7) L += dx;
                if (_resize == 2 || _resize == 3 || _resize == 4) R += dx;
                if (_resize == 0 || _resize == 1 || _resize == 2) T += dy;
                if (_resize == 4 || _resize == 5 || _resize == 6) B += dy;
                _sel = Rectangle.FromLTRB(Math.Min(L, R), Math.Min(T, B), Math.Max(L, R), Math.Max(T, B));
                Invalidate(); return;
            }
            if (_moving)
            {
                int nx = Clamp(e.X - _moveGrab.X, 0, Math.Max(0, ClientSize.Width - _sel.Width));
                int ny = Clamp(e.Y - _moveGrab.Y, 0, Math.Max(0, ClientSize.Height - _sel.Height));
                _sel = new Rectangle(nx, ny, _sel.Width, _sel.Height);
                Invalidate(); return;
            }
            if (_cur != null) { UpdateAnn(e.Location); Invalidate(); return; }

            // стан спокою: курсор + hover тулбара
            string oldHover = _hover; _hover = null;
            if (BarActive() && _bar.Contains(e.Location))
            {
                Cursor = Cursors.Default;
                foreach (var b in _btns) if (b.R.Contains(e.Location)) { _hover = b.Id; break; }
            }
            else if (_palette && _palRect.Contains(e.Location)) Cursor = Cursors.Default;
            else
            {
                int h = BarActive() ? HitHandle(e.Location) : -1;
                if (h >= 0) Cursor = HandleCursor(h);
                else if (_hasSel && SelClamped().Contains(e.Location))
                    Cursor = _tool == Tool.Select ? Cursors.SizeAll :
                             (_tool == Tool.Text ? Cursors.IBeam : Cursors.Cross);
                else Cursor = Cursors.Cross;
            }
            if (_hover != oldHover) Invalidate();
            if (!_hasSel || _selecting) Invalidate(); // лупа рухається
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            if (_selecting)
            {
                _selecting = false;
                double moved = LineAnn.Dist(_dragStart, e.Location);
                if (moved < 4)
                {
                    // клік без тяги — увесь монітор під курсором
                    var scr = Screen.FromPoint(Control.MousePosition).Bounds;
                    scr.Offset(-_virt.X, -_virt.Y);
                    _sel = Rectangle.Intersect(scr, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
                }
                if (_sel.Width < 1 || _sel.Height < 1) _hasSel = false;
                Invalidate(); return;
            }
            if (_resize >= 0) { _resize = -1; Invalidate(); return; }
            if (_moving) { _moving = false; Invalidate(); return; }
            if (_cur != null)
            {
                UpdateAnn(e.Location);
                var px = _cur as PixelateAnn;
                if (px != null) px.Bake(_shot);
                if (!_cur.IsDegenerate()) CommitAnn(_cur);
                _cur = null;
                Invalidate();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (_route != null) { FinishRoute(); return; } // 2 кліки — маршрут готово
            if (e.Button == MouseButtons.Left && _hasSel && _tool == Tool.Select &&
                SelClamped().Contains(e.Location) && !_bar.Contains(e.Location))
                DoCopy();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _width = Clamp(_width + (e.Delta > 0 ? 1 : -1), 1, 12);
            Invalidate();
        }

        // ---------- анотації ----------

        private void BeginAnn(Point p)
        {
            switch (_tool)
            {
                case Tool.Pen: _cur = new PenAnn { Color = _color, Width = _width }; ((PenAnn)_cur).Points.Add(p); break;
                case Tool.Marker: _cur = new MarkerAnn { Color = _color, Width = _width }; ((MarkerAnn)_cur).Points.Add(p); break;
                case Tool.Line: _cur = new LineAnn { Color = _color, Width = _width, A = p, B = p }; break;
                case Tool.Arrow: _cur = new ArrowAnn { Color = _color, Width = _width, A = p, B = p }; break;
                case Tool.Rect: _cur = new RectAnn { Color = _color, Width = _width, A = p, B = p }; break;
                case Tool.Ellipse: _cur = new EllipseAnn { Color = _color, Width = _width, A = p, B = p }; break;
                case Tool.FillRect: _cur = new FillRectAnn { Color = _color, Width = _width, A = p, B = p }; break;
                case Tool.Pixelate: _cur = new PixelateAnn { Color = _color, Width = _width, A = p, B = p }; break;
                case Tool.Step: _cur = new StepAnn { Color = _color, Width = _width, Pos = p, Number = NextStepNumber() }; break;
                case Tool.Redact: _cur = new RedactAnn { Color = Color.Black, Width = _width, A = p, B = p }; break;
            }
            Invalidate();
        }

        private void UpdateAnn(Point p)
        {
            var pen = _cur as PenAnn;
            if (pen != null) { pen.Points.Add(p); return; }
            var mk = _cur as MarkerAnn;
            if (mk != null) { mk.Points.Add(p); return; }
            var st = _cur as StepAnn;
            if (st != null) { st.Pos = p; return; }
            var ln = _cur as LineAnn;
            if (ln != null) { ln.B = p; return; }
            var ar = _cur as ArrowAnn;
            if (ar != null) { ar.B = p; return; }
            var rc = _cur as RectAnn;
            if (rc != null) { rc.B = p; return; }
        }

        private void BeginTextEdit(Point p)
        {
            CommitText();
            _editPos = p;
            _edit = new TextBox();
            _edit.Multiline = true;
            _edit.BorderStyle = BorderStyle.FixedSingle;
            _edit.Font = new Font("Segoe UI", 10f + _width * 2.5f, FontStyle.Bold, GraphicsUnit.Point);
            _edit.ForeColor = _color.GetBrightness() > 0.92f ? Color.Black : _color;
            _edit.BackColor = Color.White;
            _edit.Location = p;
            _edit.Size = new Size(260, _edit.Font.Height + 12);
            _edit.TextChanged += delegate
            {
                var sz = TextRenderer.MeasureText(_edit.Text + "  W", _edit.Font);
                _edit.Size = new Size(Math.Max(260, sz.Width + 16), Math.Max(_edit.Font.Height + 12, sz.Height + 12));
            };
            Controls.Add(_edit);
            _edit.BringToFront();
            _edit.Focus();
        }

        private void CommitText()
        {
            if (_edit == null) return;
            string t = _edit.Text.TrimEnd();
            var box = _edit; _edit = null;
            Controls.Remove(box);
            // TextBox.Dispose НЕ звільняє призначений ззовні Font — його треба зняти окремо,
            // інакше кожне текстове поле лишає по собі HFONT.
            var f = box.Font; box.Font = null;
            box.Dispose();
            if (f != null) { try { f.Dispose(); } catch { } }
            if (t.Trim().Length > 0)
                CommitAnn(new TextAnn { Pos = _editPos, Text = t, Color = _color, Width = _width });
            Invalidate();
        }

        private void CancelText()
        {
            if (_edit == null) return;
            var box = _edit; _edit = null;
            Controls.Remove(box);
            var f = box.Font; box.Font = null;   // див. CommitText: Font не належить TextBox
            box.Dispose();
            if (f != null) { try { f.Dispose(); } catch { } }
            Invalidate();
        }

        // ---------- клавіатура ----------

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (_edit != null)
            {
                if (e.KeyCode == Keys.Escape) { CancelText(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter && !e.Shift) { CommitText(); e.SuppressKeyPress = true; }
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                if (_route != null) { CancelRoute(); return; }
                if (_palette) { _palette = false; Invalidate(); }
                else Close();
                return;
            }

            if (e.KeyCode == Keys.Back && _route != null) { RemoveLastVertex(); return; }

            if (e.Control)
            {
                switch (e.KeyCode)
                {
                    case Keys.C: DoCopy(); return;
                    case Keys.S: if (e.Shift) DoSaveAs(); else DoQuickSave(); return;
                    case Keys.P: DoPrint(); return;
                    case Keys.D: DoShare(); return;
                    case Keys.Z: if (_route != null) RemoveLastVertex(); else if (e.Shift) Redo(); else Undo(); return;
                    case Keys.Y: Redo(); return;
                    case Keys.A:
                        _hasSel = true;
                        _sel = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
                        Invalidate(); return;
                }
                return;
            }

            if (e.KeyCode == Keys.Enter) { if (_route != null) FinishRoute(); else DoCopy(); return; }

            // стрілки — рух виділення
            if (_hasSel)
            {
                int step = e.Shift ? 10 : 1;
                int dx = 0, dy = 0;
                if (e.KeyCode == Keys.Left) dx = -step;
                else if (e.KeyCode == Keys.Right) dx = step;
                else if (e.KeyCode == Keys.Up) dy = -step;
                else if (e.KeyCode == Keys.Down) dy = step;
                if (dx != 0 || dy != 0)
                {
                    _sel = new Rectangle(
                        Clamp(_sel.X + dx, 0, Math.Max(0, ClientSize.Width - _sel.Width)),
                        Clamp(_sel.Y + dy, 0, Math.Max(0, ClientSize.Height - _sel.Height)),
                        _sel.Width, _sel.Height);
                    Invalidate(); return;
                }
            }

            Tool nt;
            switch (e.KeyCode)
            {
                case Keys.V: nt = Tool.Select; break;
                case Keys.P: nt = Tool.Pen; break;
                case Keys.L: nt = Tool.Line; break;
                case Keys.A: nt = Tool.Arrow; break;
                case Keys.R: nt = Tool.Rect; break;
                case Keys.E: nt = Tool.Ellipse; break;
                case Keys.F: nt = Tool.FillRect; break;
                case Keys.I: nt = Tool.Pixelate; break;
                case Keys.N: nt = Tool.Step; break;
                case Keys.G: nt = Tool.Route; break;
                case Keys.M: nt = Tool.Marker; break;
                case Keys.T: nt = Tool.Text; break;
                case Keys.B: nt = Tool.Redact; break;
                default: return;
            }
            SetTool(nt);
        }

        // ---------- кнопки тулбара ----------

        private void OnButton(string id)
        {
            switch (id)
            {
                case "select": SetTool(Tool.Select); return;
                case "pen": SetTool(Tool.Pen); return;
                case "line": SetTool(Tool.Line); return;
                case "arrow": SetTool(Tool.Arrow); return;
                case "rect": SetTool(Tool.Rect); return;
                case "ellipse": SetTool(Tool.Ellipse); return;
                case "fillrect": SetTool(Tool.FillRect); return;
                case "pixelate": SetTool(Tool.Pixelate); return;
                case "step": SetTool(Tool.Step); return;
                case "route": SetTool(Tool.Route); return;
                case "marker": SetTool(Tool.Marker); return;
                case "text": SetTool(Tool.Text); return;
                case "redact": SetTool(Tool.Redact); return;
                case "color": _palette = !_palette; break;
                case "width": _width = _width >= 12 ? 1 : (_width + (_width < 3 ? 1 : 2)); break;
                case "undo": Undo(); return;
                case "redo": Redo(); return;
                case "copy": DoCopy(); return;
                case "save": DoQuickSave(); return;
                case "saveas": DoSaveAs(); return;
                case "print": DoPrint(); return;
                case "share": DoShare(); return;
                case "close": Close(); return;
            }
            Invalidate();
        }

        // ---------- результат ----------

        private Bitmap Flatten()
        {
            var r = SelClamped();
            if (r.Width < 1 || r.Height < 1) return null;
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.DrawImage(_shot, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
                g.TranslateTransform(-r.X, -r.Y);
                AnnRender.DrawAll(g, _anns);
            }
            return bmp;
        }

        private void DoCopy()
        {
            if (!_hasSel) { WarnEmptySelection(); return; }
            CommitText();
            FinishRoute();
            using (var flat = Flatten())
            {
                if (flat == null) { WarnEmptySelection(); return; }
                // ★ РЕЗУЛЬТАТ ВІДКИДАВСЯ. Виняток тут ловили й показували, а от `false` —
                //   ні: CopyImage має власну пастку і вміє повернути невдачу БЕЗ винятку.
                //   У цьому разі оверлей закривався з Result = Copied, трей рапортував
                //   «Скопійовано в буфер», а в буфері лишався попередній вміст. Це та сама
                //   брехня про успіх, лише на третьому з чотирьох шляхів копіювання.
                bool ok;
                try { ok = ClipboardUtil.CopyImage(flat); }
                catch (Exception ex)
                {
                    Ui.Msg(this, L.S("Буфер обміну зайнятий: ", "The clipboard is busy: ") + ex.Message, L.Name);
                    return;
                }
                if (!ok)
                {
                    Ui.Msg(this, L.S("Буфер обміну зайнятий іншою програмою — скопіювати не вдалося.",
                                     "Another program is holding the clipboard — the copy did not happen."),
                        "ValeraScreenshot", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Result = OverlayResult.Copied;
                ResultSize = flat.Size;
            }
            Close();
        }

        private void DoQuickSave()
        {
            if (!_hasSel) { WarnEmptySelection(); return; }
            CommitText();
            FinishRoute();
            using (var flat = Flatten())
            {
                if (flat == null) { WarnEmptySelection(); return; }
                try
                {
                    string path = _cfg.MakeFilePath(flat.Width, flat.Height);
                    ScreenCap.Save(flat, path, _cfg);
                    if (_cfg.CopyAfterSave) ClipboardOk = ClipboardUtil.CopyImage(flat);
                    SavedPath = path;
                }
                catch (Exception ex) { Ui.Msg(this, L.S("Не зберегти: ", "Could not save: ") + ex.Message, L.Name); return; }
                Result = OverlayResult.Saved;
                ResultSize = flat.Size;
            }
            Close();
        }

        private void DoSaveAs()
        {
            if (!_hasSel) { WarnEmptySelection(); return; }
            CommitText();
            FinishRoute();
            using (var flat = Flatten())
            {
                if (flat == null) { WarnEmptySelection(); return; }
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg";
                    dlg.FilterIndex = _cfg.Format == "jpg" ? 2 : 1;
                    try { dlg.InitialDirectory = _cfg.EffectiveSaveDir; } catch { }
                    dlg.FileName = Path.GetFileNameWithoutExtension(_cfg.MakeFilePath(flat.Width, flat.Height));
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        ScreenCap.Save(flat, dlg.FileName, _cfg);
                        if (_cfg.CopyAfterSave) ClipboardOk = ClipboardUtil.CopyImage(flat);
                        SavedPath = dlg.FileName;
                    }
                    catch (Exception ex) { Ui.Msg(this, L.S("Не зберегти: ", "Could not save: ") + ex.Message, L.Name); return; }
                }
                Result = OverlayResult.Saved;
                ResultSize = flat.Size;
            }
            Close();
        }

        // «Поділитися»: зберігає файл, кладе зображення в буфер і відкриває
        // діалог зі знайденими месенджерами / поштою.
        private void DoShare()
        {
            if (!_hasSel) { WarnEmptySelection(); return; }
            CommitText();
            FinishRoute();
            string path;
            using (var flat = Flatten())
            {
                if (flat == null) { WarnEmptySelection(); return; }
                try
                {
                    path = _cfg.MakeFilePath(flat.Width, flat.Height);
                    ScreenCap.Save(flat, path, _cfg);
                    ClipboardOk = ClipboardUtil.CopyImage(flat);
                    SavedPath = path;
                    ResultSize = flat.Size;
                }
                catch (Exception ex) { Ui.Msg(this, L.S("Не зберегти: ", "Could not save: ") + ex.Message, L.Name); return; }
            }
            // Result ставився БЕЗУМОВНО — навіть коли користувач закривав діалог хрестиком,
            // нічого не обравши. Трей після цього рапортував «Знімок у буфері й у файлі.
            // У чаті месенджера натисніть Ctrl+V» про месенджер, який ніхто не відкривав.
            // ClipboardOk передається далі: без нього ДІАЛОГ радив Ctrl+V поверх зайнятого
            // буфера — ще до того, як трей встигав чесно попередити.
            using (var f = new ShareForm(path, ClipboardOk))
            {
                f.ShowDialog(this);
                Result = f.LaunchedTarget ? OverlayResult.Shared : OverlayResult.Saved;
            }
            Close();
        }

        // Три різні шляхи тихого «нічого»: немає виділення, схлопнуте до нуля виділення, і
        // Flatten(), що повернув null. У всіх трьох оверлей просто не реагував на Ctrl+S / Ctrl+C /
        // Ctrl+P / Ctrl+D, і користувач не знав, чи програма зависла, чи він щось робить не так.
        private void WarnEmptySelection()
        {
            Ui.Msg(this, L.S("Спершу виділіть область: протягніть мишею або натисніть Ctrl+A для всього екрана.", "Select an area first: drag with the mouse, or press Ctrl+A for the whole screen."),
                "ValeraScreenshot", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DoPrint()
        {
            if (!_hasSel) { WarnEmptySelection(); return; }
            CommitText();
            FinishRoute();
            Bitmap flat = Flatten();
            // Нульове виділення поверталося без жодного слова — Ctrl+P просто не реагував.
            if (flat == null) { WarnEmptySelection(); return; }
            PrintDocument pd = null;   // створювався поза using і не звільнявся жодного разу
            try
            {
                pd = new PrintDocument();
                pd.PrintPage += delegate(object s, PrintPageEventArgs ev)
                {
                    Rectangle mb = ev.MarginBounds;
                    double k = Math.Min((double)mb.Width / flat.Width, (double)mb.Height / flat.Height);
                    if (k > 1) k = 1;
                    int w = (int)(flat.Width * k), h = (int)(flat.Height * k);
                    ev.Graphics.DrawImage(flat, mb.X, mb.Y, w, h);
                    ev.HasMorePages = false;
                };
                using (var dlg = new PrintDialog { Document = pd, UseEXDialog = true })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        pd.Print();
                        Result = OverlayResult.Printed;
                        ResultSize = flat.Size;
                        Close();
                    }
                }
            }
            catch (Exception ex) { Ui.Msg(this, L.S("Друк не вдався: ", "Printing failed: ") + ex.Message, L.Name); }
            finally { flat.Dispose(); if (pd != null) pd.Dispose(); }
        }

        // ---------- малювання ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.DrawImageUnscaled(_shot, 0, 0);

            using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                if (!_hasSel)
                {
                    g.FillRectangle(dim, ClientRectangle);
                    DrawTopHint(g);
                    DrawMagnifier(g);
                    return;
                }

                var s = SelClamped();
                int W = ClientSize.Width, H = ClientSize.Height;
                if (s.Y > 0) g.FillRectangle(dim, 0, 0, W, s.Y);
                if (s.X > 0) g.FillRectangle(dim, 0, s.Y, s.X, s.Height);
                if (W - s.Right > 0) g.FillRectangle(dim, s.Right, s.Y, W - s.Right, s.Height);
                if (H - s.Bottom > 0) g.FillRectangle(dim, 0, s.Bottom, W, H - s.Bottom);

                var st = g.Save();
                g.SetClip(s);
                AnnRender.DrawAll(g, _anns);
                if (_cur != null) { g.SmoothingMode = SmoothingMode.AntiAlias; _cur.Draw(g); }
                if (_route != null) DrawInProgressRoute(g);
                g.Restore(st);

                using (var pen = new Pen(Theme.Accent, 2f))
                    g.DrawRectangle(pen, s.X, s.Y, Math.Max(1, s.Width - 1), Math.Max(1, s.Height - 1));

                if (!_selecting)
                {
                    using (var fill = new SolidBrush(Color.White))
                    using (var pen = new Pen(Theme.Accent, 1.4f))
                        for (int i = 0; i < 8; i++)
                        {
                            var h = HandlePoint(i, s);
                            var r = new Rectangle(h.X - 4, h.Y - 4, 8, 8);
                            g.FillRectangle(fill, r);
                            g.DrawRectangle(pen, r);
                        }
                }

                DrawBadge(g, s.Width + " × " + s.Height,
                          new Point(s.X, Math.Max(4, s.Y - 26)));

                if (_selecting) DrawMagnifier(g);
                else
                {
                    if (_route != null) DrawMagnifier(g); // піксель-точне ведення маршруту
                    LayoutBar();
                    if (BarActive())
                    {
                        DrawBar(g);
                        if (_palette) DrawPalette(g);
                    }
                    if (_route != null)
                        DrawBadge(g, L.S("Точок: ", "Points: ") + _route.Points.Count + L.S("   ·   2 кліки або Enter — завершити", "   ·   double-click or Enter to finish"),
                                  new Point(_routePreview.X + 18, _routePreview.Y + 18));
                }
            }
        }

        // «Живий» маршрут: побудовані сегменти + пунктирна лінія до курсору.
        private void DrawInProgressRoute(Graphics g)
        {
            RouteRender.Draw(g, _route.Points, _color, _width, false);
            if (_route.Points.Count > 0)
            {
                var last = _route.Points[_route.Points.Count - 1];
                using (var pen = new Pen(Color.FromArgb(220, _color), Math.Max(1, _width)))
                {
                    pen.DashStyle = DashStyle.Dash;
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, last, _routePreview);
                }
                int r = Math.Max(3, 3 + _width);
                using (var ring = new Pen(Color.White, 2f))
                    g.DrawEllipse(ring, _routePreview.X - r, _routePreview.Y - r, r * 2, r * 2);
            }
        }

        private void DrawTopHint(Graphics g)
        {
            string l1 = L.S("Виділіть область мишею   ·   клік — увесь монітор   ·   Enter / 2×клік — копіювати", "Drag to select   ·   click for the whole monitor   ·   Enter / double-click to copy");
            string l2 = L.S("Інструменти: стрілка, текст, мозаїка, маршрут (G)   ·   Ctrl+S — файл   ·   Esc — вийти", "Tools: arrow, text, mosaic, route (G)   ·   Ctrl+S saves   ·   Esc exits");
            using (var f = new Font("Segoe UI", 11f))
            {
                var sz1 = g.MeasureString(l1, f);
                var sz2 = g.MeasureString(l2, f);
                int w = (int)Math.Max(sz1.Width, sz2.Width) + 40;
                int h = (int)(sz1.Height + sz2.Height) + 24;
                int x = (ClientSize.Width - w) / 2, y = 48;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Rounded(new Rectangle(x, y, w, h), 10))
                using (var bg = new SolidBrush(Color.FromArgb(190, 24, 24, 28)))
                    g.FillPath(bg, path);
                using (var b = new SolidBrush(Color.White))
                {
                    g.DrawString(l1, f, b, x + (w - sz1.Width) / 2, y + 10);
                    g.DrawString(l2, f, b, x + (w - sz2.Width) / 2, y + 12 + sz1.Height);
                }
            }
        }

        private void DrawBadge(Graphics g, string text, Point at)
        {
            using (var f = new Font("Segoe UI", 9f))
            {
                var sz = g.MeasureString(text, f);
                var r = new Rectangle(at.X, at.Y, (int)sz.Width + 14, (int)sz.Height + 6);
                r.X = Clamp(r.X, 4, Math.Max(4, ClientSize.Width - r.Width - 4));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Rounded(r, 5))
                using (var bg = new SolidBrush(Color.FromArgb(200, 24, 24, 28)))
                    g.FillPath(bg, path);
                using (var b = new SolidBrush(Color.White))
                    g.DrawString(text, f, b, r.X + 7, r.Y + 3);
            }
        }

        private void DrawMagnifier(Graphics g)
        {
            const int N = 21, ZOOM = 7;
            int half = N / 2, dw = N * ZOOM;
            int sx = Clamp(_mouse.X - half, 0, Math.Max(0, _shot.Width - N));
            int sy = Clamp(_mouse.Y - half, 0, Math.Max(0, _shot.Height - N));
            int ax = _mouse.X + 26, ay = _mouse.Y + 26;
            if (ax + dw + 8 > ClientSize.Width) ax = _mouse.X - 26 - dw;
            if (ay + dw + 34 > ClientSize.Height) ay = _mouse.Y - 26 - dw - 26;
            ax = Clamp(ax, 4, Math.Max(4, ClientSize.Width - dw - 8));
            ay = Clamp(ay, 4, Math.Max(4, ClientSize.Height - dw - 34));

            var old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_shot, new Rectangle(ax, ay, dw, dw), new Rectangle(sx, sy, N, N), GraphicsUnit.Pixel);
            g.InterpolationMode = old;

            using (var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 2f))
                g.DrawRectangle(pen, ax, ay, dw, dw);
            using (var cross = new Pen(Color.FromArgb(160, Theme.Accent)))
            {
                int cx = ax + (_mouse.X - sx) * ZOOM, cy = ay + (_mouse.Y - sy) * ZOOM;
                g.DrawRectangle(cross, cx, cy, ZOOM, ZOOM);
                g.DrawLine(cross, ax, cy + ZOOM / 2, ax + dw, cy + ZOOM / 2);
                g.DrawLine(cross, cx + ZOOM / 2, ay, cx + ZOOM / 2, ay + dw);
            }

            string info;
            try
            {
                var px = _shot.GetPixel(Clamp(_mouse.X, 0, _shot.Width - 1), Clamp(_mouse.Y, 0, _shot.Height - 1));
                info = _mouse.X + ", " + _mouse.Y + "   #" +
                       px.R.ToString("X2") + px.G.ToString("X2") + px.B.ToString("X2");
            }
            catch { info = _mouse.X + ", " + _mouse.Y; }
            if (_selecting) info += "   " + _sel.Width + "×" + _sel.Height;
            DrawBadge(g, info, new Point(ax, ay + dw + 4));
        }

        private void DrawBar(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Office-стиль: біла панель, тонкий бордер, роздільники між групами,
            // активний інструмент — блакитна підкладка з акцентним контуром.
            using (var path = Theme.Rounded(_bar, 6))
            using (var bg = new SolidBrush(Theme.CardBg))
            using (var pen = new Pen(Theme.CardBorder))
            {
                g.FillPath(bg, path);
                g.DrawPath(pen, path);
            }

            using (var div = new Pen(Theme.Divider))
                foreach (var b in _btns)
                    if (b.GroupEnd && b != _btns[_btns.Count - 1])
                    {
                        int dx = b.R.Right + 5;
                        g.DrawLine(div, dx, _bar.Y + 8, dx, _bar.Bottom - 8);
                    }

            foreach (var b in _btns)
            {
                bool active = IsActiveTool(b.Id);
                bool hover = b.Id == _hover;
                if (active)
                {
                    using (var p2 = Theme.Rounded(b.R, 4))
                    using (var f = new SolidBrush(Theme.SelectedBg))
                    using (var pn = new Pen(Theme.Accent))
                    {
                        g.FillPath(f, p2);
                        g.DrawPath(pn, p2);
                    }
                }
                else if (hover)
                {
                    using (var p2 = Theme.Rounded(b.R, 4))
                    using (var f = new SolidBrush(Theme.SubtleHover))
                        g.FillPath(f, p2);
                }
                DrawGlyph(g, b.Id, b.R);
            }

            if (_hover != null)
            {
                string hint = null;
                foreach (var b in _btns) if (b.Id == _hover) { hint = b.Hint; break; }
                if (hint != null)
                    DrawBadge(g, hint, new Point(_bar.X, _bar.Bottom + 4 + (_bar.Bottom + 30 > ClientSize.Height ? -_bar.Height - 34 : 0)));
            }
        }

        private bool IsActiveTool(string id)
        {
            switch (id)
            {
                case "select": return _tool == Tool.Select;
                case "pen": return _tool == Tool.Pen;
                case "line": return _tool == Tool.Line;
                case "arrow": return _tool == Tool.Arrow;
                case "rect": return _tool == Tool.Rect;
                case "ellipse": return _tool == Tool.Ellipse;
                case "fillrect": return _tool == Tool.FillRect;
                case "pixelate": return _tool == Tool.Pixelate;
                case "step": return _tool == Tool.Step;
                case "route": return _tool == Tool.Route;
                case "marker": return _tool == Tool.Marker;
                case "text": return _tool == Tool.Text;
                case "redact": return _tool == Tool.Redact;
                default: return false;
            }
        }

        private void DrawPalette(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(_palRect, 8))
            using (var bg = new SolidBrush(Color.FromArgb(252, Theme.CardBg)))
            using (var pen = new Pen(Theme.CardBorder))
            {
                g.FillPath(bg, path);
                g.DrawPath(pen, path);
            }
            foreach (var kv in _palCells)
            {
                using (var b = new SolidBrush(kv.Value)) g.FillRectangle(b, kv.Key);
                using (var pen = new Pen(kv.Value.ToArgb() == _color.ToArgb() ? Theme.Accent : Theme.ControlBorder,
                                         kv.Value.ToArgb() == _color.ToArgb() ? 2f : 1f))
                    g.DrawRectangle(pen, kv.Key.X, kv.Key.Y, kv.Key.Width, kv.Key.Height);
            }
            using (var f = new Font("Segoe UI", 9f))
            using (var b = new SolidBrush(Theme.Accent))
                g.DrawString(L.S("Інший колір…", "Another colour…"), f, b, _palCustom.X + 2, _palCustom.Y + 3);
        }

        // Піктограми у стилі Word 2024 / Fluent: двотон (нейтральний + синій акцент), м'які
        // заокруглені штрихи. Активний інструмент виділяє підкладка-«пігулка» в DrawBar, а не
        // перефарбування гліфа (як у стрічці Office).
        //
        // ★ БУЛИ readonly-КОНСТАНТАМИ СВІТЛОЇ ТЕМИ. Тулбар оверлея — єдина поверхня застосунку,
        //   яку тема не діставала: графітове чорнило на темній панелі просто зникало. Snipping
        //   Tool теж темнить свою панель, тож «власна контрастна семантика» тут не аргумент —
        //   панель має ВЛАСНЕ тло (Theme.CardBg), а не тло знімка, тож читається за темою.
        //   Це властивості, а не поля: палітра читається ПІД ЧАС малювання (як у Card/ToggleSwitch).
        private static Color GInk { get { return Theme.TextPrimary; } }
        private static Color GAcc { get { return Theme.Accent; } }
        private static Color GAccFill { get { return Theme.SelectedBg; } }
        // Червоний «закрити» на темному тлі дає 2.5:1 — нижче за 3:1 (WCAG 1.4.11).
        private static Color GDanger
        {
            get
            {
                return Theme.IsDark ? Color.FromArgb(0xFF, 0x7A, 0x6B) : Color.FromArgb(0xC4, 0x2B, 0x1C);
            }
        }

        private void DrawGlyph(Graphics g, string id, Rectangle r)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;

            using (var inkPen = RoundPen(GInk, 1.7f))
            using (var accPen = RoundPen(GAcc, 1.7f))
            using (var inkBrush = new SolidBrush(GInk))
            using (var accBrush = new SolidBrush(GAcc))
            using (var accFillBrush = new SolidBrush(GAccFill))
            // «Папір» усередині гліфів (аркуш, корпус принтера, картка «копіювати») — це не білий
            // колір, а ТЛО ПАНЕЛІ: у темній темі білі вставки виглядали як дірки у гліфах.
            using (var paperBrush = new SolidBrush(Theme.CardBg))
            // Те, що лежить НА акценті (цифра в кружку кроку, вузли «поділитися»), мусить читатися
            // на ньому: у темній темі акцент світлий, тож біле по світлому зникає.
            using (var onAccentBrush = new SolidBrush(Theme.AccentText))
            {
                switch (id)
                {
                    case "select":
                        g.FillPolygon(inkBrush, new[]
                        {
                            new PointF(cx-4, cy-7), new PointF(cx-4, cy+5), new PointF(cx-1, cy+2),
                            new PointF(cx+1.5f, cy+7), new PointF(cx+3.5f, cy+6), new PointF(cx+1, cy+1.5f),
                            new PointF(cx+5, cy+1)
                        });
                        break;
                    case "pen":
                        using (var body = RoundPen(GAcc, 3.2f))
                            g.DrawLine(body, cx - 4f, cy + 4f, cx + 5f, cy - 5f);
                        g.FillPolygon(inkBrush, new[] { new PointF(cx - 7f, cy + 7f), new PointF(cx - 4.6f, cy + 3.2f), new PointF(cx - 3.2f, cy + 4.6f) });
                        break;
                    case "line":
                        g.DrawLine(inkPen, cx - 6, cy + 6, cx + 6, cy - 6);
                        g.FillEllipse(accBrush, cx - 8f, cy + 4f, 4f, 4f);
                        g.FillEllipse(accBrush, cx + 4f, cy - 8f, 4f, 4f);
                        break;
                    case "arrow":
                        g.DrawLine(inkPen, cx - 6, cy + 6, cx + 3.5f, cy - 3.5f);
                        g.FillPolygon(accBrush, new[] { new PointF(cx + 6.5f, cy - 6.5f), new PointF(cx + 6f, cy - 1f), new PointF(cx + 1f, cy - 6f) });
                        break;
                    case "rect":
                        using (var p = Theme.Rounded(new Rectangle((int)(cx - 6), (int)(cy - 4.5f), 12, 9), 2))
                            g.DrawPath(inkPen, p);
                        break;
                    case "ellipse":
                        g.DrawEllipse(inkPen, cx - 6, cy - 4.5f, 12, 9);
                        break;
                    case "marker":
                        using (var swipe = RoundPen(GAccFill, 6f))
                            g.DrawLine(swipe, cx - 5, cy + 4.5f, cx + 5, cy - 3.5f);
                        using (var nib = RoundPen(GInk, 2.4f))
                            g.DrawLine(nib, cx + 2f, cy - 1.5f, cx + 6f, cy - 6f);
                        g.DrawLine(inkPen, cx - 6f, cy + 6f, cx - 2.5f, cy + 2f);
                        break;
                    case "text":
                        using (var f = new Font("Segoe UI Semibold", 12f, FontStyle.Bold))
                        {
                            var sz = g.MeasureString("A", f);
                            g.DrawString("A", f, inkBrush, cx - sz.Width / 2, cy - sz.Height / 2 - 1.5f);
                        }
                        using (var ul = RoundPen(GAcc, 2f))
                            g.DrawLine(ul, cx - 4.5f, cy + 6.5f, cx + 4.5f, cy + 6.5f);
                        break;
                    case "redact":
                        using (var p = Theme.Rounded(new Rectangle((int)(cx - 6), (int)(cy - 4.5f), 12, 9), 2))
                        using (var db = new SolidBrush(GInk))
                            g.FillPath(db, p);
                        break;
                    case "fillrect":
                        using (var p = Theme.Rounded(new Rectangle((int)(cx - 6), (int)(cy - 4.5f), 12, 9), 2))
                        {
                            g.FillPath(accFillBrush, p);
                            g.DrawPath(inkPen, p);
                        }
                        break;
                    case "pixelate":
                        for (int ix = 0; ix < 3; ix++)
                            for (int iy = 0; iy < 3; iy++)
                            {
                                var cell = new RectangleF(cx - 6.5f + ix * 4.6f, cy - 6.5f + iy * 4.6f, 3.9f, 3.9f);
                                if ((ix + iy) % 2 == 0) g.FillRectangle(inkBrush, cell);
                                else g.FillRectangle(accFillBrush, cell);
                            }
                        break;
                    case "step":
                        g.FillEllipse(accBrush, cx - 7.5f, cy - 7.5f, 15, 15);
                        using (var f = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold))
                        {
                            var sz = g.MeasureString("1", f);
                            g.DrawString("1", f, onAccentBrush, cx - sz.Width / 2 + 0.5f, cy - sz.Height / 2 + 0.5f);
                        }
                        break;
                    case "route":
                        {
                            var rpts = new[] { new PointF(cx - 7, cy + 5), new PointF(cx - 2.5f, cy - 4),
                                               new PointF(cx + 2.5f, cy + 3), new PointF(cx + 7, cy - 5) };
                            g.DrawLines(inkPen, rpts);
                            for (int i = 0; i < rpts.Length; i++)
                            {
                                float rad = (i == 0) ? 2.6f : 2f;
                                g.FillEllipse(accBrush, rpts[i].X - rad, rpts[i].Y - rad, rad * 2, rad * 2);
                            }
                        }
                        break;
                    case "share":
                        {
                            var n1 = new PointF(cx + 4.5f, cy - 5.5f);
                            var n2 = new PointF(cx - 5.5f, cy);
                            var n3 = new PointF(cx + 4.5f, cy + 5.5f);
                            g.DrawLine(inkPen, n2, n1);
                            g.DrawLine(inkPen, n2, n3);
                            foreach (var n in new[] { n1, n2, n3 })
                            {
                                g.FillEllipse(accBrush, n.X - 3f, n.Y - 3f, 6, 6);
                                g.FillEllipse(onAccentBrush, n.X - 1.2f, n.Y - 1.2f, 2.4f, 2.4f);
                            }
                        }
                        break;
                    case "color":
                        using (var cb = new SolidBrush(_color)) g.FillEllipse(cb, cx - 7, cy - 7, 14, 14);
                        using (var ring = new Pen(Theme.ControlBorder, 1.4f)) g.DrawEllipse(ring, cx - 7, cy - 7, 14, 14);
                        break;
                    case "width":
                        {
                            float d = Math.Min(14f, 3f + _width * 1.1f);
                            g.FillEllipse(inkBrush, cx - d / 2, cy - d / 2, d, d);
                        }
                        break;
                    case "undo":
                        g.DrawArc(inkPen, cx - 5.5f, cy - 5.5f, 11, 11, -40, 250);
                        g.FillPolygon(accBrush, new[] { new PointF(cx - 8, cy - 1), new PointF(cx - 2.5f, cy - 2.5f), new PointF(cx - 5.5f, cy + 3.5f) });
                        break;
                    case "redo":
                        g.DrawArc(inkPen, cx - 5.5f, cy - 5.5f, 11, 11, -30, -250);
                        g.FillPolygon(accBrush, new[] { new PointF(cx + 8, cy - 1), new PointF(cx + 2.5f, cy - 2.5f), new PointF(cx + 5.5f, cy + 3.5f) });
                        break;
                    case "copy":
                        using (var back = Theme.Rounded(new Rectangle((int)(cx - 1.5f), (int)(cy - 6.5f), 9, 11), 2))
                        {
                            g.FillPath(accFillBrush, back);
                            g.DrawPath(accPen, back);
                        }
                        using (var front = Theme.Rounded(new Rectangle((int)(cx - 6.5f), (int)(cy - 2.5f), 9, 11), 2))
                        {
                            g.FillPath(paperBrush, front);
                            g.DrawPath(inkPen, front);
                        }
                        break;
                    case "save":
                        using (var body = Theme.Rounded(new Rectangle((int)(cx - 6), (int)(cy - 6), 12, 12), 2))
                        {
                            g.FillPath(paperBrush, body);
                            g.DrawPath(inkPen, body);
                        }
                        g.FillRectangle(accBrush, cx - 1.5f, cy - 6f, 5f, 4.5f); // засувка
                        using (var lbl = Theme.Rounded(new Rectangle((int)(cx - 3.5f), (int)(cy + 1f), 7, 5), 1))
                            g.DrawPath(inkPen, lbl);
                        break;
                    case "saveas":
                        using (var body = Theme.Rounded(new Rectangle((int)(cx - 6.5f), (int)(cy - 6.5f), 11, 11), 2))
                        {
                            g.FillPath(paperBrush, body);
                            g.DrawPath(inkPen, body);
                        }
                        g.FillRectangle(accBrush, cx - 2.5f, cy - 6.5f, 4.5f, 3.5f);
                        using (var plus = RoundPen(GAcc, 2f))
                        {
                            g.DrawLine(plus, cx + 4.5f, cy + 2f, cx + 4.5f, cy + 8f);
                            g.DrawLine(plus, cx + 1.5f, cy + 5f, cx + 7.5f, cy + 5f);
                        }
                        break;
                    case "print":
                        using (var paper = Theme.Rounded(new Rectangle((int)(cx - 3.5f), (int)(cy - 7f), 7, 5), 1))
                        {
                            g.FillPath(accFillBrush, paper);
                            g.DrawPath(inkPen, paper);
                        }
                        using (var body = Theme.Rounded(new Rectangle((int)(cx - 6), (int)(cy - 1.5f), 12, 6), 2))
                        {
                            g.FillPath(paperBrush, body);
                            g.DrawPath(inkPen, body);
                        }
                        g.FillEllipse(accBrush, cx + 3f, cy + 0.5f, 2f, 2f);
                        using (var outp = Theme.Rounded(new Rectangle((int)(cx - 3.5f), (int)(cy + 3.5f), 7, 4), 1))
                        {
                            g.FillPath(paperBrush, outp);
                            g.DrawPath(inkPen, outp);
                        }
                        break;
                    case "close":
                        using (var xp = RoundPen(GDanger, 2f))
                        {
                            g.DrawLine(xp, cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f);
                            g.DrawLine(xp, cx - 4.5f, cy + 4.5f, cx + 4.5f, cy - 4.5f);
                        }
                        break;
                }
            }
        }

        private static Pen RoundPen(Color c, float w)
        {
            var p = new Pen(c, w);
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
            return p;
        }

        // ---------- QA-гачок (використовує tools\ShotTest) ----------

        internal void TestSetup(Rectangle sel, Ann[] anns, Tool active)
        {
            _hasSel = true;
            _sel = sel;
            if (anns != null) _anns.AddRange(anns);
            _tool = active;
        }

        // ---- ТЕСТ-ШВИ ДЛЯ МАТРИЦІ OVERLAY (заведено 2026-07-29) ----
        //
        // Цей файл — 1248 рядків, найбільший у продукті й головна робоча поверхня: заморожений
        // кадр, інструменти, тулбар, ручки виділення. До цього дня його стерегли НУЛЬ мутацій і
        // жодне твердження, тобто найуживаніша частина застосунку була захищена найгірше. Саме
        // це, а не окремі помилки, і давало власнику відчуття «баги на баг».
        //
        // Шви навмисно ВУЗЬКІ й читають лише те, що вже є: жодної нової логіки, жодної гілки
        // «якщо тест». Вікно при цьому НЕ показується — конструктор не потребує екрана, а
        // перевірки нижче не малюють нічого.
        internal void TestUndo() { Undo(); }
        internal void TestRedo() { Redo(); }
        internal void TestCommit(Ann a) { CommitAnn(a); }
        internal int TestAnnCount { get { return _anns.Count; } }
        internal int TestRedoCount { get { return _redo.Count; } }
        internal int TestNextStepNumber() { return NextStepNumber(); }
        internal Rectangle TestSelClamped() { return SelClamped(); }
        internal int TestHitHandle(Point p) { return HitHandle(p); }
        internal static Point TestHandlePoint(int i, Rectangle s) { return HandlePoint(i, s); }
        internal static Cursor TestHandleCursor(int i) { return HandleCursor(i); }
    }

    // Спільний буфер обміну: DIB + PNG (для застосунків із прозорістю).
    internal static class ClipboardUtil
    {
        // Повертає УСПІХ. Раніше метод був void, а всі виклики стояли в порожньому catch — і
        // застосунок після цього показував балун «Знімок у буфері». Буфер справді буває зайнятий
        // чужим процесом (RDP, Teams, менеджери буфера): вбудований ретрай (8 спроб × 120 мс)
        // тут не випадковий — автор знав про це і все одно ковтав відмову. Користувач тиснув
        // Ctrl+V і вставляв попередній вміст, вважаючи, що зламався месенджер.
        public static bool CopyImage(Bitmap b)
        {
            try
            {
                var d = new DataObject();
                d.SetData(DataFormats.Bitmap, true, b);
                using (var ms = new MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    d.SetData("PNG", false, ms);
                    Clipboard.SetDataObject(d, true, 8, 120);
                }
                return true;
            }
            catch { return false; }
        }
    }
}
