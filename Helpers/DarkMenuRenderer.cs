using System.Drawing;
using System.Windows.Forms;

namespace CopilotUsage.Helpers;

/// <summary>
/// Custom WinForms ToolStrip renderer that paints the tray context menu
/// in the app's dark Claude Code colour palette.
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
	private static readonly Color BgColor       = Color.FromArgb( 0x1A, 0x1A, 0x1A );
	private static readonly Color HoverBg       = Color.FromArgb( 0x2E, 0x2E, 0x2E );
	private static readonly Color PressedBg     = Color.FromArgb( 0x3A, 0x3A, 0x3A );
	private static readonly Color TextColor     = Color.FromArgb( 0xEB, 0xEB, 0xEB );
	private static readonly Color SeparatorColor= Color.FromArgb( 0x3A, 0x3A, 0x3A );
	private static readonly Color BorderColor   = Color.FromArgb( 0x33, 0x33, 0x33 );
	private static readonly Color AccentColor   = Color.FromArgb( 0xD4, 0x84, 0x5A );

	public DarkMenuRenderer() : base( new DarkColorTable() ) { }

	// ── Menu drop-down background ─────────────────────────────────────────
	protected override void OnRenderToolStripBackground( ToolStripRenderEventArgs e )
	{
		using var brush = new SolidBrush( BgColor );
		e.Graphics.FillRectangle( brush, e.AffectedBounds );
	}

	// ── Drop-down border ──────────────────────────────────────────────────
	protected override void OnRenderToolStripBorder( ToolStripRenderEventArgs e )
	{
		using var pen = new Pen( BorderColor );
		var r = e.AffectedBounds;
		e.Graphics.DrawRectangle( pen, r.X, r.Y, r.Width - 1, r.Height - 1 );
	}

	// ── Item background (normal + hover + pressed) ────────────────────────
	protected override void OnRenderMenuItemBackground( ToolStripItemRenderEventArgs e )
	{
		var item = e.Item;
		var bounds = new Rectangle( 2, 0, item.Width - 4, item.Height );

		Color fill;
		if ( !item.Enabled )
			fill = BgColor;
		else if ( item.Pressed )
			fill = PressedBg;
		else if ( item.Selected )
			fill = HoverBg;
		else
			fill = BgColor;

		using var brush = new SolidBrush( fill );
		e.Graphics.FillRectangle( brush, bounds );

		// Accent left bar on hover
		if ( item.Selected && item.Enabled )
		{
			using var accentBrush = new SolidBrush( AccentColor );
			e.Graphics.FillRectangle( accentBrush, new Rectangle( 2, 2, 2, item.Height - 4 ) );
		}
	}

	// ── Item text ─────────────────────────────────────────────────────────
	protected override void OnRenderItemText( ToolStripItemTextRenderEventArgs e )
	{
		e.TextColor = !e.Item.Enabled
			? Color.FromArgb( 0x55, 0x55, 0x55 )
			: e.Item.ForeColor != SystemColors.ControlText
				? e.Item.ForeColor   // respect explicit ForeColor (used for active provider highlight)
				: TextColor;
		base.OnRenderItemText( e );
	}

	// ── Separator ─────────────────────────────────────────────────────────
	protected override void OnRenderSeparator( ToolStripSeparatorRenderEventArgs e )
	{
		int y = e.Item.Height / 2;
		using var pen = new Pen( SeparatorColor );
		e.Graphics.DrawLine( pen, 8, y, e.Item.Width - 8, y );
	}

	// ── Check mark (active provider indicator) ───────────────────────────
	protected override void OnRenderItemCheck( ToolStripItemImageRenderEventArgs e )
	{
		var r = e.ImageRectangle;
		if ( r.IsEmpty ) return;

		var boxRect = new Rectangle( r.X + 1, r.Y + 2, r.Width - 2, r.Height - 4 );
		using ( var bg = new SolidBrush( Color.FromArgb( 0x2A, 0x2A, 0x2A ) ) )
			e.Graphics.FillRectangle( bg, boxRect );
		using ( var border = new Pen( AccentColor ) )
			e.Graphics.DrawRectangle( border, boxRect );

		var g = e.Graphics;
		g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
		using var pen = new Pen( AccentColor, 1.5f )
		{
			StartCap = System.Drawing.Drawing2D.LineCap.Round,
			EndCap   = System.Drawing.Drawing2D.LineCap.Round,
			LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
		};
		float cx = r.X + r.Width  / 2f;
		float cy = r.Y + r.Height / 2f;
		g.DrawLines( pen, new PointF[]
		{
			new( cx - 3.5f, cy        ),
			new( cx - 1f,   cy + 2.5f ),
			new( cx + 3.5f, cy - 3f   ),
		} );
	}

	// ── Arrow (sub-menu indicator) ────────────────────────────────────────
	protected override void OnRenderArrow( ToolStripArrowRenderEventArgs e )
	{
		e.ArrowColor = TextColor;
		base.OnRenderArrow( e );
	}

	// ── Colour table (disables gradient chrome) ───────────────────────────
	private sealed class DarkColorTable : ProfessionalColorTable
	{
		public override Color MenuBorder                    => BorderColor;
		public override Color MenuItemBorder               => Color.Transparent;
		public override Color MenuItemSelected             => HoverBg;
		public override Color MenuItemSelectedGradientBegin => HoverBg;
		public override Color MenuItemSelectedGradientEnd   => HoverBg;
		public override Color MenuItemPressedGradientBegin  => PressedBg;
		public override Color MenuItemPressedGradientEnd    => PressedBg;
		public override Color ToolStripDropDownBackground   => BgColor;
		public override Color ImageMarginGradientBegin      => BgColor;
		public override Color ImageMarginGradientMiddle     => BgColor;
		public override Color ImageMarginGradientEnd        => BgColor;
	}
}
