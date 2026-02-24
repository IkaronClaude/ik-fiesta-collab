namespace Mimir.Api.Models;

public record CreateAccountRequest(string Username, string WebPassword, string IngamePassword, string? Email);

public record LoginRequest(string Username, string Password);

public record SetIngamePasswordRequest(string NewPassword);

public record AddCashRequest(int Amount);

public record AccountResponse(int UserNo, string Username, string? Email, DateTime Created);

public record CashResponse(int UserNo, int Cash);

public record LoginResponse(string Token);
