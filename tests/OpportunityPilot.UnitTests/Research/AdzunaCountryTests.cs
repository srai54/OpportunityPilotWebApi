using OpportunityPilot.Application.Research.Boards;

namespace OpportunityPilot.UnitTests.Research;

public class AdzunaCountryTests
{
    [Theory]
    [InlineData(null, "in", null)]
    [InlineData("", "in", null)]
    [InlineData("Remote", "in", null)]
    [InlineData("Anywhere", "in", null)]
    [InlineData("India", "in", null)]
    [InlineData("Pune", "in", "Pune")]
    [InlineData("Gurugram, Haryana", "in", "Gurugram, Haryana")]
    [InlineData("UK", "gb", null)]
    [InlineData("gb", "gb", null)]
    [InlineData("London", "gb", "London")]
    [InlineData("Berlin, Germany", "de", "Berlin, Germany")]
    [InlineData("Germany", "de", null)]
    [InlineData("Paris, FR", "fr", "Paris, FR")]
    [InlineData("Amsterdam", "nl", "Amsterdam")]
    [InlineData("Sydney, New South Wales", "au", "Sydney, New South Wales")]
    [InlineData("Albuquerque, New Mexico", "us", "Albuquerque, New Mexico")]
    [InlineData("Mexico City", "mx", "Mexico City")]
    [InlineData("Singapore", "sg", null)]
    [InlineData("Zürich", "ch", "Zürich")]
    [InlineData("Lagos, Nigeria", "in", "Lagos, Nigeria")]
    public void The_country_comes_from_the_place_and_falls_back_to_india(string? place, string country, string? where) =>
        Assert.Equal((country, where), AdzunaCountry.Resolve(place));

    [Fact]
    public void Every_adzuna_country_is_known_once() =>
        Assert.Equal(["at", "au", "be", "br", "ca", "ch", "de", "es", "fr", "gb", "in", "it", "mx", "nl", "nz", "pl", "sg", "us", "za"],
            AdzunaCountry.Codes.Order().ToArray());
}
