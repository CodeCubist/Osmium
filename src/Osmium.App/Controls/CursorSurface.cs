using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Osmium.App.Controls;

public sealed class CursorLockGrid : Grid
{
    public InputCursor? CustomCursor
    {
        get => ProtectedCursor;
        set => ProtectedCursor = value;
    }
}