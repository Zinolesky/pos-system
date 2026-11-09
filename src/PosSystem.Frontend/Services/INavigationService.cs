using System;
using PosSystem.Frontend.ViewModels;

namespace PosSystem.Frontend.Services;

public interface INavigationService
{
    ViewModelBase? CurrentViewModel { get; }
    void NavigateTo<TViewModel>() where TViewModel : ViewModelBase;

    event Action? CurrentViewModelChanged;
}

public class NavigationService : INavigationService
{
    private readonly ISessionService _sessionService;
    private readonly IServiceProvider _serviceProvider;
    private ViewModelBase? _currentViewModel;

    public ViewModelBase? CurrentViewModel
    {
        get => _currentViewModel;
        private set
        {
            if (_currentViewModel is IDisposable disposable && !ReferenceEquals(_currentViewModel, value))
            {
                disposable.Dispose();
            }
            _currentViewModel = value;
            CurrentViewModelChanged?.Invoke();
        }
    }

    public event Action? CurrentViewModelChanged;

    public NavigationService(ISessionService sessionService, IServiceProvider serviceProvider)
    {
        _sessionService = sessionService;
        _serviceProvider = serviceProvider;
    }

    public void NavigateTo<TViewModel>() where TViewModel : ViewModelBase
    {
        if (typeof(TViewModel) != typeof(LoginViewModel) && !_sessionService.IsLoggedIn)
        {
            NavigateTo<LoginViewModel>();
            return;
        }

        CurrentViewModel = (ViewModelBase)_serviceProvider.GetService(typeof(TViewModel))!;
    }
}
