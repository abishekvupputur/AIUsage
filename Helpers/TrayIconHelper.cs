using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using DrawingColor = System.Drawing.Color;

namespace CopilotUsage.Helpers;

internal static class TrayIconHelper
{
	private const int IconSize = 32;

	// BorderProgress constants
	private const float BorderWidth = 4.0f;

	private static Bitmap? s_IconBase;


	/// <summary>
	/// Creates the tray icon for the given usage percentage.
	/// Layers: AI icon bitmap → thick progress line tracing the perimeter clockwise.
	/// Pass <c>null</c> when data is unavailable.
	/// </summary>
	public static Icon CreateUsageIcon( double? usagePercent )
	{
		using var bitmap = new Bitmap( IconSize, IconSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb );
		using var g = Graphics.FromImage( bitmap );
		g.SmoothingMode = SmoothingMode.AntiAlias;

		DrawIconBase( g );

		var fillRgb = GetFillColorRgb( usagePercent );
		float half = BorderWidth / 2f;
		float side = IconSize - BorderWidth;
		var topLeft     = new PointF( half,           half           );
		var topRight    = new PointF( IconSize - half, half           );
		var bottomRight = new PointF( IconSize - half, IconSize - half );
		var bottomLeft  = new PointF( half,           IconSize - half );

		var segments = new (PointF From, PointF To)[]
		{
			( topLeft,     topRight    ),
			( topRight,    bottomRight ),
			( bottomRight, bottomLeft  ),
			( bottomLeft,  topLeft     ),
		};

		float totalPerimeter = side * 4f;
		float progressLength = usagePercent.HasValue
			? (float) ( usagePercent.Value / 100.0 * totalPerimeter )
			: totalPerimeter;

		if ( progressLength <= 0 )
			return BitmapToIcon( bitmap );

		using var pen = new Pen( fillRgb, BorderWidth )
		{
			StartCap = LineCap.Round,
			EndCap   = LineCap.Round,
		};

		float remaining = progressLength;
		foreach ( var (from, to) in segments )
		{
			if ( remaining <= 0 ) break;

			float segLen = Distance( from, to );
			if ( remaining >= segLen )
			{
				g.DrawLine( pen, from, to );
				remaining -= segLen;
			}
			else
			{
				float t = remaining / segLen;
				var partial = new PointF(
					from.X + ( to.X - from.X ) * t,
					from.Y + ( to.Y - from.Y ) * t );
				g.DrawLine( pen, from, partial );
				remaining = 0;
			}
		}

		return BitmapToIcon( bitmap );
	}

	private static float Distance( PointF a, PointF b )
	{
		float dx = b.X - a.X;
		float dy = b.Y - a.Y;
		return (float) Math.Sqrt( dx * dx + dy * dy );
	}


	// ── Icon loading ─────────────────────────────────────────────────────────────

	/// <summary>
	/// Loads and caches the AI-icon.ico embedded resource as a 32×32 bitmap.
	/// </summary>
	private static Bitmap? GetIconBase()
	{
		if ( s_IconBase != null ) return s_IconBase;

		var asm  = Assembly.GetExecutingAssembly();
		var name = asm.GetManifestResourceNames()
			.FirstOrDefault( n => n.EndsWith( "AI-icon.ico", StringComparison.OrdinalIgnoreCase ) );

		if ( name == null ) return null;

		using var stream = asm.GetManifestResourceStream( name );
		if ( stream == null ) return null;

		using var ico = new Icon( stream, IconSize, IconSize );
		s_IconBase = ico.ToBitmap();
		return s_IconBase;
	}

	private static void DrawIconBase( Graphics g )
	{
		var bmp = GetIconBase();
		if ( bmp != null )
			g.DrawImage( bmp, 0, 0, IconSize, IconSize );
	}


	// ── GDI helpers ──────────────────────────────────────────────────────────────

	private static Icon BitmapToIcon( Bitmap bitmap )
	{
		var hIcon = bitmap.GetHicon();
		try
		{
			return (Icon) Icon.FromHandle( hIcon ).Clone();
		}
		finally
		{
			DestroyIcon( hIcon );
		}
	}

	private static DrawingColor GetFillColorRgb( double? usagePercent )
	{
		if ( !usagePercent.HasValue )
			return DrawingColor.FromArgb( 108, 117, 125 );  // grey — error/unknown

		return usagePercent.Value switch
		{
			< 60 => DrawingColor.FromArgb( 40,  167, 69  ),  // green
			< 80 => DrawingColor.FromArgb( 255, 193, 7   ),  // amber
			_    => DrawingColor.FromArgb( 220, 53,  69  ),  // red
		};
	}


	[DllImport( "user32.dll" )]
	private static extern bool DestroyIcon( IntPtr handle );

	[DllImport( "gdi32.dll" )]
	private static extern bool DeleteObject( IntPtr hObject );


	/// <summary>
	/// Returns a WPF <see cref="System.Windows.Media.Imaging.BitmapSource"/> of the app icon
	/// for use as <c>Window.Icon</c> or an <c>Image.Source</c>.
	/// </summary>
	public static System.Windows.Media.Imaging.BitmapSource? GetWpfImageSource()
	{
		var bmp = GetIconBase();
		if ( bmp == null ) return null;

		var hBmp = bmp.GetHbitmap();
		try
		{
			return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
				hBmp,
				IntPtr.Zero,
				System.Windows.Int32Rect.Empty,
				System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions() );
		}
		finally
		{
			DeleteObject( hBmp );
		}
	}
}
