using Core.DTO;

namespace Services.Scanner;


/// <summary>
///    Service interface for scanning the local file system to identify Git projects.
/// </summary>
public interface ILocalScannerService
{
    Task<List<LocalProjectDto>> ScanForProjectsAsync(string rootPath);
}