namespace Mimir.Api.Models;

public record CreateAccountRequest(
    string Username,
    string WebPassword,
    string IngamePassword,
    string? Email = null,
    string? CaptchaToken = null);

public record LoginRequest(string Username, string Password, string? CaptchaToken = null);

public record LoginResponse(string? Token, bool RequiresCaptcha = false);

public record SetIngamePasswordRequest(string NewPassword);

public record SetWebPasswordRequest(string NewPassword);

public record AddCashRequest(int Amount);

public record AccountResponse(int UserNo, string Username, string? Email, DateTime Created);

public record CashResponse(int UserNo, int Cash);
