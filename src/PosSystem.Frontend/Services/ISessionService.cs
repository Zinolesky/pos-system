using System;
using PosSystem.Shared.Models;

namespace PosSystem.Frontend.Services;

public interface ISessionService
{
    string? CurrentToken { get; }
    Cashier? CurrentUser { get; }
    ShiftSession? CurrentShift { get; }
    bool IsLoggedIn { get; }
    bool IsStationLocked { get; }
    string TerminalId { get; }

    event Action? SessionChanged;

    void Login(string token, Cashier user);
    void Logout();
    void SetStationLocked(bool isLocked);
    void SetCurrentShift(ShiftSession? shift);
}

public class SessionService : ISessionService
{
    private readonly ITokenVault _tokenVault;

    public SessionService(ITokenVault tokenVault)
    {
        _tokenVault = tokenVault;

        var restored = _tokenVault.ReadSession();
        if (restored != null && restored.User != null && !string.IsNullOrEmpty(restored.Token))
        {
            CurrentToken = restored.Token;
            CurrentUser = restored.User;
        }
    }

    public string? CurrentToken { get; private set; }
    public Cashier? CurrentUser { get; private set; }
    public ShiftSession? CurrentShift { get; private set; }
    public bool IsStationLocked { get; private set; }
    public string TerminalId { get; } = Environment.GetEnvironmentVariable("POS_TERMINAL_ID") ?? "Terminal #04-A";

    public bool IsLoggedIn => CurrentUser != null && !string.IsNullOrEmpty(CurrentToken);

    public event Action? SessionChanged;

    public void Login(string token, Cashier user)
    {
        CurrentToken = token;
        CurrentUser = user;
        IsStationLocked = false;
        _tokenVault.SaveSession(token, user);
        SessionChanged?.Invoke();
    }

    public void Logout()
    {
        _tokenVault.ClearToken();
        CurrentToken = null;
        CurrentUser = null;
        IsStationLocked = false;
        SessionChanged?.Invoke();
    }

    public void SetStationLocked(bool isLocked)
    {
        IsStationLocked = isLocked;
        SessionChanged?.Invoke();
    }

    public void SetCurrentShift(ShiftSession? shift)
    {
        CurrentShift = shift;
        SessionChanged?.Invoke();
    }
}
