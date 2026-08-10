using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ValeraScreenshot
{
    internal enum Tool { Select, Pen, Line, Arrow, Rect, Ellipse, FillRect, Pixelate, Step, Route, Marker, Text, Redact }

    // Анотації зберігаються в координатах зображення (фізичні пікселі знімка),
    // тому однаково рендеряться і на оверлеї, і в фінальний файл.
    internal abstract class Ann : IDisposable
    {
        public Color Color;
        public int Width;

        // Більшість анотацій — чиста геометрія й нічого не тримає. Виняток — «Мозаїка», яка
        // володіє власним Bitmap. Доти його не звільняв ніхто: оверлей диспозив лише кадр, а
        // списки анотацій просто губилися разом із формою. Інструктор, що робить 50 знімків із
        // мозаїкою, накопичував сотні незвільнених GDI-обʼєктів за сесію.
        public virtual void Dispose() { }
        public abstract void Draw(Graphics g);
        public virtual bool IsDegenerate() { return false; }

        protected Pen MakePen()
        {
            var p = new Pen(Color, Width);
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            p.LineJoin = LineJoin.Round;
            return p;
        }
    }

    internal class PenAnn : Ann
    {
        public List<Point> Points = new List<Point>();
        public override bool IsDegenerate() { return Points.Count < 2; }
        public override void Draw(Graphics g)
        {
            if (Points.Count < 2) return;
            using (var p = MakePen()) g.DrawLines(p, Points.ToArray());
        }
    }

    internal class MarkerAnn : Ann
    {
        public List<Point> Points = new List<Point>();
        public override bool IsDegenerate() { return Points.Count < 2; }
        public override void Draw(Graphics g)
        {
            if (Points.Count < 2) return;
            using (var p = new Pen(Color.FromArgb(110, Color), Width * 4))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                g.DrawLines(p, Points.ToArray());
            }
        }
    }

    internal class LineAnn : Ann
    {
        public Point A, B;
        public override bool IsDegenerate() { return Dist(A, B) < 3; }
        public override void Draw(Graphics g)
        {
            using (var p = MakePen()) g.DrawLine(p, A, B);
        }
        internal static double Dist(Point a, Point b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    internal class ArrowAnn : Ann
    {
        public Point A, B;
        public override bool IsDegenerate() { return LineAnn.Dist(A, B) < 3; }
        public override void Draw(Graphics g)
        {
            double len = LineAnn.Dist(A, B);
            if (len < 1) return;
            double ux = (B.X - A.X) / len, uy = (B.Y - A.Y) / len;
            double head = Math.Max(10, Width * 4);
            if (head > len * 0.6) head = len * 0.6;
            double bx = B.X - ux * head, by = B.Y - uy * head;
            double w = head * 0.5;
            var p1 = new PointF((float)(bx - uy * w), (float)(by + ux * w));
            var p2 = new PointF((float)(bx + uy * w), (float)(by - ux * w));
            using (var pen = MakePen())
                g.DrawLine(pen, A, new Point((int)Math.Round(bx), (int)Math.Round(by)));
            using (var b = new SolidBrush(Color))
                g.FillPolygon(b, new[] { new PointF(B.X, B.Y), p1, p2 });
        }
    }

    internal class RectAnn : Ann
    {
        public Point A, B;
        public override bool IsDegenerate() { return Norm().Width < 3 && Norm().Height < 3; }
        public Rectangle Norm()
        {
            return new Rectangle(Math.Min(A.X, B.X), Math.Min(A.Y, B.Y),
                                 Math.Abs(A.X - B.X), Math.Abs(A.Y - B.Y));
        }
        public override void Draw(Graphics g)
        {
            using (var p = MakePen()) g.DrawRectangle(p, Norm());
        }
    }

    internal class EllipseAnn : RectAnn
    {
        public override void Draw(Graphics g)
        {
            var r = Norm();
            if (r.Width < 1 || r.Height < 1) return;
            using (var p = MakePen()) g.DrawEllipse(p, r);
        }
    }

    // Напівпрозора кольорова заливка — підсвітити зону, не ховаючи вміст.
    internal class FillRectAnn : RectAnn
    {
        public override void Draw(Graphics g)
        {
            var r = Norm();
            if (r.Width < 1 || r.Height < 1) return;
            using (var b = new SolidBrush(Color.FromArgb(70, Color)))
                g.FillRectangle(b, r);
            using (var p = new Pen(Color, 1.5f))
                g.DrawRectangle(p, r);
        }
    }

    // Мозаїка (пікселізація): при фіксації бере регіон зі знімка, зменшує і
    // розтягує назад без згладжування. Tile обчислюється один раз у Bake().
    internal class PixelateAnn : RectAnn
    {
        public Bitmap Tile;

        public override void Dispose()
        {
            if (Tile != null) { Tile.Dispose(); Tile = null; }
        }

        // Bake() відмовляється пекти тайл, коли БУДЬ-ЯКИЙ вимір < 2, а успадкований предикат
        // відкидав фігуру лише коли ОБИДВА < 3. Тяга 100x1 прослизала в щілину між ними:
        // анотація фіксувалася БЕЗ Tile, і Draw() малював у ЕКСПОРТОВАНИЙ файл білий пунктир
        // прев'ю, нічого не пікселюючи. Предикати мусять збігатися: занадто тонка мозаїка —
        // вироджена, і тяга просто не фіксується, як у решти вироджених фігур.
        public override bool IsDegenerate()
        {
            var r = Norm();
            return r.Width < 2 || r.Height < 2 || base.IsDegenerate();
        }

        public void Bake(Bitmap src)
        {
            var r = Rectangle.Intersect(Norm(), new Rectangle(0, 0, src.Width, src.Height));
            if (r.Width < 2 || r.Height < 2) return;
            int block = Math.Max(6, Width * 4);
            int tw = Math.Max(1, r.Width / block), th = Math.Max(1, r.Height / block);
            var t = new Bitmap(tw, th);
            using (var g = Graphics.FromImage(t))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, new Rectangle(0, 0, tw, th), r, GraphicsUnit.Pixel);
            }
            // Старий тайл звільняємо ПЕРЕД заміною. Сьогодні Bake кличеться раз на завершену
            // тягу (OverlayForm.OnMouseUp, після чого _cur обнуляється), тож витоку немає — але
            // метод ПУБЛІЧНИЙ і перезаписував поле беззастережно. Другий виклик (перепікання при
            // зміні виділення — цілком імовірна майбутня фіча) мовчки лишив би попередній Bitmap
            // до кінця процесу. Саме цей клас витоку цей файл уже возив одного разу: тайл не
            // звільняв ніхто, і година роботи з мозаїкою з'їдала сотні GDI-об'єктів.
            if (Tile != null) { try { Tile.Dispose(); } catch { } }
            Tile = t;
        }

        public override void Draw(Graphics g)
        {
            var r = Norm();
            if (r.Width < 1 || r.Height < 1) return;
            if (Tile == null)
            {
                // прев'ю під час тяги
                using (var p = new Pen(Color.FromArgb(200, 255, 255, 255), 1.5f) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(p, r);
                return;
            }
            var oldI = g.InterpolationMode;
            var oldP = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(Tile, r, new Rectangle(0, 0, Tile.Width, Tile.Height), GraphicsUnit.Pixel);
            g.InterpolationMode = oldI;
            g.PixelOffsetMode = oldP;
        }
    }

    // Нумерований крок: кружок із числом (1, 2, 3…) — для інструкцій.
    internal class StepAnn : Ann
    {
        public Point Pos;
        public int Number = 1;
        public int Diameter { get { return 26 + Width * 3; } }

        public override void Draw(Graphics g)
        {
            int d = Diameter;
            var r = new Rectangle(Pos.X - d / 2, Pos.Y - d / 2, d, d);
            using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillEllipse(shadow, r.X + 2, r.Y + 2, d, d);
            using (var b = new SolidBrush(Color))
                g.FillEllipse(b, r);
            using (var ring = new Pen(Color.White, 2f))
                g.DrawEllipse(ring, r);
            using (var f = new Font("Segoe UI", d * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var tb = new SolidBrush(Color.White))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(Number.ToString(), f, tb, new RectangleF(r.X, r.Y + 1, d, d), sf);
            }
        }
    }

    // Маршрут: ламана з вузлів (полілінія). Точки в координатах зображення.
    // Будується кліками, тому окремий тип, а не Pen (той тягнеться мишею).
    internal class RouteAnn : Ann
    {
        public List<Point> Points = new List<Point>();
        public override bool IsDegenerate() { return Points.Count < 2; }

        public double PixelLength()
        {
            double s = 0;
            for (int i = 1; i < Points.Count; i++) s += LineAnn.Dist(Points[i - 1], Points[i]);
            return s;
        }

        public override void Draw(Graphics g) { RouteRender.Draw(g, Points, Color, Width, true); }
    }

    // Спільний рендер маршруту — використовується і для фінальної анотації,
    // і для «живого» маршруту в оверлеї (drawArrow=false під час побудови).
    internal static class RouteRender
    {
        public static void Draw(Graphics g, IList<Point> pts, Color color, int width, bool drawArrow)
        {
            if (pts == null || pts.Count == 0) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (pts.Count >= 2)
            {
                // легка «тінь» під лінією для контрасту на строкатій карті
                using (var halo = new Pen(Color.FromArgb(90, 0, 0, 0), width + 2f))
                {
                    halo.StartCap = LineCap.Round; halo.EndCap = LineCap.Round; halo.LineJoin = LineJoin.Round;
                    g.DrawLines(halo, ToArray(pts));
                }
                using (var pen = new Pen(color, width))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, ToArray(pts));
                }
                if (drawArrow) Head(g, pts[pts.Count - 2], pts[pts.Count - 1], color, width);
            }

            int r = Math.Max(3, 3 + width);
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                Color fill = (i == 0) ? Color.FromArgb(0x16, 0xC6, 0x0C) : color; // старт — зелений
                using (var b = new SolidBrush(fill))
                    g.FillEllipse(b, p.X - r, p.Y - r, r * 2, r * 2);
                using (var ring = new Pen(Color.White, 2f))
                    g.DrawEllipse(ring, p.X - r, p.Y - r, r * 2, r * 2);
            }
            g.SmoothingMode = old;
        }

        private static Point[] ToArray(IList<Point> pts)
        {
            var a = new Point[pts.Count];
            for (int i = 0; i < pts.Count; i++) a[i] = pts[i];
            return a;
        }

        private static void Head(Graphics g, Point a, Point b, Color color, int width)
        {
            double len = LineAnn.Dist(a, b);
            if (len < 1) return;
            double ux = (b.X - a.X) / len, uy = (b.Y - a.Y) / len;
            double head = Math.Max(12, width * 4);
            if (head > len) head = len;
            double bx = b.X - ux * head, by = b.Y - uy * head;
            double w = head * 0.5;
            var p1 = new PointF((float)(bx - uy * w), (float)(by + ux * w));
            var p2 = new PointF((float)(bx + uy * w), (float)(by - ux * w));
            using (var br = new SolidBrush(color))
                g.FillPolygon(br, new[] { new PointF(b.X, b.Y), p1, p2 });
        }
    }

    // Суцільна заливка чорним — незворотне приховання фрагмента (для чутливих даних).
    internal class RedactAnn : RectAnn
    {
        public override void Draw(Graphics g)
        {
            var r = Norm();
            if (r.Width < 1 || r.Height < 1) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;
            using (var b = new SolidBrush(Color.Black))
                g.FillRectangle(b, r.X, r.Y, r.Width + 1, r.Height + 1);
            g.SmoothingMode = old;
        }
    }

    internal class TextAnn : Ann
    {
        public Point Pos;
        public string Text = "";
        public override bool IsDegenerate() { return Text == null || Text.Trim().Length == 0; }
        public float FontSize { get { return 10f + Width * 2.5f; } }
        public override void Draw(Graphics g)
        {
            if (IsDegenerate()) return;
            using (var f = new Font("Segoe UI", FontSize, FontStyle.Bold, GraphicsUnit.Point))
            using (var b = new SolidBrush(Color))
            using (var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.DrawString(Text, f, shadow, Pos.X + 1, Pos.Y + 1);
                g.DrawString(Text, f, b, Pos.X, Pos.Y);
            }
        }
    }

    internal static class AnnRender
    {
        public static void DrawAll(Graphics g, IEnumerable<Ann> anns)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var a in anns) a.Draw(g);
        }
    }
}
