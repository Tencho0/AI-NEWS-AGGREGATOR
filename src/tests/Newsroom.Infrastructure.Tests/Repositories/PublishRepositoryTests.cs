using Newsroom.Infrastructure.Repositories;

namespace Newsroom.Infrastructure.Tests.Repositories;

public class PublishRepositoryTests
{
    [Theory]
    [InlineData("https://images.pexels.com/photos/321/pexels-photo-321.jpeg?auto=compress&w=1200",
        "pexels-photo-321.jpeg")]
    [InlineData("https://cdn.pixabay.com/photo/2026/05/01/village-9000_1280.jpg",
        "village-9000_1280.jpg")]
    [InlineData("https://example.com/path%20with%20spaces.png", "path with spaces.png")]
    [InlineData("https://example.com/", "image.jpg")] // no path segment
    [InlineData("not a url", "image.jpg")]
    public void FileNameFromUrl_uses_the_last_path_segment_or_falls_back(string url, string expected)
    {
        Assert.Equal(expected, PublishRepository.FileNameFromUrl(url));
    }
}
