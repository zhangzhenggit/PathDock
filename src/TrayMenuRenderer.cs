using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PathDock;

internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    public TrayMenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var inset = Scale(e.ToolStrip, 3);
        using var brush = new SolidBrush(Color.FromArgb(232, 240, 250));
        e.Graphics.FillRectangle(brush, new Rectangle(inset, 0, e.Item.Width - inset * 2, e.Item.Height));
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var bounds = e.ImageRectangle;
        var size = Scale(e.ToolStrip, 12);
        var x = bounds.Left + (bounds.Width - size) / 2f;
        var y = bounds.Top + (bounds.Height - size) / 2f;
        var state = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(0, 103, 192), Scale(e.ToolStrip, 2))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        e.Graphics.DrawLines(pen, new[] { new PointF(x + size * .12f, y + size * .5f),
            new PointF(x + size * .4f, y + size * .78f), new PointF(x + size * .9f, y + size * .2f) });
        e.Graphics.Restore(state);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var inset = Scale(e.ToolStrip, 10);
        using var pen = new Pen(Color.FromArgb(226, 229, 234));
        e.Graphics.DrawLine(pen, inset, e.Item.Height / 2, e.Item.Width - inset, e.Item.Height / 2);
    }

    static int Scale(ToolStrip? strip, int value) => (int)System.Math.Round(value * (strip?.DeviceDpi ?? 96) / 96d);

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(250, 250, 250);
        public override Color MenuBorder => Color.FromArgb(210, 214, 220);
    }
}
