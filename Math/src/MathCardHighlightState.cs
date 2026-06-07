using Godot;
using MegaCrit.Sts2.Core.Models;

namespace MathMod;

internal static class MathCardHighlightState
{
    private static readonly Dictionary<CardModel, Color> _highlightColors = new();

    public static void Replace(IReadOnlyDictionary<CardModel, Color> nextColors)
    {
        _highlightColors.Clear();
        foreach ((CardModel card, Color color) in nextColors)
        {
            _highlightColors[card] = color;
        }
    }

    public static void Clear()
    {
        _highlightColors.Clear();
    }

    public static bool TryGetColor(CardModel card, out Color color)
    {
        return _highlightColors.TryGetValue(card, out color);
    }
}
