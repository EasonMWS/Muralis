using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muralis.App.UI.Motion;

namespace Muralis.App.UI.Controls;

public class MuralisIconButton : Button
{
    private bool _pointerOver;

    public MuralisIconButton()
    {
        Loaded += (_, _) =>
        {
            if (Application.Current.Resources["MuralisIconButtonStyle"] is Style style)
            {
                Style = style;
            }
        };
        PointerEntered += (_, _) =>
        {
            _pointerOver = true;
            HoverMotion.Enter(this, lift: false);
        };
        PointerExited += (_, _) =>
        {
            _pointerOver = false;
            HoverMotion.Exit(this);
        };
        PointerPressed += (_, _) => PressMotion.Down(this);
        PointerReleased += (_, _) => PressMotion.Release(this, _pointerOver);
        PointerCanceled += (_, _) => HoverMotion.Exit(this);
    }
}
