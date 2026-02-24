namespace Mimir.Api.Models;

public record CharacterResponse(
    int CharNo,
    string Name,
    int Level,
    long Exp,
    long Money,
    string LoginZone,
    DateTime? LastLoginDate,
    int Class,
    int Race,
    int Gender
);
