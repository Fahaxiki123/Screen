namespace Screen.Core;

public static class ColorText
{
    public static string ToHex(uint argb) => $"#{argb & 0x00FFFFFFu:X6}";

    public static string ToRgb(uint argb) =>
        $"rgb({(argb >> 16) & 0xFF}, {(argb >> 8) & 0xFF}, {argb & 0xFF})";
}
