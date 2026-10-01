
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Joycon2PC.ViGEm;

namespace Joycon2PC.App
{
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  JOY-CON 2 VISUALIZER  (custom-drawn GDI+ panel)
    //
    //  Everything is authored in a fixed DESIGN coordinate space and mapped onto the panel
    //  with one uniform transform, so the diagram scales and stays centred at any size or
    //  DPI instead of pinning itself to hardcoded pixels. Shapes use the world transform;
    //  text is drawn unscaled because GDI+ already applies the monitor DPI to point sizes.
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    internal sealed class JoyConVisualizerPanel : Panel
    {
        // ---- design-space canvas (units are "design pixels", not screen pixels) ----
        private const float DESIGN_W = 404f;
        private const float DESIGN_H = 344f;
        private const float PAD_X = 18f;
        private const float BODY_W = 152f;
        private const float BODY_H = 252f;
        private const float GAP_X = 64f;
        private const float SHOULDER_Y = 16f;
        private const float SHOULDER_H = 38f;
        private const float BODY_Y = SHOULDER_Y + SHOULDER_H + 6f;   // 60
        private const float CAPTION_Y = BODY_Y + BODY_H + 8f;        // 320
        private const float LEFT_X = PAD_X;
        private const float RIGHT_X = PAD_X + BODY_W + GAP_X;        // 234

        // ---- state ----
        private readonly Dictionary<string, bool> _p = new(StringComparer.Ordinal);
        private float _lx, _ly, _rx, _ry;

        // ---- transform cache ----
        private float _k = 1f;
        private float _ox, _oy;

        // ---- fonts: allocated once, never per paint ----
        private Font _fButton = null!;     // face buttons, stick captions
        private Font _fShoulder = null!;   // ZL / L / ZR / R
        private Font _fSide = null!;       // big L / R badge on the body
        private Font _fCaption = null!;    // JOY-CON (L) under each controller
        private readonly UiMetrics _m = new();

        private UiTheme T => _m.Theme!;

        // ---- one accent for "this button is down" everywhere: unambiguous at a glance ----
        private static readonly Dictionary<string, Color> _faceHues = new(StringComparer.Ordinal)
        {
            ["X"] = Color.FromArgb(0x5B, 0x9B, 0xD5),
            ["Y"] = Color.FromArgb(0xE8, 0x61, 0x5C),
            ["A"] = Color.FromArgb(0x4F, 0xBF, 0x7B),
            ["B"] = Color.FromArgb(0xE3, 0xB3, 0x41),
        };

        internal UiMetrics Metrics => _m;

        public JoyConVisualizerPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.FromArgb(0x14, 0x17, 0x1D);
        }

        public void SetButton(string name, bool pressed)
        {
            if (_p.TryGetValue(name, out var cur) && cur == pressed) return;
            _p[name] = pressed;
            Invalidate();
        }

        public void SetSticks(int rawLX, int rawLY, int rawRX, int rawRY)
        {
            // Apply the same deadzone math as ViGEmBridge so the visualiser dot
            // stays centred for any stick position that produces zero ViGEm output.
            // Reuse shared constant to keep UI and output mapping in sync.
            static float Apply(int raw)
            {
                const float C = 1998f, R = 1251f;
                const float DZ = ViGEmBridge.AxisOutputDeadzone;
                const float MAX = 32767f, RANGE = MAX - DZ;
                float v = Math.Clamp((raw - C) / R, -1f, 1f);
                float i16 = v * MAX;
                float abs = Math.Abs(i16);
                if (abs <= DZ) return 0f;
                return (i16 < 0f ? -1f : 1f) * (abs - DZ) / RANGE;
            }
            float lx = Apply(rawLX);
            float ly = Apply(rawLY);
            float rx = Apply(rawRX);
            float ry = Apply(rawRY);
            if (lx == _lx && ly == _ly && rx == _rx && ry == _ry) return;
            _lx = lx; _ly = ly; _rx = rx; _ry = ry;
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            EnsureFonts();
        }

        private void EnsureFonts()
        {
            if (_fButton != null) return;
            _fButton   = new Font("Segoe UI Semibold", 6.4f, FontStyle.Regular, GraphicsUnit.Point);
            _fShoulder = new Font("Segoe UI Semibold", 7.0f, FontStyle.Regular, GraphicsUnit.Point);
            _fSide     = new Font("Segoe UI Semibold", 10.5f, FontStyle.Regular, GraphicsUnit.Point);
            _fCaption  = new Font("Segoe UI Semibold", 6.8f, FontStyle.Regular, GraphicsUnit.Point);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fButton.Dispose();
                _fShoulder.Dispose();
                _fSide.Dispose();
                _fCaption.Dispose();
            }
            base.Dispose(disposing);
        }

        private bool On(string b) => _p.TryGetValue(b, out var v) && v;

        // ------------------------------------------------------------------ paint

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ink.Prepare(g);
            g.Clear(BackColor);
            EnsureFonts();

            // ---- uniform design -> device mapping, always centred, never clipped ----
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w <= 0 || h <= 0) return;
            _k = Math.Min(w / DESIGN_W, h / DESIGN_H);
            if (_k <= 0f) return;
            _ox = (w - DESIGN_W * _k) * 0.5f;
            _oy = (h - DESIGN_H * _k) * 0.5f;

            DrawBackdrop(g);

            var saved = g.Transform;
            g.TranslateTransform(_ox, _oy);
            g.ScaleTransform(_k, _k);

            DrawLJoyCon(g);
            DrawRJoyCon(g);

            g.Transform = saved;
        }

        /// <summary>Soft pools of accent light behind each controller plus a contact shadow, so the pair has presence.</summary>
        private void DrawBackdrop(Graphics g)
        {
            float cxl = _ox + (LEFT_X + BODY_W * 0.5f) * _k;
            float cxr = _ox + (RIGHT_X + BODY_W * 0.5f) * _k;
            float cy = _oy + (BODY_Y + BODY_H * 0.5f) * _k;
            float rx = BODY_W * 0.86f * _k;
            float ry = BODY_H * 0.62f * _k;

            using (var pool = new GraphicsPath())
            {
                pool.AddEllipse(cxl - rx, cy - ry, rx * 2, ry * 2);
                using var b = new PathGradientBrush(pool)
                {
                    CenterColor = Ink.Alpha(T.Accent, 20),
                    SurroundColors = new[] { Ink.Alpha(T.Accent, 0) },
                };
                g.FillPath(b, pool);
            }
            using (var pool = new GraphicsPath())
            {
                pool.AddEllipse(cxr - rx, cy - ry, rx * 2, ry * 2);
                using var b = new PathGradientBrush(pool)
                {
                    CenterColor = Ink.Alpha(T.RailR, 18),
                    SurroundColors = new[] { Ink.Alpha(T.RailR, 0) },
                };
                g.FillPath(b, pool);
            }
        }

        // ------------------------------------------------------------------ L controller

        private void DrawLJoyCon(Graphics g)
        {
            float x = LEFT_X, y = BODY_Y, w = BODY_W, h = BODY_H;
            var rail = T.RailL;

            DrawShoulder(g, x, SHOULDER_Y, w, rail, "ZL", "L");
            DrawBody(g, x, y, w, h, rail, railOnRight: true);

            // Minus
            CircBtn(g, x + 34f, y + 34f, 8.5f, "-", On("-"), null);

            // Left stick
            StickViz(g, x + 46f, y + 66f, 26f, _lx, _ly, rail, On("LS"));
            DesignText(g, "LS", _fButton, Ink.Alpha(T.TextFaint, 0.95f), x + 46f, y + 100f);

            // Player LED pips (hardware detail, not stateful)
            for (int i = 0; i < 4; i++)
                LedPip(g, x + w - 21f, y + 60f + i * 11f);

            // D-pad
            DrawDPad(g, x + 58f, y + 170f);

            // Capture
            CircBtn(g, x + 112f, y + 172f, 12f, "Cap", On("Cap"), null, Icon.Shutter);

            DrawSideBadge(g, x, y, w, h, "L", rail);
            DesignText(g, "JOY-CON (L)", _fCaption, T.TextFaint, x + w * 0.5f, CAPTION_Y);
        }

        // ------------------------------------------------------------------ R controller

        private void DrawRJoyCon(Graphics g)
        {
            float x = RIGHT_X, y = BODY_Y, w = BODY_W, h = BODY_H;
            var rail = T.RailR;

            DrawShoulder(g, x, SHOULDER_Y, w, rail, "ZR", "R");
            DrawBody(g, x, y, w, h, rail, railOnRight: false);

            // Plus
            CircBtn(g, x + 34f, y + 34f, 8.5f, "+", On("+"), null);

            // ABXY diamond: X top, Y left, A right, B bottom
            const float dR = 11.5f, dGap = 21f;
            float dx = x + 104f, dy = y + 58f;
            CircBtn(g, dx,          dy - dGap, dR, "X", On("X"), _faceHues["X"]);
            CircBtn(g, dx - dGap,   dy,       dR, "Y", On("Y"), _faceHues["Y"]);
            CircBtn(g, dx + dGap,   dy,       dR, "A", On("A"), _faceHues["A"]);
            CircBtn(g, dx,          dy + dGap, dR, "B", On("B"), _faceHues["B"]);

            // Right stick
            StickViz(g, x + 46f, y + 122f, 26f, _rx, _ry, rail, On("RS"));
            DesignText(g, "RS", _fButton, Ink.Alpha(T.TextFaint, 0.95f), x + 46f, y + 156f);

            // Home
            CircBtn(g, x + 46f, y + 200f, 10.5f, "Home", On("Home"), null, Icon.Home);

            // C button
            CircBtn(g, x + 108f, y + 200f, 10.5f, "C", On("C"), null);

            // Player LED pips
            for (int i = 0; i < 4; i++)
                LedPip(g, x + 21f, y + 60f + i * 11f);

            DrawSideBadge(g, x, y, w, h, "R", rail);
            DesignText(g, "JOY-CON (R)", _fCaption, T.TextFaint, x + w * 0.5f, CAPTION_Y);
        }

        // ------------------------------------------------------------------ shared parts

        private void DrawShoulder(Graphics g, float x, float y, float w, Color rail, string zName, string bName)
        {
            float sw = w - 42f;
            float sx = x + 21f;

            // ZL / ZR
            bool zOn = On(zName);
            var zRect = new RectangleF(sx, SHOULDER_Y, sw, 24f);
            if (zOn)
                Ink.Glow(g, zRect, 8f, T.Accent, 3, 3.2f);
            Ink.FillVertical(g, zRect, 8f,
                Ink.Lift(zOn ? T.AccentDeep : T.Raised, 0.10f),
                Ink.Sink(zOn ? T.AccentDeep : T.Raised, 0.16f));
            Ink.Outline(g, zRect, 8f, zOn ? T.Accent : T.Line, 1f);
            DesignText(g, zName, _fShoulder, zOn ? Color.White : T.TextSoft, sx + sw * 0.5f, SHOULDER_Y + 12.5f);

            // L / R bumper
            bool bOn = On(bName);
            var bRect = new RectangleF(sx, SHOULDER_Y + 27f, sw, 11f);
            Ink.FillVertical(g, bRect, 4f,
                Ink.Lift(bOn ? T.AccentDeep : T.Raised, 0.14f),
                Ink.Sink(bOn ? T.AccentDeep : T.Raised, 0.20f));
            Ink.Outline(g, bRect, 4f, bOn ? T.Accent : T.Line, 1f);
            DesignText(g, bName, _fShoulder, bOn ? Color.White : T.TextSoft, sx + sw * 0.5f, SHOULDER_Y + 33f);
        }

        private void DrawBody(Graphics g, float x, float y, float w, float h, Color rail, bool railOnRight)
        {
            // contact shadow
            for (int i = 3; i >= 1; i--)
            {
                using var sh = Ink.Silhouette(x, y + 3f * i, w, h, 20f, 40f);
                Ink.FillPath(g, sh, Ink.Alpha(Color.Black, 20 / i));
            }

            // shell
            using (var shell = Ink.Silhouette(x, y, w, h, 20f, 40f))
            {
                using var lg = new LinearGradientBrush(
                           new RectangleF(x, y, w, h),
                           Ink.Lift(T.Raised, 0.14f), Ink.Sink(T.Raised, 0.04f),
                           LinearGradientMode.ForwardDiagonal);
                g.FillPath(lg, shell);
            }

            // soft top-left key light
            using (var sheen = new GraphicsPath())
            {
                sheen.AddArc(x + 3f, y + 3f, 40f, 40f, 185, 100);
                using var p = new Pen(Ink.Alpha(Color.White, 14), 2.2f);
                g.DrawPath(p, sheen);
            }

            // slide rail on the inner edge
            float railW = 9.5f;
            float railX = railOnRight ? x + w - railW - 3f : x + 3f;
            var railRect = new RectangleF(railX, y + 24f, railW, h - 48f);
            Ink.FillVertical(g, railRect, 3.5f, Ink.Sink(rail, 0.06f), Ink.Sink(rail, 0.34f));
            Ink.Outline(g, railRect, 3.5f, Ink.Sink(rail, 0.48f), 1f);
            for (int i = 0; i < 4; i++)
            {
                float ny = y + 42f + i * ((h - 84f) / 3f);
                Ink.Line(g, railX + 3f, ny, railX + railW - 3f, ny, Ink.Alpha(rail, 70), 1.1f);
            }

            // outline, darker than the shell so the bottom edge does not glare
            using (var outline = Ink.Silhouette(x, y, w, h, 20f, 40f))
            {
                using var p = new Pen(Ink.Sink(T.Raised, 0.36f), 1.1f);
                g.DrawPath(p, outline);
            }
        }

        private void DrawSideBadge(Graphics g, float x, float y, float w, float h, string side, Color rail)
        {
            var badge = new RectangleF(x + 12f, y + h - 30f, 22f, 18f);
            Ink.Fill(g, badge, 4f, Ink.Alpha(rail, 34));
            Ink.Outline(g, badge, 4f, Ink.Alpha(rail, 110), 1f);
            DesignText(g, side, _fSide, Ink.Lift(rail, 0.22f), badge.X + badge.Width * 0.5f, badge.Y + badge.Height * 0.5f);
        }

        private void DrawDPad(Graphics g, float cx, float cy)
        {
            // One connected plus: four arms that overlap the hub, so it reads as a single
            // moulded D-pad rather than four floating squares.
            const float t = 21f, R = 31f, rad = 4f;
            float half = t * 0.5f;

            var up  = new RectangleF(cx - half, cy - R, t, R + half);
            var dn  = new RectangleF(cx - half, cy - half, t, R + half);
            var lf  = new RectangleF(cx - R, cy - half, R + half, t);
            var rt  = new RectangleF(cx - half, cy - half, R + half, t);

            Ink.Fill(g, up, rad, T.Raised);
            Ink.Fill(g, dn, rad, T.Raised);
            Ink.Fill(g, lf, rad, T.Raised);
            Ink.Fill(g, rt, rad, T.Raised);
            Ink.Fill(g, new RectangleF(cx - half, cy - half, t, t), rad, Ink.Sink(T.Raised, 0.16f));

            if (On("Up")) Ink.Fill(g, new RectangleF(up.X + 1.5f, up.Y + 1.5f, t - 3, up.Height - half - 1.5f), rad * 0.6f, T.AccentDeep);
            if (On("Dn")) Ink.Fill(g, new RectangleF(dn.X + 1.5f, dn.Y, t - 3, dn.Height - half - 1.5f), rad * 0.6f, T.AccentDeep);
            if (On("Lt")) Ink.Fill(g, new RectangleF(lf.X + 1.5f, lf.Y + 1.5f, lf.Width - half - 1.5f, t - 3), rad * 0.6f, T.AccentDeep);
            if (On("Rt")) Ink.Fill(g, new RectangleF(rt.X, rt.Y + 1.5f, rt.Width - half - 1.5f, t - 3), rad * 0.6f, T.AccentDeep);

            // pivot well
            Ink.Fill(g, new RectangleF(cx - 5.5f, cy - 5.5f, 11f, 11f), 5.5f, Ink.Sink(T.Raised, 0.38f));
            Ink.Outline(g, new RectangleF(cx - 5.5f, cy - 5.5f, 11f, 11f), 5.5f, Ink.Sink(T.Raised, 0.10f), 1f);

            // Arrowhead angle must match the arm it sits in. Screen Y grows downward, so
            // up = -90 deg, right = 0, down = +90, left = 180. These four were previously
            // rotated a quarter turn, which made the pad read right/down/left/up.
            bool lit = On("Up") || On("Dn") || On("Lt") || On("Rt");
            Color ink = lit ? Color.White : T.TextSoft;
            const float s = 4.4f, inset = 12f;
            Arrow(g, cx, cy - (R - inset), -90, s, ink);  // up
            Arrow(g, cx + (R - inset), cy, 0, s, ink);    // right
            Arrow(g, cx, cy + (R - inset), 90, s, ink);   // down
            Arrow(g, cx - (R - inset), cy, 180, s, ink);  // left
        }

        private static void Arrow(Graphics g, float cx, float cy, float angleDeg, float s, Color color)
        {
            double a = angleDeg * Math.PI / 180.0;
            float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
            float px = -dy, py = dx;
            using var brush = new SolidBrush(color);
            g.FillPolygon(brush, new[]
            {
                new PointF(cx + dx * s, cy + dy * s),
                new PointF(cx - dx * s * 0.35f + px * s, cy - dy * s * 0.35f + py * s),
                new PointF(cx - dx * s * 0.35f - px * s, cy - dy * s * 0.35f - py * s),
            });
        }

        private void StickViz(Graphics g, float cx, float cy, float r, float nx, float ny, Color rim, bool pressed)
        {
            // recessed well
            var well = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            Ink.FillVertical(g, well, r, Ink.Sink(T.Sunken, 0.35f), Ink.Lift(T.Sunken, 0.10f));
            Ink.Outline(g, well, r, Ink.Sink(T.Raised, 0.10f), 1.2f);

            // travel gate + crosshair
            Ink.Outline(g, RectangleF.Inflate(well, -r * 0.22f, -r * 0.22f), r * 0.78f, Ink.Alpha(T.Line, 0.95f), 1f);
            Ink.Line(g, cx - r * 0.88f, cy, cx + r * 0.88f, cy, Ink.Alpha(T.Line, 0.9f), 1f);
            Ink.Line(g, cx, cy - r * 0.88f, cx, cy + r * 0.88f, Ink.Alpha(T.Line, 0.9f), 1f);

            bool live = Math.Abs(nx) > 0.045f || Math.Abs(ny) > 0.045f;
            var knobR = r * 0.40f;
            float kx = cx + nx * (r - knobR - 3f);
            float ky = cy - ny * (r - knobR - 3f);
            var knob = new RectangleF(kx - knobR, ky - knobR, knobR * 2, knobR * 2);

            if (live || pressed)
                Ink.Glow(g, knob, knobR, live ? rim : T.Accent, 2, 2.0f);

            Color knobTop = pressed ? T.Accent : live ? Ink.Lift(rim, 0.26f) : Ink.Lift(rim, 0.02f);
            Color knobBot = pressed ? T.AccentDeep : Ink.Sink(rim, 0.24f);
            Ink.FillVertical(g, knob, knobR, knobTop, knobBot);
            Ink.Outline(g, knob, knobR, Ink.Sink(rim, 0.50f), 1f);

            using (var spec = new GraphicsPath())
            {
                spec.AddArc(knob.X, knob.Y, knob.Width, knob.Height, 205, 120);
                using var p = new Pen(Ink.Alpha(Color.White, 60), 1.1f);
                g.DrawPath(p, spec);
            }
        }

        private void CircBtn(Graphics g, float cx, float cy, float r, string label, bool pressed,
                             Color? hue, Icon shape = Icon.None)
        {
            var rect = new RectangleF(cx - r, cy - r, r * 2, r * 2);

            if (pressed)
                Ink.Glow(g, rect, r, T.Accent, 3, 2.8f);

            Color top = pressed ? T.Accent : T.Raised;
            Color bot = pressed ? T.AccentDeep : Ink.Sink(T.Raised, 0.10f);
            Ink.FillVertical(g, rect, r, Ink.Lift(top, pressed ? 0.10f : 0.06f), bot);
            Ink.Outline(g, rect, r, pressed ? T.Accent : T.Line, 1.2f);

            if (!pressed && hue.HasValue)
                Ink.Outline(g, RectangleF.Inflate(rect, -2.4f, -2.4f), r - 2.4f, Ink.Alpha(hue.Value, 130), 1f);

            if (shape != Icon.None)
            {
                DrawIcon(g, shape, cx, cy, r * 0.52f, pressed ? Color.White : T.TextSoft);
                return;
            }

            if (label == "-" || label == "+")
            {
                // + and - are drawn as strokes so they stay crisp at any scale
                float s = r * 0.36f;
                using var pen = new Pen(pressed ? Color.White : T.TextSoft, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(pen, cx - s, cy, cx + s, cy);
                if (label == "+")
                    g.DrawLine(pen, cx, cy - s, cx, cy + s);
                return;
            }

            DesignText(g, label, _fButton, pressed ? Color.White : T.TextSoft, cx, cy);
        }

        private void LedPip(Graphics g, float cx, float cy)
        {
            const float r = 2.4f;
            Ink.Fill(g, new RectangleF(cx - r, cy - r, r * 2, r * 2), r, Ink.Alpha(T.TextFaint, 70));
        }

        // ------------------------------------------------------------------ text

        private enum Icon
        {
            None,
            Cross,
            Home,
            Shutter,
        }

        /// <summary>Vector glyphs for the small system buttons, so no font has to carry them.</summary>
        private void DrawIcon(Graphics g, Icon icon, float cx, float cy, float s, Color color)
        {
            if (icon == Icon.None) return;
            using var pen = new Pen(color, Math.Max(1f, s * 0.14f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            switch (icon)
            {
                case Icon.Cross:
                    g.DrawLine(pen, cx - s, cy, cx + s, cy);
                    g.DrawLine(pen, cx, cy - s, cx, cy + s);
                    break;
                case Icon.Home:
                    g.DrawLines(pen, new[]
                    {
                        new PointF(cx - s, cy - s * 0.05f),
                        new PointF(cx, cy - s * 0.95f),
                        new PointF(cx + s, cy - s * 0.05f),
                    });
                    g.DrawLines(pen, new[]
                    {
                        new PointF(cx - s * 0.68f, cy - s * 0.22f),
                        new PointF(cx - s * 0.68f, cy + s * 0.9f),
                        new PointF(cx + s * 0.68f, cy + s * 0.9f),
                        new PointF(cx + s * 0.68f, cy - s * 0.22f),
                    });
                    break;
                case Icon.Shutter:
                    g.DrawEllipse(pen, cx - s, cy - s * 0.82f, s * 2f, s * 1.64f);
                    g.DrawLine(pen, cx - s * 0.45f, cy + s * 0.28f, cx + s * 0.45f, cy + s * 0.28f);
                    g.DrawLine(pen, cx - s * 0.34f, cy - s * 0.02f, cx + s * 0.34f, cy - s * 0.02f);
                    break;
            }
        }

        /// <summary>
        /// Draws design-space text at real device pixels: the world transform is dropped so the
        /// GDI+ font is not scaled twice, and the design coordinate is mapped by hand instead.
        /// Point-sized fonts already grow with the monitor DPI, which is exactly what we want.
        /// </summary>
        private void DesignText(Graphics g, string text, Font font, Color color, float dx, float dy)
        {
            if (string.IsNullOrEmpty(text)) return;
            var saved = g.Transform;
            g.ResetTransform();
            try
            {
                var size = Ink.Measure(g, text, font);
                float x = _ox + dx * _k - size.Width * 0.5f;
                float y = _oy + dy * _k - size.Height * 0.5f;
                Ink.TextAt(g, text, font, x, y, color);
            }
            finally
            {
                g.Transform = saved;
            }
        }
    }
}
