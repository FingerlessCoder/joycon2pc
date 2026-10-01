
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Joycon2PC.App
{
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  THEME  (single source of truth for palette, type scale, spacing rhythm and GDI+ helpers)
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    /// <summary>
    /// Owns every colour, font and spacing value the UI uses, plus the small painting
    /// primitives shared by all custom controls. One instance per window; dispose it
    /// when the form goes away so the GDI font handles are released.
    /// </summary>
    internal sealed class UiTheme : IDisposable
    {
        // ------ surfaces: cool, near-black, layered from window to well ------
        /// <summary>Window canvas - the deepest layer.</summary>
        public readonly Color Root    = Hex(0x0C0E12);
        /// <summary>Card / panel surface - the main elevated plane.</summary>
        public readonly Color Surface = Hex(0x14171D);
        /// <summary>Inputs, hover rows, pressed chips.</summary>
        public readonly Color Raised  = Hex(0x1C212A);
        /// <summary>Sunken wells: log text area, status bar.</summary>
        public readonly Color Sunken  = Hex(0x0E1116);
        /// <summary>Header band, subtly separated from the canvas.</summary>
        public readonly Color Overlay = Hex(0x101319);

        // ------ lines ------
        public readonly Color Line       = Hex(0x252B36);
        public readonly Color LineSoft   = Hex(0x1B2029);
        public readonly Color LineStrong = Hex(0x364052);

        // ------ accent: one teal, used for primary actions and live state ------
        public readonly Color Accent     = Hex(0x2ED9C3);
        public readonly Color AccentDeep = Hex(0x12A594);
        public readonly Color AccentLift = Hex(0x19BFA9);
        public readonly Color AccentWash = Hex(0x0F2A28);

        // ------ restrained semantic set ------
        public readonly Color Ok   = Hex(0x3FD99C);
        public readonly Color Warn = Hex(0xF5B942);
        public readonly Color Err  = Hex(0xFF6B6B);
        public readonly Color Idle = Hex(0x5A6474);

        // ------ type ------
        public readonly Color Text      = Hex(0xE9ECF3);
        public readonly Color TextSoft  = Hex(0xA6AEBB);
        public readonly Color TextFaint = Hex(0x7C8697);

        // ------ joy-con identity (matches the physical hardware) ------
        public readonly Color RailL = Hex(0x4C9BE8);
        public readonly Color RailR = Hex(0xE8683C);

        // ------ type scale, in points (points auto-scale with DPI) ------
        public readonly Font FDisplay;   // 16.0  window wordmark
        public readonly Font FTitle;     // 10.5  card headline / big status value
        public readonly Font FSection;   //  7.75 tracked uppercase section caption
        public readonly Font FBody;      //  9.0  body copy
        public readonly Font FBodyBold;  //  9.0  body emphasis
        public readonly Font FSmall;     //  8.25 field captions, combo text
        public readonly Font FSmallBold; //  8.25 field captions, emphasis, chips
        public readonly Font FButton;    //  9.25 buttons
        public readonly Font FButtonBig; // 11.5  primary action
        public readonly Font FMono;      //  8.5  log + telemetry
        public readonly Font FMonoBold;  //  8.5  telemetry emphasis

        // ------ spacing rhythm (design pixels, multiplied by the DPI scale) ------
        // Every gap, pad and inset in the window is a multiple of 4 drawn from
        // {4, 8, 12, 16, 20, 24}. The named band sizes live on MainForm.

        // ------ corner radii (design pixels) ------
        public const int RadiusCard = 6;
        public const int RadiusField = 4;

        public UiTheme()
        {
            FDisplay   = new Font("Segoe UI Semibold", 16.0f, FontStyle.Regular, GraphicsUnit.Point);
            FTitle     = new Font("Segoe UI Semibold", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
            FSection   = new Font("Segoe UI Semibold",  7.75f, FontStyle.Regular, GraphicsUnit.Point);
            FBody      = new Font("Segoe UI",           9.0f, FontStyle.Regular, GraphicsUnit.Point);
            FBodyBold  = new Font("Segoe UI Semibold",  9.0f, FontStyle.Regular, GraphicsUnit.Point);
            FSmall     = new Font("Segoe UI",           8.25f, FontStyle.Regular, GraphicsUnit.Point);
            FSmallBold = new Font("Segoe UI Semibold",  8.25f, FontStyle.Regular, GraphicsUnit.Point);
            FButton    = new Font("Segoe UI Semibold",  9.25f, FontStyle.Regular, GraphicsUnit.Point);
            FButtonBig = new Font("Segoe UI Semibold", 11.5f, FontStyle.Regular, GraphicsUnit.Point);
            FMono      = new Font("Consolas",           8.5f, FontStyle.Regular, GraphicsUnit.Point);
            FMonoBold  = new Font("Consolas",           8.5f, FontStyle.Bold,    GraphicsUnit.Point);
        }

        public void Dispose()
        {
            FDisplay.Dispose();
            FTitle.Dispose();
            FSection.Dispose();
            FBody.Dispose();
            FBodyBold.Dispose();
            FSmall.Dispose();
            FSmallBold.Dispose();
            FButton.Dispose();
            FButtonBig.Dispose();
            FMono.Dispose();
            FMonoBold.Dispose();
        }

        public static Color Hex(int rgb)
            => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  METRICS  (design units -> device pixels)
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    /// <summary>
    /// The one place a design-pixel number becomes a real pixel. Every custom control holds
    /// one of these and is told the current scale by <see cref="MainForm"/>, so a single
    /// DPI change re-proportions the whole UI.
    /// </summary>
    internal sealed class UiMetrics
    {
        public UiTheme? Theme = null;
        public float Scale = 1f;

        public int S(int design) => (int)Math.Round(design * Scale, MidpointRounding.AwayFromZero);
        public float F(float design) => design * Scale;
        public float Radius(int design) => Math.Max(0.5f, design * Scale);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  PAINT  (shared GDI/GDI+ helpers)
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    internal static class Ink
    {
        internal const TextFormatFlags Tight =
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        /// <summary>
        /// The nearest real ancestor background. Custom controls must not clear with a
        /// transparent parent colour - Graphics.Clear(Color.Transparent) paints black.
        /// </summary>
        internal static Color ParentColor(Control? c, Color fallback)
        {
            while (c != null)
            {
                if (c.BackColor.A != 0) return c.BackColor;
                c = c.Parent;
            }
            return fallback;
        }

        internal static void Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }

        // ------ paths ------

        /// <summary>Rounded rectangle with independent top/bottom corner radii (design units).</summary>
        internal static GraphicsPath Round(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            if (w <= 0 || h <= 0) return p;
            float rt = Math.Max(0f, Math.Min(r, Math.Min(w, h) * 0.5f));
            if (rt <= 0.01f)
            {
                p.AddRectangle(new RectangleF(x, y, w, h));
                return p;
            }
            float d = rt * 2f;
            p.AddArc(x,         y,         d, d, 180, 90);
            p.AddArc(x + w - d, y,         d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d,   0, 90);
            p.AddArc(x,         y + h - d, d, d,  90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Rounded rectangle whose bottom corners are deeper than its top corners - the Joy-Con silhouette.</summary>
        internal static GraphicsPath Silhouette(float x, float y, float w, float h, float rTop, float rBottom)
        {
            var p = new GraphicsPath();
            float rt = Math.Max(0f, Math.Min(rTop, w * 0.5f));
            float rb = Math.Max(0f, Math.Min(rBottom, Math.Min(w * 0.5f, h * 0.5f)));
            float dt = rt * 2f, db = rb * 2f;
            p.AddArc(x,         y,         dt, dt, 180, 90);
            p.AddArc(x + w - dt, y,        dt, dt, 270, 90);
            p.AddArc(x + w - db, y + h - db, db, db, 0, 90);
            p.AddArc(x,          y + h - db, db, db, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ------ fills ------

        internal static void Fill(Graphics g, RectangleF r, float radius, Color color)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            if (radius <= 0.01f)
            {
                using var b = new SolidBrush(color);
                g.FillRectangle(b, r);
                return;
            }
            using var path = Round(r.X, r.Y, r.Width, r.Height, radius);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }

        internal static void FillPath(Graphics g, GraphicsPath path, Color color)
        {
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }

        internal static void Outline(Graphics g, RectangleF r, float radius, Color color, float width = 1f)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using var pen = new Pen(color, width);
            if (radius <= 0.01f)
            {
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                return;
            }
            using var path = Round(r.X, r.Y, r.Width, r.Height, radius);
            g.DrawPath(pen, path);
        }

        /// <summary>Vertical two-stop fill - the main way surfaces get volume.</summary>
        internal static void FillVertical(Graphics g, RectangleF r, float radius, Color top, Color bottom)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            if (top == bottom) { Fill(g, r, radius, top); return; }
            using var path = Round(r.X, r.Y, r.Width, r.Height, radius);
            // rect-bounded gradient: it cannot tile past the ends, which is what a
            // two-point LinearGradientBrush does and shows up as a visible band.
            using var brush = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical);
            g.FillPath(brush, path);
        }

        /// <summary>Soft halo behind a shape: stacked translucent rings, widest first.</summary>
        internal static void Glow(Graphics g, RectangleF r, float radius, Color color, int layers, float spread)
        {
            for (int i = layers; i >= 1; i--)
            {
                var grown = RectangleF.Inflate(r, spread * i, spread * i);
                Fill(g, grown, radius + spread * i, Alpha(color, color.A / (layers + 1)));
            }
        }

        internal static void Line(Graphics g, float x1, float y1, float x2, float y2, Color color, float width = 1f)
        {
            using var pen = new Pen(color, width);
            g.DrawLine(pen, x1, y1, x2, y2);
        }

        // ------ colour maths ------

        internal static Color Alpha(Color c, int a) => Color.FromArgb(Math.Clamp(a, 0, 255), c.R, c.G, c.B);
        internal static Color Alpha(Color c, float f) => Color.FromArgb(Math.Clamp((int)(c.A * f), 0, 255), c.R, c.G, c.B);
        internal static Color Mix(Color a, Color b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }
        internal static Color Lift(Color c, float t) => Mix(c, Color.White, t);
        internal static Color Sink(Color c, float t) => Mix(c, Color.Black, t);

        // ------ text (GDI, so custom controls match Label rendering exactly) ------

        internal static SizeF Measure(Graphics g, string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return SizeF.Empty;
            return TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), Tight);
        }

        internal static void Text(Graphics g, string text, Font font, RectangleF area, Color color,
                                 StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center)
        {
            if (string.IsNullOrEmpty(text)) return;
            var sz = Measure(g, text, font);
            float x = h switch
            {
                StringAlignment.Center => area.X + (area.Width - sz.Width) * 0.5f,
                StringAlignment.Far    => area.Right - sz.Width,
                _                       => area.X,
            };
            float y = v switch
            {
                StringAlignment.Center => area.Y + (area.Height - sz.Height) * 0.5f,
                StringAlignment.Far    => area.Bottom - sz.Height,
                _                       => area.Y,
            };
            TextAt(g, text, font, x, y, color);
        }

        internal static void TextAt(Graphics g, string text, Font font, float x, float y, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            TextRenderer.DrawText(g, text, font, Point.Round(new PointF(x, y)), color, Tight);
        }

        /// <summary>Letter-spaced text. The section-caption look: wide tracking turns a plain label into a heading.</summary>
        internal static float TrackedWidth(Graphics g, string text, Font font, float tracking)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            float total = 0f;
            for (int i = 0; i < text.Length; i++)
                total += Measure(g, text[i].ToString(), font).Width + tracking;
            return Math.Max(0f, total - tracking);
        }

        internal static void Tracked(Graphics g, string text, Font font, RectangleF area, Color color,
                                     float tracking, StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center)
        {
            if (string.IsNullOrEmpty(text)) return;
            float total = TrackedWidth(g, text, font, tracking);
            float x = h switch
            {
                StringAlignment.Center => area.X + (area.Width - total) * 0.5f,
                StringAlignment.Far    => area.Right - total,
                _                       => area.X,
            };
            var sz = Measure(g, text, font);
            float y = v switch
            {
                StringAlignment.Center => area.Y + (area.Height - sz.Height) * 0.5f,
                StringAlignment.Far    => area.Bottom - sz.Height,
                _                       => area.Y,
            };
            for (int i = 0; i < text.Length; i++)
            {
                string ch = text[i].ToString();
                TextAt(g, ch, font, x, y, color);
                x += Measure(g, ch, font).Width + tracking;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  GLYPHS  (small vector icons, so buttons do not have to lean on Segoe MDL2)
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    internal enum Glyph
    {
        None,
        Link,
        Refresh,
        Speaker,
        Bulb,
        Download,
        Copy,
        Activity,
    }

    internal static class Glyphs
    {
        /// <summary>Draws an icon inside <paramref name="box"/> (square, centred). Stroke width is auto-scaled.</summary>
        internal static void Draw(Graphics g, Glyph glyph, RectangleF box, Color color, float weight = 1.6f)
        {
            if (glyph == Glyph.None || box.Width <= 0) return;
            float cx = box.X + box.Width * 0.5f;
            float cy = box.Y + box.Height * 0.5f;
            float s = Math.Min(box.Width, box.Height);
            float u = s / 16f;            // 16-unit design grid
            float w = Math.Max(1f, weight * Math.Max(0.75f, u));

            using var pen = new Pen(color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var brush = new SolidBrush(color);

            switch (glyph)
            {
                case Glyph.Link:
                    // Two interlocking chain links, drawn as rounded rectangles.
                    using (var p1 = Ink.Round(cx - u * 6.2f, cy - u * 3.4f, u * 7.4f, u * 6.8f, u * 3.4f))
                        g.DrawPath(pen, p1);
                    using (var p2 = Ink.Round(cx - u * 1.2f, cy - u * 3.4f, u * 7.4f, u * 6.8f, u * 3.4f))
                        g.DrawPath(pen, p2);
                    g.DrawLine(pen, cx - u * 2.2f, cy, cx + u * 2.2f, cy);
                    break;

                case Glyph.Refresh:
                    g.DrawArc(pen, cx - u * 5.4f, cy - u * 5.4f, u * 10.8f, u * 10.8f, 40f, 250f);
                    g.FillPolygon(brush, new[]
                    {
                        new PointF(cx + u * 2.2f, cy - u * 6.4f),
                        new PointF(cx + u * 6.6f, cy - u * 5.0f),
                        new PointF(cx + u * 4.4f, cy - u * 1.0f),
                    });
                    break;

                case Glyph.Speaker:
                    using (var cone = new GraphicsPath())
                    {
                        cone.AddPolygon(new[]
                        {
                            new PointF(cx - u * 5.6f, cy - u * 2.0f),
                            new PointF(cx - u * 2.4f, cy - u * 2.0f),
                            new PointF(cx + u * 1.2f, cy - u * 5.4f),
                            new PointF(cx + u * 1.2f, cy + u * 5.4f),
                            new PointF(cx - u * 2.4f, cy + u * 2.0f),
                            new PointF(cx - u * 5.6f, cy + u * 2.0f),
                        });
                        cone.CloseFigure();
                        g.FillPath(brush, cone);
                    }
                    g.DrawArc(pen, cx + u * 1.0f, cy - u * 3.4f, u * 4.2f, u * 6.8f, -55f, 110f);
                    g.DrawArc(pen, cx + u * 1.0f, cy - u * 1.6f, u * 6.4f, u * 3.2f, -55f, 110f);
                    break;

                case Glyph.Bulb:
                    g.DrawArc(pen, cx - u * 4.2f, cy - u * 6.0f, u * 8.4f, u * 8.4f, 200f, 140f);
                    g.DrawArc(pen, cx - u * 4.2f, cy - u * 6.0f, u * 8.4f, u * 8.4f, 20f, 140f);
                    g.DrawLine(pen, cx - u * 2.2f, cy + u * 1.9f, cx - u * 2.2f, cy + u * 4.4f);
                    g.DrawLine(pen, cx + u * 2.2f, cy + u * 1.9f, cx + u * 2.2f, cy + u * 4.4f);
                    g.DrawLine(pen, cx - u * 2.6f, cy + u * 5.4f, cx + u * 2.6f, cy + u * 5.4f);
                    break;

                case Glyph.Download:
                    g.DrawLine(pen, cx, cy - u * 5.4f, cx, cy + u * 1.6f);
                    g.DrawLines(pen, new[]
                    {
                        new PointF(cx - u * 3.4f, cy - u * 1.4f),
                        new PointF(cx, cy + u * 2.0f),
                        new PointF(cx + u * 3.4f, cy - u * 1.4f),
                    });
                    g.DrawLine(pen, cx - u * 5.6f, cy + u * 5.0f, cx + u * 5.6f, cy + u * 5.0f);
                    break;

                case Glyph.Copy:
                    using (var back = Ink.Round(cx - u * 5.6f, cy - u * 5.6f, u * 7.6f, u * 7.6f, u * 1.4f))
                        g.DrawPath(pen, back);
                    using (var front = Ink.Round(cx - u * 2.0f, cy - u * 2.0f, u * 7.6f, u * 7.6f, u * 1.4f))
                        g.FillPath(brush, front);
                    break;

                case Glyph.Activity:
                    g.DrawLines(pen, new[]
                    {
                        new PointF(cx - u * 6.0f, cy),
                        new PointF(cx - u * 3.0f, cy),
                        new PointF(cx - u * 1.0f, cy - u * 4.4f),
                        new PointF(cx + u * 1.6f, cy + u * 4.4f),
                        new PointF(cx + u * 3.6f, cy),
                        new PointF(cx + u * 6.0f, cy),
                    });
                    break;
            }
        }
    }
}
