using Custodian.Core.Model;

namespace Custodian.Core.Presentation;

public sealed record BreadcrumbItem(string Name, string FullPath, FileSystemEntry Entry);
