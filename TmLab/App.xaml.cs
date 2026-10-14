using Microsoft.Maui.Controls;

namespace TmLab;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell()) { Title = "Транзакционная память" };

#if WINDOWS
        // Открываем окно достаточно широким, чтобы сразу включилась двухколоночная
        // раскладка: тогда ничего не приходится прокручивать.
        window.Width = 1180;
        window.Height = 860;
#endif

        return window;
    }
}
