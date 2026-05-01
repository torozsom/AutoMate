namespace Core.DTO;

public record DatabaseConfigDto
{
    public string DbType { get; set; } = string.Empty;

    public string DbName { get; set; } = "appdb";

    public string DbUser { get; set; } = "admin";

    public string DbPassword { get; set; } = "AdminPwd123";

    public string ConnectionStringName { get; set; } = "DefaultConnection";

    public string ContainerNameSuffix { get; set; } = "db";
}