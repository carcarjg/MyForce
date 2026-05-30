using Avalonia.Controls;
using MyForce.ViewModels;

namespace MyForce;

public partial class MainWindow : Window
{
	public MainWindow()
	{
		InitializeComponent();
		DataContext = new MainWindowViewModel();
	}
}
