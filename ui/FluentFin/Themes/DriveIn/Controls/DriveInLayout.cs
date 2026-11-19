using Windows.Foundation;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>One row of parked cars: their baseline, their scale (1 = 220 DIP wide) and each car's left edge.</summary>
public sealed record CarRow(double Base, double Scale, IReadOnlyList<double> Xs, bool Near);

/// <summary>
/// Drive-in: where everything of the lot goes at a window width (the scene, the outdoor screen, the Coming up sign, the
/// sun, the cars), measured from the concept's two layouts (1920 and 1100 wide) and spread between them. Three states:
/// wide from <see cref="WideFrom"/> (the sign with thumbnails and four entries), narrow from <see cref="SignFrom"/> (three
/// entries, no thumbnails, no overview on the screen), and below that or in Compact Home no sign (its entries become a
/// row). Compact Home folds the whole lot into a <see cref="CompactHeight"/> strip with no screen.
/// </summary>
public sealed record DriveInLayout(
	double Width,
	bool Compact,
	bool Wide,
	double Margin,
	double SceneHeight,
	double Ground,
	Rect Screen,
	Rect Board,
	Rect Arrow,
	Point Sun,
	double SunRadius,
	IReadOnlyList<CarRow> Cars,
	double RowsTop,
	int Legs = 3)
{
	public const double WideFrom = 1600, SignFrom = 1000;

	/// <summary>Compact Home's strip, the top bar included (the concept: "folds it into a 200 px strip").</summary>
	public const double CompactHeight = 200;

	/// <summary>The outdoor screen's widest (an ultrawide window does not get a cinema-sized hero).</summary>
	public const double MaxScreenWidth = 1280;

	public bool HasScreen => Screen.Width > 0;

	public bool HasSign => Board.Width > 0;

	/// <summary>The screen's overview, the favourite button and the sign's thumbnails and Play buttons: wide only.</summary>
	public bool ShowDetails => Wide;

	/// <summary>
	/// Home at <paramref name="width"/> DIP. <paramref name="pageTop"/>: how far below the window's top the page starts (0 when it
	/// runs under the top bar, the bar's height when it starts below it); the concept places the screen 84 DIP below the
	/// window's top (76 narrow), so a page that starts lower moves the lot up by as much (never closer than 20 to the page's top).
	/// </summary>
	public static DriveInLayout Home(double width, bool compact, double pageTop = 0)
	{
		width = Math.Max(320, width);
		// the concept's margins: 40 at 1100, 96 at 1920, in between in proportion
		var margin = MarginFor(width);
		if (compact)
		{
			var ground = CompactHeight - 34;
			var k = 0.42;
			return new DriveInLayout(width, true, false, margin, CompactHeight, ground, Rect.Empty, Rect.Empty, Rect.Empty,
				new Point(width * 0.72, ground - 30), 56, [Row(ground + 22, k, margin, width - margin, 330 * k / 0.56, near: false)],
				CompactHeight + 20);
		}

		var wide = width >= WideFrom;
		var sign = width >= SignFrom;
		var right = Math.Max(24, margin - 8);
		var boardWidth = sign ? (wide ? 444 : 352) : 0;
		var boardX = sign ? width - right - boardWidth : width - margin;
		var gap = sign ? 36 : 0;
		var maxScreen = Math.Min(MaxScreenWidth, Math.Max(640, Math.Round(width * 0.5209)));
		var screenWidth = Math.Round(Math.Max(280, Math.Min(maxScreen, boardX - gap - margin)));
		var screenTop = Math.Max(20, (wide ? 84 : 76) - Math.Clamp(pageTop, 0, 64));
		var screenHeight = Math.Ceiling(screenWidth * 9 / 16);
		var screen = new Rect(margin, screenTop, screenWidth, screenHeight);
		var groundY = Math.Round(screen.Bottom + (wide ? 44 : 64));
		var sceneHeight = groundY + (wide ? 50 : 40);

		var entries = Core.ViewModels.DriveInHome.SignEntries(wide);
		var arrowHeight = wide ? 74 : 58;
		var arrow = sign ? new Rect(boardX - (wide ? 24 : 20), screenTop, boardWidth + (wide ? 24 : 20), arrowHeight) : Rect.Empty;
		var boardTop = arrow.IsEmpty ? 0 : arrow.Bottom + (wide ? 10 : 16);
		// the art pass: the concept's 136 left the link "All of Up next" on the last entry; room for it below the entries
		var board = sign ? new Rect(boardX, boardTop, boardWidth, entries * (wide ? 76 : 60) + (wide ? 160 : 150)) : Rect.Empty;

		var s = screenWidth / 1000;
		var sunRadius = Math.Round(Math.Clamp(60 + (screenWidth - 640) / 360 * 56, 52, 150));
		var sunX = sign ? (screen.Right + boardX) / 2 : width - margin - sunRadius * 1.4;
		// high enough that four of its five slices clear the hills (the concept's art pass B)
		var sun = new Point(Math.Round(sunX), Math.Round(groundY - sunRadius * 1.3 - 18));

		var farK = 0.56 * s;
		var nearK = 0.84 * s;
		var stopFar = sign ? board.Left + board.Width * 0.3 : width - margin;
		var cars = new List<CarRow>
		{
			Row(groundY + 22 * s, farK, margin + 154 * s, stopFar, 360 * s, near: false),
			Row(groundY + 52 * s, nearK, margin - 36 * s, screen.Right - 100 * s + 220 * nearK, 370 * s, near: true),
		};

		return new DriveInLayout(width, false, wide, margin, sceneHeight, groundY, screen, board, arrow, sun, sunRadius, cars, sceneHeight + (wide ? 20 : 14));
	}

	/// <summary>
	/// A page header's strip of the lot (the Library's letter board sits on it, never the posters): the sky and the hills,
	/// the sun setting in the middle, no screen, sign or cars. Nixie 1.4.0 (the UX review's DI-m1): the sun sat behind Sort
	/// and Filter (light words on its white disc); it sets mid-strip now, its top below the toolbar's row.
	/// </summary>
	public static DriveInLayout Strip(double width, double height)
	{
		width = Math.Max(320, width);
		height = Math.Max(120, height);
		var ground = height - 30;
		var radius = Math.Round(Math.Clamp(height * 0.28, 48, 96));
		return new DriveInLayout(width, false, width >= WideFrom, MarginFor(width), height, ground, Rect.Empty, Rect.Empty, Rect.Empty,
			new Point(Math.Round(width * 0.5), ground + radius * 0.1), radius, [], height);
	}

	/// <summary>A page's side margin at a width: 40 at 1100, 96 at 1920 (the concept's), in proportion between.</summary>
	public static double MarginFor(double width) => Math.Clamp(Math.Round(40 + (width - 1100) * 56 / 820), 24, 96);

	/// <summary>The sign-in page's lot: the screen in the middle (the form is on it), the sun to the right, two rows of cars.</summary>
	public static DriveInLayout SignIn(double width, double height)
	{
		width = Math.Max(320, width);
		height = Math.Max(320, height);
		// the shared sign-in form is centred in the window: the screen frames it, centred too (tall enough for the form), and
		// the ground goes down below the screen's legs on a short window
		var screenHeight = Math.Round(Math.Clamp(height * 0.55, 600, 720));
		var screenWidth = Math.Round(Math.Min(screenHeight * 1.3, width - 80));
		var top = Math.Max(40, Math.Round((height - screenHeight) / 2));
		var screen = new Rect(Math.Round((width - screenWidth) / 2), top, screenWidth, screenHeight);
		var ground = Math.Max(Math.Round(height * 0.815), screen.Bottom + 24);
		var radius = Math.Round(Math.Clamp(height * 0.14, 70, 170));
		var sun = new Point(Math.Round(width * 0.81), ground - radius * 0.22);
		var k = Math.Clamp(height / 1080, 0.6, 1.3);
		var cars = new List<CarRow>
		{
			new(ground + 30 * k, 0.6 * k, [.. new[] { 0.06, 0.22, 0.72, 0.865 }.Select(f => Math.Round(width * f))], false),
			new(ground + 120 * k, 0.95 * k, [.. new[] { 0.115, 0.78 }.Select(f => Math.Round(width * f))], true),
		};
		return new DriveInLayout(width, false, true, 0, height, ground, screen, Rect.Empty, Rect.Empty, sun, radius, cars, height);
	}

	/// <summary>
	/// Nixie 1.3.1: the theatre window's little lot (the trailer's frame): a small screen on two posts at <paramref name="screen"/>,
	/// the ground a little below it, the sun low at the right peeking past the screen, and two cars in front at its corners with
	/// their speaker posts, small enough to stay below the screen.
	/// </summary>
	public static DriveInLayout Theatre(double width, double height, Rect screen)
	{
		// exactly the lot's size (never more: the lot is sized by its stage, and a scene larger than it would grow it)
		width = Math.Max(1, width);
		height = Math.Max(1, height);
		var ground = Math.Round(Math.Min(height - 30, screen.Bottom + 50));
		var k = Math.Clamp(screen.Width / 1150, 0.55, 1.2);
		var radius = Math.Round(54 * k);
		// half of it peeks past the screen's right edge, high over the hills
		var sun = new Point(Math.Round(screen.Right + radius * 0.25), Math.Round(screen.Y + screen.Height * 0.58));
		var scale = Math.Round(0.62 * k, 3);
		var carWidth = 220 * scale;
		var cars = new List<CarRow>
		{
			new(Math.Round(height - 16), scale, [Math.Round(screen.X - carWidth * 0.18), Math.Round(screen.Right - carWidth * 0.82)], true),
		};
		return new DriveInLayout(width, false, true, 0, height, ground, screen, Rect.Empty, Rect.Empty, sun, radius, cars, height, Legs: 2);
	}

	/// <summary>Cars along a row from <paramref name="from"/>, one every <paramref name="step"/>, while a whole car still ends before <paramref name="to"/>.</summary>
	private static CarRow Row(double baseline, double scale, double from, double to, double step, bool near)
	{
		var xs = new List<double>();
		for (var x = from; x + 220 * scale <= to && xs.Count < 12; x += Math.Max(40, step))
		{
			xs.Add(Math.Round(x));
		}

		return new CarRow(Math.Round(baseline), scale, xs, near);
	}
}
