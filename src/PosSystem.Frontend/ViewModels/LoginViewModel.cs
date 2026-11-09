using System;
using System.Threading.Tasks;
using System.Windows.Input;
using PosSystem.Frontend.Services;

namespace PosSystem.Frontend.ViewModels;

public class LoginViewModel : ViewModelBase
{
    private readonly ISessionService _sessionService;
    private readonly INavigationService _navigationService;
    private readonly AuthenticationService _authService;

    private string _username = "admin";
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    private string _pin = string.Empty;
    public string Pin
    {
        get => _pin;
        set => SetProperty(ref _pin, value);
    }

    private string _errorMessage = string.Empty;
    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public ICommand LoginCommand { get; }
    public ICommand QuickFillCommand { get; }

    public LoginViewModel(
        ISessionService sessionService,
        INavigationService navigationService,
        AuthenticationService authService)
    {
        _sessionService = sessionService;
        _navigationService = navigationService;
        _authService = authService;

        LoginCommand = new RelayCommand(async _ => await LoginAsync(), _ => !IsLoading);
        QuickFillCommand = new RelayCommand(param => QuickFill(param as string));
    }

    private void QuickFill(string? rolePreset)
    {
        ErrorMessage = string.Empty;
        switch (rolePreset)
        {
            case "admin":
                Username = "admin";
                Pin = "1234";
                break;
            case "manager":
                Username = "manager";
                Pin = "2222";
                break;
            case "cashier":
                Username = "cashier";
                Pin = "0000";
                break;
        }
    }

    private async Task LoginAsync()
    {
        ErrorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Pin))
        {
            ErrorMessage = "Please enter both Username and PIN.";
            return;
        }

        IsLoading = true;
        CommandManager.InvalidateRequerySuggested();

        try
        {
            var result = await _authService.AuthenticateHybridAsync(Username, Pin);
            if (result.Success && result.Cashier != null)
            {
                _sessionService.Login(result.Token, result.Cashier);
                try
                {
                    _navigationService.NavigateTo<CheckoutViewModel>();
                }
                catch (Exception navEx)
                {
                    _sessionService.Logout();
                    ErrorMessage = $"Failed to open register: {navEx.Message}";
                }
            }
            else
            {
                ErrorMessage = result.ErrorMessage ?? "Invalid Username or PIN.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Login error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
