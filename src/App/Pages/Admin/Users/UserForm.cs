using FamilyMemories.Admin;
using FamilyMemories.Data;

namespace FamilyMemories.Pages.Admin.Users;

public sealed class UserForm
{
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public List<string> Roles { get; set; } = [];
    public Guid? PromptSetId { get; set; }
    public Pacing Pacing { get; set; } = Pacing.Weekly;
    public List<PromptSet> Sets { get; set; } = [];

    public string? Validate() =>
        string.IsNullOrWhiteSpace(DisplayName) ? "Please type their name."
        : string.IsNullOrWhiteSpace(Email) || !Email.Contains('@') ? "Please type their whole email address."
        : null;

    public UserInput ToInput() => new(DisplayName, Email, Roles, PromptSetId, Pacing);
}
