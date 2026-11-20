using FluentFin.Core.Contracts.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.4.0 (bug hunt m4: a slow library's first poster took the keyboard out of the search box the user had typed in): the
/// focus a library's load asks for (the first poster on arrival, the one Back or R puts back) asks the app's one arrival rule
/// (<see cref="LibraryViewModel.LoadFocusYields"/>), in every theme, through <see cref="LibraryViewModel.FocusYields"/>, which
/// each theme's page asks before it focuses; a card the user asked for (an A to Z jump, a page's type-to-jump) never yields.
/// </summary>
[Collection(nameof(LibraryLoadFocusTests))] // LoadFocusYields is the app's one (static) rule
public sealed class LibraryLoadFocusTests
{
	private static IJellyfinClient Server(int count)
	{
		var items = Enumerable.Range(0, count).Select(i => new BaseItemDto { Id = Guid.NewGuid(), Name = $"T{i:D3}", Type = BaseItemDto_Type.Movie }).ToList();
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.UserId).Returns(Guid.NewGuid());
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = items });
		return client.Object;
	}

	private static BaseItemDto Movies() => new() { Id = Guid.NewGuid(), Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies };

	[Fact]
	public async Task TheArrivalsPoster_AsksTheArrivalRule_AJumpDoesNot()
	{
		var asked = new List<LibraryViewModel>();
		var userMoved = true;
		LibraryViewModel.LoadFocusYields = vm => { asked.Add(vm); return userMoved; };
		try
		{
			var library = Movies();
			var vm = new LibraryViewModel(Server(120));
			await vm.OnNavigatedTo(library);

			// the arrival: the first poster, unless the user moved meanwhile
			Assert.Equal((0, true), (vm.FocusRequest, vm.ArrivalFocus));
			Assert.True(vm.FocusYields!());
			Assert.Same(vm, Assert.Single(asked));
			userMoved = false;
			Assert.False(vm.FocusYields!());

			// an A to Z jump the user pressed: the card, whatever the arrival rule says
			userMoved = true;
			vm.FocusRequest = -1;
			vm.JumpTo(vm.Letters.First(l => l.Letter == "T"));
			Assert.True(vm.FocusRequest >= 0);
			Assert.False(vm.FocusYields!());

			// a page's own type-to-jump (Editorial sets FocusRequest itself): the user's too
			vm.FocusRequest = -1;
			vm.FocusRequest = 57;
			Assert.False(vm.FocusYields!());
		}
		finally
		{
			LibraryViewModel.LoadFocusYields = null;
		}
	}

	[Fact]
	public async Task BackPutsThePosterBack_AndItAsksTheArrivalRuleToo()
	{
		LibraryViewModel.LoadFocusYields = _ => true;
		try
		{
			var library = Movies();
			var server = Server(120);
			var vm = new LibraryViewModel(server);
			await vm.OnNavigatedTo(library);
			vm.LeftAt(vm.Items[40], offset: 900);

			// Back: a new view model for the same library and user, its place put back
			var back = new LibraryViewModel(server) { ComingBack = true };
			await back.OnNavigatedTo(library);
			Assert.True(back.FocusRequest >= 0);
			Assert.True(back.FocusYields!());

			// a page's own rule is asked too, for the load's focus only
			LibraryViewModel.LoadFocusYields = _ => false;
			var pageRule = true;
			back.FocusYields = () => pageRule;
			Assert.True(back.FocusYields!());
			pageRule = false;
			Assert.False(back.FocusYields!());
		}
		finally
		{
			LibraryViewModel.LoadFocusYields = null;
		}
	}

	[Theory]
	[InlineData(true, false, false, false)]
	[InlineData(true, true, false, true)]
	[InlineData(true, false, true, true)]
	[InlineData(false, true, true, false)]   // the user's own jump never yields
	public void OnlyALoadsFocus_Yields(bool fromLoad, bool pageRule, bool arrivalRule, bool yields) =>
		Assert.Equal(yields, LibraryViewModel.FocusYieldsNow(fromLoad, pageRule, arrivalRule));
}
