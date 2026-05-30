using System;
using Avalonia.Controls;
using MyForce.ViewModels;

namespace MyForce;

public partial class MainWindow : Window
{
 private readonly MainWindowViewModel _viewModel;

	public MainWindow()
	{
		InitializeComponent();
        _viewModel = new MainWindowViewModel();
		DataContext = _viewModel;
	}

	protected override void OnClosed(EventArgs e)
	{
		_viewModel.Dispose();
		base.OnClosed(e);
	}
}
