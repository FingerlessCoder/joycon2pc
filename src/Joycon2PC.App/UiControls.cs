
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// This window is built entirely in code - there is no WinForms designer surface, so the
// designer's code-serialisation rules for public control properties do not apply.
#pragma warning disable WFO1000

namespace Joycon2PC.App
{
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  BASE CLASSES
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    internal abstract class ThemedControl : Control
    {
        protected readonly UiMetrics _m = new();

        internal UiTheme T => _m.Theme ?? throw new InvalidOperationException("Theme not attached.");
        internal UiMetrics Metrics => _m;

        protected ThemedControl()
        {
            SetStyle(ControlStyles.UserPaint
                   | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw, true);
            TabStop = true;
        }
    }

    internal abstract class ThemedPanel : Panel
    {
        protected readonly UiMetrics _m = new();

        internal UiTheme T => _m.Theme ?? throw new InvalidOperationException("Theme not attached.");
        internal UiMetrics Metrics => _m;

        protected ThemedPanel()
        {
            SetStyle(ControlStyles.UserPaint
                   | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw, true);
            BorderStyle = BorderStyle.None;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  SECTION CAPTION  - tracked uppercase heading, the main structural cue
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    internal sealed class SectionCaption : ThemedControl
    {
        private string _text = string.Empty;

        internal SectionCaption()
        {
            SetStyle(ControlStyles.Selectable, false);
            Height = 16;
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string Text
        {
            get => _text;
            set { _text = value ?? string.Empty; Invalidate(); }
        }

        public bool ShowRule { get; set; } = true;
        public Color Tint { get; set; } = Color.Empty;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ink.Prepare(g);
            g.Clear(Ink.ParentColor(Parent, T.Root));

            var color = Tint.IsEmpty ? T.Accent : Tint;
            float tracking = 0.9f * Math.Max(0.8f, _m.Scale);
            var area = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
            Ink.Tracked(g, _text, T.FSection, area, color, tracking, StringAlignment.Near, StringAlignment.Center);

            if (!ShowRule)
                return;

            float textW = Ink.TrackedWidth(g, _text, T.FSection, tracking);
            float lineX = textW + _m.S(10);
            if (lineX >= ClientSize.Width - _m.S(6))
                return;

            using var pen = new Pen(Ink.Alpha(T.Line, 0.9f), 1f);
            g.DrawLine(pen, lineX, ClientSize.Height * 0.5f, ClientSize.Width, ClientSize.Height * 0.5f);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  CARD  - elevated surface, optional accent bar
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    internal class CardSurface : ThemedPanel
    {
        private Color? _fill;
        private Color? _stroke;

        public CardSurface()
        {
            Padding = new Padding(0);
        }

        public Color SurfaceColor { get => _fill ?? T.Surface; set { _fill = value; BackColor = value; Invalidate(); } }
        public Color StrokeColor { get => _stroke ?? T.Line; set { _stroke = value; Invalidate(); } }
        public int Radius { get; set; } = UiTheme.RadiusCard;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ink.Prepare(g);
            g.Clear(Ink.ParentColor(Parent, T.Root));

            var r = new RectangleF(0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f);
            float rad = _m.Radius(Radius);
            // Flat fill on purpose: children (captions, labels) clear with BackColor, and a
            // gradient here would show as a seam under every one of them.
            Ink.Fill(g, r, rad, SurfaceColor);
            Ink.Outline(g, r, rad, StrokeColor, 1f);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  STATUS CARD  - caption + value + live state dot
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    /// <summary>
    /// A headline status readout. The dot colour is read straight off the value label's
    /// ForeColor, which is how every existing status update in this app signals state -
    /// so the dot stays in sync without any extra bookkeeping.
    /// </summary>
    internal sealed class StatusCard : CardSurface
    {
        public StatusCard()
        {
            Radius = UiTheme.RadiusCard;
        }

        /// <summary>The label whose text and colour define this card's value and state.</summary>
        public Label? ValueLabel { get; set; }

        public string Caption { get; set; } = string.Empty;

        /// <summary>Overrides the automatic dot colour when set.</summary>
        public Color? StateOverride { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            Ink.Prepare(g);

            var state = StateOverride
                        ?? (ValueLabel != null && ValueLabel.ForeColor.ToArgb() != 0 ? ValueLabel.ForeColor : T.Idle);

            int pad = _m.S(13);
            int dotCx = pad + _m.S(4);
            int dotCy = _m.S(18);
            int dotR = _m.S(4);

            // soft halo so the state reads at a glance
            Ink.Fill(g, new RectangleF(dotCx - dotR * 2, dotCy - dotR * 2, dotR * 4, dotR * 4), dotR * 2f, Ink.Alpha(state, 34));
            Ink.Fill(g, new RectangleF(dotCx - dotR, dotCy - dotR, dotR * 2, dotR * 2), dotR, state);

            var capArea = new RectangleF(pad + _m.S(14), _m.S(8), ClientSize.Width - pad * 2 - _m.S(14), _m.S(18));
            Ink.Tracked(g, Caption, T.FSmallBold, capArea, T.TextFaint, 0.7f * Math.Max(0.8f, _m.Scale), StringAlignment.Near, StringAlignment.Center);

            if (ValueLabel != null)
            {
                var vArea = new RectangleF(pad, _m.S(28), ClientSize.Width - pad * 2, Math.Max(_m.S(18), ClientSize.Height - _m.S(34)));
                Ink.Text(g, ValueLabel.Text, T.FTitle, vArea, state, StringAlignment.Near, StringAlignment.Center);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  APP HEADER  - wordmark + tagline + live session pill
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    internal sealed class AppHeader : ThemedPanel
    {
        private string _stateText = "Idle";
        private Color _stateColor;

        public AppHeader()
        {
            _stateColor = Color.FromArgb(0x5A, 0x64, 0x74);
        }

        public void SetState(string text, Color color)
        {
            if (_stateText == text && _stateColor.ToArgb() == color.ToArgb())
                return;
            _stateText = text;
            _stateColor = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ink.Prepare(g);
            g.Clear(T.Root);

            int cy = ClientSize.Height / 2;
            int x = _m.S(2);

            // ---- wordmark: two-tone, "JOYCON" bright / "2PC" dimmed ----
            string a = "JOYCON";
            string b = "2PC";
            float aw = Ink.Measure(g, a, T.FDisplay).Width;
            float bw = Ink.Measure(g, b, T.FDisplay).Width;
            var ah = Ink.Measure(g, a, T.FDisplay).Height;
            float ty = cy - ah * 0.5f - _m.F(0.5f);

            Ink.TextAt(g, a, T.FDisplay, x, ty, T.Text);
            Ink.TextAt(g, b, T.FDisplay, x + aw + _m.F(2f), ty, T.Accent);

            // ---- hairline + tagline ----
            float ruleX = x + aw + bw + _m.S(16);
            float ruleTop = cy - _m.S(9);
            float ruleBottom = cy + _m.S(9);
            if (ruleX < ClientSize.Width * 0.55f)
            {
                Ink.Line(g, ruleX, ruleTop, ruleX, ruleBottom, T.Line, 1f);
                Ink.TextAt(g, "Joy-Con 2 \u2192 virtual Xbox 360 controller",
                    T.FBody, ruleX + _m.S(12), cy - Ink.Measure(g, "Joy-Con 2", T.FBody).Height * 0.5f, T.TextFaint);
            }

            // ---- session pill, right aligned ----
            string pill = string.IsNullOrWhiteSpace(_stateText) ? "Idle" : _stateText;
            var fs = Ink.Measure(g, pill, T.FSmallBold);
            int dotR = _m.S(3);
            int gapDot = _m.S(8);
            int padX = _m.S(11);
            int pillW = padX * 2 + dotR * 2 + gapDot + (int)Math.Ceiling(fs.Width) + _m.S(2);
            int pillH = _m.S(24);
            var pillRect = new RectangleF(ClientSize.Width - pillW - _m.S(2), (cy - pillH * 0.5f), pillW, pillH);

            Ink.Fill(g, pillRect, pillH * 0.5f, Ink.Alpha(_stateColor, 30));
            Ink.Outline(g, pillRect, pillH * 0.5f, Ink.Alpha(_stateColor, 96), 1f);
            Ink.Fill(g, new RectangleF(pillRect.X + padX, cy - dotR, dotR * 2f, dotR * 2f), dotR, _stateColor);
            Ink.Text(g, pill, T.FSmallBold,
                new RectangleF(pillRect.X + padX + dotR * 2f + gapDot, pillRect.Y, pillRect.Width, pillRect.Height),
                _stateColor, StringAlignment.Near, StringAlignment.Center);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  STATUS BAR  - bottom telemetry strip: a live dot, a caption and a flooding label
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    internal sealed class StatusBarPanel : ThemedPanel
    {
        private Color _hue = Color.FromArgb(0x7C, 0x86, 0x97);

        public StatusBarPanel()
        {
        }

        public int Radius { get; set; } = UiTheme.RadiusCard;

        public string Caption { get; set; } = "LINK";

        /// <summary>Colour of the leading dot; set from the telemetry label's ForeColor.</summary>
        public Color Hue
        {
            get => _hue;
            set
            {
                if (_hue.ToArgb() == value.ToArgb()) return;
                _hue = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ink.Prepare(g);
            g.Clear(BackColor);

            var r = new RectangleF(0.5f, 0.5f, Math.Max(0, ClientSize.Width - 1f), Math.Max(0, ClientSize.Height - 1f));
            float rad = _m.Radius(Radius);
            Ink.Fill(g, r, rad, BackColor);
            Ink.Outline(g, r, rad, T.Line, 1f);

            int d = _m.S(3);
            Ink.Fill(g, new RectangleF(_m.S(11) - d, r.Height * 0.5f - d, d * 2f, d * 2f), d, _hue);
            Ink.Tracked(g, Caption, T.FSmallBold,
                new RectangleF(_m.S(20), 0, _m.S(50), r.Height),
                T.TextFaint, 0.7f * Math.Max(0.8f, _m.Scale), StringAlignment.Near, StringAlignment.Center);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  BUTTON
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    internal enum ButtonSkin
    {
        /// <summary>Filled with BackColor, no border. For the single primary action.</summary>
        Solid,
        /// <summary>BackColor fill plus a hairline border. Default for everything else.</summary>
        Soft,
        /// <summary>Border only; fills on hover. For tertiary actions in a header row.</summary>
        Ghost,
    }

    /// <summary>
    /// Flat themed button. Deliberately reads its fill from <see cref="Control.BackColor"/> and
    /// its label from <see cref="Control.ForeColor"/>, so existing code that repaints a button
    /// by assigning those two properties keeps working exactly as before.
    /// </summary>
    internal sealed class ThemedButton : Button
    {
        private readonly UiMetrics _m = new();
        private bool _hover;
        private bool _down;

        public ThemedButton()
        {
            SetStyle(ControlStyles.UserPaint
                   | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = Color.Transparent;
            FlatAppearance.MouseDownBackColor = Color.Transparent;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
        }

        internal UiTheme T => _m.Theme ?? throw new InvalidOperationException("Theme not attached.");
        internal UiMetrics Metrics => _m;
        public int Radius { get; set; } = UiTheme.RadiusField;
        public ButtonSkin Skin { get; set; } = ButtonSkin.Soft;
        public Glyph Icon { get; set; } = Glyph.None;
        public int IconSize { get; set; } = 15;
        public Color FocusRing { get; set; } = Color.Empty;

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _down = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _down = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _down = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ink.Prepare(g);

            var bounds = new RectangleF(0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f);
            float rad = _m.Radius(Radius);

            Color fill = BackColor;
            Color stroke = T.Line;
            float strokeW = 1f;
            Color fg = ForeColor;

            if (!Enabled)
            {
                fill = Ink.Sink(T.Surface, 0.10f);
                fg = Ink.Alpha(T.TextFaint, 0.55f);
                stroke = Ink.Alpha(T.Line, 0.6f);
            }
            else
            {
                switch (Skin)
                {
                    case ButtonSkin.Ghost:
                        if (_hover) { fill = Ink.Lift(BackColor, 0.06f); }
                        stroke = _hover ? T.LineStrong : T.Line;
                        break;
                    case ButtonSkin.Solid:
                        if (_hover) fill = Ink.Lift(BackColor, _down ? 0.02f : 0.12f);
                        else if (_down) fill = Ink.Sink(BackColor, 0.14f);
                        break;
                    default: // Soft
                        if (_hover) fill = Ink.Lift(BackColor, _down ? 0.04f : 0.10f);
                        else if (_down) fill = Ink.Sink(BackColor, 0.10f);
                        stroke = _hover ? T.LineStrong : T.Line;
                        break;
                }

                if (Focused)
                    stroke = FocusRing.IsEmpty ? T.Accent : FocusRing;
            }

            if (Skin != ButtonSkin.Solid || !Enabled)
                strokeW = 1f;

            if (Enabled && Skin == ButtonSkin.Solid)
            {
                // filled buttons get a subtle inner top highlight so they read as raised
                Ink.FillVertical(g, bounds, rad, Ink.Lift(fill, 0.10f), Ink.Sink(fill, 0.06f));
            }
            else
            {
                Ink.Fill(g, bounds, rad, fill);
            }

            if (strokeW > 0f)
                Ink.Outline(g, bounds, rad, stroke, strokeW);

            // ---- content: optional glyph + label ----
            float iconSlot = 0f;
            if (Icon != Glyph.None && Enabled)
            {
                int s = _m.S(IconSize);
                int iconGap = _m.S(6);
                var textSize = string.IsNullOrEmpty(Text) ? SizeF.Empty : Ink.Measure(g, Text, Font);
                float totalW = s + iconGap + textSize.Width;
                float x0 = (ClientSize.Width - totalW) * 0.5f;
                Glyphs.Draw(g, Icon, new RectangleF(x0, (ClientSize.Height - s) * 0.5f, s, s), fg, 1.5f);
                iconSlot = s + iconGap;
            }

            var textArea = new RectangleF(iconSlot, 0, ClientSize.Width - iconSlot, ClientSize.Height);
            Ink.Text(g, Text, Font, textArea, fg, StringAlignment.Center, StringAlignment.Center);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  CHECKBOX  - rounded box, accent check
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    internal sealed class ThemedCheckBox : CheckBox
    {
        private readonly UiMetrics _m = new();
        private bool _hover;

        public ThemedCheckBox()
        {
            SetStyle(ControlStyles.UserPaint
                   | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        internal UiTheme T => _m.Theme ?? throw new InvalidOperationException("Theme not attached.");
        internal UiMetrics Metrics => _m;

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ink.Prepare(g);
            g.Clear(Ink.ParentColor(Parent, T.Root));

            int box = _m.S(14);
            int gap = _m.S(6);
            int cy = ClientSize.Height / 2;
            var boxRect = new RectangleF(0.5f, cy - box * 0.5f, box, box);
            float rad = _m.Radius(3);

            Color edge = !Enabled ? Ink.Alpha(T.LineStrong, 0.5f)
                      : Checked ? T.AccentDeep
                      : _hover ? T.LineStrong : T.LineStrong;
            Color fill = !Enabled ? Ink.Sink(T.Surface, 0.1f)
                       : Checked ? T.AccentDeep
                       : _hover ? Ink.Lift(T.Raised, 0.08f) : T.Raised;

            Ink.Fill(g, boxRect, rad, fill);
            Ink.Outline(g, boxRect, rad, edge, 1f);

            if (Checked)
            {
                using var pen = new Pen(Color.White, Math.Max(1.3f, _m.F(1.7f)))
                {
                    StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
                };
                float x = boxRect.X, y = boxRect.Y, w = boxRect.Width, h = boxRect.Height;
                g.DrawLines(pen, new[]
                {
                    new PointF(x + w * 0.26f, y + h * 0.52f),
                    new PointF(x + w * 0.44f, y + h * 0.70f),
                    new PointF(x + w * 0.76f, y + h * 0.30f),
                });
            }

            Color fg = !Enabled ? Ink.Alpha(T.TextFaint, 0.55f) : (Checked ? T.Text : T.TextSoft);
            var textArea = new RectangleF(box + gap, 0, Math.Max(0, ClientSize.Width - box - gap), ClientSize.Height);
            Ink.Text(g, Text, Font, textArea, fg, StringAlignment.Near, StringAlignment.Center);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  TOGGLE  - pill switch, used for "mouse mode" (a mode, not a setting)
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    internal sealed class ThemedToggle : CheckBox
    {
        private readonly UiMetrics _m = new();
        private bool _hover;

        public ThemedToggle()
        {
            SetStyle(ControlStyles.UserPaint
                   | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        internal UiTheme T => _m.Theme ?? throw new InvalidOperationException("Theme not attached.");
        internal UiMetrics Metrics => _m;

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ink.Prepare(g);
            g.Clear(Ink.ParentColor(Parent, T.Root));

            int trackW = _m.S(30);
            int trackH = _m.S(17);
            int cy = ClientSize.Height / 2;
            var track = new RectangleF(0.5f, cy - trackH * 0.5f, trackW, trackH);
            float rad = trackH * 0.5f;

            Color fill = !Enabled ? Ink.Sink(T.Surface, 0.1f)
                      : Checked ? T.AccentDeep
                      : _hover ? Ink.Lift(T.Raised, 0.10f) : T.Raised;
            Ink.Fill(g, track, rad, fill);
            Ink.Outline(g, track, rad, !Enabled ? Ink.Alpha(T.LineStrong, 0.5f)
                                               : Checked ? T.Accent : T.LineStrong, 1f);

            int knob = trackH - _m.S(5);
            float kx = Checked ? track.Right - knob - _m.F(2.5f) : track.X + _m.F(2.5f);
            var knobRect = new RectangleF(kx, cy - knob * 0.5f, knob, knob);
            Ink.FillVertical(g, knobRect, knob * 0.5f,
                Ink.Lift(Checked ? T.Accent : T.TextSoft, 0.25f),
                Checked ? T.Accent : Ink.Sink(T.TextSoft, 0.18f));

            int gap = _m.S(9);
            Color fg = !Enabled ? Ink.Alpha(T.TextFaint, 0.55f) : (Checked ? T.Text : T.TextSoft);
            var textArea = new RectangleF(trackW + gap, 0, Math.Max(0, ClientSize.Width - trackW - gap), ClientSize.Height);
            Ink.Text(g, Text, Font, textArea, fg, StringAlignment.Near, StringAlignment.Center);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  PROGRESS BAR  - thin rounded rail
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    /// <summary>Keeps the stock <see cref="ProgressBar"/> Value/Minimum/Maximum contract so existing update code is untouched.</summary>
    internal sealed class ThemedProgressBar : Control
    {
        private readonly UiMetrics _m = new();
        private int _minimum;
        private int _maximum = 100;
        private int _value;

        public ThemedProgressBar()
        {
            SetStyle(ControlStyles.UserPaint
                   | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        internal UiTheme T => _m.Theme ?? throw new InvalidOperationException("Theme not attached.");
        internal UiMetrics Metrics => _m;

        public int Minimum
        {
            get => _minimum;
            set { _minimum = value; if (_value < _minimum) _value = _minimum; Invalidate(); }
        }

        public int Maximum
        {
            get => _maximum;
            set { _maximum = Math.Max(_minimum + 1, value); if (_value > _maximum) _value = _maximum; Invalidate(); }
        }

        public int Value
        {
            get => _value;
            set
            {
                int clamped = Math.Clamp(value, _minimum, _maximum);
                if (clamped == _value) return;
                _value = clamped;
                Invalidate();
            }
        }

        /// <summary>Set alongside <see cref="Value"/> by the stage painter so the rail picks up the active state colour.</summary>
        public Color Tint { get; set; } = Color.Empty;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ink.Prepare(g);
            g.Clear(Ink.ParentColor(Parent, T.Root));

            int h = Math.Max(3, _m.S(4));
            var rail = new RectangleF(0, (ClientSize.Height - h) * 0.5f, Math.Max(0, ClientSize.Width - 1f), h);
            float rad = h * 0.5f;
            Ink.Fill(g, rail, rad, Ink.Alpha(T.Line, 0.85f));

            float t = _maximum > _minimum ? (_value - _minimum) / (float)(_maximum - _minimum) : 0f;
            float w = rail.Width * Math.Clamp(t, 0f, 1f);
            if (w < 1f) return;

            var fill = new RectangleF(rail.X, rail.Y, w, rail.Height);
            Color c = Tint.IsEmpty ? T.Accent : Tint;
            Ink.FillVertical(g, fill, rad, Ink.Lift(c, 0.18f), Ink.Sink(c, 0.10f));

            if (w > rad * 2f)
                Ink.Fill(g, new RectangleF(fill.Right - _m.F(2f), fill.Y, _m.F(2f), fill.Height), _m.F(1f), Ink.Lift(c, 0.45f));
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  STAGE CHIP  - Scan / Init / Ready
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    internal sealed class StageChip : ThemedControl
    {
        private string _text = string.Empty;

        public StageChip()
        {
            SetStyle(ControlStyles.Selectable, false);
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string Text
        {
            get => _text;
            set { _text = value ?? string.Empty; Invalidate(); }
        }

        public bool Active { get; set; }
        public Color Tint { get; set; } = Color.Empty;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ink.Prepare(g);
            g.Clear(Ink.ParentColor(Parent, T.Root));

            var r = new RectangleF(0.5f, 0.5f, Math.Max(0, ClientSize.Width - 1f), Math.Max(0, ClientSize.Height - 1f));
            float rad = r.Height * 0.5f;
            Color c = Tint.IsEmpty ? T.Accent : Tint;

            if (Active)
            {
                Ink.Fill(g, r, rad, Ink.Alpha(c, 38));
                Ink.Outline(g, r, rad, Ink.Alpha(c, 120), 1f);
            }
            else
            {
                Ink.Fill(g, r, rad, T.Sunken);
                Ink.Outline(g, r, rad, T.LineSoft, 1f);
            }

            Color fg = Active ? Ink.Lift(c, 0.15f) : T.TextFaint;
            var area = new RectangleF(r.X + _m.S(8), r.Y, r.Width - _m.S(16), r.Height);
            Ink.Tracked(g, _text, T.FSmallBold, area, fg, 0.6f * Math.Max(0.8f, _m.Scale), StringAlignment.Center, StringAlignment.Center);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    //  DROP DOWN  - fully custom combo: the one stock control that cannot carry a dark theme
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    /// <summary>
    /// Drop-down list that owns its own popup. Exposes the slice of the ComboBox surface this
    /// app actually uses (Items / SelectedIndex / SelectedItem / SelectedIndexChanged) so the
    /// existing option plumbing is unchanged.
    /// </summary>
    internal sealed class ThemedDropDown : ThemedControl
    {
        private readonly ItemCollection _items;
        private int _selectedIndex = -1;
        private bool _hover;
        private bool _hoverPreview;
        private Form? _popup;
        private int _hoverIndex = -1;

        public ThemedDropDown()
        {
            _items = new ItemCollection(this);
            SetStyle(ControlStyles.Selectable, true);
            Height = 28;
        }

        public ItemCollection Items => _items;

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                int v = _items.Count == 0 ? -1 : Math.Clamp(value, -1, _items.Count - 1);
                if (v == _selectedIndex) return;
                _selectedIndex = v;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public object? SelectedItem => _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : null;

        public event EventHandler? SelectedIndexChanged;

        /// <summary>Raised when the popup opens / closes so the owning window can re-check the field.</summary>
        public event EventHandler? DropDownVisibilityChanged;

        public bool IsDropDownOpen => _popup is { Visible: true };

        /// <summary>What the field shows: the hovered row while the list is open, otherwise the selection.</summary>
        internal string DisplayText
        {
            get
            {
                if (_hoverPreview && _hoverIndex >= 0 && _hoverIndex < _items.Count)
                    return _items[_hoverIndex]?.ToString() ?? string.Empty;
                return SelectedItem?.ToString() ?? string.Empty;
            }
        }

        // ------------------------------------------------------------------ items plumbing

        internal sealed class ItemCollection : IList
        {
            private readonly List<object> _list = new();
            private readonly ThemedDropDown _owner;

            public ItemCollection(ThemedDropDown owner) => _owner = owner;

            private void Changed()
            {
                if (_owner._selectedIndex >= _list.Count)
                    _owner.SelectedIndex = Math.Max(0, _list.Count - 1);
                _owner._hoverIndex = -1;
                _owner.RefreshList();
            }

            public int Count => _list.Count;
            public bool IsFixedSize => false;
            public bool IsReadOnly => false;
            public bool IsSynchronized => false;
            public object SyncRoot => _list;

            public object? this[int index]
            {
                get => _list[index];
                set { _list[index] = value!; Changed(); }
            }

            public int Add(object? item) { _list.Add(item!); Changed(); return _list.Count - 1; }

            public void AddRange(object[] items) { _list.AddRange(items); Changed(); }

            public void Clear() { _list.Clear(); Changed(); }

            public bool Contains(object? item) => _list.Contains(item!);
            public void CopyTo(Array array, int index) => ((ICollection)_list).CopyTo(array, index);
            public IEnumerator GetEnumerator() => _list.GetEnumerator();
            public int IndexOf(object? item) => _list.IndexOf(item!);
            public void Insert(int index, object? item) { _list.Insert(index, item!); Changed(); }
            public void Remove(object? item) { _list.Remove(item!); Changed(); }
            public void RemoveAt(int index) { _list.RemoveAt(index); Changed(); }
        }

        // ------------------------------------------------------------------ open / close

        private void ToggleDropDown()
        {
            if (IsDropDownOpen) CloseDropDown();
            else OpenDropDown();
        }

        /// <summary>
        /// The single commit path for both click and Enter: stop previewing, close, then
        /// publish the selection. SelectedIndex only raises its event when the value really
        /// changes, so exactly one SelectedIndexChanged reaches the rest of the app.
        /// </summary>
        private void CommitIndex(int target)
        {
            if (target < 0) return;
            _hoverPreview = false;
            _hoverIndex = -1;
            CloseDropDown();
            SelectedIndex = target;
        }

        /// <summary>
        /// Keyboard semantics for the drop-down, shared by the field and the open list so
        /// Up/Down/Enter/Space/Escape behave identically whether or not the list has focus.
        /// </summary>
        internal void HandleDropDownKey(Keys key, KeyEventArgs e)
        {
            var list = _popup is { Visible: true } ? (ThemedList)_popup.Tag! : null;

            switch (key)
            {
                case Keys.Up:
                    if (list != null) SetListIndex(list, list.SelectedIndex - 1);
                    e.Handled = true;
                    break;
                case Keys.Down:
                    if (list != null) SetListIndex(list, list.SelectedIndex + 1);
                    e.Handled = true;
                    break;
                case Keys.Space:
                case Keys.Enter:
                    if (list != null) CommitIndex(list.SelectedIndex);
                    else ToggleDropDown();
                    e.Handled = true;
                    break;
                case Keys.Escape:
                    CloseDropDown();
                    e.Handled = true;
                    break;
            }
        }

        private static void SetListIndex(ThemedList list, int index)
        {
            int clamped = Math.Clamp(index, 0, list.Items.Count - 1);
            list.SelectedIndex = clamped;
            list.HoverIndex = clamped;   // keep the field's preview in step with the keyboard
        }

        private void OpenDropDown()
        {
            if (IsDropDownOpen || _items.Count == 0)
                return;

            _popup ??= CreatePopup();
            var list = (ThemedList)_popup.Tag!;

            int itemH = _m.S(26);
            int pad = _m.S(5);
            int visible = Math.Min(_items.Count, 9);
            int h = visible * itemH + pad * 2;
            int w = Math.Max(Width, _m.S(120));

            _popup.ClientSize = new Size(w, h);
            // the list is Dock.Fill inside the host's 1px padding, which draws the border
            list.ItemHeight = itemH;
            list.SetItems(_items);
            list.SelectedIndex = _selectedIndex;

            var screen = Screen.FromControl(this).WorkingArea;
            Point origin = PointToScreen(new Point(0, Height + _m.S(3)));
            if (origin.Y + h > screen.Bottom)
            {
                int above = PointToScreen(new Point(0, 0)).Y - h - _m.S(3);
                origin.Y = above >= screen.Top ? above : Math.Max(screen.Top, screen.Bottom - h);
            }
            origin.X = Math.Clamp(origin.X, screen.Left, Math.Max(screen.Left, screen.Right - w));

            _popup.Location = origin;
            _hoverPreview = true;
            _hoverIndex = _selectedIndex;

            var owner = FindForm();
            if (owner != null && owner != _popup)
                _popup.Owner = owner;

            _popup.Show();
            list.Focus();
            Invalidate();
            DropDownVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }

        private void CloseDropDown()
        {
            if (!IsDropDownOpen)
                return;

            var popup = _popup!;
            _hoverPreview = false;
            _hoverIndex = -1;

            // Only the paths where the popup is still the foreground window owe focus back:
            // item click, Escape/Enter and keyboard select. When we arrive via
            // popup.Deactivate, whatever the user clicked has already taken focus and
            // stealing it back would yank the caret out from under them.
            // WM_KILLFOCUS is delivered before WM_ACTIVATE(WA_INACTIVE), so ContainsFocus is
            // already false on the deactivate path and both tests below correctly say no.
            bool giveFocusBack = ReferenceEquals(Form.ActiveForm, popup) || popup.ContainsFocus;

            popup.Hide();

            if (giveFocusBack)
                RestoreOwnerFocus();

            Invalidate();
            DropDownVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// The popup is a real top-level window, so showing it made it the foreground window.
        /// Hiding a foreground window leaves the process owning no foreground window at all, and
        /// Control.Focus() cannot create one - that is why the main window used to stay inactive
        /// with a greyed title bar until the user clicked it. Put the owner back explicitly.
        /// Activation is asynchronous, so the focus hand-off is retried on the next pump turn.
        /// </summary>
        private void RestoreOwnerFocus()
        {
            var owner = FindForm();
            if (owner == null || owner.IsDisposed || !owner.IsHandleCreated)
                return;

            try
            {
                owner.Activate();
            }
            catch
            {
                // Best effort - the queued hand-off below retries.
            }

            try
            {
                if (IsDisposed || !IsHandleCreated)
                    return;

                BeginInvoke(() =>
                {
                    try
                    {
                        if (IsDisposed || IsDropDownOpen)
                            return;

                        // If the user has since moved to a different window, leave them alone.
                        Form? active = Form.ActiveForm;
                        if (active != null && !ReferenceEquals(active, owner))
                            return;

                        if (!ReferenceEquals(Form.ActiveForm, owner))
                            owner.Activate();

                        if (!ContainsFocus)
                            Focus();
                    }
                    catch
                    {
                        // A focus hand-off must never break the close path.
                    }
                });
            }
            catch
            {
                // BeginInvoke can fail while the form is tearing down; Activate already ran.
            }
        }

        private Form CreatePopup()
        {
            var list = new ThemedList { Dock = DockStyle.Fill };
            list.Metrics = _m;
            list.DropDown = this;
            list.ItemHeight = _m.S(26);

            // Hovering a row previews it in the field.
            list.IndexPreviewChanged += (s, e) =>
            {
                _hoverIndex = list.HoverIndex;
                Invalidate();
            };

            // One commit path for click and Enter, so ordering is identical for both:
            // stop previewing, close, then publish the selection exactly once.
            list.ItemCommitted += (s, e) => CommitIndex(list.SelectedIndex);

            var host = new Panel { Dock = DockStyle.Fill, BackColor = T.Line, Padding = new Padding(1) };
            host.Controls.Add(list);

            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                BackColor = T.Line,
                Padding = new Padding(1),
                MinimizeBox = false,
                MaximizeBox = false,
            };
            form.Controls.Add(host);
            form.Tag = list;
            form.Deactivate += (s, e) => CloseDropDown();
            form.FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; CloseDropDown(); }
            };
            return form;
        }

        private void RefreshList()
        {
            if (_popup is { Visible: true })
            {
                int keep = _selectedIndex;
                CloseDropDown();
                if (_items.Count > 0)
                {
                    SelectedIndex = Math.Min(keep, _items.Count - 1);
                    OpenDropDown();
                }
            }
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _popup?.Dispose();
                _popup = null;
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ input

        protected override bool IsInputKey(Keys keyData)
            => keyData is Keys.Up or Keys.Down or Keys.Enter or Keys.Space
                || base.IsInputKey(keyData);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left)
                ToggleDropDown();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (IsDropDownOpen && e.KeyCode is Keys.Up or Keys.Down or Keys.Enter or Keys.Space or Keys.Escape)
            {
                // the open list normally owns the keyboard; this is the safety net for the
                // moment focus has not landed on it yet
                HandleDropDownKey(e.KeyCode, e);
                return;
            }

            switch (e.KeyCode)
            {
                case Keys.Down:
                    MoveSelection(1);
                    e.Handled = true;
                    break;
                case Keys.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Keys.Space:
                case Keys.Enter:
                    ToggleDropDown();
                    e.Handled = true;
                    break;
                case Keys.Escape:
                    CloseDropDown();
                    e.Handled = true;
                    break;
            }
            base.OnKeyDown(e);
        }

        private void MoveSelection(int delta)
        {
            if (_items.Count == 0)
                return;
            if (!IsDropDownOpen)
            {
                SelectedIndex = _selectedIndex < 0
                    ? 0
                    : Math.Clamp(_selectedIndex + delta, 0, _items.Count - 1);
                return;
            }
            var list = (ThemedList)_popup!.Tag!;
            SetListIndex(list, list.SelectedIndex + delta);
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        // ------------------------------------------------------------------ paint

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Ink.Prepare(g);
            g.Clear(Ink.ParentColor(Parent, T.Root));

            var r = new RectangleF(0.5f, 0.5f, Math.Max(0, ClientSize.Width - 1f), Math.Max(0, ClientSize.Height - 1f));
            float rad = _m.Radius(UiTheme.RadiusField);
            bool open = IsDropDownOpen;
            bool on = open || Focused;

            Color fill = _hover || open ? Ink.Lift(T.Sunken, 0.06f) : T.Sunken;
            Color edge = on ? T.Accent : T.Line;
            Color fg = Enabled ? T.Text : Ink.Alpha(T.TextFaint, 0.55f);

            Ink.Fill(g, r, rad, fill);
            Ink.Outline(g, r, rad, edge, 1f);

            int pad = _m.S(10);
            int arrowW = _m.S(18);
            var textArea = new RectangleF(r.X + pad, r.Y, r.Width - pad * 2 - arrowW, r.Height);
            string text = DisplayText;
            if (text.Length > 0)
            {
                var sz = Ink.Measure(g, text, Font);
                if (sz.Width > textArea.Width)
                {
                    // simple ellipsis
                    int chars = text.Length;
                    while (chars > 1 && Ink.Measure(g, text[..(chars - 1)] + "\u2026", Font).Width > textArea.Width)
                        chars--;
                    text = text[..(chars - 1)] + "\u2026";
                }
            }
            Ink.Text(g, text, Font, textArea, fg, StringAlignment.Near, StringAlignment.Center);

            // chevron
            float cx = r.Right - arrowW * 0.55f;
            float cy = r.Height * 0.5f;
            float s = _m.F(3.2f);
            using var pen = new Pen(open ? T.Accent : T.TextFaint, Math.Max(1f, _m.F(1.4f)))
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(cx - s, cy - s * 0.55f),
                new PointF(cx, cy + s * 0.5f),
                new PointF(cx + s, cy - s * 0.55f),
            });
        }

        // ------------------------------------------------------------------ popup list

        /// <summary>
        /// The drop-down's item list. This is deliberately NOT a ListBox: the native
        /// owner-draw path leaves a white background and no text (the system erase uses
        /// SystemColors.Window, and the themed row callback is never reliably reached), so
        /// this paints every pixel itself and owns its own hit-testing, scrolling and
        /// keyboard handling.
        /// </summary>
        private sealed class ThemedList : Control
        {
            private UiMetrics _metrics = new();
            private readonly List<object> _items = new();
            private int _itemHeight = 26;
            private int _hoverIndex = -1;
            private int _scroll;

            /// <summary>Back-reference so hovering a row can drive the field's preview text.</summary>
            public ThemedDropDown? DropDown { get; set; }

            public UiMetrics Metrics
            {
                get => _metrics;
                set { _metrics = value; BackColor = value.Theme?.Sunken ?? Color.Black; ForeColor = value.Theme?.TextSoft ?? Color.Black; }
            }

            private UiTheme T => _metrics.Theme ?? throw new InvalidOperationException("Theme not attached.");

            public IReadOnlyList<object> Items => _items;

            public int ItemHeight
            {
                get => _itemHeight;
                set { _itemHeight = Math.Max(8, value); Invalidate(); }
            }

            public int SelectedIndex
            {
                get => _selectedIndex;
                set
                {
                    int v = _items.Count == 0 ? -1 : Math.Clamp(value, -1, _items.Count - 1);
                    if (v == _selectedIndex) return;
                    _selectedIndex = v;
                    EnsureVisible();
                    Invalidate();
                }
            }
            private int _selectedIndex = -1;

            /// <summary>Row under the pointer; drives the field's live preview text.</summary>
            public int HoverIndex
            {
                get => _hoverIndex;
                set
                {
                    if (_hoverIndex == value) return;
                    _hoverIndex = value;
                    IndexPreviewChanged?.Invoke(this, EventArgs.Empty);
                    Invalidate();
                }
            }

            public event EventHandler? IndexPreviewChanged;

            /// <summary>Raised once when the user picks a row (click) or confirms the selection (Enter).</summary>
            public event EventHandler? ItemCommitted;

            public ThemedList()
            {
                SetStyle(ControlStyles.UserPaint
                       | ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.Selectable, true);
                BackColor = Color.Black;
                TabStop = true;
            }

            public void SetItems(IEnumerable source)
            {
                _items.Clear();
                foreach (object? item in source)
                    _items.Add(item!);
                _selectedIndex = -1;
                _hoverIndex = -1;
                _scroll = 0;
                Invalidate();
            }

            private int VisibleRows => ItemHeight <= 0 ? 0 : ClientSize.Height / ItemHeight;

            private void EnsureVisible()
            {
                if (_selectedIndex < 0) return;
                if (_selectedIndex < _scroll) _scroll = _selectedIndex;
                else if (VisibleRows > 0 && _selectedIndex >= _scroll + VisibleRows)
                    _scroll = _selectedIndex - VisibleRows + 1;
                if (_scroll < 0) _scroll = 0;
            }

            private int RowAt(Point p)
            {
                int row = _scroll + p.Y / ItemHeight;
                return row >= 0 && row < _items.Count ? row : -1;
            }

            // ------------------------------------------------------------------ input

            protected override bool IsInputKey(Keys keyData)
                => keyData is Keys.Up or Keys.Down or Keys.Enter or Keys.Space or Keys.Escape
                    || base.IsInputKey(keyData);

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                HoverIndex = RowAt(e.Location);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                HoverIndex = -1;
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                Focus();
                if (e.Button == MouseButtons.Left)
                    HoverIndex = RowAt(e.Location);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Left) return;
                int row = RowAt(e.Location);
                if (row < 0) return;
                SelectedIndex = row;
                ItemCommitted?.Invoke(this, EventArgs.Empty);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                if (_items.Count <= VisibleRows) return;
                _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta), 0, Math.Max(0, _items.Count - VisibleRows));
                Invalidate();
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                // one source of truth: the field owns the semantics, the list just forwards
                DropDown?.HandleDropDownKey(e.KeyCode, e);
            }

            // ------------------------------------------------------------------ paint

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                Ink.Prepare(g);
                g.Clear(T.Sunken);

                int w = ClientSize.Width;
                float y = -_scroll * ItemHeight;
                for (int i = 0; i < _items.Count; i++)
                {
                    if (y > ClientSize.Height) break;
                    var row = new RectangleF(0, y, w, ItemHeight);
                    bool sel = i == SelectedIndex;

                    if (sel)
                    {
                        Ink.Fill(g, new RectangleF(_metrics.S(3), row.Y + _metrics.S(2), w - _metrics.S(6), row.Height - _metrics.S(4)),
                            _metrics.F(4), Ink.Alpha(T.Accent, 30));
                        Ink.Fill(g, new RectangleF(_metrics.S(3), row.Y + _metrics.S(4), _metrics.F(2), row.Height - _metrics.S(8)),
                            _metrics.F(1), T.Accent);
                    }
                    else if (i == HoverIndex)
                    {
                        Ink.Fill(g, new RectangleF(_metrics.S(3), row.Y + _metrics.S(2), w - _metrics.S(6), row.Height - _metrics.S(4)),
                            _metrics.F(4), T.Raised);
                    }

                    string text = _items[i]?.ToString() ?? string.Empty;
                    var area = new RectangleF(_metrics.S(11), row.Y, w - _metrics.S(18), row.Height);
                    Ink.Text(g, text, T.FSmall, area, sel ? T.Text : T.TextSoft, StringAlignment.Near, StringAlignment.Center);

                    y += ItemHeight;
                }

                if (Focused)
                {
                    var ring = new RectangleF(0.5f, 0.5f, w - 1f, Math.Max(0, ClientSize.Height - 1f));
                    Ink.Outline(g, ring, _metrics.Radius(UiTheme.RadiusField), Ink.Alpha(T.Accent, 110), 1f);
                }
            }
        }
    }
}
