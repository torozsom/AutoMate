namespace Core.Entities;

public class User
{
    public Guid Id { get; set; }

    public required string GitHubUsername { get; set; }

    public required string Email { get; set; }

    public string? GitHubAccessToken { get; set; }

    public ICollection<Project> Projects { get; set; }
}