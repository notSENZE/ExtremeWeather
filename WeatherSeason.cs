namespace SenzeExtremeWeather;

internal static class WeatherSeason
{
    internal static bool IsWinter => Seasons.Controller?.Season == ESeason.Winter;

    internal static string PrecipitationName => IsWinter ? "snow" : "rain";
}
