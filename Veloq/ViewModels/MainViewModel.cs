using CommunityToolkit.Mvvm.ComponentModel;

namespace Veloq.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial string Greeting { get; set; } = "Welcome to Avalonia!";
}
