using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

public sealed class SearchResultTreeMapperTests
{
    private readonly SearchResultTreeMapper _sut = new();

    [Fact]
    public void Map_groups_files_and_compacts_single_folder_chain()
    {
        var groups = new[]
        {
            new SearchFileGroup("c:/work/src/app/a.cs", "src/app/a.cs", Array.Empty<SearchMatchItem>()),
            new SearchFileGroup("c:/work/src/app/b.cs", "src/app/b.cs", Array.Empty<SearchMatchItem>()),
        };

        var folder = Assert.IsType<SearchFolderNode>(Assert.Single(_sut.Map(groups)));

        Assert.Equal("src/app", folder.Name);
        Assert.Equal(2, folder.Children.Count);
    }

    [Fact]
    public void Map_places_folders_before_root_files()
    {
        var groups = new[]
        {
            new SearchFileGroup("c:/work/z.cs", "z.cs", Array.Empty<SearchMatchItem>()),
            new SearchFileGroup("c:/work/src/a.cs", "src/a.cs", Array.Empty<SearchMatchItem>()),
        };

        var roots = _sut.Map(groups);

        Assert.IsType<SearchFolderNode>(roots[0]);
        Assert.IsType<SearchFileGroup>(roots[1]);
    }
}
